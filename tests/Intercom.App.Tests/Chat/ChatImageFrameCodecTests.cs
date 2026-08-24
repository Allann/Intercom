using System.Buffers.Binary;
using Intercom.Chat;
using Intercom.ControlChannel;
using Xunit;

namespace Intercom.App.Tests.Chat;

public sealed class ChatImageFrameCodecTests
{
    static readonly Guid TransferId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Theory]
    [InlineData(0, 1, 1, ChatImageFormat.Jpeg)]
    [InlineData(ChatImageOptimizer.MaxTransferBytes + 1, 1, 1, ChatImageFormat.Jpeg)]
    [InlineData(1, 0, 1, ChatImageFormat.Jpeg)]
    [InlineData(1, 1, 0, ChatImageFormat.Jpeg)]
    [InlineData(1, 1, 1, (ChatImageFormat)99)]
    public void DecodeStart_RejectsEachInvalidMetadataBoundary(int total, int width, int height, ChatImageFormat format)
    {
        var payload = new byte[47];
        BinaryPrimitives.WriteInt32BigEndian(payload, total);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4), width);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(8), height);
        payload[12] = (byte)format;
        var frame = new ControlFrame { Type = ControlMessageType.ChatImageStart, MessageId = TransferId, Payload = payload };

        Assert.Throws<MalformedFrameException>(() => ChatImageFrameCodec.DecodeStart(frame));
    }

    [Theory]
    [InlineData(ControlMessageType.Chat, true, 5)]
    [InlineData(ControlMessageType.ChatImageChunk, false, 5)]
    [InlineData(ControlMessageType.ChatImageChunk, true, 4)]
    public void DecodeChunk_RejectsEachInvalidEnvelope(ControlMessageType type, bool hasCorrelation, int payloadLength)
    {
        var frame = new ControlFrame
        {
            Type = type,
            MessageId = Guid.NewGuid(),
            CorrelationId = hasCorrelation ? TransferId : null,
            Payload = new byte[payloadLength],
        };

        Assert.Throws<MalformedFrameException>(() => ChatImageFrameCodec.DecodeChunk(frame));
    }

    [Fact]
    public void StartAndChunk_RoundTripAllMetadata()
    {
        var image = new OptimizedChatImage([1, 2, 3], 2, 3, ChatImageFormat.Png);
        var start = ChatImageFrameCodec.DecodeStart(ChatImageFrameCodec.Start(image, "caption", TransferId));
        var chunk = ChatImageFrameCodec.DecodeChunk(ChatImageFrameCodec.Chunk(TransferId, 7, [4, 5]));

        Assert.Equal((3, 2, 3, ChatImageFormat.Png, "caption"),
            (start.TotalBytes, start.Width, start.Height, start.Format, start.Caption));
        Assert.Equal(7, chunk.Offset);
        Assert.Equal([4, 5], chunk.Bytes.ToArray());
    }

    [Fact]
    public void DecodeStart_AcceptsMaximumTransferSizeBoundary()
    {
        var payload = new byte[47];
        BinaryPrimitives.WriteInt32BigEndian(payload, ChatImageOptimizer.MaxTransferBytes);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(8), 1);
        payload[12] = (byte)ChatImageFormat.Jpeg;
        var frame = new ControlFrame { Type = ControlMessageType.ChatImageStart, MessageId = TransferId, Payload = payload };

        Assert.Equal(ChatImageOptimizer.MaxTransferBytes, ChatImageFrameCodec.DecodeStart(frame).TotalBytes);
    }
}
