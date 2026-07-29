using Intercom.ControlChannel;

namespace Intercom.Audio;

public interface IAudioControlTransport
{
    event Action<ControlFrame>? FrameReceived;
    event Action? ConnectionDropped;
    Task SendAsync(ControlFrame frame, CancellationToken cancellationToken);
}
