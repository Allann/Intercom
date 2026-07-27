namespace Intercom.Discovery;

/// <summary>Wraps System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged
/// (a static OS event) behind an instance seam, the same reason
/// INetworkInterfaceSnapshotProvider wraps GetAllNetworkInterfaces(): so
/// DiscoveryService's re-evaluation-on-network-change behavior can be
/// exercised deterministically from a test by raising the event manually,
/// rather than depending on a real adapter changing state.</summary>
public interface INetworkChangeNotifier : IDisposable
{
    event Action? NetworkChanged;
}
