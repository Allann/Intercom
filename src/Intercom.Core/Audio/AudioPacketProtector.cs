using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Intercom.Audio;

/// <summary>AES-256-GCM protection for one sender/stream epoch. The 96-bit
/// nonce is a fresh four-byte epoch prefix plus the big-endian sequence.</summary>
public sealed class AudioPacketProtector : IDisposable
{
    public const byte ProtocolVersion = 1;
    public const int HeaderSize = 53;
    public const int TagSize = 16;
    public const int MaxOpusPayloadSize = 1275;

    readonly AesGcm _aes;
    readonly uint _noncePrefix;

    public AudioPacketProtector(ReadOnlySpan<byte> key, uint noncePrefix)
    {
        if (key.Length != 32) throw new ArgumentException("Audio keys must be 256 bits.", nameof(key));
        _aes = new AesGcm(key, TagSize);
        _noncePrefix = noncePrefix;
    }

    public byte[] Protect(AudioPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.OpusPayload.Length > MaxOpusPayloadSize) throw new ArgumentOutOfRangeException(nameof(packet));

        var result = new byte[HeaderSize + packet.OpusPayload.Length + TagSize];
        var header = result.AsSpan(0, HeaderSize);
        WriteHeader(header, packet, checked((ushort)packet.OpusPayload.Length));
        var ciphertext = result.AsSpan(HeaderSize, packet.OpusPayload.Length);
        var tag = result.AsSpan(HeaderSize + packet.OpusPayload.Length, TagSize);
        Span<byte> nonce = stackalloc byte[12];
        WriteNonce(nonce, packet.Sequence);
        _aes.Encrypt(nonce, packet.OpusPayload, ciphertext, tag, header);
        return result;
    }

    public bool TryUnprotect(ReadOnlySpan<byte> datagram, AudioReplayWindow replayWindow, out AudioPacket? packet)
    {
        packet = null;
        if (datagram.Length < HeaderSize + TagSize || datagram[0] != ProtocolVersion) return false;
        var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(datagram.Slice(51, 2));
        if (payloadLength > MaxOpusPayloadSize || datagram.Length != HeaderSize + payloadLength + TagSize) return false;

        var sequence = BinaryPrimitives.ReadUInt64BigEndian(datagram.Slice(33, 8));
        if (!replayWindow.CanAccept(sequence)) return false;

        var plaintext = new byte[payloadLength];
        Span<byte> nonce = stackalloc byte[12];
        WriteNonce(nonce, sequence);
        try
        {
            _aes.Decrypt(
                nonce,
                datagram.Slice(HeaderSize, payloadLength),
                datagram.Slice(HeaderSize + payloadLength, TagSize),
                plaintext,
                datagram.Slice(0, HeaderSize));
        }
        catch (AuthenticationTagMismatchException)
        {
            return false;
        }

        if (!replayWindow.Accept(sequence)) return false;
        packet = new AudioPacket
        {
            SessionId = new Guid(datagram.Slice(1, 16)),
            StreamId = new Guid(datagram.Slice(17, 16)),
            Sequence = sequence,
            SampleTimestamp = BinaryPrimitives.ReadUInt64BigEndian(datagram.Slice(41, 8)),
            Flags = (AudioPacketFlags)BinaryPrimitives.ReadUInt16BigEndian(datagram.Slice(49, 2)),
            OpusPayload = plaintext,
        };
        return true;
    }

    static void WriteHeader(Span<byte> header, AudioPacket packet, ushort payloadLength)
    {
        header[0] = ProtocolVersion;
        packet.SessionId.TryWriteBytes(header.Slice(1, 16));
        packet.StreamId.TryWriteBytes(header.Slice(17, 16));
        BinaryPrimitives.WriteUInt64BigEndian(header.Slice(33, 8), packet.Sequence);
        BinaryPrimitives.WriteUInt64BigEndian(header.Slice(41, 8), packet.SampleTimestamp);
        BinaryPrimitives.WriteUInt16BigEndian(header.Slice(49, 2), (ushort)packet.Flags);
        BinaryPrimitives.WriteUInt16BigEndian(header.Slice(51, 2), payloadLength);
    }

    void WriteNonce(Span<byte> nonce, ulong sequence)
    {
        BinaryPrimitives.WriteUInt32BigEndian(nonce.Slice(0, 4), _noncePrefix);
        BinaryPrimitives.WriteUInt64BigEndian(nonce.Slice(4, 8), sequence);
    }

    public void Dispose() => _aes.Dispose();
}
