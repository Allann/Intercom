namespace Intercom.Audio;

public static class FloatPcmConverter
{
    public static short[] ToPcm16(ReadOnlySpan<float> source)
    {
        var destination = new short[source.Length];
        for (var index = 0; index < source.Length; index++)
        {
            var normalized = Math.Clamp(source[index], -1f, 1f);
            destination[index] = checked((short)MathF.Round(normalized * short.MaxValue));
        }
        return destination;
    }

    public static void ToFloat(ReadOnlySpan<short> source, Span<float> destination)
    {
        if (destination.Length < source.Length)
            throw new ArgumentException("The destination is smaller than the source.", nameof(destination));

        for (var index = 0; index < source.Length; index++)
            destination[index] = Math.Clamp(source[index] / (float)short.MaxValue, -1f, 1f);
    }
}
