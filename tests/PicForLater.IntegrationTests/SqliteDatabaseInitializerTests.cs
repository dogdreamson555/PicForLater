using Microsoft.Data.Sqlite;
using PicForLater.Infrastructure.Storage;
using System.Security.Cryptography;

namespace PicForLater.IntegrationTests;

public sealed class SqliteDatabaseInitializerTests
{
    [Fact]
    public async Task Initialize_CreatesCurrentSchemaWithoutBackupForANewDatabase()
    {
        using var temporaryRoot = new TemporaryAppDataRoot();
        var initializer = new SqliteDatabaseInitializer(temporaryRoot.Paths);

        var result = await initializer.InitializeAsync();

        Assert.Equal(0, result.PreviousVersion);
        Assert.Equal(12, result.CurrentVersion);
        Assert.Null(result.BackupFilePath);
        Assert.True(File.Exists(temporaryRoot.Paths.DatabasePath));

        await using var connection = await OpenAsync(temporaryRoot.Paths.DatabasePath);
        var tableNames = await ReadTableNamesAsync(connection);
        Assert.Contains("SchemaMigrations", tableNames);
        Assert.Contains("ImageAssets", tableNames);
        Assert.Contains("ImageItems", tableNames);
        Assert.Contains("ImportJobs", tableNames);
        Assert.Contains("AnalysisJobs", tableNames);
        Assert.Contains("Categories", tableNames);
        Assert.Contains("ImageCategories", tableNames);
        Assert.Contains("AnalysisStageResults", tableNames);
        Assert.Contains("AnalysisSettings", tableNames);
        Assert.Contains("ModelPackages", tableNames);
        Assert.Contains("ModelCapabilityProfiles", tableNames);
        Assert.Contains("RemoteApiProfiles", tableNames);
        Assert.Contains("EntityCandidates", tableNames);
        Assert.Contains("Reminders", tableNames);
        Assert.Contains("ReminderNotificationOutbox", tableNames);
        Assert.Contains("DeletionJobs", tableNames);
        Assert.Equal(12L, await ExecuteScalarLongAsync(connection, "SELECT COUNT(*) FROM SchemaMigrations;"));
        Assert.Equal(12L, await ExecuteScalarLongAsync(connection, "PRAGMA user_version;"));
    }

    [Fact]
    public async Task Initialize_IsIdempotentWhenSchemaIsCurrent()
    {
        using var temporaryRoot = new TemporaryAppDataRoot();
        var initializer = new SqliteDatabaseInitializer(temporaryRoot.Paths);
        await initializer.InitializeAsync();

        var secondResult = await initializer.InitializeAsync();

        Assert.Equal(12, secondResult.PreviousVersion);
        Assert.Equal(12, secondResult.CurrentVersion);
        Assert.Null(secondResult.BackupFilePath);
        Assert.Empty(Directory.EnumerateFiles(temporaryRoot.Paths.BackupDirectoryPath));
    }

    [Fact]
    public async Task Initialize_RejectsChangedMigrationChecksum()
    {
        using var temporaryRoot = new TemporaryAppDataRoot();
        var initializer = new SqliteDatabaseInitializer(temporaryRoot.Paths);
        await initializer.InitializeAsync();
        await ExecuteNonQueryAsync(
            temporaryRoot.Paths.DatabasePath,
            "UPDATE SchemaMigrations SET SqlChecksum = 'changed';");
        var beforeFailure = SHA256.HashData(await File.ReadAllBytesAsync(temporaryRoot.Paths.DatabasePath));

        await Assert.ThrowsAsync<DatabaseSchemaException>(() => initializer.InitializeAsync());

        var afterFailure = SHA256.HashData(await File.ReadAllBytesAsync(temporaryRoot.Paths.DatabasePath));
        Assert.Equal(beforeFailure, afterFailure);
        Assert.Empty(Directory.EnumerateFiles(temporaryRoot.Paths.BackupDirectoryPath));
    }

