namespace Intercom.AttentionCards;

public enum AttentionCardNotificationSetting
{
    Enabled,
    DisabledForApplication,
    DisabledForUser,
    DisabledByGroupPolicy,
    DisabledByManifest,
    Unsupported,
}

public enum AttentionCardNotificationSubmissionStatus
{
    Submitted,
    Blocked,
}

public readonly record struct AttentionCardNotificationSubmission(
    AttentionCardNotificationSubmissionStatus Status,
    AttentionCardNotificationSetting Setting)
{
    public static AttentionCardNotificationSubmission From(AttentionCardNotificationSetting setting) =>
        new(
            setting == AttentionCardNotificationSetting.Enabled
                ? AttentionCardNotificationSubmissionStatus.Submitted
                : AttentionCardNotificationSubmissionStatus.Blocked,
            setting);
}
