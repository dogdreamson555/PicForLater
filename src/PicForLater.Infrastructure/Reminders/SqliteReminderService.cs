using System.Globalization;
using Microsoft.Data.Sqlite;
using PicForLater.Core.Images;
using PicForLater.Core.Reminders;
using PicForLater.Infrastructure.Storage;

namespace PicForLater.Infrastructure.Reminders;

public sealed class SqliteReminderService :
    IReminderService,
    IReminderOutboxNotifier,
    IDisposable
{
    private const int MaximumLocationLength = 300;
    private const int MaximumTitleLength = 300;
    private const int MaximumOutboxAttempts = 5;
    private static readonly TimeSpan MissedDeliveryWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaximumWorkerWait = TimeSpan.FromDays(1);
    private static readonly TimeSpan DefaultCandidateTime = TimeSpan.FromHours(10);
    private static readonly string[] CandidateDateTimeFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss.FFFFFFF",
        "yyyy/M/d H:mm",
        "yyyy/M/d H:mm:ss",
        "yyyy.M.d H:mm",
        "yyyy.M.d H:mm:ss",
    ];
    private static readonly string[] CandidateDateFormats =
        ["yyyy-MM-dd", "yyyy/M/d", "yyyy.M.d"];
    private static readonly string[] AmbiguousDateOrderFormats =
        ["M/d/yyyy", "d/M/yyyy"];

    private readonly AppDataPaths _paths;
    private readonly IReminderNotificationScheduler _scheduler;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _reconciliationGate = new(1, 1);
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private bool _disposed;

    public SqliteReminderService(
        AppDataPaths paths,
        IReminderNotificationScheduler scheduler,
        TimeProvider? timeProvider = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await ReconcileAsync(cancellationToken).ConfigureAwait(false);
        while (true)
        {
            await WaitForNextReconciliationAsync(cancellationToken).ConfigureAwait(false);
            await ReconcileAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void Notify()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_wakeSignal.CurrentCount == 0)
        {
            _wakeSignal.Release();
        }
    }

    private async Task WaitForNextReconciliationAsync(CancellationToken cancellationToken)
    {
        var nextWakeAtUtc = await GetNextWakeAtUtcAsync(cancellationToken).ConfigureAwait(false);
        if (nextWakeAtUtc is null)
        {
            await _wakeSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var delay = nextWakeAtUtc.Value - _timeProvider.GetUtcNow();
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        delay = delay > MaximumWorkerWait ? MaximumWorkerWait : delay;
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var signalTask = _wakeSignal.WaitAsync(waitCancellation.Token);
        var timerTask = Task.Delay(delay, _timeProvider, waitCancellation.Token);
        var completed = await Task.WhenAny(signalTask, timerTask).ConfigureAwait(false);
        await waitCancellation.CancelAsync().ConfigureAwait(false);
        await completed.ConfigureAwait(false);
    }

    private async Task<DateTimeOffset?> GetNextWakeAtUtcAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                (SELECT MIN(NotBeforeUtc)
                 FROM ReminderNotificationOutbox
                 WHERE State IN (1, 4) AND AttemptCount < @maximumAttempts),
                (SELECT MIN(DueAtUtc)
                 FROM Reminders
                 WHERE State = 1);
            """;
        command.Parameters.AddWithValue("@maximumAttempts", MaximumOutboxAttempts);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        DateTimeOffset? nextOutbox = reader.IsDBNull(0)
            ? null
            : ParseDate(reader.GetString(0));
        DateTimeOffset? nextMissedCheck = reader.IsDBNull(1)
            ? null
            : ParseDate(reader.GetString(1)).Add(MissedDeliveryWindow);
        if (nextOutbox is null)
        {
            return nextMissedCheck;
        }

        if (nextMissedCheck is null)
        {
            return nextOutbox;
        }

        return nextOutbox <= nextMissedCheck ? nextOutbox : nextMissedCheck;
    }

    public async Task<IReadOnlyList<ReminderCandidate>> GetPendingCandidatesAsync(
        int offset = 0,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        ValidatePage(offset, limit);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<ReminderCandidate>();
        var actionableSeen = 0;
        var rawOffset = 0;
        const int batchSize = 200;
        var nowUtc = _timeProvider.GetUtcNow();
        while (results.Count < limit)
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT c.Id, c.ImageItemId, i.Title, c.Kind, c.RawText,
                       c.NormalizedValue, c.Evidence, c.Source, c.BoundingBoxJson,
                       c.ReferenceTimeUtc, c.TimeZoneId, c.AmbiguityReason,
                       c.GeneratedAtUtc,
                       location.Id, COALESCE(location.NormalizedValue, location.RawText),
                       location.Evidence,
                       COALESCE(a.ThumbnailRelativePath, a.OriginalRelativePath)
                FROM EntityCandidates c
                INNER JOIN ImageItems i ON i.Id = c.ImageItemId
                INNER JOIN ImageAssets a ON a.Id = i.AssetId
                LEFT JOIN EntityCandidates location
                  ON location.Id = (
                      SELECT locationCandidate.Id
                      FROM EntityCandidates locationCandidate
                      WHERE locationCandidate.ImageItemId = c.ImageItemId
                        AND locationCandidate.Kind = 'Location'
                        AND locationCandidate.CandidateStatus = 1
                      ORDER BY
                        CASE locationCandidate.Source
                            WHEN 'Metadata' THEN 1
                            WHEN 'Ocr' THEN 2
                            ELSE 3
                        END,
                        locationCandidate.GeneratedAtUtc DESC
                      LIMIT 1)
                WHERE c.Kind = 'DateTime'
                  AND c.CandidateStatus = 1
                  AND i.DeletedAtUtc IS NULL
                ORDER BY c.GeneratedAtUtc DESC, i.CreatedAtUtc DESC, c.Id
                LIMIT @batchSize OFFSET @rawOffset;
                """;
            command.Parameters.AddWithValue("@batchSize", batchSize);
            command.Parameters.AddWithValue("@rawOffset", rawOffset);
            var rowsRead = 0;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rowsRead++;
                var candidate = NormalizeMissingYearProjection(ReadCandidate(reader));
                if (!IsActionableCandidate(candidate, nowUtc))
                {
                    continue;
                }

                if (actionableSeen++ < offset)
                {
                    continue;
                }

                results.Add(candidate);
                if (results.Count == limit)
                {
                    break;
                }
            }

            if (rowsRead < batchSize)
            {
                break;
            }

            rawOffset += rowsRead;
        }

        return results;
    }

    public async Task<IReadOnlyList<Reminder>> GetRemindersAsync(
        int offset = 0,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        ValidatePage(offset, limit);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT {ReminderColumns}
            FROM Reminders r
            INNER JOIN ImageItems i ON i.Id = r.ImageItemId
            INNER JOIN ImageAssets a ON a.Id = i.AssetId
            ORDER BY
                CASE r.State WHEN 4 THEN 1 WHEN 5 THEN 2 WHEN 1 THEN 3 ELSE 4 END,
                r.DueAtUtc, r.Id
            LIMIT @limit OFFSET @offset;
            """;
        command.Parameters.AddWithValue("@limit", limit);
        command.Parameters.AddWithValue("@offset", offset);
        var results = new List<Reminder>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadReminder(reader));
        }

        return results;
    }

    public async Task<Reminder> ConfirmAsync(
        ReminderConfirmation confirmation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        var now = _timeProvider.GetUtcNow();
        var dueAtUtc = ValidateAndConvertDueTime(
            confirmation.LocalDueDateTime,
            confirmation.TimeZoneId,
            now);
        var title = NormalizeTitle(confirmation.ImageTitle);
        var location = NormalizeLocation(confirmation.ConfirmedLocation);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (confirmation.DateCandidateId is Guid candidateId)
        {
            var existingReminderId = await GetConfirmedReminderIdAsync(
                connection,
                transaction,
                candidateId,
                confirmation.ImageItemId,
                cancellationToken).ConfigureAwait(false);
            if (existingReminderId is Guid existingId)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return await GetReminderAsync(existingId, cancellationToken).ConfigureAwait(false)
                    ?? throw new KeyNotFoundException("The confirmed reminder no longer exists.");
            }
        }

        await EnsureActiveImageAsync(
            connection,
            transaction,
            confirmation.ImageItemId,
            cancellationToken).ConfigureAwait(false);
        if (confirmation.DateCandidateId is Guid dateCandidateId)
        {
            await EnsureCandidateAsync(
                connection,
                transaction,
                dateCandidateId,
                confirmation.ImageItemId,
                "DateTime",
                cancellationToken).ConfigureAwait(false);
        }

        if (confirmation.LocationCandidateId is Guid locationCandidateId)
        {
            await EnsureCandidateAsync(
                connection,
                transaction,
                locationCandidateId,
                confirmation.ImageItemId,
                "Location",
                cancellationToken).ConfigureAwait(false);
        }

        var reminderId = Guid.NewGuid();
        var schedulerId = CreateSchedulerId(reminderId);
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO Reminders (
                Id, ImageItemId, Title, DueAtUtc, TimeZoneId, ConfirmedLocation,
                SchedulerId, State, CreatedAtUtc, UpdatedAtUtc,
                SourceDateCandidateId, SourceLocationCandidateId,
                NotificationState, NotificationLastErrorCode,
                CompletionReason, ActivatedAtUtc, LastReconciledAtUtc)
            VALUES (
                @id, @itemId, @title, @dueAtUtc, @timeZoneId, @location,
                @schedulerId, 1, @created, @updated,
                @dateCandidateId, @locationCandidateId,
                1, NULL, NULL, NULL, NULL);
            """,
            cancellationToken,
            ("@id", ToDb(reminderId)),
            ("@itemId", ToDb(confirmation.ImageItemId)),
            ("@title", title),
            ("@dueAtUtc", ToDb(dueAtUtc)),
            ("@timeZoneId", confirmation.TimeZoneId),
            ("@location", location),
            ("@schedulerId", schedulerId),
            ("@created", ToDb(now)),
            ("@updated", ToDb(now)),
            ("@dateCandidateId", confirmation.DateCandidateId is null
                ? null
                : ToDb(confirmation.DateCandidateId.Value)),
            ("@locationCandidateId", confirmation.LocationCandidateId is null
                ? null
                : ToDb(confirmation.LocationCandidateId.Value))).ConfigureAwait(false);
        await MarkCandidatesConfirmedAsync(
            connection,
            transaction,
            reminderId,
            confirmation.DateCandidateId,
            confirmation.LocationCandidateId,
            cancellationToken).ConfigureAwait(false);
        await EnqueueScheduleAsync(
            connection,
            transaction,
            reminderId,
            schedulerId,
            confirmation.ImageItemId,
            dueAtUtc,
            location,
            now,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        Notify();
        return await GetReminderAsync(reminderId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The confirmed reminder could not be reloaded.");
    }

    public async Task<Reminder> UpdateAsync(
        ReminderUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        var now = _timeProvider.GetUtcNow();
        var dueAtUtc = ValidateAndConvertDueTime(update.LocalDueDateTime, update.TimeZoneId, now);
        var title = NormalizeTitle(update.ImageTitle);
        var location = NormalizeLocation(update.ConfirmedLocation);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var affected = await ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE Reminders
            SET Title = @title,
                DueAtUtc = @dueAtUtc, TimeZoneId = @timeZoneId,
                ConfirmedLocation = @location, State = 1,
                NotificationState = 1, NotificationLastErrorCode = NULL,
                CompletionReason = NULL, ActivatedAtUtc = NULL,
                UpdatedAtUtc = @updated
            WHERE Id = @id
              AND EXISTS (
                  SELECT 1 FROM ImageItems i
                  WHERE i.Id = Reminders.ImageItemId AND i.DeletedAtUtc IS NULL);
            """,
            cancellationToken,
            ("@title", title),
            ("@dueAtUtc", ToDb(dueAtUtc)),
            ("@timeZoneId", update.TimeZoneId),
            ("@location", location),
            ("@updated", ToDb(now)),
            ("@id", ToDb(update.ReminderId))).ConfigureAwait(false);
        EnsureFound(affected);
        var outboxData = await GetOutboxReminderDataAsync(
            connection,
            transaction,
            update.ReminderId,
            cancellationToken).ConfigureAwait(false);
        await EnqueueScheduleAsync(
            connection,
            transaction,
            update.ReminderId,
            outboxData.SchedulerId,
            outboxData.ImageItemId,
            dueAtUtc,
            location,
            now,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        Notify();
        return await GetReminderAsync(update.ReminderId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The updated reminder could not be reloaded.");
    }

    public async Task DismissCandidateAsync(
        Guid candidateId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var affected = await ExecuteAsync(
            connection,
            transaction: null,
            """
            UPDATE EntityCandidates
            SET CandidateStatus = 3
            WHERE Id = @id AND CandidateStatus = 1;
            """,
            cancellationToken,
            ("@id", ToDb(candidateId))).ConfigureAwait(false);
        EnsureFound(affected);
    }

    public async Task CancelAsync(
        Guid reminderId,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var data = await GetOutboxReminderDataAsync(
            connection,
            transaction,
            reminderId,
            cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE Reminders
            SET State = 2, NotificationState = 1, CompletionReason = 'Cancelled',
                ActivatedAtUtc = NULL, UpdatedAtUtc = @updated
            WHERE Id = @id;
            """,
            cancellationToken,
            ("@updated", ToDb(now)),
            ("@id", ToDb(reminderId))).ConfigureAwait(false);
        await SupersedePendingScheduleAsync(
            connection,
            transaction,
            reminderId,
            now,
            cancellationToken).ConfigureAwait(false);
        await EnqueueCancelAsync(
            connection,
            transaction,
            reminderId,
            data.SchedulerId,
            now,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        Notify();
    }

    public async Task DeleteAsync(
        Guid reminderId,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var data = await GetOutboxReminderDataAsync(
            connection,
            transaction,
            reminderId,
            cancellationToken).ConfigureAwait(false);
        await SupersedePendingScheduleAsync(
            connection,
            transaction,
            reminderId,
            now,
            cancellationToken).ConfigureAwait(false);
        await EnqueueCancelAsync(
            connection,
            transaction,
            reminderId,
            data.SchedulerId,
            now,
            cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE EntityCandidates
            SET CandidateStatus = 3, ConfirmedReminderId = NULL
            WHERE ConfirmedReminderId = @id;
            """,
            cancellationToken,
            ("@id", ToDb(reminderId))).ConfigureAwait(false);
        var affected = await ExecuteAsync(
            connection,
            transaction,
            "DELETE FROM Reminders WHERE Id = @id;",
            cancellationToken,
            ("@id", ToDb(reminderId))).ConfigureAwait(false);
        EnsureFound(affected);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        Notify();
    }

    public async Task<bool> MarkActivatedAsync(
        Guid reminderId,
        Guid imageItemId,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var affected = await ExecuteAsync(
            connection,
            transaction: null,
            """
            UPDATE Reminders
            SET State = 2, NotificationState = 5, CompletionReason = 'Activated',
                ActivatedAtUtc = @activated, UpdatedAtUtc = @activated,
                LastReconciledAtUtc = @activated
            WHERE Id = @id
              AND ImageItemId = @imageItemId
              AND State IN (1, 4)
              AND EXISTS (
                  SELECT 1 FROM ImageItems i
                  WHERE i.Id = Reminders.ImageItemId AND i.DeletedAtUtc IS NULL);
            """,
            cancellationToken,
            ("@activated", ToDb(now)),
            ("@id", ToDb(reminderId)),
            ("@imageItemId", ToDb(imageItemId))).ConfigureAwait(false);
        return affected == 1;
    }

    public async Task<ReminderReconciliationResult> ReconcileAsync(
        CancellationToken cancellationToken = default)
    {
        await _reconciliationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _timeProvider.GetUtcNow();
            var missedCount = await PrepareReconciliationAsync(now, cancellationToken)
                .ConfigureAwait(false);
            var notificationsSupported = _scheduler.IsSupported;
            IReadOnlySet<string> scheduledIds = new HashSet<string>(StringComparer.Ordinal);
            if (notificationsSupported)
            {
                try
                {
                    scheduledIds = await _scheduler.GetScheduledIdsAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // The scheduler is an external projection. Continue from
                    // SQLite and leave retryable outbox work instead of making
                    // reminder records unavailable.
                    notificationsSupported = false;
                }
            }

            await EnqueueReconciliationOperationsAsync(
                scheduledIds,
                now,
                cancellationToken).ConfigureAwait(false);
            var processed = await DrainOutboxAsync(now, cancellationToken).ConfigureAwait(false);
            return new ReminderReconciliationResult(
                missedCount,
                processed.Scheduled,
                processed.Cancelled,
                processed.Failed,
                notificationsSupported);
        }
        finally
        {
            _reconciliationGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _reconciliationGate.Dispose();
        _wakeSignal.Dispose();
    }

    private async Task<int> PrepareReconciliationAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE ReminderNotificationOutbox
            SET State = 4, UpdatedAtUtc = @updated, LastErrorCode = 'Interrupted'
            WHERE State = 2;
            """,
            cancellationToken,
            ("@updated", ToDb(now))).ConfigureAwait(false);
        var missed = await ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE Reminders
            SET State = 4, UpdatedAtUtc = @updated,
                LastReconciledAtUtc = @updated
            WHERE State = 1 AND DueAtUtc <= @cutoff;
            """,
            cancellationToken,
            ("@updated", ToDb(now)),
            ("@cutoff", ToDb(now.Subtract(MissedDeliveryWindow)))).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return missed;
    }

    private async Task EnqueueReconciliationOperationsAsync(
        IReadOnlySet<string> scheduledIds,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var reminders = await ReadReconciliationRowsAsync(
            connection,
            transaction,
            cancellationToken).ConfigureAwait(false);
        var knownSchedulerIds = reminders.Select(reminder => reminder.SchedulerId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var reminder in reminders)
        {
            var shouldBeScheduled = reminder.State == ReminderState.Active
                && reminder.DueAtUtc > now;
            if (shouldBeScheduled && !scheduledIds.Contains(reminder.SchedulerId))
            {
                await EnqueueScheduleAsync(
                    connection,
                    transaction,
                    reminder.ReminderId,
                    reminder.SchedulerId,
                    reminder.ImageItemId,
                    reminder.DueAtUtc,
                    reminder.Location,
                    now,
                    cancellationToken).ConfigureAwait(false);
            }
            else if (!shouldBeScheduled && scheduledIds.Contains(reminder.SchedulerId))
            {
                await EnqueueCancelAsync(
                    connection,
                    transaction,
                    reminder.ReminderId,
                    reminder.SchedulerId,
                    now,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var orphanedSchedulerId in scheduledIds.Where(id => !knownSchedulerIds.Contains(id)))
        {
            await EnqueueCancelAsync(
                connection,
                transaction,
                reminderId: null,
                orphanedSchedulerId,
                now,
                cancellationToken).ConfigureAwait(false);
        }

        await ExecuteAsync(
            connection,
            transaction,
            "UPDATE Reminders SET LastReconciledAtUtc = @updated WHERE 1 = 1;",
            cancellationToken,
            ("@updated", ToDb(now))).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<OutboxDrainResult> DrainOutboxAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var scheduled = 0;
        var cancelled = 0;
        var failed = 0;
        var processedOperationIds = new HashSet<Guid>();
        while (true)
        {
            var operation = await LeaseNextOutboxAsync(now, cancellationToken).ConfigureAwait(false);
            if (operation is null)
            {
                break;
            }

            if (!processedOperationIds.Add(operation.Id))
            {
                throw new InvalidDataException("ReminderOutboxOperationRepeatedInSingleDrain");
            }

            try
            {
                if (!_scheduler.IsSupported)
                {
                    throw new InvalidOperationException("NotificationsUnsupported");
                }

                if (operation.Operation == OutboxOperation.Schedule)
                {
                    if (operation.ReminderId is null
                        || operation.ImageItemId is null
                        || operation.DueAtUtc is null
                        || operation.Title is null)
                    {
                        throw new InvalidDataException("ReminderOutboxPayloadInvalid");
                    }

                    if (operation.DueAtUtc <= now)
                    {
                        await MarkOutboxObsoleteAsync(
                            operation,
                            now,
                            cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    await _scheduler.ScheduleAsync(
                        new ReminderNotification(
                            operation.SchedulerId,
                            operation.ReminderId.Value,
                            operation.ImageItemId.Value,
                            operation.DueAtUtc.Value,
                            operation.Title,
                            operation.Body ?? string.Empty,
                            operation.Location),
                        cancellationToken).ConfigureAwait(false);
                    scheduled++;
                }
                else
                {
                    await _scheduler.CancelAsync(
                        operation.SchedulerId,
                        cancellationToken).ConfigureAwait(false);
                    cancelled++;
                }

                await CompleteOutboxAsync(operation, now, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await RetryOutboxAsync(
                    operation,
                    "Cancelled",
                    now,
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (Exception exception)
            {
                failed++;
                await RetryOutboxAsync(
                    operation,
                    ClassifySchedulerError(exception),
                    now,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        return new OutboxDrainResult(scheduled, cancelled, failed);
    }

    private async Task<OutboxOperationRow?> LeaseNextOutboxAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText =
            """
            SELECT o.Id, o.ReminderId, o.SchedulerId, o.Operation, o.DueAtUtc,
                   o.Title, o.Body, o.Location, o.AttemptCount,
                   r.ImageItemId
            FROM ReminderNotificationOutbox o
            LEFT JOIN Reminders r ON r.Id = o.ReminderId
            WHERE o.State IN (1, 4)
              AND o.NotBeforeUtc <= @now
              AND o.AttemptCount < @maximumAttempts
            ORDER BY o.CreatedAtUtc
            LIMIT 1;
            """;
        select.Parameters.AddWithValue("@now", ToDb(now));
        select.Parameters.AddWithValue("@maximumAttempts", MaximumOutboxAttempts);
        OutboxOperationRow? row = null;
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                row = new OutboxOperationRow(
                    Guid.Parse(reader.GetString(0)),
                    reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)),
                    reader.GetString(2),
                    (OutboxOperation)reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : ParseDate(reader.GetString(4)),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetInt32(8),
                    reader.IsDBNull(9) ? null : Guid.Parse(reader.GetString(9)));
            }
        }

        if (row is not null)
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE ReminderNotificationOutbox
                SET State = 2, AttemptCount = AttemptCount + 1, UpdatedAtUtc = @updated
                WHERE replace(Id, '-', '') = @id AND State IN (1, 4);
                """,
                cancellationToken,
                ("@updated", ToDb(now)),
                ("@id", ToDbCompact(row.Id))).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return row;
    }

    private async Task CompleteOutboxAsync(
        OutboxOperationRow operation,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE ReminderNotificationOutbox
            SET State = 3, LastErrorCode = NULL, UpdatedAtUtc = @updated,
                CompletedAtUtc = @updated
            WHERE replace(Id, '-', '') = @id;
            """,
            cancellationToken,
            ("@updated", ToDb(now)),
            ("@id", ToDbCompact(operation.Id))).ConfigureAwait(false);
        if (operation.ReminderId is Guid reminderId)
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE Reminders
                SET NotificationState = @notificationState,
                    NotificationLastErrorCode = NULL,
                    UpdatedAtUtc = @updated, LastReconciledAtUtc = @updated
                WHERE Id = @id;
                """,
                cancellationToken,
                ("@notificationState", operation.Operation == OutboxOperation.Schedule ? 2 : 4),
                ("@updated", ToDb(now)),
                ("@id", ToDb(reminderId))).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RetryOutboxAsync(
        OutboxOperationRow operation,
        string errorCode,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var exhausted = operation.AttemptCount + 1 >= MaximumOutboxAttempts;
        await ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE ReminderNotificationOutbox
            SET State = @state, LastErrorCode = @error,
                NotBeforeUtc = @notBefore, UpdatedAtUtc = @updated,
                CompletedAtUtc = CASE WHEN @state = 3 THEN @updated ELSE NULL END
            WHERE replace(Id, '-', '') = @id;
            """,
            cancellationToken,
            ("@state", exhausted ? 3 : 4),
            ("@error", errorCode),
            ("@notBefore", ToDb(now.AddMinutes(Math.Min(30, operation.AttemptCount + 1)))),
            ("@updated", ToDb(now)),
            ("@id", ToDbCompact(operation.Id))).ConfigureAwait(false);
        if (operation.ReminderId is Guid reminderId)
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE Reminders
                SET NotificationState = 3, NotificationLastErrorCode = @error,
                    UpdatedAtUtc = @updated, LastReconciledAtUtc = @updated
                WHERE Id = @id;
                """,
                cancellationToken,
                ("@error", errorCode),
                ("@updated", ToDb(now)),
                ("@id", ToDb(reminderId))).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task MarkOutboxObsoleteAsync(
        OutboxOperationRow operation,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE ReminderNotificationOutbox
            SET State = 3, LastErrorCode = 'DueTimeElapsed',
                UpdatedAtUtc = @updated, CompletedAtUtc = @updated
            WHERE replace(Id, '-', '') = @id;
            """,
            cancellationToken,
            ("@updated", ToDb(now)),
            ("@id", ToDbCompact(operation.Id))).ConfigureAwait(false);
        if (operation.ReminderId is Guid reminderId)
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE Reminders
                SET State = 4, NotificationState = 4,
                    NotificationLastErrorCode = 'DueTimeElapsed',
                    UpdatedAtUtc = @updated, LastReconciledAtUtc = @updated
                WHERE Id = @id AND State = 1;
                """,
                cancellationToken,
                ("@updated", ToDb(now)),
                ("@id", ToDb(reminderId))).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<Reminder?> GetReminderAsync(
        Guid reminderId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT {ReminderColumns}
            FROM Reminders r
            INNER JOIN ImageItems i ON i.Id = r.ImageItemId
            INNER JOIN ImageAssets a ON a.Id = i.AssetId
            WHERE r.Id = @id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@id", ToDb(reminderId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadReminder(reader)
            : null;
    }

    private static async Task<Guid?> GetConfirmedReminderIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid candidateId,
        Guid imageItemId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT ConfirmedReminderId
            FROM EntityCandidates
            WHERE Id = @id AND ImageItemId = @imageItemId
              AND CandidateStatus = 2;
            """;
        command.Parameters.AddWithValue("@id", ToDb(candidateId));
        command.Parameters.AddWithValue("@imageItemId", ToDb(imageItemId));
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is string text ? Guid.Parse(text) : null;
    }

    private static async Task EnsureActiveImageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid imageItemId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT COUNT(*) FROM ImageItems WHERE Id = @id AND DeletedAtUtc IS NULL;";
        command.Parameters.AddWithValue("@id", ToDb(imageItemId));
        var count = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        if (count == 0)
        {
            throw new KeyNotFoundException("The reminder image is unavailable.");
        }
    }

    private static async Task EnsureCandidateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid candidateId,
        Guid imageItemId,
        string kind,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM EntityCandidates
            WHERE Id = @id AND ImageItemId = @imageItemId
              AND Kind = @kind AND CandidateStatus = 1;
            """;
        command.Parameters.AddWithValue("@id", ToDb(candidateId));
        command.Parameters.AddWithValue("@imageItemId", ToDb(imageItemId));
        command.Parameters.AddWithValue("@kind", kind);
        var count = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        if (count == 0)
        {
            throw new ReminderValidationException("CandidateUnavailable");
        }
    }

    private static Task MarkCandidatesConfirmedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid reminderId,
        Guid? dateCandidateId,
        Guid? locationCandidateId,
        CancellationToken cancellationToken)
    {
        var ids = new[] { dateCandidateId, locationCandidateId }
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();
        if (ids.Length == 0)
        {
            return Task.CompletedTask;
        }

        return ExecuteAsync(
            connection,
            transaction,
            $"UPDATE EntityCandidates SET CandidateStatus = 2, ConfirmedReminderId = @reminderId WHERE Id IN ({string.Join(",", ids.Select((_, index) => $"@candidate{index}"))});",
            cancellationToken,
            [("@reminderId", ToDb(reminderId)), .. ids.Select((id, index) => ($"@candidate{index}", (object?)ToDb(id)))]);
    }

    private static async Task EnqueueScheduleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid reminderId,
        string schedulerId,
        Guid imageItemId,
        DateTimeOffset dueAtUtc,
        string? location,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await SupersedePendingScheduleAsync(
            connection,
            transaction,
            reminderId,
            now,
            cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO ReminderNotificationOutbox (
                Id, ReminderId, SchedulerId, Operation, DueAtUtc,
                Title, Body, Location, State, AttemptCount, NotBeforeUtc,
                LastErrorCode, CreatedAtUtc, UpdatedAtUtc, CompletedAtUtc)
            SELECT
                @id, @reminderId, @schedulerId, 1, @dueAtUtc,
                COALESCE(r.Title, i.Title), '', @location, 1, 0, @notBefore,
                NULL, @created, @updated, NULL
            FROM Reminders r
            INNER JOIN ImageItems i ON i.Id = r.ImageItemId
            WHERE r.Id = @reminderId
              AND r.ImageItemId = @imageItemId
              AND NOT EXISTS (
                  SELECT 1
                  FROM ReminderNotificationOutbox pending
                  WHERE pending.ReminderId = @reminderId
                    AND pending.Operation = 1
                    AND pending.State IN (1, 2, 4)
                    AND pending.DueAtUtc = @dueAtUtc);
            """,
            cancellationToken,
            ("@id", ToDb(Guid.NewGuid())),
            ("@reminderId", ToDb(reminderId)),
            ("@schedulerId", schedulerId),
            ("@dueAtUtc", ToDb(dueAtUtc)),
            ("@location", location),
            ("@notBefore", ToDb(now)),
            ("@created", ToDb(now)),
            ("@updated", ToDb(now)),
            ("@imageItemId", ToDb(imageItemId))).ConfigureAwait(false);
    }

    private static async Task EnqueueCancelAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid? reminderId,
        string schedulerId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO ReminderNotificationOutbox (
                Id, ReminderId, SchedulerId, Operation, DueAtUtc,
                Title, Body, Location, State, AttemptCount, NotBeforeUtc,
                LastErrorCode, CreatedAtUtc, UpdatedAtUtc, CompletedAtUtc)
            SELECT
                @id, @reminderId, @schedulerId, 2, NULL,
                NULL, NULL, NULL, 1, 0, @notBefore,
                NULL, @created, @updated, NULL
            WHERE NOT EXISTS (
                SELECT 1
                FROM ReminderNotificationOutbox pending
                WHERE pending.SchedulerId = @schedulerId
                  AND pending.Operation = 2
                  AND pending.State IN (1, 2, 4));
            """,
            cancellationToken,
            ("@id", ToDb(Guid.NewGuid())),
            ("@reminderId", reminderId is null ? null : ToDb(reminderId.Value)),
            ("@schedulerId", schedulerId),
            ("@notBefore", ToDb(now)),
            ("@created", ToDb(now)),
            ("@updated", ToDb(now))).ConfigureAwait(false);
    }

    private static Task SupersedePendingScheduleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid reminderId,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE ReminderNotificationOutbox
            SET State = 3, LastErrorCode = 'Superseded',
                UpdatedAtUtc = @updated, CompletedAtUtc = @updated
            WHERE ReminderId = @reminderId
              AND Operation = 1
              AND State IN (1, 4);
            """,
            cancellationToken,
            ("@updated", ToDb(now)),
            ("@reminderId", ToDb(reminderId)));

    private static async Task<(Guid ImageItemId, string SchedulerId)> GetOutboxReminderDataAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid reminderId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT ImageItemId, SchedulerId FROM Reminders WHERE Id = @id;";
        command.Parameters.AddWithValue("@id", ToDb(reminderId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new KeyNotFoundException("The requested reminder was not found.");
        }

        return (Guid.Parse(reader.GetString(0)), reader.GetString(1));
    }

    private static async Task<IReadOnlyList<ReconciliationRow>> ReadReconciliationRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT r.Id, r.ImageItemId, r.SchedulerId, r.DueAtUtc,
                   r.ConfirmedLocation, r.State
            FROM Reminders r;
            """;
        var rows = new List<ReconciliationRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new ReconciliationRow(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                ParseDate(reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                (ReminderState)reader.GetInt32(5)));
        }

        return rows;
    }

    private DateTimeOffset ValidateAndConvertDueTime(
        DateTime localDueDateTime,
        string timeZoneId,
        DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZoneId);
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            throw new ReminderValidationException("TimeZoneUnavailable");
        }
        catch (InvalidTimeZoneException)
        {
            throw new ReminderValidationException("TimeZoneInvalid");
        }

        var unspecified = DateTime.SpecifyKind(localDueDateTime, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(unspecified))
        {
            throw new ReminderValidationException("DaylightSavingInvalidTime");
        }

        if (zone.IsAmbiguousTime(unspecified))
        {
            throw new ReminderValidationException("DaylightSavingAmbiguousTime");
        }

        var dueAtUtc = new DateTimeOffset(
            TimeZoneInfo.ConvertTimeToUtc(unspecified, zone),
            TimeSpan.Zero);
        if (dueAtUtc <= nowUtc.AddSeconds(5))
        {
            throw new ReminderValidationException("DueTimeMustBeFuture");
        }

        return dueAtUtc;
    }

    private static string? NormalizeLocation(string? location)
    {
        var normalized = string.IsNullOrWhiteSpace(location) ? null : location.Trim();
        if (normalized?.Length > MaximumLocationLength)
        {
            throw new ReminderValidationException("LocationTooLong");
        }

        return normalized;
    }

    private static string NormalizeTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ReminderValidationException("TitleRequired");
        }

        var normalized = title.Trim();
        if (normalized.Length > MaximumTitleLength)
        {
            throw new ReminderValidationException("TitleTooLong");
        }

        return normalized;
    }

    private static string CreateSchedulerId(Guid reminderId) =>
        reminderId.ToString("N", CultureInfo.InvariantCulture)[..16];

    private static string ClassifySchedulerError(Exception exception)
    {
        if (exception is UnauthorizedAccessException)
        {
            return "NotificationsAccessDenied";
        }

        if (exception is InvalidOperationException
            && exception.Message.Contains("Unsupported", StringComparison.OrdinalIgnoreCase))
        {
            return "NotificationsUnsupported";
        }

        return "NotificationSchedulingFailed";
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        _paths.EnsureSafePath(_paths.DatabasePath);
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = _paths.DatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
            }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<int> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Reminder ReadReminder(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        Guid.Parse(reader.GetString(1)),
        reader.GetString(2),
        ParseDate(reader.GetString(3)),
        reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.GetString(6),
        (ReminderState)reader.GetInt32(7),
        (ReminderNotificationState)reader.GetInt32(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetString(10),
        reader.IsDBNull(11) ? null : ParseDate(reader.GetString(11)),
        reader.IsDBNull(12) ? null : ParseDate(reader.GetString(12)),
        ParseDate(reader.GetString(13)),
        ParseDate(reader.GetString(14)))
    {
        PreviewRelativePath = reader.IsDBNull(15)
            ? null
            : ManagedRelativePath.Parse(reader.GetString(15)),
    };

    private static ReminderCandidate ReadCandidate(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        Guid.Parse(reader.GetString(1)),
        reader.GetString(2),
        ParseCandidateKind(reader.GetString(3)),
        reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.GetString(6),
        ParseCandidateSource(reader.GetString(7)),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.IsDBNull(9) ? null : ParseDate(reader.GetString(9)),
        reader.IsDBNull(10) ? null : reader.GetString(10),
        reader.IsDBNull(11) ? null : reader.GetString(11),
        ParseDate(reader.GetString(12)))
    {
        SuggestedLocationCandidateId = reader.IsDBNull(13)
            ? null
            : Guid.Parse(reader.GetString(13)),
        SuggestedLocation = reader.IsDBNull(14) ? null : reader.GetString(14),
        SuggestedLocationEvidence = reader.IsDBNull(15) ? null : reader.GetString(15),
        PreviewRelativePath = reader.IsDBNull(16)
            ? null
            : ManagedRelativePath.Parse(reader.GetString(16)),
    };

    private static ReminderCandidate NormalizeMissingYearProjection(
        ReminderCandidate candidate)
    {
        if (candidate.AmbiguityReason != "MissingYear"
            || !TryGetCandidateTimeZone(candidate.TimeZoneId, out var zone))
        {
            return candidate;
        }

        var referenceLocal = TimeZoneInfo.ConvertTime(
            candidate.ReferenceTimeUtc ?? candidate.GeneratedAtUtc,
            zone);
        var hasTime = TryParseCandidateDateTime(
            candidate.NormalizedValue,
            out var parsed);
        if (!hasTime
            && !TryParseCandidateDate(candidate.NormalizedValue, out parsed))
        {
            return candidate;
        }

        if (parsed.Year == referenceLocal.Year)
        {
            return candidate;
        }

        DateTime corrected;
        try
        {
            corrected = new DateTime(
                referenceLocal.Year,
                parsed.Month,
                parsed.Day,
                parsed.Hour,
                parsed.Minute,
                parsed.Second,
                parsed.Millisecond,
                DateTimeKind.Unspecified)
                .AddTicks(parsed.Ticks % TimeSpan.TicksPerMillisecond);
        }
        catch (ArgumentOutOfRangeException)
        {
            return candidate with { NormalizedValue = null };
        }

        var normalized = hasTime
            ? corrected.ToString(
                corrected.Ticks % TimeSpan.TicksPerSecond == 0
                    ? "yyyy-MM-dd'T'HH:mm:ss"
                    : "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
                CultureInfo.InvariantCulture)
            : corrected.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return candidate with { NormalizedValue = normalized };
    }

    private static bool IsActionableCandidate(
        ReminderCandidate candidate,
        DateTimeOffset nowUtc)
    {
        if (candidate.Kind != EntityCandidateKind.DateTime)
        {
            return true;
        }

        if (candidate.AmbiguityReason == "MissingDate"
            && !TryParseCandidateDateTime(candidate.NormalizedValue, out _))
        {
            return false;
        }

        if (!TryGetCandidateTimeZone(candidate.TimeZoneId, out var zone))
        {
            return true;
        }

        var normalized = candidate.NormalizedValue;
        if (candidate.AmbiguityReason == "DateOrder"
            && string.IsNullOrWhiteSpace(normalized))
        {
            var possibleDates = ParseAmbiguousDateOrder(candidate.RawText);
            return possibleDates.Count == 0
                || possibleDates.Any(date => IsLocalCandidateDueInFuture(
                    date.Date.Add(DefaultCandidateTime),
                    zone,
                    nowUtc));
        }

        if (TryParseCandidateDateTime(normalized, out var localDateTime)
            || TryParseCandidateDateTime(candidate.RawText, out localDateTime))
        {
            var unspecified = DateTime.SpecifyKind(localDateTime, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(unspecified) || zone.IsAmbiguousTime(unspecified))
            {
                return true;
            }

            var dueAtUtc = new DateTimeOffset(
                TimeZoneInfo.ConvertTimeToUtc(unspecified, zone),
                TimeSpan.Zero);
            return dueAtUtc > nowUtc.AddSeconds(5);
        }

        if (TryParseCandidateDate(normalized, out var date)
            || TryParseCandidateDate(candidate.RawText, out date))
        {
            return IsLocalCandidateDueInFuture(
                date.Date.Add(DefaultCandidateTime),
                zone,
                nowUtc);
        }

        if (DateTime.TryParseExact(
                normalized,
                "yyyy-MM",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var yearMonth))
        {
            return IsLocalCandidateDueInFuture(
                new DateTime(yearMonth.Year, yearMonth.Month, 1)
                    .Add(DefaultCandidateTime),
                zone,
                nowUtc);
        }

        if (DateTime.TryParseExact(
                normalized,
                "yyyy",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var year))
        {
            return IsLocalCandidateDueInFuture(
                new DateTime(year.Year, 1, 1).Add(DefaultCandidateTime),
                zone,
                nowUtc);
        }

        // Unresolved date-order/model candidates remain visible for human review.
        // The query only suppresses values that can be proven to be elapsed.
        return true;
    }

    private static IReadOnlyList<DateTime> ParseAmbiguousDateOrder(string value)
    {
        var dates = new List<DateTime>();
        foreach (var format in AmbiguousDateOrderFormats)
        {
            if (DateTime.TryParseExact(
                    value.Trim(),
                    format,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var date)
                && !dates.Contains(date.Date))
            {
                dates.Add(date.Date);
            }
        }

        return dates;
    }

    private static bool IsLocalCandidateDueInFuture(
        DateTime localDue,
        TimeZoneInfo zone,
        DateTimeOffset nowUtc)
    {
        var unspecified = DateTime.SpecifyKind(localDue, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(unspecified) || zone.IsAmbiguousTime(unspecified))
        {
            return true;
        }

        var dueAtUtc = new DateTimeOffset(
            TimeZoneInfo.ConvertTimeToUtc(unspecified, zone),
            TimeSpan.Zero);
        return dueAtUtc > nowUtc.AddSeconds(5);
    }

    private static bool TryGetCandidateTimeZone(
        string? timeZoneId,
        out TimeZoneInfo zone)
    {
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(
                string.IsNullOrWhiteSpace(timeZoneId)
                    ? TimeZoneInfo.Local.Id
                    : timeZoneId);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            zone = TimeZoneInfo.Utc;
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            zone = TimeZoneInfo.Utc;
            return false;
        }
    }

    private static bool TryParseCandidateDateTime(string? value, out DateTime parsed) =>
        DateTime.TryParseExact(
            value,
            CandidateDateTimeFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out parsed);

    private static bool TryParseCandidateDate(string? value, out DateTime parsed) =>
        DateTime.TryParseExact(
            value,
            CandidateDateFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out parsed);

    private static EntityCandidateKind ParseCandidateKind(string value) => value switch
    {
        "DateTime" => EntityCandidateKind.DateTime,
        "Location" => EntityCandidateKind.Location,
        _ => throw new InvalidDataException("The candidate kind is unsupported."),
    };

    private static EntityCandidateSource ParseCandidateSource(string value) => value switch
    {
        "Metadata" => EntityCandidateSource.Metadata,
        "Ocr" => EntityCandidateSource.Ocr,
        "Model" => EntityCandidateSource.Model,
        _ => throw new InvalidDataException("The candidate source is unsupported."),
    };

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string ToDb(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);

    private static string ToDbCompact(Guid value) =>
        value.ToString("N", CultureInfo.InvariantCulture);

    private static string ToDb(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static void EnsureFound(int affected)
    {
        if (affected == 0)
        {
            throw new KeyNotFoundException("The requested reminder record was not found.");
        }
    }

    private static void ValidatePage(int offset, int limit)
    {
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        if (limit is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }
    }

    private const string ReminderColumns =
        """
        r.Id, r.ImageItemId, COALESCE(r.Title, i.Title), r.DueAtUtc, r.TimeZoneId,
        r.ConfirmedLocation, r.SchedulerId, r.State, r.NotificationState,
        r.NotificationLastErrorCode, r.CompletionReason, r.ActivatedAtUtc,
        r.LastReconciledAtUtc, r.CreatedAtUtc, r.UpdatedAtUtc,
        COALESCE(a.ThumbnailRelativePath, a.OriginalRelativePath)
        """;

    private enum OutboxOperation
    {
        Schedule = 1,
        Cancel = 2,
    }

    private sealed record OutboxOperationRow(
        Guid Id,
        Guid? ReminderId,
        string SchedulerId,
        OutboxOperation Operation,
        DateTimeOffset? DueAtUtc,
        string? Title,
        string? Body,
        string? Location,
        int AttemptCount,
        Guid? ImageItemId);

    private sealed record ReconciliationRow(
        Guid ReminderId,
        Guid ImageItemId,
        string SchedulerId,
        DateTimeOffset DueAtUtc,
        string? Location,
        ReminderState State);

    private sealed record OutboxDrainResult(int Scheduled, int Cancelled, int Failed);
}
