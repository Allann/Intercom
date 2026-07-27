using System.Security.Cryptography.X509Certificates;
using Intercom.Identity;

namespace Intercom.ControlChannel;

/// <summary>
/// Fail-closed peer-identity validation at the moment mutual TLS completes
/// (ADR-0001 / ADR-0002). Pure policy — no socket/stream access, no
/// IdentityStore dependency (callers pass in whatever approved-peer state is
/// relevant) — so it is fully unit-testable against hand-built certificates
/// and registries.
/// </summary>
public static class PeerCertificateValidator
{
    /// <param name="presentedCertificate">The certificate the remote peer
    /// authenticated with during the TLS handshake.</param>
    /// <param name="expectedApprovedPeer">Pass the specific, previously-
    /// approved peer this connection was dialed (or re-dialed) to reach —
    /// e.g. a reconnect after a drop to a known contact's last endpoint — so
    /// a pin mismatch is caught as <see cref="PeerIdentityOutcome.IdentityChanged"/>
    /// rather than silently treated as a brand-new, never-approved peer.
    /// Pass null when accepting a connection with no specific expected
    /// identity (the normal case for a first-time pairing dial, or an
    /// unsolicited inbound connection).</param>
    /// <param name="approvedPeers">The current approved-peer set (typically
    /// <c>IdentityStore.ApprovedPeers</c>) to search when there is no
    /// specific expected peer.</param>
    public static PeerIdentityOutcome Validate(
        X509Certificate2 presentedCertificate,
        ApprovedPeer? expectedApprovedPeer,
        IReadOnlyList<ApprovedPeer> approvedPeers)
    {
        var presentedPin = SpkiHash.Compute(presentedCertificate);

        if (expectedApprovedPeer is not null)
        {
            // Fail-closed, no fallback: a mismatch against a SPECIFIC
            // expected peer is an identity change, full stop — never
            // re-evaluated against the rest of the registry as if this were
            // an ordinary, no-prior-relationship connection. A peer whose
            // pin was already forgotten (SpkiSha256 is null) can never match
            // here either, by construction.
            return expectedApprovedPeer.SpkiSha256 is { } expectedPin
                    && SpkiPinConstantTimeComparer.Matches(presentedPin, expectedPin)
                ? new PeerIdentityOutcome.Approved { Peer = expectedApprovedPeer }
                : new PeerIdentityOutcome.IdentityChanged { PreviouslyApproved = expectedApprovedPeer };
        }

        var match = FindApprovedBySpki(presentedPin, approvedPeers);
        return match is not null
            ? new PeerIdentityOutcome.Approved { Peer = match }
            : new PeerIdentityOutcome.PairingOnly();
    }

    static ApprovedPeer? FindApprovedBySpki(SpkiPin candidate, IReadOnlyList<ApprovedPeer> approvedPeers)
    {
        foreach (var peer in approvedPeers)
        {
            if (peer.Revoked || peer.SpkiSha256 is not { } pin) continue;
            if (SpkiPinConstantTimeComparer.Matches(candidate, pin)) return peer;
        }
        return null;
    }
}
