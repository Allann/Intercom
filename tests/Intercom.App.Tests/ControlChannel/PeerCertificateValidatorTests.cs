using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Intercom.ControlChannel;
using Intercom.Identity;
using Xunit;

namespace Intercom.App.Tests.ControlChannel;

public class PeerCertificateValidatorTests
{
    [Fact]
    public void NoExpectedPeer_PresentedCertMatchesApprovedRegistry_ReturnsApproved()
    {
        using var cert = SelfSignedCert();
        var approved = MakeApprovedPeer(cert);

        var outcome = PeerCertificateValidator.Validate(cert, expectedApprovedPeer: null, approvedPeers: [approved]);

        var approvedOutcome = Assert.IsType<PeerIdentityOutcome.Approved>(outcome);
        Assert.Same(approved, approvedOutcome.Peer);
    }

    [Fact]
    public void NoExpectedPeer_PresentedCertNotInRegistry_ReturnsPairingOnly()
    {
        using var cert = SelfSignedCert();

        var outcome = PeerCertificateValidator.Validate(cert, expectedApprovedPeer: null, approvedPeers: []);

        Assert.IsType<PeerIdentityOutcome.PairingOnly>(outcome);
    }

    [Fact]
    public void NoExpectedPeer_RevokedRegistryEntry_NeverMatches_ReturnsPairingOnly()
    {
        using var cert = SelfSignedCert();
        var approved = MakeApprovedPeer(cert) with { Revoked = true };

        var outcome = PeerCertificateValidator.Validate(cert, expectedApprovedPeer: null, approvedPeers: [approved]);

        Assert.IsType<PeerIdentityOutcome.PairingOnly>(outcome);
    }

    [Fact]
    public void ExpectedPeer_PresentedCertMatchesItsPin_ReturnsApproved()
    {
        using var cert = SelfSignedCert();
        var expected = MakeApprovedPeer(cert);

        var outcome = PeerCertificateValidator.Validate(cert, expectedApprovedPeer: expected, approvedPeers: [expected]);

        Assert.IsType<PeerIdentityOutcome.Approved>(outcome);
    }

    [Fact]
    public void ExpectedPeer_PresentedCertHasDifferentPin_ReturnsIdentityChanged_NeverPairingOnly()
    {
        using var previousCert = SelfSignedCert();
        using var newCert = SelfSignedCert(); // different key -> different SPKI hash
        var expected = MakeApprovedPeer(previousCert);

        var outcome = PeerCertificateValidator.Validate(newCert, expectedApprovedPeer: expected, approvedPeers: [expected]);

        var changed = Assert.IsType<PeerIdentityOutcome.IdentityChanged>(outcome);
        Assert.Same(expected, changed.PreviouslyApproved);
    }

    [Fact]
    public void ExpectedPeer_MismatchIsNeverReEvaluatedAgainstTheRestOfTheRegistry()
    {
        // Even if the presented cert happens to match some OTHER approved
        // peer, a mismatch against the SPECIFICALLY expected peer must still
        // surface as IdentityChanged for that expected peer — no fallback.
        using var previousCert = SelfSignedCert();
        using var newCert = SelfSignedCert();
        var expected = MakeApprovedPeer(previousCert);
        var otherApprovedPeer = MakeApprovedPeer(newCert);

        var outcome = PeerCertificateValidator.Validate(newCert, expectedApprovedPeer: expected, approvedPeers: [expected, otherApprovedPeer]);

        var changed = Assert.IsType<PeerIdentityOutcome.IdentityChanged>(outcome);
        Assert.Same(expected, changed.PreviouslyApproved);
    }

    [Fact]
    public void ExpectedPeer_WithForgottenPin_AlwaysIdentityChanged()
    {
        using var cert = SelfSignedCert();
        var expected = MakeApprovedPeer(cert) with { SpkiSha256 = null, Revoked = true };

        var outcome = PeerCertificateValidator.Validate(cert, expectedApprovedPeer: expected, approvedPeers: [expected]);

        Assert.IsType<PeerIdentityOutcome.IdentityChanged>(outcome);
    }

    static X509Certificate2 SelfSignedCert()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Test Peer", ecdsa, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
    }

    static ApprovedPeer MakeApprovedPeer(X509Certificate2 cert) => new()
    {
        PeerId = Guid.NewGuid(),
        FriendlyName = "Test",
        SpkiSha256 = new SpkiPin(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo())),
        Certificate = cert.Export(X509ContentType.Cert),
        ApprovedAt = DateTimeOffset.UtcNow,
    };
}
