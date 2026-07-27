using Intercom.ControlChannel;
using Xunit;

namespace Intercom.App.Tests.ControlChannel;

public class FrameCodecTests
{
    [Fact]
    public void EncodeThenDecode_RoundTrips()
    {
        var frame = new ControlFrame
        {
            Type = ControlMessageType.Hello,
            MessageId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            Payload = [1, 2, 3, 4],
        };

        var bytes = FrameCodec.Encode(frame);
        var header = FrameCodec.DecodeHeader(bytes.AsSpan(0, FrameCodec.HeaderSize));
        var payload = bytes[FrameCodec.HeaderSize..];
        var decoded = FrameCodec.ToFrame(header, payload);

        Assert.Equal(frame.Type, decoded.Type);
        Assert.Equal(frame.MessageId, decoded.MessageId);
        Assert.Equal(frame.CorrelationId, decoded.CorrelationId);
        Assert.Equal(frame.Payload, decoded.Payload);
    }

    [Fact]
    public void EncodeThenDecode_NoCorrelationId_RoundTripsAsNull()
    {
        var frame = new ControlFrame
        {
            Type = ControlMessageType.Delivered,
            MessageId = Guid.NewGuid(),
            CorrelationId = null,
            Payload = [],
        };

        var bytes = FrameCodec.Encode(frame);
        var header = FrameCodec.DecodeHeader(bytes.AsSpan(0, FrameCodec.HeaderSize));

        Assert.Null(header.CorrelationId);
    }

    [Fact]
    public void Encode_PayloadExceedsMaxLength_Throws()
    {
        var frame = new ControlFrame
        {
            Type = ControlMessageType.Hello,
            MessageId = Guid.NewGuid(),
            Payload = new byte[FrameCodec.MaxPayloadLength + 1],
        };

        Assert.Throws<ArgumentException>(() => FrameCodec.Encode(frame));
    }

    [Fact]
    public void DecodeHeader_DeclaredLengthExceedsMax_ThrowsBeforeAnyPayloadIsRead()
    {
        // Hand-craft a header whose PayloadLength field claims far more than
        // the limit — this is exactly the "untrusted input" case the split
        // header/payload decode exists to guard: DecodeHeader must reject
        // this from the header alone, with no payload bytes involved at all.
        var frame = new ControlFrame
        {
            Type = ControlMessageType.Hello,
            MessageId = Guid.NewGuid(),
            Payload = [],
        };
        var bytes = FrameCodec.Encode(frame);
        var oversizedLength = (uint)FrameCodec.MaxPayloadLength + 1000;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
            bytes.AsSpan(FrameCodec.HeaderSize - sizeof(uint), sizeof(uint)), oversizedLength);

        Assert.Throws<MalformedFrameException>(() => FrameCodec.DecodeHeader(bytes.AsSpan(0, FrameCodec.HeaderSize)));
    }

    [Fact]
    public void DecodeHeader_WrongLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => FrameCodec.DecodeHeader(new byte[FrameCodec.HeaderSize - 1]));
        Assert.Throws<ArgumentException>(() => FrameCodec.DecodeHeader(new byte[FrameCodec.HeaderSize + 1]));
    }

    [Fact]
    public void ToFrame_PayloadLengthMismatch_Throws()
    {
        var header = new FrameHeader
        {
            Type = ControlMessageType.Hello,
            MessageId = Guid.NewGuid(),
            PayloadLength = 4,
        };

        Assert.Throws<MalformedFrameException>(() => FrameCodec.ToFrame(header, [1, 2]));
    }

    [Fact]
    public void UnknownMessageType_StillDecodesWithoutThrowing()
    {
        // Future tickets add message types without touching this file —
        // an unrecognized numeric type must decode fine (dispatch decides
        // what to do with it), not throw.
        var frame = new ControlFrame
        {
            Type = (ControlMessageType)9999,
            MessageId = Guid.NewGuid(),
            Payload = [42],
        };

        var bytes = FrameCodec.Encode(frame);
        var header = FrameCodec.DecodeHeader(bytes.AsSpan(0, FrameCodec.HeaderSize));

        Assert.Equal((ControlMessageType)9999, header.Type);
    }
}
