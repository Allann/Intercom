using Intercom.Audio;
using Xunit;

namespace Intercom.App.Tests.Audio;

public sealed class AdaptiveJitterBufferTests
{
    static JitterFrame Frame(ulong sequence) => new(sequence, sequence * 960, [(byte)sequence]);

    [Fact]
    public void StartsAfterThreeFramesAndConcealsMissingFrame()
    {
        var buffer = new AdaptiveJitterBuffer();
        buffer.Add(Frame(0));
        buffer.Add(Frame(1));
        Assert.False(buffer.Read().Conceal);
        buffer.Add(Frame(3));
        Assert.Equal((ulong)0, buffer.Read().Frame!.Sequence);
        Assert.Equal((ulong)1, buffer.Read().Frame!.Sequence);
        Assert.True(buffer.Read().Conceal);
        Assert.Equal((ulong)3, buffer.Read().Frame!.Sequence);
    }

    [Fact]
    public void OverflowDiscardsOldestAndStaysBounded()
    {
        var buffer = new AdaptiveJitterBuffer();
        for (ulong sequence = 0; sequence < 20; sequence++) buffer.Add(Frame(sequence));
        Assert.True(buffer.Occupancy <= AdaptiveJitterBuffer.InitialTargetFrames + 2);
        Assert.True(buffer.DiscardedFrames > 0);
    }

    [Fact]
    public void StopsConcealmentAndResynchronizesAfterPushToTalkIdleGap()
    {
        var buffer = new AdaptiveJitterBuffer();
        buffer.Add(Frame(0)); buffer.Add(Frame(1)); buffer.Add(Frame(2));
        Assert.Equal((ulong)0, buffer.Read().Frame!.Sequence);
        Assert.Equal((ulong)1, buffer.Read().Frame!.Sequence);
        Assert.Equal((ulong)2, buffer.Read().Frame!.Sequence);
        for (var i = 0; i < AdaptiveJitterBuffer.IdleConcealmentLimit; i++) buffer.Read();

        Assert.True(buffer.Add(Frame(3)));
        Assert.True(buffer.Add(Frame(4)));
        Assert.True(buffer.Add(Frame(5)));
        Assert.Equal((ulong)3, buffer.Read().Frame!.Sequence);
    }
}
