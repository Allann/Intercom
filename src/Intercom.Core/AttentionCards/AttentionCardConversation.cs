namespace Intercom.AttentionCards;

/// <summary>
/// The card history and delivery-/ack-state bookkeeping for one two-party
/// attention-card exchange — pure policy, no I/O, no transport, fully unit
/// testable. Mirrors <c>Intercom.Chat.ChatConversation</c>'s split from
/// <see cref="AttentionCardService"/> (which actually sends/receives frames
/// and drives this class), but tracks TWO independent receipts per card
/// instead of chat's one: the mechanical <see cref="AttentionCardDeliveryState"/>
/// (identical to chat's delivery tracking) and the human-triggered
/// <see cref="AttentionCardAckState"/> (issue #25's addition on top).
///
/// Guarded by <see cref="_gate"/>, never held across an event raise —
/// matches this codebase's established discipline (<c>ChatConversation</c>,
/// <c>PresenceEngine</c>, <c>IdentityStore</c>).
/// </summary>
public sealed class AttentionCardConversation
{
    readonly object _gate = new();
    readonly List<AttentionCard> _cards = [];
    readonly Func<DateTimeOffset> _clock;

    public AttentionCardConversation(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Raised whenever a brand new card (sent or received) is
    /// added.</summary>
    public event Action<AttentionCard>? CardAdded;

    /// <summary>Raised whenever an existing card's <see cref="AttentionCard.DeliveryState"/>
    /// or <see cref="AttentionCard.AckState"/> changes.</summary>
    public event Action<AttentionCard>? CardUpdated;

    public IReadOnlyList<AttentionCard> Cards
    {
        get { lock (_gate) { return _cards.ToArray(); } }
    }

    /// <summary>Records a card this side is sending, starting out
    /// <see cref="AttentionCardDeliveryState.Pending"/> /
    /// <see cref="AttentionCardAckState.NotAcknowledged"/>.</summary>
    public AttentionCard AddOutbound(Guid messageId, string purpose, string icon)
    {
        var card = new AttentionCard
        {
            MessageId = messageId,
            Direction = AttentionCardDirection.Sent,
            Purpose = purpose,
            Icon = icon,
            Timestamp = _clock(),
            DeliveryState = AttentionCardDeliveryState.Pending,
            AckState = AttentionCardAckState.NotAcknowledged,
        };
        lock (_gate) { _cards.Add(card); }
        CardAdded?.Invoke(card);
        return card;
    }

    /// <summary>Records a card that arrived from the peer. Arrival IS
    /// delivery for a received card (mirrors chat) — Ack starts
    /// <see cref="AttentionCardAckState.NotAcknowledged"/> until the local
    /// human explicitly acts on it.</summary>
    public AttentionCard AddInbound(Guid messageId, string purpose, string icon)
    {
        var card = new AttentionCard
        {
            MessageId = messageId,
            Direction = AttentionCardDirection.Received,
            Purpose = purpose,
            Icon = icon,
            Timestamp = _clock(),
            DeliveryState = AttentionCardDeliveryState.Delivered,
            AckState = AttentionCardAckState.NotAcknowledged,
        };
        lock (_gate) { _cards.Add(card); }
        CardAdded?.Invoke(card);
        return card;
    }

    /// <summary>Transitions a still-Pending sent card to
    /// <see cref="AttentionCardDeliveryState.Delivered"/> — the mechanical
    /// Delivered receipt arrived while still connected. A no-op (no event
    /// raised) if the card isn't found or is no longer Pending.</summary>
    public void MarkDelivered(Guid messageId) => TransitionPendingDelivery(messageId, AttentionCardDeliveryState.Delivered);

    /// <summary>Transitions a single still-Pending sent card straight to
    /// <see cref="AttentionCardDeliveryState.Undelivered"/> — used when the
    /// send itself fails immediately (e.g. not currently connected).</summary>
    public void MarkUndelivered(Guid messageId) => TransitionPendingDelivery(messageId, AttentionCardDeliveryState.Undelivered);

    /// <summary>Transitions every currently-Pending sent card to
    /// <see cref="AttentionCardDeliveryState.Undelivered"/> in one pass —
    /// called when the connection drops with one or more sends still in
    /// flight (ADR-0001: no auto-resend).</summary>
    public void MarkAllPendingUndelivered()
    {
        List<AttentionCard> updated = [];
        lock (_gate)
        {
            for (var i = 0; i < _cards.Count; i++)
            {
                var existing = _cards[i];
                if (existing.Direction != AttentionCardDirection.Sent || existing.DeliveryState != AttentionCardDeliveryState.Pending) continue;

                var next = existing with { DeliveryState = AttentionCardDeliveryState.Undelivered };
                _cards[i] = next;
                updated.Add(next);
            }
        }
        foreach (var card in updated) CardUpdated?.Invoke(card);
    }

    /// <summary>Transitions one card — sent OR received, found purely by
    /// MessageId — from <see cref="AttentionCardAckState.NotAcknowledged"/>
    /// to <see cref="AttentionCardAckState.Acknowledged"/>. This single
    /// method covers both real-world triggers: <see cref="AttentionCardService"/>
    /// calls it for a RECEIVED card the instant the local human clicks
    /// Acknowledge (before the outbound Acknowledged frame is even sent —
    /// see that method's doc), and again for a SENT card when the peer's
    /// inbound Acknowledged frame arrives back. Idempotent: a no-op (returns
    /// null, raises no event) if the card isn't found or is already
    /// Acknowledged — callers use the null return to avoid sending a
    /// redundant Acknowledged frame for a card that's already been acted on.
    /// A no-op is also silently correct behavior on its own (e.g. a duplicate
    /// inbound Acknowledged frame racing itself), not just a caller
    /// convenience.</summary>
    public AttentionCard? MarkAcknowledged(Guid messageId)
    {
        AttentionCard? updated = null;
        lock (_gate)
        {
            var index = _cards.FindIndex(c => c.MessageId == messageId);
            if (index < 0) return null;

            var existing = _cards[index];
            if (existing.AckState != AttentionCardAckState.NotAcknowledged) return null;

            updated = existing with { AckState = AttentionCardAckState.Acknowledged };
            _cards[index] = updated;
        }
        CardUpdated?.Invoke(updated);
        return updated;
    }

    void TransitionPendingDelivery(Guid messageId, AttentionCardDeliveryState newState)
    {
        AttentionCard? updated = null;
        lock (_gate)
        {
            var index = _cards.FindIndex(c => c.MessageId == messageId && c.Direction == AttentionCardDirection.Sent);
            if (index < 0) return;

            var existing = _cards[index];
            if (existing.DeliveryState != AttentionCardDeliveryState.Pending) return;

            updated = existing with { DeliveryState = newState };
            _cards[index] = updated;
        }
        if (updated is not null) CardUpdated?.Invoke(updated);
    }
}
