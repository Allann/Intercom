using Intercom.Audio;
using Xunit;

namespace Intercom.App.Tests.Audio;

public sealed class FloatPcmConverterTests
{
    [Fact]
    public void ConvertsNormalizedFloatCaptureToSignedPcm16()
    {
        float[] source = [-1f, -0.5f, 0f, 0.5f, 1f];

        Assert.Equal(
            [-32767, -16384, 0, 16384, 32767],
            FloatPcmConverter.ToPcm16(source));
    }

    [Fact]
    public void ConvertsSignedPcm16PlaybackToNormalizedFloat()
    {
        short[] source = [-32767, 0, 32767];
        var destination = new float[3];

        FloatPcmConverter.ToFloat(source, destination);

        Assert.Equal([-1f, 0f, 1f], destination);
    }
}
