using Intercom.ControlChannel;
using Xunit;

namespace Intercom.App.Tests.ControlChannel;

/// <summary>
/// Every scenario here is a direct translation of a
/// prototypes/14-lan-resilience/Program.cs REPL command sequence into a real
/// test against <see cref="PeerConnectionStateMachine"/> (issue #21's
/// acceptance criterion: "matches the state machine transitions validated in
/// the #14 prototype"). Test names reference the prototype command(s) they
/// correspond to.
/// </summary>
public class PeerConnectionStateMachineTests
{
    static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // ---- discovery ----

    [Fact]
    public void Discover_FromIdle_MovesToDiscovered()
    {
        var sm = new PeerConnectionStateMachine();
        sm.Discover();
        Assert.IsType<ConnState.Discovered>(sm.State);
    }

    [Fact]
    public void Discover_NeverJumpsStraightToConnected()
    {
        var sm = new PeerConnectionStateMachine();
        sm.Discover();
        Assert.IsNotType<ConnState.Connected>(sm.State);
    }

    [Fact]
    public void ExpireRecord_FromDiscovered_ReturnsToIdle()
    {
        var sm = new PeerConnectionStateMachine();
        sm.Discover();
        sm.ExpireRecord();
        Assert.IsType<ConnState.Idle>(sm.State);
    }

    [Fact]
    public void ExpireRecord_WhileConnected_IsIrrelevant_HeartbeatLeaseIsTheLivenessSignalNow()
    {
        var sm = new PeerConnectionStateMachine();
        ConnectSuccessfully(sm, Epoch);

        sm.ExpireRecord();

        Assert.IsType<ConnState.Connected>(sm.State);
    }

    // ---- connecting ----

    [Fact]
    public void Connect_FromDiscovered_MovesToConnecting()
    {
        var sm = new PeerConnectionStateMachine();
        sm.Discover();
        sm.Connect();
        Assert.IsType<ConnState.Connecting>(sm.State);
    }

    [Fact]
    public void Connect_FromIdle_IsNoOp()
    {
        var sm = new PeerConnectionStateMachine();
        sm.Connect();
        Assert.IsType<ConnState.Idle>(sm.State);
    }

    [Fact]
    public void ConnectOk_FromConnecting_MovesToConnected_AndStartsHeartbeatClock()
    {
        var sm = new PeerConnectionStateMachine();
        sm.Discover();
        sm.Connect();
        var incarnation = Guid.NewGuid();

        sm.ConnectOk(incarnation, Epoch);

        var connected = Assert.IsType<ConnState.Connected>(sm.State);
        Assert.Equal(Epoch, connected.LastHeartbeatAt);
        Assert.Equal(incarnation, connected.Incarnation);
    }

    [Fact]
    public void ConnectOk_FromAnyOtherState_IsNoOp()
    {
        var sm = new PeerConnectionStateMachine();
        sm.ConnectOk(Guid.NewGuid(), Epoch);
        Assert.IsType<ConnState.Idle>(sm.State);
    }

    [Fact]
    public void ConnectFail_FromConnecting_MovesToReconnecting_WithNoIncarnationYet()
    {
        var sm = new PeerConnectionStateMachine();
        sm.Discover();
        sm.Connect();

        sm.ConnectFail(Epoch);

        var reconnecting = Assert.IsType<ConnState.Reconnecting>(sm.State);
        Assert.Null(reconnecting.Incarnation);
    }

    [Fact]
    public void ConnectFail_ThenConnect_ReconnectAttemptGoesThroughTheSameTieBreakEligibleStates()
    {
        // ADR-0001: the same deterministic tie-break rule is reused for
        // reconnects, not just the initial race — this asserts the state
        // machine allows re-entering Connecting from Reconnecting, which is
        // the precondition for that reuse.
        var sm = new PeerConnectionStateMachine();
        sm.Discover();
        sm.Connect();
        sm.ConnectFail(Epoch);

        sm.Connect();

        Assert.IsType<ConnState.Connecting>(sm.State);
    }

    // ---- race (tie-break) ----

