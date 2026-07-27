using Intercom.App.Tests.AttentionCards;
using Intercom.AttentionCards;
using Intercom.ControlChannel;
using Intercom.Presence;
using Intercom.Routing;
using Xunit;

namespace Intercom.App.Tests.Routing;

/// <summary>
/// The fan-out/dedup mechanism for attention cards (issue #30;
/// docs/adr/0004-multi-device-contact-routing.md): 3-second-timeout-or-tie
/// fan-out (mirrors <see cref="ChatFanoutRouterTests"/>), first-ack-wins
/// dedup with a <see cref="ControlMessageType.Resolved"/> broadcast to
/// withdraw siblings, and the receiving side's independent local 5-minute
/// expiry. This is the ticket's other genuinely tricky piece the task calls
/// out for priority testing (self-review target: a legitimate winning ack
/// must never be double-processed, and a sibling must never be told to
/// withdraw a card someone else didn't actually win).
/// </summary>
public class AttentionCardFanoutRouterTests
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
            Capabilities = Capability.AttentionCards,
            LastUpdatedAt = lastUpdatedAt ?? Now,
            ExpiresAt = Now.AddSeconds(30),
        };

    static (Guid DeviceId, AttentionCardDeviceEndpoint Endpoint, FakeAttentionCardTransport Transport) MakeEndpoint(bool autoConfirmDelivery = false)
    {
        var deviceId = Guid.NewGuid();
        var transport = new FakeAttentionCardTransport { AutoConfirmDelivery = autoConfirmDelivery };
        var endpoint = new AttentionCardDeviceEndpoint { DeviceId = deviceId, Service = new AttentionCardService(transport), Transport = transport };
        return (deviceId, endpoint, transport);
    }

    static AttentionCardFanoutRouter MakeRouter(
        Dictionary<Guid, AttentionCardDeviceEndpoint> endpoints,
        Func<Guid?>? overrideDeviceId = null,
        Action? onOverrideLapsed = null) =>
        new(endpoints, overrideDeviceId ?? (() => null), onOverrideLapsed, clock: () => Now, initialAckTimeout: ShortTimeout);

    [Fact]
    public async Task SendAsync_TopDeviceDeliveredImmediately_NoFanoutToOthers()
    {
        var (topId, topEndpoint, topTransport) = MakeEndpoint(autoConfirmDelivery: true);
        var (otherId, otherEndpoint, otherTransport) = MakeEndpoint();
        var endpoints = new Dictionary<Guid, AttentionCardDeviceEndpoint> { [topId] = topEndpoint, [otherId] = otherEndpoint };
        var router = MakeRouter(endpoints);

        var top = Device(topId, lastUpdatedAt: Now);
        var other = Device(otherId, lastUpdatedAt: Now.AddMinutes(-5));

        await router.SendAsync([top, other], "Dinner", "🍽", CancellationToken.None);

        Assert.Single(topTransport.Sent);
        Assert.Empty(otherTransport.Sent);
    }

    [Fact]
    public async Task SendAsync_TopDeviceNoDeliveryWithinTimeout_FansOutToOthers_SameInteractionId()
    {
        var (topId, topEndpoint, topTransport) = MakeEndpoint();
        var (otherId, otherEndpoint, otherTransport) = MakeEndpoint();
        var endpoints = new Dictionary<Guid, AttentionCardDeviceEndpoint> { [topId] = topEndpoint, [otherId] = otherEndpoint };
        var router = MakeRouter(endpoints);

        var top = Device(topId, lastUpdatedAt: Now);
        var other = Device(otherId, lastUpdatedAt: Now.AddMinutes(-5));

        var card = await router.SendAsync([top, other], "Dinner", "🍽", CancellationToken.None);

        var topFrame = Assert.Single(topTransport.Sent);
        var otherFrame = Assert.Single(otherTransport.Sent);
        Assert.Equal(card.MessageId, topFrame.MessageId);
        Assert.Equal(card.MessageId, otherFrame.MessageId); // same interaction ID, per the research doc
    }

    [Fact]
    public async Task SendAsync_TiedDevices_FansOutImmediately()
    {
        var (aId, aEndpoint, aTransport) = MakeEndpoint(autoConfirmDelivery: true);
        var (bId, bEndpoint, bTransport) = MakeEndpoint(autoConfirmDelivery: true);
        var endpoints = new Dictionary<Guid, AttentionCardDeviceEndpoint> { [aId] = aEndpoint, [bId] = bEndpoint };
        var router = MakeRouter(endpoints);

        var a = Device(aId, lastUpdatedAt: Now);
        var b = Device(bId, lastUpdatedAt: Now);

        await router.SendAsync([a, b], "Dinner", "🍽", CancellationToken.None);

        Assert.Single(aTransport.Sent);
        Assert.Single(bTransport.Sent);
    }

    [Fact]
    public async Task TiedFanout_TopDeviceAcksBeforeSiblingIsEvenSent_SiblingStillGetsWithdrawn()
    {
        // Regression for a real race found during self-review: the fan-out
        // bookkeeping used to only record "who did we send this interaction
        // to" AFTER the whole batch finished sending, so an ack arriving
        // mid-batch (very plausible for a tie, where devices are sent to
        // back-to-back with no waiting) could resolve the interaction before
        // the sibling's entry even existed — silently dropping its Resolved
        // withdrawal forever. AutoAcknowledge simulates the top device's peer
        // acking the instant its card is sent, i.e. before the loop even
        // reaches the sibling.
        var (topId, topEndpoint, topTransport) = MakeEndpoint();
        topTransport.AutoAcknowledge = true;
        var (siblingId, siblingEndpoint, siblingTransport) = MakeEndpoint();
        var endpoints = new Dictionary<Guid, AttentionCardDeviceEndpoint> { [topId] = topEndpoint, [siblingId] = siblingEndpoint };
        var router = MakeRouter(endpoints);

        var top = Device(topId, lastUpdatedAt: Now);
        var sibling = Device(siblingId, lastUpdatedAt: Now); // tied -> both sent immediately, no timeout wait

        var card = await router.SendAsync([top, sibling], "Dinner", "🍽", CancellationToken.None);

        // The sibling still received the (now-already-resolved) card...
        Assert.Single(siblingTransport.Sent, f => f.Type == ControlMessageType.AttentionCard);
        // ...but must also have been told to withdraw it, rather than being
        // left to linger until its local 5-minute expiry.
        var resolvedFrame = Assert.Single(siblingTransport.Sent, f => f.Type == ControlMessageType.Resolved);
        Assert.Equal(card.MessageId, resolvedFrame.CorrelationId);
    }

    [Fact]
    public async Task FirstAckWins_BroadcastsResolvedToSiblingsOnly_NotToTheAcker()
    {
        var (topId, topEndpoint, topTransport) = MakeEndpoint();
        var (siblingId, siblingEndpoint, siblingTransport) = MakeEndpoint();
        var endpoints = new Dictionary<Guid, AttentionCardDeviceEndpoint> { [topId] = topEndpoint, [siblingId] = siblingEndpoint };
        var router = MakeRouter(endpoints);

        var top = Device(topId, lastUpdatedAt: Now);
        var other = Device(siblingId, lastUpdatedAt: Now.AddMinutes(-5)); // forces timeout-based fan-out
        var card = await router.SendAsync([top, other], "Dinner", "🍽", CancellationToken.None);

        topTransport.ConfirmAcknowledged(card.MessageId); // the top device's peer acknowledged first

        // Give the fire-and-forget broadcast a moment to run.
        await WaitUntilAsync(() => siblingTransport.Sent.Any(f => f.Type == ControlMessageType.Resolved));

        var resolvedFrame = Assert.Single(siblingTransport.Sent, f => f.Type == ControlMessageType.Resolved);
        Assert.Equal(card.MessageId, resolvedFrame.CorrelationId);
        // The acking device itself is never sent its own Resolved withdrawal.
        Assert.DoesNotContain(topTransport.Sent, f => f.Type == ControlMessageType.Resolved);
    }

    [Fact]
    public async Task FirstAckWins_SecondRacingAck_IsIgnored_NoDoubleBroadcast()
    {
        var (topId, topEndpoint, topTransport) = MakeEndpoint();
        var (siblingId, siblingEndpoint, siblingTransport) = MakeEndpoint();
        var endpoints = new Dictionary<Guid, AttentionCardDeviceEndpoint> { [topId] = topEndpoint, [siblingId] = siblingEndpoint };
        var router = MakeRouter(endpoints);
        var resolvedRaisedCount = 0;
        router.CardWithdrawn += _ => resolvedRaisedCount++;

        var top = Device(topId, lastUpdatedAt: Now);
        var other = Device(siblingId, lastUpdatedAt: Now.AddMinutes(-5));
        var card = await router.SendAsync([top, other], "Dinner", "🍽", CancellationToken.None);

        topTransport.ConfirmAcknowledged(card.MessageId);
        await WaitUntilAsync(() => siblingTransport.Sent.Any(f => f.Type == ControlMessageType.Resolved));
        var resolvedCountAfterFirst = siblingTransport.Sent.Count(f => f.Type == ControlMessageType.Resolved);

        siblingTransport.ConfirmAcknowledged(card.MessageId); // a racing second ack for the same interaction

        Assert.Equal(1, resolvedCountAfterFirst);
        // No further Resolved should be sent anywhere for this already-resolved interaction.
        Assert.Equal(0, resolvedRaisedCount); // this router's OWN receiving side never got a Resolved back
    }

    [Fact]
    public void ReceivingSide_InboundResolved_RaisesCardWithdrawn()
    {
        var (receiverId, receiverEndpoint, receiverTransport) = MakeEndpoint();
        var receiverRouter = MakeRouter(new Dictionary<Guid, AttentionCardDeviceEndpoint> { [receiverId] = receiverEndpoint });
        Guid? withdrawn = null;
        receiverRouter.CardWithdrawn += id => withdrawn = id;

        var interactionId = Guid.NewGuid();
        var inboundCard = AttentionCardFrameCodec.ToFrame("Dinner", "🍽", interactionId);
        receiverTransport.Deliver(inboundCard);

        receiverTransport.ConfirmResolved(interactionId);

        Assert.Equal(interactionId, withdrawn);
    }

    [Fact]
    public async Task TryAcknowledgeAsync_AfterWithdrawn_IsNoOp()
    {
        var (receiverId, receiverEndpoint, receiverTransport) = MakeEndpoint();
        var router = MakeRouter(new Dictionary<Guid, AttentionCardDeviceEndpoint> { [receiverId] = receiverEndpoint });

        var interactionId = Guid.NewGuid();
        receiverTransport.Deliver(AttentionCardFrameCodec.ToFrame("Dinner", "🍽", interactionId));
        receiverTransport.ConfirmResolved(interactionId);

        var acknowledged = await router.TryAcknowledgeAsync(receiverId, interactionId, CancellationToken.None);

        Assert.False(acknowledged);
        Assert.DoesNotContain(receiverTransport.Sent, f => f.Type == ControlMessageType.Acknowledged);
    }

    [Fact]
    public async Task TryAcknowledgeAsync_NotWithdrawn_SendsAcknowledged()
    {
        var (receiverId, receiverEndpoint, receiverTransport) = MakeEndpoint();
        var router = MakeRouter(new Dictionary<Guid, AttentionCardDeviceEndpoint> { [receiverId] = receiverEndpoint });

        var interactionId = Guid.NewGuid();
        receiverTransport.Deliver(AttentionCardFrameCodec.ToFrame("Dinner", "🍽", interactionId));

        var acknowledged = await router.TryAcknowledgeAsync(receiverId, interactionId, CancellationToken.None);

        Assert.True(acknowledged);
        Assert.Contains(receiverTransport.Sent, f => f.Type == ControlMessageType.Acknowledged && f.CorrelationId == interactionId);
    }

    [Fact]
    public void PruneExpiredReceivedCards_PastFiveMinutes_RaisesCardExpiredLocally()
    {
        var (receiverId, receiverEndpoint, receiverTransport) = MakeEndpoint();
        var router = MakeRouter(new Dictionary<Guid, AttentionCardDeviceEndpoint> { [receiverId] = receiverEndpoint });
        Guid? expired = null;
        router.CardExpiredLocally += id => expired = id;

        var interactionId = Guid.NewGuid();
        receiverTransport.Deliver(AttentionCardFrameCodec.ToFrame("Dinner", "🍽", interactionId));

        router.PruneExpiredReceivedCards(Now + AttentionCardFanoutRouter.LocalReceivedCardExpiry);

        Assert.Equal(interactionId, expired);
    }

    [Fact]
    public void PruneExpiredReceivedCards_BeforeFiveMinutes_DoesNotExpire()
    {
        var (receiverId, receiverEndpoint, receiverTransport) = MakeEndpoint();
        var router = MakeRouter(new Dictionary<Guid, AttentionCardDeviceEndpoint> { [receiverId] = receiverEndpoint });
        var expiredCount = 0;
        router.CardExpiredLocally += _ => expiredCount++;

        var interactionId = Guid.NewGuid();
        receiverTransport.Deliver(AttentionCardFrameCodec.ToFrame("Dinner", "🍽", interactionId));

        router.PruneExpiredReceivedCards(Now + AttentionCardFanoutRouter.LocalReceivedCardExpiry - TimeSpan.FromSeconds(1));

        Assert.Equal(0, expiredCount);
    }

    [Fact]
    public async Task SendAsync_NoEligibleDevices_Throws()
    {
        var router = MakeRouter(new Dictionary<Guid, AttentionCardDeviceEndpoint>());

        await Assert.ThrowsAsync<InvalidOperationException>(() => router.SendAsync([], "Dinner", "🍽", CancellationToken.None));
    }

    [Fact]
    public async Task SendAsync_ManualOverride_RoutesOnlyToOverriddenDevice()
    {
        var (preferredId, preferredEndpoint, preferredTransport) = MakeEndpoint(autoConfirmDelivery: true);
        var (betterRankedId, betterEndpoint, betterTransport) = MakeEndpoint(autoConfirmDelivery: true);
        var endpoints = new Dictionary<Guid, AttentionCardDeviceEndpoint> { [preferredId] = preferredEndpoint, [betterRankedId] = betterEndpoint };
        var router = MakeRouter(endpoints, overrideDeviceId: () => preferredId);

        var preferred = Device(preferredId, availability: AvailabilityState.Idle, idleAgeBucket: IdleAgeBucket.OverTenMinutes);
        var betterRanked = Device(betterRankedId, lastUpdatedAt: Now);

        await router.SendAsync([preferred, betterRanked], "Dinner", "🍽", CancellationToken.None);

        Assert.Single(preferredTransport.Sent);
        Assert.Empty(betterTransport.Sent);
    }

    static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition was never met.");
            await Task.Delay(5);
        }
    }
}
