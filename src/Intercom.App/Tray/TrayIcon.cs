using System.Runtime.InteropServices;
using Intercom.Lifecycle;

namespace Intercom.App.Tray;

/// <summary>
/// Wraps Shell_NotifyIcon. WinUI 3 has no first-party tray API (windows-resident-app
/// research doc), so this talks to the Win32 shell notification API directly against
/// the main window's HWND.
/// </summary>
public sealed class TrayIcon : ITrayIcon
{
    const uint NIM_ADD = 0x00000000;
    const uint NIM_MODIFY = 0x00000001;
    const uint NIM_DELETE = 0x00000002;
    const uint NIM_SETVERSION = 0x00000004;
    const uint NIM_SETFOCUS = 0x00000003;
    const uint NIF_MESSAGE = 0x00000001;
    const uint NIF_ICON = 0x00000002;
    const uint NIF_TIP = 0x00000004;
    const uint NIF_GUID = 0x00000020;
    const uint NOTIFYICON_VERSION_4 = 4;

    readonly IntPtr _hwnd;
    readonly Guid _iconGuid;
    bool _added;

    public TrayIcon(IntPtr hwnd, Guid stableIconGuid)
    {
        _hwnd = hwnd;
        _iconGuid = stableIconGuid;
    }

    public void Add(string tooltip)
    {
        var data = MakeData(tooltip);
        _added = Shell_NotifyIconW(NIM_ADD, ref data);
        if (!_added)
        {
            throw new InvalidOperationException("Windows could not add the Intercom notification-area icon.");
        }

        data.uVersionOrTimeout = NOTIFYICON_VERSION_4;
        if (!Shell_NotifyIconW(NIM_SETVERSION, ref data))
        {
            Remove();
            throw new InvalidOperationException("Windows could not configure the Intercom notification-area icon.");
        }
    }

    public void SetFocus()
    {
        if (!_added) return;
        var data = MakeData(string.Empty);
        Shell_NotifyIconW(NIM_SETFOCUS, ref data);
    }

    public void UpdateTooltip(string tooltip)
    {
        if (!_added) return;
        var data = MakeData(tooltip);
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    public void Remove()
    {
        if (!_added) return;
        var data = MakeData(string.Empty);
        Shell_NotifyIconW(NIM_DELETE, ref data);
        _added = false;
    }

    NOTIFYICONDATAW MakeData(string tooltip)
    {
        return new NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_GUID,
            uCallbackMessage = TrayInterop.CallbackMessage,
            hIcon = LoadIconForApp(),
            szTip = tooltip,
            guidItem = _iconGuid,
        };
    }

    static IntPtr LoadIconForApp()
    {
        // Placeholder: load the app's own executable icon. A dedicated .ico asset
        // (comic-intercom mid-century style, per issue #8) replaces this later.
        return LoadIconW(IntPtr.Zero, new IntPtr(32512)); // IDI_APPLICATION
    }

    public void Dispose() => Remove();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uVersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll")]
    static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);
}
