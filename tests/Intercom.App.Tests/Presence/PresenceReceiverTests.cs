using Intercom.ControlChannel;
using Intercom.Presence;
using Xunit;

namespace Intercom.App.Tests.Presence;

/// <summary>
/// Focused on the acceptance rule <see cref="PresenceReceiver.Ingest"/>
/// enforces (issue #30) — this is the "genuinely tricky, easy to get subtly
/// wrong" logic the ticket calls out: strictly-increasing-per-incarnation
/// sequence acceptance, a changed incarnation resetting that entirely (even
/// with a numerically lower sequence), out-of-order arrival, and local,
/// receiver-clock-only expiry.
/// </summary>
public class PresenceReceiverTests
{
    static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    static readonly Guid DeviceA = Guid.NewGuid();
    static readonly Guid ContactId = Guid.NewGuid();

    static PresenceLease Lease(Guid incarnation, ulong sequence, AvailabilityState availability = AvailabilityState.Available, bool dnd = false, int leaseSeconds = 30) =>
        new()
        {
            DeviceId = DeviceA,
            ContactId = ContactId,
            IncarnationId = incarnation,
            Sequence = sequence,
            Availability = availability,
            Dnd = dnd,
            IdleAgeBucket = availability == AvailabilityState.Unavailable ? null : IdleAgeBucket.UnderTwoMinutes,
            LeaseSeconds = leaseSeconds,
            Capabilities = Capability.Text,
        };

    [Fact]
    public void Ingest_FirstEverLeaseForDevice_IsAccepted()
    {
        var receiver = new PresenceReceiver();
        var incarnation = Guid.NewGuid();

        var accepted = receiver.Ingest(Lease(incarnation, 1), Epoch);

        Assert.True(accepted);
        Assert.Equal(1UL, receiver.Get(DeviceA)!.Sequence);
    }

    [Fact]
    public void Ingest_HigherSequence_SameIncarnation_IsAccepted()
    {
        var receiver = new PresenceReceiver();
        var incarnation = Guid.NewGuid();
        receiver.Ingest(Lease(incarnation, 1), Epoch);

        var accepted = receiver.Ingest(Lease(incarnation, 2), Epoch.AddSeconds(1));

        Assert.True(accepted);
        Assert.Equal(2UL, receiver.Get(DeviceA)!.Sequence);
    }

    [Fact]
    public void Ingest_EqualSequence_SameIncarnation_IsRejected()
    {
        var receiver = new PresenceReceiver();
        var incarnation = Guid.NewGuid();
        receiver.Ingest(Lease(incarnation, 5), Epoch);

        var accepted = receiver.Ingest(Lease(incarnation, 5), Epoch.AddSeconds(1));

        Assert.False(accepted);
        Assert.Equal(5UL, receiver.Get(DeviceA)!.Sequence); // untouched
    }

    [Fact]
    public void Ingest_LowerSequence_SameIncarnation_IsRejected_OutOfOrderArrival()
    {
        var receiver = new PresenceReceiver();
        var incarnation = Guid.NewGuid();
        receiver.Ingest(Lease(incarnation, 10), Epoch);

        // A delayed/reordered packet for sequence 7 arrives after 10 already landed.
        var accepted = receiver.Ingest(Lease(incarnation, 7), Epoch.AddSeconds(1));

        Assert.False(accepted);
        Assert.Equal(10UL, receiver.Get(DeviceA)!.Sequence);
    }

    [Fact]
    public void Ingest_LowerSequenceAfterHigher_ThenAnotherHigher_StillAccepted()
    {
        var receiver = new PresenceReceiver();
        var incarnation = Guid.NewGuid();
        receiver.Ingest(Lease(incarnation, 10), Epoch);
        receiver.Ingest(Lease(incarnation, 3), Epoch.AddSeconds(1)); // rejected, stale

        var accepted = receiver.Ingest(Lease(incarnation, 11), Epoch.AddSeconds(2));

        Assert.True(accepted);
        Assert.Equal(11UL, receiver.Get(DeviceA)!.Sequence);
    }

    [Fact]
    public void Ingest_ChangedIncarnation_WithLowerSequence_IsStillAccepted_IncarnationResetsCounter()
    {
        var receiver = new PresenceReceiver();
        var oldIncarnation = Guid.NewGuid();
        var newIncarnation = Guid.NewGuid();
        receiver.Ingest(Lease(oldIncarnation, 100), Epoch);

        // The device restarted: a brand new incarnation starts back at a low
        // sequence number. Numerically lower than 100, but MUST be accepted
        // because the incarnation changed — this is the subtle case the
        // ticket explicitly calls out.
        var accepted = receiver.Ingest(Lease(newIncarnation, 1), Epoch.AddSeconds(1));

        Assert.True(accepted);
        var tracked = receiver.Get(DeviceA)!;
        Assert.Equal(newIncarnation, tracked.IncarnationId);
        Assert.Equal(1UL, tracked.Sequence);
    }

    [Fact]
    public void Ingest_ChangedIncarnation_WithSequenceZero_IsAccepted()
    {
        var receiver = new PresenceReceiver();
        var oldIncarnation = Guid.NewGuid();
        var newIncarnation = Guid.NewGuid();
        receiver.Ingest(Lease(oldIncarnation, 1), Epoch);

        var accepted = receiver.Ingest(Lease(newIncarnation, 0), Epoch.AddSeconds(1));

        Assert.True(accepted);
        Assert.Equal(newIncarnation, receiver.Get(DeviceA)!.IncarnationId);
    }

