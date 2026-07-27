namespace Intercom.Lifecycle;

/// <summary>The notification-area icon, as far as AppLifecycle needs to know.</summary>
public interface ITrayIcon : IDisposable
{
    void Add(string tooltip);

    /// <summary>Returns keyboard focus to the tray icon after a context menu closes.</summary>
    void SetFocus();
}
