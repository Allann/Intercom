namespace Intercom.Presence;

/// <summary>
/// Pure policy implementation of the availability state machine
/// (docs/research/active-device-presence.md; docs/adr/0004-multi-device-
/// contact-routing.md's "Hysteresis sampling" decision) — no Win32 calls, no
/// timers of its own. Every transition is driven by an explicit signal
/// method and an explicit <see cref="DateTimeOffset"/> passed by the caller
/// (never <see cref="DateTimeOffset.UtcNow"/> read internally), the same
/// "explicit signals + clock" shape as
/// <c>Intercom.ControlChannel.PeerConnectionStateMachine</c>, so hysteresis
/// timing, session-signal interruption, and resume re-evaluation can all be
/// driven deterministically from a test instead of a real Win32 idle-time
/// read or <c>Thread.Sleep</c>.
///
/// GetLastInputInfo is polled — the research doc explicitly rules out
/// installing keyboard/mouse hooks for presence — so there is no separate
/// "instant input" signal distinct from a poll sample. <see cref="Poll"/>
/// alone captures both directions: a single sample under the idle threshold
/// promotes to Available immediately (ADR-0004: "promotion back to
/// available on new input remains instant, unaffected by [the 30-second
/// polling] cadence"), while demoting to Idle requires two CONSECUTIVE
/// samples at or past the threshold, so boundary jitter around the 2-minute
/// mark does not repeatedly flap the state.
///
/// Session/power/shutdown signals are a separate, higher-priority axis:
/// <see cref="SessionBecameUnusable"/> forces Unavailable immediately
/// (locked, disconnected, suspending, shutting down) and always wins over a
/// stale/racing <see cref="Poll"/> result — Poll is a no-op while the
/// session isn't usable. <see cref="SessionBecameUsable"/> deliberately does
/// NOT assume Available — it lands on Idle and waits for the next real
/// <see cref="Poll"/> to decide, per the research doc's "unlock/desktop-
/// ready triggers a fresh input/state evaluation rather than blindly
/// declaring it active." The driving code (<see cref="Presence.PresenceEngine"/>)
/// is expected to take a fresh idle-time sample and call Poll immediately
/// after calling SessionBecameUsable, rather than waiting for the next
/// scheduled 30-second tick — that immediacy is an orchestration concern
/// this pure class cannot (and should not) enforce on its own.
///
/// Thread safety: every public method takes an internal lock around reading
/// and replacing state; <see cref="StateChanged"/> is always raised with the
/// lock released, mirroring PeerConnectionStateMachine's documented
/// reasoning — a subscriber calling back into this class from the event
/// handler must never deadlock against the thread that raised it. This
/// matters here because a poll-timer tick and a Win32 session/power
/// callback can race each other.
/// </summary>
public sealed class AvailabilityPolicy
{
    /// <summary>docs/research/active-device-presence.md: input age at or
    /// beyond this is a candidate idle sample.</summary>
    public static readonly TimeSpan IdleThreshold = TimeSpan.FromMinutes(2);

    /// <summary>ADR-0004: two consecutive idle samples are required before
    /// actually demoting to Idle.</summary>
    const int RequiredConsecutiveIdleSamples = 2;

    readonly object _gate = new();
    AvailabilityState _state = AvailabilityState.Unavailable;
    bool _sessionUsable;
    int _consecutiveIdleSamples;

    public AvailabilityState State { get { lock (_gate) { return _state; } } }

    /// <summary>Raised whenever a signal actually changes the state (never
    /// for a no-op). Always raised with the internal lock released — see the
    /// class remarks.</summary>
    public event Action<AvailabilityState, AvailabilityState>? StateChanged;

    /// <summary>A poll sample of the current input idle age (from
    /// GetLastInputInfo, bounded/sanitized by the caller — never a raw tick
    /// value reaches this method). A no-op while the session is not usable;
    /// Unavailable always wins over a poll result (see
    /// <see cref="SessionBecameUnusable"/>). <paramref name="now"/> is
    /// accepted for API symmetry with this repo's other explicit-clock
    /// policy classes even though this particular transition doesn't need
    /// it — the hysteresis counter, not wall-clock time, drives
    /// demotion.</summary>
    public void Poll(TimeSpan idleAge, DateTimeOffset now) => Apply(s =>
    {
        if (!_sessionUsable) return s; // Unavailable always wins; nothing to evaluate

        if (idleAge < IdleThreshold)
        {
            _consecutiveIdleSamples = 0;
            return AvailabilityState.Available; // instant promotion — no hysteresis on the way up
        }

        _consecutiveIdleSamples++;
        return _consecutiveIdleSamples >= RequiredConsecutiveIdleSamples
            ? AvailabilityState.Idle
            : s; // first idle sample only: not enough yet, hold the current state
    });

    /// <summary>Lock, disconnect, logoff, suspend, or WM_QUERYENDSESSION —
    /// forces Unavailable immediately regardless of any in-flight poll
    /// result, and resets hysteresis so a later
    /// <see cref="SessionBecameUsable"/> starts clean.</summary>
    public void SessionBecameUnusable(DateTimeOffset now) => Apply(_ =>
    {
        _sessionUsable = false;
        _consecutiveIdleSamples = 0;
        return AvailabilityState.Unavailable;
    });

    /// <summary>Unlock, desktop-ready, or a user-triggered resume — marks
    /// the session usable again but deliberately lands on Idle rather than
    /// Available; see the class remarks for why.</summary>
    public void SessionBecameUsable(DateTimeOffset now) => Apply(_ =>
    {
        _sessionUsable = true;
        _consecutiveIdleSamples = 0;
        return AvailabilityState.Idle;
    });

    void Apply(Func<AvailabilityState, AvailabilityState> transform)
    {
        AvailabilityState oldState, newState;
        lock (_gate)
        {
            oldState = _state;
            newState = transform(oldState);
            _state = newState;
        }

        if (oldState != newState)
        {
            StateChanged?.Invoke(oldState, newState);
        }
    }
}
