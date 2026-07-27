using System.Runtime.InteropServices;

namespace Intercom.App.Presence;

/// <summary>
/// Registers for and handles session-lock/power/shutdown notifications
/// (WM_WTSSESSION_CHANGE, WM_POWERBROADCAST, WM_QUERYENDSESSION) on the main
/// window's HWND — the real Win32 signal source
/// <c>Intercom.Presence.PresenceEngine</c> is driven from.
///
/// Sibling to <c>Intercom.App.Tray.TrayMessagePump</c> rather than an
/// extension of it: the two classes own unrelated, non-overlapping message
/// sets (tray-icon callback vs. session/power/shutdown), and
/// TrayMessagePump's constructor/Dispose already has its own lifecycle
/// (add/remove the icon, register/unregister nothing WTS-related) that
/// earlier tickets' reviewers have already validated — folding session
/// notification registration into it would widen its responsibility for no
/// shared benefit. Both classes subclass the SAME hwnd's WndProc
/// independently; because each one's WndProc falls through to whatever the
/// PREVIOUS WndProc pointer was (via CallWindowProc) for any message it
/// doesn't recognize, the two subclasses chain safely regardless of
/// construction order — the standard Win32 subclass-chaining pattern — and
/// their message sets never overlap, so there is no ordering dependency to
/// get wrong.
///
/// <b>Thread context.</b> Windows delivers a message to a WndProc only via
/// the message loop belonging to the thread that created the window
/// (GetMessage/DispatchMessage on that thread) — WinUI 3 runs that loop on
/// the UI thread, the same thread TrayMessagePump's own WM_TRAYICON handling
/// already assumes (it invokes its events synchronously, with no dispatcher
/// marshaling). So <see cref="SessionLocked"/>/<see cref="Suspending"/>/etc.
/// all fire on the UI thread too, NOT an arbitrary thread pool thread —
/// verified against this repo's own established TrayMessagePump precedent,
/// not assumed. This still races the 30-second idle-poll timer (a
/// background <see cref="System.Threading.Timer"/> in PresenceEngine) and a
/// possible heartbeat/Tick timer driving PeerControlChannel, both of which
/// run on thread-pool threads — PresenceEngine's own <c>_gate</c> and
/// AvailabilityPolicy's internal lock are what keep those safe against this
/// class's UI-thread callbacks, not anything in this class itself.
///
/// GC lifetime: the WndProc delegate is rooted in
/// <see cref="_wndProcDelegate"/> for exactly as long as this object is
/// alive — the same pattern TrayMessagePump already established (its
/// lifetime matches its owning object's, unlike the free-running native
/// callbacks in Win32DnsServiceDiscovery that need a separate GCHandle
/// because nothing else keeps THOSE objects alive).
///
/// Untestable in this sandbox for the same reason Win32DnsServiceDiscovery
/// and SslPeerConnection are: it requires a real HWND pumping real Windows
/// messages and a real WTS session. The policy this drives
/// (Intercom.Presence.AvailabilityPolicy, PresenceEngine) is fully unit
/// tested without it.
/// </summary>
public sealed class SessionMessagePump : IDisposable
{
    delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);

    const int GWLP_WNDPROC = -4;

    const uint WM_WTSSESSION_CHANGE = 0x02B1;
    const uint WM_POWERBROADCAST = 0x0218;
    const uint WM_QUERYENDSESSION = 0x0011;

    const int WTS_CONSOLE_CONNECT = 0x1;
    const int WTS_CONSOLE_DISCONNECT = 0x2;
    const int WTS_REMOTE_CONNECT = 0x3;
    const int WTS_REMOTE_DISCONNECT = 0x4;
    const int WTS_SESSION_LOGON = 0x5;
    const int WTS_SESSION_LOGOFF = 0x6;
    const int WTS_SESSION_LOCK = 0x7;
    const int WTS_SESSION_UNLOCK = 0x8;
    const int WTS_SESSION_DESKTOP_READY = 0xF;

    const int PBT_APMSUSPEND = 0x4;
    const int PBT_APMRESUMESUSPEND = 0x7;
    const int PBT_APMRESUMEAUTOMATIC = 0x12;

    const uint NOTIFY_FOR_THIS_SESSION = 0;

    readonly nint _hwnd;
    readonly nint _originalWndProc;
    readonly WndProcDelegate _wndProcDelegate; // rooted so the GC never collects it
    bool _disposed;

    /// <summary>WTS_SESSION_LOCK, WTS_CONSOLE_DISCONNECT,
    /// WTS_REMOTE_DISCONNECT, or WTS_SESSION_LOGOFF — every signal that
    /// makes the session immediately unusable for presence
    /// purposes.</summary>
    public event Action? SessionBecameUnusable;

    /// <summary>WTS_SESSION_UNLOCK or WTS_SESSION_DESKTOP_READY — a signal
    /// that the session may be usable again; the caller re-evaluates from
    /// scratch rather than assuming so.</summary>
    public event Action? SessionBecameUsable;

    /// <summary>WTS_CONSOLE_CONNECT or WTS_REMOTE_CONNECT. Not currently
    /// wired to presence re-evaluation (docs/research/active-device-
    /// presence.md calls out unlock/desktop-ready specifically, not
    /// connect) — exposed for completeness/future use.</summary>
    public event Action? SessionConnected;

    /// <summary>WTS_SESSION_LOGON. Exposed for completeness/future use —
    /// see <see cref="SessionConnected"/>'s remarks.</summary>
    public event Action? SessionLoggedOn;

    /// <summary>PBT_APMSUSPEND.</summary>
    public event Action? Suspending;

    /// <summary>PBT_APMRESUMEAUTOMATIC — sent on every resume; not
    /// necessarily user-triggered.</summary>
    public event Action? ResumedAutomatic;

    /// <summary>PBT_APMRESUMESUSPEND — only sent (after
    /// PBT_APMRESUMEAUTOMATIC) when the resume was triggered by user
    /// input.</summary>
    public event Action? ResumedByUser;

    /// <summary>WM_QUERYENDSESSION. Raised synchronously, BEFORE this
    /// method returns TRUE to Windows — subscribers must do only fast,
    /// non-blocking work here (learn.microsoft.com/windows/win32/shutdown/
    /// wm-queryendsession: "respect the user's intentions and return
    /// TRUE"... "defer any cleanup operations until [WM_ENDSESSION]").</summary>
    public event Action? EndingSession;

    public SessionMessagePump(nint hwnd)
    {
        _hwnd = hwnd;
        _wndProcDelegate = WndProc;
        var newProcPtr = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
        _originalWndProc = SetWindowLongPtr(hwnd, GWLP_WNDPROC, newProcPtr);

        if (!WTSRegisterSessionNotification(hwnd, NOTIFY_FOR_THIS_SESSION))
        {
            // Restore the WndProc immediately — no point staying subclassed
            // if registration failed; every message would just fall through
            // to CallWindowProc anyway, but leaving an unnecessary subclass
            // installed invites confusion later.
            SetWindowLongPtr(hwnd, GWLP_WNDPROC, _originalWndProc);
            throw new InvalidOperationException("WTSRegisterSessionNotification failed.");
        }
    }

    nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_WTSSESSION_CHANGE)
        {
            HandleSessionChange((int)wParam);
            return 0;
        }
        if (msg == WM_POWERBROADCAST)
        {
            HandlePowerBroadcast(wParam);
            return 1; // TRUE: message processed
        }
        if (msg == WM_QUERYENDSESSION)
        {
            // Per Microsoft's guidance: respond immediately, defer cleanup
            // to WM_ENDSESSION — never block shutdown here. The event fires
            // synchronously before returning TRUE, so PresenceEngine can
            // flip to Unavailable in time, but subscribers must not do
            // anything slower than that (see EndingSession's doc).
            EndingSession?.Invoke();
            return 1; // TRUE
        }
        return CallWindowProc(_originalWndProc, hWnd, msg, wParam, lParam);
    }

    void HandleSessionChange(int eventCode)
    {
        switch (eventCode)
        {
            case WTS_SESSION_LOCK:
            case WTS_CONSOLE_DISCONNECT:
            case WTS_REMOTE_DISCONNECT:
            case WTS_SESSION_LOGOFF:
                SessionBecameUnusable?.Invoke();
                break;
            case WTS_SESSION_UNLOCK:
            case WTS_SESSION_DESKTOP_READY:
                SessionBecameUsable?.Invoke();
                break;
            case WTS_CONSOLE_CONNECT:
            case WTS_REMOTE_CONNECT:
                SessionConnected?.Invoke();
                break;
            case WTS_SESSION_LOGON:
                SessionLoggedOn?.Invoke();
                break;
        }
    }

    void HandlePowerBroadcast(nint wParam)
    {
        var value = (long)wParam;
        if (value == PBT_APMSUSPEND) Suspending?.Invoke();
        else if (value == PBT_APMRESUMEAUTOMATIC) ResumedAutomatic?.Invoke();
        else if (value == PBT_APMRESUMESUSPEND) ResumedByUser?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        WTSUnRegisterSessionNotification(_hwnd);
        SetWindowLongPtr(_hwnd, GWLP_WNDPROC, _originalWndProc);
    }

    static nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
            : SetWindowLong32(hWnd, nIndex, dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    static extern nint SetWindowLongPtr64(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    static extern nint SetWindowLong32(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll")]
    static extern nint CallWindowProc(nint lpPrevWndFunc, nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    static extern bool WTSRegisterSessionNotification(nint hWnd, uint dwFlags);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    static extern bool WTSUnRegisterSessionNotification(nint hWnd);
}
