using Intercom.ControlChannel;
using Intercom.Presence;
using Intercom.Routing;
using Xunit;

namespace Intercom.App.Tests.Routing;

/// <summary>
/// Exhaustive tie/non-tie coverage for <see cref="DeviceRanker.Rank"/> —
/// this ticket's other genuinely tricky piece: exclusion rules, ordering
/// priority, and ADR-0004's precise tie definition (same availability, same
/// idle bucket, recency within the heartbeat window).
/// </summary>
public class DeviceRankerTests
{
    static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    static TrackedDevicePresence Device(
        Guid? deviceId = null,
        AvailabilityState availability = AvailabilityState.Available,
        bool dnd = false,
        IdleAgeBucket? idleAgeBucket = IdleAgeBucket.UnderTwoMinutes,
        DateTimeOffset? lastUpdatedAt = null,
        DateTimeOffset? expiresAt = null) => new()
        {
            DeviceId = deviceId ?? Guid.NewGuid(),
            ContactId = Guid.NewGuid(),
            IncarnationId = Guid.NewGuid(),
            Sequence = 1,
            Availability = availability,
            Dnd = dnd,
            IdleAgeBucket = idleAgeBucket,
            Capabilities = Capability.Text,
            LastUpdatedAt = lastUpdatedAt ?? Now,
            ExpiresAt = expiresAt ?? Now.AddSeconds(30),
        };

    [Fact]
    public void Rank_NoDevices_ReturnsEmptyNoTie()
    {
        var result = DeviceRanker.Rank([], InteractionKind.Text, Now);

        Assert.Empty(result.RankedDevices);
        Assert.False(result.IsTie);
        Assert.Null(result.TopDeviceId);
    }

    [Fact]
    public void Rank_ExcludesExpiredDevice()
    {
        var expired = Device(expiresAt: Now.AddSeconds(-1));

        var result = DeviceRanker.Rank([expired], InteractionKind.Text, Now);

        Assert.Empty(result.RankedDevices);
    }

    [Fact]
    public void Rank_ExcludesUnavailableDevice()
    {
        var unavailable = Device(availability: AvailabilityState.Unavailable, idleAgeBucket: null);

        var result = DeviceRanker.Rank([unavailable], InteractionKind.Text, Now);

        Assert.Empty(result.RankedDevices);
    }

    [Fact]
    public void Rank_Voice_ExcludesDndDevice()
    {
        var dndDevice = Device(dnd: true);

        var result = DeviceRanker.Rank([dndDevice], InteractionKind.Voice, Now);

        Assert.Empty(result.RankedDevices);
    }

    [Fact]
    public void Rank_AttentionChime_ExcludesDndDevice()
    {
        var dndDevice = Device(dnd: true);

        var result = DeviceRanker.Rank([dndDevice], InteractionKind.AttentionChime, Now);

        Assert.Empty(result.RankedDevices);
    }

    [Fact]
    public void Rank_Text_KeepsDndDeviceEligible()
    {
        var dndDevice = Device(dnd: true);

        var result = DeviceRanker.Rank([dndDevice], InteractionKind.Text, Now);

        Assert.Single(result.RankedDevices);
    }

    [Fact]
    public void Rank_PrefersAvailableOverIdle()
    {
        var idleDevice = Device(availability: AvailabilityState.Idle, idleAgeBucket: IdleAgeBucket.TwoToTenMinutes);
        var availableDevice = Device(availability: AvailabilityState.Available);

        var result = DeviceRanker.Rank([idleDevice, availableDevice], InteractionKind.Voice, Now);

        Assert.Equal(availableDevice.DeviceId, result.TopDeviceId);
    }

    [Fact]
    public void Rank_WithinAvailable_PrefersLowerIdleBucket()
    {
        var moreIdle = Device(idleAgeBucket: IdleAgeBucket.OverTenMinutes, lastUpdatedAt: Now);
        var lessIdle = Device(idleAgeBucket: IdleAgeBucket.UnderTwoMinutes, lastUpdatedAt: Now);

        var result = DeviceRanker.Rank([moreIdle, lessIdle], InteractionKind.Voice, Now);

        Assert.Equal(lessIdle.DeviceId, result.TopDeviceId);
    }

