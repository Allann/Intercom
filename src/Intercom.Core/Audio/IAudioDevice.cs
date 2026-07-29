namespace Intercom.Audio;

public interface IAudioDevice : IAsyncDisposable
{
    string InputDeviceName { get; }
    string OutputDeviceName { get; }
    event Action<short[]>? Captured;
    event Action<Exception>? DeviceFailed;
    Task StartAsync();
    void QueuePlayback(short[] pcm);
}
