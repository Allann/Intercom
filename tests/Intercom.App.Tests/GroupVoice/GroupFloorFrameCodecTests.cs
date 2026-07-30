using Intercom.ControlChannel;
using Intercom.GroupVoice;
using Xunit;

namespace Intercom.App.Tests.GroupVoice;

public sealed class GroupFloorFrameCodecTests
{
    [Fact]
    public void RoundTripsCommand()
    {
        var expected = new GroupFloorCommand(Guid.NewGuid(), GroupFloorCommandKind.Interrupt, Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(expected, GroupFloorFrameCodec.Decode(GroupFloorFrameCodec.Encode(expected)));
    }

    [Fact]
    public void RejectsUnknownCommand()
    {
        var frame = GroupFloorFrameCodec.Encode(new GroupFloorCommand(Guid.NewGuid(), GroupFloorCommandKind.Join, Guid.NewGuid(), Guid.NewGuid()));
        frame.Payload[16] = 255;
        Assert.Throws<MalformedFrameException>(() => GroupFloorFrameCodec.Decode(frame));
    }
}
