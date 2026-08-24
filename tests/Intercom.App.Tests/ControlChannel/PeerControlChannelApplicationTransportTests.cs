using System.Net;
using System.Reflection;
using Intercom.AttentionCards;
using Intercom.Chat;
using Intercom.ControlChannel;
using Intercom.Identity;
using Xunit;

namespace Intercom.App.Tests.ControlChannel;

public sealed class PeerControlChannelApplicationTransportTests
{
    [Fact]
    public void AttentionAdapter_RoutesCardsAndCorrelatedReceiptsOnly()
    {
        var channel = Channel();
        var transport = new PeerControlChannelAttentionCardTransport(channel);
        var cards = new List<ControlFrame>();
        var acknowledged = new List<Guid>();
        var resolved = new List<Guid>();
        transport.FrameReceived += cards.Add;
        transport.Acknowledged += acknowledged.Add;
        transport.Resolved += resolved.Add;
        var acknowledgementId = Guid.NewGuid();
        var resolvedId = Guid.NewGuid();

        Raise(channel, "MessageReceived", Frame(ControlMessageType.AttentionCard));
        Raise(channel, "MessageReceived", Frame(ControlMessageType.Acknowledged, acknowledgementId));
        Raise(channel, "MessageReceived", Frame(ControlMessageType.Resolved, resolvedId));
        Raise(channel, "MessageReceived", Frame(ControlMessageType.Acknowledged));
        Raise(channel, "MessageReceived", Frame(ControlMessageType.Chat));

        Assert.Single(cards);
        Assert.Equal([acknowledgementId], acknowledged);
        Assert.Equal([resolvedId], resolved);
    }

    [Fact]
    public void AttentionAdapter_ForwardsMechanicalDeliveryConfirmation()
    {
        var channel = Channel();
        var transport = new PeerControlChannelAttentionCardTransport(channel);
        var confirmed = new List<Guid>();
        transport.DeliveryConfirmed += confirmed.Add;
        var id = Guid.NewGuid();

        Raise(channel, "DeliveryConfirmed", id);

        Assert.Equal([id], confirmed);
    }

    [Fact]
    public void ChatAdapter_ForwardsOnlyChatFramesAndDeliveryConfirmations()
    {
        var channel = Channel();
        var transport = new PeerControlChannelChatTransport(channel);
        var frames = new List<ControlFrame>();
        var confirmed = new List<Guid>();
        transport.FrameReceived += frames.Add;
        transport.DeliveryConfirmed += confirmed.Add;
        var id = Guid.NewGuid();

        foreach (var type in new[]
                 {
                     ControlMessageType.Chat, ControlMessageType.ChatTyping,
                     ControlMessageType.ChatImageStart, ControlMessageType.ChatImageChunk,
                     ControlMessageType.ChatImageComplete, ControlMessageType.ChatImageReceived,
                     ControlMessageType.ChatImageCancelled, ControlMessageType.ChatImageFailed,
                 })
            Raise(channel, "MessageReceived", Frame(type));
        Raise(channel, "MessageReceived", Frame(ControlMessageType.Presence));
        Raise(channel, "DeliveryConfirmed", id);

        Assert.Equal(8, frames.Count);
        Assert.Equal([id], confirmed);
    }

    [Fact]
    public void ChatAdapter_RemovingOneFrameHandler_KeepsTheOtherHandler()
    {
        var channel = Channel();
        var transport = new PeerControlChannelChatTransport(channel);
        var removedCalls = 0;
        var retainedCalls = 0;
        Action<ControlFrame> removed = _ => removedCalls++;
        Action<ControlFrame> retained = _ => retainedCalls++;
        transport.FrameReceived += removed;
        transport.FrameReceived += retained;

        transport.FrameReceived -= removed;
        Raise(channel, "MessageReceived", Frame(ControlMessageType.Chat));

        Assert.Equal(0, removedCalls);
        Assert.Equal(1, retainedCalls);
    }

    [Fact]
    public void ChatAdapter_RemovingANullFrameHandler_IsSafeAndKeepsExistingHandlers()
    {
        var channel = Channel();
        var transport = new PeerControlChannelChatTransport(channel);
        var calls = 0;
        transport.FrameReceived += _ => calls++;
        Action<ControlFrame>? missing = null;

        transport.FrameReceived -= missing;
        Raise(channel, "MessageReceived", Frame(ControlMessageType.Chat));

        Assert.Equal(1, calls);
    }

    static PeerControlChannel Channel() => new(
        new NeverConnector(),
        new SpkiPin(Enumerable.Repeat((byte)1, 32).ToArray()),
        Capability.Text,
        () => []);

    static ControlFrame Frame(ControlMessageType type, Guid? correlationId = null) => new()
    {
        Type = type,
        MessageId = Guid.NewGuid(),
        CorrelationId = correlationId,
        Payload = [],
    };

    static void Raise(PeerControlChannel channel, string eventName, params object[] arguments)
    {
        var field = typeof(PeerControlChannel).GetField(eventName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var handlers = Assert.IsAssignableFrom<Delegate>(field.GetValue(channel));
        handlers.DynamicInvoke(arguments);
    }

    sealed class NeverConnector : IPeerTransportConnector
    {
        public Task<IPeerTransportConnection> ConnectAsync(IPEndPoint endpoint, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This test does not connect.");
    }
}
