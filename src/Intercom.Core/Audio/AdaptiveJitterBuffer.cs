namespace Intercom.Audio;

public sealed record JitterFrame(ulong Sequence, ulong SampleTimestamp, byte[] Payload, AudioPacketFlags Flags = AudioPacketFlags.None);
public sealed record JitterReadResult(JitterFrame? Frame, bool Conceal);

/// <summary>Bounded 20 ms jitter buffer based on prototype #15. It starts at
/// 60 ms, adapts between 40 and 120 ms, and drops oldest audio on overflow.</summary>
public sealed class AdaptiveJitterBuffer
{
    public const int MinTargetFrames = 2;
    public const int InitialTargetFrames = 3;
    public const int MaxTargetFrames = 6;
    public const int IdleConcealmentLimit = 6;
    const int AdaptWindowFrames = 50;
    const int OverflowHeadroomFrames = 2;

    readonly object _gate = new();
    readonly SortedDictionary<ulong, JitterFrame> _frames = [];
    readonly Queue<bool> _recentConcealment = [];
    ulong _nextSequence;
    bool _started;
    int _consecutiveMissing;

    public int TargetFrames { get; private set; } = InitialTargetFrames;
    public long ConcealedFrames { get; private set; }
    public long DiscardedFrames { get; private set; }
    public int Occupancy { get { lock (_gate) return _frames.Count; } }

    public bool Add(JitterFrame frame)
    {
        lock (_gate)
        {
            if (_started && frame.Sequence < _nextSequence) return false;
            if (!_frames.TryAdd(frame.Sequence, frame)) return false;
            while (_frames.Count > TargetFrames + OverflowHeadroomFrames)
            {
                _frames.Remove(_frames.Keys.First());
                DiscardedFrames++;
            }
            return true;
        }
    }

    public JitterReadResult Read()
    {
        lock (_gate)
        {
            if (!_started)
            {
                if (_frames.Count < TargetFrames) return new(null, false);
                _nextSequence = _frames.Keys.First();
                _started = true;
            }

            var found = _frames.Remove(_nextSequence, out var frame);
            _nextSequence++;
            if (!found)
            {
                ConcealedFrames++;
                _consecutiveMissing++;
            }
            else _consecutiveMissing = 0;
            RecordAndAdapt(!found);
            if (_consecutiveMissing >= IdleConcealmentLimit)
            {
                // PTT silence is not packet loss. Stop advancing the sequence
                // clock so the next burst can establish a fresh playout base.
                _started = false;
                _consecutiveMissing = 0;
                _recentConcealment.Clear();
                TargetFrames = InitialTargetFrames;
            }
            return new(frame, !found);
        }
    }

    void RecordAndAdapt(bool concealed)
    {
        _recentConcealment.Enqueue(concealed);
        if (_recentConcealment.Count > AdaptWindowFrames) _recentConcealment.Dequeue();
        if (_recentConcealment.Count < AdaptWindowFrames) return;
        var concealedCount = _recentConcealment.Count(value => value);
        if (concealedCount > AdaptWindowFrames / 10 && TargetFrames < MaxTargetFrames) TargetFrames++;
        else if (concealedCount == 0 && TargetFrames > MinTargetFrames) TargetFrames--;
    }

    public void Reset()
    {
        lock (_gate)
        {
            _frames.Clear();
            _recentConcealment.Clear();
            _started = false;
            _nextSequence = 0;
            _consecutiveMissing = 0;
            TargetFrames = InitialTargetFrames;
        }
    }
}
