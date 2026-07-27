namespace Intercom.ControlChannel;

/// <summary>
/// Marks every ConnState that local sleep can interrupt. Deliberately not
/// implemented by <see cref="ConnState.Asleep"/> — see that type's doc for
/// why that single omission is what makes "asleep while already asleep"
/// (and every other "connected AND asleep at once" incoherence) a compile
/// error instead of a runtime check, per
/// prototypes/14-lan-resilience/README.md.
/// </summary>
public interface IAwakeConnState
{
}

/// <summary>
/// The connection lifecycle for one peer, translated 1:1 from the closed
/// <c>ConnState</c> hierarchy validated in
/// prototypes/14-lan-resilience/Program.cs (issue #14) into the real
/// implementation (issue #21's acceptance criterion: "matches the state
/// machine transitions validated in the #14 prototype").
///
/// Modeled as one sealed record per state — not an enum plus loose nullable
/// fields — per "making illegal states unrepresentable": heartbeat/lease data
/// only exists where it's meaningful (<see cref="Connected"/> and
/// <see cref="Reconnecting"/>), and <see cref="Asleep"/> wraps whatever awake
/// state it interrupted rather than being tracked as a separate bool that
/// could drift out of sync with the rest of the state (that drift was a real
/// bug the prototype hit and fixed — see the prototype README's "What this
/// fixed for real" section).
///
/// The private constructor closes the hierarchy to the nested sealed records
/// below, matching this codebase's other closed-hierarchy convention (see
/// Intercom.Discovery.DiscoverySignal).
/// </summary>
public abstract record ConnState
{
    private ConnState() { }

    /// <summary>No discovery record, no connection.</summary>
    public sealed record Idle : ConnState, IAwakeConnState;

    /// <summary>An mDNS/DNS-SD record for the peer is currently visible.
    /// NOT trusted, NOT connected — discovery is visibility only
    /// (ADR-0001).</summary>
    public sealed record Discovered : ConnState, IAwakeConnState;

    /// <summary>A TLS handshake plus the one-time Hello capability exchange
    /// is in flight.</summary>
    public sealed record Connecting : ConnState, IAwakeConnState;

    /// <summary>Mutual TLS and Hello succeeded; the heartbeat/lease clock is
    /// live. <see cref="Incarnation"/> identifies this specific connection
    /// instance (assigned fresh at every successful handshake) — it exists
    /// so a heartbeat that arrives tagged with a superseded incarnation
    /// (e.g. a stray frame from an old, not-yet-fully-torn-down connection
    /// racing a brand new one) can be told apart from a genuine, current
    /// heartbeat and rejected outright, mirroring the prototype's
    /// heartbeat-stale scenario and docs/research/active-device-presence.md's
    /// "the receiver accepts only a sequence newer than the last one for the
    /// same (device_id, incarnation_id)" rule.</summary>
    public sealed record Connected : ConnState, IAwakeConnState
    {
        public required DateTimeOffset LastHeartbeatAt { get; init; }
        public required Guid Incarnation { get; init; }
    }

    /// <summary>The lease lapsed or the socket dropped; retrying. Carries the
    /// last known-good heartbeat time forward (not reset to "now") so
    /// <see cref="PeerConnectionStateMachine.Tick"/> can measure the
    /// give-up-to-Offline window from when liveness was actually last
    /// confirmed, not from when the drop was noticed. <see cref="Incarnation"/>
    /// is null only for the very first-ever connection attempt failing
    /// (there was never a successful handshake to assign one from).</summary>
    public sealed record Reconnecting : ConnState, IAwakeConnState
    {
        public required DateTimeOffset LastHeartbeatAt { get; init; }
        public required Guid? Incarnation { get; init; }
    }

    /// <summary>The lease fully expired with no successful reconnect for an
    /// extended period (see
    /// <see cref="PeerConnectionStateMachine.ReconnectGiveUpTimeout"/>).</summary>
    public sealed record Offline : ConnState, IAwakeConnState;

    /// <summary>The local PC suspended. Wraps whatever awake state it
    /// interrupted rather than tracking sleep as an independent bool — see
    /// <see cref="IAwakeConnState"/>'s doc. <see cref="Was"/> is typed as the
    /// marker interface (not <see cref="ConnState"/> directly) so
    /// <c>Asleep(Asleep(...))</c> cannot even be constructed, matching the
    /// prototype's <c>IAwakeState</c> device exactly.</summary>
    public sealed record Asleep : ConnState
    {
        public required IAwakeConnState Was { get; init; }
    }
}
