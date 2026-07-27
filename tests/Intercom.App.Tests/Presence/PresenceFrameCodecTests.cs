using Intercom.ControlChannel;
using Intercom.Presence;
using Xunit;

namespace Intercom.App.Tests.Presence;

public class PresenceFrameCodecTests
{
    static PresenceLease SampleLease(IdleAgeBucket? bucket = IdleAgeBucket.TwoToTenMinutes) => new()
    {
        DeviceId = Guid.NewGuid(),
        ContactId = Guid.NewGuid(),
        IncarnationId = Guid.NewGuid(),
        Sequence = 42,
        Availability = AvailabilityState.Idle,
        Dnd = true,
        IdleAgeBucket = bucket,
        LeaseSeconds = 30,
        Capabilities = Capability.Text | Capability.SendAudio,
    };

    [Fact]
    public void RoundTrip_PreservesEveryField()
    {
        var lease = SampleLease();
        var frame = lease.ToFrame(Guid.NewGuid());

        var decoded = PresenceFrameCodec.Decode(frame);

        Assert.Equal(lease, decoded);
    }

    [Fact]
    public void ToFrame_SetsPresenceMessageType()
    {
        var frame = SampleLease().ToFrame(Guid.NewGuid());
        Assert.Equal(ControlMessageType.Presence, frame.Type);
    }

    [Fact]
    public void RoundTrip_UnavailableWithNullIdleAgeBucket_OmitsBucket()
    {
        var lease = SampleLease(bucket: null) with { Availability = AvailabilityState.Unavailable };
        var frame = lease.ToFrame(Guid.NewGuid());

        var decoded = PresenceFrameCodec.Decode(frame);

        Assert.Null(decoded.IdleAgeBucket);
        Assert.Equal(AvailabilityState.Unavailable, decoded.Availability);
    }

    [Fact]
    public void RoundTrip_DndFalse_IsPreserved()
    {
        var lease = SampleLease() with { Dnd = false };
        var decoded = PresenceFrameCodec.Decode(lease.ToFrame(Guid.NewGuid()));
        Assert.False(decoded.Dnd);
    }

    [Fact]
    public void Decode_WrongFrameType_Throws()
    {
        var frame = new ControlFrame { Type = ControlMessageType.Hello, MessageId = Guid.NewGuid(), Payload = [] };
        Assert.Throws<ArgumentException>(() => PresenceFrameCodec.Decode(frame));
    }

    [Fact]
    public void Decode_WrongPayloadLength_ThrowsMalformedFrameException()
    {
        var frame = new ControlFrame { Type = ControlMessageType.Presence, MessageId = Guid.NewGuid(), Payload = new byte[3] };
        Assert.Throws<MalformedFrameException>(() => PresenceFrameCodec.Decode(frame));
    }

    [Fact]
    public void RoundTrip_LargeSequence_NearUInt64Max_IsPreserved()
    {
        // Never let the sender's monotonic sequence bookkeeping silently
        // truncate at a wire boundary.
        var lease = SampleLease() with { Sequence = ulong.MaxValue - 1 };
        var decoded = PresenceFrameCodec.Decode(lease.ToFrame(Guid.NewGuid()));
        Assert.Equal(ulong.MaxValue - 1, decoded.Sequence);
    }
}
