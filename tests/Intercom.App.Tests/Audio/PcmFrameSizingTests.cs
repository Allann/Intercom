using Intercom.Audio;
using Xunit;

namespace Intercom.App.Tests.Audio;

public sealed class PcmFrameSizingTests
{
    [Theory]
    [InlineData(1_920u, 480u, 1, 480)]
    [InlineData(1_920u, 960u, 1, 960)]
    [InlineData(960u, 480u, 1, 480)]
    public void CaptureUsesQuantumSizeRatherThanOversizedBufferLength(
        uint bufferBytes, uint samplesPerQuantum, int channels, int expectedSamples)
    {
        Assert.Equal(expectedSamples, PcmFrameSizing.ValidCaptureSamples(
            bufferBytes, samplesPerQuantum, channels, sizeof(short)));
    }
}
