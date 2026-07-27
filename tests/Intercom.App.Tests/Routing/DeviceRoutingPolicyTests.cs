using Intercom.ControlChannel;
using Intercom.Presence;
using Intercom.Routing;
using Xunit;

namespace Intercom.App.Tests.Routing;

/// <summary>
/// The manual-override hard-override-and-auto-lapse behavior (ADR-0004) —
/// this ticket's third priority-tested area. Covers: an override winning
/// outright over a better-ranked competitor, auto-lapse when the overridden
/// device disappears/expires/goes Unavailable, and the DND-suppressed case
/// that must NOT lapse the persisted override.
/// </summary>
public class DeviceRoutingPolicyTests
{
    static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    static TrackedDevicePresence Device(
        Guid deviceId,
        AvailabilityState availability = AvailabilityState.Available,
        bool dnd = false,
        IdleAgeBucket? idleAgeBucket = IdleAgeBucket.UnderTwoMinutes,
        DateTimeOffset? lastUpdatedAt = null,
        DateTimeOffset? expiresAt = null) => new()
        {
            DeviceId = deviceId,
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
    public void ResolveTarget_NoOverride_FallsThroughToRanking()
    {
        var deviceId = Guid.NewGuid();
        var device = Device(deviceId);

        var decision = DeviceRoutingPolicy.ResolveTarget(null, [device], InteractionKind.Voice, Now);

        Assert.False(decision.IsOverridden);
        Assert.False(decision.OverrideLapsed);
        Assert.Equal(deviceId, decision.TopDeviceId);
    }

    [Fact]
    public void ResolveTarget_OverrideDevice_WinsOutright_EvenIfNotTopRanked()
    {
        var laptop = Guid.NewGuid(); // best-ranked: available, fresh
        var phone = Guid.NewGuid();  // idle: would normally lose
        var bestRanked = Device(laptop, lastUpdatedAt: Now);
        var overridden = Device(phone, availability: AvailabilityState.Idle, idleAgeBucket: IdleAgeBucket.TwoToTenMinutes);

        var decision = DeviceRoutingPolicy.ResolveTarget(phone, [bestRanked, overridden], InteractionKind.Voice, Now);

        Assert.True(decision.IsOverridden);
        Assert.False(decision.OverrideLapsed);
        Assert.Equal(phone, decision.TopDeviceId);
        Assert.Single(decision.RankedDevices); // ranking of the rest isn't even computed
    }

    [Fact]
    public void ResolveTarget_OverrideDeviceUnavailable_AutoLapses_FallsThroughToRanking()
    {
        var deviceId = Guid.NewGuid();
        var unavailable = Device(deviceId, availability: AvailabilityState.Unavailable, idleAgeBucket: null);
        var other = Device(Guid.NewGuid());

        var decision = DeviceRoutingPolicy.ResolveTarget(deviceId, [unavailable, other], InteractionKind.Voice, Now);

        Assert.True(decision.OverrideLapsed);
        Assert.False(decision.IsOverridden);
        Assert.Equal(other.DeviceId, decision.TopDeviceId);
    }

    [Fact]
    public void ResolveTarget_OverrideDeviceExpired_AutoLapses()
    {
        var deviceId = Guid.NewGuid();
        var expired = Device(deviceId, expiresAt: Now.AddSeconds(-1));
        var other = Device(Guid.NewGuid());

        var decision = DeviceRoutingPolicy.ResolveTarget(deviceId, [expired, other], InteractionKind.Voice, Now);

        Assert.True(decision.OverrideLapsed);
        Assert.Equal(other.DeviceId, decision.TopDeviceId);
    }

    [Fact]
    public void ResolveTarget_OverrideDeviceNoLongerTracked_AutoLapses()
    {
        var overrideDeviceId = Guid.NewGuid(); // not in the device list at all
        var other = Device(Guid.NewGuid());

        var decision = DeviceRoutingPolicy.ResolveTarget(overrideDeviceId, [other], InteractionKind.Voice, Now);

        Assert.True(decision.OverrideLapsed);
        Assert.Equal(other.DeviceId, decision.TopDeviceId);
    }

    [Fact]
    public void ResolveTarget_OverrideDeviceDndSuppressedForVoice_IsNotALapse_FallsThroughForThisSendOnly()
    {
        var deviceId = Guid.NewGuid();
        var dndOverride = Device(deviceId, dnd: true);
        var other = Device(Guid.NewGuid());

        var decision = DeviceRoutingPolicy.ResolveTarget(deviceId, [dndOverride, other], InteractionKind.Voice, Now);

        Assert.False(decision.IsOverridden);
        Assert.False(decision.OverrideLapsed); // DND is transient, not "unavailable" — must not clear the persisted override
        Assert.Equal(other.DeviceId, decision.TopDeviceId);
    }

    [Fact]
    public void ResolveTarget_OverrideDeviceDndSuppressedForText_StillWinsOutright_TextIsNeverDndSuppressed()
    {
        var deviceId = Guid.NewGuid();
        var dndOverride = Device(deviceId, dnd: true);

        var decision = DeviceRoutingPolicy.ResolveTarget(deviceId, [dndOverride], InteractionKind.Text, Now);

        Assert.True(decision.IsOverridden);
        Assert.Equal(deviceId, decision.TopDeviceId);
    }

    [Fact]
    public void ResolveTarget_OverrideDeviceIdle_StillWinsOutright_OverrideIsHardNotRankingInput()
    {
        var deviceId = Guid.NewGuid();
        var idleOverride = Device(deviceId, availability: AvailabilityState.Idle, idleAgeBucket: IdleAgeBucket.OverTenMinutes);

        var decision = DeviceRoutingPolicy.ResolveTarget(deviceId, [idleOverride], InteractionKind.Voice, Now);

        Assert.True(decision.IsOverridden);
        Assert.Equal(deviceId, decision.TopDeviceId);
    }
}