    [Fact]
    public async Task Initialize_RejectsFutureMigrationHistory()
    {
        using var temporaryRoot = new TemporaryAppDataRoot();
        var initializer = new SqliteDatabaseInitializer(temporaryRoot.Paths);
        await initializer.InitializeAsync();
        await ExecuteNonQueryAsync(
            temporaryRoot.Paths.DatabasePath,
            """
            INSERT INTO SchemaMigrations (Version, Name, SqlChecksum, AppliedAtUtc)
            VALUES (99, 'future', 'future', '2026-07-17T00:00:00.0000000+00:00');
            """);

        await Assert.ThrowsAsync<DatabaseSchemaException>(() => initializer.InitializeAsync());
    }

    [Fact]
    public async Task Initialize_RejectsUserVersionThatDoesNotMatchHistory()
    {
        using var temporaryRoot = new TemporaryAppDataRoot();
        var initializer = new SqliteDatabaseInitializer(temporaryRoot.Paths);
        await initializer.InitializeAsync();
        await ExecuteNonQueryAsync(temporaryRoot.Paths.DatabasePath, "PRAGMA user_version = 99;");

        await Assert.ThrowsAsync<DatabaseSchemaException>(() => initializer.InitializeAsync());
    }

    [Fact]
    public async Task Migration3_SplitsLegacyAnalysisFactsAndPreservesProvenance()
    {
        using var temporaryRoot = new TemporaryAppDataRoot();
        var v2Initializer = new SqliteDatabaseInitializer(
            temporaryRoot.Paths,
            SqliteSchema.Migrations.Take(2).ToArray());
        await v2Initializer.InitializeAsync();
        var assetId = Guid.NewGuid().ToString("D");
        var itemId = Guid.NewGuid().ToString("D");
        var resultId = Guid.NewGuid().ToString("D");
        await ExecuteNonQueryAsync(
            temporaryRoot.Paths.DatabasePath,
            $"""
            INSERT INTO ImageAssets (
                Id, ContentHash, OriginalRelativePath, ThumbnailRelativePath, MediaType,
                ByteLength, PixelWidth, PixelHeight, CreatedAtUtc)
            VALUES (
                '{assetId}', '{new string('a', 64)}', 'assets/originals/legacy.png', NULL,
                'image/png', 1, 1, 1, '2026-07-18T00:00:00.0000000+00:00');
            INSERT INTO ImageItems (
                Id, AssetId, OriginalFileName, SourceKind, Title, Summary,
                TitleSource, SummarySource, AnalysisState, Revision,
                CreatedAtUtc, UpdatedAtUtc, DeletedAtUtc)
            VALUES (
                '{itemId}', '{assetId}', 'legacy.png', 1, 'legacy', '', 1, 1, 4, 0,
                '2026-07-18T00:00:00.0000000+00:00',
                '2026-07-18T00:00:00.0000000+00:00', NULL);
            INSERT INTO AnalysisResults (
                Id, ImageItemId, OcrText, VisualFacts, ModelId, ModelVersion,
                PromptSchemaVersion, Warnings, GeneratedAtUtc)
            VALUES (
                '{resultId}', '{itemId}', '原始 OCR 文本', '视觉事实',
                'legacy-model', '1.0', 'legacy.v1', 'legacy warning',
                '2026-07-18T00:00:00.0000000+00:00');
            """);

        var upgraded = await new SqliteDatabaseInitializer(
            temporaryRoot.Paths,
            SqliteSchema.Migrations.Take(3).ToArray()).InitializeAsync();

        Assert.Equal(2, upgraded.PreviousVersion);
        Assert.Equal(3, upgraded.CurrentVersion);
        Assert.NotNull(upgraded.BackupFilePath);
        await using var connection = await OpenAsync(temporaryRoot.Paths.DatabasePath);
        var tables = await ReadTableNamesAsync(connection);
        Assert.DoesNotContain("AnalysisResults", tables);
        Assert.Contains("AnalysisStageResults", tables);
        Assert.Equal(2L, await ExecuteScalarLongAsync(
            connection,
            "SELECT COUNT(*) FROM AnalysisStageResults;"));
        Assert.Equal("原始 OCR 文本", await ExecuteScalarStringAsync(
            connection,
            "SELECT FactText FROM AnalysisStageResults WHERE Stage = 1;"));
        Assert.Equal("legacy-model", await ExecuteScalarStringAsync(
            connection,
            "SELECT ModelId FROM AnalysisStageResults WHERE Stage = 3;"));
        Assert.Equal("legacy warning", await ExecuteScalarStringAsync(
            connection,
            "SELECT json_extract(WarningsJson, '$[0]') FROM AnalysisStageResults WHERE Stage = 1;"));
    }

