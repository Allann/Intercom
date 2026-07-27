using System.Buffers.Binary;
using Intercom.ControlChannel;

namespace Intercom.Presence;

/// <summary>
/// Wire encode/decode for <see cref="PresenceLease"/>'s frame payload,
/// matching <c>HelloFrameCodec</c>/<c>PairingFrameCodec</c>'s fixed-size,
/// big-endian convention.
///
/// Payload layout (fixed 67 bytes, big-endian):
/// <code>
/// [0..16)  DeviceId        16-byte GUID
/// [16..32) ContactId       16-byte GUID
/// [32..48) IncarnationId   16-byte GUID
/// [48..56) Sequence        uint64
/// [56..57) Availability    byte (0=Available, 1=Idle, 2=Unavailable)
/// [57..58) Flags           byte (bit0 = Dnd; bit1 = HasIdleAgeBucket)
/// [58..59) IdleAgeBucket   byte (0/1/2; meaningless if Flags bit1 unset)
/// [59..63) LeaseSeconds    int32
/// [63..67) Capabilities    uint32
/// </code>
///
/// This ticket (#23) is send-only. <see cref="Decode"/> is provided anyway
/// (and round-trip tested) purely as a verified wire contract for #30 to
/// build its receiver on — the sender-side incarnation/sequence bookkeeping
/// this frame carries is #23's job (see <see cref="PresenceEngine"/>);
/// actually rejecting a stale sequence on receipt is #30's.
/// </summary>
public static class PresenceFrameCodec
{
    const int DeviceIdOffset = 0;
    const int ContactIdOffset = DeviceIdOffset + 16;
    const int IncarnationIdOffset = ContactIdOffset + 16;
    const int SequenceOffset = IncarnationIdOffset + 16;
    const int AvailabilityOffset = SequenceOffset + sizeof(ulong);
    const int FlagsOffset = AvailabilityOffset + sizeof(byte);
    const int IdleAgeBucketOffset = FlagsOffset + sizeof(byte);
    const int LeaseSecondsOffset = IdleAgeBucketOffset + sizeof(byte);
    const int CapabilitiesOffset = LeaseSecondsOffset + sizeof(int);
    const int PayloadSize = CapabilitiesOffset + sizeof(uint);

    const byte DndFlag = 0b0000_0001;
    const byte HasIdleAgeBucketFlag = 0b0000_0010;

    public static ControlFrame ToFrame(this PresenceLease lease, Guid messageId)
    {
        var payload = new byte[PayloadSize];
        var span = payload.AsSpan();

        lease.DeviceId.TryWriteBytes(span.Slice(DeviceIdOffset, 16));
        lease.ContactId.TryWriteBytes(span.Slice(ContactIdOffset, 16));
        lease.IncarnationId.TryWriteBytes(span.Slice(IncarnationIdOffset, 16));
        BinaryPrimitives.WriteUInt64BigEndian(span.Slice(SequenceOffset, sizeof(ulong)), lease.Sequence);
        span[AvailabilityOffset] = (byte)lease.Availability;

        var flags = lease.Dnd ? DndFlag : (byte)0;
        if (lease.IdleAgeBucket is not null) flags |= HasIdleAgeBucketFlag;
        span[FlagsOffset] = flags;
        span[IdleAgeBucketOffset] = (byte)(lease.IdleAgeBucket ?? default);

        BinaryPrimitives.WriteInt32BigEndian(span.Slice(LeaseSecondsOffset, sizeof(int)), lease.LeaseSeconds);
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(CapabilitiesOffset, sizeof(uint)), (uint)lease.Capabilities);

        return new ControlFrame
        {
            Type = ControlMessageType.Presence,
            MessageId = messageId,
            Payload = payload,
        };
    }

    /// <summary>Throws <see cref="MalformedFrameException"/> on anything but
    /// a well-formed Presence frame — untrusted input, per FrameCodec's
    /// bounds discipline.</summary>
    public static PresenceLease Decode(ControlFrame frame)
    {
        if (frame.Type != ControlMessageType.Presence)
        {
            throw new ArgumentException($"Frame is not a Presence frame (type {frame.Type}).", nameof(frame));
        }
        if (frame.Payload.Length != PayloadSize)
        {
            throw new MalformedFrameException($"Presence payload must be exactly {PayloadSize} bytes, got {frame.Payload.Length}.");
        }

        var span = frame.Payload.AsSpan();
        var deviceId = new Guid(span.Slice(DeviceIdOffset, 16));
        var contactId = new Guid(span.Slice(ContactIdOffset, 16));
        var incarnationId = new Guid(span.Slice(IncarnationIdOffset, 16));
        var sequence = BinaryPrimitives.ReadUInt64BigEndian(span.Slice(SequenceOffset, sizeof(ulong)));
        var availability = (AvailabilityState)span[AvailabilityOffset];
        var flags = span[FlagsOffset];
        var dnd = (flags & DndFlag) != 0;
        IdleAgeBucket? idleAgeBucket = (flags & HasIdleAgeBucketFlag) != 0 ? (IdleAgeBucket)span[IdleAgeBucketOffset] : null;
        var leaseSeconds = BinaryPrimitives.ReadInt32BigEndian(span.Slice(LeaseSecondsOffset, sizeof(int)));
        var capabilities = (Capability)BinaryPrimitives.ReadUInt32BigEndian(span.Slice(CapabilitiesOffset, sizeof(uint)));

        return new PresenceLease
        {
            DeviceId = deviceId,
            ContactId = contactId,
            IncarnationId = incarnationId,
            Sequence = sequence,
            Availability = availability,
            Dnd = dnd,
            IdleAgeBucket = idleAgeBucket,
            LeaseSeconds = leaseSeconds,
            Capabilities = capabilities,
        };
    }
}
