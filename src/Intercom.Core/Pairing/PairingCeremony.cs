namespace Intercom.Pairing;

/// <summary>Where one side's view of a single pairing ceremony currently
/// stands. Terminal phases (<see cref="Approved"/>, <see cref="Rejected"/>,
/// <see cref="Expired"/>, <see cref="Cancelled"/>) never transition further —
/// once reached, every subsequent event on that ceremony is a no-op.</summary>
public enum PairingCeremonyPhase
{
    /// <summary>Neither this side's user nor the remote side has confirmed
    /// the code yet.</summary>
    AwaitingConfirmation,

    /// <summary>This side's user stamped "approved" (and the local Confirm
    /// message has been sent), but the remote side's Confirm has not arrived
    /// yet.</summary>
    LocalConfirmed,

    /// <summary>The remote side's Confirm message arrived, but this side's
    /// user has not yet stamped "approved".</summary>
    RemoteConfirmed,

    /// <summary>Both sides confirmed. Terminal — the caller pins the SPKI and
    /// calls <c>IdentityStore.Approve</c> exactly when this phase is
    /// reached.</summary>
    Approved,

    /// <summary>An explicit reject (local or remote) was received. Terminal —
    /// no trust-state change results (ADR-0002).</summary>
    Rejected,

    /// <summary>The 2-minute pairing-request window elapsed before both
    /// sides confirmed. Terminal — no trust-state change results
    /// (ADR-0002).</summary>
    Expired,

    /// <summary>The local user cancelled before completion (e.g. closed the
    /// postcard without stamping or tearing). Terminal — no trust-state
    /// change results.</summary>
    Cancelled,
}

/// <summary>
/// Pure confirmation state machine for one side of one pairing ceremony —
/// no I/O, no wire format, no cryptography. This is what makes ADR-0002's
/// "both people must explicitly confirm before either side becomes an
/// approved peer" and "confirmation on only one side... never creates an
/// approved peer" independently, deterministically testable: the only way to
/// reach <see cref="PairingCeremonyPhase.Approved"/> is
/// <see cref="ConfirmLocal"/> and <see cref="ConfirmRemote"/> both having
/// been called, in either order, before any terminal-reject/expire/cancel
/// event.
///
/// The wire-level "send my Confirm frame" / "I received the peer's Confirm
/// frame" plumbing, the six-digit code itself, and the actual
/// <c>IdentityStore.Approve</c> call are all the caller's (the coordinator's)
/// job — this class only tracks whose confirmations have been seen.
/// </summary>
public sealed class PairingCeremony
{
    public PairingCeremonyPhase Phase { get; private set; } = PairingCeremonyPhase.AwaitingConfirmation;

    public bool IsTerminal => Phase is PairingCeremonyPhase.Approved or PairingCeremonyPhase.Rejected
        or PairingCeremonyPhase.Expired or PairingCeremonyPhase.Cancelled;

    /// <summary>Records that this side's user stamped "approved" (and the
    /// local Confirm frame is about to be/has been sent). Returns true if
    /// this call caused a phase transition (including straight to
    /// <see cref="PairingCeremonyPhase.Approved"/> if the remote side had
    /// already confirmed); false if the ceremony was already terminal or
    /// this side had already confirmed.</summary>
    public bool ConfirmLocal()
    {
        if (IsTerminal || Phase is PairingCeremonyPhase.LocalConfirmed) return false;

        Phase = Phase switch
        {
            PairingCeremonyPhase.AwaitingConfirmation => PairingCeremonyPhase.LocalConfirmed,
            PairingCeremonyPhase.RemoteConfirmed => PairingCeremonyPhase.Approved,
            _ => Phase,
        };
        return true;
    }

    /// <summary>Records that the remote side's Confirm frame arrived. Same
    /// semantics as <see cref="ConfirmLocal"/>, mirrored for the other
    /// side.</summary>
    public bool ConfirmRemote()
    {
        if (IsTerminal || Phase is PairingCeremonyPhase.RemoteConfirmed) return false;

        Phase = Phase switch
        {
            PairingCeremonyPhase.AwaitingConfirmation => PairingCeremonyPhase.RemoteConfirmed,
            PairingCeremonyPhase.LocalConfirmed => PairingCeremonyPhase.Approved,
            _ => Phase,
        };
        return true;
    }

    /// <summary>An explicit reject, from either side. Terminal from any
    /// non-terminal phase. Returns false (no-op) if already terminal.</summary>
    public bool Reject()
    {
        if (IsTerminal) return false;
        Phase = PairingCeremonyPhase.Rejected;
        return true;
    }

    /// <summary>The 2-minute window elapsed. Terminal from any non-terminal
    /// phase, including one where only one side had confirmed — a one-sided
    /// confirmation never becomes approval, no matter how long it waits.
    /// Returns false (no-op) if already terminal (in particular, an
    /// already-Approved ceremony never regresses to Expired).</summary>
    public bool Expire()
    {
        if (IsTerminal) return false;
        Phase = PairingCeremonyPhase.Expired;
        return true;
    }

    /// <summary>The local user cancelled (e.g. dismissed the postcard).
    /// Terminal from any non-terminal phase. Returns false (no-op) if already
    /// terminal.</summary>
    public bool Cancel()
    {
        if (IsTerminal) return false;
        Phase = PairingCeremonyPhase.Cancelled;
        return true;
    }
}
