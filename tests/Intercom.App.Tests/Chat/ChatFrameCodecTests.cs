using Intercom.Chat;
using Intercom.ControlChannel;
using Xunit;

namespace Intercom.App.Tests.Chat;

public class ChatFrameCodecTests
{
    [Fact]
    public void ToFrameThenDecode_RoundTrips()
    {
        var messageId = Guid.NewGuid();
        var frame = ChatFrameCodec.ToFrame("Dinner's ready!", messageId);

        Assert.Equal(ControlMessageType.Chat, frame.Type);
        Assert.Equal(messageId, frame.MessageId);
        Assert.Equal("Dinner's ready!", ChatFrameCodec.Decode(frame));
    }

    [Fact]
    public void ToFrameThenDecode_EmptyText_RoundTrips()
    {
        var frame = ChatFrameCodec.ToFrame("", Guid.NewGuid());

        Assert.Equal("", ChatFrameCodec.Decode(frame));
    }

    [Fact]
    public void ToFrameThenDecode_NonAsciiText_RoundTrips()
    {
        var frame = ChatFrameCodec.ToFrame("On my way 👍 — café", Guid.NewGuid());

        Assert.Equal("On my way 👍 — café", ChatFrameCodec.Decode(frame));
    }

    [Fact]
    public void ToFrameThenDecode_SurvivesTheOuterFrameCodecRoundTrip()
    {
        var frame = ChatFrameCodec.ToFrame("Round trip through the real wire codec too", Guid.NewGuid());

        var bytes = FrameCodec.Encode(frame);
        var header = FrameCodec.DecodeHeader(bytes.AsSpan(0, FrameCodec.HeaderSize));
        var payload = bytes[FrameCodec.HeaderSize..];
        var decodedFrame = FrameCodec.ToFrame(header, payload);

        Assert.Equal("Round trip through the real wire codec too", ChatFrameCodec.Decode(decodedFrame));
    }

    [Fact]
    public void Decode_WrongFrameType_Throws()
    {
        var frame = new ControlFrame { Type = ControlMessageType.Hello, MessageId = Guid.NewGuid(), Payload = [] };

        Assert.Throws<ArgumentException>(() => ChatFrameCodec.Decode(frame));
    }

    [Fact]
    public void Decode_PayloadTooShortForLengthPrefix_Throws()
    {
        var frame = new ControlFrame { Type = ControlMessageType.Chat, MessageId = Guid.NewGuid(), Payload = [1, 2] };

        Assert.Throws<MalformedFrameException>(() => ChatFrameCodec.Decode(frame));
    }

    [Fact]
    public void Decode_DeclaredLengthDisagreesWithActualPayload_Throws()
    {
        // Declares a 100-byte text but carries none.
        var payload = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(payload, 100);
        var frame = new ControlFrame { Type = ControlMessageType.Chat, MessageId = Guid.NewGuid(), Payload = payload };

        Assert.Throws<MalformedFrameException>(() => ChatFrameCodec.Decode(frame));
    }

    [Fact]
    public void Decode_InvalidUtf8Bytes_Throws()
    {
        // 0xFF is never valid as a UTF-8 leading byte.
        byte[] invalidUtf8 = [0xFF, 0xFE];
        var payload = new byte[4 + invalidUtf8.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(payload, (uint)invalidUtf8.Length);
        invalidUtf8.CopyTo(payload, 4);
        var frame = new ControlFrame { Type = ControlMessageType.Chat, MessageId = Guid.NewGuid(), Payload = payload };

        Assert.Throws<MalformedFrameException>(() => ChatFrameCodec.Decode(frame));
    }
}
