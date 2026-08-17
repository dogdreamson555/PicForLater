using PicForLater.Core.Images;

namespace PicForLater.Core.Reminders;

public enum EntityCandidateKind
{
    DateTime = 1,
    Location = 2,
}

public enum EntityCandidateSource
{
    Metadata = 1,
    Ocr = 2,
    Model = 3,
}

public enum EntityCandidateStatus
{
    Pending = 1,
    Confirmed = 2,
    Dismissed = 3,
}

public enum ReminderState
{
    Active = 1,
    Completed = 2,
    SuspendedByDeletion = 3,
    Missed = 4,
    NeedsReconfirmation = 5,
}

public enum ReminderNotificationState
{
    Pending = 1,
    Scheduled = 2,
    Failed = 3,
    Cancelled = 4,
    Activated = 5,
}

public sealed record ReminderCandidate(
    Guid Id,
    Guid ImageItemId,
    string ImageTitle,
    EntityCandidateKind Kind,
    string RawText,
    string? NormalizedValue,
    string Evidence,
    EntityCandidateSource Source,
    string? BoundingBoxJson,
    DateTimeOffset? ReferenceTimeUtc,
    string? TimeZoneId,
    string? AmbiguityReason,
    DateTimeOffset GeneratedAtUtc)
{
    public Guid? SuggestedLocationCandidateId { get; init; }

    public string? SuggestedLocation { get; init; }

    public string? SuggestedLocationEvidence { get; init; }

    public ManagedRelativePath? PreviewRelativePath { get; init; }
}

public sealed record Reminder(
    Guid Id,
    Guid ImageItemId,
    string ImageTitle,
    DateTimeOffset DueAtUtc,
    string TimeZoneId,
    string? ConfirmedLocation,
    string SchedulerId,
    ReminderState State,
    ReminderNotificationState NotificationState,
    string? NotificationLastErrorCode,
    string? CompletionReason,
    DateTimeOffset? ActivatedAtUtc,
    DateTimeOffset? LastReconciledAtUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    public ManagedRelativePath? PreviewRelativePath { get; init; }
}

public sealed record ReminderConfirmation(
    Guid ImageItemId,
    Guid? DateCandidateId,
    Guid? LocationCandidateId,
    string ImageTitle,
    DateTime LocalDueDateTime,
    string TimeZoneId,
    string? ConfirmedLocation);

public sealed record ReminderUpdate(
    Guid ReminderId,
    string ImageTitle,
    DateTime LocalDueDateTime,
    string TimeZoneId,
    string? ConfirmedLocation);

public sealed record ReminderReconciliationResult(
    int MissedCount,
    int ScheduledCount,
    int CancelledCount,
    int FailedCount,
    bool NotificationsSupported);

public sealed record ReminderNotification(
    string SchedulerId,
    Guid ReminderId,
    Guid ImageItemId,
    DateTimeOffset DueAtUtc,
    string Title,
    string Body,
    string? Location);
