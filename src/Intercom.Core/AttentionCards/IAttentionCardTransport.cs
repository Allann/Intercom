using Intercom.ControlChannel;

namespace Intercom.AttentionCards;

/// <summary>
/// The narrow send/receive/receipt seam <see cref="AttentionCardService"/>
/// needs from a live connection — mirrors <c>Intercom.Chat.IChatTransport</c>'s
/// role for <c>ChatService</c>, plus one extra event: attention cards need
/// TWO distinct receipt channels (mechanical <see cref="DeliveryConfirmed"/>
/// and human-triggered <see cref="Acknowledged"/>), where chat only ever
/// needed the first. Lets <see cref="AttentionCardService"/> be driven by a
/// hand-written fake in tests and, in the WinUI layer, by an in-memory
/// loopback pair for the on-device demonstration when no second real device
/// is available (see <see cref="LoopbackAttentionCardTransport"/>).
/// </summary>
public interface IAttentionCardTransport
{
    /// <summary>Raised for every inbound AttentionCard-type frame already
    /// past <c>FrameDispatcher</c>'s ordering/trust checks.</summary>
    event Action<ControlFrame>? FrameReceived;

    /// <summary>Raised with the MessageId of one of this side's own
    /// previously-sent frames (a card OR an Acknowledged receipt — every
    /// message type gets this mechanically per ADR-0001) the instant the
    /// peer's mechanical Delivered receipt for it arrives.</summary>
    event Action<Guid>? DeliveryConfirmed;

    /// <summary>Raised with the MessageId of one of this side's own
    /// previously-sent CARD frames the instant the peer's human-triggered
    /// Acknowledged receipt for it arrives — issue #25's addition on top of
    /// <see cref="DeliveryConfirmed"/>, never raised automatically.</summary>
    event Action<Guid>? Acknowledged;

    /// <summary>Issue #30: raised with the interaction ID (the MessageId
    /// shared by every fanned-out copy of one card) the instant the peer's
    /// <see cref="ControlChannel.ControlMessageType.Resolved"/> withdrawal
    /// broadcast for it arrives — the receiving side's signal to withdraw a
    /// duplicate notification for an interaction someone else already
    /// acknowledged (docs/research/active-device-presence.md: "the original
    /// sender then broadcasts a resolved(interaction_id) message to the
    /// contact's other live devices so they withdraw duplicate
    /// notifications"). See <c>Intercom.Routing.AttentionCardFanoutRouter</c>.</summary>
    event Action<Guid>? Resolved;

    /// <summary>Raised when the underlying connection is no longer usable —
    /// any card still Pending at that instant must be shown Undelivered
    /// (ADR-0001: no auto-resend). Never raised by
    /// <see cref="LoopbackAttentionCardTransport"/>, which has no real
    /// connection to drop.</summary>
    event Action? ConnectionDropped;

    Task SendAsync(ControlFrame frame, CancellationToken cancellationToken);
}

/// <summary>Adapts a real <see cref="PeerControlChannel"/> to
/// <see cref="IAttentionCardTransport"/> for production wiring.</summary>
public sealed class PeerControlChannelAttentionCardTransport : IAttentionCardTransport
{
    readonly PeerControlChannel _channel;

    public PeerControlChannelAttentionCardTransport(PeerControlChannel channel)
    {
        _channel = channel;
        _channel.MessageReceived += OnMessageReceived;
        _channel.DeliveryConfirmed += id => DeliveryConfirmed?.Invoke(id);
        _channel.StateChanged += (previous, next) =>
        {
            // Only a genuine drop away FROM Connected counts, mirroring
            // PeerControlChannelChatTransport's identical reasoning.
            if (previous is ConnState.Connected && next is not ConnState.Connected)
            {
                ConnectionDropped?.Invoke();
            }
        };
    }

    public event Action<ControlFrame>? FrameReceived;
    public event Action<Guid>? DeliveryConfirmed;
    public event Action<Guid>? Acknowledged;
    public event Action<Guid>? Resolved;
    public event Action? ConnectionDropped;

