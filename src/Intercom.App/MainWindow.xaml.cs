using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Windows.UI;
using WinRT.Interop;
using Intercom.Chat;
using Intercom.Discovery;
using Intercom.Identity;
using Intercom.Lifecycle;
using Intercom.Presence;
using Intercom.App.Chat;
using Intercom.App.Pairing;

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

    public nint Hwnd { get; }
    public AppWindow AppWin { get; }

    readonly DispatcherQueue _dispatcherQueue;
    readonly DispatcherQueueTimer _chatChimeHideTimer;

    IdentityStore? _identityStore;
    DndSettingsStore? _dndSettings;

    ChatTtsSettingsStore? _chatTtsSettings;
    ChatSpeechService? _chatSpeechService;
    ChatService? _chatServiceLocal;
    ChatService? _chatServicePeer;

    // There is no live multi-peer connection roster in this app shell yet
    // (see MainWindow.xaml's chat drawer comment) — this Guid stands in for
    // "the approved peer this drawer is talking to" purely so the per-peer
    // spoken-chat toggle (issue #24 requirement 4) has a real key to persist
    // against for the lifetime of this demo session.
    readonly Guid _demoPeerId = Guid.NewGuid();

    // Guards re-entrant CheckBox.Checked/Unchecked firing while
    // RenderChatSpokenToggle programmatically sets IsChecked to reflect
    // loaded state, so that doesn't get misread as a user action and
    // re-persisted as a no-op toggle.
    bool _suppressSpokenChatToggleHandler;

    public MainWindow()
    {
        InitializeComponent();
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

        // ADR-0003: closing the main window hides it to the tray; only an
        // explicit tray "Quit" action actually ends the process.
        AppWin.Closing += OnAppWindowClosing;

        InitializeChatDrawer();
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
        NoDiscoveredPeersNotice.Visibility = peers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DiscoveredPeersList.ItemsSource = peers.Select(DescribePeer).ToList();
    }

    static string DescribePeer(VisiblePeer peer)
    {
        var endpoints = string.Join(", ", peer.Endpoints.Select(e => $"{e.Address}:{e.Port} ({e.InterfaceId})"));
        return $"{peer.PeerIdHint} — v{peer.ProtocolVersion} — {endpoints}";
    }

    /// <summary>Issue #22: gives this window access to the real
    /// <see cref="IdentityStore"/> so "+ Add Family Member" can open a
    /// pairing dialog backed by real data. Called once from App.OnLaunched,
    /// after AppLifecycle.Start has loaded the store — not passed through
    /// the constructor, since <see cref="Intercom.Lifecycle.AppLifecycle"/>'s
    /// window factory is a parameterless <c>Func&lt;IResidentWindow&gt;</c>.</summary>
    public void AttachIdentityStore(IdentityStore identityStore) => _identityStore = identityStore;

    async void OnAddFamilyMemberClick(object sender, RoutedEventArgs e)
    {
        if (_identityStore is null) return;

        var dialog = new PairingDialog(_identityStore) { XamlRoot = Content.XamlRoot };
        await dialog.ShowAsync();
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
        var (localTransport, peerTransport) = LoopbackChatTransport.CreatePair();
        _chatServiceLocal = new ChatService(localTransport);
        _chatServicePeer = new ChatService(peerTransport);

        _chatServiceLocal.MessageReceived += OnIncomingChatMessage;
        _chatServiceLocal.Conversation.MessageAdded += _ => _dispatcherQueue.TryEnqueue(RenderChatMessages);
        _chatServiceLocal.Conversation.MessageUpdated += _ => _dispatcherQueue.TryEnqueue(RenderChatMessages);

        RenderChatMessages();
    }

    void OnIncomingChatMessage(ChatMessage message)
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
            if (_chatTtsSettings?.IsEnabled(_demoPeerId) == true)
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
        if (_chatServiceLocal is null || string.IsNullOrWhiteSpace(text)) return;

        ChatInputBox.Text = "";
        try
        {
            await _chatServiceLocal.SendAsync(text, CancellationToken.None);
        }
        catch
        {
            // Already reflected as Undelivered in the conversation by
            // ChatService itself (ADR-0001: no auto-resend) — nothing
            // further to do here beyond not crashing the UI thread.
        }
    }

    void OnSimulatePeerReplyClick(object sender, RoutedEventArgs e)
    {
        if (_chatServicePeer is null) return;
        _ = _chatServicePeer.SendAsync("On my way 👍", CancellationToken.None);
    }

    void OnSimulateDropClick(object sender, RoutedEventArgs e)
    {
        // Demonstrates the "message in flight when the connection drops
        // shows undelivered, no silent auto-resend" acceptance criterion —
        // a loopback pair has no real socket to actually sever, so this is
        // the demo's explicit stand-in (see LoopbackChatTransport.SimulateDrop).
        _chatServiceLocal?.Conversation.MarkAllPendingUndelivered();
    }

    void OnSpokenChatToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressSpokenChatToggleHandler || _chatTtsSettings is null) return;
        _chatTtsSettings.SetEnabled(_demoPeerId, SpokenChatCheckBox.IsChecked == true);
    }

    void RenderChatSpokenToggle()
    {
        if (_chatTtsSettings is null) return;
        _suppressSpokenChatToggleHandler = true;
        SpokenChatCheckBox.IsChecked = _chatTtsSettings.IsEnabled(_demoPeerId);
        _suppressSpokenChatToggleHandler = false;
    }

    void RenderChatMessages()
    {
        if (_chatServiceLocal is null) return;

        ChatMessagesPanel.Children.Clear();
        foreach (var message in _chatServiceLocal.Conversation.Messages)
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
}
