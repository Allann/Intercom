using Intercom.Presence;

namespace Intercom.Routing;

/// <summary>
/// Resolves the actual routing decision for one contact given its live
/// device set: applies the manual "prefer this device" hard override
/// (docs/adr/0004-multi-device-contact-routing.md "Manual device override")
/// FIRST, only falling back to <see cref="DeviceRanker.Rank"/> when no
/// override is set or the overridden device itself is no longer eligible.
///
/// Pure — takes the override as a plain <see cref="Guid"/>? value, never
/// touches <see cref="ManualOverrideStore"/> itself, so persisting an
/// auto-lapse (<see cref="DeviceRoutingDecision.OverrideLapsed"/>) stays the
/// caller's job. This mirrors the pure-policy/impure-store split every other
/// pair in this codebase uses (<see cref="AvailabilityPolicy"/> vs.
/// <see cref="PresenceEngine"/>).
///
/// Override semantics, precisely (ADR-0004: "acting as a hard override that
/// outranks activity-based ranking entirely for that contact, not merely
/// another ranking input"):
/// <list type="bullet">
/// <item>An override whose target device is still live and not Unavailable
/// wins outright — the result's <c>RankedDevices</c> is exactly that one
/// device, and normal ranking is not even computed for the pick.</item>
/// <item>An override whose target device is no longer tracked, has expired,
/// or is now Unavailable "auto-lapses" (ADR-0004) — this call falls through
/// to normal ranking, and <see cref="DeviceRoutingDecision.OverrideLapsed"/>
/// is set so the caller clears the persisted override rather than
/// re-attempting it on every future send.</item>
/// <item>An override whose target device IS still live but DND-suppressed
/// for this specific interrupting <see cref="InteractionKind"/> is NOT a
/// lapse — DND is a transient, per-interaction interruption preference
/// (docs/research/active-device-presence.md: "DND always beats recent
/// activity for voice/chime routing"), not a statement that the device
/// itself is unavailable. This call simply falls through to ranking for THIS
/// send only; the override remains persisted for next time.</item>
/// </list>
/// </summary>
public static class DeviceRoutingPolicy
{
    public static DeviceRoutingDecision ResolveTarget(
        Guid? overrideDeviceId,
        IReadOnlyList<TrackedDevicePresence> devices,
        InteractionKind kind,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(devices);

        if (overrideDeviceId is Guid deviceId)
        {
            var overridden = devices.FirstOrDefault(d => d.DeviceId == deviceId);
            var overrideDeviceGone = overridden is null || now >= overridden.ExpiresAt || overridden.Availability == AvailabilityState.Unavailable;

            if (overrideDeviceGone)
            {
                var fallback = DeviceRanker.Rank(devices, kind, now);
                return new DeviceRoutingDecision
                {
                    RankedDevices = fallback.RankedDevices,
                    IsTie = fallback.IsTie,
                    IsOverridden = false,
                    OverrideLapsed = true,
                };
            }

            if (!DndPolicy.IsSuppressed(overridden!.Dnd, kind))
            {
                // Hard override wins outright — bypass ranking entirely.
                return new DeviceRoutingDecision
                {
                    RankedDevices = [overridden],
                    IsTie = false,
                    IsOverridden = true,
                    OverrideLapsed = false,
                };
            }

            // DND-suppressed for this interaction only — not a lapse, just
            // fall through to ranking below for this one send.
        }

        var ranked = DeviceRanker.Rank(devices, kind, now);
        return new DeviceRoutingDecision
        {
            RankedDevices = ranked.RankedDevices,
            IsTie = ranked.IsTie,
            IsOverridden = false,
            OverrideLapsed = false,
        };
    }
}

/// <summary>The outcome of <see cref="DeviceRoutingPolicy.ResolveTarget"/>.</summary>
public sealed record DeviceRoutingDecision
{
    public required IReadOnlyList<TrackedDevicePresence> RankedDevices { get; init; }
    public required bool IsTie { get; init; }
    public required bool IsOverridden { get; init; }
    public required bool OverrideLapsed { get; init; }
    public Guid? TopDeviceId => RankedDevices.Count > 0 ? RankedDevices[0].DeviceId : null;
}
