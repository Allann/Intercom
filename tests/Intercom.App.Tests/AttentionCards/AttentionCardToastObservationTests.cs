using Intercom.App.AttentionCards;
using Intercom.AttentionCards;
using Xunit;

namespace Intercom.App.Tests.AttentionCards;

public sealed class AttentionCardToastObservationTests
{
    [Theory]
    [InlineData(AttentionCardNotificationSetting.Enabled)]
    public async Task ObserveAsync_ReportsSubmittedAtRuntime(AttentionCardNotificationSetting setting)
    {
        var observation = await AttentionCardToastObservation.ObserveAsync(() =>
            Task.FromResult(AttentionCardNotificationSubmission.From(setting)));

        Assert.Equal(AttentionCardToastObservationStatus.Submitted, observation.Status);
        Assert.Equal(setting, observation.Setting);
        Assert.Null(observation.Error);
        Assert.False(observation.RequiresFallback);
    }

    [Theory]
    [InlineData(AttentionCardNotificationSetting.DisabledForApplication)]
    [InlineData(AttentionCardNotificationSetting.DisabledForUser)]
    [InlineData(AttentionCardNotificationSetting.DisabledByGroupPolicy)]
    [InlineData(AttentionCardNotificationSetting.DisabledByManifest)]
    [InlineData(AttentionCardNotificationSetting.Unsupported)]
    public async Task ObserveAsync_ReportsEveryBlockedSettingAtRuntime(AttentionCardNotificationSetting setting)
    {
        var observation = await AttentionCardToastObservation.ObserveAsync(() =>
            Task.FromResult(AttentionCardNotificationSubmission.From(setting)));

        Assert.Equal(AttentionCardToastObservationStatus.Blocked, observation.Status);
        Assert.Equal(setting, observation.Setting);
        Assert.Null(observation.Error);
        Assert.False(observation.RequiresFallback);
    }

    [Fact]
    public async Task ObserveAsync_RequiresFallbackWhenPresenterIsUnavailable()
    {
        var observation = await AttentionCardToastObservation.ObserveAsync(null);

        Assert.Equal(AttentionCardToastObservationStatus.Failed, observation.Status);
        Assert.Null(observation.Setting);
        Assert.Null(observation.Error);
        Assert.True(observation.RequiresFallback);
    }

    [Fact]
    public async Task ObserveAsync_RequiresFallbackAndKeepsSubmissionError()
    {
        var expected = new InvalidOperationException("native submission failed");

        var observation = await AttentionCardToastObservation.ObserveAsync(
            () => Task.FromException<AttentionCardNotificationSubmission>(expected));

        Assert.Equal(AttentionCardToastObservationStatus.Failed, observation.Status);
        Assert.Null(observation.Setting);
        Assert.Same(expected, observation.Error);
        Assert.True(observation.RequiresFallback);
    }
}
