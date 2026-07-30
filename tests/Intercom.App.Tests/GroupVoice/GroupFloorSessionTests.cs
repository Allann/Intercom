using Intercom.GroupVoice;
using Xunit;

namespace Intercom.App.Tests.GroupVoice;

public sealed class GroupFloorSessionTests
{
    static readonly Guid Starter = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
    static readonly Guid Low = Guid.Parse("00000000-0000-0000-0000-000000000001");
    static readonly Guid High = Guid.Parse("00000000-0000-0000-0000-000000000002");

    [Fact]
    public void EveryReplicaChoosesLowestActivePeerWhenCoordinatorLeaves()
    {
        var sessionId = Guid.NewGuid();
        var replicas = Enumerable.Range(0, 3)
            .Select(_ => new GroupFloorSession(sessionId, Starter, [Starter, High, Low]))
            .ToList();
        var leave = new GroupFloorCommand(sessionId, GroupFloorCommandKind.Leave, Starter, Starter);

        foreach (var replica in replicas) replica.Apply(leave);

        Assert.All(replicas, replica => Assert.Equal(Low, replica.CoordinatorPeerId));
    }

    [Fact]
    public void InterruptTransfersFloorDirectlyAndRemovesInterrupterFromQueue()
    {
        var session = NewSession();
        session.Apply(Command(GroupFloorCommandKind.RaiseHand, Low, Low));
        session.Apply(Command(GroupFloorCommandKind.GrantFloor, Starter, High));

        session.Apply(Command(GroupFloorCommandKind.Interrupt, Low, Low));

        Assert.Equal(Low, session.SpeakerPeerId);
        Assert.DoesNotContain(Low, session.RaiseHandQueue);
    }

    [Fact]
    public void QueueIsReplicatedByApplyingBroadcastCommands()
    {
        var first = NewSession();
        var second = NewSession();
        var commands = new[]
        {
            Command(GroupFloorCommandKind.RaiseHand, High, High),
            Command(GroupFloorCommandKind.RaiseHand, Low, Low),
            Command(GroupFloorCommandKind.LowerHand, High, High),
        };

        foreach (var command in commands) { first.Apply(command); second.Apply(command); }

        Assert.Equal(first.RaiseHandQueue, second.RaiseHandQueue);
        Assert.Equal([Low], first.RaiseHandQueue);
    }

    [Fact]
    public void OnlyCoordinatorCanGrantOrEndSession()
    {
        var session = NewSession();
        Assert.Throws<InvalidOperationException>(() => session.Apply(Command(GroupFloorCommandKind.GrantFloor, Low, High)));
        Assert.Throws<InvalidOperationException>(() => session.Apply(Command(GroupFloorCommandKind.EndSession, Low, Guid.Empty)));
        Assert.False(session.Ended);
    }

    [Fact]
    public void SessionEndsAtZeroParticipants()
    {
        var session = NewSession();
        session.Apply(Command(GroupFloorCommandKind.Leave, High, High));
        session.Apply(Command(GroupFloorCommandKind.Leave, Low, Low));
        session.Apply(Command(GroupFloorCommandKind.Leave, Starter, Starter));
        Assert.True(session.Ended);
        Assert.Null(session.CoordinatorPeerId);
    }

    GroupFloorSession NewSession() => new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), Starter, [Starter, Low, High]);
    GroupFloorCommand Command(GroupFloorCommandKind kind, Guid actor, Guid subject) => new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), kind, actor, subject);
}
