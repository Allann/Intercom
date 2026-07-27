using System.Buffers.Binary;
using System.Text;
using Intercom.ControlChannel;

namespace Intercom.AttentionCards;

/// <summary>
/// Wire encode/decode for one attention card's frame payload (issue #25) —
/// purpose text plus an emoji icon, both variable length, so this follows
/// <c>Intercom.Chat.ChatFrameCodec</c>'s "self-describing length prefix per
/// field" shape rather than <c>HelloFrameCodec</c>/<c>PresenceFrameCodec</c>'s
/// fixed-size payloads. Two length-prefixed UTF-8 strings back to back, in
/// the same "declared length must agree with actual payload size" defense-in-
/// depth style as chat (<see cref="FrameCodec.DecodeHeader"/> already rejects
/// an oversized PayloadLength before any payload byte is read, so this codec
/// never needs its own separate size ceiling — only agreement checks).
///
/// Payload layout (big-endian):
/// <code>
/// [0..4)   PurposeByteLength   uint32 — length of the UTF-8 purpose text
/// [4..N)   Purpose             UTF-8 bytes, PurposeByteLength long
/// [N..N+4) IconByteLength      uint32 — length of the UTF-8 icon text
/// [N+4..)  Icon                UTF-8 bytes, IconByteLength long
/// </code>
/// </summary>
public static class AttentionCardFrameCodec
{
    const int LengthPrefixSize = sizeof(uint);

    // Strict: a well-behaved peer never sends invalid UTF-8; a malformed
    // sequence is untrusted-input territory, same discipline as
    // ChatFrameCodec/FrameCodec's bounds checks.
    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static ControlFrame ToFrame(string purpose, string icon, Guid messageId)
    {
        ArgumentNullException.ThrowIfNull(purpose);
        ArgumentNullException.ThrowIfNull(icon);

        var purposeBytes = StrictUtf8.GetBytes(purpose);
        var iconBytes = StrictUtf8.GetBytes(icon);
        var payload = new byte[LengthPrefixSize + purposeBytes.Length + LengthPrefixSize + iconBytes.Length];
        var span = payload.AsSpan();

        var offset = 0;
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(offset, LengthPrefixSize), (uint)purposeBytes.Length);
        offset += LengthPrefixSize;
        purposeBytes.CopyTo(span[offset..]);
        offset += purposeBytes.Length;

        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(offset, LengthPrefixSize), (uint)iconBytes.Length);
        offset += LengthPrefixSize;
        iconBytes.CopyTo(span[offset..]);

        return new ControlFrame
        {
            Type = ControlMessageType.AttentionCard,
            MessageId = messageId,
            Payload = payload,
        };
    }

    /// <summary>Throws <see cref="MalformedFrameException"/> on anything but
    /// a well-formed AttentionCard frame — too short to hold either length
    /// prefix, a declared length that disagrees with the actual payload
    /// size, or bytes that aren't valid UTF-8 — mirroring
    /// <c>ChatFrameCodec.Decode</c>'s untrusted-input discipline.</summary>
    public static (string Purpose, string Icon) Decode(ControlFrame frame)
    {
        if (frame.Type != ControlMessageType.AttentionCard)
        {
            throw new ArgumentException($"Frame is not an AttentionCard frame (type {frame.Type}).", nameof(frame));
        }

        var span = frame.Payload.AsSpan();

        var purpose = ReadLengthPrefixedUtf8(span, offset: 0, out var afterPurpose);
        var icon = ReadLengthPrefixedUtf8(span, offset: afterPurpose, out var afterIcon);

        if (afterIcon != frame.Payload.Length)
        {
            throw new MalformedFrameException(
                $"AttentionCard payload declares {afterIcon} total bytes but the frame carries {frame.Payload.Length}.");
        }

        return (purpose, icon);
    }

    static string ReadLengthPrefixedUtf8(ReadOnlySpan<byte> span, int offset, out int nextOffset)
    {
        if (span.Length < offset + LengthPrefixSize)
        {
            throw new MalformedFrameException(
                $"AttentionCard payload must be at least {offset + LengthPrefixSize} bytes for a length prefix at offset {offset}, got {span.Length}.");
        }

        var byteLength = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(offset, LengthPrefixSize));
        var textStart = offset + LengthPrefixSize;
        var textEnd = (long)textStart + byteLength;
        if (textEnd > span.Length)
        {
            throw new MalformedFrameException(
                $"AttentionCard payload declares {byteLength} bytes at offset {textStart} but only {span.Length - textStart} remain.");
        }

        try
        {
            var text = StrictUtf8.GetString(span.Slice(textStart, (int)byteLength));
            nextOffset = (int)textEnd;
            return text;
        }
        catch (DecoderFallbackException)
        {
            throw new MalformedFrameException("AttentionCard payload text is not valid UTF-8.");
        }
    }
}
