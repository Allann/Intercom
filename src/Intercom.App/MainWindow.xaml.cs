using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Windows.UI;
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
using Intercom.Updates;
using Intercom.App.AttentionCards;
using Intercom.App.Audio;
using Intercom.App.Chat;

namespace Intercom.App;

public sealed partial class MainWindow : Window, IResidentWindow
{
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

    public nint Hwnd { get; }
    public AppWindow AppWin { get; }

    readonly DispatcherQueue _dispatcherQueue;
    readonly DispatcherQueueTimer _chatChimeHideTimer;
    readonly DispatcherQueueTimer _audioDiagnosticsTimer;
    readonly AudioDeviceSettingsStore _audioDeviceSettingsStore = new();
    AudioDeviceSettings _audioDeviceSettings = new(null, null);
    bool _loadingAudioDevices;

    IdentityStore? _identityStore;
    DndSettingsStore? _dndSettings;

    // ---- Issue #31: manual update check ----
    UpdateAvailableNotice? _pendingUpdateNotice;
    Action<AppVersion>? _onUpdateNoticeDismissed;

    ChatTtsSettingsStore? _chatTtsSettings;
    ChatSpeechService? _chatSpeechService;
    readonly Dictionary<Guid, ChatService> _chatServices = [];
    readonly Dictionary<Guid, IAttentionCardTransport> _attentionCardTransports = [];
    readonly Dictionary<Guid, AttentionCardFanoutRouter> _attentionCardRouters = [];
    readonly Dictionary<Guid, AudioSessionNegotiator> _audioNegotiators = [];
    readonly Dictionary<Guid, AudioPipelineSession> _audioSessions = [];

    // ---- Issue #25: attention cards ----
    const string CustomComposerPresetLabel = "Custom…";

    readonly Dictionary<Guid, AttentionCardService> _attentionCardServices = [];
    LanPairingHost? _peerHost;
    ContactStore? _contactStore;
    ManualOverrideStore? _manualOverrideStore;
    PresenceReceiver? _presenceReceiver;
    Guid? _selectedChatPeerId;
    Guid? _selectedAttentionPeerId;
    readonly HashSet<Guid> _selectedFamilyPeerIds = [];
    AttentionCardToastPresenter? _attentionCardToastPresenter;
    string _selectedComposerIcon = AttentionCardPresets.Presets[0].Icon;

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
    IReadOnlyList<VisiblePeer> _lastDiscoveredPeers = [];
    bool _refreshingPeerChoices;
    bool _hasGroupFloor;
    bool _handRaised;
    bool _handsFreeActive;
    bool _pushToTalkHeld;

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

        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _chatChimeHideTimer = _dispatcherQueue.CreateTimer();
        _chatChimeHideTimer.Interval = TimeSpan.FromSeconds(3);
        _chatChimeHideTimer.Tick += (_, _) =>
        {
            _chatChimeHideTimer.Stop();
            ChatChimeNotice.Visibility = Visibility.Collapsed;
        };
        _audioDiagnosticsTimer = _dispatcherQueue.CreateTimer();
        _audioDiagnosticsTimer.Interval = TimeSpan.FromSeconds(1);
        _audioDiagnosticsTimer.Tick += (_, _) => RenderAudioDiagnostics();

        // ADR-0003: closing the main window hides it to the tray; only an
        // explicit tray "Quit" action actually ends the process.
        AppWin.Closing += OnAppWindowClosing;

