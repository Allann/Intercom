// PROTOTYPE for issue #16 (part 1 of 2) — "which narrow Windows input mechanism
// can safely detect both press and release for a configurable system-wide
// push-to-talk shortcut?"
//
// This is a REAL, runnable Windows console app — not a mockup. It registers an
// actual system-wide hotkey (Ctrl+Alt+Space by default) using RegisterHotKey,
// then detects release using a narrowly-scoped GetAsyncKeyState poll on just
// that one key — approach (a) from docs/research/windows-resident-app.md,
// the least invasive of the three candidates it lists (the others being raw
// input and a WH_KEYBOARD_LL hook thread).
//
// This needs an actual human to hold and release a real key to mean anything.
// Nobody was here to do that during this session, so this has NOT been
// validated against a live press/release yet — that's exactly what you should
// do first thing tomorrow. See README.md for what to check.
//
// Run: dotnet run
// Hold Ctrl+Alt+Space, release it, watch the timestamps. Type 'quit' + Enter to exit.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

const uint MOD_ALT = 0x0001;
const uint MOD_CONTROL = 0x0002;
const uint MOD_NOREPEAT = 0x4000; // suppress OS key-repeat re-firing WM_HOTKEY while held
const int VK_SPACE = 0x20;
const uint WM_HOTKEY = 0x0312;
const uint WM_QUIT = 0x0012;
const int HOTKEY_ID = 1;

uint messageThreadId = 0;
bool held = false;
var stopwatch = new Stopwatch();
var ready = new ManualResetEventSlim(false);
bool registrationFailed = false;

var msgThread = new Thread(() =>
{
    messageThreadId = GetCurrentThreadId();

    if (!RegisterHotKey(IntPtr.Zero, HOTKEY_ID, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_SPACE))
    {
        registrationFailed = true;
        Console.WriteLine($"! RegisterHotKey failed (Win32 error {Marshal.GetLastWin32Error()}). " +
                          "Another app likely already owns Ctrl+Alt+Space, or the combination is reserved. " +
                          "Edit VK_SPACE/MOD_* in Program.cs to try a different combination.");
        ready.Set();
        return;
    }

    ready.Set();

    while (GetMessage(out var msg, IntPtr.Zero, 0, 0))
    {
        if (msg.message == WM_HOTKEY && msg.wParam.ToInt32() == HOTKEY_ID)
        {
            OnHotkeyDown();
        }
    }

    UnregisterHotKey(IntPtr.Zero, HOTKEY_ID);
});
msgThread.IsBackground = true;
msgThread.Start();
ready.Wait();

if (registrationFailed)
{
    Environment.Exit(1);
}

Console.WriteLine("Push-to-talk input prototype (issue #16).");
Console.WriteLine("Hotkey registered: Ctrl+Alt+Space.");
Console.WriteLine("Hold it down (simulates starting to transmit), release it (simulates stopping).");
Console.WriteLine("Type 'quit' + Enter to exit.\n");

while (true)
{
    var line = Console.ReadLine();
    if (line is null) break;
    if (line.Trim().Equals("quit", StringComparison.OrdinalIgnoreCase))
    {
        if (messageThreadId != 0) PostThreadMessage(messageThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        break;
    }
}

void OnHotkeyDown()
{
    if (held) return; // defensive: MOD_NOREPEAT should already prevent this
    held = true;
    stopwatch.Restart();
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] PTT DOWN  (would start transmitting now)");

    // WM_HOTKEY only signals the initial press. Release detection is the whole
    // point of this prototype: poll just this one key's state on a short interval
    // until it's physically released. This is narrowly scoped to VK_SPACE — it
    // does not observe or suppress any other keystroke on the system.
    ThreadPool.QueueUserWorkItem(_ =>
    {
        while ((GetAsyncKeyState(VK_SPACE) & 0x8000) != 0)
        {
            Thread.Sleep(15);
        }
        held = false;
        var elapsed = stopwatch.ElapsedMilliseconds;
        stopwatch.Stop();
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] PTT UP    (would stop transmitting now) — held for {elapsed}ms");
    });
}

[DllImport("user32.dll", SetLastError = true)]
static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

[DllImport("user32.dll", SetLastError = true)]
static extern bool UnregisterHotKey(IntPtr hWnd, int id);

[DllImport("user32.dll")]
static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

[DllImport("user32.dll")]
static extern short GetAsyncKeyState(int vKey);

[DllImport("kernel32.dll")]
static extern uint GetCurrentThreadId();

[DllImport("user32.dll")]
static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

[StructLayout(LayoutKind.Sequential)]
struct POINT { public int x; public int y; }

[StructLayout(LayoutKind.Sequential)]
struct MSG
{
    public IntPtr hwnd;
    public uint message;
    public IntPtr wParam;
    public IntPtr lParam;
    public uint time;
    public POINT pt;
}