    [Fact]
    public async Task Migration5_PreservesExistingCandidatesAndReminders()
    {
        using var temporaryRoot = new TemporaryAppDataRoot();
        var v4Initializer = new SqliteDatabaseInitializer(
            temporaryRoot.Paths,
            SqliteSchema.Migrations.Take(4).ToArray());
        await v4Initializer.InitializeAsync();
        var assetId = Guid.NewGuid().ToString("D");
        var itemId = Guid.NewGuid().ToString("D");
        var jobId = Guid.NewGuid().ToString("D");
        var candidateId = Guid.NewGuid().ToString("D");
        var reminderId = Guid.NewGuid().ToString("D");
        const string now = "2026-07-28T00:00:00.0000000+00:00";
        await ExecuteNonQueryAsync(
            temporaryRoot.Paths.DatabasePath,
            $"""
            INSERT INTO ImageAssets (
                Id, ContentHash, OriginalRelativePath, ThumbnailRelativePath, MediaType,
                ByteLength, PixelWidth, PixelHeight, CreatedAtUtc)
            VALUES (
                '{assetId}', '{new string('b', 64)}', 'assets/originals/migration5.png', NULL,
                'image/png', 1, 1, 1, '{now}');
            INSERT INTO ImageItems (
                Id, AssetId, OriginalFileName, SourceKind, Title, Summary,
                TitleSource, SummarySource, AnalysisState, Revision,
                CreatedAtUtc, UpdatedAtUtc, DeletedAtUtc)
            VALUES (
                '{itemId}', '{assetId}', 'migration5.png', 1, 'Migration 5', '',
                1, 1, 4, 0, '{now}', '{now}', NULL);
            INSERT INTO AnalysisJobs (
                Id, ImageItemId, Kind, InputRevision, State, AttemptCount,
                NotBeforeUtc, LeaseExpiresAtUtc, LastErrorCode,
                CreatedAtUtc, UpdatedAtUtc, CompletedAtUtc,
                CurrentStage, LeaseOwner, AnalysisMode, ProfileRevision,
                ModelProfileSnapshotJson)
            VALUES (
                '{jobId}', '{itemId}', 1, 0, 4, 1,
                '{now}', NULL, NULL, '{now}', '{now}', '{now}',
                4, NULL, 2, 1, json_object());
            INSERT INTO EntityCandidates (
                Id, AnalysisJobId, ImageItemId, Kind, RawText,
                NormalizedValue, Evidence, Source, GeneratedAtUtc)
            VALUES (
                '{candidateId}', '{jobId}', '{itemId}', 'DateTime',
                '2026年9月15日', '2026-09-15',
                '发布于 2026年9月15日', 'Ocr', '{now}');
            INSERT INTO Reminders (
                Id, ImageItemId, DueAtUtc, TimeZoneId, ConfirmedLocation,
                SchedulerId, State, CreatedAtUtc, UpdatedAtUtc)
            VALUES (
                '{reminderId}', '{itemId}', '2026-09-15T04:00:00.0000000+00:00',
                'China Standard Time', NULL, 'migration5testid', 1, '{now}', '{now}');
            """);

        var upgraded = await new SqliteDatabaseInitializer(temporaryRoot.Paths)
            .InitializeAsync();

        Assert.Equal(4, upgraded.PreviousVersion);
        Assert.Equal(12, upgraded.CurrentVersion);
        Assert.NotNull(upgraded.BackupFilePath);
        await using var connection = await OpenAsync(temporaryRoot.Paths.DatabasePath);
        Assert.Equal(1L, await ExecuteScalarLongAsync(
            connection,
            $"SELECT CandidateStatus FROM EntityCandidates WHERE Id = '{candidateId}';"));
        Assert.Equal(1L, await ExecuteScalarLongAsync(
            connection,
            $"SELECT NotificationState FROM Reminders WHERE Id = '{reminderId}';"));
        Assert.Equal(1L, await ExecuteScalarLongAsync(
            connection,
            $"SELECT COUNT(*) FROM Reminders WHERE Id = '{reminderId}' AND Title IS NULL;"));
        Assert.Contains("ReminderNotificationOutbox", await ReadTableNamesAsync(connection));
    }

