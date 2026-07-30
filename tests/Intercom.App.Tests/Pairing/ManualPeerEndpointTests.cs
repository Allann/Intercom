using Intercom.Pairing;
using Xunit;

namespace Intercom.App.Tests.Pairing;

public sealed class ManualPeerEndpointTests
{
    [Theory]
    [InlineData("family-vpn.local", "family-vpn.local", 47811)]
    [InlineData("10.20.30.40:49000", "10.20.30.40", 49000)]
    [InlineData("[fd00::42]:49000", "[fd00::42]", 49000)]
    public void Parse_AcceptsHostnameAndIpWithOptionalPort(string input, string host, int port)
    {
        var endpoint = ManualPeerEndpoint.Parse(input, 47811);
        Assert.Equal(host.Trim('[', ']'), endpoint.Host.Trim('[', ']'));
        Assert.Equal(port, endpoint.Port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("host:99999")]
    public void Parse_RejectsInvalidInput(string input) =>
        Assert.Throws<FormatException>(() => ManualPeerEndpoint.Parse(input, 47811));
}
