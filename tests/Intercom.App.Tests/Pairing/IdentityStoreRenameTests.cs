using Intercom.Identity;
using Xunit;

namespace Intercom.App.Tests.Pairing;

/// <summary>Tests for <see cref="IdentityStore.Rename"/>, added by issue #22
/// for the Rolodex's rename action. <c>Forget</c>/revocation itself was
/// already covered by <c>IdentityStoreTests</c> before this ticket; these
/// tests cover only the new surface.</summary>
public class IdentityStoreRenameTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "IntercomPairingTests_" + Guid.NewGuid());

    public IdentityStoreRenameTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    static SpkiPin Pin(byte seed) => new(Enumerable.Repeat(seed, 32).ToArray());

    static ApprovedPeer MakePeer(byte pinSeed, string name = "Old Name") => new()
    {
        PeerId = Guid.NewGuid(),
        FriendlyName = name,
        SpkiSha256 = Pin(pinSeed),
        Certificate = [1, 2, 3],
        ApprovedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void Rename_UpdatesFriendlyName()
    {
        var store = new IdentityStore(_dir);
        store.LoadOrCreate();
        var peer = store.Approve(MakePeer(1));

        var result = store.Rename(peer.PeerId, "New Name");

        Assert.True(result);
        Assert.Equal("New Name", store.ApprovedPeers.Single().FriendlyName);
    }

    [Fact]
    public void Rename_UnknownPeerId_ReturnsFalse()
    {
        var store = new IdentityStore(_dir);
        store.LoadOrCreate();

        var result = store.Rename(Guid.NewGuid(), "New Name");

        Assert.False(result);
    }

    [Fact]
    public void Rename_NeverChangesSpkiPinOrApprovalState()
    {
        var store = new IdentityStore(_dir);
        store.LoadOrCreate();
        var peer = store.Approve(MakePeer(1));
        var originalPin = peer.SpkiSha256;

        store.Rename(peer.PeerId, "New Name");

        var renamed = store.ApprovedPeers.Single();
        Assert.Equal(originalPin, renamed.SpkiSha256);
        Assert.False(renamed.Revoked);
    }

    [Fact]
    public void Rename_Persists_SoARestartSeesTheNewName()
    {
        var first = new IdentityStore(_dir);
        first.LoadOrCreate();
        var peer = first.Approve(MakePeer(1));
        first.Rename(peer.PeerId, "New Name");

        var second = new IdentityStore(_dir);
        second.LoadOrCreate();

        Assert.Equal("New Name", second.ApprovedPeers.Single().FriendlyName);
    }

    [Fact]
    public void Rename_WorksEvenOnARevokedPeer_ForAuditTraceReadability()
    {
        var store = new IdentityStore(_dir);
        store.LoadOrCreate();
        var peer = store.Approve(MakePeer(1));
        store.Forget(peer.PeerId);

        var result = store.Rename(peer.PeerId, "Renamed After Forget");

        Assert.True(result);
        Assert.Equal("Renamed After Forget", store.ApprovedPeers.Single().FriendlyName);
        Assert.True(store.ApprovedPeers.Single().Revoked);
    }
}
