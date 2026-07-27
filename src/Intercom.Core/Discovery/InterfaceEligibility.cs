using System.Net.NetworkInformation;

namespace Intercom.Discovery;

/// <summary>
/// Decides which local interfaces automatic discovery should register/browse
/// on (issue #20: "exclude loopback and VPN/tunnel adapters from automatic
/// discovery").
///
/// Perfect VPN-adapter detection is not possible cross-vendor: many VPN
/// clients present a normal-looking <see cref="NetworkInterfaceType.Ethernet"/>
/// TAP/TUN adapter with no reliable marker distinguishing it from a real NIC.
/// This heuristic only catches what .NET can tell us cheaply:
/// <list type="bullet">
/// <item>the interface is actually up and carries at least one non-loopback
/// unicast address (an interface with no address can't usefully register or
/// browse mDNS anyway);</item>
/// <item><see cref="NetworkInterfaceType.Loopback"/> is always excluded;</item>
/// <item><see cref="NetworkInterfaceType.Tunnel"/> is always excluded — this
/// is the one type .NET labels explicitly as a tunnel/VPN-style adapter;</item>
/// <item>an interface that reports it does not support multicast is excluded,
/// since mDNS depends on multicast — this also happens to exclude some
/// point-to-point VPN adapters that disable multicast, though not all of
/// them do.</item>
/// </list>
/// A vendor VPN adapter that reports Ethernet, up, multicast-capable, and a
/// real-looking address will NOT be excluded by this heuristic. That is a
/// known limitation, not an oversight — see the "what this does NOT prove"
/// section of prototypes/14-lan-resilience/README.md for the class of things
/// that need real hardware/VPN clients to validate.
/// </summary>
public static class InterfaceEligibility
{
    public static bool IsEligible(LanInterface iface)
    {
        if (iface.OperationalStatus != OperationalStatus.Up) return false;
        if (iface.Type == NetworkInterfaceType.Loopback) return false;
        if (iface.Type == NetworkInterfaceType.Tunnel) return false;
        if (!iface.SupportsMulticast) return false;
        if (iface.UnicastAddresses.Count == 0) return false;

        return true;
    }

    public static IReadOnlyList<LanInterface> FilterEligible(IEnumerable<LanInterface> all) =>
        all.Where(IsEligible).ToList();
}
