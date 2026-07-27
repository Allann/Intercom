using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Intercom.App.Diagnostics;
using Intercom.App.Identity;
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
    readonly IdentityStore _identityStore = new();
    bool _crashNoticePending;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _crashNoticePending = _crashMarker.ClosedUnexpectedlyLastTime();

        _identityStore.LoadOrCreate();
        if (_identityStore.IdentityWasRegenerated)
        {
            // TODO(#22 pairing ceremony): surface this via the shell UI — every
            // previously approved peer is now unknown and needs re-pairing.
            System.Diagnostics.Debug.WriteLine("Local identity was unreadable and has been regenerated.");
        }
        else if (_identityStore.RegistryWasReset)
        {
            // TODO(#22 pairing ceremony): surface this too — the identity is
            // still valid, but the approved-peer list itself was unreadable
            // and every peer now needs re-pairing.
            System.Diagnostics.Debug.WriteLine("Approved-peer registry was unreadable and has been reset.");
        }

        _window = new MainWindow();
        _window.QuitRequested += OnQuitRequested;

        _trayPump = new TrayMessagePump(_window.Hwnd);
        _trayPump.OpenRequested += ShowWindow;
        _trayPump.QuitRequested += () => _window.Quit();
        _trayPump.FocusReturnRequested += () => _trayIcon?.SetFocus();

        _trayIcon = new TrayIcon(_window.Hwnd, TrayIconGuid);
        _trayIcon.Add("Intercom");

        Program.RedirectedActivationReceived += OnRedirectedActivation;

        if (Program.InitialActivationArguments.Kind != ExtendedActivationKind.StartupTask)
        {
            ShowWindow();
        }

        _ = TryEnableStartupAsync();
    }

    void OnRedirectedActivation(AppActivationArguments args)
    {
        _window?.DispatcherQueue.TryEnqueue(ShowWindow);
    }

    void ShowWindow()
    {
        if (_window is null) return;

        _window.Activate();
        _window.ShowFromTray();
        if (_crashNoticePending)
        {
            _window.ShowCrashNotice();
            _crashNoticePending = false;
        }
    }

    async Task TryEnableStartupAsync()
    {
        try
        {
            // ADR-0003: auto-enable at first run, no prompt. If the user or
            // policy disables startup later, StartupTask will preserve that state.
            await new StartupTaskService().EnableOnFirstRunAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"StartupTask unavailable: {ex.Message}");
        }
    }

    void OnQuitRequested()
    {
        Program.RedirectedActivationReceived -= OnRedirectedActivation;
        _crashMarker.MarkCleanShutdown();
        _trayIcon?.Dispose();
        _trayPump?.Dispose();
        Exit();
    }
}
