using Concentus.Enums;
using Concentus;

namespace Intercom.Audio;

public interface IAudioEncoder
{
    byte[] Encode(ReadOnlySpan<short> pcm);
}

public interface IAudioDecoder
{
    short[] Decode(ReadOnlySpan<byte> packet);
    short[] ConcealLoss();
}

public static class AudioFormat
{
    public const int SampleRate = 48_000;
    public const int Channels = 1;
    public const int FrameMilliseconds = 20;
    public const int SamplesPerFrame = 960;
    public const int TargetBitrate = 24_000;
}

public sealed class ConcentusAudioEncoder : IAudioEncoder
{
    readonly IOpusEncoder _encoder;

    public ConcentusAudioEncoder()
    {
        _encoder = OpusCodecFactory.CreateEncoder(
            AudioFormat.SampleRate, AudioFormat.Channels, OpusApplication.OPUS_APPLICATION_VOIP, null);
        _encoder.Bitrate = AudioFormat.TargetBitrate;
        _encoder.UseVBR = true;
        _encoder.UseDTX = false;
        _encoder.UseInbandFEC = false;
        _encoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
    }

    public byte[] Encode(ReadOnlySpan<short> pcm)
    {
        if (pcm.Length != AudioFormat.SamplesPerFrame)
            throw new ArgumentException($"Expected exactly {AudioFormat.SamplesPerFrame} mono samples.", nameof(pcm));
        Span<byte> encoded = stackalloc byte[AudioPacketProtector.MaxOpusPayloadSize];
        var length = _encoder.Encode(pcm, AudioFormat.SamplesPerFrame, encoded, encoded.Length);
        return encoded[..length].ToArray();
    }
}

public sealed class ConcentusAudioDecoder : IAudioDecoder
{
    readonly IOpusDecoder _decoder = OpusCodecFactory.CreateDecoder(AudioFormat.SampleRate, AudioFormat.Channels, null);

    public short[] Decode(ReadOnlySpan<byte> packet) => DecodeCore(packet);
    public short[] ConcealLoss() => DecodeCore(ReadOnlySpan<byte>.Empty);

    short[] DecodeCore(ReadOnlySpan<byte> packet)
    {
        var pcm = new short[AudioFormat.SamplesPerFrame];
        var samples = _decoder.Decode(packet, pcm, AudioFormat.SamplesPerFrame, false);
        if (samples != AudioFormat.SamplesPerFrame) Array.Resize(ref pcm, samples);
        return pcm;
    }
}
