using Intercom.Identity;
using Xunit;

namespace Intercom.App.Tests;

public class ApprovedPeerRegistryTests
{
    static SpkiPin Pin(byte seed) => new(Enumerable.Repeat(seed, 32).ToArray());

    static ApprovedPeer MakePeer(byte pinSeed, string name = "Test Peer") => new()
    {
        PeerId = Guid.NewGuid(),
        FriendlyName = name,
        SpkiSha256 = Pin(pinSeed),
        Certificate = [1, 2, 3],
        ApprovedAt = DateTimeOffset.UtcNow,
        ContactId = "contact-1",
    };

    [Fact]
    public void FindApprovedBySpki_FindsMatchingUnrevokedPeer()
    {
        var registry = new ApprovedPeerRegistry();
        var peer = MakePeer(5);
        registry.Add(peer);

        var found = registry.FindApprovedBySpki(Pin(5));

        Assert.NotNull(found);
        Assert.Equal(peer.PeerId, found!.PeerId);
    }

    [Fact]
    public void FindApprovedBySpki_NeverReturnsRevokedPeer()
    {
        var registry = new ApprovedPeerRegistry();
        var peer = MakePeer(5);
        registry.Add(peer);

        registry.Forget(peer.PeerId);

        Assert.Null(registry.FindApprovedBySpki(Pin(5)));
    }

    [Fact]
    public void Forget_RemovesPinCertificateAndContactAssociation_NotJustAFlag()
    {
        var registry = new ApprovedPeerRegistry();
        var peer = MakePeer(5);
        registry.Add(peer);

        var result = registry.Forget(peer.PeerId);

        Assert.True(result);
        var stored = registry.Peers.Single(p => p.PeerId == peer.PeerId);
        Assert.True(stored.Revoked);
        Assert.Null(stored.SpkiSha256);
        Assert.Null(stored.Certificate);
        Assert.Null(stored.ContactId);
        // The record itself is retained as a local audit trace, per ADR-0002.
        Assert.Equal(peer.FriendlyName, stored.FriendlyName);
    }

    [Fact]
    public void Forget_IsIdempotent_SecondCallReturnsFalse()
    {
        var registry = new ApprovedPeerRegistry();
        var peer = MakePeer(5);
        registry.Add(peer);

        Assert.True(registry.Forget(peer.PeerId));
        Assert.False(registry.Forget(peer.PeerId));
    }

    [Fact]
    public void Forget_UnknownPeerId_ReturnsFalse()
    {
        var registry = new ApprovedPeerRegistry();
        Assert.False(registry.Forget(Guid.NewGuid()));
    }
}
