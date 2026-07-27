using System.Buffers.Binary;
using System.Text;
using Intercom.ControlChannel;

namespace Intercom.Chat;

/// <summary>
/// Wire encode/decode for one plain-text chat message's frame payload
/// (issue #24). Unlike <c>HelloFrameCodec</c>/<c>PresenceFrameCodec</c>'s
/// fixed-size payloads, chat text is variable length, so the payload
/// self-describes its own text length rather than relying solely on the
/// outer <see cref="FrameCodec"/> header's PayloadLength — a defense-in-depth
/// check (the two must agree) rather than the only bound: <see cref="FrameCodec.DecodeHeader"/>
/// already rejects an oversized PayloadLength before any payload byte is read
/// or allocated, so this codec never needs its own separate size ceiling.
///
/// Payload layout (big-endian):
/// <code>
/// [0..4)  TextByteLength   uint32 — length of the UTF-8 text that follows
/// [4..)   Text             UTF-8 bytes, TextByteLength long
/// </code>
/// </summary>
public static class ChatFrameCodec
{
    const int TextByteLengthOffset = 0;
    const int TextByteLengthSize = sizeof(uint);
    const int TextOffset = TextByteLengthOffset + TextByteLengthSize;

    // Strict: a well-behaved peer never sends invalid UTF-8; a malformed
    // sequence is untrusted-input territory, same discipline as FrameCodec's
    // bounds checks.
    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static ControlFrame ToFrame(string text, Guid messageId)
    {
        ArgumentNullException.ThrowIfNull(text);

        var textBytes = StrictUtf8.GetBytes(text);
        var payload = new byte[TextOffset + textBytes.Length];
        var span = payload.AsSpan();

        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(TextByteLengthOffset, TextByteLengthSize), (uint)textBytes.Length);
        textBytes.CopyTo(span[TextOffset..]);

        return new ControlFrame
        {
            Type = ControlMessageType.Chat,
            MessageId = messageId,
            Payload = payload,
        };
    }

    /// <summary>Throws <see cref="MalformedFrameException"/> on anything but
    /// a well-formed Chat frame (too short a payload to even hold the length
    /// prefix, a declared text length that disagrees with the actual payload
    /// size, or bytes that aren't valid UTF-8) — untrusted input, per
    /// FrameCodec's bounds discipline.</summary>
    public static string Decode(ControlFrame frame)
    {
        if (frame.Type != ControlMessageType.Chat)
        {
            throw new ArgumentException($"Frame is not a Chat frame (type {frame.Type}).", nameof(frame));
        }
        if (frame.Payload.Length < TextOffset)
        {
            throw new MalformedFrameException(
                $"Chat payload must be at least {TextOffset} bytes (length prefix), got {frame.Payload.Length}.");
        }

        var span = frame.Payload.AsSpan();
        var textByteLength = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(TextByteLengthOffset, TextByteLengthSize));
        var expectedTotal = (long)TextOffset + textByteLength;
        if (expectedTotal != frame.Payload.Length)
        {
            throw new MalformedFrameException(
                $"Chat payload declares {textByteLength} text bytes (total {expectedTotal}) but the frame carries {frame.Payload.Length}.");
        }

        try
        {
            return StrictUtf8.GetString(span[TextOffset..]);
        }
        catch (DecoderFallbackException)
        {
            throw new MalformedFrameException("Chat payload text is not valid UTF-8.");
        }
    }
}
