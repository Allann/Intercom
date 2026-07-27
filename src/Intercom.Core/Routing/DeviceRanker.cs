using Intercom.Presence;

namespace Intercom.Routing;

/// <summary>
/// Pure ranking policy for choosing which of a contact's live devices should
/// receive a new interaction (issue #30;
/// docs/research/active-device-presence.md "Estimating the most recently
/// active device" and "Routing by interaction";
/// docs/adr/0004-multi-device-contact-routing.md "Tie definition"). Consumes
/// <see cref="TrackedDevicePresence"/> records for one contact's members
/// (see <see cref="PresenceReceiver.LiveDevices"/>) — this class adds the
/// remaining eligibility/ordering rules on top of "not yet expired":
///
/// <list type="number">
/// <item>Exclude explicitly Unavailable devices.</item>
/// <item>Exclude DND devices for interrupting <see cref="InteractionKind"/>s
/// (<see cref="DndPolicy"/>); DND devices remain eligible for silent
/// <see cref="InteractionKind.Text"/>.</item>
/// <item>Prefer <see cref="AvailabilityState.Available"/> over
/// <see cref="AvailabilityState.Idle"/>.</item>
/// <item>Within the same availability, prefer the lowest advertised idle
/// bucket, then the most recently updated (this receiver's own local receive
/// time, per <see cref="TrackedDevicePresence.LastUpdatedAt"/>).</item>
/// <item>Break any remaining tie deterministically by stable
/// <see cref="TrackedDevicePresence.DeviceId"/> ordering.</item>
/// </list>
///
/// This is pure decision logic with no live audio to actually route yet
/// (#26-#29 don't exist) — it exists so the live-voice tie-break rule from
/// ADR-0004 ("Two active devices for one contact resolve a live-voice tie
/// deterministically and never both ring/play the same stream") is built and
/// fully tested now, ready for whichever future ticket wires it to a real
/// call. <see cref="DeviceRankingResult.RankedDevices"/>[0] IS that
/// deterministic single choice for live voice; <see cref="DeviceRankingResult.IsTie"/>
/// is what a text/attention-card sender uses to decide whether to widen from
/// one device to a fan-out (see <see cref="DeviceRoutingPolicy"/> and
/// <c>ChatFanoutRouter</c>/<c>AttentionCardFanoutRouter</c>).
/// </summary>
public static class DeviceRanker
{
    /// <summary>ADR-0004's tie definition: "no distinguishable recency
    /// between them within the same heartbeat window." Reuses the same
    /// 10-second cadence <see cref="PresenceEngine"/> heartbeats on (see
    /// <see cref="ControlChannel.PeerConnectionStateMachine.HeartbeatInterval"/>)
    /// — the natural resolution bound of "recency" information this system
    /// actually has.</summary>
    public static readonly TimeSpan RecencyTieWindow = TimeSpan.FromSeconds(10);

    public static DeviceRankingResult Rank(IReadOnlyList<TrackedDevicePresence> devices, InteractionKind kind, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(devices);

        var eligible = devices
            .Where(d => now < d.ExpiresAt)
            .Where(d => d.Availability != AvailabilityState.Unavailable)
            .Where(d => !DndPolicy.IsSuppressed(d.Dnd, kind))
            .OrderBy(d => d.Availability == AvailabilityState.Available ? 0 : 1)
            .ThenBy(d => IdleBucketRank(d.IdleAgeBucket))
            .ThenByDescending(d => d.LastUpdatedAt)
            .ThenBy(d => d.DeviceId)
            .ToList();

        if (eligible.Count == 0)
        {
            return new DeviceRankingResult { RankedDevices = [], IsTie = false };
        }

        var top = eligible[0];
        var tiedWithTopCount = eligible.Count(d =>
            d.Availability == top.Availability &&
            d.IdleAgeBucket == top.IdleAgeBucket &&
            (top.LastUpdatedAt - d.LastUpdatedAt).Duration() <= RecencyTieWindow);

        return new DeviceRankingResult
        {
            RankedDevices = eligible,
            IsTie = tiedWithTopCount > 1,
        };
    }

    // Unavailable devices (the only case with a null bucket) are already
    // excluded above, so the null arm below is defensive/unreachable in
    // practice, not a real ranking case.
    static int IdleBucketRank(IdleAgeBucket? bucket) => bucket switch
    {
        IdleAgeBucket.UnderTwoMinutes => 0,
        IdleAgeBucket.TwoToTenMinutes => 1,
        IdleAgeBucket.OverTenMinutes => 2,
        _ => 3,
    };
}

/// <summary>The outcome of <see cref="DeviceRanker.Rank"/>: every eligible
/// device for the requested <see cref="InteractionKind"/>, best first, plus
/// whether the top of that ordering is a genuine ADR-0004 tie.</summary>
public sealed record DeviceRankingResult
{
    public required IReadOnlyList<TrackedDevicePresence> RankedDevices { get; init; }
    public required bool IsTie { get; init; }

    /// <summary>The single deterministic pick — what live voice/Quick Chat
    /// would ring, and what text/attention-card sends first before any
    /// fan-out decision. Null when no device is eligible at all.</summary>
    public Guid? TopDeviceId => RankedDevices.Count > 0 ? RankedDevices[0].DeviceId : null;
}