    [Fact]
    public async Task Migration7_BackfillsExplicitLocalExecutionAndStageOutputKinds()
    {
        using var temporaryRoot = new TemporaryAppDataRoot();
        var v6Initializer = new SqliteDatabaseInitializer(
            temporaryRoot.Paths,
            SqliteSchema.Migrations.Take(6).ToArray());
        await v6Initializer.InitializeAsync();
        var assetId = Guid.NewGuid().ToString("D");
        var itemId = Guid.NewGuid().ToString("D");
        const string now = "2026-07-31T00:00:00.0000000+00:00";
        await ExecuteNonQueryAsync(
            temporaryRoot.Paths.DatabasePath,
            $$"""
            INSERT INTO ImageAssets (
                Id, ContentHash, OriginalRelativePath, ThumbnailRelativePath, MediaType,
                ByteLength, PixelWidth, PixelHeight, CreatedAtUtc)
            VALUES (
                '{{assetId}}', '{{new string('c', 64)}}', 'assets/originals/provenance.png', NULL,
                'image/png', 1, 1, 1, '{{now}}');
            INSERT INTO ImageItems (
                Id, AssetId, OriginalFileName, SourceKind, Title, Summary,
                TitleSource, SummarySource, AnalysisState, Revision,
                CreatedAtUtc, UpdatedAtUtc, DeletedAtUtc)
            VALUES (
                '{{itemId}}', '{{assetId}}', 'provenance.png', 1, 'Provenance', '',
                1, 1, 4, 0, '{{now}}', '{{now}}', NULL);
            INSERT INTO AnalysisStageResults (
                Id, AnalysisJobId, ImageItemId, Stage, InputRevision,
                ProviderId, ModelId, ModelVersion, ModelFileHashesJson,
                LanguageTagsJson, SchemaVersion, PayloadJson, FactText,
                WarningsJson, GeneratedAtUtc)
            VALUES
                ('ocr', NULL, '{{itemId}}', 1, 0, 'opaque.ocr', 'ocr-model', '1',
                 '{}', '[]', 'ocr.v1', '{}', 'ocr', '[]', '{{now}}'),
                ('entities', NULL, '{{itemId}}', 2, 0, 'opaque.entities', 'entity-model', '1',
                 '{}', '[]', 'entities.v1', '{}', 'date', '[]', '{{now}}'),
                ('routing', NULL, '{{itemId}}', 3, 0, 'opaque.router', NULL, NULL,
                 '{}', '[]', 'routing.v1', '{}', '', '[]', '{{now}}'),
                ('vision', NULL, '{{itemId}}', 3, 0, 'opaque.generator', 'vision-model', '1',
                 '{}', '[]', 'vision.v1', '{}', 'fact', '[]', '{{now}}'),
                ('composition', NULL, '{{itemId}}', 4, 0, 'opaque.extractive', NULL, NULL,
                 '{}', '[]', 'composition.v1', '{}', 'draft', '[]', '{{now}}');
            """);

        var upgraded = await new SqliteDatabaseInitializer(
                temporaryRoot.Paths,
                SqliteSchema.Migrations.Take(7).ToArray())
            .InitializeAsync();

        Assert.Equal(6, upgraded.PreviousVersion);
        Assert.Equal(7, upgraded.CurrentVersion);
        Assert.NotNull(upgraded.BackupFilePath);
        await using var connection = await OpenAsync(temporaryRoot.Paths.DatabasePath);
        Assert.Equal(5L, await ExecuteScalarLongAsync(
            connection,
            "SELECT COUNT(*) FROM AnalysisStageResults WHERE ExecutionLocation = 0;"));
        Assert.Equal(1L, await ExecuteScalarLongAsync(
            connection,
            "SELECT COUNT(*) FROM AnalysisStageResults WHERE Id = 'ocr' AND OutputKind = 1;"));
        Assert.Equal(1L, await ExecuteScalarLongAsync(
            connection,
            "SELECT COUNT(*) FROM AnalysisStageResults WHERE Id = 'entities' AND OutputKind = 2;"));
        Assert.Equal(1L, await ExecuteScalarLongAsync(
            connection,
            "SELECT COUNT(*) FROM AnalysisStageResults WHERE Id = 'routing' AND OutputKind = 3;"));
        Assert.Equal(1L, await ExecuteScalarLongAsync(
            connection,
            "SELECT COUNT(*) FROM AnalysisStageResults WHERE Id = 'vision' AND OutputKind = 4;"));
        Assert.Equal(1L, await ExecuteScalarLongAsync(
            connection,
            "SELECT COUNT(*) FROM AnalysisStageResults WHERE Id = 'composition' AND OutputKind = 5;"));
    }