    [Fact]
    public void Race_BothSidesDialSimultaneously_TieBreakEnsuresOnlyOneLogicalChannelPerSide()
    {
        // The state machine itself doesn't compute the tie-break (see
        // TieBreakTests) — but it must support the shape of a race: a
        // Connecting attempt that gets abandoned via ConnectFail while the
        // other side's inbound connection is what actually succeeds via
        // ConnectOk, landing on exactly one Connected state either way.
        var loser = new PeerConnectionStateMachine();
        loser.Discover();
        loser.Connect();
        loser.ConnectFail(Epoch); // abandons its own outbound attempt

        var winner = new PeerConnectionStateMachine();
        winner.Discover();
        winner.Connect();
        winner.ConnectOk(Guid.NewGuid(), Epoch); // accepts the other side's connection instead

        Assert.IsType<ConnState.Reconnecting>(loser.State);
        Assert.IsType<ConnState.Connected>(winner.State);
    }

    // ---- liveness: heartbeat ----

    [Fact]
    public void Heartbeat_FreshInOrder_RefreshesLease()
    {
        var sm = new PeerConnectionStateMachine();
        var incarnation = Guid.NewGuid();
        ConnectSuccessfully(sm, Epoch, incarnation);

        var later = Epoch + TimeSpan.FromSeconds(9);
        sm.Heartbeat(incarnation, later);

        var connected = Assert.IsType<ConnState.Connected>(sm.State);
        Assert.Equal(later, connected.LastHeartbeatAt);
    }

    [Fact]
    public void Heartbeat_StaleIncarnation_IsRejected_LeaseNotRefreshed()
    {
        var sm = new PeerConnectionStateMachine();
        var incarnation = Guid.NewGuid();
        ConnectSuccessfully(sm, Epoch, incarnation);

        var staleIncarnation = Guid.NewGuid(); // an old, superseded incarnation
        var later = Epoch + TimeSpan.FromSeconds(9);
        sm.Heartbeat(staleIncarnation, later);

        var connected = Assert.IsType<ConnState.Connected>(sm.State);
        Assert.Equal(Epoch, connected.LastHeartbeatAt); // unchanged
    }

    [Fact]
    public void Heartbeat_WithNoSession_IsIgnored()
    {
        var sm = new PeerConnectionStateMachine();
        sm.Heartbeat(Guid.NewGuid(), Epoch);
        Assert.IsType<ConnState.Idle>(sm.State);
    }

    [Fact]
    public void Heartbeat_WhileReconnecting_DoesNotByItselfRestoreConnected()
    {
        // Deliberate, documented deviation from the prototype's REPL
        // shortcut — see PeerConnectionStateMachine.Heartbeat's doc.
        // Reconnection success is only ever signaled by a fresh ConnectOk.
        var sm = new PeerConnectionStateMachine();
        var incarnation = Guid.NewGuid();
        ConnectSuccessfully(sm, Epoch, incarnation);
        sm.Drop();
        Assert.IsType<ConnState.Reconnecting>(sm.State);

        sm.Heartbeat(incarnation, Epoch + TimeSpan.FromSeconds(1));

        Assert.IsType<ConnState.Reconnecting>(sm.State);
    }

    // ---- liveness: lease/tick ----

    [Fact]
    public void Tick_ConnectedWithinLease_StaysConnected()
    {
        var sm = new PeerConnectionStateMachine();
        ConnectSuccessfully(sm, Epoch);

        sm.Tick(Epoch + PeerConnectionStateMachine.LeaseTimeout - TimeSpan.FromSeconds(1));

        Assert.IsType<ConnState.Connected>(sm.State);
    }

    [Fact]
    public void Tick_ConnectedPastLease_DropsToReconnecting()
    {
        var sm = new PeerConnectionStateMachine();
        ConnectSuccessfully(sm, Epoch);

        sm.Tick(Epoch + PeerConnectionStateMachine.LeaseTimeout + TimeSpan.FromSeconds(1));

        Assert.IsType<ConnState.Reconnecting>(sm.State);
    }

    [Fact]
    public void Tick_ReconnectingWithinGiveUpWindow_StaysReconnecting()
    {
        var sm = new PeerConnectionStateMachine();
        ConnectSuccessfully(sm, Epoch);
        sm.Drop();

        sm.Tick(Epoch + PeerConnectionStateMachine.ReconnectGiveUpTimeout - TimeSpan.FromSeconds(1));

        Assert.IsType<ConnState.Reconnecting>(sm.State);
    }

    [Fact]
    public void Tick_ReconnectingPastGiveUpWindow_MovesToOffline()
    {
        var sm = new PeerConnectionStateMachine();
        ConnectSuccessfully(sm, Epoch);
        sm.Drop();

        sm.Tick(Epoch + PeerConnectionStateMachine.ReconnectGiveUpTimeout + TimeSpan.FromSeconds(1));

        Assert.IsType<ConnState.Offline>(sm.State);
    }