    void OnMessageReceived(ControlFrame frame)
    {
        if (frame.Type == ControlMessageType.AttentionCard) { FrameReceived?.Invoke(frame); return; }
        if (frame.Type == ControlMessageType.Acknowledged) { RaiseCorrelated(frame, Acknowledged); return; }
        if (frame.Type == ControlMessageType.Resolved) RaiseCorrelated(frame, Resolved);
    }

    static void RaiseCorrelated(ControlFrame frame, Action<Guid>? receive)
    {
        if (frame.CorrelationId is Guid id) receive?.Invoke(id);
    }

    public Task SendAsync(ControlFrame frame, CancellationToken cancellationToken) =>
        _channel.SendAsync(frame, cancellationToken);
}

/// <summary>
/// An in-memory, no-socket <see cref="IAttentionCardTransport"/> for the
/// WinUI attention-card shelf/composer's on-device demonstration, when no
/// second physical device is available — the attention-card equivalent of
/// <c>Intercom.Chat.LoopbackChatTransport</c> (see that type's doc for the
/// precedent this follows). Two instances are always created and
/// cross-linked as a pair.
///
/// Built on a REAL <see cref="FrameDispatcher"/> per side (each pre-seeded
/// with one bootstrap Hello) so the demo's mechanical Delivered receipt for
/// both card sends AND Acknowledged sends is genuinely produced by
/// <see cref="FrameDispatcher"/> production code, not reimplemented — same
/// "real ceremony over real protocol code" bar #22/#24 already set.
/// </summary>
public sealed class LoopbackAttentionCardTransport : IAttentionCardTransport
{
    readonly FrameDispatcher _dispatcher;

    public LoopbackAttentionCardTransport? Peer { get; set; }

    public event Action<ControlFrame>? FrameReceived;
    public event Action<Guid>? DeliveryConfirmed;
    public event Action<Guid>? Acknowledged;
    public event Action<Guid>? Resolved;

    /// <summary>Only ever raised by <see cref="SimulateDrop"/> — a loopback
    /// pair has no real socket to drop on its own.</summary>
    public event Action? ConnectionDropped;

    public LoopbackAttentionCardTransport(Guid? peerId = null)
    {
        _dispatcher = new FrameDispatcher(new ConnectionTrust.Approved { PeerId = peerId ?? Guid.NewGuid() });
        _dispatcher.FrameAccepted += OnFrameAccepted;
        _dispatcher.DeliveredReceiptReady += receipt => Peer?.DispatchInbound(receipt);

        // Bootstraps past FrameDispatcher's "Hello must be first" rule —
        // mirrors LoopbackChatTransport's identical bootstrap.
        _dispatcher.Dispatch(Hello.Current(Capability.AttentionCards).ToFrame(Guid.NewGuid()));
    }

    void OnFrameAccepted(ControlFrame frame)
    {
        if (frame.Type == ControlMessageType.Delivered) { Raise(frame, DeliveryConfirmed); return; }
        if (frame.Type == ControlMessageType.Acknowledged) { Raise(frame, Acknowledged); return; }
        if (frame.Type == ControlMessageType.Resolved) { Raise(frame, Resolved); return; }
        if (frame.Type is not ControlMessageType.Hello) FrameReceived?.Invoke(frame);
    }

    static void Raise(ControlFrame frame, Action<Guid>? receive)
    {
        if (frame.CorrelationId is Guid id) receive?.Invoke(id);
    }

    public Task SendAsync(ControlFrame frame, CancellationToken cancellationToken)
    {
        Peer?.DispatchInbound(frame);
        return Task.CompletedTask;
    }

    void DispatchInbound(ControlFrame frame) => _dispatcher.Dispatch(frame);

    /// <summary>Demo-only: fires <see cref="ConnectionDropped"/> so the
    /// shelf can show what a mid-flight drop looks like without a real
    /// socket to actually sever.</summary>
    public void SimulateDrop() => ConnectionDropped?.Invoke();

    /// <summary>Creates and cross-links a ready-to-use pair.</summary>
    public static (LoopbackAttentionCardTransport A, LoopbackAttentionCardTransport B) CreatePair()
    {
        var a = new LoopbackAttentionCardTransport();
        var b = new LoopbackAttentionCardTransport();
        a.Peer = b;
        b.Peer = a;
        return (a, b);
    }
}
