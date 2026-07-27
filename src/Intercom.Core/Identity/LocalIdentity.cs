using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Intercom.Identity;

/// <summary>
/// This device's own long-lived cryptographic identity: a self-signed ECDSA
/// P-256 certificate (ADR-0002). 10-year validity, no renewal mechanism in the
/// MVP — expiry is handled identically to identity loss (regenerate, require
/// re-pairing everywhere).
///
/// PeerId is a random, non-secret display/routing identifier, distinct from
/// the cryptographic identity (the SPKI hash). It has no security meaning on
/// its own — see docs/research/pairing-security.md.
/// </summary>
public sealed class LocalIdentity
{
    const int ValidityYears = 10;

    public required Guid PeerId { get; init; }
    public required X509Certificate2 Certificate { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    public SpkiPin SpkiSha256 => SpkiHash.Compute(Certificate);

    public static LocalIdentity CreateNew()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Intercom Peer", ecdsa, HashAlgorithmName.SHA256);

        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5); // small clock-skew allowance
        var notAfter = notBefore.AddYears(ValidityYears);

        // CreateSelfSigned already returns a certificate with a directly usable,
        // exportable private key — no Windows certificate store involved. An
        // earlier version of this method re-exported and reloaded it as an
        // "ephemeral" PKCS#12 for no real benefit, which produced an
        // NTE_BAD_KEY_STATE crash under package identity. Just keep it.
        var certificate = request.CreateSelfSigned(notBefore, notAfter);

        return new LocalIdentity
        {
            PeerId = Guid.NewGuid(),
            Certificate = certificate,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }
}
