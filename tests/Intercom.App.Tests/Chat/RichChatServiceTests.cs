using Intercom.Chat;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Intercom.App.Tests.Chat;

public sealed class RichChatServiceTests
{
    [Fact]
    public async Task TypingSignal_IsEphemeralAndExpiresWithoutBecomingAMessage()
    {
        var now = new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
        var (senderTransport, receiverTransport) = LinkedChatTransport.CreatePair();
        var sender = new RichChatService(senderTransport, clock: () => now);
        var receiver = new RichChatService(receiverTransport, clock: () => now);

        await sender.SetTypingAsync(true, CancellationToken.None);

        Assert.True(receiver.IsPeerTyping);
        Assert.Empty(receiver.Conversation.Messages);

        now += TimeSpan.FromSeconds(6);
        receiver.Tick();

        Assert.False(receiver.IsPeerTyping);
        Assert.Empty(receiver.Conversation.Messages);
    }

    [Fact]
    public async Task SendingText_ClearsTypingAndEnforcesMessageLimit()
    {
        var (senderTransport, receiverTransport) = LinkedChatTransport.CreatePair();
        var sender = new RichChatService(senderTransport);
        var receiver = new RichChatService(receiverTransport);
        await sender.SetTypingAsync(true, CancellationToken.None);

        await sender.SendTextAsync("hello 👋", CancellationToken.None);

        Assert.False(receiver.IsPeerTyping);
        Assert.Equal("hello 👋", Assert.Single(receiver.Conversation.Messages).Text);
        await Assert.ThrowsAsync<ArgumentException>(() => sender.SendTextAsync(new string('x', 4001), CancellationToken.None));
    }

