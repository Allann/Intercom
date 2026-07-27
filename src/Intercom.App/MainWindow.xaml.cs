using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using WinRT.Interop;
using Intercom.Discovery;
using Intercom.Lifecycle;

namespace Intercom.App;

public sealed partial class MainWindow : Window, IResidentWindow
{
    public event Action? QuitRequested;

    public nint Hwnd { get; }
    public AppWindow AppWin { get; }

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
}
