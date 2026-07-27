using System.Net;
using System.Net.NetworkInformation;
using Intercom.Discovery;
using Xunit;

namespace Intercom.App.Tests.Discovery;

public class DiscoveryServiceTests
{
    static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    static LanInterface Eligible(string id) => new()
    {
        Id = id,
        Name = id,
        Type = NetworkInterfaceType.Ethernet,
        OperationalStatus = OperationalStatus.Up,
        SupportsMulticast = true,
        UnicastAddresses = [IPAddress.Parse("192.168.1.10")],
    };

    static LanInterface Ineligible(string id) => new()
    {
        Id = id,
        Name = id,
        Type = NetworkInterfaceType.Tunnel,
        OperationalStatus = OperationalStatus.Up,
        SupportsMulticast = true,
        UnicastAddresses = [IPAddress.Parse("10.8.0.2")],
    };

    static DiscoveryService MakeService(
        FakeDnsServiceDiscovery dns,
        FakeInterfaceSnapshotProvider interfaces,
        FakeNetworkChangeNotifier networkChange,
        DateTimeOffset? now = null) => new(
            dns,
            interfaces,
            networkChange,
            new PeerIdHint("localpeer"),
            controlChannelPort: 5001,
            clock: () => now ?? Epoch,
            expirySweepInterval: TimeSpan.FromDays(1)); // keep the real timer from firing mid-test

    [Fact]
    public void Start_RegistersAndBrowsesEveryEligibleInterface()
    {
        var dns = new FakeDnsServiceDiscovery();
        var interfaces = new FakeInterfaceSnapshotProvider([Eligible("eth0"), Eligible("wifi0")]);
        var service = MakeService(dns, interfaces, new FakeNetworkChangeNotifier());

        service.Start();

        Assert.Equal(2, dns.Registrations.Count);
        Assert.Equal(2, dns.Browses.Count);
    }

    [Fact]
    public void Start_SkipsIneligibleInterfaces()
    {
        var dns = new FakeDnsServiceDiscovery();
        var interfaces = new FakeInterfaceSnapshotProvider([Eligible("eth0"), Ineligible("tun0")]);
        var service = MakeService(dns, interfaces, new FakeNetworkChangeNotifier());

        service.Start();

        Assert.Single(dns.Registrations);
        Assert.Equal("eth0", dns.Registrations[0].Interface.Id);
    }

    [Fact]
    public void Start_CalledTwice_Throws()
    {
        var service = MakeService(new FakeDnsServiceDiscovery(), new FakeInterfaceSnapshotProvider([]), new FakeNetworkChangeNotifier());
        service.Start();

        Assert.Throws<InvalidOperationException>(() => service.Start());
    }

    [Fact]
    public void BrowseSignal_PopulatesVisiblePeers()
    {
        var dns = new FakeDnsServiceDiscovery();
        var interfaces = new FakeInterfaceSnapshotProvider([Eligible("eth0")]);
        var service = MakeService(dns, interfaces, new FakeNetworkChangeNotifier());
        service.Start();

        dns.Browses[0].Emit(new DiscoverySignal.Seen
        {
            PeerIdHint = new PeerIdHint("remotepeer"),
            InterfaceId = "eth0",
            ObservedAt = Epoch,
            ProtocolVersion = 1,
            Ttl = TimeSpan.FromSeconds(120),
            Endpoint = new PeerEndpoint { Address = IPAddress.Parse("192.168.1.50"), Port = 6000, InterfaceId = "eth0" },
        });

        var peer = Assert.Single(service.VisiblePeers);
        Assert.Equal(new PeerIdHint("remotepeer"), peer.PeerIdHint);
    }

    [Fact]
    public void BrowseSignal_MatchingOwnPeerIdHint_NeverAppearsInVisiblePeers()
    {
        // MakeService advertises "localpeer" as this device's own hint —
        // a browse notification carrying that same hint is this device
        // seeing its own mDNS traffic, not a real peer.
        var dns = new FakeDnsServiceDiscovery();
        var interfaces = new FakeInterfaceSnapshotProvider([Eligible("eth0")]);
        var service = MakeService(dns, interfaces, new FakeNetworkChangeNotifier());
        service.Start();

        dns.Browses[0].Emit(new DiscoverySignal.Seen
        {
            PeerIdHint = new PeerIdHint("localpeer"),
            InterfaceId = "eth0",
            ObservedAt = Epoch,
            ProtocolVersion = 1,
            Ttl = TimeSpan.FromSeconds(120),
            Endpoint = new PeerEndpoint { Address = IPAddress.Parse("192.168.1.50"), Port = 6000, InterfaceId = "eth0" },
        });

        Assert.Empty(service.VisiblePeers);
    }

