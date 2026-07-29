using Intercom.Audio;
using Xunit;

namespace Intercom.App.Tests.Audio;

public sealed class AudioCodecTests
{
    [Fact]
    public void Opus_EncodesAndDecodesOneTwentyMillisecondMonoFrame()
    {
        var pcm = Enumerable.Range(0, AudioFormat.SamplesPerFrame)
            .Select(i => (short)(Math.Sin(i * 2 * Math.PI * 440 / AudioFormat.SampleRate) * short.MaxValue / 4))
            .ToArray();
        var encoded = new ConcentusAudioEncoder().Encode(pcm);
        var decoded = new ConcentusAudioDecoder().Decode(encoded);
        Assert.InRange(encoded.Length, 1, AudioPacketProtector.MaxOpusPayloadSize);
        Assert.Equal(AudioFormat.SamplesPerFrame, decoded.Length);
    }

    [Fact]
    public void OpusDecoder_ProducesPlcForMissingFrame()
    {
        var decoder = new ConcentusAudioDecoder();
        Assert.Equal(AudioFormat.SamplesPerFrame, decoder.ConcealLoss().Length);
    }
}
