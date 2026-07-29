namespace Intercom.Audio;

public enum AudioSessionState { Stopped, Starting, Running, Degraded, Stopping, Failed }

public sealed class AudioSessionStateMachine
{
    readonly object _gate = new();
    public AudioSessionState State { get; private set; } = AudioSessionState.Stopped;
    public event Action<AudioSessionState, AudioSessionState>? StateChanged;

    public bool Start() => Transition(AudioSessionState.Stopped, AudioSessionState.Starting);
    public bool Started() => Transition(AudioSessionState.Starting, AudioSessionState.Running);
    public bool Degrade() => Transition(AudioSessionState.Running, AudioSessionState.Degraded);
    public bool Recover() => Transition(AudioSessionState.Degraded, AudioSessionState.Running);

    public bool BeginStop()
    {
        lock (_gate)
        {
            if (State is AudioSessionState.Stopped or AudioSessionState.Stopping) return false;
            return SetLocked(AudioSessionState.Stopping);
        }
    }

    public bool Stopped() => Transition(AudioSessionState.Stopping, AudioSessionState.Stopped);

    public bool Fail()
    {
        lock (_gate)
        {
            if (State == AudioSessionState.Failed) return false;
            return SetLocked(AudioSessionState.Failed);
        }
    }

    bool Transition(AudioSessionState expected, AudioSessionState next)
    {
        lock (_gate) return State == expected && SetLocked(next);
    }

    bool SetLocked(AudioSessionState next)
    {
        var previous = State;
        State = next;
        StateChanged?.Invoke(previous, next);
        return true;
    }
}
