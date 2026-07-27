using Intercom.Chat;
using Intercom.ControlChannel;
using Xunit;

namespace Intercom.App.Tests.Chat;

public class ChatServiceTests
{
    [Fact]
    public async Task SendAsync_AddsPendingOutboundMessage_AndSendsRealChatFrame()
    {
        var transport = new FakeChatTransport();
        var service = new ChatService(transport);

        var message = await service.SendAsync("Dinner's ready!", CancellationToken.None);

        Assert.Equal(ChatDeliveryState.Pending, message.DeliveryState);
        var sent = Assert.Single(transport.Sent);
        Assert.Equal(ControlMessageType.Chat, sent.Type);
        Assert.Equal("Dinner's ready!", ChatFrameCodec.Decode(sent));
        Assert.Equal(message.MessageId, sent.MessageId);
    }

    [Fact]
    public async Task SendAsync_TransportThrows_MarksUndeliveredAndRethrows()
    {
        var transport = new FakeChatTransport { FailSendWith = new InvalidOperationException("not connected") };
        var service = new ChatService(transport);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendAsync("hi", CancellationToken.None));

        Assert.Equal(ChatDeliveryState.Undelivered, service.Conversation.Messages.Single().DeliveryState);
    }

    [Fact]
    public async Task DeliveryConfirmed_FromTransport_MarksMatchingMessageDelivered()
    {
        var transport = new FakeChatTransport();
        var service = new ChatService(transport);
        var sent = await service.SendAsync("hi", CancellationToken.None);

        transport.ConfirmDelivery(sent.MessageId);

        Assert.Equal(ChatDeliveryState.Delivered, service.Conversation.Messages.Single().DeliveryState);
    }

    [Fact]
    public async Task ConnectionDropped_MarksAllPendingUndelivered()
    {
        var transport = new FakeChatTransport();
        var service = new ChatService(transport);
        await service.SendAsync("in flight", CancellationToken.None);

        transport.Drop();

        Assert.Equal(ChatDeliveryState.Undelivered, service.Conversation.Messages.Single().DeliveryState);
    }

    [Fact]
    public void FrameReceived_ValidChatFrame_AddsInboundMessageAndRaisesEvent()
    {
        var transport = new FakeChatTransport();
        var service = new ChatService(transport);
        ChatMessage? raised = null;
        service.MessageReceived += m => raised = m;

        var frame = ChatFrameCodec.ToFrame("On my way", Guid.NewGuid());
        transport.Deliver(frame);

        Assert.NotNull(raised);
        Assert.Equal("On my way", raised!.Text);
        Assert.Equal(ChatMessageDirection.Received, raised.Direction);
        Assert.Equal(ChatDeliveryState.Delivered, raised.DeliveryState);
        Assert.Same(raised, service.Conversation.Messages.Single());
    }

    [Fact]
    public void FrameReceived_MalformedChatFrame_IsDroppedSilently()
    {
        var transport = new FakeChatTransport();
        var service = new ChatService(transport);
        var raisedCount = 0;
        service.MessageReceived += _ => raisedCount++;

        var malformed = new ControlFrame
        {
            Type = ControlMessageType.Chat,
            MessageId = Guid.NewGuid(),
            Payload = [1, 2], // too short for the length prefix
        };
        transport.Deliver(malformed);

        Assert.Equal(0, raisedCount);
        Assert.Empty(service.Conversation.Messages);
    }
}
