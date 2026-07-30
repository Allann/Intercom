using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Intercom.App.Input;

/// <summary>Owns Intercom's single global hold-to-talk binding on a dedicated
/// Win32 message thread. Only the configured main key is polled, and only
/// between the registered press and its release.</summary>
public sealed class GlobalPushToTalkHotkey : IDisposable
{
    const int HotkeyId = 1;
    const uint ModAlt = 0x0001;
    const uint ModControl = 0x0002;
    const uint ModNoRepeat = 0x4000;
    const uint WmHotkey = 0x0312;
    const uint WmQuit = 0x0012;
    const int VkSpace = 0x20;

    readonly CancellationTokenSource _releaseCancellation = new();
    readonly ManualResetEventSlim _registered = new();
    readonly Thread _thread;
    uint _threadId;
    bool _disposed;

    public event Action? Pressed;
    public event Action? Released;
    public string? RegistrationError { get; private set; }
    public bool IsRegistered => RegistrationError is null && _registered.IsSet;

    public GlobalPushToTalkHotkey()
    {
        _thread = new Thread(MessageLoop) { IsBackground = true, Name = "Intercom push-to-talk hotkey" };
        _thread.Start();
        _registered.Wait();
    }

    void MessageLoop()
    {
        _threadId = GetCurrentThreadId();
        if (!RegisterHotKey(IntPtr.Zero, HotkeyId, ModControl | ModAlt | ModNoRepeat, VkSpace))
        {
            RegistrationError = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            _registered.Set();
            return;
        }

        _registered.Set();
        try
        {
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                if (message.Message != WmHotkey || message.WParam.ToInt32() != HotkeyId) continue;
                Pressed?.Invoke();
                while (!_releaseCancellation.IsCancellationRequested && (GetAsyncKeyState(VkSpace) & 0x8000) != 0)
                    Thread.Sleep(8);
                Released?.Invoke();
            }
        }
        finally
        {
            UnregisterHotKey(IntPtr.Zero, HotkeyId);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _releaseCancellation.Cancel();
        if (_threadId != 0) PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(1));
        _releaseCancellation.Dispose();
        _registered.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, int virtualKey);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("user32.dll")] static extern int GetMessage(out NativeMessage message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll", SetLastError = true)] static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    struct NativeMessage
    {
        public IntPtr HWnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public NativePoint Point;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)] struct NativePoint { public int X; public int Y; }
}
