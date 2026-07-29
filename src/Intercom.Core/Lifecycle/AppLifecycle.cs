using Intercom.Diagnostics;
using Intercom.Identity;

namespace Intercom.Lifecycle;

/// <summary>
/// Owns the resident-app launch/quit sequence: crash-marker check, identity
/// load, window/tray construction and wiring, launch-at-sign-in enabling, and
/// teardown ordering — the concerns that used to be scattered across
/// App.OnLaunched/OnQuitRequested. Depends only on small seams (IResidentWindow,
/// ITrayIcon, ITrayMessagePump, IStartupService) so the orchestration itself is
/// testable without a live window, tray, or OS startup-task registration.
/// </summary>
public sealed class AppLifecycle
{
    readonly CrashMarker _crashMarker;
    readonly IdentityStore _identityStore;
    readonly IStartupService _startupService;
    readonly Func<IResidentWindow> _createWindow;
    readonly Func<nint, ITrayMessagePump> _createTrayPump;
    readonly Func<nint, ITrayIcon> _createTrayIcon;

    IResidentWindow? _window;
    ITrayMessagePump? _trayPump;
    ITrayIcon? _trayIcon;
    bool _crashNoticePending;
    bool _started;

    /// <summary>Raised when the tray or window asks to quit — the owner
    /// (App) is responsible for actually ending the process (Exit()) once
    /// Quit() has torn things down.</summary>
    public event Action? QuitRequested;

    /// <summary>This device's local identity, loaded by <see cref="Start"/>.
    /// Exposed so callers that need PeerId (e.g. wiring up discovery) don't
    /// have to keep a second IdentityStore around — there is exactly one
    /// identity per app instance, and AppLifecycle already owns loading
    /// it.</summary>
    public LocalIdentity Identity => _identityStore.Identity;

    /// <summary>The full identity store (identity, approved peers, pending
    /// pairings) — issue #22's pairing UI needs the whole store, not just
    /// the local identity, to drive the Postcard Badges/Rolodex flow against
    /// real data.</summary>
    public IdentityStore IdentityStore => _identityStore;

    public AppLifecycle(
        CrashMarker crashMarker,
        IdentityStore identityStore,
        IStartupService startupService,
        Func<IResidentWindow> createWindow,
        Func<nint, ITrayMessagePump> createTrayPump,
        Func<nint, ITrayIcon> createTrayIcon)
    {
        _crashMarker = crashMarker;
        _identityStore = identityStore;
        _startupService = startupService;
        _createWindow = createWindow;
        _createTrayPump = createTrayPump;
        _createTrayIcon = createTrayIcon;
    }

    /// <summary>Call once, from OnLaunched. Loads identity, builds the window
    /// and tray, wires them together, and shows the window unless launched
    /// silently via the OS startup task. Also kicks off enabling
    /// launch-at-sign-in in the background (ADR-0003) — failures there (e.g.
    /// running unpackaged) are swallowed, per IStartupService's contract.</summary>
    public LaunchOutcome Start(bool launchedViaStartupTask)
    {
        if (_started) throw new InvalidOperationException("AppLifecycle.Start must only be called once.");
        _started = true;

        _crashNoticePending = _crashMarker.ClosedUnexpectedlyLastTime();
        _identityStore.LoadOrCreate();

        _window = _createWindow();
        _window.QuitRequested += RequestQuit;

        _trayPump = _createTrayPump(_window.Hwnd);
        _trayPump.OpenRequested += ShowWindow;
        _trayPump.QuitRequested += RequestQuit;
        _trayPump.FocusReturnRequested += () => _trayIcon?.SetFocus();

        _trayIcon = _createTrayIcon(_window.Hwnd);
        try
        {
            _trayIcon.Add("Intercom");
        }
        catch (Exception ex)
        {
            // The notification-area icon is valuable resident-app UI, but a
            // shell refusal must not prevent the main window, discovery, and
            // communications from starting. Keep the failure observable.
            DiagnosticLog.Current.Error("tray.unavailable", "Continuing without a notification-area icon.", ex);
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        if (!launchedViaStartupTask) ShowWindow();

        _ = TryEnableStartupAsync();

        return new LaunchOutcome
        {
            ClosedUnexpectedlyLastTime = _crashNoticePending,
            IdentityWasRegenerated = _identityStore.IdentityWasRegenerated,
            RegistryWasReset = _identityStore.RegistryWasReset,
            PendingPairingsWereReset = _identityStore.PendingPairingsWereReset,
        };
    }

    /// <summary>Brings the window to the foreground — from a tray action or a
    /// redirected second-launch activation. Shows the pending crash notice
    /// exactly once, the first time the window is shown after Start.</summary>
    public void ShowWindow()
    {
        if (_window is null) return;

        _window.ShowFromTray();
        if (_crashNoticePending)
        {
            _window.ShowCrashNotice();
            _crashNoticePending = false;
        }
    }

    /// <summary>Orderly shutdown: marks the clean-exit marker before tearing
    /// down the tray, so a failure mid-teardown still gets recorded as
    /// unclean rather than silently swallowed.</summary>
    public void Quit()
    {
        _crashMarker.MarkCleanShutdown();
        _trayIcon?.Dispose();
        _trayPump?.Dispose();
    }

    void RequestQuit() => QuitRequested?.Invoke();

    async Task TryEnableStartupAsync()
    {
        try
        {
            await _startupService.EnableOnFirstRunAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"StartupTask unavailable: {ex.Message}");
        }
    }
}
