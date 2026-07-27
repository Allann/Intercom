using System.Buffers.Binary;

namespace Intercom.ControlChannel;

/// <summary>
/// The one-time capability exchange sent/received immediately after mutual
/// TLS succeeds, before any other message type is accepted (ADR-0001,
/// docs/mvp-specification.md §2). Exactly one Hello is ever valid per
/// connection in each direction — enforced by <see cref="FrameDispatcher"/>,
/// not by this type.
/// </summary>
public sealed record Hello
{
    /// <summary>The only protocol version this build speaks. Not a
    /// negotiation range in the MVP — a future incompatible version bump is
    /// out of scope here (no version-skew handling beyond carrying the field).</summary>
    public const int CurrentProtocolVersion = 1;

    public required int ProtocolVersion { get; init; }
    public required Capability Capabilities { get; init; }

    public static Hello Current(Capability capabilities) => new()
    {
        ProtocolVersion = CurrentProtocolVersion,
        Capabilities = capabilities,
    };
}

/// <summary>Wire encode/decode for <see cref="Hello"/>'s frame payload: a
/// fixed 8 bytes (4-byte protocol version + 4-byte capability bitmask) —
/// small and fixed-size enough that no length-prefixing beyond the outer
/// frame's PayloadLength is needed.</summary>
public static class HelloFrameCodec
{
    const int PayloadSize = 8;

    public static ControlFrame ToFrame(this Hello hello, Guid messageId)
    {
        var payload = new byte[PayloadSize];
        BinaryPrimitives.WriteInt32BigEndian(payload, hello.ProtocolVersion);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), (uint)hello.Capabilities);

        return new ControlFrame
        {
            Type = ControlMessageType.Hello,
            MessageId = messageId,
            Payload = payload,
        };
    }

    /// <summary>Throws <see cref="MalformedFrameException"/> on anything but
    /// a well-formed Hello frame — untrusted input, per FrameCodec's bounds
    /// discipline.</summary>
    public static Hello Decode(ControlFrame frame)
    {
        if (frame.Type != ControlMessageType.Hello)
        {
            throw new ArgumentException($"Frame is not a Hello frame (type {frame.Type}).", nameof(frame));
        }
        if (frame.Payload.Length != PayloadSize)
        {
            throw new MalformedFrameException($"Hello payload must be exactly {PayloadSize} bytes, got {frame.Payload.Length}.");
        }

        var version = BinaryPrimitives.ReadInt32BigEndian(frame.Payload);
        var capabilities = (Capability)BinaryPrimitives.ReadUInt32BigEndian(frame.Payload.AsSpan(4));
        return new Hello { ProtocolVersion = version, Capabilities = capabilities };
    }
}
