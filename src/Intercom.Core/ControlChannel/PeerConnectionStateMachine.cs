namespace Intercom.ControlChannel;

/// <summary>
/// Pure policy implementation of the <see cref="ConnState"/> lifecycle
/// validated in prototypes/14-lan-resilience (issue #14) — no sockets, no
/// TLS, no timers of its own. Every transition is driven by an explicit
/// signal method and an explicit <see cref="DateTimeOffset"/> passed by the
/// caller (never <see cref="DateTimeOffset.UtcNow"/> read internally), the
/// same "explicit signals + clock" shape as
/// <c>Intercom.Discovery.VisiblePeerList</c> — so every hard case (races,
/// drops, sleep/resume, Wi-Fi changes, restarts, lease expiry) can be driven
/// deterministically from a test with a fake clock instead of
/// <c>Thread.Sleep</c>.
///
/// One instance manages exactly one peer's connection lifecycle. Thread
/// safety: every public method takes an internal lock around reading and
/// replacing the current state, and the <see cref="StateChanged"/> event is
/// always raised with that lock released — mirroring
/// <c>Intercom.Discovery.VisiblePeerList</c>'s documented reasoning: a
/// subscriber calling straight back into this class from the event handler
/// must never deadlock against the thread that raised it. In the real
/// control channel this matters because a heartbeat-timer tick, an inbound
/// frame on the receive loop, and an outside caller reacting to sleep/resume
/// notifications can all call in concurrently.
/// </summary>
public sealed class PeerConnectionStateMachine
{
    /// <summary>ADR-0001 / docs/research/active-device-presence.md: the
    /// presence lease's heartbeat cadence, reused unmodified as the
    /// connection-liveness cadence — there is no separate transport
    /// keepalive.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);

    /// <summary>ADR-0001: no heartbeat within this window means the
    /// connection is dead, not just "presence is stale."</summary>
    public static readonly TimeSpan LeaseTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Mirrors the #14 prototype's "no successful reconnect for an
    /// extended period -&gt; Offline" rule (there: <c>LeaseSeconds * 3</c>).
    /// Not separately specified anywhere else — this is the same illustrative
    /// multiple the prototype validated, carried into the real
    /// implementation for lifecycle fidelity.</summary>
    public static readonly TimeSpan ReconnectGiveUpTimeout = TimeSpan.FromTicks(LeaseTimeout.Ticks * 3);

    readonly object _gate = new();
    ConnState _state = new ConnState.Idle();

    public ConnState State
    {
        get { lock (_gate) { return _state; } }
    }

    /// <summary>Raised whenever a signal actually changes the state (never
    /// for a no-op, e.g. Discover() called twice). Always raised with the
    /// internal lock released — see the class doc.</summary>
    public event Action<ConnState, ConnState>? StateChanged;

    /// <summary>An mDNS/DNS-SD record for the peer became visible. Never
    /// jumps straight to Connected — discovery is visibility only
    /// (ADR-0001).</summary>
    public void Discover() => Apply(s => s switch
    {
        ConnState.Idle => new ConnState.Discovered(),
        ConnState.Asleep => s, // ignored while asleep
        _ => s, // already past Discovered; re-seeing the record is a no-op here
    });

    /// <summary>The discovery record's TTL lapsed, or a goodbye record
    /// arrived, before a connection was ever established.</summary>
    public void ExpireRecord() => Apply(s => s is ConnState.Discovered ? new ConnState.Idle() : s);
    // Once connected, the heartbeat/lease is the liveness signal, not the
    // mDNS record (ADR-0001) — expiry from any other state is a no-op.

    /// <summary>We are about to attempt a TLS connection to the peer (either
    /// the initial connect, or a reconnect after a drop). Valid from
    /// Discovered, Reconnecting, or Offline; a no-op from anywhere else,
    /// including while a connection attempt is already in flight — callers
    /// must check <see cref="State"/> is <see cref="ConnState.Connecting"/>
    /// afterward to know whether this call actually started one.</summary>
    public void Connect() => Apply(s => s switch
    {
        ConnState.Discovered or ConnState.Reconnecting or ConnState.Offline => new ConnState.Connecting(),
        ConnState.Asleep => s, // ignored while asleep
        _ => s,
    });

    /// <summary>Mutual TLS and the one-time Hello exchange both succeeded.
    /// <paramref name="incarnation"/> is a fresh identifier for this specific
    /// connection instance (the caller mints it, e.g. <see cref="Guid.NewGuid"/>
    /// at the moment the handshake completes) — every heartbeat accepted
    /// against this session must carry the same value; see
    /// <see cref="ConnState.Connected.Incarnation"/>.</summary>
    public void ConnectOk(Guid incarnation, DateTimeOffset now) => Apply(s => s is ConnState.Connecting
        ? new ConnState.Connected { LastHeartbeatAt = now, Incarnation = incarnation }
        : s);

    /// <summary>The TLS handshake attempt failed. Moves to Reconnecting; the
    /// same deterministic tie-break rule applies again on the next Connect()
    /// (ADR-0001: the rule is reused for reconnects, not just the initial
    /// race).</summary>
    public void ConnectFail(DateTimeOffset now) => Apply(s => s is ConnState.Connecting
        ? new ConnState.Reconnecting { LastHeartbeatAt = now, Incarnation = null }
        : s);

