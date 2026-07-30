using Intercom.ControlChannel;

namespace Intercom.GroupVoice;

public interface IGroupFloorTransport
{
    event Action<Guid, ControlFrame>? FrameReceived;
    Task BroadcastAsync(ControlFrame frame, CancellationToken cancellationToken);
}

/// <summary>Prepares the pairwise audio paths at group join. Floor grants do
/// not call this interface, which makes hand-off a pure control-plane change.</summary>
public interface IGroupAudioPreparer
{
    Task PrepareAsync(Guid peerId, CancellationToken cancellationToken);
}
