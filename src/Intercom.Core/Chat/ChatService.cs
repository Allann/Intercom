using Intercom.ControlChannel;

namespace Intercom.Chat;

/// <summary>
/// Wires one <see cref="IChatTransport"/> to one <see cref="ChatConversation"/>
/// — sending, decoding inbound frames, and reacting to delivery confirmation
/// / connection drop — for a single two-party conversation (issue #24 is not
/// group chat). This is the layer <see cref="ChatFrameCodec"/>/<see cref="ChatConversation"/>
/// don't own: actually crossing the transport seam.
///
/// A malformed inbound Chat frame (see <see cref="ChatFrameCodec.Decode"/>)
/// is dropped rather than surfaced as a message — by the time a frame
/// reaches here it already passed <c>FrameDispatcher</c>'s ordering/trust
/// checks and <see cref="FrameCodec"/>'s own bounds checks on the connection
/// itself, so a codec-level decode failure at this layer indicates a
/// same-version peer bug, not an attack to react to by tearing down the
/// connection.
/// </summary>
public sealed class ChatService
{
    readonly IChatTransport _transport;

    public ChatConversation Conversation { get; }

    /// <summary>Raised for every inbound chat message actually added to
    /// <see cref="Conversation"/> — the caller (chat drawer / TTS / DND
    /// chime gating) decides what to do about it; this class makes no
    /// chime/toast/TTS decisions of its own.</summary>
    public event Action<ChatMessage>? MessageReceived;

    public ChatService(IChatTransport transport, ChatConversation? conversation = null)
    {
        _transport = transport;
        Conversation = conversation ?? new ChatConversation();

        _transport.FrameReceived += OnFrameReceived;
        _transport.DeliveryConfirmed += Conversation.MarkDelivered;
        _transport.ConnectionDropped += Conversation.MarkAllPendingUndelivered;
    }

    void OnFrameReceived(ControlFrame frame)
    {
        string text;
        try
        {
            text = ChatFrameCodec.Decode(frame);
        }
        catch (MalformedFrameException)
        {
            return;
        }

        var message = Conversation.AddInbound(frame.MessageId, text);
        MessageReceived?.Invoke(message);
    }

    /// <summary>Sends <paramref name="text"/> as a new outbound chat message.
    /// Immediately recorded as <see cref="ChatDeliveryState.Pending"/>; if
    /// the send itself throws (e.g. not currently connected), the message is
    /// immediately marked <see cref="ChatDeliveryState.Undelivered"/> and the
    /// exception is rethrown for the caller to react to (e.g. surface an
    /// error) — ADR-0001: no auto-resend, the user must explicitly send the
    /// text again.</summary>
    public async Task<ChatMessage> SendAsync(string text, CancellationToken cancellationToken)
    {
        var messageId = Guid.NewGuid();
        var frame = ChatFrameCodec.ToFrame(text, messageId);
        var message = Conversation.AddOutbound(messageId, text);

        try
        {
            await _transport.SendAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Conversation.MarkUndelivered(messageId);
            throw;
        }

        return message;
    }
}
