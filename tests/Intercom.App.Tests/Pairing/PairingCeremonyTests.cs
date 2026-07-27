using Intercom.Pairing;
using Xunit;

namespace Intercom.App.Tests.Pairing;

/// <summary>
/// Pure state-machine tests for <see cref="PairingCeremony"/> — no I/O, no
/// wire format. Covers ADR-0002's core invariant: the only way to reach
/// <see cref="PairingCeremonyPhase.Approved"/> is both
/// <see cref="PairingCeremony.ConfirmLocal"/> and
/// <see cref="PairingCeremony.ConfirmRemote"/> having been called, and that
/// reject/expire/cancel are always terminal with no approval possible
/// afterward.
/// </summary>
public class PairingCeremonyTests
{
    [Fact]
    public void InitialPhase_IsAwaitingConfirmation()
    {
        var ceremony = new PairingCeremony();

        Assert.Equal(PairingCeremonyPhase.AwaitingConfirmation, ceremony.Phase);
        Assert.False(ceremony.IsTerminal);
    }

    [Fact]
    public void ConfirmLocalOnly_NeverReachesApproved()
    {
        var ceremony = new PairingCeremony();

        ceremony.ConfirmLocal();

        Assert.Equal(PairingCeremonyPhase.LocalConfirmed, ceremony.Phase);
        Assert.NotEqual(PairingCeremonyPhase.Approved, ceremony.Phase);
    }

    [Fact]
    public void ConfirmRemoteOnly_NeverReachesApproved()
    {
        var ceremony = new PairingCeremony();

        ceremony.ConfirmRemote();

        Assert.Equal(PairingCeremonyPhase.RemoteConfirmed, ceremony.Phase);
        Assert.NotEqual(PairingCeremonyPhase.Approved, ceremony.Phase);
    }

    [Fact]
    public void ConfirmLocalThenRemote_ReachesApproved()
    {
        var ceremony = new PairingCeremony();

        ceremony.ConfirmLocal();
        ceremony.ConfirmRemote();

        Assert.Equal(PairingCeremonyPhase.Approved, ceremony.Phase);
        Assert.True(ceremony.IsTerminal);
    }

    [Fact]
    public void ConfirmRemoteThenLocal_ReachesApproved()
    {
        var ceremony = new PairingCeremony();

        ceremony.ConfirmRemote();
        ceremony.ConfirmLocal();

        Assert.Equal(PairingCeremonyPhase.Approved, ceremony.Phase);
    }

    [Fact]
    public void Reject_FromAwaitingConfirmation_NeverApproves()
    {
        var ceremony = new PairingCeremony();

        ceremony.Reject();

        Assert.Equal(PairingCeremonyPhase.Rejected, ceremony.Phase);
        Assert.True(ceremony.IsTerminal);
    }

    [Fact]
    public void Reject_AfterOneSidedConfirm_StillNeverApproves()
    {
        var ceremony = new PairingCeremony();
        ceremony.ConfirmLocal();

        ceremony.Reject();

        Assert.Equal(PairingCeremonyPhase.Rejected, ceremony.Phase);
    }

    [Fact]
    public void ConfirmAfterReject_IsANoOp_StaysRejected()
    {
        var ceremony = new PairingCeremony();
        ceremony.Reject();

        var localChanged = ceremony.ConfirmLocal();
        var remoteChanged = ceremony.ConfirmRemote();

        Assert.False(localChanged);
        Assert.False(remoteChanged);
        Assert.Equal(PairingCeremonyPhase.Rejected, ceremony.Phase);
    }

    [Fact]
    public void Expire_FromAwaitingConfirmation_NeverApproves()
    {
        var ceremony = new PairingCeremony();

        ceremony.Expire();

        Assert.Equal(PairingCeremonyPhase.Expired, ceremony.Phase);
    }

    [Fact]
    public void Expire_AfterOneSidedConfirm_StillNeverApproves()
    {
        var ceremony = new PairingCeremony();
        ceremony.ConfirmRemote();

        ceremony.Expire();

        Assert.Equal(PairingCeremonyPhase.Expired, ceremony.Phase);
    }

    [Fact]
    public void Expire_AfterBothConfirmed_NeverRegressesFromApproved()
    {
        var ceremony = new PairingCeremony();
        ceremony.ConfirmLocal();
        ceremony.ConfirmRemote();

        var changed = ceremony.Expire();

        Assert.False(changed);
        Assert.Equal(PairingCeremonyPhase.Approved, ceremony.Phase);
    }

    [Fact]
    public void Cancel_FromAwaitingConfirmation_NeverApproves()
    {
        var ceremony = new PairingCeremony();

        ceremony.Cancel();

        Assert.Equal(PairingCeremonyPhase.Cancelled, ceremony.Phase);
    }

    [Fact]
    public void Cancel_AfterOneSidedConfirm_StillNeverApproves()
    {
        var ceremony = new PairingCeremony();
        ceremony.ConfirmLocal();

        ceremony.Cancel();

        Assert.Equal(PairingCeremonyPhase.Cancelled, ceremony.Phase);
    }

    [Fact]
    public void RepeatedConfirmLocal_IsIdempotent_DoesNotDoubleAdvance()
    {
        var ceremony = new PairingCeremony();

        var first = ceremony.ConfirmLocal();
        var second = ceremony.ConfirmLocal();

        Assert.True(first);
        Assert.False(second);
        Assert.Equal(PairingCeremonyPhase.LocalConfirmed, ceremony.Phase);
    }

    [Fact]
    public void RepeatedConfirmRemote_IsIdempotent_DoesNotDoubleAdvance()
    {
        var ceremony = new PairingCeremony();

        var first = ceremony.ConfirmRemote();
        var second = ceremony.ConfirmRemote();

        Assert.True(first);
        Assert.False(second);
        Assert.Equal(PairingCeremonyPhase.RemoteConfirmed, ceremony.Phase);
    }
}
