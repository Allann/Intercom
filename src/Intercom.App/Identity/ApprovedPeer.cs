namespace Intercom.App.Identity;

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
    public required byte[] SpkiSha256 { get; init; }
    public required byte[] Certificate { get; init; }
    public required DateTimeOffset ApprovedAt { get; init; }
    public string? ContactId { get; set; }

    /// <summary>
    /// Set by Forget. Kept rather than deleted outright so a forgotten peer's
    /// prior approval remains visible for local audit/debugging, per
    /// docs/research/pairing-security.md — it is never treated as approved.
    /// </summary>
    public bool Revoked { get; set; }
}
