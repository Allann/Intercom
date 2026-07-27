using System.Security.Cryptography;
using Intercom.Identity;

namespace Intercom.ControlChannel;

/// <summary>
/// Constant-time SPKI pin comparison for the one security-sensitive path
/// that needs it: validating a live TLS peer's presented certificate against
/// an approved pin (<see cref="PeerCertificateValidator"/>). The certificate
/// presented in a handshake is attacker-controlled input arriving over an
/// active connection, so a data-dependent-time comparison there could in
/// principle let a timing side channel narrow down a valid pin byte by byte.
///
/// <see cref="SpkiPin.Equals"/> itself stays ordinary <c>SequenceEqual</c>
/// (used for plain dictionary/collection lookups elsewhere, e.g.
/// <c>ApprovedPeerRegistry</c>'s dictionary-style lookups) — those compare
/// values that are already locally known and don't cross a live connection
/// as attacker-supplied bytes, so constant-time overhead there would be
/// paying for a threat model that doesn't apply. This narrow helper is
/// deliberately kept in <c>Intercom.ControlChannel</c> rather than changing
/// <c>SpkiPin.Equals</c>'s general-purpose semantics or performance.
/// </summary>
public static class SpkiPinConstantTimeComparer
{
    public static bool Matches(SpkiPin a, SpkiPin b) => CryptographicOperations.FixedTimeEquals(a.Bytes, b.Bytes);
}
