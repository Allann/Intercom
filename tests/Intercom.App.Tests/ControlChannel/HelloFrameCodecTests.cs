using Intercom.ControlChannel;
using Xunit;

namespace Intercom.App.Tests.ControlChannel;

public class HelloFrameCodecTests
{
    [Fact]
    public void ToFrame_ThenDecode_RoundTrips()
    {
        var hello = Hello.Current(Capability.Text | Capability.SendAudio | Capability.AttentionCards);

        var frame = hello.ToFrame(Guid.NewGuid());
        var decoded = HelloFrameCodec.Decode(frame);

        Assert.Equal(hello.ProtocolVersion, decoded.ProtocolVersion);
        Assert.Equal(hello.Capabilities, decoded.Capabilities);
    }

    [Fact]
    public void Decode_WrongMessageType_Throws()
    {
        var frame = new ControlFrame { Type = ControlMessageType.Delivered, MessageId = Guid.NewGuid(), Payload = new byte[8] };
        Assert.Throws<ArgumentException>(() => HelloFrameCodec.Decode(frame));
    }

    [Fact]
    public void Decode_WrongPayloadSize_ThrowsMalformed()
    {
        var frame = new ControlFrame { Type = ControlMessageType.Hello, MessageId = Guid.NewGuid(), Payload = new byte[3] };
        Assert.Throws<MalformedFrameException>(() => HelloFrameCodec.Decode(frame));
    }

    [Fact]
    public void Current_UsesCurrentProtocolVersion()
    {
        var hello = Hello.Current(Capability.None);
        Assert.Equal(Hello.CurrentProtocolVersion, hello.ProtocolVersion);
    }
}
