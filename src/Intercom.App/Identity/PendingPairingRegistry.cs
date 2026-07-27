namespace Intercom.App.Identity;

/// <summary>
/// In-progress pairing ceremonies. ADR-0002: one outstanding request per peer
/// identity; a 2-minute expiry with no trust-state change. This registry only
/// enforces those two rules over storage — the ceremony itself is #22.
/// </summary>
public sealed class PendingPairingRegistry
{
    public static readonly TimeSpan ExpiryTimeout = TimeSpan.FromMinutes(2);

    readonly List<PendingPairing> _pending = [];

    public IReadOnlyList<PendingPairing> Pending => _pending;

    /// <summary>False if a request for this peer is already outstanding
    /// (ADR-0002: one outstanding request per peer identity).</summary>
    public bool TryStart(Guid peerId, DateTimeOffset now)
    {
        if (_pending.Any(p => p.PeerId == peerId)) return false;
        _pending.Add(new PendingPairing { PeerId = peerId, StartedAt = now });
        return true;
    }

    public bool Complete(Guid peerId) => _pending.RemoveAll(p => p.PeerId == peerId) > 0;

    /// <summary>Drops requests older than the expiry timeout. No trust-state
    /// change results from expiry — the request is just gone.</summary>
    public int RemoveExpired(DateTimeOffset now) =>
        _pending.RemoveAll(p => p.IsExpired(ExpiryTimeout, now));

    internal void ReplaceAll(IEnumerable<PendingPairing> pending)
    {
        _pending.Clear();
        _pending.AddRange(pending);
    }
}
