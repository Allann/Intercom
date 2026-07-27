using System.Net;
using Intercom.Discovery;
using Xunit;

namespace Intercom.App.Tests.Discovery;

public class VisiblePeerListTests
{
    static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    static PeerIdHint Hint(string s = "abc123") => new(s);

    static DiscoverySignal.Seen SeenSignal(
        string interfaceId = "eth0",
        PeerIdHint? hint = null,
        int port = 5000,
        int ttlSeconds = 120,
        DateTimeOffset? at = null,
        string address = "192.168.1.20") => new()
    {
        PeerIdHint = hint ?? Hint(),
        InterfaceId = interfaceId,
        ObservedAt = at ?? Epoch,
        ProtocolVersion = 1,
        Ttl = TimeSpan.FromSeconds(ttlSeconds),
        Endpoint = new PeerEndpoint { Address = IPAddress.Parse(address), Port = port, InterfaceId = interfaceId },
    };

    [Fact]
    public void Observe_Seen_AddsPeerToVisibleList()
    {
        var list = new VisiblePeerList();

        list.Observe(SeenSignal(), Epoch);

        var peer = Assert.Single(list.Peers);
        Assert.Equal(Hint(), peer.PeerIdHint);
        Assert.Single(peer.Endpoints);
    }

    [Fact]
    public void Observe_Seen_RaisesChanged()
    {
        var list = new VisiblePeerList();
        var raised = 0;
        list.Changed += () => raised++;

        list.Observe(SeenSignal(), Epoch);

        Assert.Equal(1, raised);
    }

    [Fact]
    public void Observe_SamePeerSameInterfaceTwice_DedupsIntoOneEntry()
    {
        var list = new VisiblePeerList();

        list.Observe(SeenSignal(at: Epoch), Epoch);
        list.Observe(SeenSignal(at: Epoch.AddSeconds(10)), Epoch.AddSeconds(10));

        var peer = Assert.Single(list.Peers);
        Assert.Single(peer.Endpoints);
        Assert.Equal(Epoch, peer.FirstSeenAt); // first sighting preserved
        Assert.Equal(Epoch.AddSeconds(10), peer.LastSeenAt); // refreshed
    }

    [Fact]
    public void Observe_SamePeerDifferentInterfaces_MergesEndpointsIntoOnePeer()
    {
        var list = new VisiblePeerList();

        list.Observe(SeenSignal(interfaceId: "eth0", address: "192.168.1.20"), Epoch);
        list.Observe(SeenSignal(interfaceId: "wifi0", address: "192.168.2.20"), Epoch);

        var peer = Assert.Single(list.Peers);
        Assert.Equal(2, peer.Endpoints.Count);
    }

    [Fact]
    public void Observe_Withdrawn_RemovesThatInterfacesSighting_ButKeepsPeerIfOtherInterfaceStillLive()
    {
        var list = new VisiblePeerList();
        list.Observe(SeenSignal(interfaceId: "eth0"), Epoch);
        list.Observe(SeenSignal(interfaceId: "wifi0"), Epoch);

        list.Observe(new DiscoverySignal.Withdrawn { PeerIdHint = Hint(), InterfaceId = "eth0", ObservedAt = Epoch }, Epoch);

        var peer = Assert.Single(list.Peers);
        Assert.Single(peer.Endpoints);
        Assert.Equal("wifi0", peer.Endpoints[0].InterfaceId);
    }

    [Fact]
    public void Observe_WithdrawnLastInterface_RemovesPeerEntirely()
    {
        var list = new VisiblePeerList();
        list.Observe(SeenSignal(), Epoch);

        list.Observe(new DiscoverySignal.Withdrawn { PeerIdHint = Hint(), InterfaceId = "eth0", ObservedAt = Epoch }, Epoch);

        Assert.Empty(list.Peers);
    }

    [Fact]
    public void Observe_WithdrawnUnknownSighting_DoesNotRaiseChanged()
    {
        var list = new VisiblePeerList();
        var raised = 0;
        list.Changed += () => raised++;

        list.Observe(new DiscoverySignal.Withdrawn { PeerIdHint = Hint(), InterfaceId = "eth0", ObservedAt = Epoch }, Epoch);

        Assert.Equal(0, raised);
    }

