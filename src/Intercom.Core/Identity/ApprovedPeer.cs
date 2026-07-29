namespace Intercom.Identity;

/// <summary>
/// One peer this device has approved via the pairing ceremony (ADR-0002).
/// Approval is pinned by SPKI hash, never by friendly name, IP address, or
/// certificate subject. ContactId groups peers under a locally-named contact
/// (ADR-0004) — that grouping is routing metadata only and carries no trust:
/// it is never transmitted and never lets one peer act on another's behalf.
/// </summary>
public sealed record ApprovedPeer
{
    public required Guid PeerId { get; init; }
    public required string FriendlyName { get; set; }

    /// <summary>Null once forgotten — ADR-0002 requires removing the SPKI pin
    /// locally, not merely flagging the peer, so a revoked record genuinely
    /// no longer carries a usable pin.</summary>
    public required SpkiPin? SpkiSha256 { get; init; }

    /// <summary>Null once forgotten, for the same reason as SpkiSha256.</summary>
    public required byte[]? Certificate { get; init; }

    public required DateTimeOffset ApprovedAt { get; init; }

    /// <summary>Null once forgotten — ADR-0002 requires removing the local
    /// contact association on forget, not just the pin.</summary>
    public string? ContactId { get; set; }

    /// <summary>Last endpoint at which this pinned identity successfully
    /// connected. This is routing metadata, never identity evidence: every
    /// reconnect still requires the stored SPKI pin to match.</summary>
    public string? LastKnownAddress { get; set; }
    public int? LastKnownPort { get; set; }

    /// <summary>
    /// Set by Forget. The record itself is kept (rather than deleted outright)
    /// so a forgotten peer remains visible for local audit/debugging, per
    /// docs/research/pairing-security.md — but with its pin/cert/contact
    /// association actually removed, not merely flagged. Never treated as approved.
    /// </summary>
    public bool Revoked { get; set; }
}
