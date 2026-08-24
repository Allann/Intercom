using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Windowing;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI;
using Windows.UI.Core;
using WinRT.Interop;
using Intercom.AttentionCards;
using Intercom.Audio;
using Intercom.Chat;
using Intercom.Contacts;
using Intercom.Diagnostics;
using Intercom.Discovery;
using Intercom.Identity;
using Intercom.Lifecycle;
using Intercom.Presence;
using Intercom.Pairing;
using Intercom.Routing;
using Intercom.GroupVoice;
using Intercom.Updates;
using Intercom.App.AttentionCards;
using Intercom.App.Audio;
using Intercom.App.Chat;
using Intercom.App.Input;
using Intercom.App.Notifications;

namespace Intercom.App;

public sealed partial class MainWindow : Window, IResidentWindow
{
    const int MinimumWindowWidth = 1050;
    const int MinimumWindowHeight = 720;
    bool _enforcingMinimumWindowSize;
    // Matches the prototype's va-sign palette (intercom-shell-prototype.html
    // --green/--red), not the pairing dialog's cream/ink palette — the sign
    // is a status indicator, not a document/postcard motif.
    static readonly SolidColorBrush AvailableGreen = new(Color.FromArgb(255, 0x5C, 0x8A, 0x52));
    static readonly SolidColorBrush DndRed = new(Color.FromArgb(255, 0xC1, 0x44, 0x3A));
    static readonly SolidColorBrush SignText = new(Colors.White);

    // Chat drawer's fixed "postcard" palette (intercom-shell-prototype.html
    // --paper/--ink/--teal/--mustard), matching PairingDialog's brushes —
    // see MainWindow.xaml's chat drawer comment for why this section is a
    // fixed light palette rather than the shell's default theme brushes.
    static readonly SolidColorBrush Paper = new(Color.FromArgb(255, 0xFB, 0xF4, 0xE4));
    static readonly SolidColorBrush Ink = new(Color.FromArgb(255, 0x2B, 0x1D, 0x14));
    static readonly SolidColorBrush Teal = new(Color.FromArgb(255, 0x2F, 0x6F, 0x6B));
    static readonly SolidColorBrush Mustard = new(Color.FromArgb(255, 0xE8, 0xA3, 0x3D));

    public event Action? QuitRequested;
    public event Action<VisiblePeer>? PairingRequested;
    public event Action<ManualPeerEndpoint, ApprovedPeer?>? ManualPeerRequested;

    public nint Hwnd { get; }
    public AppWindow AppWin { get; }

    readonly DispatcherQueue _dispatcherQueue;
    readonly DispatcherQueueTimer _chatChimeHideTimer;
    readonly GlobalPushToTalkHotkey _pushToTalkHotkey;

    IdentityStore? _identityStore;
    DndSettingsStore? _dndSettings;

    // ---- Issue #31: manual update check ----
    UpdateAvailableNotice? _pendingUpdateNotice;
    Action<AppVersion>? _onUpdateNoticeDismissed;

    ChatTtsSettingsStore? _chatTtsSettings;
    ChatSpeechService? _chatSpeechService;
    readonly Dictionary<Guid, RichChatService> _chatServices = [];
    readonly Dictionary<Guid, IAttentionCardTransport> _attentionCardTransports = [];
    readonly Dictionary<Guid, AttentionCardFanoutRouter> _attentionCardRouters = [];
    readonly Dictionary<Guid, AudioSessionNegotiator> _audioNegotiators = [];
    readonly Dictionary<Guid, AudioPipelineSession> _audioSessions = [];

    readonly Dictionary<Guid, AttentionCardService> _attentionCardServices = [];
    LanPairingHost? _peerHost;
    ContactStore? _contactStore;
    ManualOverrideStore? _manualOverrideStore;
    PresenceReceiver? _presenceReceiver;
    Guid? _selectedChatPeerId;
    Guid? _selectedAttentionPeerId;
    readonly HashSet<Guid> _selectedFamilyPeerIds = [];
    AttentionCardToastPresenter? _attentionCardToastPresenter;
    readonly UnreadBadgePresenter _unreadBadgePresenter = new();

    // There is no live multi-peer connection roster in this app shell yet
    // (see MainWindow.xaml's chat drawer comment) — this Guid stands in for
    // "the approved peer this drawer is talking to" purely so the per-peer
    // spoken-chat toggle (issue #24 requirement 4) has a real key to persist
    // against for the lifetime of this demo session.

    // Guards re-entrant CheckBox.Checked/Unchecked firing while
    // RenderChatSpokenToggle programmatically sets IsChecked to reflect
    // loaded state, so that doesn't get misread as a user action and
    // re-persisted as a no-op toggle.
    bool _suppressSpokenChatToggleHandler;
    OptimizedChatImage? _pendingChatImage;
    readonly DispatcherQueueTimer _typingTimer;
    DateTimeOffset _lastTypingSignal;
    Flyout? _emojiFlyout;
    IReadOnlyList<VisiblePeer> _lastDiscoveredPeers = [];
    string? _renderedFamilyRosterSnapshot;
    bool _refreshingPeerChoices;
    bool _hasGroupFloor;
    bool _handRaised;
    bool _handsFreeActive;
    Guid? _handsFreePeerId;
    bool _pushToTalkHeld;
    IGroupFloorTransport? _groupFloorTransport;
    GroupFloorService? _groupFloorService;

    public MainWindow()
    {
        InitializeComponent();
        PushToTalkButton.AddHandler(
            UIElement.PointerPressedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler(OnPushToTalkPressed),
            handledEventsToo: true);
        Title = "Intercom";

        Hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(Hwnd);
        AppWin = AppWindow.GetFromWindowId(windowId);
        AppWin.Changed += OnAppWindowChanged;
        EnforceMinimumWindowSize();

        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _chatChimeHideTimer = _dispatcherQueue.CreateTimer();
        _chatChimeHideTimer.Interval = TimeSpan.FromSeconds(3);
        _chatChimeHideTimer.Tick += (_, _) =>
        {
            _chatChimeHideTimer.Stop();
            ChatChimeNotice.Visibility = Visibility.Collapsed;
        };
        _typingTimer = _dispatcherQueue.CreateTimer();
        _typingTimer.Interval = TimeSpan.FromSeconds(1);
        _typingTimer.Tick += (_, _) =>
        {
            if (_selectedChatPeerId is Guid peerId && _chatServices.TryGetValue(peerId, out var service))
            {
                service.Tick();
                RenderTypingIndicator();
            }
        };
        _typingTimer.Start();
        _pushToTalkHotkey = new GlobalPushToTalkHotkey();
        _pushToTalkHotkey.Pressed += () => _dispatcherQueue.TryEnqueue(() => _ = BeginPushToTalkAsync());
        _pushToTalkHotkey.Released += () => _dispatcherQueue.TryEnqueue(EndPushToTalk);
        if (!_pushToTalkHotkey.IsRegistered)
        {
            HotkeyNotice.Message = $"Ctrl+Alt+Space is already in use or could not be registered ({_pushToTalkHotkey.RegistrationError}). The on-screen control still works.";
            HotkeyNotice.IsOpen = true;
        }

        // ADR-0003: closing the main window hides it to the tray; only an
        // explicit tray "Quit" action actually ends the process.
        AppWin.Closing += OnAppWindowClosing;

        InitializeChatDrawer();
        InitializeAttentionCardShelf();
    }

