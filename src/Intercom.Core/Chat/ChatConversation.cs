namespace Intercom.Chat;

/// <summary>
/// The message history and delivery-state bookkeeping for one two-party
/// chat conversation — pure policy, no I/O, no transport, fully unit
/// testable (mirrors <see cref="Intercom.ControlChannel.FrameDispatcher"/>'s
/// "pure policy" split from <see cref="Intercom.ControlChannel.PeerControlChannel"/>'s
/// I/O). <see cref="ChatService"/> is the layer that actually sends/receives
/// frames and drives this class.
///
/// Guarded by <see cref="_gate"/>, never held across an event raise —
/// matches this codebase's established discipline (<c>DndSettingsStore</c>,
/// <c>PresenceEngine</c>).
/// </summary>
public sealed class ChatConversation
{
    readonly object _gate = new();
    readonly List<ChatMessage> _messages = [];
    readonly Func<DateTimeOffset> _clock;

    public ChatConversation(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Raised whenever a brand new message (sent or received) is
    /// added.</summary>
    public event Action<ChatMessage>? MessageAdded;

    /// <summary>Raised whenever an existing message's <see cref="ChatMessage.DeliveryState"/>
    /// changes (Pending -> Delivered or Pending -> Undelivered).</summary>
    public event Action<ChatMessage>? MessageUpdated;

    public IReadOnlyList<ChatMessage> Messages
    {
        get { lock (_gate) { return _messages.ToArray(); } }
    }

    /// <summary>Records a message this side is sending, starting out
    /// <see cref="ChatDeliveryState.Pending"/>.</summary>
    public ChatMessage AddOutbound(Guid messageId, string text)
    {
        var message = new ChatMessage
        {
            MessageId = messageId,
            Direction = ChatMessageDirection.Sent,
            Text = text,
            Timestamp = _clock(),
            DeliveryState = ChatDeliveryState.Pending,
        };
        lock (_gate) { _messages.Add(message); }
        MessageAdded?.Invoke(message);
        return message;
    }

    /// <summary>Records a message that arrived from the peer. Arrival IS
    /// delivery for a received message — see <see cref="ChatDeliveryState.Delivered"/>'s
    /// doc.</summary>
    public ChatMessage AddInbound(Guid messageId, string text)
    {
        var message = new ChatMessage
        {
            MessageId = messageId,
            Direction = ChatMessageDirection.Received,
            Text = text,
            Timestamp = _clock(),
            DeliveryState = ChatDeliveryState.Delivered,
        };
        lock (_gate) { _messages.Add(message); }
        MessageAdded?.Invoke(message);
        return message;
    }

    /// <summary>Transitions a still-<see cref="ChatDeliveryState.Pending"/>
    /// sent message to <see cref="ChatDeliveryState.Delivered"/> — the
    /// mechanical Delivered receipt arrived while still connected. A no-op
    /// (no event raised) if the message isn't found or is no longer
    /// Pending — e.g. a Delivered receipt racing a connection drop that
    /// already marked it Undelivered arrives too late to matter.</summary>
    public void MarkDelivered(Guid messageId) => TransitionPending(messageId, ChatDeliveryState.Delivered);

    /// <summary>Transitions a single still-Pending sent message straight to
    /// <see cref="ChatDeliveryState.Undelivered"/> — used when the send
    /// itself fails immediately (e.g. not currently connected).</summary>
    public void MarkUndelivered(Guid messageId) => TransitionPending(messageId, ChatDeliveryState.Undelivered);

    /// <summary>Transitions every currently-Pending sent message to
    /// <see cref="ChatDeliveryState.Undelivered"/> in one pass — called when
    /// the connection drops with one or more sends still in flight
    /// (ADR-0001: no auto-resend, the sender UI must show undelivered).</summary>
    public void MarkAllPendingUndelivered()
    {
        List<ChatMessage> updated = [];
        lock (_gate)
        {
            for (var i = 0; i < _messages.Count; i++)
            {
                var existing = _messages[i];
                if (existing.Direction != ChatMessageDirection.Sent || existing.DeliveryState != ChatDeliveryState.Pending) continue;

                var next = existing with { DeliveryState = ChatDeliveryState.Undelivered };
                _messages[i] = next;
                updated.Add(next);
            }
        }
        foreach (var message in updated) MessageUpdated?.Invoke(message);
    }

    void TransitionPending(Guid messageId, ChatDeliveryState newState)
    {
        ChatMessage? updated = null;
        lock (_gate)
        {
            var index = _messages.FindIndex(m => m.MessageId == messageId && m.Direction == ChatMessageDirection.Sent);
            if (index < 0) return;

            var existing = _messages[index];
            if (existing.DeliveryState != ChatDeliveryState.Pending) return;

            updated = existing with { DeliveryState = newState };
            _messages[index] = updated;
        }
        if (updated is not null) MessageUpdated?.Invoke(updated);
    }
}
