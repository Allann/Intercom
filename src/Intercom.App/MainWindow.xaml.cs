using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using WinRT.Interop;
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
}
