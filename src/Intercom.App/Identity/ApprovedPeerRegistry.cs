namespace Intercom.App.Identity;

/// <summary>
/// The set of peers this device has approved. In-memory only — IdentityStore
/// owns loading/persisting it.
/// </summary>
public sealed class ApprovedPeerRegistry
{
    readonly List<ApprovedPeer> _peers = [];

    public IReadOnlyList<ApprovedPeer> Peers => _peers;

    public void Add(ApprovedPeer peer) => _peers.Add(peer);

    /// <summary>Fail-closed lookup: never returns a revoked peer as approved.</summary>
    public ApprovedPeer? FindApprovedBySpki(byte[] spkiSha256) =>
        _peers.FirstOrDefault(p => !p.Revoked && p.SpkiSha256.AsSpan().SequenceEqual(spkiSha256));

    /// <summary>
    /// Local and immediate (ADR-0002): marks the peer revoked so it is never
    /// again treated as approved. Does not notify the other side — that is a
    /// best-effort, connection-layer concern, not this registry's job.
    /// </summary>
    public bool Forget(Guid peerId)
    {
        var peer = _peers.FirstOrDefault(p => p.PeerId == peerId);
        if (peer is null || peer.Revoked) return false;
        peer.Revoked = true;
        return true;
    }

    internal void ReplaceAll(IEnumerable<ApprovedPeer> peers)
    {
        _peers.Clear();
        _peers.AddRange(peers);
    }
}