    async void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsDialog(CanTestSelectedSpeaker() ? TestSelectedSpeaker : null)
        {
            XamlRoot = Content.XamlRoot,
        };
        await dialog.ShowAsync();
    }

    void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidSizeChange) EnforceMinimumWindowSize();
    }

    void EnforceMinimumWindowSize()
    {
        if (_enforcingMinimumWindowSize) return;
        var size = AppWin.Size;
        var width = Math.Max(size.Width, MinimumWindowWidth);
        var height = Math.Max(size.Height, MinimumWindowHeight);
        if (width == size.Width && height == size.Height) return;
        _enforcingMinimumWindowSize = true;
        try { AppWin.Resize(new Windows.Graphics.SizeInt32(width, height)); }
        finally { _enforcingMinimumWindowSize = false; }
    }

    void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = true;
        AppWin.Hide();
    }

    public void ShowFromTray()
    {
        _unreadBadgePresenter.Clear();
        Activate();
        AppWin.Show();
        AppWin.MoveInZOrderAtTop();
    }

    public void ShowCrashNotice()
    {
        CrashNotice.IsOpen = true;
    }

    /// <summary>Issue #31: surfaces the passive "update available" notice.
    /// <paramref name="onDismissed"/> is invoked (with the notice's own
    /// version) only when the user actually clicks the InfoBar's close
    /// button — see <see cref="OnUpdateNoticeCloseButtonClick"/> — not on any
    /// other path, so the caller can persist "dismissed" exactly once per
    /// real dismissal. Must be called on the UI thread.</summary>
    public void ShowUpdateNotice(UpdateAvailableNotice notice, Action<AppVersion> onDismissed)
    {
        _pendingUpdateNotice = notice;
        _onUpdateNoticeDismissed = onDismissed;
        UpdateNotice.Message = $"Version {notice.Version} is available. You're currently running an older version.";
        UpdateNotice.IsOpen = true;
    }

    void OnUpdateNoticeCloseButtonClick(InfoBar sender, object args)
    {
        if (_pendingUpdateNotice is { } notice) _onUpdateNoticeDismissed?.Invoke(notice.Version);
    }

    /// <summary>Issue #31 requirement 4: no download/install logic at all —
    /// this just opens the release's GitHub page in the user's default
    /// browser, per ADR-0003's "manual update" workflow (re-running the
    /// newly downloaded, identically-signed MSIX upgrades in place).</summary>
    async void OnUpdateNoticeOpenReleaseClick(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdateNotice is not { } notice) return;
        if (!Uri.TryCreate(notice.ReleaseUrl, UriKind.Absolute, out var uri)) return;
        await Windows.System.Launcher.LaunchUriAsync(uri);
    }

    public void Quit()
    {
        AppWin.Closing -= OnAppWindowClosing;
        QuitRequested?.Invoke();
    }

    /// <summary>Issue #20's entire UI surface: the raw "visible, unapproved"
    /// list, for confirming discovery actually works on real hardware. No
    /// approve/connect affordance — that's #21/#22. Must be called on the UI
    /// thread.</summary>
    public void UpdateDiscoveredPeers(IReadOnlyList<VisiblePeer> peers)
    {
        _lastDiscoveredPeers = peers;
        var approvedPeers = _identityStore?.ApprovedPeers.Where(peer => !peer.Revoked).ToList() ?? [];
        var onlineApprovedPeers = approvedPeers
            .Where(peer => _peerHost?.ConnectedPeerIds.Contains(peer.PeerId) == true)
            .ToList();
        if (onlineApprovedPeers.Count == 1)
        {
            _selectedFamilyPeerIds.Clear();
            _selectedFamilyPeerIds.Add(onlineApprovedPeers[0].PeerId);
        }
        var pairablePeers = peers.Where(peer => peer.Spki is not null
            && !approvedPeers.Any(approved => approved.PeerId.ToString("N").Equals(peer.PeerIdHint.Value, StringComparison.OrdinalIgnoreCase))).ToList();
        var snapshot = string.Join('|', approvedPeers.Select(peer =>
            $"{peer.PeerId:N}:{peer.FriendlyName}:{_peerHost?.ConnectedPeerIds.Contains(peer.PeerId) == true}:{RemotePresenceLabel(peer.PeerId, _peerHost?.ConnectedPeerIds.Contains(peer.PeerId) == true)}"))
            + "||" + string.Join('|', pairablePeers.Select(peer => $"{peer.PeerIdHint}:{DescribePeer(peer)}"));
        if (snapshot == _renderedFamilyRosterSnapshot)
        {
            RefreshMessagingPeers();
            return;
        }
        _renderedFamilyRosterSnapshot = snapshot;
        NoDiscoveredPeersNotice.Visibility = approvedPeers.Count == 0 && pairablePeers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DiscoveredPeersList.Children.Clear();
        foreach (var approved in approvedPeers)
        {
            var online = _peerHost?.ConnectedPeerIds.Contains(approved.PeerId) == true;
            var row = new Grid { ColumnSpacing = 6 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var memberContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            memberContent.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 11,
                Height = 11,
                Fill = online ? AvailableGreen : DndRed,
                Stroke = Ink,
                StrokeThickness = 1.5,
                VerticalAlignment = VerticalAlignment.Center,
            });
            memberContent.Children.Add(new TextBlock
            {
                Text = approved.FriendlyName,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var member = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton
            {
                Content = memberContent,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                IsEnabled = online,
                IsChecked = _selectedFamilyPeerIds.Contains(approved.PeerId),
                Tag = approved.PeerId,
                Padding = new Thickness(10, 8, 10, 8),
                BorderBrush = Ink,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(8),
            };
            ToolTipService.SetToolTip(member, $"{approved.FriendlyName} is {RemotePresenceLabel(approved.PeerId, online)}");
            member.Click += OnFamilyMemberClick;
            var remove = new Button
            {
                Content = new SymbolIcon(Symbol.Delete),
                Tag = approved.PeerId,
                VerticalAlignment = VerticalAlignment.Stretch,
                Width = 38,
                Padding = new Thickness(6),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
            };
            ToolTipService.SetToolTip(remove, $"Remove {approved.FriendlyName}");
            remove.Click += OnRemoveDeviceClick;
            Grid.SetColumn(remove, 1);
            row.Children.Add(member);
            row.Children.Add(remove);
            DiscoveredPeersList.Children.Add(row);
        }
        foreach (var peer in pairablePeers)
        {
            var peerId = Guid.TryParseExact(peer.PeerIdHint.Value, "N", out var parsed) ? parsed : Guid.Empty;
            var button = new Button
            {
                Content = $"Pair {DescribePeer(peer)}",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                IsEnabled = true,
            };
            button.Click += (_, _) => PairingRequested?.Invoke(peer);
            DiscoveredPeersList.Children.Add(button);
        }
        DiagnosticLog.Current.Info(
            "ui.nearby-devices-rendered",
            peers.Count == 0
                ? "count=0"
                : $"count={peers.Count} peers={string.Join(',', peers.Select(peer => peer.PeerIdHint))}");
        RefreshMessagingPeers();
    }

    static string DescribePeer(VisiblePeer peer)
    {
        var id = peer.PeerIdHint.ToString();
        var shortCode = id.Length <= 6 ? id : id[^6..];
        return $"Nearby Intercom · {shortCode.ToUpperInvariant()}";
    }

    async void OnAddFamilyMemberClick(object sender, RoutedEventArgs e)
    {
        var approvedIds = (_identityStore?.ApprovedPeers ?? []).Where(peer => !peer.Revoked).Select(peer => peer.PeerId).ToHashSet();
        var candidate = _lastDiscoveredPeers.FirstOrDefault(peer => peer.Spki is not null
            && Guid.TryParseExact(peer.PeerIdHint.Value, "N", out var id) && !approvedIds.Contains(id));
        if (candidate is not null)
        {
            PairingRequested?.Invoke(candidate);
            return;
        }

        var address = new TextBox { PlaceholderText = "IP address or hostname", MinWidth = 300 };
        var existingPeer = new ComboBox { PlaceholderText = "Pair a new peer", MinWidth = 300 };
        existingPeer.Items.Add(new ComboBoxItem { Content = "Pair a new peer" });
        foreach (var peer in (_identityStore?.ApprovedPeers ?? []).Where(peer => !peer.Revoked))
            existingPeer.Items.Add(new ComboBoxItem { Content = $"Update {peer.FriendlyName}", Tag = peer });
        existingPeer.SelectedIndex = 0;
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock { Text = "Enter a VPN address. Choose an approved peer to correct its address without pairing again." });
        content.Children.Add(address);
        content.Children.Add(existingPeer);
        var dialog = new ContentDialog
        {
            Title = "Add a VPN peer",
            Content = content,
            PrimaryButtonText = "Connect",
            CloseButtonText = "Cancel",
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            var approvedPeer = (existingPeer.SelectedItem as ComboBoxItem)?.Tag as ApprovedPeer;
            ManualPeerRequested?.Invoke(ManualPeerEndpoint.Parse(address.Text, 47811), approvedPeer);
        }
        catch (FormatException ex) { ShowPairingFailed(ex.Message); }
    }

    async void OnRaiseHandClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_groupFloorService is null) await StartGroupFloorAsync();
            else if (_handRaised) await _groupFloorService.LowerHandAsync(CancellationToken.None);
            else await _groupFloorService.RaiseHandAsync(CancellationToken.None);
        }
        catch (Exception ex) { ShowGroupFloorError(ex); }
    }

    async void OnInterruptClick(object sender, RoutedEventArgs e)
    {
        if (_groupFloorService is null || _dndSettings?.DndEnabled == true) return;
        try { await _groupFloorService.InterruptAsync(CancellationToken.None); }
        catch (Exception ex) { ShowGroupFloorError(ex); }
    }

    async void OnEndFloorClick(object sender, RoutedEventArgs e)
    {
        if (_groupFloorService is null) return;
        try { await _groupFloorService.EndForEveryoneAsync(CancellationToken.None); }
        catch (Exception ex) { ShowGroupFloorError(ex); }
    }

    async Task StartGroupFloorAsync()
    {
        if (_identityStore is null || _groupFloorTransport is null || _peerHost is null) return;
        var localId = _identityStore.Identity.PeerId;
        var peers = _selectedFamilyPeerIds.Where(_peerHost.ConnectedPeerIds.Contains).ToArray();
        if (peers.Length == 0) throw new InvalidOperationException("Select at least one online family member first.");
        var session = new GroupFloorSession(Guid.NewGuid(), localId, peers.Prepend(localId));
        var service = new GroupFloorService(localId, session, _groupFloorTransport, new GroupAudioPreparer(this));
        AttachGroupFloorService(service);
        foreach (var peerId in peers)
        {
            var start = new GroupFloorCommand(session.SessionId, GroupFloorCommandKind.StartSession, localId, peerId);
            await _groupFloorTransport.SendAsync([peerId], GroupFloorFrameCodec.Encode(start), CancellationToken.None);
        }
        foreach (var peerId in peers) await service.JoinAsync(peerId, CancellationToken.None);
        await service.GrantFloorAsync(localId, CancellationToken.None);
    }

    void OnGroupFloorFrame(Guid senderPeerId, Intercom.ControlChannel.ControlFrame frame)
    {
        if (_identityStore is null || _groupFloorTransport is null || _groupFloorService is not null) return;
        try
        {
            var start = GroupFloorFrameCodec.Decode(frame);
            if (start.Kind != GroupFloorCommandKind.StartSession || start.ActorPeerId != senderPeerId || start.SubjectPeerId != _identityStore.Identity.PeerId) return;
            var session = new GroupFloorSession(start.SessionId, senderPeerId, [senderPeerId, _identityStore.Identity.PeerId]);
            AttachGroupFloorService(new GroupFloorService(_identityStore.Identity.PeerId, session, _groupFloorTransport, new GroupAudioPreparer(this)));
            _ = new GroupAudioPreparer(this).PrepareAsync(senderPeerId, CancellationToken.None);
        }
        catch (Exception ex) { ShowGroupFloorError(ex); }
    }

    void AttachGroupFloorService(GroupFloorService service)
    {
        _groupFloorService?.Dispose();
        _groupFloorService = service;
        service.StateChanged += () => _dispatcherQueue.TryEnqueue(RenderGroupFloor);
        service.CommandRejected += ex => _dispatcherQueue.TryEnqueue(() => ShowGroupFloorError(ex));
        RenderGroupFloor();
    }

    void RenderGroupFloor()
    {
        var session = _groupFloorService?.Session;
        if (session is null || session.Ended)
        {
            _groupFloorService?.Dispose(); _groupFloorService = null;
            _handRaised = _hasGroupFloor = false;
            GroupFloorSpeakerText.Text = "● Nobody has the floor";
            GroupFloorCoordinatorText.Text = "Raise a hand to start a group floor";
            GroupFloorQueuePanel.Children.Clear();
            RaiseHandButton.Content = "✋ Raise Hand";
            InterruptButton.IsEnabled = false;
            EndFloorButton.Visibility = Visibility.Collapsed;
            RenderVoiceControls(); return;
        }
        var localId = _identityStore!.Identity.PeerId;
        _handRaised = session.RaiseHandQueue.Contains(localId);
        _hasGroupFloor = session.SpeakerPeerId == localId;
        GroupFloorSpeakerText.Text = session.SpeakerPeerId is Guid speaker ? $"● {PeerName(speaker)} has the floor" : "● Nobody has the floor";
        GroupFloorCoordinatorText.Text = session.CoordinatorPeerId == localId ? "You are coordinator" : $"Coordinator: {PeerName(session.CoordinatorPeerId)}";
        RaiseHandButton.Content = _handRaised ? "Leave Queue" : "✋ Raise Hand";
        InterruptButton.IsEnabled = session.SpeakerPeerId is not null && !_hasGroupFloor && _dndSettings?.DndEnabled != true;
        EndFloorButton.Content = "End Session for Everyone";
        EndFloorButton.Visibility = session.CoordinatorPeerId == localId ? Visibility.Visible : Visibility.Collapsed;
        GroupFloorQueuePanel.Children.Clear();
        for (var index = 0; index < session.RaiseHandQueue.Count; index++)
        {
            var peerId = session.RaiseHandQueue[index];
            var button = new Button { Content = $"{index + 1}. {PeerName(peerId)}", IsEnabled = session.CoordinatorPeerId == localId, Tag = peerId };
            button.Click += OnGrantFloorClick;
            GroupFloorQueuePanel.Children.Add(button);
        }
        RenderVoiceControls();
    }

    async void OnGrantFloorClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Guid peerId } && _groupFloorService is not null)
            try { await _groupFloorService.GrantFloorAsync(peerId, CancellationToken.None); } catch (Exception ex) { ShowGroupFloorError(ex); }
    }

    string PeerName(Guid? peerId) => peerId == _identityStore?.Identity.PeerId ? "You" :
        _identityStore?.ApprovedPeers.FirstOrDefault(peer => peer.PeerId == peerId)?.FriendlyName ?? "Family member";

    void ShowGroupFloorError(Exception ex)
    {
        DiagnosticLog.Current.Warning("group-floor.command-failed", ex.Message, ex);
        GroupFloorCoordinatorText.Text = ex.Message;
    }

    async void OnHandsFreeClick(object sender, RoutedEventArgs e)
    {
        if (_handsFreeActive)
        {
            await EndHandsFreeAsync();
            return;
        }
        if (HandsFreeRecipientCombo.SelectedItem is not PeerChoice choice) return;
        if (RemoteDndEnabled(choice.PeerId))
        {
            HandsFreeStatusText.Text = $"{choice.FriendlyName} is in Do Not Disturb. Use text instead.";
            return;
        }
        if (!_audioNegotiators.TryGetValue(choice.PeerId, out var negotiator)) return;

        HandsFreeStatusText.Text = $"Connecting hands-free with {choice.FriendlyName}…";
        try
        {
            if (_audioSessions.ContainsKey(choice.PeerId)) await negotiator.StopAsync(CancellationToken.None);
            await negotiator.OfferAsync(CancellationToken.None, AudioInteractionMode.HandsFree);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Current.Error("audio.hands-free-start-failed", $"peer={choice.PeerId}", ex);
            HandsFreeStatusText.Text = "Couldn’t start hands-free. Text is still available.";
            UpdateMessagingEnabled();
        }
    }

    void OnHandsFreeRecipientChanged(object sender, SelectionChangedEventArgs e) => UpdateMessagingEnabled();

    async void OnPushToTalkPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (VoiceModeSwitch.IsOn)
        {
            OnHandsFreeClick(sender, new RoutedEventArgs());
            return;
        }
        PushToTalkButton.CapturePointer(e.Pointer);
        await BeginPushToTalkAsync();
    }

    async Task BeginPushToTalkAsync()
    {
        if (_pushToTalkHeld || _handsFreeActive) return;
        if (_groupFloorService is not null)
        {
            if (!_hasGroupFloor) { PushToTalkHint.Text = "Raise your hand and wait for the voice floor."; return; }
            _pushToTalkHeld = true;
            foreach (var pair in _audioSessions.Where(pair => _groupFloorService.Session.Participants.Contains(pair.Key)))
                pair.Value.StartTransmitting();
            PushToTalkButton.Content = "On Air";
            PushToTalkButton.Background = Mustard;
            PushToTalkHint.Text = "Transmitting to the group while held.";
            return;
        }
        if (HandsFreeRecipientCombo.SelectedItem is not PeerChoice choice)
        {
            DiagnosticLog.Current.Warning("audio.ptt-ignored", $"reason=no-selected-peer selectedFamilyCount={_selectedFamilyPeerIds.Count}");
            PushToTalkHint.Text = "Select one online family member first.";
            return;
        }
        if (RemoteDndEnabled(choice.PeerId))
        {
            PushToTalkHint.Text = $"{choice.FriendlyName} is in Do Not Disturb. Use text instead.";
            return;
        }
        DiagnosticLog.Current.Info("audio.ptt-pressed", $"peer={choice.PeerId}");
        _pushToTalkHeld = true;
        PushToTalkButton.Content = "On Air";
        PushToTalkButton.Background = Mustard;
        if (_audioSessions.TryGetValue(choice.PeerId, out var session)
            && session.State.State is AudioSessionState.Running or AudioSessionState.Degraded)
        {
            session.StartTransmitting();
            PushToTalkHint.Text = "Transmitting while held.";
            return;
        }

        if (!_audioNegotiators.TryGetValue(choice.PeerId, out var negotiator))
        {
            DiagnosticLog.Current.Warning("audio.ptt-ignored", $"peer={choice.PeerId} reason=no-negotiator");
            PushToTalkHint.Text = "Voice endpoint is not available yet.";
            return;
        }

        PushToTalkHint.Text = "Connecting voice… keep holding to talk.";
        try
        {
            await negotiator.OfferAsync(CancellationToken.None, _groupFloorService is null ? AudioInteractionMode.PushToTalk : AudioInteractionMode.GroupVoice);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Current.Error("audio.offer-failed", $"peer={choice.PeerId}", ex);
            PushToTalkHint.Text = "Couldn’t start voice. Text is still available.";
            _pushToTalkHeld = false;
        }
    }

    void OnPushToTalkReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (VoiceModeSwitch.IsOn) return;
        PushToTalkButton.ReleasePointerCaptures();
        EndPushToTalk();
    }

    async void OnVoiceModeToggled(object sender, RoutedEventArgs e)
    {
        if (!VoiceModeSwitch.IsOn && _handsFreeActive) await EndHandsFreeAsync();
        RenderVoiceControls();
    }

    void EndPushToTalk()
    {
        if (!_pushToTalkHeld) return;
        DiagnosticLog.Current.Info("audio.ptt-released", $"selectedPeer={(HandsFreeRecipientCombo.SelectedItem as PeerChoice)?.PeerId}");
        _pushToTalkHeld = false;
        if (_groupFloorService is not null)
            foreach (var pair in _audioSessions.Where(pair => _groupFloorService.Session.Participants.Contains(pair.Key)))
                pair.Value.StopTransmitting();
        if (HandsFreeRecipientCombo.SelectedItem is PeerChoice choice
            && _audioSessions.TryGetValue(choice.PeerId, out var session))
            session.StopTransmitting();
        RenderVoiceControls();
    }

    bool RemoteDndEnabled(Guid peerId) => _presenceReceiver?.Get(peerId)?.Dnd == true;

    async Task EndHandsFreeAsync()
    {
        var peerId = _handsFreePeerId;
        _handsFreeActive = false;
        _handsFreePeerId = null;
        if (peerId is Guid id && _audioNegotiators.TryGetValue(id, out var negotiator))
            await negotiator.StopAsync(CancellationToken.None);
        RenderHandsFreeState();
        RenderVoiceControls();
        UpdateMessagingEnabled();
    }

    void RenderHandsFreeState()
    {
        var choice = HandsFreeRecipientCombo.ItemsSource is IEnumerable<PeerChoice> choices
            ? choices.FirstOrDefault(item => item.PeerId == _handsFreePeerId)
            : null;
        HandsFreeStatusText.Text = _handsFreeActive
            ? $"Hands-free with {choice?.FriendlyName ?? "a family member"}"
            : "No hands-free session";
        HandsFreeRecipientCombo.IsEnabled = !_handsFreeActive;
    }

    void RenderVoiceControls()
    {
        var connectedPeer = HandsFreeRecipientCombo.SelectedItem is PeerChoice choice
            && _peerHost?.ConnectedPeerIds.Contains(choice.PeerId) == true;
        var voiceEnabled = _handsFreeActive || (_groupFloorService is not null ? _hasGroupFloor : connectedPeer);
        PushToTalkButton.IsEnabled = voiceEnabled;
        var toggleMode = VoiceModeSwitch.IsOn;
        PushToTalkButton.Content = new TextBlock
        {
            Text = toggleMode ? (_handsFreeActive ? "Turn\nOff" : "Turn\nOn") : "Hold to\nTalk",
            TextAlignment = TextAlignment.Center,
        };
        PushToTalkButton.Background = !voiceEnabled
            ? new SolidColorBrush(Color.FromArgb(255, 0xCB, 0xBF, 0xA4))
            : toggleMode && _handsFreeActive ? AvailableGreen : DndRed;
        PushToTalkHint.Text = !voiceEnabled
            ? "Choose an online family member to talk."
            : toggleMode
                ? (_handsFreeActive ? "Speaking is on. Press again to stop." : "Press once to start speaking; press again to stop.")
                : "Hold the button while you speak.";
    }

    public async void ShowPairingCode(
        Guid peerId,
        string code,
        Func<string, Task> confirm,
        Func<Task> reject)
    {
        var name = new TextBox { Header = "Name this family member or device", PlaceholderText = "e.g. Kitchen PC" };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = "Check that this same code appears on the other PC:",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new TextBlock
        {
            Text = code,
            FontSize = 32,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        content.Children.Add(name);

        var dialog = new ContentDialog
        {
            Title = "Pair nearby Intercom",
            Content = content,
            PrimaryButtonText = "Codes match",
            SecondaryButtonText = "Reject",
            CloseButtonText = "Cancel",
            XamlRoot = Content.XamlRoot,
        };

        DiagnosticLog.Current.Info("ui.pairing-code-shown", $"peer={peerId}");
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
            await confirm(name.Text);
        else
            await reject();
    }

    public async void ShowPairingApproved(string friendlyName)
    {
        var dialog = new ContentDialog
        {
            Title = "Family member added",
            Content = $"{friendlyName} is now securely paired with this PC.",
            CloseButtonText = "Done",
            XamlRoot = Content.XamlRoot,
        };
        await dialog.ShowAsync();
    }

    public void ShowPairingFailed(string message)
    {
        PairingFailureNotice.Message = message;
        PairingFailureNotice.IsOpen = true;
    }

    /// <summary>Issue #22: gives this window access to the real
    /// <see cref="IdentityStore"/> so "+ Add Family Member" can open a
    /// pairing dialog backed by real data. Called once from App.OnLaunched,
    /// after AppLifecycle.Start has loaded the store — not passed through
    /// the constructor, since <see cref="Intercom.Lifecycle.AppLifecycle"/>'s
    /// window factory is a parameterless <c>Func&lt;IResidentWindow&gt;</c>.</summary>
    public void AttachIdentityStore(IdentityStore identityStore)
    {
        _identityStore = identityStore;
        UpdateDiscoveredPeers(_lastDiscoveredPeers);
    }

    public void AttachPeerHost(LanPairingHost peerHost)
    {
        _peerHost = peerHost;
        _groupFloorTransport = peerHost.CreateGroupFloorTransport();
        _groupFloorTransport.FrameReceived += OnGroupFloorFrame;
        peerHost.ConnectionsChanged += () => _dispatcherQueue.TryEnqueue(() =>
        {
            UpdateDiscoveredPeers(_lastDiscoveredPeers);
            if (_groupFloorService is { } floor)
                foreach (var peerId in floor.Session.Participants.Where(id => id != _identityStore?.Identity.PeerId && !peerHost.ConnectedPeerIds.Contains(id)).ToArray())
                    floor.ParticipantDeparted(peerId);
        });
        RefreshMessagingPeers();
    }

    void OnFamilyMemberClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Microsoft.UI.Xaml.Controls.Primitives.ToggleButton { Tag: Guid peerId } member) return;
        if (member.IsChecked == true) _selectedFamilyPeerIds.Add(peerId);
        else _selectedFamilyPeerIds.Remove(peerId);
        ApplyFamilySelection();
    }

    async void OnRemoveDeviceClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Guid peerId } || _identityStore is null || _peerHost is null) return;
        var peer = _identityStore.ApprovedPeers.FirstOrDefault(candidate => candidate.PeerId == peerId && !candidate.Revoked);
        if (peer is null) return;

        var dialog = new ContentDialog
        {
            Title = $"Remove {peer.FriendlyName}?",
            Content = "This device will be disconnected and must complete the pairing ceremony again before it can communicate with this PC.",
            PrimaryButtonText = "Remove device",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        if (_audioNegotiators.Remove(peerId, out var negotiator)) await negotiator.DisposeAsync();
        _audioSessions.Remove(peerId);
        _chatServices.Remove(peerId);
        _attentionCardServices.Remove(peerId);
        _attentionCardTransports.Remove(peerId);
        _attentionCardRouters.Clear();
        _selectedFamilyPeerIds.Remove(peerId);
        if (_selectedChatPeerId == peerId) _selectedChatPeerId = null;
        if (_selectedAttentionPeerId == peerId) _selectedAttentionPeerId = null;
        await _peerHost.ForgetAsync(peerId);
        DiagnosticLog.Current.Info("peer.forgotten", $"peer={peerId}");
        UpdateDiscoveredPeers(_lastDiscoveredPeers);
    }

    void ApplyFamilySelection()
    {
        if (_identityStore is null) return;
        var selected = _identityStore.ApprovedPeers
            .Where(peer => !peer.Revoked && _selectedFamilyPeerIds.Contains(peer.PeerId))
            .ToList();
        var primary = selected.FirstOrDefault(peer => _peerHost?.ConnectedPeerIds.Contains(peer.PeerId) == true);
        _selectedChatPeerId = primary?.PeerId;
        _selectedAttentionPeerId = primary?.PeerId;
        _refreshingPeerChoices = true;
        SelectPeerChoice(ChatRecipientCombo, primary?.PeerId);
        SelectPeerChoice(ComposerRecipientCombo, primary?.PeerId);
        SelectPeerChoice(HandsFreeRecipientCombo, primary?.PeerId);
        _refreshingPeerChoices = false;
        SelectedFamilyText.Text = selected.Count switch
        {
            0 => "Select who you want to reach",
            1 => $"Selected: {selected[0].FriendlyName}",
            _ => $"Group selected: {string.Join(", ", selected.Select(peer => peer.FriendlyName))}",
        };
        HandsFreeStatusText.Text = selected.Count == 0
            ? "Select an online family member from the Family list."
            : selected.Count == 1 ? $"Push-to-talk with {selected[0].FriendlyName}." : $"Group push-to-talk · {selected.Count} members.";
        var groupVisible = selected.Count > 1;
        GroupFloorPanel.Visibility = groupVisible ? Visibility.Visible : Visibility.Collapsed;
        GroupFloorRow.Height = groupVisible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        VoicePanel.RowSpacing = groupVisible ? 14 : 0;
        UpdateMessagingEnabled();
        RenderChatSpokenToggle();
        RenderChatMessages();
        RenderAttentionCardShelf();
        RenderVoiceControls();
    }

    static void SelectPeerChoice(ComboBox combo, Guid? peerId)
    {
        var choices = combo.ItemsSource as IEnumerable<PeerChoice>;
        combo.SelectedItem = choices?.FirstOrDefault(choice => choice.PeerId == peerId);
    }

    /// <summary>Issue #23: gives this window access to the real
    /// <see cref="DndSettingsStore"/> so the flippable Available/DND sign
    /// (intercom-shell-prototype.html's va-sign/vaToggleDnd) is wired to
    /// real, persisted state — not a mock — the same "+ Add Family Member"
    /// pattern MainWindow already uses for <see cref="AttachIdentityStore"/>.
    /// Called once from App.OnLaunched.</summary>
    public void AttachPresence(DndSettingsStore dndSettings)
    {
        _dndSettings = dndSettings;
        _dndSettings.Changed += _ => RenderDndSign();
        RenderDndSign();
    }

    public void AttachRouting(ContactStore contactStore, ManualOverrideStore manualOverrideStore, PresenceReceiver presenceReceiver)
    {
        _contactStore = contactStore;
        _manualOverrideStore = manualOverrideStore;
        _presenceReceiver = presenceReceiver;
        presenceReceiver.LeaseAccepted += OnRemotePresenceChanged;
    }

    void OnRemotePresenceChanged(TrackedDevicePresence presence) => _dispatcherQueue.TryEnqueue(() =>
    {
        UpdateDiscoveredPeers(_lastDiscoveredPeers);
    });

    string RemotePresenceLabel(Guid peerId, bool connected)
    {
        if (!connected) return "offline";
        var presence = _presenceReceiver?.Get(peerId);
        if (presence?.Dnd == true) return "online · Do Not Disturb";
        return presence?.Availability switch
        {
            AvailabilityState.Idle => "online · idle",
            AvailabilityState.Unavailable => "online · unavailable",
            _ => "online · available",
        };
    }

    void OnToggleDndClick(object sender, RoutedEventArgs e) => _dndSettings?.Toggle();

    void RenderDndSign()
    {
        if (_dndSettings is null) return;

        var dnd = _dndSettings.DndEnabled;
        DndSignButton.Content = dnd ? "Do Not Disturb" : "Available";
        DndSignButton.Background = dnd ? DndRed : AvailableGreen;
        DndSignButton.Foreground = SignText;
    }

    // ---- Issue #24: chat drawer ----

    /// <summary>Gives this window access to the real, persisted
    /// <see cref="ChatTtsSettingsStore"/> so the spoken-chat toggle is wired
    /// to real state — not a mock — the same "+ Add Family Member"/
    /// <see cref="AttachPresence"/> pattern MainWindow already uses. Called
    /// once from App.OnLaunched, after the store has been loaded.</summary>
    public void AttachChat(ChatTtsSettingsStore chatTtsSettings)
    {
        _chatTtsSettings = chatTtsSettings;
        _chatSpeechService = new ChatSpeechService();
        _chatSpeechService.SelectVoice(chatTtsSettings.SelectedVoiceId);
        RenderChatSpokenToggle();
    }

    /// <summary>Wires up a real <see cref="ChatService"/> pipeline (real
    /// <see cref="ChatFrameCodec"/> encode/decode, real <see cref="ChatConversation"/>
    /// delivery-state bookkeeping, real mechanical Delivered receipts via
    /// <see cref="FrameDispatcher"/>) against an in-process
    /// <see cref="LoopbackChatTransport"/> pair — see MainWindow.xaml's chat
    /// drawer comment for why a loopback stand-in is used instead of a live
    /// <see cref="PeerControlChannel"/> connection. <c>_chatServiceLocal</c>
    /// is "this device"; <c>_chatServicePeer</c> exists purely so the demo
    /// buttons can make the simulated peer actually send real frames back.</summary>
    void InitializeChatDrawer()
    {
        RenderChatMessages();
    }

    void RefreshMessagingPeers()
    {
        if (_peerHost is null || _identityStore is null) return;
        var peers = _identityStore.ApprovedPeers.Where(peer => !peer.Revoked).ToList();
        foreach (var peer in peers)
        {
            if (!_chatServices.ContainsKey(peer.PeerId))
            {
                var chat = new RichChatService(_peerHost.CreateChatTransport(peer.PeerId));
                chat.MessageReceived += message => OnIncomingChatMessage(peer.PeerId, message);
                chat.Conversation.MessageAdded += _ => _dispatcherQueue.TryEnqueue(RenderChatMessages);
                chat.Conversation.MessageUpdated += _ => _dispatcherQueue.TryEnqueue(RenderChatMessages);
                chat.PeerTypingChanged += _ => _dispatcherQueue.TryEnqueue(RenderTypingIndicator);
                _chatServices[peer.PeerId] = chat;

                var cardTransport = _peerHost.CreateAttentionCardTransport(peer.PeerId);
                var cards = new AttentionCardService(cardTransport);
                cards.CardReceived += card => OnIncomingAttentionCard(peer.PeerId, card);
                cards.Conversation.CardAdded += _ => _dispatcherQueue.TryEnqueue(RenderAttentionCardShelf);
                cards.Conversation.CardUpdated += _ => _dispatcherQueue.TryEnqueue(RenderAttentionCardShelf);
                _attentionCardServices[peer.PeerId] = cards;
                _attentionCardTransports[peer.PeerId] = cardTransport;
                _attentionCardRouters.Clear();
            }
        }
        var choices = peers.Select(peer =>
        {
            var online = _peerHost.ConnectedPeerIds.Contains(peer.PeerId);
            return new PeerChoice(peer.PeerId, peer.FriendlyName,
                $"{peer.FriendlyName} ({RemotePresenceLabel(peer.PeerId, online)})");
        }).ToList();
        _refreshingPeerChoices = true;
        ChatRecipientCombo.ItemsSource = choices;
        ComposerRecipientCombo.ItemsSource = choices.ToList();
        HandsFreeRecipientCombo.ItemsSource = choices.ToList();
        ChatRecipientCombo.SelectedIndex = choices.FindIndex(choice => choice.PeerId == _selectedChatPeerId);
        ComposerRecipientCombo.SelectedIndex = choices.FindIndex(choice => choice.PeerId == _selectedAttentionPeerId);
        _selectedChatPeerId = (ChatRecipientCombo.SelectedItem as PeerChoice)?.PeerId;
        _selectedAttentionPeerId = (ComposerRecipientCombo.SelectedItem as PeerChoice)?.PeerId;
        _refreshingPeerChoices = false;
        UpdateMessagingEnabled();
        RefreshAudioPeers(peers);
        ApplyFamilySelection();
    }

    void RefreshAudioPeers(IReadOnlyList<ApprovedPeer> peers)
    {
        if (_peerHost is null) return;
        foreach (var peer in peers)
        {
            if (_audioNegotiators.ContainsKey(peer.PeerId)) continue;
            var visible = _lastDiscoveredPeers.FirstOrDefault(candidate =>
                Guid.TryParseExact(candidate.PeerIdHint.Value, "N", out var id) && id == peer.PeerId);
            var endpoint = visible?.Endpoints
                .OrderBy(item => item.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 0 : 1)
                .FirstOrDefault();
            var remoteAddress = endpoint?.Address ?? _peerHost.ConnectedPeerAddress(peer.PeerId);
            if (remoteAddress is null) continue;

            var negotiator = new AudioSessionNegotiator(
                _peerHost.CreateAudioControlTransport(peer.PeerId),
                remoteAddress,
                () => new AudioGraphDevice());
            negotiator.IncomingOffer += (offer, messageId) =>
                _ = AcceptIncomingAudioAsync(peer.PeerId, negotiator, offer, messageId);
            negotiator.ModeSessionReady += (session, mode) => OnAudioSessionReady(peer.PeerId, session, mode);
            negotiator.SessionStopped += () => _dispatcherQueue.TryEnqueue(() => OnAudioSessionStopped(peer.PeerId));
            negotiator.NegotiationFailed += reason => _dispatcherQueue.TryEnqueue(() =>
            {
                PushToTalkHint.Text = reason;
            });
            _audioNegotiators[peer.PeerId] = negotiator;
            DiagnosticLog.Current.Info("audio.negotiator-created", $"peer={peer.PeerId} remote={remoteAddress} source={(endpoint is null ? "connection" : "discovery")}");
        }
    }

    async Task AcceptIncomingAudioAsync(Guid peerId, AudioSessionNegotiator negotiator, AudioSessionOffer offer, Guid messageId)
    {
        if (DndPolicy.IsSuppressed(_dndSettings?.DndEnabled ?? false,
                offer.Mode == AudioInteractionMode.HandsFree ? InteractionKind.HandsFreeRequest : InteractionKind.Voice))
        {
            await negotiator.RejectAsync(messageId, "That family member is in Do Not Disturb. Use text instead.", CancellationToken.None);
            DiagnosticLog.Current.Info("audio.offer-rejected", $"peer={peerId} mode={offer.Mode} reason=dnd");
            return;
        }
        try
        {
            await negotiator.AcceptAsync(offer, messageId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Current.Error("audio.accept-failed", $"peer={peerId}", ex);
        }
    }

    void OnAudioSessionReady(Guid peerId, AudioPipelineSession session, AudioInteractionMode mode) => _dispatcherQueue.TryEnqueue(async () =>
    {
        if (_audioSessions.Remove(peerId, out var previous)) await previous.DisposeAsync();
        _audioSessions[peerId] = session;
        session.State.StateChanged += (sender, state) => _dispatcherQueue.TryEnqueue(() =>
        {
            if (state == AudioSessionState.Failed)
            {
                PushToTalkHint.Text = "Audio device failed. Text is still available.";
                _ = RetireFailedAudioSessionAsync(peerId, session);
            }
        });
        try
        {
            await session.StartAsync();
            if (mode == AudioInteractionMode.HandsFree)
            {
                _handsFreeActive = true;
                _handsFreePeerId = peerId;
                SelectPeerChoice(HandsFreeRecipientCombo, peerId);
                session.StartTransmitting();
                RenderHandsFreeState();
                UpdateMessagingEnabled();
            }
            else if (mode == AudioInteractionMode.GroupVoice && _hasGroupFloor && _pushToTalkHeld)
                session.StartTransmitting();
            else if (_pushToTalkHeld && HandsFreeRecipientCombo.SelectedItem is PeerChoice choice && choice.PeerId == peerId)
                session.StartTransmitting();
            PushToTalkHint.Text = !session.CanTransmit
                ? "Receive-only audio active. This PC has no microphone."
                : !session.CanReceive
                ? "Transmit-only audio active. This PC has no speaker."
                : mode == AudioInteractionMode.HandsFree
                ? "Hands-free session active. Both sides can speak."
                : mode == AudioInteractionMode.GroupVoice ? "Group audio ready; transmission follows the voice floor."
                : session.Transmitting ? "Transmitting while held." : "Voice ready. Hold the button while you speak.";
        }
        catch (Exception ex)
        {
            DiagnosticLog.Current.Error("audio.start-failed", $"peer={peerId}", ex);
            PushToTalkHint.Text = "Microphone or speaker unavailable. Text is still available.";
        }
    });

    void OnAudioSessionStopped(Guid peerId)
    {
        _audioSessions.Remove(peerId);
        if (_handsFreePeerId == peerId)
        {
            _handsFreeActive = false;
            _handsFreePeerId = null;
            RenderHandsFreeState();
            UpdateMessagingEnabled();
        }
        RenderVoiceControls();
    }

    async Task RetireFailedAudioSessionAsync(Guid peerId, AudioPipelineSession failed)
    {
        if (_audioSessions.TryGetValue(peerId, out var current) && ReferenceEquals(current, failed))
            _audioSessions.Remove(peerId);
        if (_audioNegotiators.TryGetValue(peerId, out var negotiator))
            await negotiator.RetireAsync(failed);
        else
            await failed.DisposeAsync();
    }

    public async Task StopAudioAsync()
    {
        if (_groupFloorService is { Session.Ended: false } activeFloor)
            try { await activeFloor.LeaveAsync(CancellationToken.None); } catch { }
        _groupFloorService?.Dispose();
        if (_groupFloorTransport is IDisposable groupTransport) groupTransport.Dispose();
        _pushToTalkHotkey.Dispose();
        foreach (var session in _audioSessions.Values.ToList()) await session.DisposeAsync();
        _audioSessions.Clear();
        foreach (var negotiator in _audioNegotiators.Values.ToList()) await negotiator.DisposeAsync();
        _audioNegotiators.Clear();
    }

    bool CanTestSelectedSpeaker()
    {
        if (HandsFreeRecipientCombo.SelectedItem is not PeerChoice choice
            || !_audioSessions.TryGetValue(choice.PeerId, out var session)) return false;
        return session.State.State is AudioSessionState.Running or AudioSessionState.Degraded;
    }

    void TestSelectedSpeaker()
    {
        if (HandsFreeRecipientCombo.SelectedItem is PeerChoice choice
            && _audioSessions.TryGetValue(choice.PeerId, out var session)) session.PlayTestTone();
    }

    void UpdateMessagingEnabled()
    {
        if (_peerHost is null) return;
        var directChat = _selectedFamilyPeerIds.Count == 1
            && _selectedChatPeerId is Guid chatPeer && _peerHost.ConnectedPeerIds.Contains(chatPeer);
        var richService = _selectedChatPeerId is Guid selectedPeer && _chatServices.TryGetValue(selectedPeer, out var candidate) ? candidate : null;
        var textLimit = _pendingChatImage is null ? RichChatService.MaxMessageCharacters : RichChatService.MaxCaptionCharacters;
        ChatSendButton.IsEnabled = directChat && ChatInputBox.Text.Length <= textLimit
            && (_pendingChatImage is not null || !string.IsNullOrWhiteSpace(ChatInputBox.Text));
        ChatInputBox.IsEnabled = directChat;
        ChatImageButton.IsEnabled = directChat && _pendingChatImage is null && richService?.SupportsImages == true;
        ChatEmojiButton.IsEnabled = directChat;
        ChatInputBox.PlaceholderText = _selectedFamilyPeerIds.Count > 1 ? "Group chat is not supported yet"
            : richService is { SupportsImages: false } ? "Text only · peer update required for images" : "Type Markdown…";
        var canSendAttention = _selectedAttentionPeerId is Guid cardPeer && _peerHost.ConnectedPeerIds.Contains(cardPeer);
        foreach (var button in AttentionPresetButtonsPanel.Children.OfType<Button>()) button.IsEnabled = canSendAttention;
        HandsFreeButton.IsEnabled = _handsFreeActive ||
            HandsFreeRecipientCombo.SelectedItem is PeerChoice handsFreePeer
            && _peerHost.ConnectedPeerIds.Contains(handsFreePeer.PeerId);
    }

    void OnChatRecipientChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingPeerChoices) return;
        _selectedChatPeerId = (ChatRecipientCombo.SelectedItem as PeerChoice)?.PeerId;
        UpdateMessagingEnabled();
        RenderChatSpokenToggle();
        RenderChatMessages();
    }

    void OnAttentionRecipientChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingPeerChoices) return;
        _selectedAttentionPeerId = (ComposerRecipientCombo.SelectedItem as PeerChoice)?.PeerId;
        UpdateMessagingEnabled();
        RenderAttentionCardShelf();
    }

    void OnIncomingChatMessage(Guid peerId, ChatMessage message)
    {
        // MessageReceived can in principle fire off the UI thread (a real
        // PeerControlChannel's receive loop is not the UI thread) — the
        // loopback demo happens to call back synchronously on whichever
        // thread sent, but this handler must not assume that.
        _dispatcherQueue.TryEnqueue(() =>
        {
            var isHiddenInTray = !AppWin.IsVisible;
            if (isHiddenInTray) _unreadBadgePresenter.Increment();
            var dndEnabled = _dndSettings?.DndEnabled ?? false;

            // Issue #24 acceptance criterion: "DND suppresses the
            // chime/toast for incoming chat but still delivers it." The
            // message itself is already unconditionally in the conversation
            // (ChatService.OnFrameReceived never checks DND) — only the
            // audible/visual interruption below is gated, reusing the same
            // DndPolicy predicate the rest of this codebase uses for every
            // other interrupting interaction kind (AttentionChime is the
            // right kind here, not Text — DndPolicy.IsSuppressed always
            // returns false for Text itself, since text DELIVERY is never
            // suppressed; a chime FOR that text is a separate, interrupting
            // concern).
            if (DndPolicy.IsSuppressed(dndEnabled, InteractionKind.AttentionChime)) return;

            ShowChatChime();
            if (_chatTtsSettings?.IsEnabled(peerId) == true)
            {
                var semanticText = ChatMarkdown.Parse(message.Text).SpeechText;
                var speech = message.ContentKind == ChatContentKind.Image
                    ? string.IsNullOrWhiteSpace(semanticText) ? "Sent an image" : $"Sent an image. {semanticText}"
                    : semanticText;
                _ = _chatSpeechService?.SpeakAsync(speech);
            }
        });
    }

    void ShowChatChime()
    {
        ChatChimeNotice.Visibility = Visibility.Visible;
        _chatChimeHideTimer.Stop();
        _chatChimeHideTimer.Start();
    }

    void OnChatSendClick(object sender, RoutedEventArgs e)
    {
        _ = SendChatAsync();
    }

    void OnChatInputKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
        if (shift) return;
        e.Handled = true;
        _ = SendChatAsync();
    }

    async Task SendChatAsync()
    {
        var text = ChatInputBox.Text;
        if (_pendingChatImage is null && string.IsNullOrWhiteSpace(text)) return;
        if (text.Length > (_pendingChatImage is null ? RichChatService.MaxMessageCharacters : RichChatService.MaxCaptionCharacters)) return;
        if (_selectedFamilyPeerIds.Count != 1) return;
        var peerId = _selectedFamilyPeerIds.Single();
        if (_peerHost?.ConnectedPeerIds.Contains(peerId) != true || !_chatServices.TryGetValue(peerId, out var service)) return;

        var image = _pendingChatImage;
        ClearPendingChatImage();
        ChatInputBox.Text = "";
        try
        {
            if (image is null) await service.SendTextAsync(text, CancellationToken.None);
            else await service.SendImageAsync(image, text, CancellationToken.None);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Current.Error("chat.send-failed", "Chat send failed.", ex);
        }
    }

    async Task SendChatTextAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        ChatInputBox.Text = text;
        await SendChatAsync();
    }

    async void OnChatInputChanged(object sender, TextChangedEventArgs e)
    {
        UpdateChatCharacterCount();
        UpdateMessagingEnabled();
        if (_selectedChatPeerId is not Guid peerId || !_chatServices.TryGetValue(peerId, out var service)) return;
        var typing = !string.IsNullOrWhiteSpace(ChatInputBox.Text);
        if (!typing || DateTimeOffset.UtcNow - _lastTypingSignal >= TimeSpan.FromSeconds(3))
        {
            _lastTypingSignal = DateTimeOffset.UtcNow;
            try { await service.SetTypingAsync(typing, CancellationToken.None); } catch { }
        }
    }

    void UpdateChatCharacterCount()
    {
        var limit = _pendingChatImage is null ? RichChatService.MaxMessageCharacters : RichChatService.MaxCaptionCharacters;
        var remaining = limit - ChatInputBox.Text.Length;
        ChatCharacterCount.Text = $"{remaining:N0} remaining";
        ChatCharacterCount.Foreground = remaining < 0 ? DndRed : Ink;
        ChatCharacterCount.Visibility = remaining < 200 ? Visibility.Visible : Visibility.Collapsed;
    }

    void RenderTypingIndicator()
    {
        var visible = _selectedChatPeerId is Guid peerId && _chatServices.TryGetValue(peerId, out var service) && service.IsPeerTyping;
        ChatTypingText.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    async void OnChooseChatImage(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, Hwnd);
        picker.FileTypeFilter.Add(".jpg"); picker.FileTypeFilter.Add(".jpeg"); picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".gif"); picker.FileTypeFilter.Add(".webp"); picker.FileTypeFilter.Add(".bmp");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        await AttachChatImageAsync(await file.OpenStreamForReadAsync());
    }

    async void OnChatPaste(object sender, TextControlPasteEventArgs e)
    {
        var data = Clipboard.GetContent();
        if (!data.Contains(StandardDataFormats.Bitmap)) return;
        e.Handled = true;
        if (_pendingChatImage is not null) return;
        var reference = await data.GetBitmapAsync();
        using var source = await reference.OpenReadAsync();
        await AttachChatImageAsync(source.AsStreamForRead());
    }

    void OnChatDragOver(object sender, DragEventArgs e)
    {
        if (_pendingChatImage is null && e.DataView.Contains(StandardDataFormats.StorageItems))
            e.AcceptedOperation = DataPackageOperation.Copy;
    }

    async void OnChatDrop(object sender, DragEventArgs e)
    {
        if (_pendingChatImage is not null || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var file = (await e.DataView.GetStorageItemsAsync()).OfType<StorageFile>().FirstOrDefault();
        if (file is null) return;
        try { await AttachChatImageAsync(await file.OpenStreamForReadAsync()); } catch { }
    }

    async Task AttachChatImageAsync(Stream source)
    {
        ChatAttachmentPreview.Visibility = Visibility.Visible;
        ChatAttachmentDetails.Text = "Optimizing…";
        ChatSendButton.IsEnabled = false;
        try
        {
            _pendingChatImage = await ChatImageOptimizer.OptimizeAsync(source, CancellationToken.None);
            UpdateChatCharacterCount();
            ChatAttachmentThumbnail.Source = await CreateBitmapAsync(_pendingChatImage.Bytes);
            ChatAttachmentDetails.Text = $"{_pendingChatImage.Width} × {_pendingChatImage.Height} · {_pendingChatImage.Bytes.Length / 1024:N0} KiB · {_pendingChatImage.Format}";
        }
        catch (Exception ex)
        {
            DiagnosticLog.Current.Error("chat.image-prepare-failed", "Image attachment preparation failed.", ex);
            ClearPendingChatImage();
            ChatChimeNotice.Text = "Image could not be prepared";
            ShowChatChime();
        }
        finally { source.Dispose(); UpdateMessagingEnabled(); }
    }

    void OnRemoveChatAttachment(object sender, RoutedEventArgs e) => ClearPendingChatImage();

    void ClearPendingChatImage()
    {
        _pendingChatImage = null;
        UpdateChatCharacterCount();
        ChatAttachmentThumbnail.Source = null;
        ChatAttachmentPreview.Visibility = Visibility.Collapsed;
        UpdateMessagingEnabled();
    }

    static async Task<BitmapImage> CreateBitmapAsync(byte[] bytes)
    {
        var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
        }
        stream.Seek(0);
        var bitmap = new BitmapImage();
        bitmap.SetSource(stream);
        return bitmap;
    }

    void OnShowEmojiPicker(object sender, RoutedEventArgs e)
    {
        if (_emojiFlyout is null)
        {
            var search = new TextBox { PlaceholderText = "Search emoji", Margin = new Thickness(0, 0, 0, 6) };
            var category = new ComboBox { ItemsSource = ChatEmojiCatalog.Categories, SelectedIndex = 0, Margin = new Thickness(0, 0, 0, 6) };
            var grid = new GridView { Width = 360, MaxHeight = 300, IsItemClickEnabled = true, SelectionMode = ListViewSelectionMode.None };
            void Filter() => grid.ItemsSource = ChatEmojiCatalog.Search(search.Text, LoadRecentEmojis(), category.SelectedItem as string);
            search.TextChanged += (_, _) => Filter();
            category.SelectionChanged += (_, _) => Filter();
            grid.ItemClick += (_, args) =>
            {
                var emoji = (string)args.ClickedItem;
                var start = ChatInputBox.SelectionStart;
                ChatInputBox.Text = ChatInputBox.Text.Insert(start, emoji);
                ChatInputBox.SelectionStart = start + emoji.Length;
                SaveRecentEmoji(emoji);
                _emojiFlyout?.Hide();
                ChatInputBox.Focus(FocusState.Programmatic);
            };
            _emojiFlyout = new Flyout { Content = new StackPanel { Children = { search, category, grid } } };
            Filter();
        }
        _emojiFlyout.ShowAt(ChatEmojiButton);
    }

    static IReadOnlyList<string> LoadRecentEmojis()
    {
        var value = ApplicationData.Current.LocalSettings.Values["chat.emoji-recents"] as string;
        return value?.Split('|', StringSplitOptions.RemoveEmptyEntries) ?? [];
    }

    static void SaveRecentEmoji(string emoji)
    {
        var recent = LoadRecentEmojis().Where(item => item != emoji).Prepend(emoji).Take(16);
        ApplicationData.Current.LocalSettings.Values["chat.emoji-recents"] = string.Join('|', recent);
    }

    void OnSpokenChatToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressSpokenChatToggleHandler || _chatTtsSettings is null) return;
        if (_selectedChatPeerId is Guid peerId) _chatTtsSettings.SetEnabled(peerId, SpokenChatCheckBox.IsChecked == true);
    }

    void RenderChatSpokenToggle()
    {
        if (_chatTtsSettings is null) return;
        _suppressSpokenChatToggleHandler = true;
        SpokenChatCheckBox.IsChecked = _selectedChatPeerId is Guid peerId && _chatTtsSettings.IsEnabled(peerId);
        _suppressSpokenChatToggleHandler = false;
    }

    void RenderChatMessages()
    {
        try
        {
            RenderChatMessagesCore();
        }
        catch (Exception ex)
        {
            // DispatcherQueue.TryEnqueue callbacks run outside the managed
            // exception path App.xaml.cs's UnhandledException hooks: an
            // uncaught exception here becomes a stowed exception and hard-
            // crashes the process (observed as CoreMessagingXP.dll faults,
            // code 0xc000027b) instead of reaching that handler.
            DiagnosticLog.Current.Error("chat.render-failed", "Chat rendering failed.", ex);
        }
    }

    void RenderChatMessagesCore()
    {
        ChatMessagesPanel.Children.Clear();
        if (_selectedChatPeerId is not Guid peerId || !_chatServices.TryGetValue(peerId, out var service)) return;
        foreach (var message in service.Conversation.Messages)
        {
            ChatMessagesPanel.Children.Add(BuildChatMessageBubble(message));
        }
    }

    Border BuildChatMessageBubble(ChatMessage message)
    {
        var isMine = message.Direction == ChatMessageDirection.Sent;
        var textColor = isMine ? Paper : Ink;
        var content = new StackPanel { Spacing = 4 };
        if (message.ContentKind == ChatContentKind.Image)
        {
            if (message.Image is not null)
            {
                var imageControl = new Image { MaxWidth = 240, MaxHeight = 180, Stretch = Stretch.Uniform };
                imageControl.Tapped += (_, _) => ShowChatImage(message.Image);
                content.Children.Add(imageControl);
                _ = SetImageSourceAsync(imageControl, message.Image.Bytes);
                if (message.Direction == ChatMessageDirection.Sent && message.TransferState == ChatTransferState.Sending)
                {
                    var cancel = new Button { Content = "Cancel", FontSize = 9 };
                    cancel.Click += (_, _) =>
                    {
                        if (_selectedChatPeerId is Guid id && _chatServices.TryGetValue(id, out var rich)) _ = rich.CancelOutgoingImageAsync(message.MessageId, CancellationToken.None);
                    };
                    content.Children.Add(cancel);
                }
            }
            else
            {
                content.Children.Add(new TextBlock { Text = message.TransferState == ChatTransferState.Failed ? "Image not received" : "Receiving image…", Foreground = textColor });
                content.Children.Add(new ProgressBar { Minimum = 0, Maximum = 1, Value = message.TransferProgress, IsIndeterminate = message.TransferProgress <= 0 });
                if (message.Direction == ChatMessageDirection.Received && message.TransferState == ChatTransferState.Receiving)
                {
                    var cancel = new Button { Content = "Cancel", FontSize = 9 };
                    cancel.Click += (_, _) =>
                    {
                        if (_selectedChatPeerId is Guid id && _chatServices.TryGetValue(id, out var rich)) _ = rich.CancelImageAsync(message.MessageId, CancellationToken.None);
                    };
                    content.Children.Add(cancel);
                }
            }
        }
        if (!string.IsNullOrWhiteSpace(message.Text)) content.Children.Add(BuildMarkdownBlock(message.Text, textColor));
        content.Children.Add(new TextBlock { Text = DescribeDeliveryState(message), FontSize = 9, Opacity = 0.75, Foreground = textColor });

        if (message.DeliveryState == ChatDeliveryState.Undelivered)
        {
            var resendButton = new Button { Content = "Resend", FontSize = 9, Background = Mustard, Foreground = Ink };
            resendButton.Click += (_, _) =>
            {
                if (message.ContentKind == ChatContentKind.Image && message.Image is not null && _selectedChatPeerId is Guid id && _chatServices.TryGetValue(id, out var rich))
                    _ = rich.SendImageAsync(message.Image, message.Text, CancellationToken.None);
                else _ = SendChatTextAsync(message.Text);
            };
            content.Children.Add(resendButton);
        }

        return new Border
        {
            Child = content, Background = isMine ? Teal : Paper, BorderBrush = Ink,
            BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 6, 10, 6), MaxWidth = 280,
            HorizontalAlignment = isMine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        };
    }

    RichTextBlock BuildMarkdownBlock(string source, Brush foreground)
    {
        var block = new RichTextBlock { TextWrapping = TextWrapping.Wrap, Foreground = foreground, FontSize = 12, IsTextSelectionEnabled = true };
        var paragraph = new Paragraph();
        foreach (var inline in ChatMarkdown.Parse(source).Inlines)
        {
            if (inline is ChatLinkRun link)
            {
                var hyperlink = new Hyperlink();
                hyperlink.Inlines.Add(new Run { Text = link.Text });
                ToolTipService.SetToolTip(hyperlink, link.Destination.Host);
                hyperlink.Click += (_, _) => _ = OpenChatLinkAsync(link);
                paragraph.Inlines.Add(hyperlink);
            }
            else if (inline is ChatTextRun text)
            {
                var run = new Run
                {
                    Text = text.Text,
                    FontWeight = text.Bold ? FontWeights.Bold : FontWeights.Normal,
                    FontStyle = text.Italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
                    TextDecorations = text.Strikethrough ? Windows.UI.Text.TextDecorations.Strikethrough : Windows.UI.Text.TextDecorations.None,
                };
                if (text.Code) run.FontFamily = new FontFamily("Consolas");
                paragraph.Inlines.Add(run);
            }
        }
        block.Blocks.Add(paragraph);
        return block;
    }

    async Task OpenChatLinkAsync(ChatLinkRun link)
    {
        if (link.RequiresConfirmation)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = ChatPanel.XamlRoot, Title = "Open this website?", Content = link.Destination.AbsoluteUri,
                PrimaryButtonText = "Open", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }
        await Windows.System.Launcher.LaunchUriAsync(link.Destination);
    }

    static async Task SetImageSourceAsync(Image control, byte[] bytes) => control.Source = await CreateBitmapAsync(bytes);

    void ShowChatImage(OptimizedChatImage image)
    {
        RootShellGrid.IsHitTestVisible = false;
        RootShellGrid.Opacity = 0.55;
        var workArea = DisplayArea.GetFromWindowId(AppWin.Id, DisplayAreaFallback.Primary).WorkArea;
        var viewer = new ChatImageViewerWindow(image, workArea);
        viewer.Closed += (_, _) => { RootShellGrid.IsHitTestVisible = true; RootShellGrid.Opacity = 1; };
        viewer.Activate();
    }

    static string DescribeDeliveryState(ChatMessage message)
    {
        if (message.TransferState == ChatTransferState.RecipientCancelled) return "recipient cancelled";
        if (message.TransferState == ChatTransferState.Cancelled) return "cancelled";
        if (message.TransferState == ChatTransferState.Failed) return "image not received";
        if (message.Direction == ChatMessageDirection.Received) return "received";
        if (message.ContentKind == ChatContentKind.Image && message.TransferState == ChatTransferState.Sending)
            return $"sending… {message.TransferProgress:P0}";
        return message.DeliveryState switch
        {
            ChatDeliveryState.Pending => "sending...", ChatDeliveryState.Delivered => "delivered",
            ChatDeliveryState.Undelivered => "undelivered", _ => "",
        };
    }

    // ---- Issue #25: attention-card composer + shelf ----

    /// <summary>Gives this window access to the real
    /// <see cref="AttentionCardToastPresenter"/> so an inbound card (past the
    /// DND chime gate — see <see cref="OnIncomingAttentionCard"/>) can show a
    /// real native toast. Called once from App.OnLaunched, mirroring
    /// <see cref="AttachChat"/>. The send/receive/Ack pipeline itself is
    /// already running by this point — see <see cref="InitializeAttentionCardShelf"/>,
    /// called unconditionally from the constructor exactly like
    /// <see cref="InitializeChatDrawer"/>, since it needs no external
    /// dependency to exist.</summary>
    public void AttachAttentionCards(AttentionCardToastPresenter toastPresenter) => _attentionCardToastPresenter = toastPresenter;

    /// <summary>Wires up a real <see cref="AttentionCardService"/> pipeline
    /// (real <see cref="AttentionCardFrameCodec"/> encode/decode, real
    /// <see cref="AttentionCardConversation"/> delivery-/ack-state
    /// bookkeeping, real mechanical Delivered receipts AND real explicit
    /// Acknowledged receipts via <see cref="FrameDispatcher"/>) against an
    /// in-process <see cref="LoopbackAttentionCardTransport"/> pair — no live
    /// multi-peer connection roster exists in this app shell yet (the same
    /// gap #23/#24's reports flagged), so this is the same honest
    /// "recipient select has exactly one demo peer" constraint #22/#24 hit,
    /// not a pretense of a real roster. <c>_attentionCardServiceLocal</c> is
    /// "this device"; <c>_attentionCardServicePeer</c> exists purely so the
    /// demo button can make the simulated peer actually send real frames
    /// back.</summary>
    void InitializeAttentionCardShelf()
    {
        RenderAttentionPresetButtons();
        RenderAttentionCardShelf();
    }

    void OnIncomingAttentionCard(Guid peerId, AttentionCard card)
    {
        // CardReceived can in principle fire off the UI thread (a real
        // PeerControlChannel's receive loop is not the UI thread) — mirrors
        // OnIncomingChatMessage's identical caution, even though the
        // loopback demo happens to call back synchronously today.
        _dispatcherQueue.TryEnqueue(async () =>
        {
            if (!AppWin.IsVisible) _unreadBadgePresenter.Increment();
            var dndEnabled = _dndSettings?.DndEnabled ?? false;

            // Issue #25 acceptance criterion: "DND produces no chime/toast
            // while still queuing the item silently." The card itself is
            // already unconditionally in the conversation by this point
            // (AttentionCardService.OnFrameReceived never checks DND, and
            // RenderAttentionCardShelf above already re-rendered it into the
            // shelf via CardAdded) — only the native-toast CREATION below is
            // gated, reusing the exact same DndPolicy predicate/InteractionKind
            // OnIncomingChatMessage uses for the chat chime.
            if (DndPolicy.IsSuppressed(dndEnabled, InteractionKind.AttentionChime))
            {
                DiagnosticLog.Current.Info("attention-card.toast-suppressed", $"card={card.MessageId} reason=dnd");
                return;
            }

            var from = _identityStore?.ApprovedPeers.FirstOrDefault(peer => peer.PeerId == peerId)?.FriendlyName ?? "a family member";
            var observation = await AttentionCardToastObservation.ObserveAsync(
                _attentionCardToastPresenter is null
                    ? null
                    : () => _attentionCardToastPresenter.ShowAsync(card, fromLabel: from));
            if (observation.Status == AttentionCardToastObservationStatus.Submitted)
                DiagnosticLog.Current.Info("attention-card.toast-submitted",
                    $"card={card.MessageId} from={peerId} setting={observation.Setting}");
            else if (observation.Status == AttentionCardToastObservationStatus.Blocked)
                DiagnosticLog.Current.Info("attention-card.toast-blocked",
                    $"card={card.MessageId} from={peerId} setting={observation.Setting}");
            else
                DiagnosticLog.Current.Error("attention-card.toast-failed",
                    $"card={card.MessageId} from={peerId}", observation.Error);

            if (observation.RequiresFallback) ShowIncomingAttentionFallback(card, from);
        });
    }

    void ShowIncomingAttentionFallback(AttentionCard card, string from)
    {
        IncomingAttentionNotice.Message = $"{card.Icon}  {card.Purpose} — from {from}";
        IncomingAttentionNotice.IsOpen = true;
    }

    /// <summary>Called by App.xaml.cs when a native toast's Acknowledge
    /// button was clicked — deliberately never shows/activates this window,
    /// satisfying "action activation sends exactly one acknowledgement
    /// without forcing the main window open." The card being acknowledged
    /// is always one THIS device received (see
    /// <see cref="AttentionCardService.AcknowledgeAsync"/>'s own
    /// sent-card guard), so this always targets <c>_attentionCardServiceLocal</c>,
    /// never the demo peer service.</summary>
    public Task AcknowledgeAttentionCardAsync(Guid cardMessageId, CancellationToken cancellationToken)
    {
        var service = _attentionCardServices.Values.FirstOrDefault(candidate =>
            candidate.Conversation.Cards.Any(card => card.MessageId == cardMessageId));
        return service?.AcknowledgeAsync(cardMessageId, cancellationToken) ?? Task.CompletedTask;
    }

    void RenderAttentionPresetButtons()
    {
        AttentionPresetButtonsPanel.Children.Clear();
        foreach (var preset in AttentionCardPresets.Presets)
        {
            var button = new Button
            {
                Content = preset.Icon,
                Width = 64,
                Height = 64,
                Padding = new Thickness(0),
                FontSize = 30,
                Background = Paper,
                Foreground = Ink,
                BorderBrush = Ink,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(10),
            };
            ToolTipService.SetToolTip(button, preset.Purpose);
            button.Click += (_, _) => _ = SendAttentionCardAsync(preset.Purpose, preset.Icon);
            AttentionPresetButtonsPanel.Children.Add(button);
        }
        UpdateMessagingEnabled();
    }

    async Task SendAttentionCardAsync(string purpose, string icon)
    {

        var selectedOnline = _selectedFamilyPeerIds
            .Where(id => _peerHost?.ConnectedPeerIds.Contains(id) == true && _attentionCardServices.ContainsKey(id))
            .ToList();
        if (selectedOnline.Count == 0) return;
        if (selectedOnline.Count > 1)
        {
            foreach (var selectedPeerId in selectedOnline)
            {
                try { await _attentionCardServices[selectedPeerId].SendAsync(purpose, icon, CancellationToken.None); }
                catch { }
            }
            return;
        }

        var peerId = selectedOnline[0];
        var cardService = _attentionCardServices[peerId];

        try
        {
            var contact = FindContactForPeer(peerId);
            if (contact is null || _presenceReceiver is null || _manualOverrideStore is null)
            {
                await cardService.SendAsync(purpose, icon, CancellationToken.None);
            }
            else
            {
                var router = GetAttentionCardRouter(contact);
                await router.SendAsync(
                    _presenceReceiver.LiveDevices(contact.MemberPeerIds, DateTimeOffset.UtcNow),
                    purpose,
                    icon,
                    CancellationToken.None);
            }
        }
        catch
        {
            // Already reflected as Undelivered in the conversation by
            // AttentionCardService itself (ADR-0001: no auto-resend) —
            // nothing further to do here beyond not crashing the UI thread.
        }
    }

    Contact? FindContactForPeer(Guid peerId) =>
        _contactStore?.Contacts.FirstOrDefault(contact => contact.MemberPeerIds.Contains(peerId));

    AttentionCardFanoutRouter GetAttentionCardRouter(Contact contact)
    {
        if (_attentionCardRouters.TryGetValue(contact.ContactId, out var existing)) return existing;

        var endpoints = contact.MemberPeerIds
            .Where(id => _attentionCardServices.ContainsKey(id) && _attentionCardTransports.ContainsKey(id))
            .ToDictionary(id => id, id => new AttentionCardDeviceEndpoint
            {
                DeviceId = id,
                Service = _attentionCardServices[id],
                Transport = _attentionCardTransports[id],
            });
        var router = new AttentionCardFanoutRouter(
            endpoints,
            () => _manualOverrideStore!.Get(contact.ContactId),
            () => _manualOverrideStore!.Clear(contact.ContactId));
        _attentionCardRouters[contact.ContactId] = router;
        return router;
    }

    void RenderAttentionCardShelf()
    {
        var cards = _selectedAttentionPeerId is Guid peerId && _attentionCardServices.TryGetValue(peerId, out var service)
            ? service.Conversation.Cards
            : [];
        NoAttentionCardsNotice.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        AttentionCardShelfPanel.Children.Clear();
        // Newest first, matching the composer sitting logically "after" the
        // most recent activity.
        for (var i = cards.Count - 1; i >= 0; i--)
        {
            AttentionCardShelfPanel.Children.Add(BuildAttentionCardTile(cards[i]));
        }
    }

    Border BuildAttentionCardTile(AttentionCard card)
    {
        var content = new StackPanel { Spacing = 3, Width = 120 };
        content.Children.Add(new TextBlock
        {
            Text = card.Icon,
            FontSize = 28,
            Foreground = Ink,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        content.Children.Add(new TextBlock
        {
            Text = card.Purpose,
            FontWeight = FontWeights.Bold,
            FontSize = 11,
            Foreground = Ink,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
        });
        content.Children.Add(new TextBlock
        {
            Text = card.Direction == AttentionCardDirection.Sent ? DescribeAttentionCardState(card) : "from the demo peer",
            FontSize = 9,
            Opacity = 0.7,
            Foreground = Ink,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        if (card.Direction == AttentionCardDirection.Received && card.AckState == AttentionCardAckState.NotAcknowledged)
        {
            var ackButton = new Button
            {
                Content = "Ack",
                FontSize = 10,
                Background = Teal,
                Foreground = Paper,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            ackButton.Click += (_, _) => _ = AcknowledgeAttentionCardAsync(card.MessageId, CancellationToken.None);
            content.Children.Add(ackButton);
        }
        else if (card.AckState == AttentionCardAckState.Acknowledged)
        {
            content.Children.Add(new TextBlock
            {
                Text = "✓ acknowledged",
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                Foreground = Teal,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }

        return new Border
        {
            Child = content,
            Background = Paper,
            BorderBrush = Ink,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 8, 10, 8),
            Opacity = card.AckState == AttentionCardAckState.Acknowledged ? 0.6 : 1.0,
        };
    }

    static string DescribeAttentionCardState(AttentionCard card)
    {
        if (card.AckState == AttentionCardAckState.Acknowledged) return "acknowledged";
        return card.DeliveryState switch
        {
            AttentionCardDeliveryState.Pending => "sending...",
            AttentionCardDeliveryState.Delivered => "delivered",
            AttentionCardDeliveryState.Undelivered => "undelivered",
            _ => "",
        };
    }

    sealed record PeerChoice(Guid PeerId, string FriendlyName, string Label)
    {
        public override string ToString() => Label;
    }

    sealed class GroupAudioPreparer(MainWindow owner) : IGroupAudioPreparer
    {
        public async Task PrepareAsync(Guid peerId, CancellationToken cancellationToken)
        {
            if (owner._audioSessions.ContainsKey(peerId)) return;
            if (!owner._audioNegotiators.TryGetValue(peerId, out var negotiator))
                throw new InvalidOperationException($"Voice is not ready for {owner.PeerName(peerId)}.");
            await negotiator.OfferAsync(cancellationToken, AudioInteractionMode.GroupVoice);
        }
    }
}
