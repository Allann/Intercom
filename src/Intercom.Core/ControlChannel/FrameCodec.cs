using System.Buffers.Binary;

namespace Intercom.ControlChannel;

/// <summary>An untrusted peer sent a frame that violates the wire contract
/// (oversized/malformed length prefix, truncated header/payload). The
/// connection must be closed on this, never repaired or partially
/// trusted.</summary>
public sealed class MalformedFrameException : Exception
{
    public MalformedFrameException(string message) : base(message) { }
}

/// <summary>The fixed-size frame header, decoded independently of the
/// payload — see <see cref="FrameCodec.DecodeHeader"/> for why that split
/// matters.</summary>
public sealed record FrameHeader
{
    public required ControlMessageType Type { get; init; }
    public required Guid MessageId { get; init; }
    public Guid? CorrelationId { get; init; }
    public required int PayloadLength { get; init; }
}

/// <summary>
/// Wire encode/decode for <see cref="ControlFrame"/>. Deliberately splits
/// header decode from payload read: <see cref="DecodeHeader"/> validates
/// <see cref="FrameHeader.PayloadLength"/> against
/// <see cref="MaxPayloadLength"/> and throws before any payload byte is ever
/// read or allocated, per docs/research/local-network-transport.md's "apply
/// limits and rate controls before decoding untrusted input" — the bytes on
/// this stream are attacker-controlled input from whichever peer holds the
/// connection (including a not-yet-approved pairing-mode peer), and a bogus
/// length prefix must never be trusted enough to drive an allocation or a
/// read loop.
///
/// Header layout (all multi-byte integers big-endian):
/// <code>
/// [0..2)   MessageType   (ushort)
/// [2..3)   Flags         (byte; bit 0 = has CorrelationId)
/// [3..19)  MessageId     (16-byte Guid)
/// [19..35) CorrelationId (16-byte Guid; meaningless if Flags bit 0 unset)
/// [35..39) PayloadLength (uint)
/// [39..)   Payload
/// </code>
/// </summary>
public static class FrameCodec
{
    /// <summary>64 KiB: generous for control-channel traffic (text, pairing,
    /// presence, attention-card metadata) while bounding worst-case memory
    /// for a single frame. Voice payload never crosses this channel — that's
    /// the separate UDP voice channel (ADR-0001) — so control frames have no
    /// legitimate reason to be large.</summary>
    public const int MaxPayloadLength = 64 * 1024;

    const int MessageTypeSize = sizeof(ushort);
    const int FlagsSize = sizeof(byte);
    const int GuidSize = 16;
    const int PayloadLengthSize = sizeof(uint);
    const int MessageIdOffset = MessageTypeSize + FlagsSize;
    const int CorrelationIdOffset = MessageIdOffset + GuidSize;
    const int PayloadLengthOffset = CorrelationIdOffset + GuidSize;

    public const int HeaderSize = PayloadLengthOffset + PayloadLengthSize;

    const byte HasCorrelationIdFlag = 0b0000_0001;

    /// <summary>Encodes a full frame (header + payload) ready to write to the
    /// transport. Throws if the payload exceeds <see cref="MaxPayloadLength"/>
    /// — this is OUR own outbound data, so this is a programming-error guard,
    /// not untrusted-input handling.</summary>
    public static byte[] Encode(ControlFrame frame)
    {
        if (frame.Payload.Length > MaxPayloadLength)
        {
            throw new ArgumentException(
                $"Payload of {frame.Payload.Length} bytes exceeds the {MaxPayloadLength}-byte control-frame limit.",
                nameof(frame));
        }

        var buffer = new byte[HeaderSize + frame.Payload.Length];
        var span = buffer.AsSpan();

        BinaryPrimitives.WriteUInt16BigEndian(span, (ushort)frame.Type);
        span[MessageTypeSize] = frame.CorrelationId is not null ? HasCorrelationIdFlag : (byte)0;
        frame.MessageId.TryWriteBytes(span.Slice(MessageIdOffset, GuidSize));
        (frame.CorrelationId ?? Guid.Empty).TryWriteBytes(span.Slice(CorrelationIdOffset, GuidSize));
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(PayloadLengthOffset, PayloadLengthSize), (uint)frame.Payload.Length);
        frame.Payload.CopyTo(span[HeaderSize..]);

        return buffer;
    }

    /// <summary>Decodes the fixed-size header only — never reads or
    /// allocates the payload. Throws <see cref="MalformedFrameException"/> if
    /// the declared payload length exceeds <see cref="MaxPayloadLength"/>;
    /// callers must check this before reading that many payload bytes from
    /// the transport.</summary>
    public static FrameHeader DecodeHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length != HeaderSize)
        {
            throw new ArgumentException($"Frame header must be exactly {HeaderSize} bytes, got {header.Length}.", nameof(header));
        }

        var type = (ControlMessageType)BinaryPrimitives.ReadUInt16BigEndian(header);
        var flags = header[MessageTypeSize];
        var messageId = new Guid(header.Slice(MessageIdOffset, GuidSize));
        var correlationId = (flags & HasCorrelationIdFlag) != 0
            ? new Guid(header.Slice(CorrelationIdOffset, GuidSize))
            : (Guid?)null;
        var payloadLength = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(PayloadLengthOffset, PayloadLengthSize));

        if (payloadLength > MaxPayloadLength)
        {
            throw new MalformedFrameException(
                $"Frame declares a {payloadLength}-byte payload, exceeding the {MaxPayloadLength}-byte limit. " +
                "Rejected before reading the payload.");
        }

        return new FrameHeader
        {
            Type = type,
            MessageId = messageId,
            CorrelationId = correlationId,
            PayloadLength = (int)payloadLength,
        };
    }

    /// <summary>Combines an already-decoded header with its (already fully
    /// read) payload. Throws if the payload length doesn't match what the
    /// header declared — a caller bug, since by this point the transport
    /// should already have read exactly <see cref="FrameHeader.PayloadLength"/>
    /// bytes.</summary>
    public static ControlFrame ToFrame(FrameHeader header, byte[] payload)
    {
        if (payload.Length != header.PayloadLength)
        {
            throw new MalformedFrameException(
                $"Expected {header.PayloadLength} payload bytes but received {payload.Length}.");
        }

        return new ControlFrame
        {
            Type = header.Type,
            MessageId = header.MessageId,
            CorrelationId = header.CorrelationId,
            Payload = payload,
        };
    }
}
