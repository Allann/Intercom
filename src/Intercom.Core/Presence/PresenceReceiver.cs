namespace Intercom.Presence;

/// <summary>
/// Pure receive-side policy for inbound <see cref="PresenceLease"/> records
/// (issue #30): tracks the latest accepted lease per remote device and
/// enforces the strictly-increasing-per-incarnation acceptance rule
/// (docs/research/active-device-presence.md: "The receiver accepts only a
/// sequence newer than the last one for the same (device_id, incarnation_id).
/// A changed incarnation resets the sequence."). No I/O, no timers of its
/// own, explicit clock passed to every call — mirrors
/// <see cref="AvailabilityPolicy"/>'s discipline exactly so this can be
/// driven deterministically from a test instead of a real socket/clock.
///
/// Acceptance rule, precisely:
/// <list type="bullet">
/// <item>No tracked record yet for this DeviceId: always accept.</item>
/// <item>Same DeviceId, DIFFERENT IncarnationId: always accept, regardless of
/// the new lease's Sequence value — even a numerically LOWER sequence than
/// what was previously tracked, because a changed incarnation resets the
/// counter from scratch. This is the subtle case the research doc calls out
/// explicitly ("process restart: new incarnation supersedes the old one;
/// never accept a late sequence from the old incarnation as current" reads
/// naturally as "reject old", but the flip side — a legitimate fresh
/// incarnation's low sequence numbers must NOT be rejected just because they
/// are numerically smaller than the old incarnation's last sequence — is
/// exactly what this branch protects.)</item>
/// <item>Same DeviceId, SAME IncarnationId: accept only if the new lease's
/// Sequence is strictly greater than the tracked one's. Anything less than
/// or equal to is stale/duplicate/out-of-order and rejected.</item>
/// </list>
///
/// Expiry is entirely local: callers (<see cref="LiveDevices"/>) compare
/// their own supplied "now" against each record's
/// <see cref="TrackedDevicePresence.ExpiresAt"/>, which was computed at
/// accept time from THIS receiver's own clock plus the lease's advertised
/// LeaseSeconds — the sender's lease_seconds field is trusted only as "how
/// long until I stop trusting this without a fresh one," never as a
/// wall-clock instant (research doc: "it does not trust the sender's wall
/// clock"). A system clock change on either side has no effect on ordering
/// (per-incarnation sequence) or on expiry (this receiver's own monotonic-ish
/// clock reading, injected exactly like every other explicit-clock policy
/// class in this codebase).
///
/// Thread safety: guarded by an internal lock, released before
/// <see cref="LeaseAccepted"/> is raised — matches this codebase's
/// established discipline (<see cref="AvailabilityPolicy"/>,
/// <c>DndSettingsStore</c>).
/// </summary>
public sealed class PresenceReceiver
{
    readonly object _gate = new();
    readonly Dictionary<Guid, DeviceState> _tracked = [];

    // Per-device bookkeeping: the current record plus every incarnation ID
    // this device has already moved PAST. IncarnationId is a random GUID
    // (docs/research/active-device-presence.md: "random value generated each
    // process start") — it carries no temporal ordering of its own, so
    // "different from the currently tracked incarnation" is NOT by itself
    // enough to decide "newer": a late packet from an incarnation this
    // receiver has already superseded must still be rejected (research doc's
    // "Process restart: new incarnation supersedes the old one; never accept
    // a late sequence from the old incarnation as current" — read literally,
    // that's about the OLD incarnation specifically, not "any incarnation
    // that differs from whatever's tracked right now"). Recording every
    // abandoned incarnation ID is what makes that distinction possible.
    sealed class DeviceState
    {
        public required TrackedDevicePresence Current { get; set; }
        public HashSet<Guid> SupersededIncarnations { get; } = [];
    }

    /// <summary>Raised whenever <see cref="Ingest"/> actually accepts a lease
    /// (never for a rejected stale/duplicate one).</summary>
    public event Action<TrackedDevicePresence>? LeaseAccepted;

