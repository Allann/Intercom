namespace Intercom.Audio;

[Flags]
public enum AudioPacketFlags : ushort
{
    None = 0,
    EndOfTalkspurt = 1,
}

public sealed record AudioPacket
{
    public required Guid SessionId { get; init; }
    public required Guid StreamId { get; init; }
    public required ulong Sequence { get; init; }
    public required ulong SampleTimestamp { get; init; }
    public required AudioPacketFlags Flags { get; init; }
    public required byte[] OpusPayload { get; init; }
}