    [Fact]
    public async Task Migration9_AddsNullableRemoteInputModeProvenanceIncrementally()
    {
        using var temporaryRoot = new TemporaryAppDataRoot();
        var v8Initializer = new SqliteDatabaseInitializer(
            temporaryRoot.Paths,
            SqliteSchema.Migrations.Take(8).ToArray());
        await v8Initializer.InitializeAsync();

        var upgraded = await new SqliteDatabaseInitializer(
                temporaryRoot.Paths,
                SqliteSchema.Migrations.Take(9).ToArray())
            .InitializeAsync();

        Assert.Equal(8, upgraded.PreviousVersion);
        Assert.Equal(9, upgraded.CurrentVersion);
        Assert.NotNull(upgraded.BackupFilePath);
        await using var connection = await OpenAsync(temporaryRoot.Paths.DatabasePath);
        Assert.Equal(1L, await ExecuteScalarLongAsync(
            connection,
            """
            SELECT COUNT(*)
            FROM pragma_table_info('AnalysisStageResults')
            WHERE name = 'RemoteInputMode' AND "notnull" = 0;
            """));
        Assert.Equal(9L, await ExecuteScalarLongAsync(connection, "PRAGMA user_version;"));
    }

    [Fact]
    public async Task Migration10_AddsCompletedStageOutcomeWithoutChangingExistingRows()
    {
        using var temporaryRoot = new TemporaryAppDataRoot();
        var v9Initializer = new SqliteDatabaseInitializer(
            temporaryRoot.Paths,
            SqliteSchema.Migrations.Take(9).ToArray());
        await v9Initializer.InitializeAsync();
        var assetId = Guid.NewGuid().ToString("D");
        var itemId = Guid.NewGuid().ToString("D");
        const string now = "2026-07-31T00:00:00.0000000+00:00";
        await ExecuteNonQueryAsync(
            temporaryRoot.Paths.DatabasePath,
            $$"""
            INSERT INTO ImageAssets (
                Id, ContentHash, OriginalRelativePath, ThumbnailRelativePath, MediaType,
                ByteLength, PixelWidth, PixelHeight, CreatedAtUtc)
            VALUES (
                '{{assetId}}', '{{new string('d', 64)}}', 'assets/originals/outcome.png', NULL,
                'image/png', 1, 1, 1, '{{now}}');
            INSERT INTO ImageItems (
                Id, AssetId, OriginalFileName, SourceKind, Title, Summary,
                TitleSource, SummarySource, AnalysisState, Revision,
                CreatedAtUtc, UpdatedAtUtc, DeletedAtUtc)
            VALUES (
                '{{itemId}}', '{{assetId}}', 'outcome.png', 1, 'Outcome', '',
                1, 1, 4, 0, '{{now}}', '{{now}}', NULL);
            INSERT INTO AnalysisStageResults (
                Id, AnalysisJobId, ImageItemId, Stage, InputRevision,
                ProviderId, ModelId, ModelVersion, ModelFileHashesJson,
                ExecutionLocation, OutputKind, RemoteInputMode,
                LanguageTagsJson, SchemaVersion, PayloadJson, FactText,
                WarningsJson, GeneratedAtUtc)
            VALUES (
                'existing-stage', NULL, '{{itemId}}', 1, 0,
                'opaque.ocr', 'ocr-model', '1', '{}',
                0, 1, NULL, '[]', 'ocr.v1', '{}', 'existing OCR',
                '[]', '{{now}}');
            """);

        var upgraded = await new SqliteDatabaseInitializer(
                temporaryRoot.Paths,
                SqliteSchema.Migrations.Take(10).ToArray())
            .InitializeAsync();

        Assert.Equal(9, upgraded.PreviousVersion);
        Assert.Equal(10, upgraded.CurrentVersion);
        Assert.NotNull(upgraded.BackupFilePath);
        await using var connection = await OpenAsync(temporaryRoot.Paths.DatabasePath);
        Assert.Equal(1L, await ExecuteScalarLongAsync(
            connection,
            """
            SELECT COUNT(*)
            FROM pragma_table_info('AnalysisStageResults')
            WHERE name = 'StageOutcome' AND "notnull" = 1 AND dflt_value = '0';
            """));
        Assert.Equal(0L, await ExecuteScalarLongAsync(
            connection,
            "SELECT StageOutcome FROM AnalysisStageResults WHERE Id = 'existing-stage';"));
        Assert.Equal(
            "existing OCR",
            await ExecuteScalarStringAsync(
                connection,
                "SELECT FactText FROM AnalysisStageResults WHERE Id = 'existing-stage';"));
    }

