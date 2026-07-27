using Intercom.ControlChannel;

namespace Intercom.Chat;

/// <summary>
/// The narrow send/receive/delivery-confirmation seam <see cref="ChatService"/>
/// needs from a live connection — deliberately just what chat needs, not the
/// whole of <see cref="PeerControlChannel"/>, mirroring
/// <c>Intercom.Pairing.IPairingTransport</c>'s role for
/// <c>PairingCeremonyCoordinator</c>. Lets <see cref="ChatService"/> be driven
/// by a hand-written fake in tests and, in the WinUI layer, by an in-memory
/// loopback pair for the on-device chat demonstration when no second real
/// device is available (see <see cref="LoopbackChatTransport"/>).
/// </summary>
public interface IChatTransport
{
    /// <summary>Raised for every inbound Chat-type frame already past
    /// <c>FrameDispatcher</c>'s ordering/trust checks.</summary>
    event Action<ControlFrame>? FrameReceived;

    /// <summary>Raised with the MessageId of one of this side's own
    /// previously-sent frames the instant the peer's mechanical Delivered
    /// receipt for it arrives.</summary>
    event Action<Guid>? DeliveryConfirmed;

    /// <summary>Raised when the underlying connection is no longer usable —
    /// any message still Pending at that instant must be shown Undelivered
    /// (ADR-0001: no auto-resend). Never raised by <see cref="LoopbackChatTransport"/>,
    /// which has no real connection to drop.</summary>
    event Action? ConnectionDropped;

    Task SendAsync(ControlFrame frame, CancellationToken cancellationToken);
}

/// <summary>Adapts a real <see cref="PeerControlChannel"/> to
/// <see cref="IChatTransport"/> for production wiring.</summary>
public sealed class PeerControlChannelChatTransport : IChatTransport
{
    readonly PeerControlChannel _channel;

    public PeerControlChannelChatTransport(PeerControlChannel channel)
    {
        _channel = channel;
        _channel.MessageReceived += frame =>
        {
            if (frame.Type == ControlMessageType.Chat) FrameReceived?.Invoke(frame);
        };
        _channel.DeliveryConfirmed += id => DeliveryConfirmed?.Invoke(id);
        _channel.StateChanged += (previous, next) =>
        {
            // Only a genuine drop away FROM Connected counts — StateChanged
            // fires on every transition (Idle -> Discovered, Discovered ->
            // Connecting, etc.), and none of those on their own mean a
            // message that was actually in flight lost its connection.
            if (previous is ConnState.Connected && next is not ConnState.Connected)
            {
                ConnectionDropped?.Invoke();
            }
        };
    }

    public event Action<ControlFrame>? FrameReceived;
    public event Action<Guid>? DeliveryConfirmed;
    public event Action? ConnectionDropped;

    public Task SendAsync(ControlFrame frame, CancellationToken cancellationToken) =>
        _channel.SendAsync(frame, cancellationToken);
}

/// <summary>
/// An in-memory, no-socket <see cref="IChatTransport"/> for the WinUI chat
/// drawer's on-device demonstration, when no second physical device is
/// available — the chat equivalent of <c>Intercom.Pairing.LoopbackPairingTransport</c>
/// (see that type's doc for the precedent this follows). Two instances are
/// always created and cross-linked as a pair.
///
/// Deliberately built on a REAL <see cref="FrameDispatcher"/> per side
/// (each pre-seeded with one bootstrap Hello, exactly like a real handshake
/// would have already completed before <see cref="ChatService"/> ever sees
/// the connection) rather than delivering frames straight into
/// <see cref="FrameReceived"/> — that is what makes the demo's mechanical
/// Delivered receipt genuinely produced by <see cref="FrameDispatcher"/>
/// production code, not reimplemented or faked, satisfying the same "real
/// ceremony over real protocol code, just against a stand-in second
/// endpoint" bar #22's Postcard Badges dialog set.
/// </summary>
public sealed class LoopbackChatTransport : IChatTransport
{
    readonly FrameDispatcher _dispatcher;

    public LoopbackChatTransport? Peer { get; set; }

    public event Action<ControlFrame>? FrameReceived;
    public event Action<Guid>? DeliveryConfirmed;

    /// <summary>Only ever raised by <see cref="SimulateDrop"/> — a loopback
    /// pair has no real socket to drop on its own, so this is the demo's
    /// explicit stand-in for "the connection just dropped."</summary>
    public event Action? ConnectionDropped;

    public LoopbackChatTransport(Guid? peerId = null)
    {
        _dispatcher = new FrameDispatcher(new ConnectionTrust.Approved { PeerId = peerId ?? Guid.NewGuid() });
        _dispatcher.FrameAccepted += frame =>
        {
            if (frame.Type == ControlMessageType.Delivered)
            {
                if (frame.CorrelationId is Guid correlationId) DeliveryConfirmed?.Invoke(correlationId);
                return;
            }
            if (frame.Type is not ControlMessageType.Hello)
            {
                FrameReceived?.Invoke(frame);
            }
        };
        _dispatcher.DeliveredReceiptReady += receipt => Peer?.DispatchInbound(receipt);

        // Bootstraps past FrameDispatcher's "Hello must be first" rule — a
        // real connection already completed this exchange before ChatService
        // ever sees it (see PeerControlChannel.CompleteHandshakeAsync).
        _dispatcher.Dispatch(Hello.Current(Capability.Text).ToFrame(Guid.NewGuid()));
    }

    public Task SendAsync(ControlFrame frame, CancellationToken cancellationToken)
    {
        Peer?.DispatchInbound(frame);
        return Task.CompletedTask;
    }

    void DispatchInbound(ControlFrame frame) => _dispatcher.Dispatch(frame);

    /// <summary>Demo-only: fires <see cref="ConnectionDropped"/> so the chat
    /// drawer can show what a mid-flight drop looks like without a real
    /// socket to actually sever.</summary>
    public void SimulateDrop() => ConnectionDropped?.Invoke();

    /// <summary>Creates and cross-links a ready-to-use pair.</summary>
    public static (LoopbackChatTransport A, LoopbackChatTransport B) CreatePair()
    {
        var a = new LoopbackChatTransport();
        var b = new LoopbackChatTransport();
        a.Peer = b;
        b.Peer = a;
        return (a, b);
    }
}
