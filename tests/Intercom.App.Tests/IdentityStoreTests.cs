using System.Net;
using Intercom.Identity;
using Xunit;

namespace Intercom.App.Tests;

public class IdentityStoreTests : IDisposable
{
    static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    readonly string _dir = Path.Combine(Path.GetTempPath(), "IntercomTests_" + Guid.NewGuid());

    public IdentityStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

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
        Assert.Empty(store.ApprovedPeers);
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
        first.Approve(MakePeer(3));

        File.WriteAllBytes(Path.Combine(_dir, "identity.dat"), [1, 2, 3, 4]);

        var second = new IdentityStore(_dir);
        second.LoadOrCreate();

        Assert.True(second.IdentityWasRegenerated);
        Assert.Empty(second.ApprovedPeers); // old approvals must not carry forward onto a new identity
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
        Assert.Empty(second.ApprovedPeers);
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
        var longAgo = DateTimeOffset.UtcNow - IdentityStore.PendingPairingExpiry - TimeSpan.FromMinutes(10);
        first.StartPairing(peerId, longAgo);

        var second = new IdentityStore(_dir);
        second.LoadOrCreate();

        Assert.Empty(second.PendingPairings);
        Assert.True(second.StartPairing(peerId, DateTimeOffset.UtcNow));
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

        store.Approve(MakePeer(4));

        store.LoadOrCreate(); // second call: identity now loads fine

