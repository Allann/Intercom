namespace Intercom.Audio;

public static class PcmFrameSizing
{
    public static int ValidCaptureSamples(
        uint bufferBytes,
        uint samplesPerQuantum,
        int channels,
        int bytesPerSample)
    {
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        if (bytesPerSample <= 0) throw new ArgumentOutOfRangeException(nameof(bytesPerSample));
        var available = checked((int)(bufferBytes / (uint)bytesPerSample));
        var quantum = checked((int)samplesPerQuantum * channels);
        return Math.Min(available, quantum);
    }
}
