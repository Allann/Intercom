using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using System.Runtime.InteropServices;
using Intercom.Chat;
using Intercom.Contacts;
using Intercom.ControlChannel;
using Intercom.Diagnostics;
using Intercom.Discovery;
using Intercom.Identity;
using Intercom.Lifecycle;
using Intercom.Presence;
using Intercom.Pairing;
using Intercom.Routing;
using Intercom.Updates;
using Intercom.App.AttentionCards;
using Intercom.App.Presence;
using Intercom.App.Startup;
using Intercom.App.Tray;
using Intercom.App.Updates;

namespace Intercom.App;

public partial class App : Application
{
    static readonly Guid TrayIconGuid = new("6a1f2e2e-6b7a-4a6a-9a1e-8f1c1f6a2b1a");

    // TODO(#21 reliable control channel): replace with the actual bound port
    // of the mutual-TLS TCP listener once it exists. Discovery still needs a
    // port to put in the SRV record even though nothing is listening on it
    // yet, so peers are visible for the two-PC discovery pass ahead of #21.
    const int PlaceholderControlChannelPort = 47811;

    MainWindow? _mainWindow;

    readonly AppLifecycle _lifecycle;
    readonly DndSettingsStore _dndSettings = new();
    readonly ChatTtsSettingsStore _chatTtsSettings = new();
    readonly ContactStore _contactStore = new();
    readonly ManualOverrideStore _manualOverrideStore = new();
    DiscoveryService? _discoveryService;
    LanPairingHost? _pairingHost;
    LanDiscoveryProbe? _lanDiscoveryProbe;
    PresenceEngine? _presenceEngine;
    PresenceReceiverService? _presenceReceiverService;
    Timer? _presenceBroadcastTimer;
    SessionMessagePump? _sessionMessagePump;
    AttentionCardToastPresenter? _attentionCardToastPresenter;
    readonly UpdateChecker _updateChecker = new(
        new GitHubReleaseVersionSource(),
        new PackageRunningVersionProvider(),
        new UpdateNoticeStore());

    Microsoft.UI.Dispatching.DispatcherQueue? _uiDispatcherQueue;