    [Fact]
    public void Ingest_AfterIncarnationChange_OldIncarnationLateArrival_IsRejected()
    {
        var receiver = new PresenceReceiver();
        var oldIncarnation = Guid.NewGuid();
        var newIncarnation = Guid.NewGuid();
        receiver.Ingest(Lease(oldIncarnation, 5), Epoch);
        receiver.Ingest(Lease(newIncarnation, 1), Epoch.AddSeconds(1)); // supersedes

        // A very late packet from the OLD incarnation finally arrives.
        var accepted = receiver.Ingest(Lease(oldIncarnation, 6), Epoch.AddSeconds(2));

        Assert.False(accepted);
        Assert.Equal(newIncarnation, receiver.Get(DeviceA)!.IncarnationId);
        Assert.Equal(1UL, receiver.Get(DeviceA)!.Sequence);
    }

    [Fact]
    public void Ingest_Accepted_RaisesLeaseAccepted()
    {
        var receiver = new PresenceReceiver();
        TrackedDevicePresence? raised = null;
        receiver.LeaseAccepted += t => raised = t;

        receiver.Ingest(Lease(Guid.NewGuid(), 1), Epoch);

        Assert.NotNull(raised);
        Assert.Equal(DeviceA, raised!.DeviceId);
    }

    [Fact]
    public void Ingest_Rejected_DoesNotRaiseLeaseAccepted()
    {
        var receiver = new PresenceReceiver();
        var incarnation = Guid.NewGuid();
        receiver.Ingest(Lease(incarnation, 5), Epoch);

        var raiseCount = 0;
        receiver.LeaseAccepted += _ => raiseCount++;
        receiver.Ingest(Lease(incarnation, 5), Epoch.AddSeconds(1));

        Assert.Equal(0, raiseCount);
    }

    [Fact]
    public void ExpiresAt_ComputedFromReceiverClock_NotSenderLeaseSeconds_AsWallClock()
    {
        var receiver = new PresenceReceiver();
        receiver.Ingest(Lease(Guid.NewGuid(), 1, leaseSeconds: 30), Epoch);

        var tracked = receiver.Get(DeviceA)!;

        Assert.Equal(Epoch, tracked.LastUpdatedAt);
        Assert.Equal(Epoch.AddSeconds(30), tracked.ExpiresAt);
    }

    [Fact]
    public void LiveDevices_ExcludesExpiredRecord()
    {
        var receiver = new PresenceReceiver();
        receiver.Ingest(Lease(Guid.NewGuid(), 1, leaseSeconds: 30), Epoch);

        var live = receiver.LiveDevices([DeviceA], Epoch.AddSeconds(31));

        Assert.Empty(live);
    }

    [Fact]
    public void LiveDevices_IncludesNotYetExpiredRecord()
    {
        var receiver = new PresenceReceiver();
        receiver.Ingest(Lease(Guid.NewGuid(), 1, leaseSeconds: 30), Epoch);

        var live = receiver.LiveDevices([DeviceA], Epoch.AddSeconds(29));

        Assert.Single(live);
    }

    [Fact]
    public void LiveDevices_IncludesExplicitlyUnavailableDevice_ExcludingThemIsRankersJob()
    {
        var receiver = new PresenceReceiver();
        receiver.Ingest(Lease(Guid.NewGuid(), 1, AvailabilityState.Unavailable), Epoch);

        var live = receiver.LiveDevices([DeviceA], Epoch.AddSeconds(1));

        Assert.Single(live);
        Assert.Equal(AvailabilityState.Unavailable, live[0].Availability);
    }

    [Fact]
    public void LiveDevices_FiltersToRequestedDeviceIds_IgnoringWireContactId()
    {
        // ADR-0004: grouping is never transmitted, so this receiver must not
        // rely on the wire ContactId for "which devices belong together" —
        // only the caller-supplied device ID set (from a local ContactStore)
        // matters.
        var receiver = new PresenceReceiver();
        var otherDevice = Guid.NewGuid();
        receiver.Ingest(Lease(Guid.NewGuid(), 1) with { DeviceId = DeviceA, ContactId = Guid.NewGuid() }, Epoch);
        receiver.Ingest(Lease(Guid.NewGuid(), 1) with { DeviceId = otherDevice, ContactId = Guid.NewGuid() }, Epoch);

        var live = receiver.LiveDevices([DeviceA], Epoch.AddSeconds(1));

        Assert.Single(live);
        Assert.Equal(DeviceA, live[0].DeviceId);
    }

    [Fact]
    public void SystemClockChange_DoesNotAffectOrdering_OnlyExplicitClockMatters()
    {
        var receiver = new PresenceReceiver();
        var incarnation = Guid.NewGuid();
        receiver.Ingest(Lease(incarnation, 1), Epoch);

        // Even a "clock" that jumps backward has no bearing on acceptance —
        // only Sequence/IncarnationId decide that; receivedAt only affects
        // LastUpdatedAt/ExpiresAt bookkeeping.
        var accepted = receiver.Ingest(Lease(incarnation, 2), Epoch.AddDays(-1));

        Assert.True(accepted);
        Assert.Equal(2UL, receiver.Get(DeviceA)!.Sequence);
    }
}