    [Fact]
    public void BrowseSignal_WithdrawnMatchingOwnPeerIdHint_IsAlsoFiltered()
    {
        var dns = new FakeDnsServiceDiscovery();
        var interfaces = new FakeInterfaceSnapshotProvider([Eligible("eth0")]);
        var service = MakeService(dns, interfaces, new FakeNetworkChangeNotifier());
        service.Start();

        var raised = 0;
        service.VisiblePeersChanged += () => raised++;

        dns.Browses[0].Emit(new DiscoverySignal.Withdrawn
        {
            PeerIdHint = new PeerIdHint("localpeer"),
            InterfaceId = "eth0",
            ObservedAt = Epoch,
        });

        Assert.Equal(0, raised);
    }

    [Fact]
    public void NetworkChanged_NewInterfaceAppears_GetsRegisteredAndBrowsed()
    {
        var dns = new FakeDnsServiceDiscovery();
        var interfaces = new FakeInterfaceSnapshotProvider([Eligible("eth0")]);
        var networkChange = new FakeNetworkChangeNotifier();
        var service = MakeService(dns, interfaces, networkChange);
        service.Start();
        Assert.Single(dns.Registrations);

        interfaces.Current = [Eligible("eth0"), Eligible("wifi0")];
        networkChange.Raise();

        Assert.Equal(2, dns.Registrations.Count);
    }

    [Fact]
    public void NetworkChanged_InterfaceDisappears_TearsDownRegistrationAndDropsItsPeers()
    {
        var dns = new FakeDnsServiceDiscovery();
        var interfaces = new FakeInterfaceSnapshotProvider([Eligible("eth0"), Eligible("wifi0")]);
        var networkChange = new FakeNetworkChangeNotifier();
        var service = MakeService(dns, interfaces, networkChange);
        service.Start();

        dns.Browses.First(b => b.Interface.Id == "wifi0").Emit(new DiscoverySignal.Seen
        {
            PeerIdHint = new PeerIdHint("wifipeer"),
            InterfaceId = "wifi0",
            ObservedAt = Epoch,
            ProtocolVersion = 1,
            Ttl = TimeSpan.FromSeconds(120),
            Endpoint = new PeerEndpoint { Address = IPAddress.Parse("192.168.2.50"), Port = 6000, InterfaceId = "wifi0" },
        });
        Assert.Single(service.VisiblePeers);

        interfaces.Current = [Eligible("eth0")]; // wifi0 unplugged
        networkChange.Raise();

        Assert.Empty(service.VisiblePeers);
        Assert.True(dns.Registrations.First(r => r.Interface.Id == "wifi0").Disposed);
        Assert.True(dns.Browses.First(b => b.Interface.Id == "wifi0").Disposed);
    }

    [Fact]
    public void NetworkChanged_UnchangedEligibleSet_DoesNotReRegister()
    {
        var dns = new FakeDnsServiceDiscovery();
        var interfaces = new FakeInterfaceSnapshotProvider([Eligible("eth0")]);
        var networkChange = new FakeNetworkChangeNotifier();
        var service = MakeService(dns, interfaces, networkChange);
        service.Start();

        networkChange.Raise();

        Assert.Single(dns.Registrations); // not re-registered
    }

    [Fact]
    public void NetworkChanged_ExistingInterfaceAddressChanges_TearsDownAndReRegisters()
    {
        var dns = new FakeDnsServiceDiscovery();
        var interfaces = new FakeInterfaceSnapshotProvider([Eligible("eth0")]);
        var networkChange = new FakeNetworkChangeNotifier();
        var service = MakeService(dns, interfaces, networkChange);
        service.Start();

        // Same interface ID (DHCP renewal / roaming), different address —
        // the old registration/browse are bound to a now-stale address.
        interfaces.Current = [Eligible("eth0") with { UnicastAddresses = [IPAddress.Parse("192.168.1.99")] }];
        networkChange.Raise();

        Assert.True(dns.Registrations[0].Disposed);
        Assert.True(dns.Browses[0].Disposed);
        Assert.Equal(2, dns.Registrations.Count);
        Assert.False(dns.Registrations[1].Disposed);
        Assert.Equal("192.168.1.99", Assert.Single(dns.Registrations[1].Interface.UnicastAddresses).ToString());
    }

