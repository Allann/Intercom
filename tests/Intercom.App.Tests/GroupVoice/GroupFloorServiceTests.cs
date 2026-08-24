using Intercom.ControlChannel;
using Intercom.GroupVoice;
using Xunit;

namespace Intercom.App.Tests.GroupVoice;

public sealed class GroupFloorServiceTests
{
    [Fact]
    public async Task JoinPreparesAudioBeforeBroadcastButGrantDoesNotRenegotiate()
    {
        var local = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var remote = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var transport = new FakeTransport();
        var audio = new FakeAudioPreparer();
        var service = new GroupFloorService(local, new GroupFloorSession(Guid.NewGuid(), local, [local]), transport, audio);

        await service.JoinAsync(remote, CancellationToken.None);
        await service.GrantFloorAsync(remote, CancellationToken.None);

        Assert.Equal([remote], audio.Prepared);
        Assert.Equal(2, transport.Sent.Count);
        Assert.Equal(GroupFloorCommandKind.GrantFloor, GroupFloorFrameCodec.Decode(transport.Sent[1]).Kind);
    }

    [Fact]
    public void RejectsActorThatDoesNotMatchAuthenticatedSender()
    {
        var local = Guid.NewGuid();
        var remote = Guid.NewGuid();
        var transport = new FakeTransport();
        using var service = new GroupFloorService(local, new GroupFloorSession(Guid.NewGuid(), local, [local, remote]), transport, new FakeAudioPreparer());
        Exception? rejected = null;
        service.CommandRejected += ex => rejected = ex;
        var command = new GroupFloorCommand(service.Session.SessionId, GroupFloorCommandKind.RaiseHand, remote, remote);

        transport.Receive(Guid.NewGuid(), GroupFloorFrameCodec.Encode(command));

        var error = Assert.IsType<InvalidOperationException>(rejected);
        Assert.Equal("Group-floor actor does not match its authenticated sender.", error.Message);
        Assert.Empty(service.Session.RaiseHandQueue);
    }

    [Fact]
    public void ObservedCoordinatorDepartureRecomputesWithoutBroadcast()
    {
        var starter = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var local = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var lowest = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var transport = new FakeTransport();
        using var service = new GroupFloorService(local, new GroupFloorSession(Guid.NewGuid(), starter, [starter, local, lowest]), transport, new FakeAudioPreparer());

        service.ParticipantDeparted(starter);

        Assert.Equal(lowest, service.Session.CoordinatorPeerId);
        Assert.Empty(transport.Sent);
    }

    [Fact]
    public void ReceivedJoinAppliesStatePreparesAudioAndRaisesStateChanged()
    {
        var local = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var remote = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var transport = new FakeTransport();
        var audio = new FakeAudioPreparer();
        using var service = new GroupFloorService(local, new GroupFloorSession(Guid.NewGuid(), local, [local]), transport, audio);
        var changes = 0;
        service.StateChanged += () => changes++;

        transport.Receive(remote, GroupFloorFrameCodec.Encode(new(service.Session.SessionId, GroupFloorCommandKind.Join, remote, remote)));

        Assert.Contains(remote, service.Session.Participants);
        Assert.Equal([remote], audio.Prepared);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void ReceivedJoinDoesNotPrepareAudioWhenLocalPeerIsNotCoordinator()
    {
        var local = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var remote = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var transport = new FakeTransport();
        var audio = new FakeAudioPreparer();
        using var service = new GroupFloorService(local, new GroupFloorSession(Guid.NewGuid(), local, [local]), transport, audio);

        transport.Receive(remote, GroupFloorFrameCodec.Encode(new(service.Session.SessionId, GroupFloorCommandKind.Join, remote, remote)));

        Assert.Empty(audio.Prepared);
    }

    [Fact]
    public void ReceivedJoinDoesNotPrepareAudioForTheLocalPeer()
    {
        var local = Guid.NewGuid();
        var transport = new FakeTransport();
        var audio = new FakeAudioPreparer();
        using var service = new GroupFloorService(local, new GroupFloorSession(Guid.NewGuid(), local, [local]), transport, audio);

        transport.Receive(local, GroupFloorFrameCodec.Encode(new(service.Session.SessionId, GroupFloorCommandKind.Join, local, local)));

        Assert.Empty(audio.Prepared);
    }

    [Fact]
    public void ReceivedNonJoinDoesNotPrepareAudioAndDisposeStopsReception()
    {
        var local = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var remote = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var transport = new FakeTransport();
        var audio = new FakeAudioPreparer();
        var session = new GroupFloorSession(Guid.NewGuid(), local, [local, remote]);
        var service = new GroupFloorService(local, session, transport, audio);
        var frame = GroupFloorFrameCodec.Encode(new GroupFloorCommand(session.SessionId, GroupFloorCommandKind.RaiseHand, remote, remote));

        transport.Receive(remote, frame);
        service.Dispose();
        transport.Receive(remote, GroupFloorFrameCodec.Encode(new(session.SessionId, GroupFloorCommandKind.LowerHand, remote, remote)));

        Assert.Empty(audio.Prepared);
        Assert.Equal([remote], session.RaiseHandQueue);
    }

    sealed class FakeTransport : IGroupFloorTransport
    {
        public event Action<Guid, ControlFrame>? FrameReceived;
        public List<ControlFrame> Sent { get; } = [];
        public Task SendAsync(IEnumerable<Guid> participantPeerIds, ControlFrame frame, CancellationToken cancellationToken) { Sent.Add(frame); return Task.CompletedTask; }
        public void Receive(Guid sender, ControlFrame frame) => FrameReceived?.Invoke(sender, frame);
    }

    sealed class FakeAudioPreparer : IGroupAudioPreparer
    {
        public List<Guid> Prepared { get; } = [];
        public Task PrepareAsync(Guid peerId, CancellationToken cancellationToken) { Prepared.Add(peerId); return Task.CompletedTask; }
    }
}
