using System.Runtime.InteropServices;
using Intercom.Presence;

namespace Intercom.App.Presence;

/// <summary>
/// Real Windows implementation of <see cref="IIdleTimeProvider"/> via
/// GetLastInputInfo (winuser.h; no managed wrapper). Per Microsoft's own
/// documented caveat ("[the tick count] is not guaranteed to be
/// incremental... the value might be less than the tick count of a prior
/// event"), the raw <c>dwTime</c> value is never exposed or trusted
/// directly: idle age is computed via unsigned 32-bit subtraction against
/// the current tick count — the standard idiom for this exact API, since
/// both GetLastInputInfo's <c>dwTime</c> and <see cref="Environment.TickCount"/>
/// share the same 32-bit GetTickCount-derived tick space and wrap
/// identically together — and an implausibly large result (which would only
/// happen if <c>dwTime</c> were briefly AHEAD of "now" due to the documented
/// non-monotonicity, not genuine ~49.7-day wraparound) is clamped to zero
/// rather than reported as a multi-week idle age.
///
/// Untestable in this sandbox for the same reason
/// <c>Intercom.Discovery.Win32DnsServiceDiscovery</c> and
/// <c>Intercom.ControlChannel.SslPeerConnection</c> are: it reports however
/// long THIS machine has actually been idle, which a test cannot control or
/// assert against deterministically. <see cref="AvailabilityPolicy"/> (the
/// pure policy this feeds) is fully unit tested with a hand-written fake
/// <see cref="IIdleTimeProvider"/> instead.
/// </summary>
public sealed class Win32IdleTimeProvider : IIdleTimeProvider
{
    public TimeSpan GetIdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info))
        {
            // Documented to fail only in exceptional circumstances; treat
            // "just active" as the safe default rather than throwing out of
            // a presence poll.
            return TimeSpan.Zero;
        }

        var current = unchecked((uint)Environment.TickCount);
        var diffMs = unchecked(current - info.dwTime);

        // See the class remarks: a diff near uint.MaxValue means dwTime was
        // briefly ahead of "now", not genuine wraparound.
        if (diffMs > uint.MaxValue / 2) return TimeSpan.Zero;

        return TimeSpan.FromMilliseconds(diffMs);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
}
