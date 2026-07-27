using Intercom.Identity;
using Xunit;

namespace Intercom.App.Tests;

public class PendingPairingRegistryTests
{
    static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TryStart_SecondRequestForSamePeer_IsBlocked()
    {
        var registry = new PendingPairingRegistry();
        var peerId = Guid.NewGuid();

        Assert.True(registry.TryStart(peerId, Epoch));
        Assert.False(registry.TryStart(peerId, Epoch.AddSeconds(1)));
    }

    [Fact]
    public void TryStart_DifferentPeers_BothSucceed()
    {
        var registry = new PendingPairingRegistry();
        Assert.True(registry.TryStart(Guid.NewGuid(), Epoch));
        Assert.True(registry.TryStart(Guid.NewGuid(), Epoch));
    }

    [Fact]
    public void TryStart_ExpiredExistingRequest_NeverBlocksANewAttempt()
    {
        var registry = new PendingPairingRegistry();
        var peerId = Guid.NewGuid();
        registry.TryStart(peerId, Epoch);

        // Well past the 2-minute expiry, and critically: without anyone
        // having called RemoveExpired first. TryStart must prune it itself.
        var muchLater = Epoch + PendingPairingRegistry.ExpiryTimeout + TimeSpan.FromMinutes(10);

        Assert.True(registry.TryStart(peerId, muchLater));
    }

    [Fact]
    public void IsExpired_AtExactlyTheTimeout_CountsAsExpired()
    {
        var pairing = new PendingPairing { PeerId = Guid.NewGuid(), StartedAt = Epoch };
        var exactlyAtTimeout = Epoch + PendingPairingRegistry.ExpiryTimeout;

        Assert.True(pairing.IsExpired(PendingPairingRegistry.ExpiryTimeout, exactlyAtTimeout));
    }

    [Fact]
    public void IsExpired_JustBeforeTheTimeout_IsNotExpired()
    {
        var pairing = new PendingPairing { PeerId = Guid.NewGuid(), StartedAt = Epoch };
        var justBefore = Epoch + PendingPairingRegistry.ExpiryTimeout - TimeSpan.FromSeconds(1);

        Assert.False(pairing.IsExpired(PendingPairingRegistry.ExpiryTimeout, justBefore));
    }

    [Fact]
    public void RemoveExpired_DropsOnlyExpiredEntries()
    {
        var registry = new PendingPairingRegistry();
        var freshPeer = Guid.NewGuid();
        var stalePeer = Guid.NewGuid();
        registry.TryStart(stalePeer, Epoch);
        registry.TryStart(freshPeer, Epoch.AddMinutes(1));

        var checkTime = Epoch + PendingPairingRegistry.ExpiryTimeout + TimeSpan.FromSeconds(1);
        var removed = registry.RemoveExpired(checkTime);

        Assert.Equal(1, removed);
        Assert.Single(registry.Pending);
        Assert.Equal(freshPeer, registry.Pending[0].PeerId);
    }

    [Fact]
    public void Complete_RemovesTheRequest()
    {
        var registry = new PendingPairingRegistry();
        var peerId = Guid.NewGuid();
        registry.TryStart(peerId, Epoch);

        Assert.True(registry.Complete(peerId));
        Assert.Empty(registry.Pending);
    }

    [Fact]
    public void ReplaceAll_DeduplicatesByPeerId_KeepingTheMostRecent()
    {
        // Deserialized (e.g. loaded from disk) data is untrusted input and
        // could contain duplicates that should never have been persisted —
        // ADR-0002 permits only one outstanding request per peer.
        var peerId = Guid.NewGuid();
        var registry = new PendingPairingRegistry();

        registry.ReplaceAll([
            new PendingPairing { PeerId = peerId, StartedAt = Epoch },
            new PendingPairing { PeerId = peerId, StartedAt = Epoch.AddSeconds(30) },
        ]);

        Assert.Single(registry.Pending);
        Assert.Equal(Epoch.AddSeconds(30), registry.Pending[0].StartedAt);
    }
}
