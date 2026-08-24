using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Intercom.ControlChannel;

namespace Intercom.Chat;

public sealed record ChatImageStart(int TotalBytes, int Width, int Height, ChatImageFormat Format, string Caption, byte[] Sha256);

public static class ChatImageFrameCodec
{
    const int StartFixedSize = 4 + 4 + 4 + 1 + 2 + 32;
    const int ChunkOffsetSize = 4;
    public const int MaxChunkBytes = FrameCodec.MaxPayloadLength - ChunkOffsetSize;

    public static ControlFrame Start(OptimizedChatImage image, string caption, Guid transferId)
    {
        var captionBytes = Encoding.UTF8.GetBytes(caption);
        if (captionBytes.Length > ushort.MaxValue) throw new ArgumentException("Image caption is too large.", nameof(caption));
        var payload = new byte[StartFixedSize + captionBytes.Length];
        BinaryPrimitives.WriteInt32BigEndian(payload, image.Bytes.Length);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4), image.Width);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(8), image.Height);
        payload[12] = (byte)image.Format;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(13), (ushort)captionBytes.Length);
        SHA256.HashData(image.Bytes).CopyTo(payload, 15);
        captionBytes.CopyTo(payload, StartFixedSize);
        return new ControlFrame { Type = ControlMessageType.ChatImageStart, MessageId = transferId, Payload = payload };
    }

    public static ChatImageStart DecodeStart(ControlFrame frame)
    {
        if (frame.Type != ControlMessageType.ChatImageStart || frame.Payload.Length < StartFixedSize) throw new MalformedFrameException("Invalid image start frame.");
        var total = BinaryPrimitives.ReadInt32BigEndian(frame.Payload);
        var width = BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4));
        var height = BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(8));
        var format = (ChatImageFormat)frame.Payload[12];
        var captionLength = BinaryPrimitives.ReadUInt16BigEndian(frame.Payload.AsSpan(13));
        if (!HasValidSize(total, width, height) || !HasValidFormat(format)
            || frame.Payload.Length != StartFixedSize + captionLength)
            throw new MalformedFrameException("Image start metadata is outside the accepted bounds.");
        string caption;
        try { caption = new UTF8Encoding(false, true).GetString(frame.Payload.AsSpan(StartFixedSize)); }
        catch (DecoderFallbackException) { throw new MalformedFrameException("Image caption is not valid UTF-8."); }
        return new ChatImageStart(total, width, height, format, caption, frame.Payload.AsSpan(15, 32).ToArray());
    }

    static bool HasValidSize(int total, int width, int height) =>
        total > 0 && total <= ChatImageOptimizer.MaxTransferBytes && width > 0 && height > 0;

    static bool HasValidFormat(ChatImageFormat format) =>
        format is ChatImageFormat.Jpeg or ChatImageFormat.Png;

    public static ControlFrame Chunk(Guid transferId, int offset, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxChunkBytes) throw new ArgumentException("Image chunk is too large.", nameof(bytes));
        var payload = new byte[ChunkOffsetSize + bytes.Length];
        BinaryPrimitives.WriteInt32BigEndian(payload, offset);
        bytes.CopyTo(payload.AsSpan(ChunkOffsetSize));
        return new ControlFrame { Type = ControlMessageType.ChatImageChunk, MessageId = Guid.NewGuid(), CorrelationId = transferId, Payload = payload };
    }

    public static (int Offset, ReadOnlyMemory<byte> Bytes) DecodeChunk(ControlFrame frame)
    {
        if (frame.Type != ControlMessageType.ChatImageChunk || frame.CorrelationId is null || frame.Payload.Length <= ChunkOffsetSize)
            throw new MalformedFrameException("Invalid image chunk frame.");
        return (BinaryPrimitives.ReadInt32BigEndian(frame.Payload), frame.Payload.AsMemory(ChunkOffsetSize));
    }

    public static ControlFrame Signal(ControlMessageType type, Guid transferId) => new()
    {
        Type = type, MessageId = Guid.NewGuid(), CorrelationId = transferId, Payload = [],
    };
}
