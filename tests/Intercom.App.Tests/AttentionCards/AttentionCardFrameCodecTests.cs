using Intercom.AttentionCards;
using Intercom.ControlChannel;
using Xunit;

namespace Intercom.App.Tests.AttentionCards;

public class AttentionCardFrameCodecTests
{
    [Fact]
    public void ToFrameThenDecode_RoundTrips()
    {
        var messageId = Guid.NewGuid();
        var frame = AttentionCardFrameCodec.ToFrame("Dinner's ready", "🍽️", messageId);

        Assert.Equal(ControlMessageType.AttentionCard, frame.Type);
        Assert.Equal(messageId, frame.MessageId);
        var (purpose, icon) = AttentionCardFrameCodec.Decode(frame);
        Assert.Equal("Dinner's ready", purpose);
        Assert.Equal("🍽️", icon);
    }

    [Fact]
    public void ToFrameThenDecode_EmptyStrings_RoundTrip()
    {
        var frame = AttentionCardFrameCodec.ToFrame("", "", Guid.NewGuid());

        var (purpose, icon) = AttentionCardFrameCodec.Decode(frame);
        Assert.Equal("", purpose);
        Assert.Equal("", icon);
    }

    [Fact]
    public void ToFrameThenDecode_CustomNonAsciiPurpose_RoundTrips()
    {
        var frame = AttentionCardFrameCodec.ToFrame("Café run — anyone want anything? ☕", "❓", Guid.NewGuid());

        var (purpose, icon) = AttentionCardFrameCodec.Decode(frame);
        Assert.Equal("Café run — anyone want anything? ☕", purpose);
        Assert.Equal("❓", icon);
    }

    [Fact]
    public void ToFrameThenDecode_SurvivesTheOuterFrameCodecRoundTrip()
    {
        var frame = AttentionCardFrameCodec.ToFrame("Package arrived", "📦", Guid.NewGuid());

        var bytes = FrameCodec.Encode(frame);
        var header = FrameCodec.DecodeHeader(bytes.AsSpan(0, FrameCodec.HeaderSize));
        var payload = bytes[FrameCodec.HeaderSize..];
        var decodedFrame = FrameCodec.ToFrame(header, payload);

        var (purpose, icon) = AttentionCardFrameCodec.Decode(decodedFrame);
        Assert.Equal("Package arrived", purpose);
        Assert.Equal("📦", icon);
    }

    [Fact]
    public void Decode_WrongFrameType_Throws()
    {
        var frame = new ControlFrame { Type = ControlMessageType.Hello, MessageId = Guid.NewGuid(), Payload = [] };

        Assert.Throws<ArgumentException>(() => AttentionCardFrameCodec.Decode(frame));
    }

    [Fact]
    public void Decode_PayloadTooShortForPurposeLengthPrefix_Throws()
    {
        var frame = new ControlFrame { Type = ControlMessageType.AttentionCard, MessageId = Guid.NewGuid(), Payload = [1, 2] };

        Assert.Throws<MalformedFrameException>(() => AttentionCardFrameCodec.Decode(frame));
    }

    [Fact]
    public void Decode_PurposeLengthDisagreesWithActualPayload_Throws()
    {
        // Declares a 100-byte purpose but the payload carries none.
        var payload = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(payload, 100);
        var frame = new ControlFrame { Type = ControlMessageType.AttentionCard, MessageId = Guid.NewGuid(), Payload = payload };

        Assert.Throws<MalformedFrameException>(() => AttentionCardFrameCodec.Decode(frame));
    }

    [Fact]
    public void Decode_MissingIconLengthPrefix_Throws()
    {
        // Purpose is well-formed and consumes the whole payload; there is no
        // room left for the icon's own length prefix.
        var purposeFrame = AttentionCardFrameCodec.ToFrame("hi", "🍽️", Guid.NewGuid());
        var truncated = purposeFrame.Payload[..6]; // just past the purpose length prefix + "hi"
        var frame = new ControlFrame { Type = ControlMessageType.AttentionCard, MessageId = Guid.NewGuid(), Payload = truncated };

        Assert.Throws<MalformedFrameException>(() => AttentionCardFrameCodec.Decode(frame));
    }

    [Fact]
    public void Decode_TrailingExtraBytesAfterIcon_Throws()
    {
        var wellFormed = AttentionCardFrameCodec.ToFrame("hi", "🍽️", Guid.NewGuid());
        var withTrailingByte = wellFormed.Payload.Concat(new byte[] { 0xAA }).ToArray();
        var frame = new ControlFrame { Type = ControlMessageType.AttentionCard, MessageId = Guid.NewGuid(), Payload = withTrailingByte };

        Assert.Throws<MalformedFrameException>(() => AttentionCardFrameCodec.Decode(frame));
    }

    [Fact]
    public void Decode_InvalidUtf8Bytes_Throws()
    {
        byte[] invalidUtf8 = [0xFF, 0xFE];
        var payload = new byte[4 + invalidUtf8.Length + 4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(payload, (uint)invalidUtf8.Length);
        invalidUtf8.CopyTo(payload, 4);
        // icon length prefix (0) follows immediately, already zeroed.
        var frame = new ControlFrame { Type = ControlMessageType.AttentionCard, MessageId = Guid.NewGuid(), Payload = payload };

        Assert.Throws<MalformedFrameException>(() => AttentionCardFrameCodec.Decode(frame));
    }
}
