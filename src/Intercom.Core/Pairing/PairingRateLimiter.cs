namespace Intercom.Pairing;

/// <summary>
/// The two pairing-attempt rate-limit counters ADR-0002 requires beyond the
/// "one outstanding request per peer" rule (already enforced by
/// <c>Intercom.Identity.PendingPairingRegistry</c>): 5 attempts per source
/// per 10 minutes, and 20 attempts globally per 10 minutes. "Source" is
/// whatever the caller uses to identify where a pairing attempt originated —
/// in practice the remote peer's claimed peer ID or discovered endpoint, not
/// necessarily the peer's cryptographic identity (a not-yet-approved peer
/// making repeated pairing attempts is exactly the case this throttles).
///
/// In-memory only, per ADR-0002's own cost/benefit reasoning ("An unlikely
/// edge case... aggressive anti-abuse tooling... isn't justified by the
/// threat model") — losing the counters on a restart is an accepted MVP
/// trade-off, not an oversight. A simple fixed-size timestamp list per source
/// plus one global list is enough for a threshold pair used in exactly one
/// place; this deliberately does not generalize into a reusable
/// rate-limiter abstraction.
///
/// Not itself thread-safe by document contract — callers that touch this
/// from multiple threads (e.g. a UI-initiated attempt racing an inbound
/// pairing connection on a receive-loop thread) must serialize access
/// externally, the same way <c>PeerControlChannel</c> serializes its own
/// state behind a single gate.
/// </summary>
public sealed class PairingRateLimiter
{
    public const int MaxAttemptsPerSource = 5;
    public const int MaxAttemptsGlobal = 20;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    readonly Dictionary<string, List<DateTimeOffset>> _perSource = [];
    readonly List<DateTimeOffset> _global = [];

    /// <summary>Checks whether one more pairing attempt from
    /// <paramref name="source"/> is allowed under both thresholds and, if so,
    /// records it (both counters are updated atomically with the check — a
    /// caller never needs a separate "record" step, avoiding a TOCTOU gap
    /// between checking and recording). Returns false, with nothing
    /// recorded, if either threshold is already at its limit.</summary>
    public bool TryRecordAttempt(string source, DateTimeOffset now)
    {
        Prune(_global, now);
        var sourceAttempts = GetOrCreate(source);
        Prune(sourceAttempts, now);

        if (sourceAttempts.Count >= MaxAttemptsPerSource) return false;
        if (_global.Count >= MaxAttemptsGlobal) return false;

        sourceAttempts.Add(now);
        _global.Add(now);
        return true;
    }

    List<DateTimeOffset> GetOrCreate(string source)
    {
        if (!_perSource.TryGetValue(source, out var list))
        {
            list = [];
            _perSource[source] = list;
        }
        return list;
    }

    static void Prune(List<DateTimeOffset> attempts, DateTimeOffset now) =>
        attempts.RemoveAll(t => now - t >= Window);
}
