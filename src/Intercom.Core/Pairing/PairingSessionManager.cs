using Intercom.Identity;

namespace Intercom.Pairing;

/// <summary>
/// Owns every pairing ceremony currently in progress on this device: applies
/// ADR-0002's rate limits (one outstanding request per peer, via
/// <see cref="IdentityStore.StartPairing"/>; 5/source and 20/global per 10
/// minutes, via <see cref="PairingRateLimiter"/>) before a
/// <see cref="PairingCeremonyCoordinator"/> is ever created, then tracks the
/// live coordinators and retires them (from <see cref="Sessions"/>, though
/// never from <see cref="IdentityStore"/> — the coordinator itself already
/// calls <c>CompletePairing</c>) once each reaches a terminal phase.
///
/// This is the "thin I/O seam" tier between the pure policy in this
/// namespace and the WinUI presentation layer — it has no socket/UI code of
/// its own, but it is stateful and mutates <see cref="IdentityStore"/>, so it
/// is not unit-tested with the same "pure function" rigor as
/// <see cref="PairingTranscript"/>/<see cref="PairingCeremony"/>.
///
/// Not safe to drive from multiple threads without external synchronization
/// beyond what is documented per method — <see cref="TryStartSession"/> is
/// expected to be called from the UI thread (a user action, or a router
/// reacting to an inbound connection marshaled onto the UI thread), while
/// <see cref="Tick"/> is expected to be called from a periodic UI-thread
/// timer, mirroring the rest of this codebase's "UI thread owns timers"
/// convention (see <c>PeerControlChannel.Tick</c>'s callers).
/// </summary>
public sealed class PairingSessionManager
{
    readonly IdentityStore _identityStore;
    readonly PairingRateLimiter _rateLimiter = new();
    readonly Dictionary<Guid, PairingCeremonyCoordinator> _sessions = [];

    public PairingSessionManager(IdentityStore identityStore)
    {
        _identityStore = identityStore;
    }

    public IReadOnlyCollection<PairingCeremonyCoordinator> Sessions => _sessions.Values;

    /// <summary>Enforces ADR-0002's rate limits and, if they allow it,
    /// starts and returns a new ceremony coordinator for
    /// <paramref name="remotePeerIdHint"/>. Returns null (nothing created,
    /// nothing recorded beyond whatever <see cref="IdentityStore.StartPairing"/>
    /// itself records) if:
    /// <list type="bullet">
    /// <item>a pairing request for this peer is already outstanding;</item>
    /// <item><paramref name="source"/> has hit the 5-attempts/10-minute limit; or</item>
    /// <item>the 20-attempts/10-minute global limit has been hit.</item>
    /// </list>
    /// </summary>
    public PairingCeremonyCoordinator? TryStartSession(
        string source,
        IPairingTransport transport,
        SpkiPin localSpki,
        Guid localPeerId,
        SpkiPin remoteSpki,
        Guid remotePeerIdHint,
        byte[] remoteCertificate,
        DateTimeOffset now)
    {
        if (_sessions.ContainsKey(remotePeerIdHint)) return null;
        if (!_identityStore.StartPairing(remotePeerIdHint, now)) return null;
        if (!_rateLimiter.TryRecordAttempt(source, now))
        {
            // Roll back the pending-pairing record we just created — this
            // attempt never actually got underway, so it must not occupy the
            // one-outstanding-per-peer slot indefinitely.
            _identityStore.CompletePairing(remotePeerIdHint);
            return null;
        }

        var session = new PairingCeremonyCoordinator(
            transport, _identityStore, localSpki, localPeerId, remoteSpki, remotePeerIdHint, remoteCertificate, now);

        session.Approved += _ => Retire(remotePeerIdHint);
        session.Rejected += () => Retire(remotePeerIdHint);
        session.Expired += () => Retire(remotePeerIdHint);

        _sessions[remotePeerIdHint] = session;
        return session;
    }

    /// <summary>Cancels and retires every session for
    /// <paramref name="remotePeerIdHint"/>, if one exists. Used when the
    /// user dismisses a postcard.</summary>
    public void Cancel(Guid remotePeerIdHint)
    {
        if (!_sessions.TryGetValue(remotePeerIdHint, out var session)) return;
        session.Cancel();
        Retire(remotePeerIdHint);
    }

    void Retire(Guid remotePeerIdHint) => _sessions.Remove(remotePeerIdHint);

    /// <summary>Drive from a periodic UI-thread timer: ticks every live
    /// session (expiring any that have run past the 2-minute window) and
    /// prunes <see cref="IdentityStore"/>'s own pending-pairing records for
    /// consistency (belt-and-braces alongside each coordinator's own
    /// <c>CompletePairing</c> call on expiry).</summary>
    public void Tick(DateTimeOffset now)
    {
        foreach (var session in _sessions.Values.ToList())
        {
            session.Tick(now);
        }
        _identityStore.PruneExpiredPairings(now);
    }
}
