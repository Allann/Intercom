using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Intercom.Chat;
using Intercom.ControlChannel;
using Intercom.Diagnostics;
using Intercom.Discovery;
using Intercom.Identity;
using Intercom.Lifecycle;
using Intercom.Presence;
using Intercom.App.Presence;
using Intercom.App.Startup;
using Intercom.App.Tray;

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
    DiscoveryService? _discoveryService;
    PresenceEngine? _presenceEngine;
    SessionMessagePump? _sessionMessagePump;

    Microsoft.UI.Dispatching.DispatcherQueue? _uiDispatcherQueue;

    public App()
    {
        InitializeComponent();

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
        _uiDispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        var launchedViaStartupTask = Program.InitialActivationArguments.Kind == ExtendedActivationKind.StartupTask;
        var outcome = _lifecycle.Start(launchedViaStartupTask);

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

        StartDiscovery();
        StartPresence();
        StartChat();

        Program.RedirectedActivationReceived += OnRedirectedActivation;
    }

    /// <summary>Issue #23: local availability + DND state, and the Win32
    /// session/power/shutdown signals that drive it. Deliberately does NOT
    /// yet broadcast presence to any live peer — there is no live,
    /// multi-peer <see cref="PeerControlChannel"/> roster in this app shell
    /// today (control-channel connection routing was explicitly deferred by
    /// issue #21's own scope note — see PeerControlChannel's class doc —
    /// and #23 depends only on #21, not on that routing infrastructure
    /// existing). <see cref="PeerControlChannel"/> already has the send-side
    /// hook fully implemented and tested (see PeerControlChannelTests); once
    /// a connection-routing/roster ticket exists, wiring
    /// <c>_presenceEngine.NextLease</c> in as each channel's
    /// presenceLeaseProvider, and <c>_presenceEngine.MeaningfulStateChanged</c>
    /// to call <c>NotifyPresenceChanged()</c> across that roster, is a
    /// small, mechanical addition — not a redesign.</summary>
    void StartPresence()
    {
        _dndSettings.Load();

        _presenceEngine = new PresenceEngine(
            deviceId: _lifecycle.Identity.PeerId,
            idleTimeProvider: new Win32IdleTimeProvider(),
            dndSettings: _dndSettings,
            // No feature capability is actually implemented yet (#24 text,
            // #29 voice) — reflect that honestly rather than advertising
            // something this build can't do.
            capabilities: Capability.None);

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
        }

        _presenceEngine.Start();
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
            PlaceholderControlChannelPort);

        _discoveryService.VisiblePeersChanged += OnVisiblePeersChanged;
        _discoveryService.Start();
    }

    void OnVisiblePeersChanged()
    {
        // Native DNS-SD callbacks arrive on arbitrary threads; UI updates
        // must be marshaled back onto the UI dispatcher.
        var peers = _discoveryService?.VisiblePeers;
        if (peers is null) return;
        _uiDispatcherQueue?.TryEnqueue(() => _mainWindow?.UpdateDiscoveredPeers(peers));
    }

    void OnRedirectedActivation(AppActivationArguments args)
    {
        _uiDispatcherQueue?.TryEnqueue(_lifecycle.ShowWindow);
    }

    void OnQuitRequested()
    {
        Program.RedirectedActivationReceived -= OnRedirectedActivation;
        if (_discoveryService is not null)
        {
            _discoveryService.VisiblePeersChanged -= OnVisiblePeersChanged;
            _discoveryService.Dispose();
        }
        _sessionMessagePump?.Dispose();
        _presenceEngine?.Dispose();
        _lifecycle.Quit();
        Exit();
    }
}
