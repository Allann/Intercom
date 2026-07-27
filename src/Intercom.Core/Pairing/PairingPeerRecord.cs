using Intercom.Identity;

namespace Intercom.Pairing;

/// <summary>
/// One side's contribution to a pairing transcript (docs/research/pairing-security.md
/// "First-contact pairing ceremony" step 2): the SPKI hash proven by that
/// side's TLS client certificate, the fresh nonce it generated, and its
/// claimed peer ID. Both sides build one of these for themselves and one for
/// the other side (from the wire <c>PairingNonce</c> message they received)
/// before either can compute the shared transcript.
/// </summary>
public sealed record PairingPeerRecord
{
    public required SpkiPin Spki { get; init; }
    public required PairingNonce Nonce { get; init; }
    public required Guid PeerId { get; init; }
}
