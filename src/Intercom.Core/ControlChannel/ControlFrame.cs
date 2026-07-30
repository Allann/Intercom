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

    /// <summary>Issue #24: one plain-text chat message to a specific
    /// approved peer (this ticket is not group chat). Gets the mechanical
    /// <see cref="Delivered"/> receipt for free from <see cref="FrameDispatcher"/>
    /// like every other non-meta message type — no separate read receipt
    /// (ADR-0001, CONTEXT.md's "Conversation"/"Spoken chat"). Sent only over
    /// an already-<see cref="ConnectionTrust.Approved"/> connection. Encoded/
    /// decoded by <c>Intercom.Chat.ChatFrameCodec</c>.</summary>
    Chat = 8,

    /// <summary>Issue #25: one attention card (purpose text + emoji icon) to
    /// a specific approved peer (this ticket is not group cards — #29/#30's
    /// job). Gets the mechanical <see cref="Delivered"/> receipt for free
    /// from <see cref="FrameDispatcher"/> like every other non-meta message
    /// type, exactly like <see cref="Chat"/> — that mechanical receipt is
    /// deliberately distinct from <see cref="Acknowledged"/> below (ADR-0001:
    /// "Acknowledged is sent only for attention cards, when a human actually
    /// acts on it"). Sent only over an already-<see cref="ConnectionTrust.Approved"/>
    /// connection. Encoded/decoded by
    /// <c>Intercom.AttentionCards.AttentionCardFrameCodec</c>.</summary>
    AttentionCard = 9,

    /// <summary>Issue #25: the human-triggered acknowledgement of one
    /// specific attention card — ADR-0001's "Acknowledged is sent ONLY for
    /// attention cards, ONLY when a human actually acts on it (never
    /// automatic, unlike Delivered)". Its CorrelationId is the MessageId of
    /// the <see cref="AttentionCard"/> frame it acknowledges — same shape as
    /// <see cref="Delivered"/>'s CorrelationId convention, but sent
    /// explicitly by application code (<c>Intercom.AttentionCards.AttentionCardService.AcknowledgeAsync</c>)
    /// rather than mechanically by <see cref="FrameDispatcher"/>. Empty
    /// payload — like <see cref="Delivered"/>, the CorrelationId is the only
    /// thing that matters. Like every other non-meta message type, an
    /// inbound Acknowledged frame still gets its own mechanical Delivered
    /// receipt in turn (ADR-0001 applies to every message with a message ID,
    /// with no special exception for receipts-of-receipts beyond the
    /// Hello/Delivered meta-messages <see cref="FrameDispatcher"/> already
    /// excludes).</summary>
    Acknowledged = 10,

    /// <summary>Issue #30: the sender's withdrawal broadcast for one fanned-
    /// out interaction (docs/research/active-device-presence.md "Text and
    /// attention cards": "the original sender then broadcasts a
    /// resolved(interaction_id) message to the contact's other live devices
    /// so they withdraw duplicate notifications"). Its CorrelationId is the
    /// MessageId shared by every fanned-out copy of the same card (the
    /// "interaction ID") — same CorrelationId convention as
    /// <see cref="Delivered"/>/<see cref="Acknowledged"/>. Empty payload.
    /// Sent only over an already-<see cref="ConnectionTrust.Approved"/>
    /// connection. Encoded/decoded inline by
    /// <c>Intercom.Routing.AttentionCardFanoutRouter</c> — small and
    /// CorrelationId-only, like <see cref="Acknowledged"/>, so it gets no
    /// separate FrameCodec type of its own. Like every other non-meta message
    /// type, an inbound Resolved frame still gets its own mechanical
    /// Delivered receipt in turn (ADR-0001).</summary>
    Resolved = 11,

    /// <summary>Issue #26: negotiates a unicast UDP voice stream and carries
    /// its fresh AES-GCM key/nonce prefix inside the authenticated TLS channel.</summary>
    AudioSessionOffer = 12,
    AudioSessionAccepted = 13,
    AudioSessionStopped = 14,
    AudioSessionRejected = 15,

    /// <summary>Issue #29: a replicated group voice-floor command. Every
    /// participant applies the same commands locally; no coordinator-private
    /// queue or server-side state exists.</summary>
    GroupFloor = 16,
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
