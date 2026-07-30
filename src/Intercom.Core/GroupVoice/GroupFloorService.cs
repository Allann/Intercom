using Intercom.ControlChannel;

namespace Intercom.GroupVoice;

public sealed class GroupFloorService : IDisposable
{
    readonly Guid _localPeerId;
    readonly IGroupFloorTransport _transport;
    readonly IGroupAudioPreparer _audio;

    public GroupFloorService(Guid localPeerId, GroupFloorSession session, IGroupFloorTransport transport, IGroupAudioPreparer audio)
    {
        if (localPeerId == Guid.Empty) throw new ArgumentException("A local peer ID is required.", nameof(localPeerId));
        _localPeerId = localPeerId;
        Session = session ?? throw new ArgumentNullException(nameof(session));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _transport.FrameReceived += OnFrameReceived;
    }

    public GroupFloorSession Session { get; }
    public event Action? StateChanged;
    public event Action<Exception>? CommandRejected;

    public async Task JoinAsync(Guid peerId, CancellationToken cancellationToken)
    {
        if (_localPeerId.CompareTo(peerId) < 0)
            await _audio.PrepareAsync(peerId, cancellationToken).ConfigureAwait(false);
        await SendAsync(GroupFloorCommandKind.Join, peerId, cancellationToken).ConfigureAwait(false);
    }

    public Task LeaveAsync(CancellationToken ct) => SendAsync(GroupFloorCommandKind.Leave, _localPeerId, ct);
    public Task RaiseHandAsync(CancellationToken ct) => SendAsync(GroupFloorCommandKind.RaiseHand, _localPeerId, ct);
    public Task LowerHandAsync(CancellationToken ct) => SendAsync(GroupFloorCommandKind.LowerHand, _localPeerId, ct);
    public Task GrantFloorAsync(Guid peerId, CancellationToken ct) => SendAsync(GroupFloorCommandKind.GrantFloor, peerId, ct);
    public Task InterruptAsync(CancellationToken ct) => SendAsync(GroupFloorCommandKind.Interrupt, _localPeerId, ct);
    public Task EndForEveryoneAsync(CancellationToken ct) => SendAsync(GroupFloorCommandKind.EndSession, Guid.Empty, ct);

    /// <summary>Applies transport-observed departure locally. Every remaining
    /// peer observes the same roster loss and independently reaches the same
    /// coordinator without an election or a spoofable broadcast.</summary>
    public void ParticipantDeparted(Guid peerId)
    {
        Session.Apply(new GroupFloorCommand(Session.SessionId, GroupFloorCommandKind.Leave, peerId, peerId));
        StateChanged?.Invoke();
    }

    async Task SendAsync(GroupFloorCommandKind kind, Guid subject, CancellationToken cancellationToken)
    {
        var command = new GroupFloorCommand(Session.SessionId, kind, _localPeerId, subject);
        var recipients = Session.Participants.Where(id => id != _localPeerId).ToArray();
        Session.Apply(command);
        StateChanged?.Invoke();
        await _transport.SendAsync(recipients, GroupFloorFrameCodec.Encode(command), cancellationToken).ConfigureAwait(false);
    }

    void OnFrameReceived(Guid senderPeerId, ControlFrame frame)
    {
        if (frame.Type != ControlMessageType.GroupFloor) return;
        try
        {
            var command = GroupFloorFrameCodec.Decode(frame);
            if (command.ActorPeerId != senderPeerId) throw new InvalidOperationException("Group-floor actor does not match its authenticated sender.");
            Session.Apply(command);
            if (command.Kind == GroupFloorCommandKind.Join && _localPeerId.CompareTo(command.SubjectPeerId) < 0)
                _ = _audio.PrepareAsync(command.SubjectPeerId, CancellationToken.None);
            StateChanged?.Invoke();
        }
        catch (Exception ex) when (ex is MalformedFrameException or InvalidOperationException)
        {
            CommandRejected?.Invoke(ex);
        }
    }

    public void Dispose() => _transport.FrameReceived -= OnFrameReceived;
}
