using Intercom.AttentionCards;

namespace Intercom.App.AttentionCards;

public enum AttentionCardToastObservationStatus
{
    Submitted,
    Blocked,
    Failed,
}

public sealed record AttentionCardToastObservation(
    AttentionCardToastObservationStatus Status,
    AttentionCardNotificationSetting? Setting,
    Exception? Error)
{
    public bool RequiresFallback => Status == AttentionCardToastObservationStatus.Failed;

    public static async Task<AttentionCardToastObservation> ObserveAsync(
        Func<Task<AttentionCardNotificationSubmission>>? submit)
    {
        if (submit is null) return Failed();

        try
        {
            var submission = await submit();
            var status = submission.Status == AttentionCardNotificationSubmissionStatus.Submitted
                ? AttentionCardToastObservationStatus.Submitted
                : AttentionCardToastObservationStatus.Blocked;
            return new AttentionCardToastObservation(status, submission.Setting, null);
        }
        catch (Exception error)
        {
            return Failed(error);
        }
    }

    static AttentionCardToastObservation Failed(Exception? error = null) =>
        new(AttentionCardToastObservationStatus.Failed, null, error);
}
