using System.Net;
using System.Net.NetworkInformation;

namespace Intercom.Discovery;

/// <summary>
/// A snapshot of one local network interface, reduced to exactly what
/// discovery policy needs to decide eligibility and where to register/browse.
/// Deliberately not the live <see cref="NetworkInterface"/> object: that type
/// can't be constructed in a test, so policy logic (<see cref="InterfaceEligibility"/>,
/// <see cref="DiscoveryService"/>) is written against this plain DTO instead,
/// which a test can build by hand.
/// </summary>
public sealed record LanInterface
{
    /// <summary>Stable-for-this-boot identifier (NetworkInterface.Id). Used to
    /// correlate registrations/browses with the interface that produced them
    /// across re-evaluation, not persisted anywhere.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }
    public required NetworkInterfaceType Type { get; init; }
    public required OperationalStatus OperationalStatus { get; init; }
    public required bool SupportsMulticast { get; init; }

    /// <summary>Non-loopback unicast addresses currently bound to this
    /// interface, IPv4 and IPv6 alike. IPv6 link-local addresses keep their
    /// ScopeId (IPAddress already carries it) so callers can address them
    /// correctly on a multi-homed PC.</summary>
    public required IReadOnlyList<IPAddress> UnicastAddresses { get; init; }

    /// <summary>Numeric adapter index for the IPv4 stack, when this interface
    /// has one — DnsServiceRegister/DnsServiceBrowse address a specific
    /// adapter by numeric index, not by NetworkInterface.Id.</summary>
    public int? Ipv4InterfaceIndex { get; init; }

    /// <summary>Numeric adapter index for the IPv6 stack, when this interface
    /// has one. Distinct from <see cref="Ipv4InterfaceIndex"/> because
    /// Windows can (rarely) report different index values per family for the
    /// same physical adapter.</summary>
    public int? Ipv6InterfaceIndex { get; init; }
}