        Assert.False(store.IdentityWasRegenerated);
        Assert.Single(store.ApprovedPeers); // must actually have loaded the registry this time
    }

    [Fact]
    public void RegistryCorruption_OnAlreadyPopulatedStore_ClearsInMemoryBeforePersisting()
    {
        // Fail-closed means fail closed even when this instance already has
        // approvals in memory from an earlier successful call — the stale
        // in-memory data must not be written back to disk as the "reset" state.
        var store = new IdentityStore(_dir);
        store.LoadOrCreate();
        store.Approve(MakePeer(5));
        Assert.Single(store.ApprovedPeers);

        File.WriteAllBytes(Path.Combine(_dir, "approved-peers.dat"), [9, 9, 9]);
        store.LoadOrCreate(); // same instance, still holding the peer in memory

        Assert.True(store.RegistryWasReset);
        Assert.Empty(store.ApprovedPeers);

        // And the file on disk must reflect the cleared state too, not the
        // stale in-memory peer that existed before this call.
        var reloaded = new IdentityStore(_dir);
        reloaded.LoadOrCreate();
        Assert.Empty(reloaded.ApprovedPeers);
    }

    [Fact]
    public void IdentityFileLost_ButRegistryFileSurvives_IsStillReportedAsRegenerated()
    {
        // Identity loss is what matters, not merely "identity.dat is
        // missing" — if the registry/pending files are still there when
        // identity.dat disappears, that's identity loss (those survivors get
        // wiped below) and callers must be warned, not told this looks like
        // a fresh install.
        var first = new IdentityStore(_dir);
        first.LoadOrCreate();
        first.Approve(MakePeer(6));

        File.Delete(Path.Combine(_dir, "identity.dat")); // only identity.dat is lost

        var second = new IdentityStore(_dir);
        second.LoadOrCreate();

        Assert.True(second.IdentityWasRegenerated);
        Assert.Empty(second.ApprovedPeers);
    }

    [Fact]
    public void FutureDatedPendingPairing_IsRejectedOnLoad_AndDoesNotBlockANewAttempt()
    {
        var first = new IdentityStore(_dir);
        first.LoadOrCreate();
        var peerId = Guid.NewGuid();
        var future = DateTimeOffset.UtcNow + TimeSpan.FromDays(1);
        first.StartPairing(peerId, future);

        var second = new IdentityStore(_dir);
        second.LoadOrCreate();

        Assert.Empty(second.PendingPairings);
        Assert.True(second.StartPairing(peerId, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void FutureDatedPendingPairing_IsRemovedFromPersistedState()
    {
        var first = new IdentityStore(_dir);
        first.LoadOrCreate();
        first.StartPairing(Guid.NewGuid(), DateTimeOffset.UtcNow + TimeSpan.FromDays(1));

        new IdentityStore(_dir).LoadOrCreate();
        var third = new IdentityStore(_dir);
        third.LoadOrCreate();

        Assert.Empty(third.PendingPairings);
    }

    [Fact]
    public void ExpiredPendingPairing_IsRemovedFromPersistedState()
    {
        var first = new IdentityStore(_dir);
        first.LoadOrCreate();
        first.StartPairing(Guid.NewGuid(), DateTimeOffset.UtcNow - IdentityStore.PendingPairingExpiry - TimeSpan.FromMinutes(1));

        new IdentityStore(_dir).LoadOrCreate();
        var third = new IdentityStore(_dir);
        third.LoadOrCreate();

        Assert.Empty(third.PendingPairings);
    }

    [Fact]
    public void FutureDatedPendingPairing_RewritesThePersistedFile()
    {
        var first = new IdentityStore(_dir);
        first.LoadOrCreate();
        first.StartPairing(Guid.NewGuid(), DateTimeOffset.UtcNow + TimeSpan.FromDays(1));
        var path = Path.Combine(_dir, "pending-pairings.dat");
        var before = File.ReadAllBytes(path);

        new IdentityStore(_dir).LoadOrCreate();

        Assert.NotEqual(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void ExpiredPendingPairing_RewritesThePersistedFile()
    {
        var first = new IdentityStore(_dir);
        first.LoadOrCreate();
        first.StartPairing(Guid.NewGuid(), DateTimeOffset.UtcNow - IdentityStore.PendingPairingExpiry - TimeSpan.FromMinutes(1));
        var path = Path.Combine(_dir, "pending-pairings.dat");
        var before = File.ReadAllBytes(path);

        new IdentityStore(_dir).LoadOrCreate();

        Assert.NotEqual(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void ValidPendingPairing_IsNotRewrittenDuringLoad()
    {
        var first = new IdentityStore(_dir);
        first.LoadOrCreate();
        first.StartPairing(Guid.NewGuid(), DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1));
        var path = Path.Combine(_dir, "pending-pairings.dat");
        var before = File.ReadAllBytes(path);

        new IdentityStore(_dir).LoadOrCreate();

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void PendingLoad_KeepsValidEntryAndRejectsFutureEntry()
    {
        var first = new IdentityStore(_dir);
        first.LoadOrCreate();
        var validPeer = Guid.NewGuid();
        first.StartPairing(validPeer, DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1));
        first.StartPairing(Guid.NewGuid(), DateTimeOffset.UtcNow + TimeSpan.FromDays(1));

        var second = new IdentityStore(_dir);
        second.LoadOrCreate();

        Assert.Collection(second.PendingPairings, pairing => Assert.Equal(validPeer, pairing.PeerId));
    }

    [Fact]
    public void LostIdentity_ClearsPopulatedInMemoryStateAndDeletesPendingFile()
    {
        var store = new IdentityStore(_dir);
        store.LoadOrCreate();
        store.Approve(MakePeer(44));
        store.StartPairing(Guid.NewGuid(), DateTimeOffset.UtcNow);
        File.Delete(Path.Combine(_dir, "identity.dat"));

        store.LoadOrCreate();

        Assert.True(store.IdentityWasRegenerated);
        Assert.Empty(store.ApprovedPeers);
        Assert.Empty(store.PendingPairings);
        Assert.False(File.Exists(Path.Combine(_dir, "pending-pairings.dat")));
    }

    [Fact]
    public void PendingPairingStartedExactlyNow_IsRetainedOnLoad()
    {
        var now = new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);
        var peerId = Guid.NewGuid();
        var first = new IdentityStore(_dir, () => now);
        first.LoadOrCreate();
        first.StartPairing(peerId, now);

        var second = new IdentityStore(_dir, () => now);
        second.LoadOrCreate();

        Assert.Equal(peerId, Assert.Single(second.PendingPairings).PeerId);
    }

    [Fact]
    public void MissingRegistry_IsNotReportedAsCorruptionWhenIdentityStillExists()
    {
        var first = new IdentityStore(_dir);
        first.LoadOrCreate();
        File.Delete(Path.Combine(_dir, "approved-peers.dat"));

        var second = new IdentityStore(_dir);
        second.LoadOrCreate();

        Assert.False(second.RegistryWasReset);
        Assert.Empty(second.ApprovedPeers);
    }

    [Fact]
    public void PendingPairingCorruption_OnAlreadyPopulatedStore_ClearsInMemoryBeforePersisting()
    {
        var store = new IdentityStore(_dir);
        store.LoadOrCreate();
        store.StartPairing(Guid.NewGuid(), DateTimeOffset.UtcNow);
        Assert.Single(store.PendingPairings);

        File.WriteAllBytes(Path.Combine(_dir, "pending-pairings.dat"), [9, 9, 9]);
        store.LoadOrCreate(); // same instance, still holding the pending request in memory

        Assert.True(store.PendingPairingsWereReset);
        Assert.Empty(store.PendingPairings);

        var reloaded = new IdentityStore(_dir);
        reloaded.LoadOrCreate();
        Assert.Empty(reloaded.PendingPairings);
    }

    // --- Atomic verb behaviour (formerly ApprovedPeerRegistryTests) ---

    [Fact]
    public void FindApprovedBySpki_FindsMatchingUnrevokedPeer()
    {
        var store = new IdentityStore(_dir);
        store.LoadOrCreate();
        var peer = store.Approve(MakePeer(5));

        var found = store.FindApprovedBySpki(Pin(5));

        Assert.NotNull(found);
        Assert.Equal(peer.PeerId, found!.PeerId);
    }

    [Fact]
    public void FindApprovedBySpki_NeverReturnsRevokedPeer()
    {
        var store = new IdentityStore(_dir);
        store.LoadOrCreate();
        var peer = store.Approve(MakePeer(5));

        store.Forget(peer.PeerId);

        Assert.Null(store.FindApprovedBySpki(Pin(5)));
    }

    [Fact]
    public void Forget_RemovesPinCertificateAndContactAssociation_NotJustAFlag()
    {
        var store = new IdentityStore(_dir);
        store.LoadOrCreate();
        var peer = store.Approve(MakePeer(5));

        var result = store.Forget(peer.PeerId);

        Assert.True(result);
        var stored = store.ApprovedPeers.Single(p => p.PeerId == peer.PeerId);
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
        var store = new IdentityStore(_dir);
        store.LoadOrCreate();
        var peer = store.Approve(MakePeer(5));

        Assert.True(store.Forget(peer.PeerId));
        Assert.False(store.Forget(peer.PeerId));
    }

    [Fact]
    public void Forget_UnknownPeerId_ReturnsFalse()
    {
        var store = new IdentityStore(_dir);
        store.LoadOrCreate();
        Assert.False(store.Forget(Guid.NewGuid()));
    }

    [Fact]
    public void Forget_Persists_SoARestartSeesTheRevocation()
    {
        var first = new IdentityStore(_dir);
        first.LoadOrCreate();
        var peer = first.Approve(MakePeer(5));
        first.Forget(peer.PeerId);

        var second = new IdentityStore(_dir);
        second.LoadOrCreate();

        var stored = second.ApprovedPeers.Single(p => p.PeerId == peer.PeerId);
        Assert.True(stored.Revoked);
    }

    // --- Atomic verb behaviour (formerly PendingPairingRegistryTests) ---

    [Fact]
    public void StartPairing_SecondRequestForSamePeer_IsBlocked()
    {
        var store = new IdentityStore(_dir);
        store.LoadOrCreate();
        var peerId = Guid.NewGuid();

        Assert.True(store.StartPairing(peerId, Epoch));
        Assert.False(store.StartPairing(peerId, Epoch.AddSeconds(1)));
    }

    [Fact]
    public void StartPairing_DifferentPeers_BothSucceed()
    {
        var store = new IdentityStore(_dir);
        store.LoadOrCreate();
        Assert.True(store.StartPairing(Guid.NewGuid(), Epoch));
        Assert.True(store.StartPairing(Guid.NewGuid(), Epoch));
    }

    [Fact]
    public void StartPairing_ExpiredExistingRequest_NeverBlocksANewAttempt()
    {
        var store = new IdentityStore(_dir);
        store.LoadOrCreate();
        var peerId = Guid.NewGuid();
        store.StartPairing(peerId, Epoch);

        // Well past the 2-minute expiry, and critically: without anyone
        // having called PruneExpiredPairings first. StartPairing must prune
        // it itself.
        var muchLater = Epoch + IdentityStore.PendingPairingExpiry + TimeSpan.FromMinutes(10);

        Assert.True(store.StartPairing(peerId, muchLater));
    }

    [Fact]
    public void PruneExpiredPairings_DropsOnlyExpiredEntries()
    {
        var store = new IdentityStore(_dir);
        store.LoadOrCreate();
        var freshPeer = Guid.NewGuid();
        var stalePeer = Guid.NewGuid();
        store.StartPairing(stalePeer, Epoch);
        store.StartPairing(freshPeer, Epoch.AddMinutes(1));

        var checkTime = Epoch + IdentityStore.PendingPairingExpiry + TimeSpan.FromSeconds(1);
        var removed = store.PruneExpiredPairings(checkTime);

        Assert.Equal(1, removed);
        Assert.Single(store.PendingPairings);
        Assert.Equal(freshPeer, store.PendingPairings[0].PeerId);
    }

    [Fact]
    public void CompletePairing_RemovesTheRequest()
    {
        var store = new IdentityStore(_dir);
        store.LoadOrCreate();
        var peerId = Guid.NewGuid();
        store.StartPairing(peerId, Epoch);

        Assert.True(store.CompletePairing(peerId));
        Assert.Empty(store.PendingPairings);
    }

    [Fact]
    public void CompletePairing_UnknownPeerId_ReturnsFalse()
    {
        var store = new IdentityStore(_dir);
        store.LoadOrCreate();
        Assert.False(store.CompletePairing(Guid.NewGuid()));
    }

    [Fact]
    public void CompletePairing_Persists_SoARestartDoesNotSeeAStaleRequest()
    {
        var first = new IdentityStore(_dir);
        first.LoadOrCreate();
        var peerId = Guid.NewGuid();
        first.StartPairing(peerId, DateTimeOffset.UtcNow);
        first.CompletePairing(peerId);

        var second = new IdentityStore(_dir);
        second.LoadOrCreate();

        Assert.Empty(second.PendingPairings);
    }

    [Fact]
    public void IsExpired_AtExactlyTheTimeout_CountsAsExpired()
    {
        var pairing = new PendingPairing { PeerId = Guid.NewGuid(), StartedAt = Epoch };
        var exactlyAtTimeout = Epoch + IdentityStore.PendingPairingExpiry;

        Assert.True(pairing.IsExpired(IdentityStore.PendingPairingExpiry, exactlyAtTimeout));
    }

    [Fact]
    public void IsExpired_JustBeforeTheTimeout_IsNotExpired()
    {
        var pairing = new PendingPairing { PeerId = Guid.NewGuid(), StartedAt = Epoch };
        var justBefore = Epoch + IdentityStore.PendingPairingExpiry - TimeSpan.FromSeconds(1);

        Assert.False(pairing.IsExpired(IdentityStore.PendingPairingExpiry, justBefore));
    }

    [Fact]
    public void UpdateLastKnownEndpoint_ApprovedPeer_PersistsTrimmedHostname()
    {
        var store = new IdentityStore(_dir);
        store.LoadOrCreate();
        var peer = store.Approve(MakePeer(42));

        Assert.True(store.UpdateLastKnownEndpoint(peer.PeerId, " vpn.example ", 47811));

        var reloaded = new IdentityStore(_dir);
        reloaded.LoadOrCreate();
        Assert.Equal("vpn.example", Assert.Single(reloaded.ApprovedPeers).LastKnownAddress);
        Assert.Equal(47811, Assert.Single(reloaded.ApprovedPeers).LastKnownPort);
    }

    [Fact]
    public void UpdateLastKnownEndpoint_IpAddress_UsesAddressText()
    {
        var store = new IdentityStore(_dir);
        store.LoadOrCreate();
        var peer = store.Approve(MakePeer(43));

        Assert.True(store.UpdateLastKnownEndpoint(peer.PeerId, IPAddress.Loopback, 47811));

        Assert.Equal("127.0.0.1", Assert.Single(store.ApprovedPeers).LastKnownAddress);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void UpdateLastKnownEndpoint_BlankHostname_IsRejected(string host)
    {
        var store = new IdentityStore(_dir);
        Assert.Throws<ArgumentException>(() => store.UpdateLastKnownEndpoint(Guid.NewGuid(), host, 47811));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void UpdateLastKnownEndpoint_InvalidPort_IsRejected(int port)
    {
        var store = new IdentityStore(_dir);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            store.UpdateLastKnownEndpoint(Guid.NewGuid(), "host", port));
    }

    [Fact]
    public void UpdateLastKnownEndpoint_UnknownOrRevokedPeer_IsRejected()
    {
        var store = new IdentityStore(_dir);
        store.LoadOrCreate();
        var peer = store.Approve(MakePeer(44));
        Assert.True(store.Forget(peer.PeerId));

        Assert.False(store.UpdateLastKnownEndpoint(peer.PeerId, "host", 47811));
        Assert.False(store.UpdateLastKnownEndpoint(Guid.NewGuid(), "host", 47811));
    }
}
