namespace Intercom.Audio;

/// <summary>Bounded FIFO of decoded PCM samples. Render callbacks may request
/// a different quantum size than the codec frame, so unread samples remain
/// available for the next callback.</summary>
public sealed class PcmPlaybackBuffer
{
    readonly object _gate = new();
    readonly Queue<short> _samples = [];
    readonly int _capacity;

    public PcmPlaybackBuffer(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public int Count { get { lock (_gate) return _samples.Count; } }

    public void Enqueue(ReadOnlySpan<short> samples)
    {
        lock (_gate)
        {
            foreach (var sample in samples) _samples.Enqueue(sample);
            while (_samples.Count > _capacity) _samples.Dequeue();
        }
    }

    public int Dequeue(Span<short> destination)
    {
        lock (_gate)
        {
            var count = Math.Min(destination.Length, _samples.Count);
            for (var i = 0; i < count; i++) destination[i] = _samples.Dequeue();
            return count;
        }
    }

    public void Clear()
    {
        lock (_gate) _samples.Clear();
    }
}
