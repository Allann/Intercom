namespace Intercom.Audio;

/// <summary>64-packet sliding replay window. Reordering within the window is
/// accepted once; duplicates and packets older than the window are rejected.</summary>
public sealed class AudioReplayWindow
{
    readonly object _gate = new();
    ulong _highest;
    ulong _seen;
    bool _initialized;

    public bool CanAccept(ulong sequence)
    {
        lock (_gate) return CanAcceptLocked(sequence);
    }

    public bool Accept(ulong sequence)
    {
        lock (_gate)
        {
            if (!CanAcceptLocked(sequence)) return false;
            if (!_initialized)
            {
                _initialized = true;
                _highest = sequence;
                _seen = 1;
            }
            else if (sequence > _highest)
            {
                var shift = sequence - _highest;
                _seen = shift >= 64 ? 1 : (_seen << (int)shift) | 1;
                _highest = sequence;
            }
            else
            {
                _seen |= 1UL << (int)(_highest - sequence);
            }
            return true;
        }
    }

    bool CanAcceptLocked(ulong sequence)
    {
        if (!_initialized || sequence > _highest) return true;
        var age = _highest - sequence;
        return age < 64 && (_seen & (1UL << (int)age)) == 0;
    }
}
