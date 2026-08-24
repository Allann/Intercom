using Intercom.Discovery;
using Xunit;

namespace Intercom.App.Tests.Discovery;

public sealed class ResolvedServiceReducerTests
{
    [Fact]
    public void ReduceBuildsScopedAddressesAndMetadata()
    {
        var data = new ResolvedServiceData(
            new Dictionary<string, string?>
            {
                [DiscoveryProtocol.TxtKeyPeerIdHint] = "peer-a",
                [DiscoveryProtocol.TxtKeyVersion] = "7",
                [DiscoveryProtocol.TxtKeySpki] = Convert.ToHexString(new byte[32]),
            },
            [192, 168, 1, 2],
            [0xfe, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1],
            4242);

        var result = ResolvedServiceReducer.Reduce(data, 19);

        Assert.NotNull(result);
        Assert.Equal("peer-a", result.Value.PeerIdHint.Value);
        Assert.Equal(7, result.Value.ProtocolVersion);
        Assert.Equal("192.168.1.2", result.Value.Ipv4!.ToString());
        Assert.Equal(19, result.Value.Ipv6!.ScopeId);
        Assert.Equal(4242, result.Value.Port);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void ReduceRejectsMissingIdentity(string? hint)
    {
        var properties = new Dictionary<string, string?>();
        if (hint is not null) properties[DiscoveryProtocol.TxtKeyPeerIdHint] = hint;

        Assert.Null(ResolvedServiceReducer.Reduce(new ResolvedServiceData(properties, null, null, 1), 0));
    }

    [Fact]
    public void ReduceRejectsInvalidSpki()
    {
        var properties = new Dictionary<string, string?>
        {
            [DiscoveryProtocol.TxtKeyPeerIdHint] = "peer-a",
            [DiscoveryProtocol.TxtKeySpki] = "not-hex",
        };

        Assert.Null(ResolvedServiceReducer.Reduce(new ResolvedServiceData(properties, null, null, 1), 0));
    }

    [Fact]
    public void ReduceRejectsWellFormedHexWithTheWrongSpkiLength()
    {
        var properties = new Dictionary<string, string?>
        {
            [DiscoveryProtocol.TxtKeyPeerIdHint] = "peer-a",
            [DiscoveryProtocol.TxtKeySpki] = "00",
        };

        Assert.Null(ResolvedServiceReducer.Reduce(new ResolvedServiceData(properties, null, null, 1), 0));
    }

    [Fact]
    public void ReduceUsesDefaultsForOptionalValues()
    {
        var properties = new Dictionary<string, string?>
        {
            [DiscoveryProtocol.TxtKeyPeerIdHint] = "peer-a",
            [DiscoveryProtocol.TxtKeyVersion] = "invalid",
        };

        var result = ResolvedServiceReducer.Reduce(new ResolvedServiceData(properties, null, null, 1), 0);

        Assert.NotNull(result);
        Assert.Equal(0, result.Value.ProtocolVersion);
        Assert.Null(result.Value.Spki);
        Assert.Null(result.Value.Ipv4);
        Assert.Null(result.Value.Ipv6);
    }

    [Fact]
    public void ReduceTreatsExplicitNullSpkiAsAbsent()
    {
        var properties = new Dictionary<string, string?>
        {
            [DiscoveryProtocol.TxtKeyPeerIdHint] = "peer-a",
            [DiscoveryProtocol.TxtKeySpki] = null,
        };

        var result = ResolvedServiceReducer.Reduce(new ResolvedServiceData(properties, null, null, 1), 0);

        Assert.NotNull(result);
        Assert.Null(result.Value.Spki);
    }

    [Fact]
    public void ReduceDoesNotScopeGlobalIpv6Address()
    {
        var properties = new Dictionary<string, string?>
        {
            [DiscoveryProtocol.TxtKeyPeerIdHint] = "peer-a",
        };
        var global = System.Net.IPAddress.Parse("2001:db8::1").GetAddressBytes();

        var result = ResolvedServiceReducer.Reduce(new ResolvedServiceData(properties, null, global, 1), 19);

        Assert.NotNull(result);
        Assert.Equal(0, result.Value.Ipv6!.ScopeId);
    }
}
