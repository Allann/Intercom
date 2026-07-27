namespace Intercom.Contacts;

/// <summary>
/// A purely local, unilateral grouping of already-approved peers under one
/// named contact (issue #30;
/// docs/adr/0004-multi-device-contact-routing.md "Device-to-contact
/// association"): no coordination or acknowledgement is required from the
/// peers being grouped, this carries zero trust implication, and — per the
/// ADR's own words — "nothing about the grouping is ever transmitted."
///
/// <see cref="MemberPeerIds"/> references peers purely by their already-
/// approved <c>Intercom.Identity.ApprovedPeer.PeerId</c> (which, in this
/// app's model, is the same GUID a device advertises as its presence
/// <c>DeviceId</c> — see <c>Intercom.Presence.PresenceLease.DeviceId</c> and
/// how <c>App.xaml.cs.StartPresence</c> constructs its
/// <c>PresenceEngine</c> from <c>IdentityStore.Identity.PeerId</c>).
/// Grouping or ungrouping a peer never touches
/// <c>Intercom.Identity.IdentityStore.Approve</c>/<c>Forget</c> — those
/// remain the sole source of trust; this record is routing metadata only.
/// </summary>
public sealed record Contact
{
    public required Guid ContactId { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<Guid> MemberPeerIds { get; init; }
}
