using Intercom.ControlChannel;
using System.Security.Cryptography;
using SixLabors.ImageSharp;

namespace Intercom.Chat;

/// <summary>The public direct-chat behavior seam for rich text, transient
/// composing state, and image transfer. Conversation content remains owned
/// in memory only for the lifetime of this service.</summary>
public sealed class RichChatService
{
    public const int MaxMessageCharacters = 4000;
    public const int MaxCaptionCharacters = 500;
    public static readonly TimeSpan TypingLifetime = TimeSpan.FromSeconds(5);

    readonly IChatTransport _transport;
    readonly Func<DateTimeOffset> _clock;
    DateTimeOffset? _peerTypingExpiresAt;
    bool _localTyping;
    readonly object _imageGate = new();
    readonly Dictionary<Guid, IncomingImage> _incomingImages = [];
    Guid? _outgoingImageId;
    readonly HashSet<Guid> _cancelledOutgoingImages = [];

    public ChatConversation Conversation { get; }
    public bool SupportsMarkdown => _transport.RemoteCapabilities.HasFlag(Capability.ChatMarkdown);
    public bool SupportsImages => _transport.RemoteCapabilities.HasFlag(Capability.ChatImages);
    public bool SupportsTyping => _transport.RemoteCapabilities.HasFlag(Capability.ChatTyping);
    public bool IsPeerTyping => _peerTypingExpiresAt is { } expires && expires > _clock();
    public event Action<bool>? PeerTypingChanged;
    public event Action<ChatMessage>? MessageReceived;
    public event Action<Guid>? IncomingImageStarted;

    public RichChatService(IChatTransport transport, ChatConversation? conversation = null, Func<DateTimeOffset>? clock = null)
    {
        _transport = transport;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        Conversation = conversation ?? new ChatConversation(_clock);
        transport.FrameReceived += OnFrameReceived;
        transport.DeliveryConfirmed += OnDeliveryConfirmed;
        transport.ConnectionDropped += OnConnectionDropped;
    }

