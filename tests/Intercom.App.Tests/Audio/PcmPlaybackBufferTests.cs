using Intercom.Audio;
using Xunit;

namespace Intercom.App.Tests.Audio;

public sealed class PcmPlaybackBufferTests
{
    [Fact]
    public void RetainsRemainderWhenRenderQuantumIsSmallerThanDecodedFrame()
    {
        var buffer = new PcmPlaybackBuffer(1_920);
        var decoded = Enumerable.Range(0, 960).Select(value => (short)value).ToArray();
        buffer.Enqueue(decoded);

        var first = new short[480];
        var second = new short[480];
        Assert.Equal(480, buffer.Dequeue(first));
        Assert.Equal(480, buffer.Dequeue(second));

        Assert.Equal(decoded, first.Concat(second));
        Assert.Equal(0, buffer.Count);
    }
}
