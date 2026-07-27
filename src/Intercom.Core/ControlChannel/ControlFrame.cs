namespace Intercom.ControlChannel;

/// <summary>
/// Wire message types this ticket defines. Later tickets add more values
/// here as they need them (pairing traffic — issue #22; presence — #23;
/// chat — #24; attention cards — #25) — this module only assigns meaning to
/// Hello and Delivered; any other value decodes fine (an enum has no closed
/// set of valid underlying values) and is dispatched generically by
/// <see cref="FrameDispatcher"/>/<see cref="PeerControlChannel"/> without
/// needing to understand its payload.
/// </summary>
public enum ControlMessageType : ushort
{
    Hello = 1,

    /// <summary>The mechanical, automatic delivery receipt (ADR-0001) — its
    /// CorrelationId is the MessageId of the frame it acknowledges. Distinct
    /// from attention-card Acknowledged, which is out of scope here (#25).</summary>
    Delivered = 2,
}

/// <summary>
/// One framed message on the control channel: a type, a unique message ID,
/// an optional correlation ID (for request/response pairing — used today
/// only by <see cref="ControlMessageType.Delivered"/>, reusable by future
/// message kinds), and an opaque payload. This type only describes the wire
/// shape; <see cref="FrameCodec"/> handles encode/decode and bounds
/// validation, and <see cref="FrameDispatcher"/> handles the mechanical
/// Delivered-receipt bookkeeping.
/// </summary>
public sealed record ControlFrame
{
    public required ControlMessageType Type { get; init; }
    public required Guid MessageId { get; init; }
    public Guid? CorrelationId { get; init; }
    public required byte[] Payload { get; init; }
}
