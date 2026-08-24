using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Intercom.Identity;
using Xunit;

namespace Intercom.App.Tests.Identity;

public sealed class LocalIdentityTests
{
    [Fact]
    public void LoadPkcs12_UsesUserKeySetWhenItIsAvailable()
    {
        using var expected = Certificate();
        var calls = new List<X509KeyStorageFlags>();

        var actual = LocalIdentity.LoadPkcs12([], (_, flags) =>
        {
            calls.Add(flags);
            return expected;
        });

        Assert.Same(expected, actual);
        Assert.Equal([X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable], calls);
    }

    [Fact]
    public void LoadPkcs12_WhenUserStoreIsUnavailable_UsesEphemeralKeySet()
    {
        using var expected = Certificate();
        var calls = new List<X509KeyStorageFlags>();

        var actual = LocalIdentity.LoadPkcs12([], (_, flags) =>
        {
            calls.Add(flags);
            if (calls.Count == 1) throw new CryptographicException("store unavailable");
            return expected;
        });

        Assert.Same(expected, actual);
        Assert.Equal(
            [
                X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable,
                X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable,
            ],
            calls);
    }

    [Fact]
    public void LoadPkcs12_DoesNotHideANonCryptographicFailure()
    {
        Assert.Throws<InvalidDataException>(() =>
            LocalIdentity.LoadPkcs12([], (_, _) => throw new InvalidDataException("bad input")));
    }

    static X509Certificate2 Certificate()
    {
        using var key = ECDsa.Create();
        var request = new CertificateRequest("CN=test", key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
    }
}