    [Fact]
    public void Offline_CanReconnectViaTheSameTieBreakEligiblePath()
    {
        var sm = new PeerConnectionStateMachine();
        ConnectSuccessfully(sm, Epoch);
        sm.Drop();
        sm.Tick(Epoch + PeerConnectionStateMachine.ReconnectGiveUpTimeout + TimeSpan.FromSeconds(1));
        Assert.IsType<ConnState.Offline>(sm.State);

        sm.Connect();

        Assert.IsType<ConnState.Connecting>(sm.State);
    }

    // ---- disruptions: drop ----

    [Fact]
    public void Drop_WhileConnected_MovesToReconnecting_PreservingLastHeartbeatAndIncarnation()
    {
        var sm = new PeerConnectionStateMachine();
        var incarnation = Guid.NewGuid();
        ConnectSuccessfully(sm, Epoch, incarnation);

        sm.Drop();

        var reconnecting = Assert.IsType<ConnState.Reconnecting>(sm.State);
        Assert.Equal(Epoch, reconnecting.LastHeartbeatAt);
        Assert.Equal(incarnation, reconnecting.Incarnation);
    }

    [Fact]
    public void Drop_WithNothingConnected_IsNoOp()
    {
        var sm = new PeerConnectionStateMachine();
        sm.Discover();
        sm.Drop();
        Assert.IsType<ConnState.Discovered>(sm.State);
    }

    [Fact]
    public void Reconnect_AfterDrop_NewIncarnation_OldHeartbeatIsRejectedAsStale()
    {
        // This is the real-world analogue of the prototype's heartbeat-stale
        // scenario, expressed through drop + reconnect rather than a
        // separate command: an old connection's incarnation must never
        // refresh the lease of whatever superseded it.
        var sm = new PeerConnectionStateMachine();
        var oldIncarnation = Guid.NewGuid();
        ConnectSuccessfully(sm, Epoch, oldIncarnation);
        sm.Drop();
        sm.Connect();
        var newIncarnation = Guid.NewGuid();
        sm.ConnectOk(newIncarnation, Epoch + TimeSpan.FromSeconds(5));

        // A stray heartbeat tagged with the OLD incarnation arrives late.
        sm.Heartbeat(oldIncarnation, Epoch + TimeSpan.FromSeconds(6));

        var connected = Assert.IsType<ConnState.Connected>(sm.State);
        Assert.Equal(Epoch + TimeSpan.FromSeconds(5), connected.LastHeartbeatAt); // unchanged by the stale heartbeat
        Assert.Equal(newIncarnation, connected.Incarnation);
    }

    // ---- sleep / resume ----

    [Fact]
    public void Sleep_WhileConnected_WrapsConnected_NeverStaysConnected()
    {
        var sm = new PeerConnectionStateMachine();
        ConnectSuccessfully(sm, Epoch);

        sm.Sleep();

        var asleep = Assert.IsType<ConnState.Asleep>(sm.State);
        Assert.IsType<ConnState.Connected>(asleep.Was);
    }

    [Fact]
    public void Sleep_WhileIdle_WrapsIdle()
    {
        var sm = new PeerConnectionStateMachine();
        sm.Sleep();
        var asleep = Assert.IsType<ConnState.Asleep>(sm.State);
        Assert.IsType<ConnState.Idle>(asleep.Was);
    }

    [Fact]
    public void Sleep_WhileAlreadyAsleep_IsNoOp()
    {
        var sm = new PeerConnectionStateMachine();
        sm.Sleep();
        var first = sm.State;

        sm.Sleep();

        Assert.Equal(first, sm.State);
    }

    [Fact]
    public void Resume_DoesNotRestoreWhateverWasInterrupted_GoesToDiscoveredInstead()
    {
        var sm = new PeerConnectionStateMachine();
        ConnectSuccessfully(sm, Epoch);
        sm.Sleep();

        sm.Resume();

        Assert.IsType<ConnState.Discovered>(sm.State);
    }

    [Fact]
    public void Resume_WhileNotAsleep_IsNoOp()
    {
        var sm = new PeerConnectionStateMachine();
        sm.Discover();

        sm.Resume();

        Assert.IsType<ConnState.Discovered>(sm.State);
    }

    [Fact]
    public void Connect_WhileAsleep_IsIgnored()
    {
        var sm = new PeerConnectionStateMachine();
        sm.Discover();
        sm.Sleep();

        sm.Connect();

        Assert.IsType<ConnState.Asleep>(sm.State);
    }