    [Fact]
    public async Task Migration12_PreservesPublishedV11AndExpandsStructuredOutputModeIncrementally()
    {
        const string publishedV11Checksum =
            "1a680f4105e99ec749e8d4749dcb7dd0a87be968ee54bb54809e5ddd84a48a0f";
        Assert.Equal(publishedV11Checksum, SqliteSchema.Migrations[10].Checksum);

        using var temporaryRoot = new TemporaryAppDataRoot();
        var v11Initializer = new SqliteDatabaseInitializer(
            temporaryRoot.Paths,
            SqliteSchema.Migrations.Take(11).ToArray());
        await v11Initializer.InitializeAsync();
        await ExecuteNonQueryAsync(
            temporaryRoot.Paths.DatabasePath,
            """
            INSERT INTO RemoteApiProfiles (
                ProfileId, ProviderId, DisplayName, EndpointId, BaseUri, ModelId,
                SupportedInputModesJson, PromptVersion, OutputSchemaVersion,
                MaxTextChars, MaxImageBytes, MaxOutputTokens, TimeoutSeconds,
                PrivacyUrl, TermsUrl, RetentionTrainingStatement,
                RetentionTrainingVerifiedAtUtc, CredentialReference, DisclosureVersion,
                IsEnabled, ValidationState, LastVerifiedAtUtc, ConsentedInputMode,
                ConsentedDisclosureVersion, ConsentGrantedAtUtc, UpdatedAtUtc,
                Protocol, AuthenticationKind, StructuredOutputMode, EndpointTrustMode,
                ApiVersion, DisableProviderFallbacks, DisableExternalSearch)
            VALUES (
                'legacy-v11', 'legacy.provider', 'Legacy provider', 'legacy.endpoint',
                'https://api.example.com/v1/chat/completions', 'legacy-model', '[1]',
                'prompt.v1', 'schema.v1', 64000, 8388608, 1024, 60,
                'https://example.com/privacy', 'https://example.com/terms',
                'Legacy policy statement', '2026-08-01T00:00:00.0000000+00:00',
                'legacy-credential', 'legacy-disclosure.v1', 1, 0, NULL, NULL,
                NULL, NULL, '2026-08-01T00:00:00.0000000+00:00',
                0, 0, 1, 0, NULL, 0, 0);
            """);

        var upgraded = await new SqliteDatabaseInitializer(temporaryRoot.Paths)
            .InitializeAsync();

        Assert.Equal(11, upgraded.PreviousVersion);
        Assert.Equal(12, upgraded.CurrentVersion);
        Assert.NotNull(upgraded.BackupFilePath);
        await using var connection = await OpenAsync(temporaryRoot.Paths.DatabasePath);
        Assert.Equal(1L, await ExecuteScalarLongAsync(
            connection,
            "SELECT StructuredOutputMode FROM RemoteApiProfiles WHERE ProfileId = 'legacy-v11';"));
        Assert.Equal(1L, await ExecuteScalarLongAsync(
            connection,
            "SELECT StructuredOutputModeV2 FROM RemoteApiProfiles WHERE ProfileId = 'legacy-v11';"));
        await ExecuteNonQueryAsync(
            temporaryRoot.Paths.DatabasePath,
            "UPDATE RemoteApiProfiles SET StructuredOutputModeV2 = 2 WHERE ProfileId = 'legacy-v11';");
        Assert.Equal(2L, await ExecuteScalarLongAsync(
            connection,
            "SELECT StructuredOutputModeV2 FROM RemoteApiProfiles WHERE ProfileId = 'legacy-v11';"));
    }

