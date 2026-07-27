using Intercom.ControlChannel;

namespace Intercom.App.Tests.Pairing;

/// <summary>
/// A hand-written in-memory <see cref="IPairingTransport"/> fake, in the
/// same spirit as <c>PeerControlChannelTests</c>' FakeTransportConnection —
/// no mocking framework, per this codebase's test convention. Two instances
/// can be cross-linked via <see cref="Peer"/> to simulate a full two-sided
/// ceremony without any real socket.
/// </summary>
sealed class FakePairingTransport : Intercom.Pairing.IPairingTransport
{
    public FakePairingTransport? Peer { get; set; }
    public List<ControlFrame> Sent { get; } = [];

    public event Action<ControlFrame>? FrameReceived;

    public Task SendAsync(ControlFrame frame, CancellationToken cancellationToken)
    {
        Sent.Add(frame);
        Peer?.Deliver(frame);
        return Task.CompletedTask;
    }

    public void Deliver(ControlFrame frame) => FrameReceived?.Invoke(frame);
}
