namespace Intercom.GroupVoice;

/// <summary>
/// Replicated state for one group voice conversation. It deliberately knows
/// nothing about hands-free sessions: granting the voice floor changes only
/// this control-plane state and never starts or renegotiates audio.
/// </summary>
public sealed class GroupFloorSession
{
    readonly HashSet<Guid> _participants;
    readonly List<Guid> _queue = [];

    public GroupFloorSession(Guid sessionId, Guid starterPeerId, IEnumerable<Guid> participants)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("A session ID is required.", nameof(sessionId));
        if (starterPeerId == Guid.Empty) throw new ArgumentException("A starter peer ID is required.", nameof(starterPeerId));
        SessionId = sessionId;
        StarterPeerId = starterPeerId;
        _participants = participants.Where(id => id != Guid.Empty).ToHashSet();
        _participants.Add(starterPeerId);
        CoordinatorPeerId = starterPeerId;
    }

    public Guid SessionId { get; }
    public Guid StarterPeerId { get; }
    public Guid? CoordinatorPeerId { get; private set; }
    public Guid? SpeakerPeerId { get; private set; }
    public bool Ended { get; private set; }
    public IReadOnlyCollection<Guid> Participants => _participants;
    public IReadOnlyList<Guid> RaiseHandQueue => _queue;

    public void Apply(GroupFloorCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.SessionId != SessionId) throw new InvalidOperationException("Command belongs to another group session.");
        if (Ended) return;

        switch (command.Kind)
        {
            case GroupFloorCommandKind.Join:
                RequirePeer(command.SubjectPeerId);
                _participants.Add(command.SubjectPeerId);
                break;
            case GroupFloorCommandKind.Leave:
                Leave(command.SubjectPeerId);
                break;
            case GroupFloorCommandKind.RaiseHand:
                RequireActive(command.SubjectPeerId);
                if (SpeakerPeerId != command.SubjectPeerId && !_queue.Contains(command.SubjectPeerId))
                    _queue.Add(command.SubjectPeerId);
                break;
            case GroupFloorCommandKind.LowerHand:
                _queue.Remove(command.SubjectPeerId);
                break;
            case GroupFloorCommandKind.GrantFloor:
                RequireCoordinator(command.ActorPeerId);
                RequireActive(command.SubjectPeerId);
                SpeakerPeerId = command.SubjectPeerId;
                _queue.Remove(command.SubjectPeerId);
                break;
            case GroupFloorCommandKind.Interrupt:
                RequireActive(command.SubjectPeerId);
                SpeakerPeerId = command.SubjectPeerId;
                _queue.Remove(command.SubjectPeerId);
                break;
            case GroupFloorCommandKind.EndSession:
                RequireCoordinator(command.ActorPeerId);
                End();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command));
        }
    }

    void Leave(Guid peerId)
    {
        if (!_participants.Remove(peerId)) return;
        _queue.Remove(peerId);
        if (SpeakerPeerId == peerId) SpeakerPeerId = null;
        if (_participants.Count == 0) { End(); return; }
        if (CoordinatorPeerId == peerId)
            CoordinatorPeerId = _participants.Min();
    }

    void End()
    {
        Ended = true;
        CoordinatorPeerId = null;
        SpeakerPeerId = null;
        _queue.Clear();
        _participants.Clear();
    }

    static void RequirePeer(Guid peerId)
    {
        if (peerId == Guid.Empty) throw new InvalidOperationException("Command requires a peer.");
    }

    void RequireActive(Guid peerId)
    {
        RequirePeer(peerId);
        if (!_participants.Contains(peerId)) throw new InvalidOperationException("Peer is not an active participant.");
    }

    void RequireCoordinator(Guid peerId)
    {
        if (CoordinatorPeerId != peerId) throw new InvalidOperationException("Only the current floor coordinator may do that.");
    }
}

public enum GroupFloorCommandKind : byte
{
    Join = 1,
    Leave = 2,
    RaiseHand = 3,
    LowerHand = 4,
    GrantFloor = 5,
    Interrupt = 6,
    EndSession = 7,
}

public sealed record GroupFloorCommand(
    Guid SessionId,
    GroupFloorCommandKind Kind,
    Guid ActorPeerId,
    Guid SubjectPeerId);