    [Fact]
    public async Task PendingMigration_CreatesVerifiedBackupBeforeCommit()
    {
        using var temporaryRoot = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(temporaryRoot.Paths).InitializeAsync();
        var migrations = SqliteSchema.Migrations
            .Concat([new SqliteMigration(13, "test-upgrade", "CREATE TABLE UpgradeMarker (Id INTEGER PRIMARY KEY);")])
            .ToArray();
        var upgradingInitializer = new SqliteDatabaseInitializer(temporaryRoot.Paths, migrations);

        var result = await upgradingInitializer.InitializeAsync();

        Assert.Equal(12, result.PreviousVersion);
        Assert.Equal(13, result.CurrentVersion);
        Assert.NotNull(result.BackupFilePath);
        Assert.True(File.Exists(result.BackupFilePath));

        await using var backup = await OpenAsync(result.BackupFilePath!, readOnly: true);
        Assert.Equal(12L, await ExecuteScalarLongAsync(backup, "PRAGMA user_version;"));
        Assert.Equal("ok", await ExecuteScalarStringAsync(backup, "PRAGMA quick_check;"));

        await using var upgraded = await OpenAsync(temporaryRoot.Paths.DatabasePath);
        Assert.Contains("UpgradeMarker", await ReadTableNamesAsync(upgraded));
        Assert.Equal(13L, await ExecuteScalarLongAsync(upgraded, "PRAGMA user_version;"));
    }

