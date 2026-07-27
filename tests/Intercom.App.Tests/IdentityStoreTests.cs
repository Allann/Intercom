using Intercom.Identity;
using Xunit;

namespace Intercom.App.Tests;

public class IdentityStoreTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "IntercomTests_" + Guid.NewGuid());

    public IdentityStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void FreshDirectory_CreatesOneIdentity_AndAnEmptyRegistryFile()
    {
        var store = new IdentityStore(_dir);
        store.LoadOrCreate();

        // IdentityWasRegenerated means "a previously-existing identity was
        // lost" (the signal the app uses to warn about needing re-pairing) —
        // a genuinely fresh install has nothing to lose, so this is false.
        Assert.False(store.IdentityWasRegenerated);
        Assert.NotEqual(Guid.Empty, store.Identity.PeerId);
        Assert.True(File.Exists(Path.Combine(_dir, "identity.dat")));
        Assert.True(File.Exists(Path.Combine(_dir, "approved-peers.dat")));
        Assert.Empty(store.Registry.Peers);
    }

    [Fact]
    public void SecondLoad_ReusesTheSameIdentity_NotRegenerated()
    {
        var first = new IdentityStore(_dir);
        first.LoadOrCreate();
        var originalPeerId = first.Identity.PeerId;

        var second = new IdentityStore(_dir);
        second.LoadOrCreate();

        Assert.False(second.IdentityWasRegenerated);
        Assert.Equal(originalPeerId, second.Identity.PeerId);
    }

    [Fact]
    public void CorruptIdentityFile_RegeneratesIdentity_AndWipesApprovedPeers()
    {
        var first = new IdentityStore(_dir);
        first.LoadOrCreate();
        var peer = new ApprovedPeer
        {
            PeerId = Guid.NewGuid(),
            FriendlyName = "Someone",
            SpkiSha256 = new SpkiPin(Enumerable.Repeat((byte)3, 32).ToArray()),
            Certificate = [1, 2, 3],
            ApprovedAt = DateTimeOffset.UtcNow,
        };
        first.Registry.Add(peer);
        first.SaveRegistry();

        File.WriteAllBytes(Path.Combine(_dir, "identity.dat"), [1, 2, 3, 4]);

        var second = new IdentityStore(_dir);
        second.LoadOrCreate();

        Assert.True(second.IdentityWasRegenerated);
        Assert.Empty(second.Registry.Peers); // old approvals must not carry forward onto a new identity
    }

    [Fact]
    public void CorruptRegistryOnly_ResetsRegistry_ButLeavesIdentityUntouched()
    {
        var first = new IdentityStore(_dir);
        first.LoadOrCreate();
        var originalPeerId = first.Identity.PeerId;

        File.WriteAllBytes(Path.Combine(_dir, "approved-peers.dat"), [9, 9, 9]);

        var second = new IdentityStore(_dir);
        second.LoadOrCreate();

        Assert.False(second.IdentityWasRegenerated);
        Assert.Equal(originalPeerId, second.Identity.PeerId);
        Assert.True(second.RegistryWasReset);
        Assert.Empty(second.Registry.Peers);
    }

    [Fact]
    public void CorruptRegistry_IsReplacedOnDisk_SoItDoesNotFailAgainNextLaunch()
    {
        var first = new IdentityStore(_dir);
        first.LoadOrCreate();
        File.WriteAllBytes(Path.Combine(_dir, "approved-peers.dat"), [9, 9, 9]);

        var second = new IdentityStore(_dir);
        second.LoadOrCreate();
        Assert.True(second.RegistryWasReset);

        var third = new IdentityStore(_dir);
        third.LoadOrCreate();

        Assert.False(third.RegistryWasReset); // the corrupt file was replaced, not left behind
    }

    [Fact]
    public void ExpiredPendingPairing_IsPrunedOnLoad_AndDoesNotBlockANewAttempt()
    {
        var first = new IdentityStore(_dir);
        first.LoadOrCreate();
        var peerId = Guid.NewGuid();
        var longAgo = DateTimeOffset.UtcNow - PendingPairingRegistry.ExpiryTimeout - TimeSpan.FromMinutes(10);
        first.PendingPairings.TryStart(peerId, longAgo);
        first.SavePendingPairings();

        var second = new IdentityStore(_dir);
        second.LoadOrCreate();

        Assert.Empty(second.PendingPairings.Pending);
        Assert.True(second.PendingPairings.TryStart(peerId, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void SecondCallOnSameInstance_AfterEarlierRegeneration_StillLoadsRegistry()
    {
        // Guards against sticky recovery flags: IdentityWasRegenerated must
        // reset each call, or a call after a genuinely fine load would skip
        // registry/pending loading forever because of a PAST regeneration.
        var store = new IdentityStore(_dir);
        File.WriteAllBytes(Path.Combine(_dir, "identity.dat"), [1, 2, 3]); // force regeneration on first call
        store.LoadOrCreate();
        Assert.True(store.IdentityWasRegenerated);

        var peer = new ApprovedPeer
        {
            PeerId = Guid.NewGuid(),
            FriendlyName = "Someone",
            SpkiSha256 = new SpkiPin(Enumerable.Repeat((byte)4, 32).ToArray()),
            Certificate = [1, 2, 3],
            ApprovedAt = DateTimeOffset.UtcNow,
        };
        store.Registry.Add(peer);
        store.SaveRegistry();

        store.LoadOrCreate(); // second call: identity now loads fine

        Assert.False(store.IdentityWasRegenerated);
        Assert.Single(store.Registry.Peers); // must actually have loaded the registry this time
    }

    [Fact]
    public void RegistryCorruption_OnAlreadyPopulatedStore_ClearsInMemoryBeforePersisting()
    {
        // Fail-closed means fail closed even when this instance already has
        // approvals in memory from an earlier successful call — the stale
        // in-memory data must not be written back to disk as the "reset" state.
        var store = new IdentityStore(_dir);
        store.LoadOrCreate();
        var peer = new ApprovedPeer
        {
            PeerId = Guid.NewGuid(),
            FriendlyName = "Someone",
            SpkiSha256 = new SpkiPin(Enumerable.Repeat((byte)5, 32).ToArray()),
            Certificate = [1, 2, 3],
            ApprovedAt = DateTimeOffset.UtcNow,
        };
        store.Registry.Add(peer);
        store.SaveRegistry();
        Assert.Single(store.Registry.Peers);

        File.WriteAllBytes(Path.Combine(_dir, "approved-peers.dat"), [9, 9, 9]);
        store.LoadOrCreate(); // same instance, still holding the peer in memory

        Assert.True(store.RegistryWasReset);
        Assert.Empty(store.Registry.Peers);

        // And the file on disk must reflect the cleared state too, not the
        // stale in-memory peer that existed before this call.
        var reloaded = new IdentityStore(_dir);
        reloaded.LoadOrCreate();
        Assert.Empty(reloaded.Registry.Peers);
    }

    [Fact]
    public void PendingPairingCorruption_OnAlreadyPopulatedStore_ClearsInMemoryBeforePersisting()
    {
        var store = new IdentityStore(_dir);
        store.LoadOrCreate();
        store.PendingPairings.TryStart(Guid.NewGuid(), DateTimeOffset.UtcNow);
        store.SavePendingPairings();
        Assert.Single(store.PendingPairings.Pending);

        File.WriteAllBytes(Path.Combine(_dir, "pending-pairings.dat"), [9, 9, 9]);
        store.LoadOrCreate(); // same instance, still holding the pending request in memory

        Assert.True(store.PendingPairingsWereReset);
        Assert.Empty(store.PendingPairings.Pending);

        var reloaded = new IdentityStore(_dir);
        reloaded.LoadOrCreate();
        Assert.Empty(reloaded.PendingPairings.Pending);
    }
}
