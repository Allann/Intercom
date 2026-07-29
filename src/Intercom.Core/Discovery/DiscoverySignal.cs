namespace Intercom.Discovery;

using Intercom.Identity;

/// <summary>
/// A raw event delivered by <see cref="IDnsServiceDiscovery"/>'s browse
/// callback, before any policy (TTL bookkeeping, dedup, merging across
/// interfaces) is applied. Modeled as a closed hierarchy rather than one
/// class with a nullable "IsGoodbye"/"Ttl" field, per this codebase's
/// "making illegal states unrepresentable" convention (see
/// prototypes/14-lan-resilience/README.md): a goodbye/removal notification
/// has no TTL or endpoint to be meaningful, and a sighting always has both,
/// so the two must not be representable as the same shape with some fields
/// left null.
/// </summary>
public abstract class DiscoverySignal
{
    /// <summary>The peer-ID hint from the TXT record. Present on both signal
    /// kinds because DNS-SD "goodbye" (TTL=0) records still name the service
    /// instance being withdrawn.</summary>
    public required PeerIdHint PeerIdHint { get; init; }

    /// <summary>Interface this signal was observed on (LanInterface.Id) —
    /// browsing is per-interface, and a peer withdrawn on one interface may
    /// still be visible on another.</summary>
    public required string InterfaceId { get; init; }

    public required DateTimeOffset ObservedAt { get; init; }

    DiscoverySignal() { }

    /// <summary>A peer is present with the given TTL — either newly seen or
    /// a refresh of an existing sighting. Never used for a goodbye record;
    /// see <see cref="Withdrawn"/> for that.</summary>
    public sealed class Seen : DiscoverySignal
    {
        public required int ProtocolVersion { get; init; }
        public SpkiPin? Spki { get; init; }
        public required PeerEndpoint Endpoint { get; init; }

        /// <summary>How long this sighting is valid for absent a refresh —
        /// from the SRV/TXT record TTL, not a connection-liveness timeout
        /// (ADR-0001: TTL expiry is a discovery-only signal).</summary>
        public required TimeSpan Ttl { get; init; }
    }

    /// <summary>An explicit DNS-SD goodbye record (TTL=0) — the peer is
    /// deliberately withdrawing its advertisement (e.g. clean shutdown),
    /// distinct from simply not being refreshed before its TTL lapses.</summary>
    public sealed class Withdrawn : DiscoverySignal
    {
    }
}
