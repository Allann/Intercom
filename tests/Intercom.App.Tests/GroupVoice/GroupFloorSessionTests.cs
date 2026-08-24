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

    [Fact]
    public void JoinAndLowerHandReturnWithoutApplyingFloorCommands()
    {
        var session = NewSession();
        var joined = Guid.NewGuid();

        session.Apply(Command(GroupFloorCommandKind.Join, joined, joined));
        session.Apply(Command(GroupFloorCommandKind.RaiseHand, joined, joined));
        session.Apply(Command(GroupFloorCommandKind.LowerHand, joined, joined));

        Assert.Contains(joined, session.Participants);
        Assert.DoesNotContain(joined, session.RaiseHandQueue);
        Assert.False(session.Ended);
    }

    [Fact]
    public void DuplicateRaiseHandDoesNotDuplicateQueueAndSpeakerCannotQueue()
    {
        var session = NewSession();
        session.Apply(Command(GroupFloorCommandKind.RaiseHand, Low, Low));
        session.Apply(Command(GroupFloorCommandKind.RaiseHand, Low, Low));
        session.Apply(Command(GroupFloorCommandKind.GrantFloor, Starter, High));
        session.Apply(Command(GroupFloorCommandKind.RaiseHand, High, High));

        Assert.Equal([Low], session.RaiseHandQueue);
        Assert.Equal(High, session.SpeakerPeerId);
    }

    [Fact]
    public void GrantRemovesQueuedSpeakerAndEndClearsAllState()
    {
        var session = NewSession();
        session.Apply(Command(GroupFloorCommandKind.RaiseHand, Low, Low));
        session.Apply(Command(GroupFloorCommandKind.GrantFloor, Starter, Low));

        Assert.Equal(Low, session.SpeakerPeerId);
        Assert.Empty(session.RaiseHandQueue);

        session.Apply(Command(GroupFloorCommandKind.EndSession, Starter, Guid.Empty));

        Assert.True(session.Ended);
        Assert.Null(session.SpeakerPeerId);
        Assert.Null(session.CoordinatorPeerId);
        Assert.Empty(session.Participants);
    }

    [Fact]
    public void UnknownCommandIsRejected()
    {
        var session = NewSession();

        Assert.Throws<ArgumentOutOfRangeException>(() => session.Apply(Command((GroupFloorCommandKind)255, Starter, Low)));
    }

    [Fact]
    public void RejectsAnotherSessionAndCommandsThatRequireValidActivePeers()
    {
        var session = NewSession();
        var outsider = Guid.NewGuid();

        Assert.Throws<InvalidOperationException>(() => session.Apply(new(Guid.NewGuid(), GroupFloorCommandKind.Join, outsider, outsider)));
        Assert.Throws<InvalidOperationException>(() => session.Apply(Command(GroupFloorCommandKind.Join, Guid.Empty, Guid.Empty)));
        Assert.Throws<InvalidOperationException>(() => session.Apply(Command(GroupFloorCommandKind.RaiseHand, outsider, outsider)));
        Assert.Throws<InvalidOperationException>(() => session.Apply(Command(GroupFloorCommandKind.Interrupt, outsider, outsider)));
    }

    [Fact]
    public void EndedSessionIgnoresLaterCommands()
    {
        var session = NewSession();
        session.Apply(Command(GroupFloorCommandKind.EndSession, Starter, Guid.Empty));

        session.Apply(Command(GroupFloorCommandKind.Join, Low, Low));

        Assert.True(session.Ended);
        Assert.Empty(session.Participants);
    }

    GroupFloorSession NewSession() => new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), Starter, [Starter, Low, High]);
    GroupFloorCommand Command(GroupFloorCommandKind kind, Guid actor, Guid subject) => new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), kind, actor, subject);
}