    /// <summary>A fresh, in-order heartbeat/lease refresh arrived, tagged
    /// with the incarnation of the connection it arrived on.
    ///
    /// Only refreshes the lease when Connected AND the incarnation matches
    /// the one recorded at ConnectOk — a heartbeat tagged with any other
    /// incarnation is REJECTED outright (no lease refresh, no state change),
    /// mirroring the prototype's heartbeat-stale scenario. While
    /// Reconnecting, a heartbeat is always ignored: reconnection success is
    /// only ever signaled by completing a fresh handshake (ConnectOk), never
    /// by a bare heartbeat frame arriving. This is a deliberate, narrow
    /// deviation from the prototype, which let "fresh heartbeat while
    /// Reconnecting" stand in for "the reconnect handshake completed" as an
    /// interactive-REPL shortcut; the real transport can only ever deliver a
    /// heartbeat frame over an already-authenticated connection, so treating
    /// one as reconnection success without an intervening ConnectOk would
    /// blur exactly the incarnation bookkeeping this method exists to keep
    /// precise.</summary>
    public void Heartbeat(Guid incarnation, DateTimeOffset now) => Apply(s => s switch
    {
        ConnState.Connected c when c.Incarnation == incarnation =>
            new ConnState.Connected { LastHeartbeatAt = now, Incarnation = incarnation },
        _ => s, // stale incarnation, or no live session to refresh (Connected mismatch, Reconnecting, or anything else)
    });

    /// <summary>Advances the clock and evaluates lease/give-up expiry.
    /// Connected with no heartbeat for more than <see cref="LeaseTimeout"/>
    /// drops to Reconnecting (this doubles as "the connection is dead," not
    /// just "presence is stale" — ADR-0001). Reconnecting with no successful
    /// reconnect for more than <see cref="ReconnectGiveUpTimeout"/> gives up
    /// to Offline.</summary>
    public void Tick(DateTimeOffset now) => Apply(s => s switch
    {
        ConnState.Connected c when now - c.LastHeartbeatAt > LeaseTimeout =>
            new ConnState.Reconnecting { LastHeartbeatAt = c.LastHeartbeatAt, Incarnation = c.Incarnation },
        ConnState.Reconnecting r when now - r.LastHeartbeatAt > ReconnectGiveUpTimeout =>
            new ConnState.Offline(),
        _ => s,
    });

    /// <summary>The live TCP connection dropped (network blip, remote reset,
    /// local socket error). Any in-flight unacknowledged-as-delivered message
    /// is failed the instant this happens — no auto-resend across the drop
    /// (ADR-0001); that failure is the caller's responsibility to surface,
    /// this method only owns the lifecycle transition.</summary>
    public void Drop() => Apply(s => s is ConnState.Connected c
        ? new ConnState.Reconnecting { LastHeartbeatAt = c.LastHeartbeatAt, Incarnation = c.Incarnation }
        : s);

    /// <summary>The local PC is suspending (PBT_APMSUSPEND). Wraps whatever
    /// awake state was interrupted; a no-op if already Asleep.</summary>
    public void Sleep() => Apply(s => s switch
    {
        IAwakeConnState awake => new ConnState.Asleep { Was = awake },
        ConnState.Asleep => s,
        // Every ConnState is either IAwakeConnState or Asleep — unreachable
        // unless a future state is added without updating one of those two.
        _ => throw new InvalidOperationException($"Unhandled ConnState: {s.GetType().Name}"),
    });

    /// <summary>The local PC resumed. Deliberately does NOT restore the
    /// wrapped `Was` state — per docs/research/active-device-presence.md,
    /// resume must re-evaluate reachability from scratch rather than assume
    /// it, so this always lands on Discovered regardless of what sleep
    /// interrupted. A no-op if not currently Asleep.</summary>
    public void Resume() => Apply(s => s is ConnState.Asleep ? new ConnState.Discovered() : s);

    /// <summary>The network adapter/address changed (DHCP renewal, Wi-Fi
    /// roam, adapter re-index after sleep). Invalidates only the reachability
    /// path, never the approved-peer identity/trust (that is the SPKI pin, a
    /// separate, unaffected concern) — lands on Discovered from every awake
    /// state so discovery/connection re-evaluates against the new addresses.
    /// Ignored while asleep.</summary>
    public void WifiChange() => Apply(s => s switch
    {
        ConnState.Asleep => s,
        _ => new ConnState.Discovered(),
    });

    /// <summary>The local app process is restarting. In the real app this
    /// state machine instance would simply be discarded and replaced by a
    /// fresh one (a new process has no state to carry forward at all) — this
    /// method exists mainly so tests can exercise the transition explicitly
    /// for parity with the prototype's command set. Always lands on Idle
    /// regardless of current state.</summary>
    public void Restart() => Apply(_ => new ConnState.Idle());

    /// <summary>The REMOTE peer's process restarted while we were Connected
    /// to it (detected by the transport/handshake layer, e.g. an unexpected
    /// fresh Hello on what should have been an established session). Same
    /// state transition as <see cref="Drop"/> — kept as a separate method
    /// for call-site clarity about why the connection is being torn down,
    /// and so future reconnect-backoff policy can distinguish the two causes
    /// without changing this method's signature. The old incarnation
    /// recorded on the resulting Reconnecting state is what lets a stray
    /// heartbeat still trickling in from the peer's OLD (pre-restart)
    /// incarnation be rejected by <see cref="Heartbeat"/> once reconnected
    /// under a new incarnation.</summary>
    public void RemoteRestart() => Apply(s => s is ConnState.Connected c
        ? new ConnState.Reconnecting { LastHeartbeatAt = c.LastHeartbeatAt, Incarnation = c.Incarnation }
        : s);

    void Apply(Func<ConnState, ConnState> transform)
    {
        ConnState oldState, newState;
        lock (_gate)
        {
            oldState = _state;
            newState = transform(oldState);
            _state = newState;
        }

        if (!oldState.Equals(newState))
        {
            StateChanged?.Invoke(oldState, newState);
        }
    }
}