    [Fact]
    public void NetworkChanged_ExistingInterfaceSameAddresses_DoesNotReRegisterEvenWithANewListInstance()
    {
        var dns = new FakeDnsServiceDiscovery();
        var interfaces = new FakeInterfaceSnapshotProvider([Eligible("eth0")]);
        var networkChange = new FakeNetworkChangeNotifier();
        var service = MakeService(dns, interfaces, networkChange);
        service.Start();

        // A fresh LanInterface/list instance with identical content — must
        // not be treated as a change (record/list reference equality would
        // wrongly say otherwise).
        interfaces.Current = [Eligible("eth0") with { UnicastAddresses = [IPAddress.Parse("192.168.1.10")] }];
        networkChange.Raise();

        Assert.Single(dns.Registrations);
        Assert.False(dns.Registrations[0].Disposed);
    }

    [Fact]
    public void Start_AdvertisesTheSamePortOnEveryInterface()
    {
        var dns = new FakeDnsServiceDiscovery();
        var interfaces = new FakeInterfaceSnapshotProvider([Eligible("eth0"), Eligible("wifi0")]);
        var service = MakeService(dns, interfaces, new FakeNetworkChangeNotifier());

        service.Start();

        Assert.All(dns.Registrations, r => Assert.Equal(5001, r.Advertisement.Port));
    }

    [Fact]
    public void Dispose_TearsDownEveryActiveRegistrationAndBrowse()
    {
        var dns = new FakeDnsServiceDiscovery();
        var interfaces = new FakeInterfaceSnapshotProvider([Eligible("eth0"), Eligible("wifi0")]);
        var service = MakeService(dns, interfaces, new FakeNetworkChangeNotifier());
        service.Start();

        service.Dispose();

        Assert.All(dns.Registrations, r => Assert.True(r.Disposed));
        Assert.All(dns.Browses, b => Assert.True(b.Disposed));
    }

    [Fact]
    public void Dispose_UnsubscribesFromNetworkChanged()
    {
        var dns = new FakeDnsServiceDiscovery();
        var interfaces = new FakeInterfaceSnapshotProvider([Eligible("eth0")]);
        var networkChange = new FakeNetworkChangeNotifier();
        var service = MakeService(dns, interfaces, networkChange);
        service.Start();

        service.Dispose();
        interfaces.Current = [Eligible("eth0"), Eligible("wifi0")];
        networkChange.Raise();

        Assert.Single(dns.Registrations); // no reaction after disposal
    }

    sealed class FakeInterfaceSnapshotProvider(IReadOnlyList<LanInterface> initial) : INetworkInterfaceSnapshotProvider
    {
        public IReadOnlyList<LanInterface> Current { get; set; } = initial;
        public IReadOnlyList<LanInterface> GetCurrentInterfaces() => Current;
    }

    sealed class FakeNetworkChangeNotifier : INetworkChangeNotifier
    {
        public event Action? NetworkChanged;
        public void Raise() => NetworkChanged?.Invoke();
        public void Dispose() { }
    }

    sealed class FakeDnsServiceDiscovery : IDnsServiceDiscovery
    {
        public List<FakeRegistration> Registrations { get; } = [];
        public List<FakeBrowse> Browses { get; } = [];

        public IDisposable Register(LanInterface iface, ServiceAdvertisement advertisement)
        {
            var reg = new FakeRegistration(iface, advertisement);
            Registrations.Add(reg);
            return reg;
        }

        public IDisposable Browse(LanInterface iface, Action<DiscoverySignal> onSignal)
        {
            var browse = new FakeBrowse(iface, onSignal);
            Browses.Add(browse);
            return browse;
        }
    }

    sealed class FakeRegistration(LanInterface iface, ServiceAdvertisement advertisement) : IDisposable
    {
        public LanInterface Interface { get; } = iface;
        public ServiceAdvertisement Advertisement { get; } = advertisement;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    sealed class FakeBrowse(LanInterface iface, Action<DiscoverySignal> onSignal) : IDisposable
    {
        public LanInterface Interface { get; } = iface;
        public bool Disposed { get; private set; }
        public void Emit(DiscoverySignal signal) => onSignal(signal);
        public void Dispose() => Disposed = true;
    }
}