    public App()
    {
        UnhandledException += (_, eventArgs) =>
            DiagnosticLog.Current.Error("xaml.unhandled", eventArgs.Message, eventArgs.Exception);
        InitializeComponent();
        DiagnosticLog.Current.Info("xaml.initialized", "Application resources initialized.");

        _lifecycle = new AppLifecycle(
            new CrashMarker(),
            new IdentityStore(),
            new StartupTaskService(),
            createWindow: () => _mainWindow = new MainWindow(),
            createTrayPump: hwnd => new TrayMessagePump(hwnd),
            createTrayIcon: hwnd => new TrayIcon(hwnd, TrayIconGuid));
        _lifecycle.QuitRequested += OnQuitRequested;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        DiagnosticLog.Current.Info("launch.begin", $"activation={Program.InitialActivationArguments.Kind}");
        _uiDispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        // Issue #25: registered as early as possible in OnLaunched, per
        // AttentionCardToastPresenter's class doc — Microsoft Learn's
        // quickstart says Register() must run before the app reads its own
        // activation args, though Program.cs's existing single-instance
        // bootstrap (issue #18) already read them once before App was even
        // constructed; see that class's remarks for the resulting known
        // limitation on cold-launch-via-notification-click.
        _attentionCardToastPresenter = new AttentionCardToastPresenter(_uiDispatcherQueue);
        _attentionCardToastPresenter.AcknowledgeRequested += OnToastAcknowledgeRequested;
        _attentionCardToastPresenter.OpenRequested += OnToastOpenRequested;
        try
        {
            _attentionCardToastPresenter.Initialize();
            DiagnosticLog.Current.Info("notifications.registered", "Native app notifications registered.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            // Native app notifications are presentation-only. Some supported
            // Windows configurations reject notification/COM registration;
            // the intercom must still launch and retain its in-app attention
            // card shelf instead of fail-fast crashing during OnLaunched.
            System.Diagnostics.Debug.WriteLine($"App notification registration unavailable: {ex}");
            DiagnosticLog.Current.Warning("notifications.unavailable", "Continuing without native app notifications.", ex);
            _attentionCardToastPresenter.AcknowledgeRequested -= OnToastAcknowledgeRequested;
            _attentionCardToastPresenter.OpenRequested -= OnToastOpenRequested;
            _attentionCardToastPresenter.Dispose();
            _attentionCardToastPresenter = null;
        }

        var launchedViaStartupTask = Program.InitialActivationArguments.Kind == ExtendedActivationKind.StartupTask;
        var outcome = _lifecycle.Start(launchedViaStartupTask);
        DiagnosticLog.Current.Info("lifecycle.started", $"startupTask={launchedViaStartupTask} identityRegenerated={outcome.IdentityWasRegenerated}");

        if (outcome.IdentityWasRegenerated)
        {
            // TODO(#22 pairing ceremony): surface this via the shell UI — every
            // previously approved peer is now unknown and needs re-pairing.
            System.Diagnostics.Debug.WriteLine("Local identity was unreadable and has been regenerated.");
        }
        else if (outcome.RegistryWasReset)
        {
            // TODO(#22 pairing ceremony): surface this too — the identity is
            // still valid, but the approved-peer list itself was unreadable
            // and every peer now needs re-pairing.
            System.Diagnostics.Debug.WriteLine("Approved-peer registry was unreadable and has been reset.");
        }
        if (outcome.PendingPairingsWereReset)
        {
            System.Diagnostics.Debug.WriteLine("Pending-pairing state was unreadable and has been reset.");
        }

        _mainWindow?.AttachIdentityStore(_lifecycle.IdentityStore);
        if (_mainWindow is not null) _mainWindow.PairingRequested += OnPairingRequested;

        StartPairingHost();
        StartLanDiscoveryProbe();
        StartDiscovery();
        DiagnosticLog.Current.Info("discovery.started", "LAN discovery startup completed.");
        StartPresence();
        StartChat();
        StartAttentionCards();
        StartUpdateCheck();
        DiagnosticLog.Current.Info("launch.complete", "All startup modules initialized.");

        Program.RedirectedActivationReceived += OnRedirectedActivation;

        // Best-effort handling for a COLD launch caused by clicking a
        // notification (the process wasn't already running) — see
        // AttentionCardToastPresenter's class remarks for why this path is
        // unvalidated rather than guaranteed correct.
        var activatedArgs = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
        if (activatedArgs.Kind == ExtendedActivationKind.AppNotification
            && activatedArgs.Data is AppNotificationActivatedEventArgs notificationArgs)
        {
            _attentionCardToastPresenter?.HandleActivation(notificationArgs);
        }
    }

    /// <summary>Local availability + DND state and the Win32 signals that
    /// drive it. Issue #35 wires both directions to the live approved-peer
    /// roster: periodic/immediate broadcasts and inbound lease decoding.</summary>
    void StartPresence()
    {
        _dndSettings.Load();

        _presenceEngine = new PresenceEngine(
            deviceId: _lifecycle.Identity.PeerId,
            idleTimeProvider: new Win32IdleTimeProvider(),
            dndSettings: _dndSettings,
            capabilities: Capability.Text | Capability.Tts | Capability.SpokenChat | Capability.AttentionCards);

        _presenceReceiverService = new PresenceReceiverService();
        _contactStore.Load();
        _manualOverrideStore.Load();
        if (_pairingHost is not null)
        {
            _pairingHost.ApplicationFrameReceived += OnPeerApplicationFrameReceived;
            _pairingHost.ConnectionsChanged += OnPeerConnectionsChanged;
        }
        _presenceEngine.MeaningfulStateChanged += BroadcastPresence;

        if (_mainWindow is not null)
        {
            _sessionMessagePump = new SessionMessagePump(_mainWindow.Hwnd);
            _sessionMessagePump.SessionBecameUnusable += _presenceEngine.OnSessionBecameUnusable;
            _sessionMessagePump.SessionBecameUsable += _presenceEngine.OnSessionBecameUsable;
            _sessionMessagePump.Suspending += _presenceEngine.OnSuspending;
            _sessionMessagePump.ResumedAutomatic += _presenceEngine.OnResumedAutomatic;
            _sessionMessagePump.ResumedByUser += _presenceEngine.OnSessionBecameUsable;
            _sessionMessagePump.EndingSession += _presenceEngine.OnQueryEndSession;

            _mainWindow.AttachPresence(_dndSettings);
            _mainWindow.AttachRouting(_contactStore, _manualOverrideStore, _presenceReceiverService.Receiver);
        }

        _presenceEngine.Start();
        _presenceBroadcastTimer = new Timer(
            _ => BroadcastPresence(),
            null,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(10));
    }

    void OnPeerApplicationFrameReceived(Guid peerId, ControlFrame frame) =>
        _presenceReceiverService?.HandleInboundFrame(frame);

    void OnPeerConnectionsChanged() => BroadcastPresence();

    void BroadcastPresence()
    {
        if (_pairingHost is null || _presenceEngine is null) return;
        _ = _pairingHost.BroadcastAsync(
            () => _presenceEngine.NextLease().ToFrame(Guid.NewGuid()),
            CancellationToken.None);
    }

    /// <summary>Issue #24: loads the real, persisted per-peer spoken-chat
    /// (local TTS) preference and gives MainWindow's chat drawer access to
    /// it — the same real-store-not-a-mock pattern <see cref="StartPresence"/>
    /// already uses for <see cref="_dndSettings"/>. The chat send/receive
    /// pipeline itself is wired up inside MainWindow (see
    /// MainWindow.InitializeChatDrawer) against an in-process loopback
    /// stand-in, for the same "no live multi-peer connection roster yet"
    /// reason <see cref="StartPresence"/>'s doc comment explains.</summary>
    void StartChat()
    {
        _chatTtsSettings.Load();
        _mainWindow?.AttachChat(_chatTtsSettings);
    }

    /// <summary>Issue #25: gives MainWindow's attention-card shelf/composer
    /// access to the real <see cref="AttentionCardToastPresenter"/> so an
    /// inbound card (past the DND chime gate — see MainWindow's
    /// OnIncomingAttentionCard) can actually show a native toast. The
    /// send/receive/Ack pipeline itself is wired up inside MainWindow (see
    /// MainWindow.InitializeAttentionCardShelf) against an in-process
    /// loopback stand-in, for the same "no live multi-peer connection roster
    /// yet" reason <see cref="StartChat"/>'s doc comment explains — this
    /// ticket is not group cards either way (#29/#30's job).</summary>
    void StartAttentionCards()
    {
        if (_attentionCardToastPresenter is not null)
            _mainWindow?.AttachAttentionCards(_attentionCardToastPresenter);
    }

    /// <summary>Issue #31: fire-and-forget background check against the
    /// GitHub Releases API — per ADR-0003 and the ticket's own acceptance
    /// criteria, this must never block startup and must never surface an
    /// error to the user beyond simply not showing a notice, which is
    /// exactly what <see cref="UpdateChecker.CheckAsync"/>'s own contract
    /// guarantees (it never throws). Started after the rest of the shell is
    /// already up and running, not awaited by OnLaunched.</summary>
    void StartUpdateCheck() => _ = CheckForUpdatesAsync();

    async Task CheckForUpdatesAsync()
    {
        var notice = await _updateChecker.CheckAsync();
        if (notice is null) return;

        _uiDispatcherQueue?.TryEnqueue(() =>
            _mainWindow?.ShowUpdateNotice(notice, onDismissed: version => _updateChecker.Dismiss(version)));
    }

    /// <summary>The human clicked a toast's Acknowledge button — deliberately
    /// never brings the main window forward (the acceptance criterion this
    /// exists to satisfy), just performs the real Ack send.</summary>
    void OnToastAcknowledgeRequested(Guid cardId)
    {
        _ = _mainWindow?.AcknowledgeAttentionCardAsync(cardId, CancellationToken.None);
    }

    /// <summary>The human clicked the toast body (not a button) — the
    /// ordinary "bring the app to the foreground" activation. Already
    /// running on the UI thread (see <see cref="AttentionCardToastPresenter"/>'s
    /// dispatcher marshaling), so no further re-enqueue is needed here.</summary>
    void OnToastOpenRequested(Guid cardId) => _lifecycle.ShowWindow();

    /// <summary>Issue #20: makes discovered-but-unapproved local peers
    /// visible. Deliberately nothing more than that — no trust, no
    /// connection (#21/#22).</summary>
    void StartDiscovery()
    {
        _discoveryService = new DiscoveryService(
            new Win32DnsServiceDiscovery(),
            new SystemNetworkInterfaceSnapshotProvider(),
            new SystemNetworkChangeNotifier(),
            PeerIdHint.FromPeerId(_lifecycle.Identity.PeerId),
            PlaceholderControlChannelPort,
            localSpki: _lifecycle.Identity.SpkiSha256);

        _discoveryService.VisiblePeersChanged += OnVisiblePeersChanged;
        _discoveryService.Start();
    }

    void StartPairingHost()
    {
        _pairingHost = new LanPairingHost(
            _lifecycle.IdentityStore,
            PlaceholderControlChannelPort,
            spki =>
            {
                var peer = _discoveryService?.VisiblePeers.FirstOrDefault(candidate => candidate.Spki is { } pin && pin == spki);
                return peer is null || !Guid.TryParseExact(peer.PeerIdHint.Value, "N", out var id) ? null : id;
            });
        _pairingHost.PairingCodeReady += OnPairingCodeReady;
        _pairingHost.Approved += OnPairingApproved;
        _pairingHost.PairingFailed += OnPairingFailed;
        _pairingHost.Start();
        _mainWindow?.AttachPeerHost(_pairingHost);
        ReconnectRememberedApprovedPeers();
    }

    void StartLanDiscoveryProbe()
    {
        _lanDiscoveryProbe = new LanDiscoveryProbe(
            _lifecycle.Identity.PeerId,
            _lifecycle.Identity.SpkiSha256,
            PlaceholderControlChannelPort);
        _lanDiscoveryProbe.PeerAnswered += OnLanProbeAnswered;
        _lanDiscoveryProbe.Start();
    }

    void OnLanProbeAnswered(Guid peerId, SpkiPin advertisedSpki, System.Net.IPEndPoint endpoint)
    {
        if (_pairingHost is null) return;
        var approved = _lifecycle.IdentityStore.ApprovedPeers.FirstOrDefault(peer =>
            peer.PeerId == peerId && !peer.Revoked && peer.SpkiSha256 is { } pin &&
            SpkiPinConstantTimeComparer.Matches(pin, advertisedSpki));
        if (approved is null) return;
        _lifecycle.IdentityStore.UpdateLastKnownEndpoint(peerId, endpoint.Address, endpoint.Port);
        _ = _pairingHost.ConnectApprovedAsync(endpoint, approved, CancellationToken.None);
    }

    void OnPairingRequested(VisiblePeer peer)
    {
        if (_pairingHost is null || peer.Spki is not { } spki) return;
        if (!Guid.TryParseExact(peer.PeerIdHint.Value, "N", out var peerId)) return;
        var endpoint = peer.Endpoints
            .OrderBy(endpoint => endpoint.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 0 : 1)
            .FirstOrDefault();
        if (endpoint is null) return;

        DiagnosticLog.Current.Info("ui.pairing-requested", $"peer={peerId} endpoint={endpoint.Address}:{endpoint.Port}");
        _ = _pairingHost.ConnectAsync(
            new System.Net.IPEndPoint(endpoint.Address, endpoint.Port),
            peerId,
            spki,
            CancellationToken.None);
    }

    void OnPairingCodeReady(Guid peerId, string code) => _uiDispatcherQueue?.TryEnqueue(() =>
        _mainWindow?.ShowPairingCode(
            peerId,
            code,
            name => _pairingHost!.ConfirmAsync(peerId, name, CancellationToken.None),
            () => _pairingHost!.RejectAsync(peerId, CancellationToken.None)));

    void OnPairingApproved(ApprovedPeer peer) => _uiDispatcherQueue?.TryEnqueue(() =>
    {
        OnVisiblePeersChanged();
        _mainWindow?.ShowPairingApproved(peer.FriendlyName);
    });

    void OnPairingFailed(Guid peerId, Exception ex)
    {
        DiagnosticLog.Current.Error("ui.pairing-failed", $"peer={peerId}", ex);
        _uiDispatcherQueue?.TryEnqueue(() => _mainWindow?.ShowPairingFailed(ex.Message));
    }

    void OnVisiblePeersChanged()
    {
        // Native DNS-SD callbacks arrive on arbitrary threads; UI updates
        // must be marshaled back onto the UI dispatcher.
        var peers = _discoveryService?.VisiblePeers;
        if (peers is null) return;
        ReconnectApprovedPeers(peers);
        _uiDispatcherQueue?.TryEnqueue(() => _mainWindow?.UpdateDiscoveredPeers(peers));
    }

    void ReconnectApprovedPeers(IReadOnlyList<VisiblePeer> visiblePeers)
    {
        if (_pairingHost is null) return;
        foreach (var approved in _lifecycle.IdentityStore.ApprovedPeers.Where(peer => !peer.Revoked && peer.SpkiSha256 is not null))
        {
            var visible = visiblePeers.FirstOrDefault(peer => peer.Spki is { } spki && spki == approved.SpkiSha256!.Value);
            var endpoint = visible?.Endpoints
                .OrderBy(item => item.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 0 : 1)
                .FirstOrDefault();
            if (endpoint is null) continue;
            _lifecycle.IdentityStore.UpdateLastKnownEndpoint(approved.PeerId, endpoint.Address, endpoint.Port);
            _ = _pairingHost.ConnectApprovedAsync(
                new System.Net.IPEndPoint(endpoint.Address, endpoint.Port), approved, CancellationToken.None);
        }
    }

    void ReconnectRememberedApprovedPeers()
    {
        if (_pairingHost is null) return;
        foreach (var approved in _lifecycle.IdentityStore.ApprovedPeers.Where(peer =>
                     !peer.Revoked && peer.SpkiSha256 is not null &&
                     peer.LastKnownPort is > 0 and <= 65535 &&
                     System.Net.IPAddress.TryParse(peer.LastKnownAddress, out _)))
        {
            var address = System.Net.IPAddress.Parse(approved.LastKnownAddress!);
            var endpoint = new System.Net.IPEndPoint(address, approved.LastKnownPort!.Value);
            DiagnosticLog.Current.Info("peer.reconnect-remembered", $"peer={approved.PeerId} endpoint={endpoint}");
            _ = _pairingHost.ConnectApprovedAsync(endpoint, approved, CancellationToken.None);
        }
    }

    void OnRedirectedActivation(AppActivationArguments args)
    {
        _uiDispatcherQueue?.TryEnqueue(_lifecycle.ShowWindow);
    }

    async void OnQuitRequested()
    {
        Program.RedirectedActivationReceived -= OnRedirectedActivation;
        if (_mainWindow is not null) await _mainWindow.StopAudioAsync();
        if (_lanDiscoveryProbe is not null)
        {
            _lanDiscoveryProbe.PeerAnswered -= OnLanProbeAnswered;
            _lanDiscoveryProbe.Dispose();
        }
        if (_discoveryService is not null)
        {
            _discoveryService.VisiblePeersChanged -= OnVisiblePeersChanged;
            _discoveryService.Dispose();
        }
        if (_mainWindow is not null) _mainWindow.PairingRequested -= OnPairingRequested;
        if (_pairingHost is not null)
        {
            _pairingHost.PairingCodeReady -= OnPairingCodeReady;
            _pairingHost.Approved -= OnPairingApproved;
            _pairingHost.PairingFailed -= OnPairingFailed;
            await _pairingHost.DisposeAsync();
        }
        _sessionMessagePump?.Dispose();
        _presenceBroadcastTimer?.Dispose();
        if (_presenceEngine is not null) _presenceEngine.MeaningfulStateChanged -= BroadcastPresence;
        if (_pairingHost is not null)
        {
            _pairingHost.ApplicationFrameReceived -= OnPeerApplicationFrameReceived;
            _pairingHost.ConnectionsChanged -= OnPeerConnectionsChanged;
        }
        _presenceEngine?.Dispose();
        _attentionCardToastPresenter?.Dispose();
        _lifecycle.Quit();
        Exit();
    }
}