    [Fact]
    public async Task FailedMigration_RollsBackAndPreservesMainDatabaseAndBackup()
    {
        using var temporaryRoot = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(temporaryRoot.Paths).InitializeAsync();
        var migrations = SqliteSchema.Migrations
            .Concat(
            [
                new SqliteMigration(
                    13,
                    "broken-test-upgrade",
                    "CREATE TABLE MustRollback (Id INTEGER PRIMARY KEY); THIS IS NOT SQL;"),
            ])
            .ToArray();
        var upgradingInitializer = new SqliteDatabaseInitializer(temporaryRoot.Paths, migrations);

        await Assert.ThrowsAsync<DatabaseMigrationException>(() => upgradingInitializer.InitializeAsync());

        var backups = Directory.EnumerateFiles(temporaryRoot.Paths.BackupDirectoryPath, "*.db").ToArray();
        Assert.Single(backups);
        await using var backup = await OpenAsync(backups[0], readOnly: true);
        Assert.Equal("ok", await ExecuteScalarStringAsync(backup, "PRAGMA quick_check;"));

        await using var main = await OpenAsync(temporaryRoot.Paths.DatabasePath);
        Assert.DoesNotContain("MustRollback", await ReadTableNamesAsync(main));
        Assert.Equal(12L, await ExecuteScalarLongAsync(main, "SELECT MAX(Version) FROM SchemaMigrations;"));
        Assert.Equal(12L, await ExecuteScalarLongAsync(main, "PRAGMA user_version;"));
        Assert.Equal("ok", await ExecuteScalarStringAsync(main, "PRAGMA quick_check;"));
    }

    [Fact]
    public async Task ConcurrentPendingMigration_IsAppliedOnce()
    {
        using var temporaryRoot = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(temporaryRoot.Paths).InitializeAsync();
        var migrations = SqliteSchema.Migrations
            .Concat([new SqliteMigration(13, "concurrent-test-upgrade", "CREATE TABLE ConcurrentMarker (Id INTEGER PRIMARY KEY);")])
            .ToArray();
        var firstInitializer = new SqliteDatabaseInitializer(temporaryRoot.Paths, migrations);
        var secondInitializer = new SqliteDatabaseInitializer(temporaryRoot.Paths, migrations);

        var results = await Task.WhenAll(
            firstInitializer.InitializeAsync(),
            secondInitializer.InitializeAsync());

        Assert.All(results, result => Assert.Equal(13, result.CurrentVersion));
        Assert.Single(Directory.EnumerateFiles(temporaryRoot.Paths.BackupDirectoryPath, "*.db"));
        await using var connection = await OpenAsync(temporaryRoot.Paths.DatabasePath);
        Assert.Equal(13L, await ExecuteScalarLongAsync(connection, "SELECT COUNT(*) FROM SchemaMigrations;"));
        Assert.Contains("ConcurrentMarker", await ReadTableNamesAsync(connection));
    }

    [Fact]
    public async Task PendingMigration_RechecksBackupNeedAfterWaitingForMigrationLock()
    {
        using var temporaryRoot = new TemporaryAppDataRoot();
        var reachedLockBoundary = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLockBoundary = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var migrations = SqliteSchema.Migrations
            .Concat([new SqliteMigration(13, "racing-upgrade", "CREATE TABLE RacingMarker (Id INTEGER PRIMARY KEY);")])
            .ToArray();
        var upgradingInitializer = new SqliteDatabaseInitializer(
            temporaryRoot.Paths,
            migrations,
            async cancellationToken =>
            {
                reachedLockBoundary.TrySetResult(true);
                await releaseLockBoundary.Task.WaitAsync(cancellationToken);
            });

        var upgradeTask = upgradingInitializer.InitializeAsync();
        await reachedLockBoundary.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await new SqliteDatabaseInitializer(temporaryRoot.Paths).InitializeAsync();
        }
        finally
        {
            releaseLockBoundary.TrySetResult(true);
        }

        var result = await upgradeTask;

        Assert.Equal(12, result.PreviousVersion);
        Assert.Equal(13, result.CurrentVersion);
        Assert.NotNull(result.BackupFilePath);
        await using var backup = await OpenAsync(result.BackupFilePath!, readOnly: true);
        Assert.Equal(12L, await ExecuteScalarLongAsync(backup, "PRAGMA user_version;"));
        Assert.DoesNotContain("RacingMarker", await ReadTableNamesAsync(backup));
    }

    private static async Task<SqliteConnection> OpenAsync(string path, bool readOnly = false)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<HashSet<string>> ReadTableNamesAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
        var names = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task ExecuteNonQueryAsync(string path, string sql)
    {
        await using var connection = await OpenAsync(path);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ExecuteScalarLongAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string> ExecuteScalarStringAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync())!;
    }
}
