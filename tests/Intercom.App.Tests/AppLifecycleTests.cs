using Intercom.Diagnostics;
using Intercom.Identity;
using Intercom.Lifecycle;
using Xunit;

namespace Intercom.App.Tests;

public class AppLifecycleTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "IntercomTests_" + Guid.NewGuid());

    public AppLifecycleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    AppLifecycle MakeLifecycle(out FakeWindow window, out FakeTrayIcon trayIcon, out FakeTrayPump trayPump, out FakeStartupService startup)
    {
        var w = new FakeWindow();
        var icon = new FakeTrayIcon();
        var pump = new FakeTrayPump();
        var svc = new FakeStartupService();
        window = w;
        trayIcon = icon;
        trayPump = pump;
        startup = svc;

        return new AppLifecycle(
            new CrashMarker(_dir),
            new IdentityStore(_dir),
            svc,
            createWindow: () => w,
            createTrayPump: _ => pump,
            createTrayIcon: _ => icon);
    }

    [Fact]
    public void Start_NotLaunchedViaStartupTask_ShowsTheWindow()
    {
        var lifecycle = MakeLifecycle(out var window, out _, out _, out _);

        lifecycle.Start(launchedViaStartupTask: false);

        Assert.True(window.ShownFromTrayCount >= 1);
    }

    [Fact]
    public void Start_LaunchedViaStartupTask_DoesNotShowTheWindow()
    {
        var lifecycle = MakeLifecycle(out var window, out _, out _, out _);

        lifecycle.Start(launchedViaStartupTask: true);

        Assert.Equal(0, window.ShownFromTrayCount);
    }

    [Fact]
    public void Start_AddsTheTrayIcon()
    {
        var lifecycle = MakeLifecycle(out _, out var trayIcon, out _, out _);

        lifecycle.Start(launchedViaStartupTask: true);

        Assert.True(trayIcon.Added);
    }

    [Fact]
    public void Start_CalledTwice_Throws()
    {
        var lifecycle = MakeLifecycle(out _, out _, out _, out _);
        lifecycle.Start(launchedViaStartupTask: true);

        Assert.Throws<InvalidOperationException>(() => lifecycle.Start(launchedViaStartupTask: true));
    }

    [Fact]
    public void TrayOpenRequested_ShowsTheWindow()
    {
        var lifecycle = MakeLifecycle(out var window, out _, out var trayPump, out _);
        lifecycle.Start(launchedViaStartupTask: true);
        Assert.Equal(0, window.ShownFromTrayCount);

        trayPump.RaiseOpenRequested();

        Assert.Equal(1, window.ShownFromTrayCount);
    }

    [Fact]
    public void TrayQuitRequested_BubblesUpAsLifecycleQuitRequested()
    {
        var lifecycle = MakeLifecycle(out _, out _, out var trayPump, out _);
        lifecycle.Start(launchedViaStartupTask: true);
        var quitRaised = false;
        lifecycle.QuitRequested += () => quitRaised = true;

        trayPump.RaiseQuitRequested();

        Assert.True(quitRaised);
    }

    [Fact]
    public void WindowQuitRequested_BubblesUpAsLifecycleQuitRequested()
    {
        var lifecycle = MakeLifecycle(out var window, out _, out _, out _);
        lifecycle.Start(launchedViaStartupTask: true);
        var quitRaised = false;
        lifecycle.QuitRequested += () => quitRaised = true;

        window.RaiseQuitRequested();

        Assert.True(quitRaised);
    }

    [Fact]
    public void TrayFocusReturnRequested_ReturnsFocusToTheTrayIcon()
    {
        var lifecycle = MakeLifecycle(out _, out var trayIcon, out var trayPump, out _);
        lifecycle.Start(launchedViaStartupTask: true);

        trayPump.RaiseFocusReturnRequested();

        Assert.True(trayIcon.FocusSet);
    }

    [Fact]
    public void ClosedUnexpectedlyLastTime_ShowsCrashNoticeOnFirstShow_ThenNotAgain()
    {
        new CrashMarker(_dir).ClosedUnexpectedlyLastTime(); // simulate a dirty marker left by a previous run

        var lifecycle = MakeLifecycle(out var window, out _, out var trayPump, out _);
        lifecycle.Start(launchedViaStartupTask: true);

        trayPump.RaiseOpenRequested();
        Assert.Equal(1, window.CrashNoticeCount);

        trayPump.RaiseOpenRequested();
        Assert.Equal(1, window.CrashNoticeCount); // not shown a second time
    }

    [Fact]
    public void CleanShutdown_NeverShowsACrashNotice()
    {
        var lifecycle = MakeLifecycle(out var window, out _, out var trayPump, out _);
        lifecycle.Start(launchedViaStartupTask: true);

        trayPump.RaiseOpenRequested();

        Assert.Equal(0, window.CrashNoticeCount);
    }

    [Fact]
    public void Start_ReportsIdentityAndCrashOutcomeTogether()
    {
        new CrashMarker(_dir).ClosedUnexpectedlyLastTime();
        var lifecycle = MakeLifecycle(out _, out _, out _, out _);

        var outcome = lifecycle.Start(launchedViaStartupTask: true);

        Assert.True(outcome.ClosedUnexpectedlyLastTime);
        Assert.False(outcome.IdentityWasRegenerated); // fresh directory: nothing to lose
    }

    [Fact]
    public void Quit_MarksCleanShutdown_AndDisposesTrayIconAndPump()
    {
        var lifecycle = MakeLifecycle(out _, out var trayIcon, out var trayPump, out _);
        lifecycle.Start(launchedViaStartupTask: true);

        lifecycle.Quit();

        Assert.True(trayIcon.Disposed);
        Assert.True(trayPump.Disposed);

        var marker = new CrashMarker(_dir);
        Assert.False(marker.ClosedUnexpectedlyLastTime()); // clean marker means "no leftover dirty marker"
    }

    [Fact]
    public void Start_EnablesStartupInTheBackground()
    {
        var lifecycle = MakeLifecycle(out _, out _, out _, out var startup);

        lifecycle.Start(launchedViaStartupTask: true);

        Assert.True(startup.EnableWasCalled);
    }

    sealed class FakeWindow : IResidentWindow
    {
        public event Action? QuitRequested;
        public nint Hwnd => 1;
        public int ShownFromTrayCount { get; private set; }
        public int CrashNoticeCount { get; private set; }

        public void ShowFromTray() => ShownFromTrayCount++;
        public void ShowCrashNotice() => CrashNoticeCount++;
        public void Quit() { }
        public void RaiseQuitRequested() => QuitRequested?.Invoke();
    }

    sealed class FakeTrayIcon : ITrayIcon
    {
        public bool Added { get; private set; }
        public bool FocusSet { get; private set; }
        public bool Disposed { get; private set; }

        public void Add(string tooltip) => Added = true;
        public void SetFocus() => FocusSet = true;
        public void Dispose() => Disposed = true;
    }

    sealed class FakeTrayPump : ITrayMessagePump
    {
        public event Action? OpenRequested;
        public event Action? QuitRequested;
        public event Action? FocusReturnRequested;
        public bool Disposed { get; private set; }

        public void RaiseOpenRequested() => OpenRequested?.Invoke();
        public void RaiseQuitRequested() => QuitRequested?.Invoke();
        public void RaiseFocusReturnRequested() => FocusReturnRequested?.Invoke();
        public void Dispose() => Disposed = true;
    }

    sealed class FakeStartupService : IStartupService
    {
        public bool EnableWasCalled { get; private set; }

        public Task<StartupPreference> EnableOnFirstRunAsync()
        {
            EnableWasCalled = true;
            return Task.FromResult(StartupPreference.Enabled);
        }
    }
}
