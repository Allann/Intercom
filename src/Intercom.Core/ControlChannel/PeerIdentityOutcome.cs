using Intercom.Identity;

namespace Intercom.ControlChannel;

/// <summary>
/// Result of validating a freshly-presented peer certificate against this
/// device's approved-peer registry, evaluated once right after mutual TLS
/// completes (see <see cref="PeerCertificateValidator"/>). Exactly one of
/// these three outcomes ever applies to a given handshake — in particular,
/// <see cref="IdentityChanged"/> is never silently downgraded to
/// <see cref="PairingOnly"/> within the same connection (ADR-0002's "no
/// fallback from a pin mismatch to pairing mode").
/// </summary>
public abstract record PeerIdentityOutcome
{
    private PeerIdentityOutcome() { }

    /// <summary>The presented SPKI hash matches an approved peer's pin.
    /// Becomes <see cref="ConnectionTrust.Approved"/>.</summary>
    public sealed record Approved : PeerIdentityOutcome
    {
        public required ApprovedPeer Peer { get; init; }
    }

    /// <summary>No SPECIFIC peer was expected on this connection (a fresh
    /// inbound connection with no prior relationship), and the presented
    /// SPKI hash matches no approved peer. Becomes
    /// <see cref="ConnectionTrust.PairingOnly"/>.</summary>
    public sealed record PairingOnly : PeerIdentityOutcome;

    /// <summary>This connection was expected to reach a SPECIFIC,
    /// previously-approved peer (e.g. a reconnect to a known contact's last
    /// endpoint), but the presented certificate's SPKI hash does not match
    /// that peer's pinned hash. Fail closed: never becomes
    /// <see cref="ConnectionTrust.PairingOnly"/> or
    /// <see cref="ConnectionTrust.Approved"/> — the app layer must show
    /// "identity changed" and require an explicit forget/re-pair action
    /// before this peer can be trusted under any hash again (ADR-0002).</summary>
    public sealed record IdentityChanged : PeerIdentityOutcome
    {
        public required ApprovedPeer PreviouslyApproved { get; init; }
    }
}