        InitializeChatDrawer();
        InitializeAttentionCardShelf();
        _ = LoadAudioDevicesAsync();
    }

    async Task LoadAudioDevicesAsync()
    {
        try
        {
            _audioDeviceSettings = _audioDeviceSettingsStore.Load();
            var devices = await AudioGraphDevice.GetDevicesAsync();
            var inputs = new List<AudioDeviceChoice> { new(null, "Windows communications default"), new("", "No microphone (listen only)") };
            inputs.AddRange(devices.Inputs);
            var outputs = new List<AudioDeviceChoice> { new(null, "Windows communications default") };
            outputs.AddRange(devices.Outputs);
            _loadingAudioDevices = true;
            AudioInputCombo.ItemsSource = inputs;
            AudioOutputCombo.ItemsSource = outputs;
            AudioInputCombo.SelectedItem = inputs.FirstOrDefault(item => item.Id == _audioDeviceSettings.InputDeviceId) ?? inputs[0];
            AudioOutputCombo.SelectedItem = outputs.FirstOrDefault(item => item.Id == _audioDeviceSettings.OutputDeviceId) ?? outputs[0];
            _loadingAudioDevices = false;
            RenderSelectedAudioDevices();
        }
        catch (Exception ex)
        {
            AudioDiagnosticsText.Text = "Could not read the Windows communications audio devices.";
            DiagnosticLog.Current.Error("audio.devices-failed", "Could not resolve communications audio devices.", ex);
        }
    }

    async void OnAudioDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingAudioDevices || AudioInputCombo.SelectedItem is not AudioDeviceChoice input
            || AudioOutputCombo.SelectedItem is not AudioDeviceChoice output) return;
        _audioDeviceSettings = new(input.Id, output.Id);
        _audioDeviceSettingsStore.Save(_audioDeviceSettings);
        await StopAudioAsync();
        RefreshMessagingPeers();
        RenderSelectedAudioDevices();
        PushToTalkHint.Text = "Audio device changed. Hold the button to reconnect voice.";
        DiagnosticLog.Current.Info("audio.devices-selected", $"mic={input.Name} speaker={output.Name}");
    }

    void RenderSelectedAudioDevices()
    {
        var input = (AudioInputCombo.SelectedItem as AudioDeviceChoice)?.Name ?? "Loading…";
        var output = (AudioOutputCombo.SelectedItem as AudioDeviceChoice)?.Name ?? "Loading…";
        AudioDiagnosticsText.Text = $"Mic: {input}\nSpeaker: {output}\nVoice session: not initialized.";
    }

    void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = true;
        AppWin.Hide();
    }

    public void ShowFromTray()
    {
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
        var pairablePeers = peers.Where(peer => peer.Spki is not null
            && !approvedPeers.Any(approved => approved.PeerId.ToString("N").Equals(peer.PeerIdHint.Value, StringComparison.OrdinalIgnoreCase))).ToList();
        NoDiscoveredPeersNotice.Visibility = approvedPeers.Count == 0 && pairablePeers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DiscoveredPeersList.Children.Clear();
        foreach (var approved in approvedPeers)
        {
            var online = _peerHost?.ConnectedPeerIds.Contains(approved.PeerId) == true;
            var member = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton
            {
                Content = $"✓ {approved.FriendlyName} · {(online ? "online" : "offline")}",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                IsEnabled = online,
                IsChecked = _selectedFamilyPeerIds.Contains(approved.PeerId),
                Tag = approved.PeerId,
            };
            member.Click += OnFamilyMemberClick;
            DiscoveredPeersList.Children.Add(member);
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

        await new ContentDialog
        {
            Title = "Add Family Member",
            Content = "Open Intercom on the other PC. It will appear here automatically when both PCs are on the same private network.",
            CloseButtonText = "Done",
            XamlRoot = Content.XamlRoot,
        }.ShowAsync();
    }

    void OnRaiseHandClick(object sender, RoutedEventArgs e)
    {
        _handRaised = !_handRaised;
        _hasGroupFloor = _handRaised;
        RaiseHandButton.Content = _handRaised ? "✓ Floor Granted" : "✋ Raise Hand";
        GroupFloorSpeakerText.Text = _handRaised ? "● You have the floor" : "● Nobody has the floor";
        GroupFloorCoordinatorText.Text = _handRaised ? "You are coordinator" : "Start a floor to become coordinator";
        EndFloorButton.Visibility = _handRaised ? Visibility.Visible : Visibility.Collapsed;
        RenderVoiceControls();
    }

    void OnInterruptClick(object sender, RoutedEventArgs e)
    {
        _handRaised = true;
        _hasGroupFloor = true;
        RaiseHandButton.Content = "✓ Floor Granted";
        GroupFloorSpeakerText.Text = "● You have the floor";
        GroupFloorCoordinatorText.Text = "Interrupt granted directly";
        EndFloorButton.Visibility = Visibility.Visible;
        RenderVoiceControls();
    }

    void OnEndFloorClick(object sender, RoutedEventArgs e)
    {
        _handRaised = false;
        _hasGroupFloor = false;
        RaiseHandButton.Content = "✋ Raise Hand";
        GroupFloorSpeakerText.Text = "● Nobody has the floor";
        GroupFloorCoordinatorText.Text = "Start a floor to become coordinator";
        EndFloorButton.Visibility = Visibility.Collapsed;
        RenderVoiceControls();
    }

    void OnHandsFreeClick(object sender, RoutedEventArgs e)
    {
        if (HandsFreeRecipientCombo.SelectedItem is not PeerChoice choice) return;
        _handsFreeActive = !_handsFreeActive;
        HandsFreeStatusText.Text = _handsFreeActive ? $"Hands-free with {choice.FriendlyName}" : "No hands-free session";
        HandsFreeButton.Content = _handsFreeActive ? "End" : "Start Hands-Free";
        HandsFreeButton.Background = _handsFreeActive ? DndRed : AvailableGreen;
        HandsFreeRecipientCombo.IsEnabled = !_handsFreeActive;
        RenderVoiceControls();
    }

    void OnHandsFreeRecipientChanged(object sender, SelectionChangedEventArgs e) => UpdateMessagingEnabled();

    async void OnPushToTalkPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (HandsFreeRecipientCombo.SelectedItem is not PeerChoice choice)
        {
            DiagnosticLog.Current.Warning("audio.ptt-ignored", $"reason=no-selected-peer selectedFamilyCount={_selectedFamilyPeerIds.Count}");
            PushToTalkHint.Text = "Select one online family member first.";
            return;
        }
        DiagnosticLog.Current.Info("audio.ptt-pressed", $"peer={choice.PeerId}");
        PushToTalkButton.CapturePointer(e.Pointer);
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
            await negotiator.OfferAsync(CancellationToken.None);
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
        DiagnosticLog.Current.Info("audio.ptt-released", $"selectedPeer={(HandsFreeRecipientCombo.SelectedItem as PeerChoice)?.PeerId}");
        _pushToTalkHeld = false;
        PushToTalkButton.ReleasePointerCaptures();
        if (HandsFreeRecipientCombo.SelectedItem is PeerChoice choice
            && _audioSessions.TryGetValue(choice.PeerId, out var session))
            session.StopTransmitting();
        RenderVoiceControls();
    }

    void RenderVoiceControls()
    {
        var connectedPeer = HandsFreeRecipientCombo.SelectedItem is PeerChoice choice
            && _peerHost?.ConnectedPeerIds.Contains(choice.PeerId) == true;
        PushToTalkButton.IsEnabled = connectedPeer;
        PushToTalkButton.Content = new TextBlock { Text = "Hold to\nTalk", TextAlignment = TextAlignment.Center };
        PushToTalkButton.Background = connectedPeer ? DndRed : new SolidColorBrush(Color.FromArgb(255, 0xCB, 0xBF, 0xA4));
        PushToTalkHint.Text = PushToTalkButton.IsEnabled
            ? "Hold the button while you speak."
            : "Choose an online family member to talk.";
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

    public async void ShowPairingFailed(string message)
    {
        var dialog = new ContentDialog
        {
            Title = "Couldn’t connect to that PC",
            Content = message,
            CloseButtonText = "Close",
            XamlRoot = Content.XamlRoot,
        };
        await dialog.ShowAsync();
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
        peerHost.ConnectionsChanged += () => _dispatcherQueue.TryEnqueue(() => UpdateDiscoveredPeers(_lastDiscoveredPeers));
        RefreshMessagingPeers();
    }

    void OnFamilyMemberClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Microsoft.UI.Xaml.Controls.Primitives.ToggleButton { Tag: Guid peerId } member) return;
        if (member.IsChecked == true) _selectedFamilyPeerIds.Add(peerId);
        else _selectedFamilyPeerIds.Remove(peerId);
        ApplyFamilySelection();
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
        UpdateMessagingEnabled();
        RenderChatSpokenToggle();
        RenderChatMessages();
        RenderAttentionCardShelf();
        RenderVoiceControls();
        RenderAudioDiagnostics();
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
                var chat = new ChatService(_peerHost.CreateChatTransport(peer.PeerId));
                chat.MessageReceived += message => OnIncomingChatMessage(peer.PeerId, message);
                chat.Conversation.MessageAdded += _ => _dispatcherQueue.TryEnqueue(RenderChatMessages);
                chat.Conversation.MessageUpdated += _ => _dispatcherQueue.TryEnqueue(RenderChatMessages);
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
        var choices = peers.Select(peer => new PeerChoice(peer.PeerId, peer.FriendlyName,
            $"{peer.FriendlyName} ({(_peerHost.ConnectedPeerIds.Contains(peer.PeerId) ? "online" : "offline")})")).ToList();
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
                () => new AudioGraphDevice(_audioDeviceSettings.InputDeviceId, _audioDeviceSettings.OutputDeviceId));
            negotiator.IncomingOffer += (offer, messageId) =>
                _ = AcceptIncomingAudioAsync(peer.PeerId, negotiator, offer, messageId);
            negotiator.SessionReady += session => OnAudioSessionReady(peer.PeerId, session);
            negotiator.NegotiationFailed += reason => _dispatcherQueue.TryEnqueue(() =>
            {
                PushToTalkHint.Text = reason;
                AudioDiagnosticsText.Text += $"\nVoice negotiation failed: {reason}";
            });
            _audioNegotiators[peer.PeerId] = negotiator;
            DiagnosticLog.Current.Info("audio.negotiator-created", $"peer={peer.PeerId} remote={remoteAddress} source={(endpoint is null ? "connection" : "discovery")}");
        }
    }

    async Task AcceptIncomingAudioAsync(Guid peerId, AudioSessionNegotiator negotiator, AudioSessionOffer offer, Guid messageId)
    {
        try
        {
            await negotiator.AcceptAsync(offer, messageId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Current.Error("audio.accept-failed", $"peer={peerId}", ex);
        }
    }

    void OnAudioSessionReady(Guid peerId, AudioPipelineSession session) => _dispatcherQueue.TryEnqueue(async () =>
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
            _audioDiagnosticsTimer.Start();
            if (_pushToTalkHeld && HandsFreeRecipientCombo.SelectedItem is PeerChoice choice && choice.PeerId == peerId)
                session.StartTransmitting();
            PushToTalkHint.Text = session.Transmitting ? "Transmitting while held." : "Voice ready. Hold the button while you speak.";
        }
        catch (Exception ex)
        {
            DiagnosticLog.Current.Error("audio.start-failed", $"peer={peerId}", ex);
            PushToTalkHint.Text = "Microphone or speaker unavailable. Text is still available.";
        }
    });

    async Task RetireFailedAudioSessionAsync(Guid peerId, AudioPipelineSession failed)
    {
        if (_audioSessions.TryGetValue(peerId, out var current) && ReferenceEquals(current, failed))
            _audioSessions.Remove(peerId);
        if (_audioNegotiators.TryGetValue(peerId, out var negotiator))
            await negotiator.RetireAsync(failed);
        else
            await failed.DisposeAsync();
        RenderAudioDiagnostics();
    }

    public async Task StopAudioAsync()
    {
        _audioDiagnosticsTimer.Stop();
        foreach (var session in _audioSessions.Values.ToList()) await session.DisposeAsync();
        _audioSessions.Clear();
        foreach (var negotiator in _audioNegotiators.Values.ToList()) await negotiator.DisposeAsync();
        _audioNegotiators.Clear();
    }

    void OnTestSpeakerClick(object sender, RoutedEventArgs e)
    {
        if (HandsFreeRecipientCombo.SelectedItem is PeerChoice choice
            && _audioSessions.TryGetValue(choice.PeerId, out var session))
            session.PlayTestTone();
    }

    void RenderAudioDiagnostics()
    {
        if (HandsFreeRecipientCombo.SelectedItem is not PeerChoice choice
            || !_audioSessions.TryGetValue(choice.PeerId, out var session))
        {
            TestSpeakerButton.IsEnabled = false;
            return;
        }

        var d = session.Diagnostics;
        TestSpeakerButton.IsEnabled = session.State.State is AudioSessionState.Running or AudioSessionState.Degraded;
        var levelPercent = Math.Clamp(d.CapturePeak * 100 / short.MaxValue, 0, 100);
        AudioDiagnosticsText.Text =
            $"Mic: {d.InputDeviceName}\nSpeaker: {d.OutputDeviceName}\n" +
            $"Mic level: {levelPercent}% · captured: {d.CapturedSamples} samples · sent: {d.SentPackets}\n" +
            $"received: {d.ReceivedPackets} · played: {d.PlayedFrames} · concealed: {d.ConcealedFrames}";
    }

    void UpdateMessagingEnabled()
    {
        if (_peerHost is null) return;
        ChatSendButton.IsEnabled = _selectedChatPeerId is Guid chatPeer && _peerHost.ConnectedPeerIds.Contains(chatPeer);
        SendAttentionCardButton.IsEnabled = _selectedAttentionPeerId is Guid cardPeer && _peerHost.ConnectedPeerIds.Contains(cardPeer);
        HandsFreeButton.IsEnabled = false;
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
                _ = _chatSpeechService?.SpeakAsync(message.Text);
            }
        });
    }

    void ShowChatChime()
    {
        ChatChimeNotice.Visibility = Visibility.Visible;
        _chatChimeHideTimer.Stop();
        _chatChimeHideTimer.Start();
    }

    void OnChatSendClick(object sender, RoutedEventArgs e) => _ = SendChatTextAsync(ChatInputBox.Text);

    void OnChatInputKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) _ = SendChatTextAsync(ChatInputBox.Text);
    }

    async Task SendChatTextAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var selectedOnline = _selectedFamilyPeerIds
            .Where(id => _peerHost?.ConnectedPeerIds.Contains(id) == true && _chatServices.ContainsKey(id))
            .ToList();
        if (selectedOnline.Count == 0) return;

        ChatInputBox.Text = "";
        if (selectedOnline.Count > 1)
        {
            foreach (var selectedPeerId in selectedOnline)
            {
                try { await _chatServices[selectedPeerId].SendAsync(text, CancellationToken.None); }
                catch { }
            }
            return;
        }

        var peerId = selectedOnline[0];
        var chatService = _chatServices[peerId];
        try
        {
            var contact = FindContactForPeer(peerId);
            if (contact is null || _presenceReceiver is null || _manualOverrideStore is null)
            {
                await chatService.SendAsync(text, CancellationToken.None);
            }
            else
            {
                var endpoints = contact.MemberPeerIds
                    .Where(_chatServices.ContainsKey)
                    .ToDictionary(id => id, id => new ChatDeviceEndpoint { DeviceId = id, Service = _chatServices[id] });
                var router = new ChatFanoutRouter(
                    endpoints,
                    () => _manualOverrideStore.Get(contact.ContactId),
                    () => _manualOverrideStore.Clear(contact.ContactId));
                await router.SendAsync(
                    _presenceReceiver.LiveDevices(contact.MemberPeerIds, DateTimeOffset.UtcNow),
                    text,
                    CancellationToken.None);
            }
        }
        catch
        {
            // Already reflected as Undelivered in the conversation by
            // ChatService itself (ADR-0001: no auto-resend) — nothing
            // further to do here beyond not crashing the UI thread.
        }
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

        var content = new StackPanel { Spacing = 2 };
        content.Children.Add(new TextBlock
        {
            Text = message.Text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = textColor,
            FontSize = 12,
        });
        content.Children.Add(new TextBlock
        {
            Text = DescribeDeliveryState(message),
            FontSize = 9,
            Opacity = 0.75,
            Foreground = textColor,
        });

        if (message.DeliveryState == ChatDeliveryState.Undelivered)
        {
            // ADR-0001: no auto-resend — this button sends the same text as
            // a brand new message with a brand new MessageId, exactly what
            // "the sender retries manually" means; it never tries to
            // resurrect the original MessageId.
            var resendButton = new Button { Content = "Resend", FontSize = 9, Background = Mustard, Foreground = Ink };
            resendButton.Click += (_, _) => _ = SendChatTextAsync(message.Text);
            content.Children.Add(resendButton);
        }

        return new Border
        {
            Child = content,
            Background = isMine ? Teal : Paper,
            BorderBrush = Ink,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 6, 10, 6),
            MaxWidth = 280,
            HorizontalAlignment = isMine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        };
    }

    static string DescribeDeliveryState(ChatMessage message)
    {
        if (message.Direction == ChatMessageDirection.Received) return "received";
        return message.DeliveryState switch
        {
            ChatDeliveryState.Pending => "sending...",
            ChatDeliveryState.Delivered => "delivered",
            ChatDeliveryState.Undelivered => "undelivered",
            _ => "",
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
        RenderComposerPresets();
        RenderEmojiPicker();
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
            if (_attentionCardToastPresenter is null)
            {
                DiagnosticLog.Current.Warning("attention-card.toast-failed", $"card={card.MessageId} reason=presenter-unavailable");
                ShowIncomingAttentionFallback(card, from);
                return;
            }

            try
            {
                await _attentionCardToastPresenter.ShowAsync(card, fromLabel: from);
                DiagnosticLog.Current.Info("attention-card.toast-shown", $"card={card.MessageId} from={peerId}");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Current.Error("attention-card.toast-failed", $"card={card.MessageId} from={peerId}", ex);
                ShowIncomingAttentionFallback(card, from);
            }
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

    void RenderComposerPresets()
    {
        var items = AttentionCardPresets.Presets.Select(p => p.Purpose).ToList();
        items.Add(CustomComposerPresetLabel);
        ComposerPresetCombo.ItemsSource = items;
        ComposerPresetCombo.SelectedIndex = 0;
    }

    void OnComposerPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        var isCustom = ComposerPresetCombo.SelectedItem as string == CustomComposerPresetLabel;
        ComposerCustomTextBox.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
        if (!isCustom && ComposerPresetCombo.SelectedIndex >= 0 && ComposerPresetCombo.SelectedIndex < AttentionCardPresets.Presets.Count)
        {
            SetSelectedComposerIcon(AttentionCardPresets.Presets[ComposerPresetCombo.SelectedIndex].Icon);
        }
    }

    /// <summary>The icon picker — issue #25's explicit "32x32 touch targets"
    /// sizing requirement, one button per <see cref="AttentionCardPresets.IconChoices"/>
    /// entry (intercom-shell-prototype.html's #emoji-grid). Available for
    /// BOTH a preset (as an override) and a Custom card (as the only way to
    /// pick an icon at all).</summary>
    void RenderEmojiPicker()
    {
        EmojiPickerPanel.Children.Clear();
        foreach (var icon in AttentionCardPresets.IconChoices)
        {
            var isSelected = icon == _selectedComposerIcon;
            var button = new Button
            {
                Content = icon,
                Width = 32,
                Height = 32,
                Padding = new Thickness(0),
                FontSize = 16,
                Background = isSelected ? Mustard : Paper,
                Foreground = Ink,
                BorderBrush = Ink,
                BorderThickness = new Thickness(2),
            };
            button.Click += (_, _) => SetSelectedComposerIcon(icon);
            EmojiPickerPanel.Children.Add(button);
        }
    }

    void SetSelectedComposerIcon(string icon)
    {
        _selectedComposerIcon = icon;
        RenderEmojiPicker();
    }

    void OnSendAttentionCardClick(object sender, RoutedEventArgs e) => _ = SendAttentionCardAsync();

    async Task SendAttentionCardAsync()
    {
        var isCustom = ComposerPresetCombo.SelectedItem as string == CustomComposerPresetLabel;
        var purpose = isCustom ? ComposerCustomTextBox.Text.Trim() : ComposerPresetCombo.SelectedItem as string ?? "";
        if (string.IsNullOrWhiteSpace(purpose)) return;

        var selectedOnline = _selectedFamilyPeerIds
            .Where(id => _peerHost?.ConnectedPeerIds.Contains(id) == true && _attentionCardServices.ContainsKey(id))
            .ToList();
        if (selectedOnline.Count == 0) return;
        if (selectedOnline.Count > 1)
        {
            foreach (var selectedPeerId in selectedOnline)
            {
                try { await _attentionCardServices[selectedPeerId].SendAsync(purpose, _selectedComposerIcon, CancellationToken.None); }
                catch { }
            }
            if (isCustom) ComposerCustomTextBox.Text = "";
            return;
        }

        var peerId = selectedOnline[0];
        var cardService = _attentionCardServices[peerId];

        try
        {
            var contact = FindContactForPeer(peerId);
            if (contact is null || _presenceReceiver is null || _manualOverrideStore is null)
            {
                await cardService.SendAsync(purpose, _selectedComposerIcon, CancellationToken.None);
            }
            else
            {
                var router = GetAttentionCardRouter(contact);
                await router.SendAsync(
                    _presenceReceiver.LiveDevices(contact.MemberPeerIds, DateTimeOffset.UtcNow),
                    purpose,
                    _selectedComposerIcon,
                    CancellationToken.None);
            }
        }
        catch
        {
            // Already reflected as Undelivered in the conversation by
            // AttentionCardService itself (ADR-0001: no auto-resend) —
            // nothing further to do here beyond not crashing the UI thread.
        }

        if (isCustom) ComposerCustomTextBox.Text = "";
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
}
