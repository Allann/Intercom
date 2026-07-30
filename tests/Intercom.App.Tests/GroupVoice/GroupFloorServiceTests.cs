using Intercom.ControlChannel;
using Intercom.GroupVoice;
using Xunit;

namespace Intercom.App.Tests.GroupVoice;

public sealed class GroupFloorServiceTests
{
    [Fact]
    public async Task JoinPreparesAudioBeforeBroadcastButGrantDoesNotRenegotiate()
    {
        var local = Guid.NewGuid();
        var remote = Guid.NewGuid();
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

        Assert.NotNull(rejected);
        Assert.Empty(service.Session.RaiseHandQueue);
    }

    sealed class FakeTransport : IGroupFloorTransport
    {
        public event Action<Guid, ControlFrame>? FrameReceived;
        public List<ControlFrame> Sent { get; } = [];
        public Task BroadcastAsync(ControlFrame frame, CancellationToken cancellationToken) { Sent.Add(frame); return Task.CompletedTask; }
        public void Receive(Guid sender, ControlFrame frame) => FrameReceived?.Invoke(sender, frame);
    }

    sealed class FakeAudioPreparer : IGroupAudioPreparer
    {
        public List<Guid> Prepared { get; } = [];
        public Task PrepareAsync(Guid peerId, CancellationToken cancellationToken) { Prepared.Add(peerId); return Task.CompletedTask; }
    }
}
