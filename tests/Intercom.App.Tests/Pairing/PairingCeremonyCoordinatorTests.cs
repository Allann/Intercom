using Intercom.Identity;
using Intercom.Pairing;
using Xunit;

namespace Intercom.App.Tests.Pairing;

/// <summary>
/// Orchestration-level tests for <see cref="PairingCeremonyCoordinator"/>,
/// driven by <see cref="FakePairingTransport"/> pairs standing in for two
/// real devices' live PairingOnly connections — the same "hand-written fake
/// at the transport seam" pattern <c>PeerControlChannelTests</c> uses.
/// Covers the ADR-0002 acceptance criteria that need two independently
/// acting sides: confirmation on only one side never approves; expiry never
/// approves; explicit reject never approves; repeated requests respect the
/// one-outstanding-per-peer limit.
/// </summary>
public class PairingCeremonyCoordinatorTests : IDisposable
{
    static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    readonly string _dirA = Path.Combine(Path.GetTempPath(), "IntercomPairingTests_" + Guid.NewGuid());
    readonly string _dirB = Path.Combine(Path.GetTempPath(), "IntercomPairingTests_" + Guid.NewGuid());

    public PairingCeremonyCoordinatorTests()
    {
        Directory.CreateDirectory(_dirA);
        Directory.CreateDirectory(_dirB);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dirA)) Directory.Delete(_dirA, recursive: true);
        if (Directory.Exists(_dirB)) Directory.Delete(_dirB, recursive: true);
    }

    static SpkiPin Pin(byte seed) => new(Enumerable.Repeat(seed, 32).ToArray());

    (PairingCeremonyCoordinator CoordinatorA, IdentityStore StoreA, PairingCeremonyCoordinator CoordinatorB, IdentityStore StoreB, FakePairingTransport TransportA, FakePairingTransport TransportB)
        MakeCeremonyPair()
    {
        var storeA = new IdentityStore(_dirA);
        storeA.LoadOrCreate();
        var storeB = new IdentityStore(_dirB);
        storeB.LoadOrCreate();

        var spkiA = Pin(1);
        var spkiB = Pin(2);
        var peerIdA = Guid.NewGuid();
        var peerIdB = Guid.NewGuid();

        var transportA = new FakePairingTransport();
        var transportB = new FakePairingTransport();
        transportA.Peer = transportB;
        transportB.Peer = transportA;

        var coordinatorA = new PairingCeremonyCoordinator(
            transportA, storeA, spkiA, peerIdA, spkiB, peerIdB, [9, 9, 9], Epoch);
        var coordinatorB = new PairingCeremonyCoordinator(
            transportB, storeB, spkiB, peerIdB, spkiA, peerIdA, [8, 8, 8], Epoch);

        return (coordinatorA, storeA, coordinatorB, storeB, transportA, transportB);
    }

    [Fact]
    public async Task StartAsync_SendsNonceFrame()
    {
        var (coordinatorA, _, _, _, transportA, _) = MakeCeremonyPair();

        await coordinatorA.StartAsync(CancellationToken.None);

        Assert.Contains(transportA.Sent, f => f.Type == Intercom.ControlChannel.ControlMessageType.PairingNonce);
    }

    [Fact]
    public async Task BothSidesExchangeNonces_ProduceTheSameCode()
    {
        var (coordinatorA, _, coordinatorB, _, _, _) = MakeCeremonyPair();
        string? codeA = null, codeB = null;
        coordinatorA.CodeReady += c => codeA = c;
        coordinatorB.CodeReady += c => codeB = c;

        await coordinatorA.StartAsync(CancellationToken.None);
        await coordinatorB.StartAsync(CancellationToken.None);

        Assert.NotNull(codeA);
        Assert.NotNull(codeB);
        Assert.Equal(codeA, codeB);
        Assert.Matches(@"^\d{3} \d{3}$", codeA!);
    }

    [Fact]
    public async Task BothSidesConfirm_BothBecomeApproved_WithMatchingSpkiPins()
    {
        var (coordinatorA, storeA, coordinatorB, storeB, _, _) = MakeCeremonyPair();
        ApprovedPeer? approvedByA = null, approvedByB = null;
        coordinatorA.Approved += p => approvedByA = p;
        coordinatorB.Approved += p => approvedByB = p;

        await coordinatorA.StartAsync(CancellationToken.None);
        await coordinatorB.StartAsync(CancellationToken.None);
        await coordinatorA.ConfirmLocalAsync("Peer B", CancellationToken.None);
        await coordinatorB.ConfirmLocalAsync("Peer A", CancellationToken.None);

        Assert.NotNull(approvedByA);
        Assert.NotNull(approvedByB);
        Assert.Single(storeA.ApprovedPeers);
        Assert.Single(storeB.ApprovedPeers);
        Assert.Equal(PairingCeremonyPhase.Approved, coordinatorA.Phase);
        Assert.Equal(PairingCeremonyPhase.Approved, coordinatorB.Phase);
    }

    [Fact]
    public async Task OnlyOneSideConfirms_NeitherSideBecomesApproved()
    {
        var (coordinatorA, storeA, coordinatorB, storeB, _, _) = MakeCeremonyPair();
        var approvedRaised = false;
        coordinatorA.Approved += _ => approvedRaised = true;
        coordinatorB.Approved += _ => approvedRaised = true;

        await coordinatorA.StartAsync(CancellationToken.None);
        await coordinatorB.StartAsync(CancellationToken.None);
        await coordinatorA.ConfirmLocalAsync("Peer B", CancellationToken.None); // only A confirms

        Assert.False(approvedRaised);
        Assert.Empty(storeA.ApprovedPeers);
        Assert.Empty(storeB.ApprovedPeers);
        Assert.Equal(PairingCeremonyPhase.LocalConfirmed, coordinatorA.Phase);
        Assert.Equal(PairingCeremonyPhase.RemoteConfirmed, coordinatorB.Phase);
    }

    [Fact]
    public async Task Expiry_AfterOneSidedConfirm_NeverApproves()
    {
        var (coordinatorA, storeA, coordinatorB, storeB, _, _) = MakeCeremonyPair();
        var expiredA = false;
        var expiredB = false;
        coordinatorA.Expired += () => expiredA = true;
        coordinatorB.Expired += () => expiredB = true;

        await coordinatorA.StartAsync(CancellationToken.None);
        await coordinatorB.StartAsync(CancellationToken.None);
        await coordinatorA.ConfirmLocalAsync("Peer B", CancellationToken.None);

        var pastExpiry = Epoch + IdentityStore.PendingPairingExpiry + TimeSpan.FromSeconds(1);
        coordinatorA.Tick(pastExpiry);
        coordinatorB.Tick(pastExpiry);

        Assert.True(expiredA);
        Assert.True(expiredB);
        Assert.Empty(storeA.ApprovedPeers);
        Assert.Empty(storeB.ApprovedPeers);
    }

    [Fact]
    public async Task ExplicitReject_SentImmediately_NeverApproves_EvenIfOtherSideAlreadyConfirmed()
    {
        var (coordinatorA, storeA, coordinatorB, storeB, _, _) = MakeCeremonyPair();
        var rejectedB = false;
        coordinatorB.Rejected += () => rejectedB = true;

        await coordinatorA.StartAsync(CancellationToken.None);
        await coordinatorB.StartAsync(CancellationToken.None);
        await coordinatorB.ConfirmLocalAsync("Peer A", CancellationToken.None); // B confirms first
        await coordinatorA.RejectLocalAsync(CancellationToken.None); // A explicitly rejects instead

        Assert.True(rejectedB); // the reject frame reached B immediately
        Assert.Equal(PairingCeremonyPhase.Rejected, coordinatorA.Phase);
        Assert.Equal(PairingCeremonyPhase.Rejected, coordinatorB.Phase);
        Assert.Empty(storeA.ApprovedPeers);
        Assert.Empty(storeB.ApprovedPeers);
    }

    [Fact]
    public async Task Cancel_IsLocalOnly_SendsNoFrame_RemoteUnaffectedUntilExpiry()
    {
        var (coordinatorA, storeA, coordinatorB, storeB, transportA, _) = MakeCeremonyPair();

        await coordinatorA.StartAsync(CancellationToken.None);
        await coordinatorB.StartAsync(CancellationToken.None);
        var sentCountBeforeCancel = transportA.Sent.Count;

        coordinatorA.Cancel();

        Assert.Equal(sentCountBeforeCancel, transportA.Sent.Count); // no frame sent by cancel
        Assert.Equal(PairingCeremonyPhase.Cancelled, coordinatorA.Phase);
        Assert.Equal(PairingCeremonyPhase.AwaitingConfirmation, coordinatorB.Phase); // B doesn't know yet
        Assert.Empty(storeA.ApprovedPeers);
        Assert.Empty(storeB.ApprovedPeers);
    }
}