    /// <summary>Attempts to record <paramref name="lease"/>, applying the
    /// staleness rule documented on the class. <paramref name="receivedAt"/>
    /// is this receiver's own clock reading at arrival — never derived from
    /// anything inside <paramref name="lease"/>. Returns true if
    /// accepted.</summary>
    public bool Ingest(PresenceLease lease, DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(lease);

        TrackedDevicePresence updated;
        lock (_gate)
        {
            var found = _tracked.TryGetValue(lease.DeviceId, out var state);

            if (found)
            {
                if (state!.Current.IncarnationId == lease.IncarnationId)
                {
                    if (lease.Sequence <= state.Current.Sequence)
                    {
                        return false; // stale or duplicate within the same incarnation
                    }
                }
                else if (state.SupersededIncarnations.Contains(lease.IncarnationId))
                {
                    return false; // a late packet from an incarnation this device already moved past
                }
                else
                {
                    // Genuinely new incarnation this receiver hasn't seen
                    // before — accept unconditionally (sequence resets), and
                    // remember the old one as superseded so a later late
                    // arrival from IT is rejected instead of mistaken for yet
                    // another fresh incarnation.
                    state.SupersededIncarnations.Add(state.Current.IncarnationId);
                }
            }

            updated = new TrackedDevicePresence
            {
                DeviceId = lease.DeviceId,
                ContactId = lease.ContactId,
                IncarnationId = lease.IncarnationId,
                Sequence = lease.Sequence,
                Availability = lease.Availability,
                Dnd = lease.Dnd,
                IdleAgeBucket = lease.IdleAgeBucket,
                Capabilities = lease.Capabilities,
                LastUpdatedAt = receivedAt,
                ExpiresAt = receivedAt + TimeSpan.FromSeconds(Math.Max(0, lease.LeaseSeconds)),
            };

            if (found)
            {
                state!.Current = updated;
            }
            else
            {
                _tracked[lease.DeviceId] = new DeviceState { Current = updated };
            }
        }

        LeaseAccepted?.Invoke(updated);
        return true;
    }

    /// <summary>The current tracked record for one device, or null if never
    /// seen (or never accepted). Does not itself consider expiry — callers
    /// wanting "live" devices should use <see cref="LiveDevices"/> or check
    /// <see cref="TrackedDevicePresence.ExpiresAt"/> directly.</summary>
    public TrackedDevicePresence? Get(Guid deviceId)
    {
        lock (_gate) { return _tracked.TryGetValue(deviceId, out var state) ? state.Current : null; }
    }

    /// <summary>Every tracked record for a device in <paramref name="deviceIds"/>
    /// whose lease has not yet expired against <paramref name="now"/> —
    /// "expired" is treated as "offline immediately for new routing"
    /// (research doc's "Stale state and failure rules"). Callers pass the
    /// device IDs of one local <c>Intercom.Contacts.Contact</c>'s members —
    /// this receiver has no notion of contacts itself (see
    /// <see cref="TrackedDevicePresence"/>'s remarks on why the wire
    /// ContactId is not used for this). Explicitly Unavailable devices ARE
    /// still included here (not yet expired, just advertising Unavailable) —
    /// excluding those is <c>Intercom.Routing.DeviceRanker</c>'s job, not
    /// this receiver's.</summary>
    public IReadOnlyList<TrackedDevicePresence> LiveDevices(IEnumerable<Guid> deviceIds, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        var wanted = new HashSet<Guid>(deviceIds);
        lock (_gate)
        {
            return _tracked.Values
                .Select(s => s.Current)
                .Where(t => wanted.Contains(t.DeviceId) && now < t.ExpiresAt)
                .ToList();
        }
    }

    /// <summary>Every tracked device, live or not — for diagnostics/UI.
    /// Removes nothing; expiry is always evaluated by the caller against a
    /// supplied "now," never mutated away here.</summary>
    public IReadOnlyList<TrackedDevicePresence> AllTracked()
    {
        lock (_gate) { return _tracked.Values.Select(s => s.Current).ToList(); }
    }
}
