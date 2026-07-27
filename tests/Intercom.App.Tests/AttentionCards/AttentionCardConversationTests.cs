using Intercom.AttentionCards;
using Xunit;

namespace Intercom.App.Tests.AttentionCards;

public class AttentionCardConversationTests
{
    [Fact]
    public void AddOutbound_StartsPendingAndNotAcknowledged()
    {
        var conversation = new AttentionCardConversation();
        var messageId = Guid.NewGuid();

        var card = conversation.AddOutbound(messageId, "Dinner's ready", "🍽️");

        Assert.Equal(AttentionCardDeliveryState.Pending, card.DeliveryState);
        Assert.Equal(AttentionCardAckState.NotAcknowledged, card.AckState);
        Assert.Equal(AttentionCardDirection.Sent, card.Direction);
        Assert.Same(card, Assert.Single(conversation.Cards));
    }

    [Fact]
    public void AddInbound_IsImmediatelyDeliveredButNotAcknowledged()
    {
        var conversation = new AttentionCardConversation();

        var card = conversation.AddInbound(Guid.NewGuid(), "Get the door?", "🚪");

        Assert.Equal(AttentionCardDeliveryState.Delivered, card.DeliveryState);
        Assert.Equal(AttentionCardAckState.NotAcknowledged, card.AckState);
        Assert.Equal(AttentionCardDirection.Received, card.Direction);
    }

    [Fact]
    public void MarkDelivered_TransitionsPendingSentCard()
    {
        var conversation = new AttentionCardConversation();
        var messageId = Guid.NewGuid();
        conversation.AddOutbound(messageId, "hello", "🍽️");
        AttentionCard? updated = null;
        conversation.CardUpdated += c => updated = c;

        conversation.MarkDelivered(messageId);

        Assert.NotNull(updated);
        Assert.Equal(AttentionCardDeliveryState.Delivered, updated!.DeliveryState);
    }

    [Fact]
    public void MarkAllPendingUndelivered_OnlyAffectsPendingSentCards()
    {
        var conversation = new AttentionCardConversation();
        var deliveredId = Guid.NewGuid();
        var pendingId = Guid.NewGuid();
        conversation.AddOutbound(deliveredId, "already delivered", "🍽️");
        conversation.MarkDelivered(deliveredId);
        conversation.AddOutbound(pendingId, "in flight", "🚪");
        conversation.AddInbound(Guid.NewGuid(), "from peer", "📦"); // must be untouched

        conversation.MarkAllPendingUndelivered();

        var byId = conversation.Cards.ToDictionary(c => c.MessageId);
        Assert.Equal(AttentionCardDeliveryState.Delivered, byId[deliveredId].DeliveryState);
        Assert.Equal(AttentionCardDeliveryState.Undelivered, byId[pendingId].DeliveryState);
    }

    [Fact]
    public void MarkAcknowledged_OnReceivedCard_TransitionsAndReturnsCard()
    {
        var conversation = new AttentionCardConversation();
        var messageId = Guid.NewGuid();
        conversation.AddInbound(messageId, "Package arrived", "📦");
        AttentionCard? updated = null;
        conversation.CardUpdated += c => updated = c;

        var result = conversation.MarkAcknowledged(messageId);

        Assert.NotNull(result);
        Assert.Equal(AttentionCardAckState.Acknowledged, result!.AckState);
        Assert.Same(result, updated);
        Assert.Equal(AttentionCardAckState.Acknowledged, conversation.Cards.Single().AckState);
    }

    [Fact]
    public void MarkAcknowledged_OnSentCard_TransitionsWhenPeerAcksBack()
    {
        // A card THIS side sent gets acknowledged when the peer's human acts
        // on it — the delivery state is independent of ack state.
        var conversation = new AttentionCardConversation();
        var messageId = Guid.NewGuid();
        conversation.AddOutbound(messageId, "Call when free", "☎️");
        conversation.MarkDelivered(messageId);

        var result = conversation.MarkAcknowledged(messageId);

        Assert.NotNull(result);
        Assert.Equal(AttentionCardAckState.Acknowledged, result!.AckState);
        Assert.Equal(AttentionCardDeliveryState.Delivered, result.DeliveryState);
    }

    [Fact]
    public void MarkAcknowledged_AlreadyAcknowledged_IsIdempotentNoOp()
    {
        var conversation = new AttentionCardConversation();
        var messageId = Guid.NewGuid();
        conversation.AddInbound(messageId, "Need a hand", "❤️");
        conversation.MarkAcknowledged(messageId);
        var updates = new List<AttentionCard>();
        conversation.CardUpdated += updates.Add;

        var second = conversation.MarkAcknowledged(messageId);

        Assert.Null(second);
        Assert.Empty(updates);
    }

    [Fact]
    public void MarkAcknowledged_UnknownMessageId_IsNoOp()
    {
        var conversation = new AttentionCardConversation();
        conversation.AddInbound(Guid.NewGuid(), "hi", "🍽️");
        var updates = new List<AttentionCard>();
        conversation.CardUpdated += updates.Add;

        var result = conversation.MarkAcknowledged(Guid.NewGuid());

        Assert.Null(result);
        Assert.Empty(updates);
    }

    [Fact]
    public void Cards_ReturnsSnapshot_NotLiveView()
    {
        var conversation = new AttentionCardConversation();
        conversation.AddOutbound(Guid.NewGuid(), "one", "🍽️");
        var snapshot = conversation.Cards;

        conversation.AddOutbound(Guid.NewGuid(), "two", "🚪");

        Assert.Single(snapshot);
        Assert.Equal(2, conversation.Cards.Count);
    }
}
