namespace Intercom.Presence;

/// <summary>
/// Thin seam over the native GetLastInputInfo call (winuser.h; no managed
/// wrapper). All policy (thresholds, hysteresis) lives in
/// <see cref="AvailabilityPolicy"/>, which depends only on this interface —
/// the same seam pattern as <c>Intercom.Discovery.IDnsServiceDiscovery</c>.
/// The real implementation (<c>Intercom.App.Presence.Win32IdleTimeProvider</c>)
/// cannot be exercised in a normal CI/test sandbox in any deterministic way
/// (it reports however long this actual machine has been idle), so it is a
/// thin, deliberately untested wrapper — same reasoning as
/// <c>Win32DnsServiceDiscovery</c>.
/// </summary>
public interface IIdleTimeProvider
{
    /// <summary>How long since the last input event for the CURRENT session
    /// only (GetLastInputInfo's own documented scope — not system-wide
    /// across sessions). Implementations must bound/sanitize any anomalous
    /// or backward-looking native tick value themselves (winuser.h
    /// documents the tick count as "not guaranteed to be incremental") —
    /// never a raw tick value leaves this method.</summary>
    TimeSpan GetIdleTime();
}
