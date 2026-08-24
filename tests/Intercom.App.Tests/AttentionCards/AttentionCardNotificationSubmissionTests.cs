using Intercom.AttentionCards;
using Xunit;

namespace Intercom.App.Tests.AttentionCards;

public sealed class AttentionCardNotificationSubmissionTests
{
    [Fact]
    public void Enabled_IsSubmittedOnWindows10AndWindows11()
    {
        var result = AttentionCardNotificationSubmission.From(AttentionCardNotificationSetting.Enabled);

        Assert.Equal(AttentionCardNotificationSubmissionStatus.Submitted, result.Status);
        Assert.Equal(AttentionCardNotificationSetting.Enabled, result.Setting);
    }

    [Theory]
    [InlineData(AttentionCardNotificationSetting.DisabledForApplication)]
    [InlineData(AttentionCardNotificationSetting.DisabledForUser)]
    [InlineData(AttentionCardNotificationSetting.DisabledByGroupPolicy)]
    [InlineData(AttentionCardNotificationSetting.DisabledByManifest)]
    [InlineData(AttentionCardNotificationSetting.Unsupported)]
    public void WindowsNotificationRestriction_IsBlockedWithExactReason(
        AttentionCardNotificationSetting setting)
    {
        var result = AttentionCardNotificationSubmission.From(setting);

        Assert.Equal(AttentionCardNotificationSubmissionStatus.Blocked, result.Status);
        Assert.Equal(setting, result.Setting);
    }
}
