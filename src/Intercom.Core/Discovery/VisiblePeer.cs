namespace Intercom.Discovery;

using Intercom.Identity;

/// <summary>
/// One entry in the "visible, unapproved" peer list (issue #20). This is
/// discovery's entire output to the rest of the app: a peer being in this
/// list means only that a DNS-SD record for it currently exists on the LAN
/// — never that it has been approved, that a connection exists, or that it
/// is actually reachable (ADR-0001: discovery is visibility, never trust).
/// Approval/connection is issue #21's concern entirely.
/// </summary>
public sealed record VisiblePeer
{
    public required PeerIdHint PeerIdHint { get; init; }
    public required int ProtocolVersion { get; init; }
    public SpkiPin? Spki { get; init; }

    /// <summary>All currently-live endpoints for this peer, merged across
    /// every interface it has been seen on (e.g. both Ethernet and Wi-Fi on
    /// a multi-homed PC). Never empty for an entry still in the list — the
    /// last endpoint expiring is what removes the entry.</summary>
    public required IReadOnlyList<PeerEndpoint> Endpoints { get; init; }

    public required DateTimeOffset FirstSeenAt { get; init; }
    public required DateTimeOffset LastSeenAt { get; init; }
}
