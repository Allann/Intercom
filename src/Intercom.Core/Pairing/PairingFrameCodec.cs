using System.Buffers.Binary;
using Intercom.ControlChannel;

namespace Intercom.Pairing;

/// <summary>One side's nonce-exchange message
/// (docs/research/pairing-security.md step 1): a fresh nonce, this device's
/// claimed peer ID, the protocol version, and the pairing intent. This is
/// wire/display data, not itself the transcript — <see cref="PairingTranscript"/>
/// combines both sides' worth of this into the canonical byte layout.</summary>
public sealed record PairingNonceMessage
{
    public required PairingNonce Nonce { get; init; }
    public required Guid PeerId { get; init; }
    public required int ProtocolVersion { get; init; }
    public required PairingIntent Intent { get; init; }
}

/// <summary>
/// Wire encode/decode for the pairing ceremony's <see cref="ControlFrame"/>
/// payloads, matching <c>HelloFrameCodec</c>'s fixed-size, big-endian
/// convention. <see cref="ControlMessageType.PairingConfirm"/>,
/// <see cref="ControlMessageType.PairingReject"/>, and
/// <see cref="ControlMessageType.Forgotten"/> all carry empty payloads — the
/// frame's <see cref="ControlFrame.Type"/> alone is the entire signal, so
/// there is nothing else to encode for them.
///
/// <c>PairingNonce</c> payload layout (fixed 37 bytes, big-endian):
/// <code>
/// [0..16)  Nonce            16-byte value
/// [16..32) PeerId           16-byte GUID
/// [32..36) ProtocolVersion  int32
/// [36..37) Intent           byte
/// </code>
/// </summary>
public static class PairingFrameCodec
{
    const int NonceOffset = 0;
    const int PeerIdOffset = NonceOffset + PairingNonce.Length;
    const int ProtocolVersionOffset = PeerIdOffset + 16;
    const int IntentOffset = ProtocolVersionOffset + sizeof(int);
    const int PairingNoncePayloadSize = IntentOffset + sizeof(byte);

    public static ControlFrame ToFrame(this PairingNonceMessage message, Guid messageId)
    {
        var payload = new byte[PairingNoncePayloadSize];
        var span = payload.AsSpan();

        message.Nonce.Bytes.CopyTo(span.Slice(NonceOffset, PairingNonce.Length));
        message.PeerId.TryWriteBytes(span.Slice(PeerIdOffset, 16));
        BinaryPrimitives.WriteInt32BigEndian(span.Slice(ProtocolVersionOffset, sizeof(int)), message.ProtocolVersion);
        span[IntentOffset] = (byte)message.Intent;

        return new ControlFrame
        {
            Type = ControlMessageType.PairingNonce,
            MessageId = messageId,
            Payload = payload,
        };
    }

    /// <summary>Throws <see cref="MalformedFrameException"/> on anything but
    /// a well-formed PairingNonce frame — untrusted input, per FrameCodec's
    /// bounds discipline (this decodes bytes received from a not-yet-approved
    /// peer over a PairingOnly connection).</summary>
    public static PairingNonceMessage DecodeNonce(ControlFrame frame)
    {
        if (frame.Type != ControlMessageType.PairingNonce)
        {
            throw new ArgumentException($"Frame is not a PairingNonce frame (type {frame.Type}).", nameof(frame));
        }
        if (frame.Payload.Length != PairingNoncePayloadSize)
        {
            throw new MalformedFrameException(
                $"PairingNonce payload must be exactly {PairingNoncePayloadSize} bytes, got {frame.Payload.Length}.");
        }

        var span = frame.Payload.AsSpan();
        var nonce = new PairingNonce(span.Slice(NonceOffset, PairingNonce.Length).ToArray());
        var peerId = new Guid(span.Slice(PeerIdOffset, 16));
        var protocolVersion = BinaryPrimitives.ReadInt32BigEndian(span.Slice(ProtocolVersionOffset, sizeof(int)));
        var intent = (PairingIntent)span[IntentOffset];

        return new PairingNonceMessage
        {
            Nonce = nonce,
            PeerId = peerId,
            ProtocolVersion = protocolVersion,
            Intent = intent,
        };
    }

    public static ControlFrame ConfirmFrame(Guid messageId) => EmptyFrame(ControlMessageType.PairingConfirm, messageId);

    public static ControlFrame RejectFrame(Guid messageId) => EmptyFrame(ControlMessageType.PairingReject, messageId);

    public static ControlFrame ForgottenFrame(Guid messageId) => EmptyFrame(ControlMessageType.Forgotten, messageId);

    static ControlFrame EmptyFrame(ControlMessageType type, Guid messageId) => new()
    {
        Type = type,
        MessageId = messageId,
        Payload = [],
    };
}
