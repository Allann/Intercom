namespace Intercom.AttentionCards;

/// <summary>Which side of the exchange an <see cref="AttentionCard"/> came
/// from.</summary>
public enum AttentionCardDirection
{
    Sent,
    Received,
}

/// <summary>A card's mechanical delivery status — identical in shape and
/// meaning to <c>Intercom.Chat.ChatDeliveryState</c> (ADR-0001: every
/// message with a message ID gets this automatically, no human involved).
/// Deliberately separate from <see cref="AttentionCardAckState"/> below —
/// that is the OTHER, human-triggered receipt this ticket adds on top.</summary>
public enum AttentionCardDeliveryState
{
    /// <summary>Sent, mechanical Delivered receipt not yet observed.</summary>
    Pending,

    /// <summary>Either the mechanical Delivered receipt arrived (sent card),
    /// or this is a received card (arrival = delivery).</summary>
    Delivered,

    /// <summary>The connection dropped (or the send itself failed) before a
    /// Delivered receipt arrived. Terminal — ADR-0001: no auto-resend; the
    /// user must send the card again as a brand new one.</summary>
    Undelivered,
}

/// <summary>Whether a human has explicitly acted on this card yet
/// (ADR-0001: "Acknowledged is sent ONLY for attention cards, ONLY when a
/// human actually acts on it — never automatic, unlike Delivered"). Applies
/// to both directions: for a <see cref="AttentionCardDirection.Received"/>
/// card, this flips when the LOCAL human clicks Acknowledge (which sends the
/// Acknowledged frame out); for a <see cref="AttentionCardDirection.Sent"/>
/// card, this flips when the PEER's human does the same and their
/// Acknowledged frame arrives back.</summary>
public enum AttentionCardAckState
{
    NotAcknowledged,
    Acknowledged,
}

/// <summary>One attention card exchanged with a specific approved peer
/// (issue #25 is not group cards — #29/#30's job, same one-to-one scope
/// discipline as issue #24's chat).</summary>
public sealed record AttentionCard
{
    public required Guid MessageId { get; init; }
    public required AttentionCardDirection Direction { get; init; }
    public required string Purpose { get; init; }
    public required string Icon { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required AttentionCardDeliveryState DeliveryState { get; init; }
    public required AttentionCardAckState AckState { get; init; }
}
