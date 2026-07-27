using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Windows.UI;
using WinRT.Interop;
using Intercom.Discovery;
using Intercom.Identity;
using Intercom.Lifecycle;
using Intercom.Presence;
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

    public event Action? QuitRequested;

    public nint Hwnd { get; }
    public AppWindow AppWin { get; }

    IdentityStore? _identityStore;
    DndSettingsStore? _dndSettings;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Intercom";

        Hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(Hwnd);
        AppWin = AppWindow.GetFromWindowId(windowId);

        // ADR-0003: closing the main window hides it to the tray; only an
        // explicit tray "Quit" action actually ends the process.
        AppWin.Closing += OnAppWindowClosing;
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
}
