using System.Runtime.InteropServices;
using Intercom.Lifecycle;

namespace Intercom.App.Tray;

/// <summary>
/// Subclasses the main window's HWND to receive the tray icon's callback
/// message (WM_TRAYICON) and a simple right-click context menu. WinUI 3 has no
/// first-party hook for this, so this talks to Win32 directly.
/// </summary>
public sealed class TrayMessagePump : ITrayMessagePump
{
    delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);

    const int GWLP_WNDPROC = -4;
    const uint WM_LBUTTONUP = 0x0202;
    const uint WM_RBUTTONUP = 0x0205;
    const uint WM_CONTEXTMENU = 0x007B;
    const uint NIN_SELECT = 0x0400;
    const uint NIN_KEYSELECT = 0x0401;
    const uint WM_NULL = 0x0000;

    const uint TPM_LEFTALIGN = 0x0000;
    const uint TPM_RIGHTALIGN = 0x0008;
    const uint TPM_BOTTOMALIGN = 0x0020;
    const uint TPM_TOPALIGN = 0x0000;
    const uint TPM_RIGHTBUTTON = 0x0002;
    const uint TPM_RETURNCMD = 0x0100;
    const uint MF_STRING = 0x0000;

    const uint ABM_GETTASKBARPOS = 0x00000005;
    const int ABE_LEFT = 0;
    const int ABE_TOP = 1;
    const int ABE_RIGHT = 2;
    const int ABE_BOTTOM = 3;

    const int MenuIdOpen = 1;
    const int MenuIdQuit = 2;

    readonly nint _hwnd;
    readonly nint _originalWndProc;
    readonly WndProcDelegate _wndProcDelegate; // rooted so the GC never collects it

    public event Action? OpenRequested;
    public event Action? QuitRequested;
    public event Action? FocusReturnRequested;

    public TrayMessagePump(nint hwnd)
    {
        _hwnd = hwnd;
        _wndProcDelegate = WndProc;
        var newProcPtr = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
        _originalWndProc = SetWindowLongPtr(hwnd, GWLP_WNDPROC, newProcPtr);
    }

    nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == TrayInterop.CallbackMessage)
        {
            var eventMsg = (uint)(lParam.ToInt64() & 0xFFFF);
            if (eventMsg is WM_LBUTTONUP or NIN_SELECT or NIN_KEYSELECT)
            {
                OpenRequested?.Invoke();
            }
            else if (eventMsg is WM_RBUTTONUP or WM_CONTEXTMENU)
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

        var align = GetPopupAlignmentForTaskbarEdge();
        var selected = TrackPopupMenuEx(menu, align | TPM_RIGHTBUTTON | TPM_RETURNCMD, pt.x, pt.y, _hwnd, 0);
        DestroyMenu(menu);

        // Per the Shell_NotifyIcon guidance in docs/research/windows-resident-app.md:
        // send a benign message so the menu closes properly on some Windows versions.
        PostMessage(_hwnd, WM_NULL, 0, 0);
        FocusReturnRequested?.Invoke();

        if (selected == MenuIdOpen) OpenRequested?.Invoke();
        else if (selected == MenuIdQuit) QuitRequested?.Invoke();
    }

    // TrackPopupMenuEx defaults to TPM_LEFTALIGN | TPM_TOPALIGN, which grows the
    // menu down and right from the cursor. That's fine near the top of the
    // screen, but the tray icon sits inside the taskbar, so with the (by far
    // most common) bottom taskbar the menu would grow off the bottom edge
    // instead of opening upward like every native tray menu does. Ask the
    // shell which edge the taskbar occupies and align away from it.
    static uint GetPopupAlignmentForTaskbarEdge()
    {
        var data = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };
        if (SHAppBarMessage(ABM_GETTASKBARPOS, ref data) == 0)
        {
            return TPM_LEFTALIGN | TPM_BOTTOMALIGN; // fall back to the common case
        }

        return data.uEdge switch
        {
            ABE_BOTTOM => TPM_LEFTALIGN | TPM_BOTTOMALIGN,
            ABE_TOP => TPM_LEFTALIGN | TPM_TOPALIGN,
            ABE_LEFT => TPM_LEFTALIGN | TPM_TOPALIGN,
            ABE_RIGHT => TPM_RIGHTALIGN | TPM_TOPALIGN,
            _ => TPM_LEFTALIGN | TPM_BOTTOMALIGN,
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct APPBARDATA
    {
        public uint cbSize;
        public nint hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public nint lParam;
    }

    [DllImport("shell32.dll")]
    static extern nint SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

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
