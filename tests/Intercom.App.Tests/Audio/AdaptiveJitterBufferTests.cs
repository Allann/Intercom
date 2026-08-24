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

    [Fact]
    public void EndTalkspurt_StopsConcealmentImmediately_AndPreservesNextBurstFrames()
    {
        var buffer = new AdaptiveJitterBuffer();
        buffer.Add(Frame(0));
        buffer.Add(Frame(1));
        buffer.Add(new JitterFrame(2, 0, [], AudioPacketFlags.EndOfTalkspurt));
        buffer.Add(Frame(3));
        buffer.Add(Frame(4));

        Assert.Equal((ulong)0, buffer.Read().Frame!.Sequence);
        Assert.Equal((ulong)1, buffer.Read().Frame!.Sequence);
        Assert.Equal(AudioPacketFlags.EndOfTalkspurt, buffer.Read().Frame!.Flags);
        buffer.EndTalkspurt();

        for (var i = 0; i < AdaptiveJitterBuffer.IdleConcealmentLimit; i++)
            Assert.False(buffer.Read().Conceal);
        Assert.Equal(0, buffer.ConcealedFrames);

        buffer.Add(Frame(5));
        Assert.Equal((ulong)3, buffer.Read().Frame!.Sequence);
    }

    [Fact]
    public void FiftyCleanReads_ReducesTargetOnlyToMinimum()
    {
        var buffer = new AdaptiveJitterBuffer();
        for (ulong sequence = 0; sequence < 60; sequence++)
        {
            buffer.Add(Frame(sequence));
            if (sequence >= 2) Assert.False(buffer.Read().Conceal);
        }

        Assert.Equal(AdaptiveJitterBuffer.MinTargetFrames, buffer.TargetFrames);
    }

    [Fact]
    public void MoreThanFiveConcealedReads_IncreasesTarget()
    {
        var buffer = new AdaptiveJitterBuffer();
        for (ulong sequence = 0; sequence < 60; sequence++)
        {
            if (sequence % 8 != 7) buffer.Add(Frame(sequence));
            if (sequence >= 2) buffer.Read();
        }

        Assert.Equal(AdaptiveJitterBuffer.MaxTargetFrames, buffer.TargetFrames);
    }

    [Fact]
    public void ExactlyFiveConcealedReadsInWindow_DoesNotIncreaseTarget()
    {
        var buffer = new AdaptiveJitterBuffer();
        buffer.Add(Frame(0));
        buffer.Add(Frame(1));
        buffer.Add(Frame(2));

        for (ulong sequence = 0; sequence < 50; sequence++)
        {
            if (sequence >= 3 && sequence % 10 != 9) buffer.Add(Frame(sequence));
            buffer.Read();
        }

        Assert.Equal(5, buffer.ConcealedFrames);
        Assert.Equal(AdaptiveJitterBuffer.InitialTargetFrames, buffer.TargetFrames);
    }

    [Fact]
    public void Add_RejectsDuplicateAndLateFrames()
    {
        var buffer = new AdaptiveJitterBuffer();
        Assert.True(buffer.Add(Frame(0)));
        Assert.False(buffer.Add(Frame(0)));
        buffer.Add(Frame(1));
        buffer.Add(Frame(2));
        Assert.Equal((ulong)0, buffer.Read().Frame!.Sequence);
        Assert.False(buffer.Add(Frame(0)));
    }

    [Fact]
    public void Reset_ClearsFramesCountersAndAdaptationState()
    {
        var buffer = new AdaptiveJitterBuffer();
        for (ulong sequence = 0; sequence < 20; sequence++) buffer.Add(Frame(sequence));
        buffer.Read();

        buffer.Reset();

        Assert.Equal(0, buffer.Occupancy);
        Assert.Equal(AdaptiveJitterBuffer.InitialTargetFrames, buffer.TargetFrames);
        Assert.False(buffer.Read().Conceal);
    }
}
