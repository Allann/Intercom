using System.Buffers.Binary;
using Intercom.ControlChannel;

namespace Intercom.Audio;

public sealed record AudioSessionOffer(Guid SessionId, Guid StreamId, ushort UdpPort, byte[] Key, uint NoncePrefix);
public sealed record AudioSessionAnswer(Guid SessionId, Guid StreamId, ushort UdpPort, byte[] Key, uint NoncePrefix);

public static class AudioSessionFrameCodec
{
    const int OfferPayloadSize = 16 + 16 + 2 + 32 + 4;

    public static ControlFrame ToFrame(this AudioSessionOffer offer, Guid messageId)
    {
        if (offer.Key.Length != 32) throw new ArgumentException("Audio session key must be 32 bytes.", nameof(offer));
        var payload = new byte[OfferPayloadSize];
        offer.SessionId.TryWriteBytes(payload.AsSpan(0, 16));
        offer.StreamId.TryWriteBytes(payload.AsSpan(16, 16));
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(32, 2), offer.UdpPort);
        offer.Key.CopyTo(payload, 34);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(66, 4), offer.NoncePrefix);
        return new ControlFrame { Type = ControlMessageType.AudioSessionOffer, MessageId = messageId, Payload = payload };
    }

    public static AudioSessionOffer DecodeOffer(ControlFrame frame)
    {
        if (frame.Type != ControlMessageType.AudioSessionOffer || frame.Payload.Length != OfferPayloadSize)
            throw new MalformedFrameException("Malformed audio session offer.");
        return new AudioSessionOffer(
            new Guid(frame.Payload.AsSpan(0, 16)),
            new Guid(frame.Payload.AsSpan(16, 16)),
            BinaryPrimitives.ReadUInt16BigEndian(frame.Payload.AsSpan(32, 2)),
            frame.Payload.AsSpan(34, 32).ToArray(),
            BinaryPrimitives.ReadUInt32BigEndian(frame.Payload.AsSpan(66, 4)));
    }

    public static ControlFrame ToFrame(this AudioSessionAnswer answer, Guid offerMessageId)
    {
        if (answer.Key.Length != 32) throw new ArgumentException("Audio session key must be 32 bytes.", nameof(answer));
        var payload = new byte[OfferPayloadSize];
        answer.SessionId.TryWriteBytes(payload.AsSpan(0, 16));
        answer.StreamId.TryWriteBytes(payload.AsSpan(16, 16));
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(32, 2), answer.UdpPort);
        answer.Key.CopyTo(payload, 34);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(66, 4), answer.NoncePrefix);
        return new ControlFrame
        {
            Type = ControlMessageType.AudioSessionAccepted,
            MessageId = Guid.NewGuid(),
            CorrelationId = offerMessageId,
            Payload = payload,
        };
    }

    public static AudioSessionAnswer DecodeAnswer(ControlFrame frame)
    {
        if (frame.Type != ControlMessageType.AudioSessionAccepted || frame.Payload.Length != OfferPayloadSize)
            throw new MalformedFrameException("Malformed audio session answer.");
        return new AudioSessionAnswer(
            new Guid(frame.Payload.AsSpan(0, 16)),
            new Guid(frame.Payload.AsSpan(16, 16)),
            BinaryPrimitives.ReadUInt16BigEndian(frame.Payload.AsSpan(32, 2)),
            frame.Payload.AsSpan(34, 32).ToArray(),
            BinaryPrimitives.ReadUInt32BigEndian(frame.Payload.AsSpan(66, 4)));
    }

    public static ControlFrame Stopped(Guid sessionId) => new()
    {
        Type = ControlMessageType.AudioSessionStopped,
        MessageId = Guid.NewGuid(),
        Payload = sessionId.ToByteArray(),
    };
}
