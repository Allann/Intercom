using Intercom.App.Tests.Chat;
using Intercom.Chat;
using Intercom.ControlChannel;
using Intercom.Presence;
using Intercom.Routing;
using Xunit;

namespace Intercom.App.Tests.Routing;

/// <summary>
/// The 3-second-timeout-or-tie fan-out trigger (ADR-0004) for chat — this
/// ticket's fourth priority-tested area. Uses <see cref="FakeChatTransport"/>'s
/// <c>AutoConfirmDelivery</c> for the "delivered immediately, no fan-out"
/// case (a deterministic synchronous confirmation, not a real wait), and a
/// short injected <c>initialAckTimeout</c> for the "times out, fans out"
/// case so the test waits on the real elapsed timer without needing the
/// full 3 seconds — the actual side effect under test (the fan-out send)
/// is awaited directly from <see cref="ChatFanoutRouter.SendAsync"/>'s own
/// returned Task, never polled.
/// </summary>
public class ChatFanoutRouterTests
{
    static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(60);

    static TrackedDevicePresence Device(
        Guid deviceId,
        AvailabilityState availability = AvailabilityState.Available,
        IdleAgeBucket? idleAgeBucket = IdleAgeBucket.UnderTwoMinutes,
        DateTimeOffset? lastUpdatedAt = null) => new()
        {
            DeviceId = deviceId,
            ContactId = Guid.NewGuid(),
            IncarnationId = Guid.NewGuid(),
            Sequence = 1,
            Availability = availability,
            Dnd = false,
            IdleAgeBucket = idleAgeBucket,
            Capabilities = Capability.Text,
            LastUpdatedAt = lastUpdatedAt ?? Now,
            ExpiresAt = Now.AddSeconds(30),
        };

    static (Guid DeviceId, ChatDeviceEndpoint Endpoint, FakeChatTransport Transport) MakeEndpoint(bool autoConfirmDelivery = false)
    {
        var deviceId = Guid.NewGuid();
        var transport = new FakeChatTransport { AutoConfirmDelivery = autoConfirmDelivery };
        var endpoint = new ChatDeviceEndpoint { DeviceId = deviceId, Service = new ChatService(transport) };
        return (deviceId, endpoint, transport);
    }

    [Fact]
    public async Task SendAsync_TopDeviceDeliveredImmediately_NoFanoutToOthers()
    {
        var (topId, topEndpoint, topTransport) = MakeEndpoint(autoConfirmDelivery: true);
        var (otherId, otherEndpoint, otherTransport) = MakeEndpoint();
        var endpoints = new Dictionary<Guid, ChatDeviceEndpoint> { [topId] = topEndpoint, [otherId] = otherEndpoint };
        var router = new ChatFanoutRouter(endpoints, () => null, clock: () => Now, initialAckTimeout: ShortTimeout);

        var top = Device(topId, lastUpdatedAt: Now);
        var other = Device(otherId, lastUpdatedAt: Now.AddMinutes(-5)); // clearly not tied with top

        await router.SendAsync([top, other], "hello", CancellationToken.None);

        Assert.Single(topTransport.Sent);
        Assert.Empty(otherTransport.Sent);
    }

    [Fact]
    public async Task SendAsync_TopDeviceNoDeliveryWithinTimeout_FansOutToOthers()
    {
        var (topId, topEndpoint, topTransport) = MakeEndpoint(); // never confirms delivery
        var (otherId, otherEndpoint, otherTransport) = MakeEndpoint();
        var endpoints = new Dictionary<Guid, ChatDeviceEndpoint> { [topId] = topEndpoint, [otherId] = otherEndpoint };
        var router = new ChatFanoutRouter(endpoints, () => null, clock: () => Now, initialAckTimeout: ShortTimeout);

        var top = Device(topId, lastUpdatedAt: Now);
        var other = Device(otherId, lastUpdatedAt: Now.AddMinutes(-5));

        await router.SendAsync([top, other], "hello", CancellationToken.None);

        Assert.Single(topTransport.Sent);
        Assert.Single(otherTransport.Sent);
    }

