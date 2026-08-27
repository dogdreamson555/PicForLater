using System.Globalization;
using Microsoft.Data.Sqlite;
using PicForLater.Core.Reminders;
using PicForLater.Infrastructure.Library;
using PicForLater.Infrastructure.Reminders;
using PicForLater.Infrastructure.Storage;

namespace PicForLater.IntegrationTests;

public sealed class ReminderWorkflowTests
{
    [Fact]
    public async Task PendingCandidates_HideElapsedAbsoluteDatesAndKeepLogicalPaging()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var seeded = await SeedCandidateAsync(root.Paths);
        var now = new DateTimeOffset(2026, 7, 30, 8, 0, 0, TimeSpan.Zero);
        await using (var connection = await OpenAsync(root.Paths.DatabasePath))
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE EntityCandidates
                SET RawText = '2026/5/9',
                    NormalizedValue = '2026-05-09',
                    Evidence = '帖子发布于 2026/5/9',
                    TimeZoneId = 'China Standard Time',
                    GeneratedAtUtc = '2026-07-30T08:03:00.0000000+00:00'
                WHERE Id = @pastDateId;

                INSERT INTO EntityCandidates (
                    Id, AnalysisJobId, ImageItemId, Kind, RawText,
                    NormalizedValue, Evidence, Source, GeneratedAtUtc,
                    CandidateStatus, BoundingBoxJson, ReferenceTimeUtc,
                    TimeZoneId, AmbiguityReason, ConfirmedReminderId)
                SELECT
                    @pastTimeId, AnalysisJobId, ImageItemId, 'DateTime',
                    '2026-05-09 08:28:10.7978042',
                    '2026-05-09T08:28:10.7978042',
                    '旧帖时间 2026-05-09 08:28:10.7978042', 'Model',
                    '2026-07-30T08:02:00.0000000+00:00',
                    1, NULL, ReferenceTimeUtc, 'China Standard Time',
                    'ModelInterpretation', NULL
                FROM EntityCandidates WHERE Id = @pastDateId;

                INSERT INTO EntityCandidates (
                    Id, AnalysisJobId, ImageItemId, Kind, RawText,
                    NormalizedValue, Evidence, Source, GeneratedAtUtc,
                    CandidateStatus, BoundingBoxJson, ReferenceTimeUtc,
                    TimeZoneId, AmbiguityReason, ConfirmedReminderId)
                SELECT
                    @futureId, AnalysisJobId, ImageItemId, 'DateTime',
                    '2026年9月15日 16:30', '2026-09-15T16:30:00',
                    '申报截止时间 2026年9月15日 16:30', 'Ocr',
                    '2026-07-30T08:01:00.0000000+00:00',
                    1, NULL, ReferenceTimeUtc, 'China Standard Time',
                    NULL, NULL
                FROM EntityCandidates WHERE Id = @pastDateId;
                """;
            command.Parameters.AddWithValue("@pastDateId", seeded.DateCandidateId.ToString("D"));
            command.Parameters.AddWithValue("@pastTimeId", Guid.NewGuid().ToString("D"));
            var futureId = Guid.NewGuid();
            command.Parameters.AddWithValue("@futureId", futureId.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }

        using var service = new SqliteReminderService(
            root.Paths,
            new FakeReminderNotificationScheduler(),
            new MutableTimeProvider(now));

        var candidate = Assert.Single(
            await service.GetPendingCandidatesAsync(offset: 0, limit: 1));
        Assert.Equal("2026-09-15T16:30:00", candidate.NormalizedValue);
        Assert.Empty(await service.GetPendingCandidatesAsync(offset: 1, limit: 1));
    }

    [Fact]
    public async Task PendingCandidates_HideElapsedDefaultDateAndUnpairedTimeOnly()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var seeded = await SeedCandidateAsync(root.Paths);
        await using (var connection = await OpenAsync(root.Paths.DatabasePath))
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE EntityCandidates
                SET RawText = '2026年7月30日',
                    NormalizedValue = '2026-07-30',
                    TimeZoneId = 'China Standard Time'
                WHERE Id = @dateId;

                INSERT INTO EntityCandidates (
                    Id, AnalysisJobId, ImageItemId, Kind, RawText,
                    NormalizedValue, Evidence, Source, GeneratedAtUtc,
                    CandidateStatus, BoundingBoxJson, ReferenceTimeUtc,
                    TimeZoneId, AmbiguityReason, ConfirmedReminderId)
                SELECT
                    @timeId, AnalysisJobId, ImageItemId, 'DateTime',
                    '16:30', NULL, '16:30', 'Ocr', GeneratedAtUtc,
                    1, NULL, ReferenceTimeUtc, 'China Standard Time',
                    'MissingDate', NULL
                FROM EntityCandidates WHERE Id = @dateId;

                INSERT INTO EntityCandidates (
                    Id, AnalysisJobId, ImageItemId, Kind, RawText,
                    NormalizedValue, Evidence, Source, GeneratedAtUtc,
                    CandidateStatus, BoundingBoxJson, ReferenceTimeUtc,
                    TimeZoneId, AmbiguityReason, ConfirmedReminderId)
                SELECT
                    @futureDateId, AnalysisJobId, ImageItemId, 'DateTime',
                    '2026年7月31日', '2026-07-31', '活动日期 2026年7月31日',
                    'Ocr', GeneratedAtUtc, 1, NULL, ReferenceTimeUtc,
                    'China Standard Time', NULL, NULL
                FROM EntityCandidates WHERE Id = @dateId;
                """;
            command.Parameters.AddWithValue("@dateId", seeded.DateCandidateId.ToString("D"));
            command.Parameters.AddWithValue("@timeId", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("@futureDateId", Guid.NewGuid().ToString("D"));
            await command.ExecuteNonQueryAsync();
        }

