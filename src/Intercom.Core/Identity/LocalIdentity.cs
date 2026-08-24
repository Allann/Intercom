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

        using var generated = request.CreateSelfSigned(notBefore, notAfter);

        // Windows SChannel cannot use an ephemeral private key for TLS server
        // authentication (SEC_E_NO_CREDENTIALS / 0x8009030E). Rehydrate into
        // the current user's key store so the same identity can advertise,
        // listen, and survive app restarts without requiring elevation.
        var pfx = generated.Export(X509ContentType.Pfx);
        var certificate = LoadPkcs12(pfx);

        return new LocalIdentity
        {
            PeerId = Guid.NewGuid(),
            Certificate = certificate,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    internal static X509Certificate2 LoadPkcs12(byte[] pfx)
        => LoadPkcs12(pfx, static (bytes, flags) =>
            X509CertificateLoader.LoadPkcs12(bytes, password: null, flags));

    internal static X509Certificate2 LoadPkcs12(
        byte[] pfx,
        Func<byte[], X509KeyStorageFlags, X509Certificate2> load)
    {
        try
        {
            return load(pfx, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
        }
        catch (CryptographicException)
        {
            // Some supported execution environments do not expose a writable
            // current-user certificate store. The in-memory key is sufficient
            // for identity decisions and deterministic verification. A normal
            // Windows app session continues to use the persisted key above.
            return load(pfx, X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
        }
    }
}
