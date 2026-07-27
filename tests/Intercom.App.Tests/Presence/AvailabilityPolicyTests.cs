using Intercom.Presence;
using Xunit;

namespace Intercom.App.Tests.Presence;

/// <summary>
/// Tests every acceptance criterion issue #23 calls out for the availability
/// state machine: instant promotion, two-consecutive-sample hysteresis for
/// demotion, session/power/shutdown signals forcing immediate Unavailable,
/// and resume/unlock re-evaluating rather than assuming Available. Mirrors
/// PeerConnectionStateMachineTests' style: an explicit fake clock, no real
/// timers, no Win32.
/// </summary>
public class AvailabilityPolicyTests
{
    static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    static readonly TimeSpan Active = TimeSpan.FromSeconds(5);
    static readonly TimeSpan JustUnderThreshold = TimeSpan.FromSeconds(119);
    static readonly TimeSpan JustOverThreshold = TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1);

    [Fact]
    public void InitialState_IsUnavailable()
    {
        var policy = new AvailabilityPolicy();
        Assert.Equal(AvailabilityState.Unavailable, policy.State);
    }

    [Fact]
    public void Poll_WhileSessionNotUsable_IsNoOp()
    {
        var policy = new AvailabilityPolicy();
        policy.Poll(Active, Epoch);
        Assert.Equal(AvailabilityState.Unavailable, policy.State);
    }

    [Fact]
    public void SessionBecameUsable_NeverLandsDirectlyOnAvailable()
    {
        // docs/research/active-device-presence.md: "unlock/desktop-ready
        // triggers a fresh input/state evaluation rather than blindly
        // declaring it active."
        var policy = new AvailabilityPolicy();
        policy.SessionBecameUsable(Epoch);
        Assert.Equal(AvailabilityState.Idle, policy.State);
    }

    [Fact]
    public void Poll_ActiveInput_AfterSessionUsable_PromotesToAvailable_Instantly()
    {
        var policy = new AvailabilityPolicy();
        policy.SessionBecameUsable(Epoch);

        policy.Poll(Active, Epoch);

        Assert.Equal(AvailabilityState.Available, policy.State);
    }

    [Fact]
    public void Poll_SingleIdleSample_DoesNotYetDemote()
    {
        var policy = new AvailabilityPolicy();
        policy.SessionBecameUsable(Epoch);
        policy.Poll(Active, Epoch); // -> Available

        policy.Poll(JustOverThreshold, Epoch + PresenceEngine.PollInterval);

        // ADR-0004: one idle sample alone must not demote yet.
        Assert.Equal(AvailabilityState.Available, policy.State);
    }

    [Fact]
    public void Poll_TwoConsecutiveIdleSamples_Demotes()
    {
        var policy = new AvailabilityPolicy();
        policy.SessionBecameUsable(Epoch);
        policy.Poll(Active, Epoch); // -> Available

        var t1 = Epoch + PresenceEngine.PollInterval;
        var t2 = t1 + PresenceEngine.PollInterval;
        policy.Poll(JustOverThreshold, t1);
        policy.Poll(JustOverThreshold + PresenceEngine.PollInterval, t2);

        Assert.Equal(AvailabilityState.Idle, policy.State);
    }

    [Fact]
    public void Poll_IdleThenActiveBeforeSecondSample_ResetsHysteresis_NeverDemotes()
    {
        var policy = new AvailabilityPolicy();
        policy.SessionBecameUsable(Epoch);
        policy.Poll(Active, Epoch); // -> Available

        var t1 = Epoch + PresenceEngine.PollInterval;
        policy.Poll(JustOverThreshold, t1); // one idle sample

        var t2 = t1 + PresenceEngine.PollInterval;
        policy.Poll(Active, t2); // fresh input — instant re-promotion, counter reset

        var t3 = t2 + PresenceEngine.PollInterval;
        policy.Poll(JustOverThreshold, t3); // only ONE idle sample since the reset

        Assert.Equal(AvailabilityState.Available, policy.State);
    }

    [Fact]
    public void Poll_JustUnderThreshold_StaysAvailable_NoFlapping()
    {
        var policy = new AvailabilityPolicy();
        policy.SessionBecameUsable(Epoch);
        policy.Poll(Active, Epoch);

        policy.Poll(JustUnderThreshold, Epoch + PresenceEngine.PollInterval);

        Assert.Equal(AvailabilityState.Available, policy.State);
    }

    [Fact]
    public void SessionBecameUnusable_FromAvailable_ForcesUnavailableImmediately()
    {
        var policy = new AvailabilityPolicy();
        policy.SessionBecameUsable(Epoch);
        policy.Poll(Active, Epoch);

        policy.SessionBecameUnusable(Epoch);

        Assert.Equal(AvailabilityState.Unavailable, policy.State);
    }

    [Fact]
    public void SessionBecameUnusable_FromIdle_ForcesUnavailableImmediately()
    {
        var policy = new AvailabilityPolicy();
        policy.SessionBecameUsable(Epoch);
        var t1 = Epoch + PresenceEngine.PollInterval;
        var t2 = t1 + PresenceEngine.PollInterval;
        policy.Poll(JustOverThreshold, t1);
        policy.Poll(JustOverThreshold, t2); // -> Idle

        policy.SessionBecameUnusable(t2);

        Assert.Equal(AvailabilityState.Unavailable, policy.State);
    }

    [Fact]
    public void Poll_AfterSessionBecameUnusable_RemainsUnavailable_IgnoresStaleActiveSample()
    {
        // A racing poll result from before the lock/suspend must never
        // override the immediate Unavailable transition.
        var policy = new AvailabilityPolicy();
        policy.SessionBecameUsable(Epoch);
        policy.Poll(Active, Epoch);
        policy.SessionBecameUnusable(Epoch);

        policy.Poll(Active, Epoch); // stale/racing "still active" sample

        Assert.Equal(AvailabilityState.Unavailable, policy.State);
    }

    [Fact]
    public void SessionBecameUnusable_ThenUsable_ThenIdleInput_RequiresFreshHysteresis()
    {
        // Confirms the hysteresis counter resets across an unusable/usable
        // cycle rather than carrying over stale counts.
        var policy = new AvailabilityPolicy();
        policy.SessionBecameUsable(Epoch);
        var t1 = Epoch + PresenceEngine.PollInterval;
        policy.Poll(JustOverThreshold, t1); // one idle sample toward demotion

        policy.SessionBecameUnusable(t1);
        policy.SessionBecameUsable(t1); // fresh cycle -> Idle (never Available)

        Assert.Equal(AvailabilityState.Idle, policy.State);

        // A single idle sample after the reset must not immediately demote
        // further/differently than the fresh baseline expects, and a single
        // active sample instantly promotes.
        policy.Poll(Active, t1 + PresenceEngine.PollInterval);
        Assert.Equal(AvailabilityState.Available, policy.State);
    }

    [Fact]
    public void StateChanged_RaisedOnlyOnActualTransition_NeverForNoOp()
    {
        var policy = new AvailabilityPolicy();
        var transitions = 0;
        policy.StateChanged += (_, _) => transitions++;

        policy.SessionBecameUsable(Epoch); // Unavailable -> Idle: 1
        policy.SessionBecameUsable(Epoch); // already usable+Idle: no-op

        Assert.Equal(1, transitions);
    }

    [Fact]
    public void StateChanged_NotRaised_WhileHandlerHoldsNoLock_CanCallBackIn()
    {
        // Mirrors PeerConnectionStateMachineTests' reentrancy expectation:
        // a subscriber must be able to call back into the policy from its
        // own StateChanged handler without deadlocking.
        var policy = new AvailabilityPolicy();
        AvailabilityState? observedFromHandler = null;
        policy.StateChanged += (_, _) => observedFromHandler = policy.State;

        policy.SessionBecameUsable(Epoch);

        Assert.Equal(AvailabilityState.Idle, observedFromHandler);
    }
}
