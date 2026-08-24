using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Intercom.ControlChannel;
using Intercom.Discovery;
using Xunit;

namespace Intercom.App.Tests.Coverage;

public sealed class NativeBoundaryCoverageTests
{
    [Fact]
    public void NetworkSnapshot_ReturnsAStableMaterializedResult()
    {
        var result = new SystemNetworkInterfaceSnapshotProvider().GetCurrentInterfaces();
        Assert.NotNull(result);
        Assert.All(result, item => Assert.False(string.IsNullOrWhiteSpace(item.Id)));
    }

    [Fact]
    public void TlsListener_DisposeIsIdempotentBeforeStart()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Intercom coverage", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        var listener = new SslPeerTransportListener(certificate, 0);
        listener.Dispose();
        listener.Dispose();
    }
}
