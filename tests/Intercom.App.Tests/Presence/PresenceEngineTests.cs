using Intercom.ControlChannel;
using Intercom.Presence;
using Xunit;

namespace Intercom.App.Tests.Presence;

/// <summary>
/// Orchestration-level tests for <see cref="PresenceEngine"/>, driven by a
/// hand-written fake <see cref="IIdleTimeProvider"/> and an explicit fake
/// clock — the same pattern DiscoveryServiceTests uses for
/// IDnsServiceDiscovery. Deliberately avoids waiting on the real 30-second
/// poll timer: <see cref="PresenceEngine.Start"/> and
/// <see cref="PresenceEngine.OnSessionBecameUsable"/> both take an immediate
/// synchronous sample, which is enough to exercise every behavior here
/// without a live timer tick.
/// </summary>
public class PresenceEngineTests : IDisposable
{
    static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    readonly string _dndDir = Path.Combine(Path.GetTempPath(), "IntercomPresenceEngineTests_" + Guid.NewGuid());
    readonly List<PresenceEngine> _engines = [];

    public void Dispose()
    {
        foreach (var engine in _engines) engine.Dispose();
        if (Directory.Exists(_dndDir)) Directory.Delete(_dndDir, recursive: true);
    }

    PresenceEngine MakeEngine(FakeIdleTimeProvider idleProvider, DndSettingsStore dndSettings, DateTimeOffset now, Guid? deviceId = null)
    {
        var engine = new PresenceEngine(
            deviceId ?? Guid.NewGuid(),
            idleProvider,
            dndSettings,
            Capability.Text,
            clock: () => now);
        _engines.Add(engine);
        return engine;
    }

    DndSettingsStore MakeDndStore()
    {
        var store = new DndSettingsStore(_dndDir + Guid.NewGuid());
        store.Load();
        return store;
    }

    [Fact]
    public void Start_MarksSessionUsable_AndTakesImmediateSample_LandingOnAvailable()
    {
        var idle = new FakeIdleTimeProvider { IdleTime = TimeSpan.FromSeconds(1) };
        var engine = MakeEngine(idle, MakeDndStore(), Epoch);

        engine.Start();

        Assert.Equal(AvailabilityState.Available, engine.Availability);
    }

    [Fact]
    public void Start_ImmediateSample_LongIdle_LandsOnIdle_NotAvailable()
    {
        // A resident app can be launched into an already-idle session — the
        // very first sample must reflect that honestly, not assume Available.
        var idle = new FakeIdleTimeProvider { IdleTime = TimeSpan.FromMinutes(5) };
        var engine = MakeEngine(idle, MakeDndStore(), Epoch);

        engine.Start();

        Assert.Equal(AvailabilityState.Idle, engine.Availability);
    }

    [Fact]
    public void Start_CalledTwice_Throws()
    {
        var engine = MakeEngine(new FakeIdleTimeProvider(), MakeDndStore(), Epoch);
        engine.Start();
        Assert.Throws<InvalidOperationException>(engine.Start);
    }

    [Fact]
    public void OnQueryEndSession_ForcesUnavailable()
    {
        var idle = new FakeIdleTimeProvider { IdleTime = TimeSpan.FromSeconds(1) };
        var engine = MakeEngine(idle, MakeDndStore(), Epoch);
        engine.Start();
        Assert.Equal(AvailabilityState.Available, engine.Availability);

        engine.OnQueryEndSession();

        Assert.Equal(AvailabilityState.Unavailable, engine.Availability);
    }

    [Fact]
    public void OnSessionBecameUnusable_ForcesUnavailable()
    {
        var idle = new FakeIdleTimeProvider { IdleTime = TimeSpan.FromSeconds(1) };
        var engine = MakeEngine(idle, MakeDndStore(), Epoch);
        engine.Start();

        engine.OnSessionBecameUnusable();

        Assert.Equal(AvailabilityState.Unavailable, engine.Availability);
    }

    [Fact]
    public void OnResumedAutomatic_DoesNotReenableAvailability()
    {
        var idle = new FakeIdleTimeProvider { IdleTime = TimeSpan.FromSeconds(1) };
        var engine = MakeEngine(idle, MakeDndStore(), Epoch);
        engine.Start();
        engine.OnSessionBecameUnusable();

        engine.OnResumedAutomatic();

        Assert.Equal(AvailabilityState.Unavailable, engine.Availability);
    }

    [Fact]
    public void OnSessionBecameUsable_AfterUnusable_ReevaluatesImmediately_UsingFreshIdleTime()
    {
        var idle = new FakeIdleTimeProvider { IdleTime = TimeSpan.FromSeconds(1) };
        var engine = MakeEngine(idle, MakeDndStore(), Epoch);
        engine.Start();
        engine.OnSessionBecameUnusable();
        Assert.Equal(AvailabilityState.Unavailable, engine.Availability);

        engine.OnSessionBecameUsable();

        // A fresh, small idle-time sample promotes straight to Available —
        // it never gets stuck showing "not yet re-evaluated".
        Assert.Equal(AvailabilityState.Available, engine.Availability);
    }