    public async Task SetTypingAsync(bool typing, CancellationToken cancellationToken)
    {
        if (!SupportsTyping) return;
        if (!typing && !_localTyping) return;
        _localTyping = typing;
        await _transport.SendAsync(new ControlFrame
        {
            Type = ControlMessageType.ChatTyping,
            MessageId = Guid.NewGuid(),
            Payload = [typing ? (byte)1 : (byte)0],
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ChatMessage> SendTextAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("A chat message cannot be blank.", nameof(text));
        if (text.Length > MaxMessageCharacters) throw new ArgumentException($"Chat messages are limited to {MaxMessageCharacters} characters.", nameof(text));
        if (_localTyping) await SetTypingAsync(false, cancellationToken).ConfigureAwait(false);

        var id = Guid.NewGuid();
        var message = Conversation.AddOutbound(id, text);
        try
        {
            await _transport.SendAsync(ChatFrameCodec.ToFrame(text, id), cancellationToken).ConfigureAwait(false);
            return message;
        }
        catch
        {
            Conversation.MarkUndelivered(id);
            throw;
        }
    }

    public async Task<ChatMessage> SendImageAsync(OptimizedChatImage image, string caption, CancellationToken cancellationToken)
    {
        if (!SupportsImages) throw new NotSupportedException("The peer must update Intercom before it can receive images.");
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(caption);
        if (caption.Length > MaxCaptionCharacters) throw new ArgumentException($"Image captions are limited to {MaxCaptionCharacters} characters.", nameof(caption));
        if (image.Bytes.Length is <= 0 or > ChatImageOptimizer.MaxTransferBytes) throw new ArgumentException("Image bytes are outside the chat transfer limit.", nameof(image));
        var transferId = Guid.NewGuid();
        lock (_imageGate)
        {
            if (_outgoingImageId is not null) throw new InvalidOperationException("An image transfer is already active in this conversation.");
            _outgoingImageId = transferId;
        }
        if (_localTyping) await SetTypingAsync(false, cancellationToken).ConfigureAwait(false);
        var message = Conversation.AddOutboundImage(transferId, image, caption);
        try
        {
            await _transport.SendAsync(ChatImageFrameCodec.Start(image, caption, transferId), cancellationToken).ConfigureAwait(false);
            for (var offset = 0; offset < image.Bytes.Length; offset += ChatImageFrameCodec.MaxChunkBytes)
            {
                lock (_imageGate) { if (_cancelledOutgoingImages.Contains(transferId)) return message; }
                if (Conversation.Messages.First(item => item.MessageId == transferId).TransferState == ChatTransferState.RecipientCancelled) return message;
                var length = Math.Min(ChatImageFrameCodec.MaxChunkBytes, image.Bytes.Length - offset);
                await _transport.SendAsync(ChatImageFrameCodec.Chunk(transferId, offset, image.Bytes.AsSpan(offset, length)), cancellationToken).ConfigureAwait(false);
                lock (_imageGate) { if (_cancelledOutgoingImages.Contains(transferId)) return message; }
                Conversation.UpdateImage(transferId, image, ChatTransferState.Sending, (offset + length) / (double)image.Bytes.Length);
            }
            await _transport.SendAsync(ChatImageFrameCodec.Signal(ControlMessageType.ChatImageComplete, transferId), cancellationToken).ConfigureAwait(false);
            return message;
        }
        catch
        {
            Conversation.UpdateImage(transferId, image, ChatTransferState.Failed, 0, ChatDeliveryState.Undelivered);
            throw;
        }
        finally
        {
            lock (_imageGate) { if (_outgoingImageId == transferId) _outgoingImageId = null; _cancelledOutgoingImages.Remove(transferId); }
        }
    }

    public async Task CancelOutgoingImageAsync(Guid transferId, CancellationToken cancellationToken)
    {
        lock (_imageGate)
        {
            if (_outgoingImageId != transferId) return;
            _cancelledOutgoingImages.Add(transferId);
        }
        Conversation.UpdateImage(transferId, null, ChatTransferState.Cancelled, 0, ChatDeliveryState.Undelivered);
        await _transport.SendAsync(ChatImageFrameCodec.Signal(ControlMessageType.ChatImageCancelled, transferId), cancellationToken).ConfigureAwait(false);
    }

    public async Task CancelImageAsync(Guid transferId, CancellationToken cancellationToken)
    {
        lock (_imageGate) { _incomingImages.Remove(transferId); }
        Conversation.UpdateImage(transferId, null, ChatTransferState.Cancelled, 0, ChatDeliveryState.Undelivered);
        await _transport.SendAsync(ChatImageFrameCodec.Signal(ControlMessageType.ChatImageCancelled, transferId), cancellationToken).ConfigureAwait(false);
    }

    public void Tick()
    {
        if (_peerTypingExpiresAt is not { } expires || expires > _clock()) return;
        _peerTypingExpiresAt = null;
        PeerTypingChanged?.Invoke(false);
    }

    void OnFrameReceived(ControlFrame frame)
    {
        if (frame.Type == ControlMessageType.ChatTyping)
        {
            if (frame.Payload.Length != 1) return;
            var wasTyping = IsPeerTyping;
            _peerTypingExpiresAt = frame.Payload[0] == 1 ? _clock() + TypingLifetime : null;
            var isTyping = IsPeerTyping;
            if (wasTyping != isTyping) PeerTypingChanged?.Invoke(isTyping);
            return;
        }
        if (frame.Type == ControlMessageType.ChatImageStart) { ReceiveImageStart(frame); return; }
        if (frame.Type == ControlMessageType.ChatImageChunk) { ReceiveImageChunk(frame); return; }
        if (frame.Type == ControlMessageType.ChatImageComplete) { _ = CompleteIncomingImageAsync(frame); return; }
        if (frame.Type == ControlMessageType.ChatImageReceived) { ReceiveImageAccepted(frame); return; }
        if (frame.Type == ControlMessageType.ChatImageCancelled) { ReceiveImageCancelled(frame); return; }
        if (frame.Type == ControlMessageType.ChatImageFailed) { ReceiveImageFailed(frame); return; }
        if (frame.Type != ControlMessageType.Chat) return;
        try
        {
            var message = Conversation.AddInbound(frame.MessageId, ChatFrameCodec.Decode(frame));
            MessageReceived?.Invoke(message);
        }
        catch (MalformedFrameException) { }
    }

    void OnDeliveryConfirmed(Guid messageId)
    {
        var message = Conversation.Messages.FirstOrDefault(item => item.MessageId == messageId);
        if (message is { ContentKind: ChatContentKind.Text }) Conversation.MarkDelivered(messageId);
    }

    void ReceiveImageStart(ControlFrame frame)
    {
        ChatImageStart start;
        try { start = ChatImageFrameCodec.DecodeStart(frame); }
        catch (MalformedFrameException) { return; }
        lock (_imageGate)
        {
            if (_incomingImages.Count > 0)
            {
                _ = _transport.SendAsync(ChatImageFrameCodec.Signal(ControlMessageType.ChatImageFailed, frame.MessageId), CancellationToken.None);
                return;
            }
            _incomingImages[frame.MessageId] = new IncomingImage(start, new MemoryStream(start.TotalBytes));
        }
        Conversation.AddInboundImage(frame.MessageId, start.Caption);
        IncomingImageStarted?.Invoke(frame.MessageId);
    }

    void ReceiveImageChunk(ControlFrame frame)
    {
        if (frame.CorrelationId is not Guid transferId) return;
        (int Offset, ReadOnlyMemory<byte> Bytes) chunk;
        try { chunk = ChatImageFrameCodec.DecodeChunk(frame); }
        catch (MalformedFrameException) { return; }
        IncomingImage? incoming;
        lock (_imageGate)
        {
            if (!_incomingImages.TryGetValue(transferId, out incoming) || chunk.Offset != incoming.Buffer.Length
                || incoming.Buffer.Length + chunk.Bytes.Length > incoming.Start.TotalBytes) return;
            incoming.Buffer.Write(chunk.Bytes.Span);
        }
        Conversation.UpdateImage(transferId, null, ChatTransferState.Receiving, incoming.Buffer.Length / (double)incoming.Start.TotalBytes);
    }

    async Task CompleteIncomingImageAsync(ControlFrame frame)
    {
        if (frame.CorrelationId is not Guid transferId) return;
        IncomingImage? incoming;
        lock (_imageGate) { _incomingImages.Remove(transferId, out incoming); }
        if (incoming is null) return;
        var bytes = incoming.Buffer.ToArray();
        incoming.Buffer.Dispose();
        if (bytes.Length != incoming.Start.TotalBytes || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), incoming.Start.Sha256)
            || !MatchesDeclaredImage(bytes, incoming.Start))
        {
            Conversation.UpdateImage(transferId, null, ChatTransferState.Failed, 0, ChatDeliveryState.Undelivered);
            await _transport.SendAsync(ChatImageFrameCodec.Signal(ControlMessageType.ChatImageFailed, transferId), CancellationToken.None).ConfigureAwait(false);
            return;
        }
        var image = new OptimizedChatImage(bytes, incoming.Start.Width, incoming.Start.Height, incoming.Start.Format);
        Conversation.UpdateImage(transferId, image, ChatTransferState.Delivered, 1, ChatDeliveryState.Delivered);
        var received = Conversation.Messages.First(item => item.MessageId == transferId);
        MessageReceived?.Invoke(received);
        await _transport.SendAsync(ChatImageFrameCodec.Signal(ControlMessageType.ChatImageReceived, transferId), CancellationToken.None).ConfigureAwait(false);
    }

