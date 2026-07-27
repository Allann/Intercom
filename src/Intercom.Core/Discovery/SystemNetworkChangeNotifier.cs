using System.Net.NetworkInformation;

namespace Intercom.Discovery;

/// <summary>Real implementation backed by NetworkChange.NetworkAddressChanged.</summary>
public sealed class SystemNetworkChangeNotifier : INetworkChangeNotifier
{
    public event Action? NetworkChanged;

    public SystemNetworkChangeNotifier()
    {
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
    }

    void OnAddressChanged(object? sender, EventArgs e) => NetworkChanged?.Invoke();

    public void Dispose() => NetworkChange.NetworkAddressChanged -= OnAddressChanged;
}
