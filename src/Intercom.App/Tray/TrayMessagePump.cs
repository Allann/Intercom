using System.Runtime.InteropServices;

namespace Intercom.App.Tray;

/// <summary>
/// Subclasses the main window's HWND to receive the tray icon's callback
/// message (WM_TRAYICON) and a simple right-click context menu. WinUI 3 has no
/// first-party hook for this, so this talks to Win32 directly.
/// </summary>
public sealed class TrayMessagePump : IDisposable
{
    delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);

    const int GWLP_WNDPROC = -4;
    const uint WM_TRAYICON = 0x8000 + 1;
    const uint WM_LBUTTONUP = 0x0202;
    const uint WM_RBUTTONUP = 0x0205;
    const uint WM_COMMAND = 0x0111;
    const uint WM_NULL = 0x0000;

    const uint TPM_RIGHTBUTTON = 0x0002;
    const uint TPM_RETURNCMD = 0x0100;
    const uint MF_STRING = 0x0000;

    const int MenuIdOpen = 1;
    const int MenuIdQuit = 2;

    readonly nint _hwnd;
    readonly nint _originalWndProc;
    readonly WndProcDelegate _wndProcDelegate; // rooted so the GC never collects it

    public event Action? OpenRequested;
    public event Action? QuitRequested;

    public TrayMessagePump(nint hwnd)
    {
        _hwnd = hwnd;
        _wndProcDelegate = WndProc;
        var newProcPtr = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
        _originalWndProc = SetWindowLongPtr(hwnd, GWLP_WNDPROC, newProcPtr);
    }

    nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_TRAYICON)
        {
            var eventMsg = (uint)(lParam.ToInt64() & 0xFFFF);
            if (eventMsg == WM_LBUTTONUP)
            {
                OpenRequested?.Invoke();
            }
            else if (eventMsg == WM_RBUTTONUP)
            {
                ShowContextMenu();
            }
            return 0;
        }
        return CallWindowProc(_originalWndProc, hWnd, msg, wParam, lParam);
    }

    void ShowContextMenu()
    {
        GetCursorPos(out var pt);

        var menu = CreatePopupMenu();
        AppendMenuW(menu, MF_STRING, MenuIdOpen, "Open Intercom");
        AppendMenuW(menu, MF_STRING, MenuIdQuit, "Quit");

        // Required so the menu dismisses correctly if the user clicks away.
        SetForegroundWindow(_hwnd);

        var selected = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD, pt.x, pt.y, _hwnd, 0);
        DestroyMenu(menu);

        // Per the Shell_NotifyIcon guidance in docs/research/windows-resident-app.md:
        // send a benign message so the menu closes properly on some Windows versions.
        PostMessage(_hwnd, WM_NULL, 0, 0);

        if (selected == MenuIdOpen) OpenRequested?.Invoke();
        else if (selected == MenuIdQuit) QuitRequested?.Invoke();
    }

    public void Dispose()
    {
        SetWindowLongPtr(_hwnd, GWLP_WNDPROC, _originalWndProc);
    }

    static nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
            : SetWindowLong32(hWnd, nIndex, dwNewLong);

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int x; public int y; }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    static extern nint SetWindowLongPtr64(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    static extern nint SetWindowLong32(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll")]
    static extern nint CallWindowProc(nint lpPrevWndFunc, nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool AppendMenuW(nint hMenu, uint uFlags, int uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    static extern bool DestroyMenu(nint hMenu);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    static extern int TrackPopupMenuEx(nint hMenu, uint uFlags, int x, int y, nint hWnd, nint lptpm);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);
}
