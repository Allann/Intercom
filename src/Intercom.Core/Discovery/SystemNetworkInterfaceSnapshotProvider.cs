using System.Net;
using System.Net.NetworkInformation;

namespace Intercom.Discovery;

/// <summary>Real implementation backed by System.Net.NetworkInformation.</summary>
public sealed class SystemNetworkInterfaceSnapshotProvider : INetworkInterfaceSnapshotProvider
{
    public IReadOnlyList<LanInterface> GetCurrentInterfaces()
    {
        var result = new List<LanInterface>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            IPInterfaceProperties props;
            try
            {
                props = nic.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                // An adapter can vanish between enumeration and property
                // lookup (hot-unplug, driver teardown). Skip it rather than
                // fail the whole snapshot.
                continue;
            }

            var addresses = new List<IPAddress>();
            foreach (var unicast in props.UnicastAddresses)
            {
                if (IPAddress.IsLoopback(unicast.Address)) continue;
                addresses.Add(unicast.Address);
            }

            int? ipv4Index = null;
            try { ipv4Index = props.GetIPv4Properties()?.Index; }
            catch (NetworkInformationException) { /* IPv4 not enabled on this adapter */ }

            int? ipv6Index = null;
            try { ipv6Index = props.GetIPv6Properties()?.Index; }
            catch (NetworkInformationException) { /* IPv6 not enabled on this adapter */ }

            result.Add(new LanInterface
            {
                Id = nic.Id,
                Name = nic.Name,
                Type = nic.NetworkInterfaceType,
                OperationalStatus = nic.OperationalStatus,
                SupportsMulticast = nic.SupportsMulticast,
                UnicastAddresses = addresses,
                Ipv4InterfaceIndex = ipv4Index,
                Ipv6InterfaceIndex = ipv6Index,
            });
        }

        return result;
    }
}
