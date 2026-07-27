using System.Net;
using System.Net.NetworkInformation;
using Intercom.Discovery;
using Xunit;

namespace Intercom.App.Tests.Discovery;

public class InterfaceEligibilityTests
{
    static LanInterface Make(
        string id = "eth0",
        NetworkInterfaceType type = NetworkInterfaceType.Ethernet,
        OperationalStatus status = OperationalStatus.Up,
        bool supportsMulticast = true,
        params IPAddress[] addresses) => new()
    {
        Id = id,
        Name = id,
        Type = type,
        OperationalStatus = status,
        SupportsMulticast = supportsMulticast,
        UnicastAddresses = addresses.Length == 0 ? [IPAddress.Parse("192.168.1.10")] : addresses,
    };

    [Fact]
    public void UpEthernetWithAddress_IsEligible()
    {
        Assert.True(InterfaceEligibility.IsEligible(Make()));
    }

    [Fact]
    public void Loopback_IsNotEligible()
    {
        var iface = Make(type: NetworkInterfaceType.Loopback, addresses: [IPAddress.Loopback]);
        Assert.False(InterfaceEligibility.IsEligible(iface));
    }

    [Fact]
    public void Tunnel_IsNotEligible()
    {
        var iface = Make(type: NetworkInterfaceType.Tunnel);
        Assert.False(InterfaceEligibility.IsEligible(iface));
    }

    [Fact]
    public void DownInterface_IsNotEligible()
    {
        var iface = Make(status: OperationalStatus.Down);
        Assert.False(InterfaceEligibility.IsEligible(iface));
    }

    [Fact]
    public void MulticastDisabled_IsNotEligible()
    {
        var iface = Make(supportsMulticast: false);
        Assert.False(InterfaceEligibility.IsEligible(iface));
    }

    [Fact]
    public void NoUnicastAddresses_IsNotEligible()
    {
        var iface = Make() with { UnicastAddresses = [] };
        Assert.False(InterfaceEligibility.IsEligible(iface));
    }

    [Fact]
    public void Ipv6LinkLocalAddress_IsStillEligible()
    {
        var iface = Make(addresses: IPAddress.Parse("fe80::1"));
        Assert.True(InterfaceEligibility.IsEligible(iface));
    }

    [Fact]
    public void FilterEligible_KeepsOnlyEligibleInterfaces()
    {
        var wifi = Make(id: "wifi0");
        var loop = Make(id: "loop0", type: NetworkInterfaceType.Loopback, addresses: [IPAddress.Loopback]);
        var tunnel = Make(id: "tun0", type: NetworkInterfaceType.Tunnel);

        var result = InterfaceEligibility.FilterEligible([wifi, loop, tunnel]);

        Assert.Single(result);
        Assert.Equal("wifi0", result[0].Id);
    }
}