    [Fact]
    public async Task SendAsync_TiedDevices_FansOutImmediately_RegardlessOfDelivery()
    {
        var (aId, aEndpoint, aTransport) = MakeEndpoint(autoConfirmDelivery: true);
        var (bId, bEndpoint, bTransport) = MakeEndpoint(autoConfirmDelivery: true);
        var endpoints = new Dictionary<Guid, ChatDeviceEndpoint> { [aId] = aEndpoint, [bId] = bEndpoint };
        var router = new ChatFanoutRouter(endpoints, () => null, clock: () => Now, initialAckTimeout: ShortTimeout);

        // Both devices share availability/idle bucket and are within the
        // recency tie window -> DeviceRanker reports IsTie.
        var a = Device(aId, lastUpdatedAt: Now);
        var b = Device(bId, lastUpdatedAt: Now);

        await router.SendAsync([a, b], "hello", CancellationToken.None);

        Assert.Single(aTransport.Sent);
        Assert.Single(bTransport.Sent);
    }

    [Fact]
    public async Task SendAsync_SingleEligibleDevice_NoOthersToFanoutTo_NoWaitNeeded()
    {
        var (topId, topEndpoint, topTransport) = MakeEndpoint(); // never confirms, but there's nobody else to fan out to
        var endpoints = new Dictionary<Guid, ChatDeviceEndpoint> { [topId] = topEndpoint };
        var router = new ChatFanoutRouter(endpoints, () => null, clock: () => Now, initialAckTimeout: ShortTimeout);

        var top = Device(topId);

        await router.SendAsync([top], "hello", CancellationToken.None);

        Assert.Single(topTransport.Sent);
    }

    [Fact]
    public async Task SendAsync_NoEligibleDevices_Throws()
    {
        var router = new ChatFanoutRouter(new Dictionary<Guid, ChatDeviceEndpoint>(), () => null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => router.SendAsync([], "hello", CancellationToken.None));
    }

    [Fact]
    public async Task SendAsync_ManualOverride_RoutesOnlyToOverriddenDevice_EvenIfOtherRanksHigher()
    {
        var (preferredId, preferredEndpoint, preferredTransport) = MakeEndpoint(autoConfirmDelivery: true);
        var (betterRankedId, betterEndpoint, betterTransport) = MakeEndpoint(autoConfirmDelivery: true);
        var endpoints = new Dictionary<Guid, ChatDeviceEndpoint> { [preferredId] = preferredEndpoint, [betterRankedId] = betterEndpoint };
        var router = new ChatFanoutRouter(endpoints, () => preferredId, clock: () => Now, initialAckTimeout: ShortTimeout);

        var preferred = Device(preferredId, availability: AvailabilityState.Idle, idleAgeBucket: IdleAgeBucket.OverTenMinutes);
        var betterRanked = Device(betterRankedId, lastUpdatedAt: Now);

        await router.SendAsync([preferred, betterRanked], "hello", CancellationToken.None);

        Assert.Single(preferredTransport.Sent);
        Assert.Empty(betterTransport.Sent);
    }

    [Fact]
    public async Task SendAsync_OverrideDeviceUnavailable_LapsesAndInvokesCallback_RoutesNormally()
    {
        var (overrideId, overrideEndpoint, _) = MakeEndpoint();
        var (otherId, otherEndpoint, otherTransport) = MakeEndpoint();
        var endpoints = new Dictionary<Guid, ChatDeviceEndpoint> { [overrideId] = overrideEndpoint, [otherId] = otherEndpoint };
        var lapsed = false;
        var router = new ChatFanoutRouter(endpoints, () => overrideId, onOverrideLapsed: () => lapsed = true, clock: () => Now, initialAckTimeout: ShortTimeout);

        var unavailableOverride = Device(overrideId, availability: AvailabilityState.Unavailable, idleAgeBucket: null);
        var other = Device(otherId);

        await router.SendAsync([unavailableOverride, other], "hello", CancellationToken.None);

        Assert.True(lapsed);
        Assert.Single(otherTransport.Sent);
    }
}
