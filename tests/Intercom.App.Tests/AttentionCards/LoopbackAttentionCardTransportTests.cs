using Intercom.AttentionCards;
using Xunit;

namespace Intercom.App.Tests.AttentionCards;

/// <summary>
/// Exercises the WinUI shelf/composer's demo transport at the
/// <see cref="AttentionCardService"/> level — confirms it drives real
/// <c>FrameDispatcher</c> production code (real mechanical Delivered
/// receipts, real explicit Acknowledged receipts), not a faked shortcut.
/// Mirrors <c>LoopbackChatTransportTests</c>.
/// </summary>
public class LoopbackAttentionCardTransportTests
{
    [Fact]
    public async Task SendFromAToB_ArrivesAtB_AndAGetsARealDeliveredReceipt()
    {
        var (a, b) = LoopbackAttentionCardTransport.CreatePair();
        var serviceA = new AttentionCardService(a);
        var serviceB = new AttentionCardService(b);
        AttentionCard? receivedByB = null;
        serviceB.CardReceived += c => receivedByB = c;

        var sent = await serviceA.SendAsync("Dinner's ready", "🍽️", CancellationToken.None);

        Assert.NotNull(receivedByB);
        Assert.Equal("Dinner's ready", receivedByB!.Purpose);
        Assert.Equal(AttentionCardDeliveryState.Delivered, serviceA.Conversation.Cards.Single(c => c.MessageId == sent.MessageId).DeliveryState);
    }

    [Fact]
    public async Task BAcknowledgesReceivedCard_AGetsARealAcknowledgedReceipt()
    {
        var (a, b) = LoopbackAttentionCardTransport.CreatePair();
        var serviceA = new AttentionCardService(a);
        var serviceB = new AttentionCardService(b);
        AttentionCard? receivedByB = null;
        serviceB.CardReceived += c => receivedByB = c;
        var sent = await serviceA.SendAsync("Package arrived", "📦", CancellationToken.None);

        await serviceB.AcknowledgeAsync(receivedByB!.MessageId, CancellationToken.None);

        Assert.Equal(AttentionCardAckState.Acknowledged, serviceA.Conversation.Cards.Single(c => c.MessageId == sent.MessageId).AckState);
        Assert.Equal(AttentionCardAckState.Acknowledged, serviceB.Conversation.Cards.Single().AckState);
    }

    [Fact]
    public async Task SimulateDrop_MarksPendingCardsUndelivered()
    {
        var (a, _) = LoopbackAttentionCardTransport.CreatePair();
        var serviceA = new AttentionCardService(a);

        // Peer is unlinked so the send never gets a Delivered receipt back —
        // simulating a card in flight when the connection drops.
        a.Peer = null;
        await serviceA.SendAsync("in flight", "🍽️", CancellationToken.None);

        a.SimulateDrop();

        Assert.Equal(AttentionCardDeliveryState.Undelivered, serviceA.Conversation.Cards.Single().DeliveryState);
    }

    [Fact]
    public async Task ResolvedReceiptRaisesOnlyWhenItHasACorrelationId()
    {
        var (a, b) = LoopbackAttentionCardTransport.CreatePair();
        var resolved = new List<Guid>();
        a.Resolved += resolved.Add;
        var correlationId = Guid.NewGuid();

        await b.SendAsync(new Intercom.ControlChannel.ControlFrame
        {
            Type = Intercom.ControlChannel.ControlMessageType.Resolved,
            MessageId = Guid.NewGuid(),
            CorrelationId = correlationId,
            Payload = [],
        }, CancellationToken.None);
        await b.SendAsync(new Intercom.ControlChannel.ControlFrame
        {
            Type = Intercom.ControlChannel.ControlMessageType.Resolved,
            MessageId = Guid.NewGuid(),
            Payload = [],
        }, CancellationToken.None);

        Assert.Equal([correlationId], resolved);
    }
}
