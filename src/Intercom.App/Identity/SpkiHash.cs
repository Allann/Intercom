using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Intercom.App.Identity;

/// <summary>
/// SHA-256 over a certificate's DER-encoded SubjectPublicKeyInfo. Per ADR-0002,
/// this — not the certificate itself, its subject, or any friendly name — is
/// what pins an approved peer's identity, so a certificate can be reissued
/// around the same key without silently changing the peer.
/// </summary>
static class SpkiHash
{
    public static SpkiPin Compute(X509Certificate2 certificate) =>
        new(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));
}