    [Fact]
    public void Rank_SameAvailabilityAndBucket_PrefersMostRecentUpdate()
    {
        var stale = Device(lastUpdatedAt: Now.AddMinutes(-5));
        var fresh = Device(lastUpdatedAt: Now);

        var result = DeviceRanker.Rank([stale, fresh], InteractionKind.Voice, Now);

        Assert.Equal(fresh.DeviceId, result.TopDeviceId);
        // Recency gap (5 minutes) is well outside the heartbeat tie window.
        Assert.False(result.IsTie);
    }

    [Fact]
    public void Rank_IdenticalDevices_TieBrokenByDeviceIdOrdering()
    {
        var lowId = Device(deviceId: new Guid("00000000-0000-0000-0000-000000000001"));
        var highId = Device(deviceId: new Guid("00000000-0000-0000-0000-000000000002"), lastUpdatedAt: Now);

        var result = DeviceRanker.Rank([highId, lowId], InteractionKind.Voice, Now);

        // Both share the same LastUpdatedAt, so device-ID ordering settles it deterministically.
        Assert.Equal(lowId.DeviceId, result.TopDeviceId);
    }

    [Fact]
    public void Rank_WithinRecencyTieWindow_SameAvailabilityAndBucket_IsATie()
    {
        var a = Device(lastUpdatedAt: Now);
        var b = Device(lastUpdatedAt: Now - DeviceRanker.RecencyTieWindow); // exactly at the boundary — still within

        var result = DeviceRanker.Rank([a, b], InteractionKind.Text, Now);

        Assert.True(result.IsTie);
    }

    [Fact]
    public void Rank_JustOutsideRecencyTieWindow_IsNotATie()
    {
        var a = Device(lastUpdatedAt: Now);
        var b = Device(lastUpdatedAt: Now - DeviceRanker.RecencyTieWindow - TimeSpan.FromMilliseconds(1));

        var result = DeviceRanker.Rank([a, b], InteractionKind.Text, Now);

        Assert.False(result.IsTie);
    }

    [Fact]
    public void Rank_DifferentIdleBucket_IsNeverATie_EvenWithinRecencyWindow()
    {
        var a = Device(idleAgeBucket: IdleAgeBucket.UnderTwoMinutes, lastUpdatedAt: Now);
        var b = Device(availability: AvailabilityState.Idle, idleAgeBucket: IdleAgeBucket.TwoToTenMinutes, lastUpdatedAt: Now);

        var result = DeviceRanker.Rank([a, b], InteractionKind.Text, Now);

        Assert.False(result.IsTie);
    }

    [Fact]
    public void Rank_ThreeDevices_OneClearlyBest_IsNotATie_EvenIfOtherTwoTieForSecond()
    {
        var best = Device(lastUpdatedAt: Now);
        var secondA = Device(availability: AvailabilityState.Idle, idleAgeBucket: IdleAgeBucket.TwoToTenMinutes, lastUpdatedAt: Now);
        var secondB = Device(availability: AvailabilityState.Idle, idleAgeBucket: IdleAgeBucket.TwoToTenMinutes, lastUpdatedAt: Now);

        var result = DeviceRanker.Rank([best, secondA, secondB], InteractionKind.Text, Now);

        Assert.Equal(best.DeviceId, result.TopDeviceId);
        Assert.False(result.IsTie);
        Assert.Equal(3, result.RankedDevices.Count);
    }

    [Fact]
    public void Rank_RankedDevices_ExcludesIneligibleButKeepsRestOrdered()
    {
        var expired = Device(expiresAt: Now.AddSeconds(-1));
        var dndForVoice = Device(dnd: true);
        var good = Device(lastUpdatedAt: Now);

        var result = DeviceRanker.Rank([expired, dndForVoice, good], InteractionKind.Voice, Now);

        Assert.Single(result.RankedDevices);
        Assert.Equal(good.DeviceId, result.TopDeviceId);
    }
}
