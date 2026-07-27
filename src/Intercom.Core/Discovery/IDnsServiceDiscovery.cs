namespace Intercom.Discovery;

/// <summary>
/// Thin seam over the native Windows DNS-SD facility (DnsServiceRegister /
/// DnsServiceBrowse in dnsapi.dll — there is no managed wrapper). All policy
/// (which interfaces to use, TTL/expiry bookkeeping, dedup, the visible-peer
/// list, re-evaluation on network change) lives in plain C# classes
/// (<see cref="VisiblePeerList"/>, <see cref="DiscoveryService"/>) that
/// depend on this interface, so they're fully unit-testable with a fake —
/// the same seam pattern as ITrayIcon/IStartupService in Intercom.Lifecycle.
/// The real implementation (<see cref="Win32DnsServiceDiscovery"/>) cannot be
/// exercised in a normal CI/test sandbox: it requires a live Windows mDNS
/// responder and firewall-permitted multicast traffic.
/// </summary>
public interface IDnsServiceDiscovery
{
    /// <summary>Registers one `_intercom._tcp.local` service instance bound
    /// to the given interface's address. Disposing stops advertising and
    /// best-effort sends a goodbye record.</summary>
    IDisposable Register(LanInterface iface, ServiceAdvertisement advertisement);

    /// <summary>Starts continuous browsing for `_intercom._tcp.local` on the
    /// given interface. <paramref name="onSignal"/> is invoked for every
    /// sighting/refresh/goodbye the OS reports — continuous, not a one-time
    /// snapshot (ADR-0001). Disposing stops browsing on that interface.</summary>
    IDisposable Browse(LanInterface iface, Action<DiscoverySignal> onSignal);
}
