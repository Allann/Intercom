namespace Intercom.ControlChannel;

/// <summary>
/// The trust level of one live connection, fixed once when the handshake
/// completes and never re-evaluated mid-connection — a change of mind (e.g.
/// the peer later gets approved via pairing) requires a fresh connection,
/// not an in-place upgrade. <see cref="FrameDispatcher"/> checks this on
/// every inbound frame.
///
/// Closed hierarchy (private constructor) so a third, invented trust level
/// can't be constructed elsewhere and silently bypass dispatch's fail-closed
/// default.
/// </summary>
public abstract record ConnectionTrust
{
    private ConnectionTrust() { }

    /// <summary>The presented certificate's SPKI hash matched an approved
    /// peer's pin. Ordinary framed-message dispatch is permitted.</summary>
    public sealed record Approved : ConnectionTrust
    {
        public required Guid PeerId { get; init; }
    }

    /// <summary>The presented certificate's SPKI hash is not (yet) in the
    /// approved registry. TLS still completed — so a future pairing exchange
    /// can happen over this same connection (ADR-0001) — but
    /// <see cref="FrameDispatcher"/> rejects every message type except
    /// whatever pairing needs. No pairing message types exist yet (issue
    /// #22), so today that means Hello only; this is the seam #22 extends,
    /// not a hardcoded "reject everything" with no way in.</summary>
    public sealed record PairingOnly : ConnectionTrust;
}
