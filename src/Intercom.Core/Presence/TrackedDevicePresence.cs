using Intercom.ControlChannel;

namespace Intercom.Presence;

/// <summary>
/// The receiver's local view of one remote device's most recently accepted
/// presence lease (issue #30 — the receive side that
/// <see cref="PresenceLease"/>'s and <see cref="PresenceFrameCodec"/>'s own
/// doc comments call out as this ticket's job). Built entirely from
/// receiver-local state: <see cref="LastUpdatedAt"/> and
/// <see cref="ExpiresAt"/> are the receiver's OWN monotonic clock readings,
/// never the sender's wall clock or anything derived from it
/// (docs/research/active-device-presence.md: "It records its own monotonic
/// receive time and expires the record locally after the lease; it does not
/// trust the sender's wall clock.").
///
/// <see cref="ContactId"/> is carried over from the wire lease purely for
/// diagnostics — per ADR-0004, contact grouping is local, unilateral, and
/// never meaningfully transmitted (see <see cref="PresenceLease.ContactId"/>'s
/// own remarks: the sender fills it with a self-identifying placeholder).
/// Routing code must determine "which local contact does this device belong
/// to" from <c>Intercom.Contacts.ContactStore</c>'s own membership list
/// keyed by <see cref="DeviceId"/>, never from this field.
/// </summary>
public sealed record TrackedDevicePresence
{
    public required Guid DeviceId { get; init; }
    public required Guid ContactId { get; init; }
    public required Guid IncarnationId { get; init; }
    public required ulong Sequence { get; init; }
    public required AvailabilityState Availability { get; init; }
    public required bool Dnd { get; init; }
    public required IdleAgeBucket? IdleAgeBucket { get; init; }
    public required Capability Capabilities { get; init; }

    /// <summary>This receiver's own clock reading at the moment this record's
    /// lease was accepted — used only for ranking recency (see
    /// <c>Intercom.Routing.DeviceRanker</c>), never transmitted.</summary>
    public required DateTimeOffset LastUpdatedAt { get; init; }

    /// <summary><see cref="LastUpdatedAt"/> plus the lease's advertised
    /// LeaseSeconds, computed against THIS receiver's own clock — see the
    /// class remarks.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }
}