    static bool MatchesDeclaredImage(byte[] bytes, ChatImageStart start)
    {
        try
        {
            var info = Image.Identify(bytes);
            var format = Image.DetectFormat(bytes).Name;
            return info.Width == start.Width && info.Height == start.Height
                && (start.Format == ChatImageFormat.Jpeg ? format.Equals("JPEG", StringComparison.OrdinalIgnoreCase)
                    : format.Equals("PNG", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException or NotSupportedException)
        {
            return false;
        }
    }

    void ReceiveImageAccepted(ControlFrame frame)
    {
        if (frame.CorrelationId is Guid id)
            Conversation.UpdateImage(id, null, ChatTransferState.Delivered, 1, ChatDeliveryState.Delivered);
    }

    void ReceiveImageCancelled(ControlFrame frame)
    {
        if (frame.CorrelationId is Guid id)
        {
            IncomingImage? incoming;
            lock (_imageGate) { _incomingImages.Remove(id, out incoming); }
            incoming?.Buffer.Dispose();
            var message = Conversation.Messages.FirstOrDefault(item => item.MessageId == id);
            var state = message?.Direction == ChatMessageDirection.Received ? ChatTransferState.Cancelled : ChatTransferState.RecipientCancelled;
            Conversation.UpdateImage(id, null, state, 0, ChatDeliveryState.Undelivered);
        }
    }

    void ReceiveImageFailed(ControlFrame frame)
    {
        if (frame.CorrelationId is Guid id)
            Conversation.UpdateImage(id, null, ChatTransferState.Failed, 0, ChatDeliveryState.Undelivered);
    }

    void OnConnectionDropped()
    {
        Conversation.MarkAllPendingUndelivered();
        foreach (var message in Conversation.Messages.Where(item => item.ContentKind == ChatContentKind.Image && item.DeliveryState == ChatDeliveryState.Undelivered))
            Conversation.UpdateImage(message.MessageId, null, ChatTransferState.Failed, message.TransferProgress, ChatDeliveryState.Undelivered);
        lock (_imageGate)
        {
            foreach (var incoming in _incomingImages.Values) incoming.Buffer.Dispose();
            _incomingImages.Clear();
            _outgoingImageId = null;
            _cancelledOutgoingImages.Clear();
        }
        if (_peerTypingExpiresAt is null) return;
        _peerTypingExpiresAt = null;
        PeerTypingChanged?.Invoke(false);
    }

    sealed record IncomingImage(ChatImageStart Start, MemoryStream Buffer);
}
