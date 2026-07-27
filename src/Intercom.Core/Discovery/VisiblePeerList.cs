using System.Net.Sockets;

namespace Intercom.Discovery;

/// <summary>
/// Turns the raw, per-interface <see cref="DiscoverySignal"/> stream into the
/// "visible, unapproved" peer list issue #20 asks discovery to produce:
/// dedups repeated sightings of the same peer across interfaces, tracks
/// each sighting's TTL independently, and merges a peer's live endpoints
/// into one <see cref="VisiblePeer"/> entry. Pure policy — no native calls,
/// no timers of its own — so it's fully unit-testable: callers (tests or
/// <see cref="DiscoveryService"/>) push signals in and drive expiry with an
/// explicit clock via <see cref="EvaluateExpiry"/>.
///
/// A peer can be discovered on more than one interface (e.g. Ethernet and
/// Wi-Fi both up on the same PC), and on a single interface it can advertise
/// both an IPv4 and an IPv6 address (dual-stack). Each (PeerIdHint,
/// InterfaceId, AddressFamily) triple is tracked as its own "sighting" with
/// its own TTL, and the peer only leaves the visible list once every
/// sighting for it has expired or been withdrawn, per
/// docs/research/local-network-transport.md's "support IPv4 and IPv6" and
/// "retain interface/scope ID for link-local IPv6 endpoints on multi-homed
/// PCs".
/// </summary>
public sealed class VisiblePeerList
{
    // Guards _sightings. Observe/EvaluateExpiry/RemoveAllForInterface are all
    // reachable from different threads at once in the real app: native
    // DNS-SD browse callbacks (arbitrary thread pool threads, one per
    // interface), DiscoveryService's expiry-sweep Timer, a NetworkChanged
    // handler, and Peers reads from the UI thread. This class used to
    // document itself as "not internally locked" on the assumption callers
    // would serialize access, which no real caller here actually does.
    readonly object _gate = new();
    readonly Dictionary<SightingKey, Sighting> _sightings = new();

    /// <summary>Raised whenever the merged visible-peer list changes (a peer
    /// appeared, gained/lost an endpoint, or fully disappeared). No payload —
    /// callers re-read <see cref="Peers"/>, matching this codebase's other
    /// "read the model after the notification" event style (see
    /// AppLifecycle's QuitRequested). Always raised with <see cref="_gate"/>
    /// released, so a subscriber calling straight back into this class (e.g.
    /// reading <see cref="Peers"/>) can never deadlock against the thread
    /// that raised it.</summary>
    public event Action? Changed;

    public IReadOnlyList<VisiblePeer> Peers
    {
        get
        {
            lock (_gate)
            {
                return _sightings.Values.GroupBy(s => s.PeerIdHint).Select(ToVisiblePeer).ToList();
            }
        }
    }

