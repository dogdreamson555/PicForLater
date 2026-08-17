namespace PicForLater.Core.Reminders;

public interface IReminderService
{
    Task<IReadOnlyList<ReminderCandidate>> GetPendingCandidatesAsync(
        int offset = 0,
        int limit = 100,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Reminder>> GetRemindersAsync(
        int offset = 0,
        int limit = 100,
        CancellationToken cancellationToken = default);

    Task<Reminder> ConfirmAsync(
        ReminderConfirmation confirmation,
        CancellationToken cancellationToken = default);

    Task<Reminder> UpdateAsync(
        ReminderUpdate update,
        CancellationToken cancellationToken = default);

    Task DismissCandidateAsync(
        Guid candidateId,
        CancellationToken cancellationToken = default);

    Task CancelAsync(
        Guid reminderId,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        Guid reminderId,
        CancellationToken cancellationToken = default);

    Task<bool> MarkActivatedAsync(
        Guid reminderId,
        Guid imageItemId,
        CancellationToken cancellationToken = default);

    Task<ReminderReconciliationResult> ReconcileAsync(
        CancellationToken cancellationToken = default);
}

public interface IReminderNotificationScheduler
{
    bool IsSupported { get; }

    Task<IReadOnlySet<string>> GetScheduledIdsAsync(
        CancellationToken cancellationToken = default);

    Task ScheduleAsync(
        ReminderNotification notification,
        CancellationToken cancellationToken = default);

    Task CancelAsync(
        string schedulerId,
        CancellationToken cancellationToken = default);
}

public interface IReminderOutboxNotifier
{
    void Notify();
}

public sealed class ReminderValidationException : Exception
{
    public ReminderValidationException(string errorCode)
        : base("The reminder could not be confirmed.")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}
