using Intercom.Chat;
using Xunit;

namespace Intercom.App.Tests.Chat;

/// <summary>
/// Exercises the WinUI chat drawer's demo transport at the <see cref="ChatService"/>
/// level — confirms it drives real <c>FrameDispatcher</c> production code
/// (real mechanical Delivered receipts), not a faked shortcut.
/// </summary>
public class LoopbackChatTransportTests
{
    [Fact]
    public async Task SendFromAToB_ArrivesAtB_AndAGetsARealDeliveredReceipt()
    {
        var (a, b) = LoopbackChatTransport.CreatePair();
        var serviceA = new ChatService(a);
        var serviceB = new ChatService(b);
        ChatMessage? receivedByB = null;
        serviceB.MessageReceived += m => receivedByB = m;

        var sent = await serviceA.SendAsync("Dinner's ready!", CancellationToken.None);

        Assert.NotNull(receivedByB);
        Assert.Equal("Dinner's ready!", receivedByB!.Text);
        Assert.Equal(ChatDeliveryState.Delivered, serviceA.Conversation.Messages.Single(m => m.MessageId == sent.MessageId).DeliveryState);
    }

    [Fact]
    public async Task SimulateDrop_MarksPendingMessagesUndelivered()
    {
        var (a, _) = LoopbackChatTransport.CreatePair();
        var serviceA = new ChatService(a);

        // Peer is unlinked so the send never gets a Delivered receipt back —
        // simulating a message in flight when the connection drops.
        a.Peer = null;
        await serviceA.SendAsync("in flight", CancellationToken.None);

        a.SimulateDrop();

        Assert.Equal(ChatDeliveryState.Undelivered, serviceA.Conversation.Messages.Single().DeliveryState);
    }
}
