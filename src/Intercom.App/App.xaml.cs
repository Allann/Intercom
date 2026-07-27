using Microsoft.UI.Xaml;
using Intercom.App.Diagnostics;
using Intercom.App.Startup;
using Intercom.App.Tray;

namespace Intercom.App;

public partial class App : Application
{
    static readonly Guid TrayIconGuid = new("6a1f2e2e-6b7a-4a6a-9a1e-8f1c1f6a2b1a");

    MainWindow? _window;
    TrayIcon? _trayIcon;
    TrayMessagePump? _trayPump;
    readonly CrashMarker _crashMarker = new();

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var closedUnexpectedly = _crashMarker.ClosedUnexpectedlyLastTime();
        if (closedUnexpectedly)
        {
            // TODO(#18 follow-up): surface this via the shell UI once it exists,
            // rather than just a debug trace. No watchdog/auto-restart (ADR-0003) —
            // this is visibility only.
            System.Diagnostics.Debug.WriteLine("Intercom closed unexpectedly last time.");
        }

        _window = new MainWindow();
        _window.QuitRequested += OnQuitRequested;

        _trayPump = new TrayMessagePump(_window.Hwnd);
        _trayPump.OpenRequested += () => _window.ShowFromTray();
        _trayPump.QuitRequested += () => _window.Quit();

        _trayIcon = new TrayIcon(_window.Hwnd, TrayIconGuid);
        _trayIcon.Add("Intercom");

        _window.Activate();

        _ = TryEnableStartupAsync();
    }

    async Task TryEnableStartupAsync()
    {
        try
        {
            // ADR-0003: auto-enable at first run, no prompt. Requires package
            // identity (MSIX) to function — expected to throw until the
            // packaging pass lands; caught here rather than crashing the app.
            await new StartupTaskService().EnableOnFirstRunAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"StartupTask unavailable (expected until MSIX packaging): {ex.Message}");
        }
    }

    void OnQuitRequested()
    {
        _crashMarker.MarkCleanShutdown();
        _trayIcon?.Dispose();
        _trayPump?.Dispose();
        Exit();
    }
}
