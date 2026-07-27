using Intercom.ControlChannel;

namespace Intercom.Presence;

/// <summary>
/// One presence record as broadcast to approved peers
/// (docs/research/active-device-presence.md "Presence lease"). Send-only in
/// this ticket (#23) — receiving/routing/ranking multiple devices for a
/// contact is #30's job.
///
/// <see cref="ContactId"/>: the research doc lists this field as "locally
/// configured person/contact grouping", but
/// docs/adr/0004-multi-device-contact-routing.md's "Device-to-contact
/// association" decision clarifies that grouping is purely local and
/// unilateral to whichever device does the grouping, and "nothing about the
/// grouping is ever transmitted" — that concept doesn't exist on the
/// SENDING side at all yet (#30 hasn't been built). For #23, ContactId is
/// populated with this device's own <see cref="DeviceId"/> as a
/// self-identifying placeholder — effectively "this device is its own
/// contact" — until #30 defines real multi-device-per-contact association.
/// This is a deliberate, documented scoping call; reviewers should double
/// check it against #30 when that ticket is picked up.
/// </summary>
public sealed record PresenceLease
{
    public required Guid DeviceId { get; init; }
    public required Guid ContactId { get; init; }

    /// <summary>Fresh per process start (docs: "random value generated each
    /// process start").</summary>
    public required Guid IncarnationId { get; init; }

    /// <summary>Strictly increasing within <see cref="IncarnationId"/>. A
    /// changed incarnation resets this — enforcing that is a receiver
    /// concern (#30); this ticket's job is only to make sure the SENDER
    /// never emits a lower or repeated value within one incarnation (see
    /// <see cref="PresenceEngine.NextLease"/>).</summary>
    public required ulong Sequence { get; init; }

    public required AvailabilityState Availability { get; init; }
    public required bool Dnd { get; init; }

    /// <summary>Omitted (null) when <see cref="Availability"/> is
    /// Unavailable — docs/research/active-device-presence.md's field table:
    /// "omit when unavailable".</summary>
    public required IdleAgeBucket? IdleAgeBucket { get; init; }

    public required int LeaseSeconds { get; init; }
    public required Capability Capabilities { get; init; }
}
