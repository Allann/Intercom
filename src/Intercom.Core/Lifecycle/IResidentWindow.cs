namespace Intercom.Lifecycle;

/// <summary>
/// The main window, as far as AppLifecycle needs to know: it can be raised
/// from the tray, told about a pending crash notice, and asked to quit — it
/// owns the actual close-to-tray-vs-quit behaviour itself.
/// </summary>
public interface IResidentWindow
{
    event Action? QuitRequested;

    nint Hwnd { get; }

    /// <summary>Activates and brings the window to the foreground.</summary>
    void ShowFromTray();

    void ShowCrashNotice();

    void Quit();
}