    [Fact]
    public void NextLease_SequenceStrictlyIncreases_NeverRepeats()
    {
        var engine = MakeEngine(new FakeIdleTimeProvider(), MakeDndStore(), Epoch);
        engine.Start();

        var first = engine.NextLease();
        var second = engine.NextLease();
        var third = engine.NextLease();

        Assert.True(second.Sequence > first.Sequence);
        Assert.True(third.Sequence > second.Sequence);
    }

    [Fact]
    public void NextLease_SameIncarnationAcrossCalls()
    {
        var engine = MakeEngine(new FakeIdleTimeProvider(), MakeDndStore(), Epoch);
        engine.Start();

        var first = engine.NextLease();
        var second = engine.NextLease();

        Assert.Equal(first.IncarnationId, second.IncarnationId);
        Assert.Equal(engine.IncarnationId, first.IncarnationId);
    }

    [Fact]
    public void NextLease_TwoEngines_GetDifferentIncarnations()
    {
        var e1 = MakeEngine(new FakeIdleTimeProvider(), MakeDndStore(), Epoch);
        var e2 = MakeEngine(new FakeIdleTimeProvider(), MakeDndStore(), Epoch);

        Assert.NotEqual(e1.IncarnationId, e2.IncarnationId);
    }

    [Fact]
    public void NextLease_Unavailable_OmitsIdleAgeBucket()
    {
        var idle = new FakeIdleTimeProvider { IdleTime = TimeSpan.FromSeconds(1) };
        var engine = MakeEngine(idle, MakeDndStore(), Epoch);
        engine.Start();
        engine.OnSessionBecameUnusable();

        var lease = engine.NextLease();

        Assert.Equal(AvailabilityState.Unavailable, lease.Availability);
        Assert.Null(lease.IdleAgeBucket);
    }

    [Fact]
    public void NextLease_Available_IncludesUnderTwoMinutesBucket()
    {
        var idle = new FakeIdleTimeProvider { IdleTime = TimeSpan.FromSeconds(1) };
        var engine = MakeEngine(idle, MakeDndStore(), Epoch);
        engine.Start();

        var lease = engine.NextLease();

        Assert.Equal(IdleAgeBucket.UnderTwoMinutes, lease.IdleAgeBucket);
    }

    [Fact]
    public void NextLease_ReflectsDndFromStore()
    {
        var dnd = MakeDndStore();
        var engine = MakeEngine(new FakeIdleTimeProvider(), dnd, Epoch);
        engine.Start();

        Assert.False(engine.NextLease().Dnd);

        dnd.Toggle();

        Assert.True(engine.NextLease().Dnd);
    }

    [Fact]
    public void NextLease_DeviceIdAndContactId_AreTheSameValue()
    {
        // #23 scoping decision — see PresenceLease's remarks: no real
        // contact-grouping concept exists on the sender side until #30.
        var deviceId = Guid.NewGuid();
        var engine = MakeEngine(new FakeIdleTimeProvider(), MakeDndStore(), Epoch, deviceId);
        engine.Start();

        var lease = engine.NextLease();

        Assert.Equal(deviceId, lease.DeviceId);
        Assert.Equal(deviceId, lease.ContactId);
    }

    [Fact]
    public void NextLease_LeaseSecondsIsThirty()
    {
        var engine = MakeEngine(new FakeIdleTimeProvider(), MakeDndStore(), Epoch);
        engine.Start();

        Assert.Equal(30, engine.NextLease().LeaseSeconds);
    }

    [Fact]
    public void MeaningfulStateChanged_RaisedOnAvailabilityTransition()
    {
        var idle = new FakeIdleTimeProvider { IdleTime = TimeSpan.FromSeconds(1) };
        var engine = MakeEngine(idle, MakeDndStore(), Epoch);
        var raised = 0;
        engine.MeaningfulStateChanged += () => raised++;

        engine.Start(); // Unavailable -> Idle -> Available: at least one transition

        Assert.True(raised >= 1);
    }

    [Fact]
    public void MeaningfulStateChanged_RaisedOnDndToggle()
    {
        var dnd = MakeDndStore();
        var engine = MakeEngine(new FakeIdleTimeProvider(), dnd, Epoch);
        engine.Start();
        var raised = 0;
        engine.MeaningfulStateChanged += () => raised++;

        dnd.Toggle();

        Assert.Equal(1, raised);
    }

    sealed class FakeIdleTimeProvider : IIdleTimeProvider
    {
        public TimeSpan IdleTime { get; set; } = TimeSpan.Zero;
        public TimeSpan GetIdleTime() => IdleTime;
    }
}