    [Fact]
    public void EvaluateExpiry_PastTtl_RemovesTheSighting()
    {
        var list = new VisiblePeerList();
        list.Observe(SeenSignal(ttlSeconds: 30, at: Epoch), Epoch);

        var removed = list.EvaluateExpiry(Epoch.AddSeconds(31));

        Assert.Equal(1, removed);
        Assert.Empty(list.Peers);
    }

    [Fact]
    public void EvaluateExpiry_BeforeTtl_KeepsTheSighting()
    {
        var list = new VisiblePeerList();
        list.Observe(SeenSignal(ttlSeconds: 30, at: Epoch), Epoch);

        var removed = list.EvaluateExpiry(Epoch.AddSeconds(29));

        Assert.Equal(0, removed);
        Assert.Single(list.Peers);
    }

    [Fact]
    public void EvaluateExpiry_OnlyOneOfTwoInterfacesExpired_KeepsPeerWithRemainingEndpoint()
    {
        var list = new VisiblePeerList();
        list.Observe(SeenSignal(interfaceId: "eth0", ttlSeconds: 10, at: Epoch), Epoch);
        list.Observe(SeenSignal(interfaceId: "wifi0", ttlSeconds: 100, at: Epoch), Epoch);

        list.EvaluateExpiry(Epoch.AddSeconds(11));

        var peer = Assert.Single(list.Peers);
        Assert.Single(peer.Endpoints);
        Assert.Equal("wifi0", peer.Endpoints[0].InterfaceId);
    }

    [Fact]
    public void RemoveAllForInterface_DropsOnlySightingsOnThatInterface()
    {
        var list = new VisiblePeerList();
        list.Observe(SeenSignal(interfaceId: "eth0", hint: Hint("peerA")), Epoch);
        list.Observe(SeenSignal(interfaceId: "wifi0", hint: Hint("peerB")), Epoch);

        var removed = list.RemoveAllForInterface("eth0");

        Assert.Equal(1, removed);
        var peer = Assert.Single(list.Peers);
        Assert.Equal(Hint("peerB"), peer.PeerIdHint);
    }

    [Fact]
    public void DistinctPeers_AreNeverMergedByCoincidence()
    {
        var list = new VisiblePeerList();
        list.Observe(SeenSignal(hint: Hint("peerA")), Epoch);
        list.Observe(SeenSignal(hint: Hint("peerB")), Epoch);

        Assert.Equal(2, list.Peers.Count);
    }

    [Fact]
    public void Observe_SamePeerSameInterfaceBothAddressFamilies_KeepsBothEndpoints()
    {
        var list = new VisiblePeerList();

        list.Observe(SeenSignal(interfaceId: "eth0", address: "192.168.1.20"), Epoch);
        list.Observe(SeenSignal(interfaceId: "eth0", address: "fe80::1"), Epoch);

        var peer = Assert.Single(list.Peers);
        Assert.Equal(2, peer.Endpoints.Count);
    }

    [Fact]
    public void Observe_Withdrawn_RemovesBothAddressFamiliesOnThatInterface()
    {
        var list = new VisiblePeerList();
        list.Observe(SeenSignal(interfaceId: "eth0", address: "192.168.1.20"), Epoch);
        list.Observe(SeenSignal(interfaceId: "eth0", address: "fe80::1"), Epoch);

        list.Observe(new DiscoverySignal.Withdrawn { PeerIdHint = Hint(), InterfaceId = "eth0", ObservedAt = Epoch }, Epoch);

        Assert.Empty(list.Peers);
    }

    [Fact]
    public void Observe_Seen_EndpointInterfaceIdMismatchWithSignal_IsNormalizedToSignalsInterfaceId()
    {
        var list = new VisiblePeerList();
        var mismatched = new DiscoverySignal.Seen
        {
            PeerIdHint = Hint(),
            InterfaceId = "eth0",
            ObservedAt = Epoch,
            ProtocolVersion = 1,
            Ttl = TimeSpan.FromSeconds(120),
            // Deliberately disagrees with the signal's own InterfaceId.
            Endpoint = new PeerEndpoint { Address = IPAddress.Parse("192.168.1.20"), Port = 5000, InterfaceId = "wifi0" },
        };

        list.Observe(mismatched, Epoch);

        var peer = Assert.Single(list.Peers);
        var endpoint = Assert.Single(peer.Endpoints);
        Assert.Equal("eth0", endpoint.InterfaceId);
    }
}
