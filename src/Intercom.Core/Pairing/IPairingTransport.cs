using Intercom.ControlChannel;

namespace Intercom.Pairing;

/// <summary>
/// The narrow send/receive seam <see cref="PairingCeremonyCoordinator"/>
/// needs from a live <see cref="ConnectionTrust.PairingOnly"/> connection —
/// deliberately just <c>SendAsync</c>/<c>FrameReceived</c>, not the whole of
/// <see cref="PeerControlChannel"/>, so the coordinator can be driven by a
/// hand-written fake in tests (mirroring <c>IPeerTransportConnection</c>'s
/// role for <c>PeerControlChannel</c> itself) and, in the WinUI layer, by an
/// in-memory loopback pair for the on-device pairing demonstration when no
/// second real device is available.
/// </summary>
public interface IPairingTransport
{
    /// <summary>Raised for every inbound frame already past
    /// <c>FrameDispatcher</c>'s ordering/trust checks — i.e. exactly the
    /// pairing message types a PairingOnly connection permits.</summary>
    event Action<ControlFrame>? FrameReceived;

    Task SendAsync(ControlFrame frame, CancellationToken cancellationToken);
}

/// <summary>Adapts a real <see cref="PeerControlChannel"/> to
/// <see cref="IPairingTransport"/> for production wiring.</summary>
public sealed class PeerControlChannelPairingTransport : IPairingTransport
{
    readonly PeerControlChannel _channel;

    public PeerControlChannelPairingTransport(PeerControlChannel channel)
    {
        _channel = channel;
        _channel.MessageReceived += frame => FrameReceived?.Invoke(frame);
    }

    public event Action<ControlFrame>? FrameReceived;

    public Task SendAsync(ControlFrame frame, CancellationToken cancellationToken) =>
        _channel.SendAsync(frame, cancellationToken);
}

/// <summary>
/// An in-memory, no-socket <see cref="IPairingTransport"/> that delivers
/// everything sent on one side straight to <see cref="Peer"/>'s
/// <see cref="FrameReceived"/>. Not used by any real peer-to-peer connection
/// — its purpose is letting a single device demonstrate a full, real
/// ceremony (real nonces, real transcript/rejection sampling, real
/// <see cref="PairingCeremony"/> state, real <see cref="IdentityStore.Approve"/>
/// call) end to end when no second physical device is available, e.g. the
/// WinUI Postcard Badges dialog's "simulate a pairing request" action. Two
/// instances are always created and cross-linked as a pair; delivery is
/// synchronous and in-process, so it carries none of a real connection's
/// latency, ordering, or failure modes.
/// </summary>
public sealed class LoopbackPairingTransport : IPairingTransport
{
    public LoopbackPairingTransport? Peer { get; set; }

    public event Action<ControlFrame>? FrameReceived;

    public Task SendAsync(ControlFrame frame, CancellationToken cancellationToken)
    {
        Peer?.Deliver(frame);
        return Task.CompletedTask;
    }

    void Deliver(ControlFrame frame) => FrameReceived?.Invoke(frame);

    /// <summary>Creates and cross-links a ready-to-use pair.</summary>
    public static (LoopbackPairingTransport A, LoopbackPairingTransport B) CreatePair()
    {
        var a = new LoopbackPairingTransport();
        var b = new LoopbackPairingTransport();
        a.Peer = b;
        b.Peer = a;
        return (a, b);
    }
}
