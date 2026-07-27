namespace Intercom.Lifecycle;

/// <summary>Raises the tray icon's user-interaction events, as far as AppLifecycle needs to know.</summary>
public interface ITrayMessagePump : IDisposable
{
    event Action? OpenRequested;
    event Action? QuitRequested;
    event Action? FocusReturnRequested;
}