    [Fact]
    public void WifiChange_WhileAsleep_IsIgnored()
    {
        var sm = new PeerConnectionStateMachine();
        ConnectSuccessfully(sm, Epoch);
        sm.Sleep();

        sm.WifiChange();

        Assert.IsType<ConnState.Asleep>(sm.State);
    }

    // ---- wifi change ----

    [Fact]
    public void WifiChange_WhileConnected_MovesToDiscovered_ReachabilityOnly()
    {
        var sm = new PeerConnectionStateMachine();
        ConnectSuccessfully(sm, Epoch);

        sm.WifiChange();

        Assert.IsType<ConnState.Discovered>(sm.State);
    }

    [Fact]
    public void WifiChange_FromIdle_AlsoLandsOnDiscovered()
    {
        var sm = new PeerConnectionStateMachine();
        sm.WifiChange();
        Assert.IsType<ConnState.Discovered>(sm.State);
    }

    // ---- restart / remote-restart ----

    [Fact]
    public void Restart_FromAnyState_ReturnsToIdle()
    {
        var sm = new PeerConnectionStateMachine();
        ConnectSuccessfully(sm, Epoch);

        sm.Restart();

        Assert.IsType<ConnState.Idle>(sm.State);
    }

    [Fact]
    public void RemoteRestart_WhileConnected_MovesToReconnecting()
    {
        var sm = new PeerConnectionStateMachine();
        var incarnation = Guid.NewGuid();
        ConnectSuccessfully(sm, Epoch, incarnation);

        sm.RemoteRestart();

        var reconnecting = Assert.IsType<ConnState.Reconnecting>(sm.State);
        Assert.Equal(incarnation, reconnecting.Incarnation);
    }

    [Fact]
    public void RemoteRestart_WhileNotConnected_IsNoOp()
    {
        var sm = new PeerConnectionStateMachine();
        sm.Discover();

        sm.RemoteRestart();

        Assert.IsType<ConnState.Discovered>(sm.State);
    }

    [Fact]
    public void RemoteRestart_ThenReconnect_OldIncarnationHeartbeatStillRejected()
    {
        var sm = new PeerConnectionStateMachine();
        var oldIncarnation = Guid.NewGuid();
        ConnectSuccessfully(sm, Epoch, oldIncarnation);

        sm.RemoteRestart();
        sm.Connect();
        var newIncarnation = Guid.NewGuid();
        sm.ConnectOk(newIncarnation, Epoch + TimeSpan.FromSeconds(2));

        sm.Heartbeat(oldIncarnation, Epoch + TimeSpan.FromSeconds(3));

        var connected = Assert.IsType<ConnState.Connected>(sm.State);
        Assert.Equal(Epoch + TimeSpan.FromSeconds(2), connected.LastHeartbeatAt);
    }

    // ---- StateChanged event ----

    [Fact]
    public void StateChanged_RaisedOnRealTransition()
    {
        var sm = new PeerConnectionStateMachine();
        var events = new List<(ConnState Old, ConnState New)>();
        sm.StateChanged += (o, n) => events.Add((o, n));

        sm.Discover();

        var change = Assert.Single(events);
        Assert.IsType<ConnState.Idle>(change.Old);
        Assert.IsType<ConnState.Discovered>(change.New);
    }

    [Fact]
    public void StateChanged_NotRaisedOnNoOp()
    {
        var sm = new PeerConnectionStateMachine();
        sm.Discover();
        var events = new List<(ConnState Old, ConnState New)>();
        sm.StateChanged += (o, n) => events.Add((o, n));

        sm.Discover(); // already Discovered — no-op

        Assert.Empty(events);
    }

    [Fact]
    public void StateChanged_RaisedForHeartbeatRefresh_EvenThoughStillConnected()
    {
        // Connected -> Connected with a new LastHeartbeatAt is still a real
        // (value-unequal) transition and must be observable.
        var sm = new PeerConnectionStateMachine();
        ConnectSuccessfully(sm, Epoch);
        var events = new List<(ConnState Old, ConnState New)>();
        sm.StateChanged += (o, n) => events.Add((o, n));

        sm.Heartbeat(((ConnState.Connected)sm.State).Incarnation, Epoch + TimeSpan.FromSeconds(5));

        Assert.Single(events);
    }

    static void ConnectSuccessfully(PeerConnectionStateMachine sm, DateTimeOffset now, Guid? incarnation = null)
    {
        sm.Discover();
        sm.Connect();
        sm.ConnectOk(incarnation ?? Guid.NewGuid(), now);
    }
}
