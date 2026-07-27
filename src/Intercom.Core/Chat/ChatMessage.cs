namespace Intercom.Chat;

/// <summary>Which side of the conversation a <see cref="ChatMessage"/> came
/// from.</summary>
public enum ChatMessageDirection
{
    Sent,
    Received,
}

/// <summary>
/// A sent message's delivery status (issue #24's acceptance criterion: "show
/// per-message delivery state"). ADR-0001: delivery-only confirmation, no
/// read receipt — there is deliberately no "Read" state.
/// <see cref="Received"/> messages are always reported <see cref="Delivered"/>
/// (their arrival IS their delivery — there is nothing further to track).
/// </summary>
public enum ChatDeliveryState
{
    /// <summary>Sent, mechanical Delivered receipt not yet observed.</summary>
    Pending,

    /// <summary>Either the mechanical Delivered receipt arrived (sent
    /// message), or this is a received message (arrival = delivery).</summary>
    Delivered,

    /// <summary>The connection dropped (or the send itself failed) before a
    /// Delivered receipt arrived. Terminal — ADR-0001: no auto-resend; the
    /// user must send the text again as a brand new message.</summary>
    Undelivered,
}

/// <summary>One chat message in a two-party conversation (issue #24 is not
/// group chat).</summary>
public sealed record ChatMessage
{
    public required Guid MessageId { get; init; }
    public required ChatMessageDirection Direction { get; init; }
    public required string Text { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required ChatDeliveryState DeliveryState { get; init; }
}
