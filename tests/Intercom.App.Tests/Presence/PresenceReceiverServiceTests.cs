using Intercom.ControlChannel;
using Intercom.Presence;
using Xunit;

namespace Intercom.App.Tests.Presence;

/// <summary>
/// The transport-crossing glue layer for #30's presence receive side —
/// decode, drop malformed, hand to <see cref="PresenceReceiver"/>. Driven
/// directly through <see cref="PresenceReceiverService.HandleInboundFrame"/>
/// (public specifically for this reason — see its doc comment) rather than a
/// full live <see cref="PeerControlChannel"/> handshake, mirroring
/// <c>ChatServiceTests</c>' use of <c>FakeChatTransport</c> to isolate the
/// glue from real transport plumbing.
/// </summary>
public class PresenceReceiverServiceTests
{
    static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    static PresenceLease Lease(Guid deviceId, Guid incarnation, ulong sequence) => new()
    {
        DeviceId = deviceId,
        ContactId = Guid.NewGuid(),
        IncarnationId = incarnation,
        Sequence = sequence,
        Availability = AvailabilityState.Available,
        Dnd = false,
        IdleAgeBucket = IdleAgeBucket.UnderTwoMinutes,
        LeaseSeconds = 30,
        Capabilities = Capability.Text,
    };

    [Fact]
    public void HandleInboundFrame_WellFormedPresenceFrame_IsIngested()
    {
        var service = new PresenceReceiverService(clock: () => Epoch);
        var deviceId = Guid.NewGuid();
        var frame = Lease(deviceId, Guid.NewGuid(), 1).ToFrame(Guid.NewGuid());

        service.HandleInboundFrame(frame);

        Assert.NotNull(service.Receiver.Get(deviceId));
    }

    [Fact]
    public void HandleInboundFrame_UsesInjectedClock_ForReceivedAt()
    {
        var service = new PresenceReceiverService(clock: () => Epoch);
        var deviceId = Guid.NewGuid();
        var frame = Lease(deviceId, Guid.NewGuid(), 1).ToFrame(Guid.NewGuid());

        service.HandleInboundFrame(frame);

        Assert.Equal(Epoch, service.Receiver.Get(deviceId)!.LastUpdatedAt);
    }

    [Fact]
    public void HandleInboundFrame_NonPresenceFrame_IsIgnored()
    {
        var service = new PresenceReceiverService(clock: () => Epoch);
        var frame = new ControlFrame { Type = ControlMessageType.Chat, MessageId = Guid.NewGuid(), Payload = [1, 2, 3] };

        service.HandleInboundFrame(frame); // must not throw

        Assert.Empty(service.Receiver.AllTracked());
    }

    [Fact]
    public void HandleInboundFrame_MalformedPresencePayload_IsDroppedNotThrown()
    {
        var service = new PresenceReceiverService(clock: () => Epoch);
        var frame = new ControlFrame { Type = ControlMessageType.Presence, MessageId = Guid.NewGuid(), Payload = [1, 2, 3] }; // too short

        service.HandleInboundFrame(frame); // must not throw

        Assert.Empty(service.Receiver.AllTracked());
    }

    [Fact]
    public void HandleInboundFrame_StaleSequence_IsNotAccepted()
    {
        var service = new PresenceReceiverService(clock: () => Epoch);
        var deviceId = Guid.NewGuid();
        var incarnation = Guid.NewGuid();
        service.HandleInboundFrame(Lease(deviceId, incarnation, 5).ToFrame(Guid.NewGuid()));

        service.HandleInboundFrame(Lease(deviceId, incarnation, 5).ToFrame(Guid.NewGuid())); // duplicate

        Assert.Equal(5UL, service.Receiver.Get(deviceId)!.Sequence);
    }
}
