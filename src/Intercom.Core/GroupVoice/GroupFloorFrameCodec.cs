using Intercom.ControlChannel;

namespace Intercom.GroupVoice;

public static class GroupFloorFrameCodec
{
    const int PayloadLength = 49;

    public static ControlFrame Encode(GroupFloorCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var payload = new byte[PayloadLength];
        command.SessionId.TryWriteBytes(payload);
        payload[16] = (byte)command.Kind;
        command.ActorPeerId.TryWriteBytes(payload.AsSpan(17));
        command.SubjectPeerId.TryWriteBytes(payload.AsSpan(33));
        return new ControlFrame { Type = ControlMessageType.GroupFloor, MessageId = Guid.NewGuid(), Payload = payload };
    }

    public static GroupFloorCommand Decode(ControlFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Type != ControlMessageType.GroupFloor || frame.Payload.Length != PayloadLength)
            throw new MalformedFrameException("Invalid group-floor frame.");
        var kind = (GroupFloorCommandKind)frame.Payload[16];
        if (!Enum.IsDefined(kind)) throw new MalformedFrameException("Unknown group-floor command.");
        var sessionId = new Guid(frame.Payload.AsSpan(0, 16));
        var actor = new Guid(frame.Payload.AsSpan(17, 16));
        var subject = new Guid(frame.Payload.AsSpan(33, 16));
        if (sessionId == Guid.Empty || actor == Guid.Empty)
            throw new MalformedFrameException("Group-floor IDs cannot be empty.");
        return new GroupFloorCommand(sessionId, kind, actor, subject);
    }
}
