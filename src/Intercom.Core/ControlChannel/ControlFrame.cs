namespace Intercom.ControlChannel;

/// <summary>
/// Wire message types this ticket defines. Later tickets add more values
/// here as they need them (pairing traffic — issue #22; chat — #24;
/// attention cards — #25) — this module only assigns meaning to Hello and
/// Delivered; any other value decodes fine (an enum has no closed set of
/// valid underlying values) and is dispatched generically by
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

    /// <summary>Issue #22: carries one side's fresh pairing nonce, claimed
    /// peer ID, protocol version, and pairing intent
    /// (docs/research/pairing-security.md step 1). Permitted on a
    /// <see cref="ConnectionTrust.PairingOnly"/> connection — see
    /// <see cref="FrameDispatcher.IsPairingPermitted"/>. Encoded/decoded by
    /// <c>Intercom.Pairing.PairingFrameCodec</c>.</summary>
    PairingNonce = 3,

    /// <summary>Issue #22: "the codes match" — sent once the local user
    /// stamps a postcard approved. Empty payload; the frame's own MessageId
    /// is the only thing that matters. Permitted on
    /// <see cref="ConnectionTrust.PairingOnly"/>.</summary>
    PairingConfirm = 4,

    /// <summary>Issue #22: an explicit, immediate reject (ADR-0002 — distinct
    /// from letting the 2-minute window lapse). Empty payload. Permitted on
    /// <see cref="ConnectionTrust.PairingOnly"/>.</summary>
    PairingReject = 5,

    /// <summary>Issue #22: best-effort "I forgot you" notice (ADR-0002),
    /// sent only if the connection happens to be live at that instant — no
    /// queued/guaranteed delivery. Sent over an already-<see
    /// cref="ConnectionTrust.Approved"/> connection (the peer being forgotten
    /// was, by definition, previously approved), so it needs no
    /// <see cref="FrameDispatcher.IsPairingPermitted"/> entry — Approved
    /// connections already accept every message type.</summary>
    Forgotten = 6,

    /// <summary>Issue #23: a periodic authenticated presence/DND lease. Sent
    /// only over an already-<see cref="ConnectionTrust.Approved"/>
    /// connection — never during pairing, never in unauthenticated mDNS
    /// discovery (docs/research/active-device-presence.md's privacy rules;
    /// enforced by <see cref="PeerControlChannel"/>, which only ever sends
    /// this frame type when its own trust is Approved, and by
    /// <see cref="FrameDispatcher.IsPairingPermitted"/> not listing it, so a
    /// <see cref="ConnectionTrust.PairingOnly"/> connection rejects one on
    /// receipt too). Encoded/decoded by
    /// <c>Intercom.Presence.PresenceFrameCodec</c>.</summary>
    Presence = 7,
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
