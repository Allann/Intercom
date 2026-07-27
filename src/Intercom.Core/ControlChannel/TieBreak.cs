using Intercom.Identity;

namespace Intercom.ControlChannel;

/// <summary>Which role this device takes for a given tie-break outcome.</summary>
public enum TieBreakRole
{
    /// <summary>Lower SPKI hash: dial out and act as the TLS client.</summary>
    InitiateAsClient,

    /// <summary>Higher SPKI hash: do not dial; wait to accept the other
    /// side's inbound connection instead.</summary>
    WaitAndAccept,
}

/// <summary>
/// Deterministic resolution of "who dials whom," per ADR-0001 — the peer
/// with the lower SPKI hash always initiates as TLS client, the higher waits
/// and accepts. The same rule resolves both a genuine simultaneous-dial race
/// (whichever side loses simply abandons its own outbound attempt) and every
/// post-drop reconnect, so exactly one logical channel ever survives without
/// either side needing a coordination round-trip: both independently compute
/// the identical answer from values they already know.
///
/// This compares already-public values (both sides' SPKI hashes are
/// exchanged in the clear during discovery/pairing) to decide an ordering,
/// not to authenticate anything — unlike the pin-match check in
/// <see cref="PeerCertificateValidator"/>, it does not need to run in
/// constant time.
/// </summary>
public static class TieBreak
{
    public static TieBreakRole Resolve(SpkiPin local, SpkiPin remote)
    {
        var comparison = local.Bytes.SequenceCompareTo(remote.Bytes);
        if (comparison == 0)
        {
            // Two distinct devices must never legitimately present the same
            // SPKI hash. Reaching this almost certainly means the caller
            // compared a peer's hash against itself by mistake (a SHA-256
            // collision between two independently generated ECDSA P-256 keys
            // is not a realistic possibility) — fail loudly rather than
            // silently picking an arbitrary side.
            throw new InvalidOperationException(
                "Cannot resolve a connection tie-break: local and remote SPKI hashes are identical.");
        }

        return comparison < 0 ? TieBreakRole.InitiateAsClient : TieBreakRole.WaitAndAccept;
    }
}
