namespace Intercom.Identity;

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
    public ApprovedPeer? FindApprovedBySpki(SpkiPin spkiSha256) =>
        _peers.FirstOrDefault(p => !p.Revoked && p.SpkiSha256 == spkiSha256);

    /// <summary>
    /// Local and immediate (ADR-0002): removes the peer's SPKI pin, certificate,
    /// and contact association — not merely a flag — so it can never again be
    /// treated as approved or grouped under a contact. The record itself is
    /// kept, marked Revoked, as a local audit trace. Does not notify the other
    /// side — that is a best-effort, connection-layer concern, not this
    /// registry's job.
    /// </summary>
    public bool Forget(Guid peerId)
    {
        var index = _peers.FindIndex(p => p.PeerId == peerId);
        if (index < 0 || _peers[index].Revoked) return false;

        _peers[index] = _peers[index] with
        {
            Revoked = true,
            SpkiSha256 = null,
            Certificate = null,
            ContactId = null,
        };
        return true;
    }

    internal void ReplaceAll(IEnumerable<ApprovedPeer> peers)
    {
        _peers.Clear();
        _peers.AddRange(peers);
    }
}
