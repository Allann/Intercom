using Intercom.AttentionCards;
using Intercom.ControlChannel;
using Xunit;

namespace Intercom.App.Tests.AttentionCards;

public class AttentionCardServiceTests
{
    [Fact]
    public async Task SendAsync_AddsPendingOutboundCard_AndSendsRealAttentionCardFrame()
    {
        var transport = new FakeAttentionCardTransport();
        var service = new AttentionCardService(transport);

        var card = await service.SendAsync("Dinner's ready", "🍽️", CancellationToken.None);

        Assert.Equal(AttentionCardDeliveryState.Pending, card.DeliveryState);
        Assert.Equal(AttentionCardAckState.NotAcknowledged, card.AckState);
        var sent = Assert.Single(transport.Sent);
        Assert.Equal(ControlMessageType.AttentionCard, sent.Type);
        var (purpose, icon) = AttentionCardFrameCodec.Decode(sent);
        Assert.Equal("Dinner's ready", purpose);
        Assert.Equal("🍽️", icon);
    }

    [Fact]
    public async Task SendAsync_TransportThrows_MarksUndeliveredAndRethrows()
    {
        var transport = new FakeAttentionCardTransport { FailSendWith = new InvalidOperationException("not connected") };
        var service = new AttentionCardService(transport);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendAsync("hi", "🍽️", CancellationToken.None));

        Assert.Equal(AttentionCardDeliveryState.Undelivered, service.Conversation.Cards.Single().DeliveryState);
    }

    [Fact]
    public async Task DeliveryConfirmed_FromTransport_MarksMatchingCardDelivered()
    {
        var transport = new FakeAttentionCardTransport();
        var service = new AttentionCardService(transport);
        var sent = await service.SendAsync("hi", "🍽️", CancellationToken.None);

        transport.ConfirmDelivery(sent.MessageId);

        Assert.Equal(AttentionCardDeliveryState.Delivered, service.Conversation.Cards.Single().DeliveryState);
    }

    [Fact]
    public async Task ConnectionDropped_MarksAllPendingUndelivered()
    {
        var transport = new FakeAttentionCardTransport();
        var service = new AttentionCardService(transport);
        await service.SendAsync("in flight", "🍽️", CancellationToken.None);

        transport.Drop();

        Assert.Equal(AttentionCardDeliveryState.Undelivered, service.Conversation.Cards.Single().DeliveryState);
    }

    [Fact]
    public void FrameReceived_ValidAttentionCardFrame_AddsInboundCardAndRaisesEvent()
    {
        var transport = new FakeAttentionCardTransport();
        var service = new AttentionCardService(transport);
        AttentionCard? raised = null;
        service.CardReceived += c => raised = c;

        var frame = AttentionCardFrameCodec.ToFrame("Get the door?", "🚪", Guid.NewGuid());
        transport.Deliver(frame);

        Assert.NotNull(raised);
        Assert.Equal("Get the door?", raised!.Purpose);
        Assert.Equal("🚪", raised.Icon);
        Assert.Equal(AttentionCardDirection.Received, raised.Direction);
        Assert.Equal(AttentionCardDeliveryState.Delivered, raised.DeliveryState);
        Assert.Equal(AttentionCardAckState.NotAcknowledged, raised.AckState);
        Assert.Same(raised, service.Conversation.Cards.Single());
    }

    [Fact]
    public void FrameReceived_MalformedAttentionCardFrame_IsDroppedSilently()
    {
        var transport = new FakeAttentionCardTransport();
        var service = new AttentionCardService(transport);
        var raisedCount = 0;
        service.CardReceived += _ => raisedCount++;

        var malformed = new ControlFrame
        {
            Type = ControlMessageType.AttentionCard,
            MessageId = Guid.NewGuid(),
            Payload = [1, 2], // too short for the length prefix
        };
        transport.Deliver(malformed);

        Assert.Equal(0, raisedCount);
        Assert.Empty(service.Conversation.Cards);
    }

    [Fact]
    public async Task AcknowledgeAsync_OnReceivedCard_MarksAcknowledgedAndSendsAcknowledgedFrame()
    {
        var transport = new FakeAttentionCardTransport();
        var service = new AttentionCardService(transport);
        var frame = AttentionCardFrameCodec.ToFrame("Package arrived", "📦", Guid.NewGuid());
        transport.Deliver(frame);

        await service.AcknowledgeAsync(frame.MessageId, CancellationToken.None);

        Assert.Equal(AttentionCardAckState.Acknowledged, service.Conversation.Cards.Single().AckState);
        var sent = Assert.Single(transport.Sent);
        Assert.Equal(ControlMessageType.Acknowledged, sent.Type);
        Assert.Equal(frame.MessageId, sent.CorrelationId);
    }

    [Fact]
    public async Task AcknowledgeAsync_CalledTwice_SendsExactlyOneAcknowledgedFrame()
    {
        // Acceptance criterion: "...an Acknowledged receipt only on explicit
        // human action" — a second click (double-tap, re-entrant call) must
        // not produce a second wire frame.
        var transport = new FakeAttentionCardTransport();
        var service = new AttentionCardService(transport);
        var frame = AttentionCardFrameCodec.ToFrame("Call when free", "☎️", Guid.NewGuid());
        transport.Deliver(frame);

        await service.AcknowledgeAsync(frame.MessageId, CancellationToken.None);
        await service.AcknowledgeAsync(frame.MessageId, CancellationToken.None);

        Assert.Single(transport.Sent);
    }

    [Fact]
    public async Task AcknowledgeAsync_OnOwnSentCard_IsNoOp_NeverSendsAcknowledgedFrame()
    {
        // Defensive: a card THIS side sent is never something the local
        // human acknowledges (the UI never offers that affordance) — must
        // not flip local AckState or send a frame if called anyway.
        var transport = new FakeAttentionCardTransport();
        var service = new AttentionCardService(transport);
        var sent = await service.SendAsync("Dinner's ready", "🍽️", CancellationToken.None);

        await service.AcknowledgeAsync(sent.MessageId, CancellationToken.None);

        Assert.Equal(AttentionCardAckState.NotAcknowledged, service.Conversation.Cards.Single().AckState);
        Assert.Single(transport.Sent); // only the original card send, no Acknowledged frame
    }

    [Fact]
    public async Task AcknowledgeAsync_UnknownCard_IsNoOp()
    {
        var transport = new FakeAttentionCardTransport();
        var service = new AttentionCardService(transport);

        await service.AcknowledgeAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Empty(transport.Sent);
    }

    [Fact]
    public async Task Acknowledged_FromTransport_MarksMatchingSentCardAcknowledged()
    {
        // The peer's human acted on a card THIS side sent.
        var transport = new FakeAttentionCardTransport();
        var service = new AttentionCardService(transport);
        var sent = await service.SendAsync("Need a hand", "❤️", CancellationToken.None);

        transport.ConfirmAcknowledged(sent.MessageId);

        Assert.Equal(AttentionCardAckState.Acknowledged, service.Conversation.Cards.Single().AckState);
    }
}
