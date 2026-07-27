using Intercom.Identity;
using Intercom.Pairing;
using Xunit;

namespace Intercom.App.Tests.Pairing;

public class PairingSessionManagerTests : IDisposable
{
    static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    readonly string _dir = Path.Combine(Path.GetTempPath(), "IntercomPairingTests_" + Guid.NewGuid());

    public PairingSessionManagerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    static SpkiPin Pin(byte seed) => new(Enumerable.Repeat(seed, 32).ToArray());

    IdentityStore MakeStore()
    {
        var store = new IdentityStore(_dir);
        store.LoadOrCreate();
        return store;
    }

    [Fact]
    public void TryStartSession_FirstAttempt_Succeeds()
    {
        var manager = new PairingSessionManager(MakeStore());
        var remotePeerId = Guid.NewGuid();

        var session = manager.TryStartSession(
            "source-a", new FakePairingTransport(), Pin(1), Guid.NewGuid(), Pin(2), remotePeerId, [1], Epoch);

        Assert.NotNull(session);
        Assert.Single(manager.Sessions);
    }

    [Fact]
    public void TryStartSession_SecondAttemptSamePeer_Rejected_OneOutstandingPerPeer()
    {
        var manager = new PairingSessionManager(MakeStore());
        var remotePeerId = Guid.NewGuid();
        manager.TryStartSession("source-a", new FakePairingTransport(), Pin(1), Guid.NewGuid(), Pin(2), remotePeerId, [1], Epoch);

        var second = manager.TryStartSession("source-a", new FakePairingTransport(), Pin(1), Guid.NewGuid(), Pin(2), remotePeerId, [1], Epoch.AddSeconds(1));

        Assert.Null(second);
        Assert.Single(manager.Sessions);
    }

    [Fact]
    public void TryStartSession_SixthFromSameSource_Rejected_ByRateLimiter()
    {
        var manager = new PairingSessionManager(MakeStore());
        for (var i = 0; i < PairingRateLimiter.MaxAttemptsPerSource; i++)
        {
            var session = manager.TryStartSession(
                "source-a", new FakePairingTransport(), Pin(1), Guid.NewGuid(), Pin(2), Guid.NewGuid(), [1], Epoch.AddSeconds(i));
            Assert.NotNull(session);
        }

        var sixth = manager.TryStartSession(
            "source-a", new FakePairingTransport(), Pin(1), Guid.NewGuid(), Pin(2), Guid.NewGuid(), [1], Epoch.AddSeconds(10));

        Assert.Null(sixth);
    }

    [Fact]
    public void TryStartSession_RateLimited_RollsBackThePendingPairingSlot()
    {
        // A rate-limited attempt must not permanently consume the
        // one-outstanding-per-peer slot for a peer it never actually
        // started ceremony traffic with.
        var store = MakeStore();
        var manager = new PairingSessionManager(store);
        for (var i = 0; i < PairingRateLimiter.MaxAttemptsPerSource; i++)
        {
            manager.TryStartSession("source-a", new FakePairingTransport(), Pin(1), Guid.NewGuid(), Pin(2), Guid.NewGuid(), [1], Epoch.AddSeconds(i));
        }

        var blockedPeerId = Guid.NewGuid();
        var blocked = manager.TryStartSession("source-a", new FakePairingTransport(), Pin(1), Guid.NewGuid(), Pin(2), blockedPeerId, [1], Epoch.AddSeconds(10));
        Assert.Null(blocked);

        // The peer that was rejected purely due to rate limiting must not
        // itself still be "outstanding" in IdentityStore.
        Assert.DoesNotContain(store.PendingPairings, p => p.PeerId == blockedPeerId);
    }

    [Fact]
    public async Task Cancel_RetiresTheSession()
    {
        var manager = new PairingSessionManager(MakeStore());
        var remotePeerId = Guid.NewGuid();
        var session = manager.TryStartSession(
            "source-a", new FakePairingTransport(), Pin(1), Guid.NewGuid(), Pin(2), remotePeerId, [1], Epoch)!;
        await session.StartAsync(CancellationToken.None);

        manager.Cancel(remotePeerId);

        Assert.Empty(manager.Sessions);
        Assert.Equal(PairingCeremonyPhase.Cancelled, session.Phase);
    }

    [Fact]
    public void Tick_ExpiresStaleSessions_AndRetiresThem()
    {
        var manager = new PairingSessionManager(MakeStore());
        var remotePeerId = Guid.NewGuid();
        manager.TryStartSession("source-a", new FakePairingTransport(), Pin(1), Guid.NewGuid(), Pin(2), remotePeerId, [1], Epoch);

        manager.Tick(Epoch + IdentityStore.PendingPairingExpiry + TimeSpan.FromSeconds(1));

        Assert.Empty(manager.Sessions);
    }
}