    [Fact]
    public async Task SendImageAsync_ReassemblesValidatesProgressAndDeliversOnlyWhenComplete()
    {
        var (senderTransport, receiverTransport) = LinkedChatTransport.CreatePair();
        var sender = new RichChatService(senderTransport);
        var receiver = new RichChatService(receiverTransport);
        using var sourceImage = new Image<Rgba32>(1200, 800);
        var random = new Random(123);
        sourceImage.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++) row[x] = new Rgba32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
            }
        });
        await using var sourceStream = new MemoryStream();
        await sourceImage.SaveAsPngAsync(sourceStream);
        sourceStream.Position = 0;
        var image = await ChatImageOptimizer.OptimizeAsync(sourceStream, CancellationToken.None);
        var bytes = image.Bytes;
        var receivingProgress = new List<double>();
        receiver.Conversation.MessageUpdated += message =>
        {
            if (message.TransferState == ChatTransferState.Receiving) receivingProgress.Add(message.TransferProgress);
        };

        var sent = await sender.SendImageAsync(image, "**Holiday** photo", CancellationToken.None);

        var received = Assert.Single(receiver.Conversation.Messages);
        Assert.Equal(ChatContentKind.Image, received.ContentKind);
        Assert.Equal("**Holiday** photo", received.Text);
        Assert.Equal(bytes, received.Image!.Bytes);
        Assert.Equal(ChatTransferState.Delivered, received.TransferState);
        Assert.Equal(ChatDeliveryState.Delivered, sender.Conversation.Messages.Single().DeliveryState);
        Assert.Contains(receivingProgress, progress => progress > 0 && progress < 1);
        Assert.Equal(sent.MessageId, received.MessageId);
    }

    [Fact]
    public async Task CompletedTransfer_WithBytesThatAreNotTheDeclaredImage_IsRejected()
    {
        var (senderTransport, receiverTransport) = LinkedChatTransport.CreatePair();
        var sender = new RichChatService(senderTransport);
        var receiver = new RichChatService(receiverTransport);

        await sender.SendImageAsync(new OptimizedChatImage(new byte[70_000], 1024, 768, ChatImageFormat.Jpeg), "", CancellationToken.None);

        var received = Assert.Single(receiver.Conversation.Messages);
        Assert.Equal(ChatTransferState.Failed, received.TransferState);
        Assert.Null(received.Image);
        Assert.Equal(ChatTransferState.Failed, sender.Conversation.Messages.Single().TransferState);
        Assert.Equal(ChatDeliveryState.Undelivered, sender.Conversation.Messages.Single().DeliveryState);
    }

    [Fact]
    public async Task RecipientCancellation_IsVisibleToSenderAndAllowsManualResend()
    {
        var senderTransport = new LinkedChatTransport();
        var receiverTransport = new LinkedChatTransport();
        senderTransport.Link(receiverTransport);
        receiverTransport.Link(senderTransport);
        var sender = new RichChatService(senderTransport);
        var receiver = new RichChatService(receiverTransport);
        receiver.IncomingImageStarted += id => receiver.CancelImageAsync(id, CancellationToken.None).GetAwaiter().GetResult();

        var sent = await sender.SendImageAsync(
            new OptimizedChatImage(new byte[100_000], 1024, 768, ChatImageFormat.Jpeg), "", CancellationToken.None);

        Assert.Equal(ChatTransferState.RecipientCancelled, sender.Conversation.Messages.Single().TransferState);
        Assert.Equal(ChatDeliveryState.Undelivered, sender.Conversation.Messages.Single().DeliveryState);
        Assert.Equal(sent.MessageId, sender.Conversation.Messages.Single().MessageId);
    }

    [Fact]
    public async Task OlderPeer_KeepsTextButDisablesImagesAndTyping()
    {
        var transport = new LinkedChatTransport { RemoteCapabilities = Intercom.ControlChannel.Capability.Text };
        var service = new RichChatService(transport);

        await service.SetTypingAsync(true, CancellationToken.None);
        await service.SendTextAsync("plain text still works", CancellationToken.None);

        Assert.False(service.SupportsTyping);
        Assert.False(service.SupportsImages);
        Assert.Equal(1, transport.SendCount);
        await Assert.ThrowsAsync<NotSupportedException>(() => service.SendImageAsync(
            new OptimizedChatImage([1], 1, 1, ChatImageFormat.Jpeg), "", CancellationToken.None));
    }

    [Fact]
    public async Task SenderCanCancelAnImageTransferInProgress()
    {
        var (senderTransport, receiverTransport) = LinkedChatTransport.CreatePair();
        var sender = new RichChatService(senderTransport);
        _ = new RichChatService(receiverTransport);
        var cancelled = false;
        senderTransport.Sending += frame =>
        {
            if (cancelled || frame.Type != Intercom.ControlChannel.ControlMessageType.ChatImageChunk) return;
            cancelled = true;
            sender.CancelOutgoingImageAsync(frame.CorrelationId!.Value, CancellationToken.None).GetAwaiter().GetResult();
        };

        await sender.SendImageAsync(new OptimizedChatImage(new byte[150_000], 1024, 768, ChatImageFormat.Jpeg), "", CancellationToken.None);

        Assert.Equal(ChatTransferState.Cancelled, sender.Conversation.Messages.Single().TransferState);
        Assert.Equal(ChatDeliveryState.Undelivered, sender.Conversation.Messages.Single().DeliveryState);
    }
}

sealed class LinkedChatTransport : IChatTransport
{
    public LinkedChatTransport? Peer { get; private set; }
    public Intercom.ControlChannel.Capability RemoteCapabilities { get; set; } =
        Intercom.ControlChannel.Capability.Text | Intercom.ControlChannel.Capability.ChatMarkdown
        | Intercom.ControlChannel.Capability.ChatImages | Intercom.ControlChannel.Capability.ChatTyping;
    public int SendCount { get; private set; }
    public event Action<Intercom.ControlChannel.ControlFrame>? Sending;
    public event Action<Intercom.ControlChannel.ControlFrame>? FrameReceived;
    public event Action<Guid>? DeliveryConfirmed;
    public event Action? ConnectionDropped;

    public Task SendAsync(Intercom.ControlChannel.ControlFrame frame, CancellationToken cancellationToken)
    {
        SendCount++;
        Sending?.Invoke(frame);
        Peer?.FrameReceived?.Invoke(frame);
        DeliveryConfirmed?.Invoke(frame.MessageId);
        return Task.CompletedTask;
    }

    public void Link(LinkedChatTransport peer) => Peer = peer;
    public void Drop() => ConnectionDropped?.Invoke();

    public static (LinkedChatTransport A, LinkedChatTransport B) CreatePair()
    {
        var a = new LinkedChatTransport();
        var b = new LinkedChatTransport();
        a.Link(b);
        b.Link(a);
        return (a, b);
    }
}
