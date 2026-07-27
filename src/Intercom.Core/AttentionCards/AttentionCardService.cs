using Intercom.ControlChannel;

namespace Intercom.AttentionCards;

/// <summary>
/// Wires one <see cref="IAttentionCardTransport"/> to one
/// <see cref="AttentionCardConversation"/> — sending, decoding inbound
/// frames, and reacting to delivery/acknowledgement confirmation / connection
/// drop — for a single two-party exchange (issue #25 is not group cards).
/// Mirrors <c>Intercom.Chat.ChatService</c>'s role, with one addition:
/// <see cref="AcknowledgeAsync"/>, the explicit human-triggered send that has
/// no chat equivalent (ADR-0001: chat has delivery only, no read receipt).
///
/// A malformed inbound AttentionCard frame (see
/// <see cref="AttentionCardFrameCodec.Decode"/>) is dropped rather than
/// surfaced as a card — mirrors <c>ChatService</c>'s identical reasoning: by
/// the time a frame reaches here it already passed <c>FrameDispatcher</c>'s
/// checks, so a codec-level decode failure indicates a same-version peer bug,
/// not an attack to react to by tearing down the connection.
/// </summary>
public sealed class AttentionCardService
{
    readonly IAttentionCardTransport _transport;

    public AttentionCardConversation Conversation { get; }

    /// <summary>Raised for every inbound card actually added to
    /// <see cref="Conversation"/> — the caller (shelf UI / native toast /
    /// DND chime gating) decides what to do about it; this class makes no
    /// chime/toast decisions of its own (mirrors <c>ChatService.MessageReceived</c>'s
    /// identical separation of concerns).</summary>
    public event Action<AttentionCard>? CardReceived;

    public AttentionCardService(IAttentionCardTransport transport, AttentionCardConversation? conversation = null)
    {
        _transport = transport;
        Conversation = conversation ?? new AttentionCardConversation();

        _transport.FrameReceived += OnFrameReceived;
        _transport.DeliveryConfirmed += Conversation.MarkDelivered;
        _transport.Acknowledged += OnAcknowledgedReceived;
        _transport.ConnectionDropped += Conversation.MarkAllPendingUndelivered;
    }

    void OnFrameReceived(ControlFrame frame)
    {
        string purpose, icon;
        try
        {
            (purpose, icon) = AttentionCardFrameCodec.Decode(frame);
        }
        catch (MalformedFrameException)
        {
            return;
        }

        var card = Conversation.AddInbound(frame.MessageId, purpose, icon);
        CardReceived?.Invoke(card);
    }

    // The peer's human acknowledged one of OUR sent cards — just bookkeeping,
    // nothing further to send back.
    void OnAcknowledgedReceived(Guid correlationId) => Conversation.MarkAcknowledged(correlationId);

    /// <summary>Sends <paramref name="purpose"/>/<paramref name="icon"/> as a
    /// new outbound attention card. Immediately recorded as
    /// <see cref="AttentionCardDeliveryState.Pending"/>; if the send itself
    /// throws (e.g. not currently connected), the card is immediately marked
    /// <see cref="AttentionCardDeliveryState.Undelivered"/> and the exception
    /// is rethrown for the caller to react to — ADR-0001: no auto-resend, the
    /// user must explicitly send the card again.</summary>
    public async Task<AttentionCard> SendAsync(string purpose, string icon, CancellationToken cancellationToken)
    {
        var messageId = Guid.NewGuid();
        var frame = AttentionCardFrameCodec.ToFrame(purpose, icon, messageId);
        var card = Conversation.AddOutbound(messageId, purpose, icon);

        try
        {
            await _transport.SendAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Conversation.MarkUndelivered(messageId);
            throw;
        }

        return card;
    }

    /// <summary>The one human-triggered action this ticket adds on top of
    /// mechanical delivery (ADR-0001): explicitly acknowledges a RECEIVED
    /// card by MessageId, sending an <see cref="ControlMessageType.Acknowledged"/>
    /// frame whose CorrelationId is that card's MessageId. Local Ack state
    /// is flipped first via <see cref="AttentionCardConversation.MarkAcknowledged"/>
    /// — that call is idempotent and returns null if the card was already
    /// acknowledged (or doesn't exist), which this method uses as the signal
    /// to skip sending a redundant frame entirely; a human can only
    /// meaningfully acknowledge a card once. A card that's this side's own
    /// SENT card is a no-op too (defensive: only a RECEIVED card is ever
    /// something a local human acknowledges — the composer/shelf UI never
    /// offers an Ack affordance for a card this side sent). Unlike
    /// <see cref="SendAsync"/>, a failed send here does NOT roll back the
    /// local Ack state — the human genuinely acted, and ADR-0001 gives
    /// Acknowledged no separate retry story of its own; the exception is
    /// simply rethrown for the caller to surface.</summary>
    public async Task AcknowledgeAsync(Guid cardMessageId, CancellationToken cancellationToken)
    {
        // Direction is checked BEFORE mutating anything — MarkAcknowledged
        // is not undone on a wrong-direction bail, so this ordering matters:
        // a defensive no-op must never leave a sent card's AckState flipped
        // to Acknowledged without an Acknowledged frame ever having been
        // sent for it.
        var existing = Conversation.Cards.FirstOrDefault(c => c.MessageId == cardMessageId);
        if (existing is null || existing.Direction != AttentionCardDirection.Received) return;

        var card = Conversation.MarkAcknowledged(cardMessageId);
        if (card is null) return; // already acknowledged (or removed) — no redundant frame

        var frame = new ControlFrame
        {
            Type = ControlMessageType.Acknowledged,
            MessageId = Guid.NewGuid(),
            CorrelationId = cardMessageId,
            Payload = [],
        };
        await _transport.SendAsync(frame, cancellationToken).ConfigureAwait(false);
    }
}
