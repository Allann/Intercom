using Intercom.Pairing;
using Xunit;

namespace Intercom.App.Tests.Pairing;

/// <summary>Tests for ADR-0002's two rate-limit counters: 5 attempts per
/// source per 10 minutes, 20 attempts globally per 10 minutes.</summary>
public class PairingRateLimiterTests
{
    static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FirstAttempt_IsAllowed()
    {
        var limiter = new PairingRateLimiter();

        var allowed = limiter.TryRecordAttempt("source-a", Epoch);

        Assert.True(allowed);
    }

    [Fact]
    public void SixthAttemptFromSameSource_WithinWindow_IsRejected()
    {
        var limiter = new PairingRateLimiter();
        for (var i = 0; i < PairingRateLimiter.MaxAttemptsPerSource; i++)
        {
            Assert.True(limiter.TryRecordAttempt("source-a", Epoch.AddSeconds(i)));
        }

        var sixth = limiter.TryRecordAttempt("source-a", Epoch.AddSeconds(10));

        Assert.False(sixth);
    }

    [Fact]
    public void PerSourceLimit_DoesNotAffectADifferentSource()
    {
        var limiter = new PairingRateLimiter();
        for (var i = 0; i < PairingRateLimiter.MaxAttemptsPerSource; i++)
        {
            limiter.TryRecordAttempt("source-a", Epoch.AddSeconds(i));
        }

        var otherSource = limiter.TryRecordAttempt("source-b", Epoch);

        Assert.True(otherSource);
    }

    [Fact]
    public void PerSourceLimit_ResetsAfterWindowElapses()
    {
        var limiter = new PairingRateLimiter();
        for (var i = 0; i < PairingRateLimiter.MaxAttemptsPerSource; i++)
        {
            limiter.TryRecordAttempt("source-a", Epoch.AddSeconds(i));
        }

        var afterWindow = limiter.TryRecordAttempt("source-a", Epoch + PairingRateLimiter.Window + TimeSpan.FromSeconds(1));

        Assert.True(afterWindow);
    }

    [Fact]
    public void TwentyFirstGlobalAttempt_AcrossManySources_IsRejected()
    {
        var limiter = new PairingRateLimiter();
        for (var i = 0; i < PairingRateLimiter.MaxAttemptsGlobal; i++)
        {
            // Spread across distinct sources so the per-source limit (5) is
            // never what blocks these — only the global 20 should matter.
            var source = $"source-{i % 5}-batch-{i / 5}";
            Assert.True(limiter.TryRecordAttempt(source, Epoch.AddSeconds(i)));
        }

        var twentyFirst = limiter.TryRecordAttempt("source-fresh", Epoch.AddSeconds(100));

        Assert.False(twentyFirst);
    }

    [Fact]
    public void GlobalLimit_ResetsAfterWindowElapses()
    {
        var limiter = new PairingRateLimiter();
        for (var i = 0; i < PairingRateLimiter.MaxAttemptsGlobal; i++)
        {
            var source = $"source-{i % 5}-batch-{i / 5}";
            limiter.TryRecordAttempt(source, Epoch.AddSeconds(i));
        }

        var afterWindow = limiter.TryRecordAttempt("source-fresh", Epoch + PairingRateLimiter.Window + TimeSpan.FromSeconds(1));

        Assert.True(afterWindow);
    }

    [Fact]
    public void RejectedAttempt_IsNotRecorded_DoesNotConsumeASlot()
    {
        var limiter = new PairingRateLimiter();
        for (var i = 0; i < PairingRateLimiter.MaxAttemptsPerSource; i++)
        {
            limiter.TryRecordAttempt("source-a", Epoch.AddSeconds(i));
        }

        // This attempt is rejected...
        limiter.TryRecordAttempt("source-a", Epoch.AddSeconds(10));
        // ...so the count for source-a must still be exactly at the limit,
        // not one over it — verified indirectly: moving just past the
        // window from the LAST successful attempt (not the rejected one)
        // should free up a slot.
        var afterWindowFromLastSuccess = limiter.TryRecordAttempt(
            "source-a", Epoch.AddSeconds(PairingRateLimiter.MaxAttemptsPerSource - 1) + PairingRateLimiter.Window + TimeSpan.FromSeconds(1));

        Assert.True(afterWindowFromLastSuccess);
    }
}
