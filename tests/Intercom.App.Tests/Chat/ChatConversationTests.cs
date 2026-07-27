using Intercom.Chat;
using Xunit;

namespace Intercom.App.Tests.Chat;

public class ChatConversationTests
{
    [Fact]
    public void AddOutbound_StartsPending()
    {
        var conversation = new ChatConversation();
        var messageId = Guid.NewGuid();

        var message = conversation.AddOutbound(messageId, "hello");

        Assert.Equal(ChatDeliveryState.Pending, message.DeliveryState);
        Assert.Equal(ChatMessageDirection.Sent, message.Direction);
        Assert.Same(message, Assert.Single(conversation.Messages));
    }

    [Fact]
    public void AddInbound_IsImmediatelyDelivered()
    {
        var conversation = new ChatConversation();

        var message = conversation.AddInbound(Guid.NewGuid(), "hi back");

        Assert.Equal(ChatDeliveryState.Delivered, message.DeliveryState);
        Assert.Equal(ChatMessageDirection.Received, message.Direction);
    }

    [Fact]
    public void MarkDelivered_TransitionsPendingSentMessage()
    {
        var conversation = new ChatConversation();
        var messageId = Guid.NewGuid();
        conversation.AddOutbound(messageId, "hello");
        ChatMessage? updated = null;
        conversation.MessageUpdated += m => updated = m;

        conversation.MarkDelivered(messageId);

        Assert.NotNull(updated);
        Assert.Equal(ChatDeliveryState.Delivered, updated!.DeliveryState);
        Assert.Equal(ChatDeliveryState.Delivered, conversation.Messages.Single().DeliveryState);
    }

    [Fact]
    public void MarkDelivered_UnknownMessageId_IsNoOp()
    {
        var conversation = new ChatConversation();
        conversation.AddOutbound(Guid.NewGuid(), "hello");
        var updates = new List<ChatMessage>();
        conversation.MessageUpdated += updates.Add;

        conversation.MarkDelivered(Guid.NewGuid());

        Assert.Empty(updates);
    }

    [Fact]
    public void MarkDelivered_AlreadyUndelivered_DoesNotFlipBack()
    {
        // Regression guard for a late Delivered receipt racing a drop that
        // already resolved the message — ADR-0001's undelivered state must
        // be terminal, never silently overwritten by a stray late receipt.
        var conversation = new ChatConversation();
        var messageId = Guid.NewGuid();
        conversation.AddOutbound(messageId, "hello");
        conversation.MarkUndelivered(messageId);
        var updates = new List<ChatMessage>();
        conversation.MessageUpdated += updates.Add;

        conversation.MarkDelivered(messageId);

        Assert.Empty(updates);
        Assert.Equal(ChatDeliveryState.Undelivered, conversation.Messages.Single().DeliveryState);
    }

    [Fact]
    public void MarkAllPendingUndelivered_OnlyAffectsPendingSentMessages()
    {
        var conversation = new ChatConversation();
        var deliveredId = Guid.NewGuid();
        var pendingId1 = Guid.NewGuid();
        var pendingId2 = Guid.NewGuid();

        conversation.AddOutbound(deliveredId, "already delivered");
        conversation.MarkDelivered(deliveredId);
        conversation.AddOutbound(pendingId1, "in flight 1");
        conversation.AddOutbound(pendingId2, "in flight 2");
        conversation.AddInbound(Guid.NewGuid(), "from peer"); // must be untouched

        conversation.MarkAllPendingUndelivered();

        var byId = conversation.Messages.ToDictionary(m => m.MessageId);
        Assert.Equal(ChatDeliveryState.Delivered, byId[deliveredId].DeliveryState);
        Assert.Equal(ChatDeliveryState.Undelivered, byId[pendingId1].DeliveryState);
        Assert.Equal(ChatDeliveryState.Undelivered, byId[pendingId2].DeliveryState);
    }

    [Fact]
    public void MarkAllPendingUndelivered_NoPendingMessages_RaisesNoEvents()
    {
        var conversation = new ChatConversation();
        conversation.AddInbound(Guid.NewGuid(), "hi");
        var updates = new List<ChatMessage>();
        conversation.MessageUpdated += updates.Add;

        conversation.MarkAllPendingUndelivered();

        Assert.Empty(updates);
    }

    [Fact]
    public void Messages_ReturnsSnapshot_NotLiveView()
    {
        var conversation = new ChatConversation();
        conversation.AddOutbound(Guid.NewGuid(), "one");
        var snapshot = conversation.Messages;

        conversation.AddOutbound(Guid.NewGuid(), "two");

        Assert.Single(snapshot);
        Assert.Equal(2, conversation.Messages.Count);
    }
}
