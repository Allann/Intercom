using System.Net;

namespace Intercom.Discovery;

/// <summary>
/// One reachable address for a discovered peer's control-channel endpoint
/// (the SRV target). A peer can have several of these — one per address
/// family per interface it was seen on. IPv6 link-local addresses only mean
/// anything alongside the interface they're scoped to, so InterfaceId always
/// travels with the address rather than being dropped after resolution.
/// </summary>
public sealed record PeerEndpoint
{
    public required IPAddress Address { get; init; }
    public required int Port { get; init; }

    /// <summary>The local interface this endpoint was observed/is reachable
    /// on (LanInterface.Id) — required to correctly address an IPv6
    /// link-local destination on a multi-homed PC (its scope ID is only
    /// meaningful relative to a specific local interface).</summary>
    public required string InterfaceId { get; init; }
}
