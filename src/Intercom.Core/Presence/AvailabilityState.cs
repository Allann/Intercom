namespace Intercom.Presence;

/// <summary>
/// Coarse device availability (docs/research/active-device-presence.md):
/// derived locally from session usability and input idle age, never
/// transmitted as anything finer-grained than this. <c>offline</c> is
/// deliberately not a member here — it is never something a device
/// authoritatively announces about itself; a receiver infers it when a
/// peer's advertised lease expires (#30's job, not this ticket's).
/// </summary>
public enum AvailabilityState
{
    Available,
    Idle,
    Unavailable,
}