        using var service = new SqliteReminderService(
            root.Paths,
            new FakeReminderNotificationScheduler(),
            new MutableTimeProvider(
                new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero)));

        var candidate = Assert.Single(await service.GetPendingCandidatesAsync());
        Assert.Equal("2026-07-31", candidate.NormalizedValue);
    }

    [Fact]
    public async Task PendingCandidates_ProjectLegacyMissingYearValuesIntoAnalysisYear()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var seeded = await SeedCandidateAsync(root.Paths);
        await using (var connection = await OpenAsync(root.Paths.DatabasePath))
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE EntityCandidates
                SET RawText = '9月15日 16:30',
                    NormalizedValue = '2027-09-15T16:30:00',
                    Evidence = '活动日期 9月15日 16:30',
                    ReferenceTimeUtc = '2026-07-30T00:00:00.0000000+00:00',
                    TimeZoneId = 'China Standard Time',
                    AmbiguityReason = 'MissingYear'
                WHERE Id = @futureId;

                INSERT INTO EntityCandidates (
                    Id, AnalysisJobId, ImageItemId, Kind, RawText,
                    NormalizedValue, Evidence, Source, GeneratedAtUtc,
                    CandidateStatus, BoundingBoxJson, ReferenceTimeUtc,
                    TimeZoneId, AmbiguityReason, ConfirmedReminderId)
                SELECT
                    @pastId, AnalysisJobId, ImageItemId, 'DateTime',
                    '6月30日 17:05', '2027-06-30T17:05:00',
                    '旧帖日期 6月30日 17:05', 'Ocr', GeneratedAtUtc,
                    1, NULL, '2026-07-30T00:00:00.0000000+00:00',
                    'China Standard Time', 'MissingYear', NULL
                FROM EntityCandidates WHERE Id = @futureId;
                """;
            command.Parameters.AddWithValue("@futureId", seeded.DateCandidateId.ToString("D"));
            command.Parameters.AddWithValue("@pastId", Guid.NewGuid().ToString("D"));
            await command.ExecuteNonQueryAsync();
        }

        using var service = new SqliteReminderService(
            root.Paths,
            new FakeReminderNotificationScheduler(),
            new MutableTimeProvider(
                new DateTimeOffset(2026, 7, 30, 8, 0, 0, TimeSpan.Zero)));

        var candidate = Assert.Single(await service.GetPendingCandidatesAsync());
        Assert.Equal("2026-09-15T16:30:00", candidate.NormalizedValue);
        Assert.Equal("MissingYear", candidate.AmbiguityReason);
    }

    [Fact]
    public async Task PendingCandidates_HideAmbiguousDateOnlyWhenEveryInterpretationIsPast()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var seeded = await SeedCandidateAsync(root.Paths);
        await using (var connection = await OpenAsync(root.Paths.DatabasePath))
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE EntityCandidates
                SET RawText = '03/04/2026',
                    NormalizedValue = NULL,
                    Evidence = '旧帖日期 03/04/2026',
                    TimeZoneId = 'China Standard Time',
                    AmbiguityReason = 'DateOrder'
                WHERE Id = @pastId;

                INSERT INTO EntityCandidates (
                    Id, AnalysisJobId, ImageItemId, Kind, RawText,
                    NormalizedValue, Evidence, Source, GeneratedAtUtc,
                    CandidateStatus, BoundingBoxJson, ReferenceTimeUtc,
                    TimeZoneId, AmbiguityReason, ConfirmedReminderId)
                SELECT
                    @futureId, AnalysisJobId, ImageItemId, 'DateTime',
                    '08/09/2026', NULL, '活动日期 08/09/2026', 'Ocr',
                    GeneratedAtUtc, 1, NULL, ReferenceTimeUtc,
                    'China Standard Time', 'DateOrder', NULL
                FROM EntityCandidates WHERE Id = @pastId;
                """;
            command.Parameters.AddWithValue("@pastId", seeded.DateCandidateId.ToString("D"));
            command.Parameters.AddWithValue("@futureId", Guid.NewGuid().ToString("D"));
            await command.ExecuteNonQueryAsync();
        }

        using var service = new SqliteReminderService(
            root.Paths,
            new FakeReminderNotificationScheduler(),
            new MutableTimeProvider(
                new DateTimeOffset(2026, 7, 30, 0, 0, 0, TimeSpan.Zero)));

        var candidate = Assert.Single(await service.GetPendingCandidatesAsync());
        Assert.Equal("08/09/2026", candidate.RawText);
    }

    [Fact]
    public async Task ConfirmCandidate_PersistsReminderAndSchedulesOutboxIdempotently()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var seeded = await SeedCandidateAsync(root.Paths);
        var now = DateTimeOffset.UtcNow;
        var clock = new MutableTimeProvider(now);
        var scheduler = new FakeReminderNotificationScheduler();
        using var service = new SqliteReminderService(root.Paths, scheduler, clock);
        var localDue = new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Unspecified);
        var pending = Assert.Single(
            await service.GetPendingCandidatesAsync(offset: 0, limit: 1));
        Assert.Equal("会议室", pending.SuggestedLocation);
        Assert.Equal("在 会 议 室", pending.SuggestedLocationEvidence);
        Assert.Empty(await service.GetPendingCandidatesAsync(offset: 1, limit: 1));

        var reminder = await service.ConfirmAsync(new ReminderConfirmation(
            seeded.ImageItemId,
            seeded.DateCandidateId,
            seeded.LocationCandidateId,
            "Candidate-specific reminder",
            localDue,
            "UTC",
            "Room 204"));
        var result = await service.ReconcileAsync();

        Assert.Equal(ReminderState.Active, reminder.State);
        Assert.Equal(1, result.ScheduledCount);
        Assert.Contains(reminder.SchedulerId, scheduler.ScheduledIds);
        var reloaded = Assert.Single(await service.GetRemindersAsync());
        Assert.Empty(await service.GetRemindersAsync(offset: 1, limit: 1));
        Assert.Equal(ReminderNotificationState.Scheduled, reloaded.NotificationState);
        Assert.Equal("Candidate-specific reminder", reloaded.ImageTitle);
        Assert.Equal("Room 204", reloaded.ConfirmedLocation);
        Assert.NotNull(reloaded.PreviewRelativePath);
        Assert.Empty(await service.GetPendingCandidatesAsync());
        await using var connection = await OpenAsync(root.Paths.DatabasePath);
        Assert.Equal(2L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM EntityCandidates WHERE CandidateStatus = 2;"));
        Assert.Equal(1L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM ReminderNotificationOutbox WHERE State = 3 AND Operation = 1 AND LastErrorCode IS NULL;"));
        Assert.Equal(1L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM ImageItems WHERE Title = 'Sample event' AND TitleSource = 1;"));

        var duplicate = await service.ConfirmAsync(new ReminderConfirmation(
            seeded.ImageItemId,
            seeded.DateCandidateId,
            seeded.LocationCandidateId,
            "Candidate-specific reminder",
            localDue,
            "UTC",
            "Room 204"));
        Assert.Equal(reminder.Id, duplicate.Id);
        Assert.Single(await service.GetRemindersAsync());
    }

    [Fact]
    public async Task Reconcile_WhenProjectionIsUnchanged_DoesNotUpdateReminderRows()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var seeded = await SeedCandidateAsync(root.Paths);
        var now = new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero);
        var scheduler = new FakeReminderNotificationScheduler();
        using var service = new SqliteReminderService(
            root.Paths,
            scheduler,
            new MutableTimeProvider(now));
        await service.ConfirmAsync(new ReminderConfirmation(
            seeded.ImageItemId,
            seeded.DateCandidateId,
            seeded.LocationCandidateId,
            "No-op reconciliation",
            new DateTime(2026, 9, 15, 12, 0, 0),
            "UTC",
            null));
        await service.ReconcileAsync();
        await using var connection = await OpenAsync(root.Paths.DatabasePath);
        await using (var audit = connection.CreateCommand())
        {
            audit.CommandText =
                """
                CREATE TABLE ReminderUpdateAudit (Id INTEGER PRIMARY KEY);
                CREATE TRIGGER TR_Reminders_UpdateAudit
                AFTER UPDATE ON Reminders
                BEGIN
                    INSERT INTO ReminderUpdateAudit (Id) VALUES (NULL);
                END;
                """;
            await audit.ExecuteNonQueryAsync();
        }

        var result = await service.ReconcileAsync();

        Assert.Equal(0, result.MissedCount);
        Assert.Equal(0, result.ScheduledCount);
        Assert.Equal(0, result.CancelledCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM ReminderUpdateAudit;"));
    }

    [Fact]
    public async Task ConfirmManualReminder_WithoutCandidates_PersistsAndSchedules()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var seeded = await SeedCandidateAsync(root.Paths);
        var now = new DateTimeOffset(2026, 7, 29, 0, 0, 0, TimeSpan.Zero);
        var scheduler = new FakeReminderNotificationScheduler();
        using var service = new SqliteReminderService(
            root.Paths,
            scheduler,
            new MutableTimeProvider(now));

        var reminder = await service.ConfirmAsync(new ReminderConfirmation(
            seeded.ImageItemId,
            DateCandidateId: null,
            LocationCandidateId: null,
            "Manually added reminder",
            new DateTime(2026, 8, 1, 9, 30, 0),
            "UTC",
            "Meeting room"));
        var result = await service.ReconcileAsync();

        Assert.Equal(ReminderState.Active, reminder.State);
        Assert.Null(reminder.NotificationLastErrorCode);
        Assert.Equal(1, result.ScheduledCount);
        Assert.Contains(reminder.SchedulerId, scheduler.ScheduledIds);
        Assert.Equal(
            "Manually added reminder",
            scheduler.Notifications[reminder.SchedulerId].Title);
        Assert.Equal("Meeting room", reminder.ConfirmedLocation);

        await using var connection = await OpenAsync(root.Paths.DatabasePath);
        Assert.Equal(2L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM EntityCandidates WHERE CandidateStatus = 1;"));
        Assert.Equal(1L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM Reminders WHERE SourceDateCandidateId IS NULL AND SourceLocationCandidateId IS NULL;"));
        Assert.Equal(1L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM ImageItems WHERE Title = 'Sample event' AND TitleSource = 1;"));
    }

    [Fact]
    public async Task UpdateReminder_ReusesStableSchedulerIdAndReplacesSchedule()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var seeded = await SeedCandidateAsync(root.Paths);
        var now = DateTimeOffset.UtcNow;
        var clock = new MutableTimeProvider(now);
        var scheduler = new FakeReminderNotificationScheduler();
        using var service = new SqliteReminderService(root.Paths, scheduler, clock);
        var reminder = await service.ConfirmAsync(new ReminderConfirmation(
            seeded.ImageItemId,
            seeded.DateCandidateId,
            seeded.LocationCandidateId,
            "Sample event",
            now.UtcDateTime.AddDays(30),
            "UTC",
            "Room 204"));
        await service.ReconcileAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var updated = await service.UpdateAsync(new ReminderUpdate(
            reminder.Id,
            "Renamed event",
            new DateTime(2026, 9, 16, 9, 30, 0),
            "UTC",
            "Room 305"));
        await service.ReconcileAsync();

        Assert.Equal(reminder.SchedulerId, updated.SchedulerId);
        Assert.Equal("Renamed event", updated.ImageTitle);
        Assert.Equal("Room 305", updated.ConfirmedLocation);
        Assert.Single(scheduler.ScheduledIds);
        Assert.Equal(
            "Renamed event",
            scheduler.Notifications[reminder.SchedulerId].Title);
        Assert.Equal(
            new DateTimeOffset(2026, 9, 16, 9, 30, 0, TimeSpan.Zero),
            scheduler.Notifications[reminder.SchedulerId].DueAtUtc);
        await using var connection = await OpenAsync(root.Paths.DatabasePath);
        Assert.Equal(1L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM ImageItems WHERE Title = 'Sample event' AND TitleSource = 1;"));
    }

    [Fact]
    public async Task DeleteReminder_CancelsProjectionWithoutDeletingImage()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var seeded = await SeedCandidateAsync(root.Paths);
        var now = new DateTimeOffset(2026, 7, 29, 0, 0, 0, TimeSpan.Zero);
        var scheduler = new FakeReminderNotificationScheduler();
        using var service = new SqliteReminderService(
            root.Paths,
            scheduler,
            new MutableTimeProvider(now));
        var reminder = await service.ConfirmAsync(new ReminderConfirmation(
            seeded.ImageItemId,
            seeded.DateCandidateId,
            seeded.LocationCandidateId,
            "Sample event",
            new DateTime(2026, 9, 15, 12, 0, 0),
            "UTC",
            "Room 204"));
        await service.ReconcileAsync();

        await service.DeleteAsync(reminder.Id);
        await service.ReconcileAsync();

        Assert.Empty(await service.GetRemindersAsync());
        Assert.Empty(scheduler.ScheduledIds);
        await using var connection = await OpenAsync(root.Paths.DatabasePath);
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM ImageItems;"));
        Assert.Equal(2L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM EntityCandidates WHERE CandidateStatus = 3 AND ConfirmedReminderId IS NULL;"));
        Assert.Equal(1L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM ReminderNotificationOutbox WHERE Operation = 2 AND State = 3 AND ReminderId IS NULL;"));
    }

    [Fact]
    public async Task NotificationActivation_RequiresMatchingActiveReminderAndImage()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var seeded = await SeedCandidateAsync(root.Paths);
        var now = new DateTimeOffset(2026, 7, 29, 0, 0, 0, TimeSpan.Zero);
        using var service = new SqliteReminderService(
            root.Paths,
            new FakeReminderNotificationScheduler(),
            new MutableTimeProvider(now));
        var reminder = await service.ConfirmAsync(new ReminderConfirmation(
            seeded.ImageItemId,
            seeded.DateCandidateId,
            seeded.LocationCandidateId,
            "Sample event",
            new DateTime(2026, 9, 15, 12, 0, 0),
            "UTC",
            null));

        Assert.False(await service.MarkActivatedAsync(reminder.Id, Guid.NewGuid()));
        Assert.Equal(ReminderState.Active, Assert.Single(await service.GetRemindersAsync()).State);
        Assert.True(await service.MarkActivatedAsync(reminder.Id, seeded.ImageItemId));
        var activated = Assert.Single(await service.GetRemindersAsync());
        Assert.Equal(ReminderState.Completed, activated.State);
        Assert.Equal(ReminderNotificationState.Activated, activated.NotificationState);
        Assert.False(await service.MarkActivatedAsync(reminder.Id, seeded.ImageItemId));
    }

    [Fact]
    public async Task SoftDelete_CancelsSystemNotification_AndRestoreRequiresReconfirmation()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var seeded = await SeedCandidateAsync(root.Paths);
        var now = DateTimeOffset.UtcNow;
        var clock = new MutableTimeProvider(now);
        var scheduler = new FakeReminderNotificationScheduler();
        using var service = new SqliteReminderService(root.Paths, scheduler, clock);
        var reminder = await service.ConfirmAsync(new ReminderConfirmation(
            seeded.ImageItemId,
            seeded.DateCandidateId,
            seeded.LocationCandidateId,
            "Sample event",
            now.UtcDateTime.AddDays(30),
            "UTC",
            null));
        await service.ReconcileAsync();
        var library = new LibraryService(
            root.Paths,
            new ManagedImageStorage(root.Paths),
            service);

        await library.SoftDeleteAsync(seeded.ImageItemId).WaitAsync(TimeSpan.FromSeconds(5));
        clock.SetUtcNow(DateTimeOffset.UtcNow.AddSeconds(1));
        await service.ReconcileAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(scheduler.ScheduledIds);
        Assert.Equal(
            ReminderState.SuspendedByDeletion,
            Assert.Single(await service.GetRemindersAsync()).State);

        await library.RestoreAsync(seeded.ImageItemId).WaitAsync(TimeSpan.FromSeconds(5));
        await service.ReconcileAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var restored = Assert.Single(await service.GetRemindersAsync());
        Assert.Equal(reminder.Id, restored.Id);
        Assert.Equal(ReminderState.NeedsReconfirmation, restored.State);
        Assert.Empty(scheduler.ScheduledIds);
    }

    [Fact]
    public async Task StartupReconciliation_MarksElapsedDeliveryWindowAsMissed()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var seeded = await SeedCandidateAsync(root.Paths);
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 28, 0, 0, 0, TimeSpan.Zero));
        var scheduler = new FakeReminderNotificationScheduler();
        using var service = new SqliteReminderService(root.Paths, scheduler, clock);
        await service.ConfirmAsync(new ReminderConfirmation(
            seeded.ImageItemId,
            seeded.DateCandidateId,
            seeded.LocationCandidateId,
            "Sample event",
            new DateTime(2026, 7, 28, 1, 0, 0),
            "UTC",
            null));
        await service.ReconcileAsync();
        clock.SetUtcNow(new DateTimeOffset(2026, 7, 28, 1, 6, 0, TimeSpan.Zero));

        var result = await service.ReconcileAsync();

        Assert.Equal(1, result.MissedCount);
        Assert.Equal(ReminderState.Missed, Assert.Single(await service.GetRemindersAsync()).State);
        Assert.Empty(scheduler.ScheduledIds);
    }

    [Fact]
    public async Task Reconciliation_SchedulerEnumerationFailure_DoesNotHideDatabaseRecords()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var seeded = await SeedCandidateAsync(root.Paths);
        var now = new DateTimeOffset(2026, 7, 28, 0, 0, 0, TimeSpan.Zero);
        var scheduler = new FakeReminderNotificationScheduler
        {
            ThrowWhenReadingScheduledIds = true,
        };
        using var service = new SqliteReminderService(
            root.Paths,
            scheduler,
            new MutableTimeProvider(now));
        var reminder = await service.ConfirmAsync(new ReminderConfirmation(
            seeded.ImageItemId,
            seeded.DateCandidateId,
            seeded.LocationCandidateId,
            "Sample event",
            new DateTime(2026, 9, 15, 12, 0, 0),
            "UTC",
            null));

        var result = await service.ReconcileAsync();

        Assert.False(result.NotificationsSupported);
        Assert.Equal(reminder.Id, Assert.Single(await service.GetRemindersAsync()).Id);
        Assert.Contains(reminder.SchedulerId, scheduler.ScheduledIds);
    }

    [Fact]
    public async Task Confirm_RejectsAmbiguousDaylightSavingTime()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var seeded = await SeedCandidateAsync(root.Paths);
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var service = new SqliteReminderService(
            root.Paths,
            new FakeReminderNotificationScheduler(),
            clock);

        var exception = await Assert.ThrowsAsync<ReminderValidationException>(() =>
            service.ConfirmAsync(new ReminderConfirmation(
                seeded.ImageItemId,
                seeded.DateCandidateId,
                seeded.LocationCandidateId,
                "Sample event",
                new DateTime(2026, 11, 1, 1, 30, 0),
                "Eastern Standard Time",
                null)));

        Assert.Equal("DaylightSavingAmbiguousTime", exception.ErrorCode);
        Assert.Empty(await service.GetRemindersAsync());
    }

    [Fact]
    public async Task Confirm_RejectsMissingOrOversizedEditedTitle()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var seeded = await SeedCandidateAsync(root.Paths);
        using var service = new SqliteReminderService(
            root.Paths,
            new FakeReminderNotificationScheduler(),
            new MutableTimeProvider(
                new DateTimeOffset(2026, 7, 29, 0, 0, 0, TimeSpan.Zero)));

        var missing = await Assert.ThrowsAsync<ReminderValidationException>(() =>
            service.ConfirmAsync(new ReminderConfirmation(
                seeded.ImageItemId,
                seeded.DateCandidateId,
                seeded.LocationCandidateId,
                "  ",
                new DateTime(2026, 9, 15, 12, 0, 0),
                "UTC",
                null)));
        var oversized = await Assert.ThrowsAsync<ReminderValidationException>(() =>
            service.ConfirmAsync(new ReminderConfirmation(
                seeded.ImageItemId,
                seeded.DateCandidateId,
                seeded.LocationCandidateId,
                new string('a', 301),
                new DateTime(2026, 9, 15, 12, 0, 0),
                "UTC",
                null)));

        Assert.Equal("TitleRequired", missing.ErrorCode);
        Assert.Equal("TitleTooLong", oversized.ErrorCode);
        Assert.Empty(await service.GetRemindersAsync());
    }

    private static async Task<SeededCandidate> SeedCandidateAsync(AppDataPaths paths)
    {
        var assetId = Guid.NewGuid();
        var imageItemId = Guid.NewGuid();
        var analysisJobId = Guid.NewGuid();
        var dateCandidateId = Guid.NewGuid();
        var locationCandidateId = Guid.NewGuid();
        var now = "2026-07-28T00:00:00.0000000+00:00";
        await using var connection = await OpenAsync(paths.DatabasePath);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO ImageAssets (
                Id, ContentHash, OriginalRelativePath, ThumbnailRelativePath,
                MediaType, ByteLength, PixelWidth, PixelHeight, CreatedAtUtc)
            VALUES (
                @assetId, @hash, @original, NULL,
                'image/png', 1, 100, 100, @now);
            INSERT INTO ImageItems (
                Id, AssetId, OriginalFileName, SourceKind, Title, Summary,
                TitleSource, SummarySource, AnalysisState, Revision,
                CreatedAtUtc, UpdatedAtUtc, DeletedAtUtc)
            VALUES (
                @imageItemId, @assetId, 'sample.png', 1, 'Sample event', '',
                1, 1, 4, 0, @now, @now, NULL);
            INSERT INTO AnalysisJobs (
                Id, ImageItemId, Kind, InputRevision, State, AttemptCount,
                NotBeforeUtc, LeaseExpiresAtUtc, LastErrorCode,
                CreatedAtUtc, UpdatedAtUtc, CompletedAtUtc,
                CurrentStage, LeaseOwner, AnalysisMode, ProfileRevision,
                ModelProfileSnapshotJson)
            VALUES (
                @jobId, @imageItemId, 1, 0, 4, 1,
                @now, NULL, NULL, @now, @now, @now,
                4, NULL, 2, 1, '{}');
            INSERT INTO EntityCandidates (
                Id, AnalysisJobId, ImageItemId, Kind, RawText,
                NormalizedValue, Evidence, Source, GeneratedAtUtc,
                CandidateStatus, BoundingBoxJson, ReferenceTimeUtc,
                TimeZoneId, AmbiguityReason, ConfirmedReminderId)
            VALUES (
                @dateCandidateId, @jobId, @imageItemId, 'DateTime', '2026年9月15日',
                '2026-09-15', '发布于 2026年9月15日', 'Ocr', @now,
                1, '{"x":0.1,"y":0.2,"width":0.8,"height":0.1}', @now,
                'UTC', NULL, NULL),
                (@locationCandidateId, @jobId, @imageItemId, 'Location', '会 议 室',
                '会议室', '在 会 议 室', 'Ocr', @now,
                1, NULL, @now, 'UTC', NULL, NULL);
            """;
        command.Parameters.AddWithValue("@assetId", assetId.ToString("D"));
        command.Parameters.AddWithValue("@imageItemId", imageItemId.ToString("D"));
        command.Parameters.AddWithValue("@jobId", analysisJobId.ToString("D"));
        command.Parameters.AddWithValue("@dateCandidateId", dateCandidateId.ToString("D"));
        command.Parameters.AddWithValue("@locationCandidateId", locationCandidateId.ToString("D"));
        command.Parameters.AddWithValue("@hash", new string('a', 64));
        command.Parameters.AddWithValue("@original", $"assets/originals/{assetId:N}.png");
        command.Parameters.AddWithValue("@now", now);
        await command.ExecuteNonQueryAsync();
        return new SeededCandidate(imageItemId, dateCandidateId, locationCandidateId);
    }

    private static async Task<SqliteConnection> OpenAsync(string path)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        await command.ExecuteNonQueryAsync();
        return connection;
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private sealed record SeededCandidate(
        Guid ImageItemId,
        Guid DateCandidateId,
        Guid LocationCandidateId);

    private sealed class MutableTimeProvider(DateTimeOffset nowUtc) : TimeProvider
    {
        private DateTimeOffset _nowUtc = nowUtc;

        public override DateTimeOffset GetUtcNow() => _nowUtc;

        public void SetUtcNow(DateTimeOffset value) => _nowUtc = value;
    }

    private sealed class FakeReminderNotificationScheduler : IReminderNotificationScheduler
    {
        public Dictionary<string, ReminderNotification> Notifications { get; } =
            new(StringComparer.Ordinal);

        public IReadOnlySet<string> ScheduledIds =>
            Notifications.Keys.ToHashSet(StringComparer.Ordinal);

        public bool ThrowWhenReadingScheduledIds { get; init; }

        public bool IsSupported => true;

        public Task<IReadOnlySet<string>> GetScheduledIdsAsync(
            CancellationToken cancellationToken = default)
        {
            if (ThrowWhenReadingScheduledIds)
            {
                throw new InvalidOperationException("Synthetic scheduler failure.");
            }

            return Task.FromResult(ScheduledIds);
        }

        public Task ScheduleAsync(
            ReminderNotification notification,
            CancellationToken cancellationToken = default)
        {
            Notifications[notification.SchedulerId] = notification;
            return Task.CompletedTask;
        }

        public Task CancelAsync(
            string schedulerId,
            CancellationToken cancellationToken = default)
        {
            Notifications.Remove(schedulerId);
            return Task.CompletedTask;
        }
    }
}
