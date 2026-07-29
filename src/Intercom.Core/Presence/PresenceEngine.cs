using Intercom.ControlChannel;

namespace Intercom.Presence;

/// <summary>
/// Orchestrates presence for this device end to end: owns the pure
/// <see cref="AvailabilityPolicy"/>, drives it from the injected
/// <see cref="IIdleTimeProvider"/> on a 30-second poll
/// (docs/adr/0004-multi-device-contact-routing.md) and from session/power/
/// shutdown signals forwarded by the caller (this class has no Win32
/// dependency itself — see <c>Intercom.App.Presence.SessionMessagePump</c>
/// for the real signal source), tracks DND via the injected
/// <see cref="DndSettingsStore"/>, and mints fresh <see cref="PresenceLease"/>
/// snapshots (device incarnation + strictly increasing sequence) for
/// <see cref="PeerControlChannel"/> to broadcast.
///
/// Mirrors <c>Intercom.Discovery.DiscoveryService</c>'s composition shape:
/// policy (<see cref="AvailabilityPolicy"/>) is pure and separately tested;
/// this class is the "impure" driver — tested here with fakes for the clock
/// and idle-time provider, the same split <see cref="PeerControlChannel"/>
/// uses for its own <c>PeerConnectionStateMachine</c>.
/// </summary>
public sealed class PresenceEngine : IDisposable
{
    /// <summary>docs/adr/0004-multi-device-contact-routing.md: idle state
    /// polled locally every 30 seconds.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    /// <summary>docs/research/active-device-presence.md: lease valid for 30
    /// seconds.</summary>
    public const int LeaseSeconds = 30;

    readonly AvailabilityPolicy _policy = new();
    readonly IIdleTimeProvider _idleTimeProvider;
    readonly DndSettingsStore _dndSettings;
    readonly Func<DateTimeOffset> _clock;
    readonly Guid _deviceId;
    readonly Guid _incarnationId;
    readonly Capability _capabilities;

    // Guards _lastIdleAge/_pollTimer/_disposed. Never held across the
    // (blocking, native-call-backed) IIdleTimeProvider.GetIdleTime() —
    // PollNow reads it before taking the lock, mirroring
    // PeerControlChannel's "never hold _gate across a blocking call"
    // discipline.
    readonly object _gate = new();
    TimeSpan _lastIdleAge = TimeSpan.Zero;
    long _sequence;
    Timer? _pollTimer;
    bool _started;
    bool _disposed;

    public AvailabilityState Availability => _policy.State;
    public bool Dnd => _dndSettings.DndEnabled;
    public Guid IncarnationId => _incarnationId;

    /// <summary>Raised whenever availability or DND actually changes — the
    /// signal a live <see cref="PeerControlChannel"/> roster should react to
    /// by calling <see cref="PeerControlChannel.NotifyPresenceChanged"/> for
    /// an immediate out-of-cadence update (docs/research/active-device-
    /// presence.md: "immediate update on meaningful state changes"). No
    /// payload — read <see cref="Availability"/>/<see cref="Dnd"/> and call
    /// <see cref="NextLease"/> for the fresh value.</summary>
    public event Action? MeaningfulStateChanged;

    public PresenceEngine(
        Guid deviceId,
        IIdleTimeProvider idleTimeProvider,
        DndSettingsStore dndSettings,
        Capability capabilities,
        Func<DateTimeOffset>? clock = null)
    {
        _deviceId = deviceId;
        _idleTimeProvider = idleTimeProvider;
        _dndSettings = dndSettings;
        _capabilities = capabilities;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _incarnationId = Guid.NewGuid();

        _policy.StateChanged += (_, _) => MeaningfulStateChanged?.Invoke();
        _dndSettings.Changed += _ => MeaningfulStateChanged?.Invoke();
    }

    /// <summary>Starts the 30-second idle poll. Call once. The app is
    /// running with a usable session at launch by definition (there is no
    /// locked/disconnected state to recover from at process start), so this
    /// marks the session usable and takes an immediate sample rather than
    /// leaving the engine at its initial Unavailable resting state for up to
    /// 30 seconds.</summary>
    public void Start()
    {
        if (_started) throw new InvalidOperationException("PresenceEngine.Start must only be called once.");
        _started = true;

        _policy.SessionBecameUsable(_clock());
        PollNow();

        _pollTimer = new Timer(_ => PollNow(), null, PollInterval, PollInterval);
    }

    void PollNow()
    {
        var idleAge = _idleTimeProvider.GetIdleTime();
        lock (_gate) { _lastIdleAge = idleAge; }
        _policy.Poll(idleAge, _clock());
    }

    // ---- session/power/shutdown signals, forwarded by the real Win32 signal source ----

    /// <summary>Session lock, disconnect (console or remote), or
    /// logoff.</summary>
    public void OnSessionBecameUnusable() => _policy.SessionBecameUnusable(_clock());

    /// <summary>PBT_APMSUSPEND: the system is suspending.</summary>
    public void OnSuspending() => _policy.SessionBecameUnusable(_clock());

    /// <summary>WM_QUERYENDSESSION: mark unavailable immediately. The
    /// message-pump layer is responsible for returning TRUE to Windows right
    /// away without waiting on anything this method does — this call itself
    /// does no I/O and cannot block that (see
    /// <c>Intercom.App.Presence.SessionMessagePump</c>).</summary>
    public void OnQueryEndSession() => _policy.SessionBecameUnusable(_clock());

    /// <summary>PBT_APMRESUMEAUTOMATIC: deliberately NOT a re-enable signal
    /// (docs/research/active-device-presence.md: "On PBT_APMRESUMEAUTOMATIC,
    /// keep the device unavailable while sockets/discovery are
    /// re-established") — a no-op; the device stays Unavailable until a real
    /// re-enable signal arrives (unlock, desktop-ready, or user-triggered
    /// resume).</summary>
    public void OnResumedAutomatic() { /* intentionally no-op — see remarks */ }

    /// <summary>Unlock, desktop-ready, or a user-triggered resume
    /// (PBT_APMRESUMESUSPEND) — re-evaluates from scratch: marks the session
    /// usable, then immediately takes a fresh idle-time sample rather than
    /// assuming Available (research doc: "triggers a fresh input/state
    /// evaluation rather than blindly declaring it active").</summary>
    public void OnSessionBecameUsable()
    {
        _policy.SessionBecameUsable(_clock());
        PollNow();
    }

    /// <summary>Builds a fresh, immutable snapshot with a newly incremented
    /// sequence number (strictly increasing within <see cref="IncarnationId"/>
    /// — see <see cref="PresenceLease.Sequence"/>'s remarks on why the
    /// sender must never reuse or decrement it). Safe to call from any
    /// thread; a live <see cref="PeerControlChannel"/> calls this once per
    /// outbound presence send.</summary>
    public PresenceLease NextLease()
    {
        var availability = _policy.State;
        TimeSpan idleAge;
        lock (_gate) { idleAge = _lastIdleAge; }

        return new PresenceLease
        {
            DeviceId = _deviceId,
            ContactId = _deviceId, // grouping is local-only; see PresenceLease
            IncarnationId = _incarnationId,
            Sequence = (ulong)Interlocked.Increment(ref _sequence),
            Availability = availability,
            Dnd = _dndSettings.DndEnabled,
            IdleAgeBucket = availability == AvailabilityState.Unavailable
                ? null
                : IdleAgeBucketing.Bucket(idleAge),
            LeaseSeconds = LeaseSeconds,
            Capabilities = _capabilities,
        };
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pollTimer?.Dispose();
        }
    }
}