    /// <summary>Applies one raw signal from a browse callback. Safe to call
    /// concurrently from multiple interfaces' browse threads.</summary>
    public void Observe(DiscoverySignal signal, DateTimeOffset now)
    {
        var changed = false;

        switch (signal)
        {
            case DiscoverySignal.Seen seen:
                // Normalize here rather than trusting the caller: InterfaceId
                // is carried on both the signal and its Endpoint, and this is
                // the one place that turns a raw signal into stored state, so
                // it's the one place that can guarantee the two never
                // disagree (a mismatch would otherwise silently key a
                // sighting under one interface while its endpoint claims
                // another).
                var endpoint = seen.Endpoint.InterfaceId == signal.InterfaceId
                    ? seen.Endpoint
                    : seen.Endpoint with { InterfaceId = signal.InterfaceId };
                var key = new SightingKey(signal.PeerIdHint, signal.InterfaceId, endpoint.Address.AddressFamily);
                lock (_gate)
                {
                    var firstSeenAt = _sightings.TryGetValue(key, out var existing) ? existing.FirstSeenAt : now;
                    _sightings[key] = new Sighting
                    {
                        PeerIdHint = signal.PeerIdHint,
                        InterfaceId = signal.InterfaceId,
                        ProtocolVersion = seen.ProtocolVersion,
                        Endpoint = endpoint,
                        FirstSeenAt = firstSeenAt,
                        LastSeenAt = now,
                        ExpiresAt = now + seen.Ttl,
                    };
                }
                changed = true;
                break;

            case DiscoverySignal.Withdrawn:
                // A goodbye record withdraws the whole service instance, not
                // one address family — remove every sighting for this peer on
                // this interface (IPv4 and IPv6 alike). Same as TTL expiry
                // (ADR-0001), this is a discovery-only signal and never
                // implies anything about an active connection.
                lock (_gate)
                {
                    var withdrawnKeys = _sightings.Keys
                        .Where(k => k.PeerIdHint == signal.PeerIdHint && k.InterfaceId == signal.InterfaceId)
                        .ToList();
                    foreach (var k in withdrawnKeys) _sightings.Remove(k);
                    changed = withdrawnKeys.Count > 0;
                }
                break;
        }

        if (changed) Changed?.Invoke();
    }

    /// <summary>Removes every sighting whose TTL has lapsed as of <paramref
    /// name="now"/>. Returns the count removed. Callers drive this from a
    /// timer (DiscoveryService) or explicitly in a test — this class never
    /// reads the wall clock itself.</summary>
    public int EvaluateExpiry(DateTimeOffset now)
    {
        int removedCount;
        lock (_gate)
        {
            var expiredKeys = _sightings.Where(kv => kv.Value.ExpiresAt <= now).Select(kv => kv.Key).ToList();
            foreach (var key in expiredKeys) _sightings.Remove(key);
            removedCount = expiredKeys.Count;
        }
        if (removedCount > 0) Changed?.Invoke();
        return removedCount;
    }

    /// <summary>Drops every sighting observed on the given interface, e.g.
    /// when re-evaluation determines the interface is no longer eligible
    /// (unplugged, went down, VPN adapter appeared and took its place).
    /// Distinct from TTL expiry/goodbye: this is a local, not a remote,
    /// signal.</summary>
    public int RemoveAllForInterface(string interfaceId)
    {
        int removedCount;
        lock (_gate)
        {
            var keys = _sightings.Keys.Where(k => k.InterfaceId == interfaceId).ToList();
            foreach (var key in keys) _sightings.Remove(key);
            removedCount = keys.Count;
        }
        if (removedCount > 0) Changed?.Invoke();
        return removedCount;
    }

    static VisiblePeer ToVisiblePeer(IGrouping<PeerIdHint, Sighting> group)
    {
        var sightings = group.ToList();
        return new VisiblePeer
        {
            PeerIdHint = group.Key,
            // A peer advertising different versions on different interfaces
            // is not a case the protocol expects; take the most recent
            // sighting's version as the representative value rather than
            // silently picking an arbitrary one.
            ProtocolVersion = sightings.OrderByDescending(s => s.LastSeenAt).First().ProtocolVersion,
            Endpoints = sightings.Select(s => s.Endpoint).ToList(),
            FirstSeenAt = sightings.Min(s => s.FirstSeenAt),
            LastSeenAt = sightings.Max(s => s.LastSeenAt),
        };
    }

    readonly record struct SightingKey(PeerIdHint PeerIdHint, string InterfaceId, AddressFamily AddressFamily);

    sealed class Sighting
    {
        public required PeerIdHint PeerIdHint { get; init; }
        public required string InterfaceId { get; init; }
        public required int ProtocolVersion { get; init; }
        public required PeerEndpoint Endpoint { get; init; }
        public required DateTimeOffset FirstSeenAt { get; init; }
        public required DateTimeOffset LastSeenAt { get; init; }
        public required DateTimeOffset ExpiresAt { get; init; }
    }
}
