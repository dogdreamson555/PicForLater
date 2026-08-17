using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PicForLater.Core.Analysis;
using PicForLater.Core.Images;
using PicForLater.Infrastructure.Storage;

namespace PicForLater.Infrastructure.Analysis;

/// <summary>
/// Durable analysis queue with expiring leases and idempotent stage checkpoints.
/// A crashed worker can resume at the last committed stage without duplicating
/// results or overwriting a user edit made against a newer item revision.
/// </summary>
public sealed class SqliteAnalysisJobStore : IAnalysisJobStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AppDataPaths _paths;

    public SqliteAnalysisJobStore(AppDataPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public async Task<AnalysisLeaseAttempt> TryLeaseNextAsync(
        string workerId,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        int maximumAttempts,
        CancellationToken cancellationToken = default)
    {
        ValidateWorker(workerId);
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        if (maximumAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        }

        nowUtc = nowUtc.ToUniversalTime();
        var leaseExpiresAtUtc = nowUtc.Add(leaseDuration);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);

        await ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE AnalysisJobs
            SET State = @cancelled, LeaseOwner = NULL, LeaseExpiresAtUtc = NULL,
                LastErrorCode = 'analysis.item-deleted', UpdatedAtUtc = @now,
                CompletedAtUtc = @now
            WHERE State IN (@queued, @running, @retryable)
              AND EXISTS (
                  SELECT 1 FROM ImageItems i
                  WHERE i.Id = AnalysisJobs.ImageItemId AND i.DeletedAtUtc IS NOT NULL);
            """,
            cancellationToken,
            ("@cancelled", (int)AnalysisJobState.Cancelled),
            ("@queued", (int)AnalysisJobState.Queued),
            ("@running", (int)AnalysisJobState.Running),
            ("@retryable", (int)AnalysisJobState.Retryable),
            ("@now", ToDb(nowUtc))).ConfigureAwait(false);

        await ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE ImageItems
            SET AnalysisState = @needsAttention, UpdatedAtUtc = @now
            WHERE Id IN (
                SELECT ImageItemId FROM AnalysisJobs
                WHERE AttemptCount >= @maximumAttempts
                  AND (State = @retryable
                       OR (State = @running AND LeaseExpiresAtUtc <= @now)));

            UPDATE AnalysisJobs
            SET State = @failed, LeaseOwner = NULL, LeaseExpiresAtUtc = NULL,
                LastErrorCode = COALESCE(LastErrorCode, 'analysis.attempts-exhausted'),
                UpdatedAtUtc = @now, CompletedAtUtc = @now
            WHERE AttemptCount >= @maximumAttempts
              AND (State = @retryable
                   OR (State = @running AND LeaseExpiresAtUtc <= @now));

            UPDATE AnalysisJobs
            SET State = @retryable, LeaseOwner = NULL, LeaseExpiresAtUtc = NULL,
                NotBeforeUtc = @now, UpdatedAtUtc = @now
            WHERE State = @running AND LeaseExpiresAtUtc <= @now
              AND AttemptCount < @maximumAttempts;
            """,
            cancellationToken,
            ("@needsAttention", (int)AnalysisState.NeedsAttention),
            ("@failed", (int)AnalysisJobState.Failed),
            ("@retryable", (int)AnalysisJobState.Retryable),
            ("@running", (int)AnalysisJobState.Running),
            ("@maximumAttempts", maximumAttempts),
            ("@now", ToDb(nowUtc))).ConfigureAwait(false);

        AnalysisJobLease? lease = null;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText =
                """
                SELECT j.Id, j.ImageItemId, j.Kind, j.InputRevision, j.AttemptCount,
                       j.CurrentStage, a.OriginalRelativePath, a.ContentHash,
                       i.OriginalFileName, a.PixelWidth, a.PixelHeight,
                       j.ModelProfileSnapshotJson
                FROM AnalysisJobs j
                INNER JOIN ImageItems i ON i.Id = j.ImageItemId
                INNER JOIN ImageAssets a ON a.Id = i.AssetId
                WHERE j.State IN (@queued, @retryable)
                  AND j.NotBeforeUtc <= @now
                  AND j.AttemptCount < @maximumAttempts
                  AND i.DeletedAtUtc IS NULL
                ORDER BY j.NotBeforeUtc, j.CreatedAtUtc, j.Id
                LIMIT 1;
                """;
            select.Parameters.AddWithValue("@queued", (int)AnalysisJobState.Queued);
            select.Parameters.AddWithValue("@retryable", (int)AnalysisJobState.Retryable);
            select.Parameters.AddWithValue("@now", ToDb(nowUtc));
            select.Parameters.AddWithValue("@maximumAttempts", maximumAttempts);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                lease = new AnalysisJobLease(
                    Guid.Parse(reader.GetString(0)),
                    Guid.Parse(reader.GetString(1)),
                    (AnalysisJobKind)reader.GetInt32(2),
                    reader.GetInt64(3),
                    checked(reader.GetInt32(4) + 1),
                    (AnalysisStage)reader.GetInt32(5),
                    ManagedRelativePath.Parse(reader.GetString(6)),
                    Sha256Hash.Parse(reader.GetString(7)),
                    reader.GetString(8),
                    reader.GetInt32(9),
                    reader.GetInt32(10),
                    leaseExpiresAtUtc,
                    ReadProfileSnapshot(reader.GetString(11)));
            }
        }

        if (lease is not null)
        {
            var affected = await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE AnalysisJobs
                SET State = @running, AttemptCount = AttemptCount + 1,
                    LeaseOwner = @worker, LeaseExpiresAtUtc = @leaseExpires,
                    LastErrorCode = NULL, UpdatedAtUtc = @now, CompletedAtUtc = NULL
                WHERE Id = @id AND State IN (@queued, @retryable);
                """,
                cancellationToken,
                ("@running", (int)AnalysisJobState.Running),
                ("@worker", workerId),
                ("@leaseExpires", ToDb(leaseExpiresAtUtc)),
                ("@now", ToDb(nowUtc)),
                ("@id", ToDb(lease.JobId)),
                ("@queued", (int)AnalysisJobState.Queued),
                ("@retryable", (int)AnalysisJobState.Retryable)).ConfigureAwait(false);
            EnsureLeaseOwned(affected);
            await ExecuteAsync(
                connection,
                transaction,
                "UPDATE ImageItems SET AnalysisState = @running, UpdatedAtUtc = @now WHERE Id = @id;",
                cancellationToken,
                ("@running", (int)AnalysisState.Running),
                ("@now", ToDb(nowUtc)),
                ("@id", ToDb(lease.ImageItemId))).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (lease is not null)
        {
            return new AnalysisLeaseAttempt(lease, NextWakeAtUtc: null);
        }

        return new AnalysisLeaseAttempt(
            Lease: null,
            await ReadNextWakeAtAsync(connection, cancellationToken).ConfigureAwait(false));
    }

    public async Task<AnalysisStageCheckpoint?> GetCheckpointAsync(
        Guid jobId,
        AnalysisStage stage,
        CancellationToken cancellationToken = default)
    {
        ValidateStage(stage);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, AnalysisJobId, ImageItemId, Stage, InputRevision,
                   ProviderId, ModelId, ModelVersion, ModelFileHashesJson,
                   ExecutionLocation, OutputKind, RemoteInputMode,
                   StageOutcome,
                   LanguageTagsJson, SchemaVersion,
                   PayloadJson, FactText, WarningsJson, GeneratedAtUtc
            FROM AnalysisStageResults
            WHERE AnalysisJobId = @jobId AND Stage = @stage;
            """;
        command.Parameters.AddWithValue("@jobId", ToDb(jobId));
        command.Parameters.AddWithValue("@stage", (int)stage);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadCheckpoint(reader)
            : null;
    }

    public async Task<AnalysisCompositionContext> GetCompositionContextAsync(
        Guid imageItemId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name FROM Categories ORDER BY Name COLLATE NOCASE, Id;";
        var categories = new List<AnalysisCategoryOption>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            categories.Add(new AnalysisCategoryOption(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1)));
        }

        return new AnalysisCompositionContext(categories);
    }

    public async Task SaveCheckpointAsync(
        string workerId,
        AnalysisStageCheckpoint checkpoint,
        DateTimeOffset leaseExpiresAtUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateWorker(workerId);
        ArgumentNullException.ThrowIfNull(checkpoint);
        ValidateStage(checkpoint.Stage);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureActiveLeaseAsync(
            connection,
            transaction,
            checkpoint.JobId,
            workerId,
            cancellationToken).ConfigureAwait(false);
        await UpsertCheckpointAsync(connection, transaction, checkpoint, cancellationToken).ConfigureAwait(false);
        var affected = await ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE AnalysisJobs
            SET CurrentStage = CASE WHEN CurrentStage < @stage THEN @stage ELSE CurrentStage END,
                LeaseExpiresAtUtc = @leaseExpires, UpdatedAtUtc = @updated
            WHERE Id = @jobId AND State = @running AND LeaseOwner = @worker;
            """,
            cancellationToken,
            ("@stage", (int)checkpoint.Stage),
            ("@leaseExpires", ToDb(leaseExpiresAtUtc)),
            ("@updated", ToDb(checkpoint.GeneratedAtUtc)),
            ("@jobId", ToDb(checkpoint.JobId)),
            ("@running", (int)AnalysisJobState.Running),
            ("@worker", workerId)).ConfigureAwait(false);
        EnsureLeaseOwned(affected);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CompleteAsync(
        string workerId,
        AnalysisJobLease lease,
        AnalysisStageCheckpoint compositionCheckpoint,
        ExtractiveContentDraft draft,
        DateTimeOffset completedAtUtc,
        AnalysisCompletionFailure? completionFailure = null,
        CancellationToken cancellationToken = default)
    {
        ValidateWorker(workerId);
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(compositionCheckpoint);
        ArgumentNullException.ThrowIfNull(draft);
        if (compositionCheckpoint.Stage != AnalysisStage.TextComposition
            || compositionCheckpoint.JobId != lease.JobId
            || compositionCheckpoint.ImageItemId != lease.ImageItemId
            || compositionCheckpoint.InputRevision != lease.InputRevision)
        {
            throw new ArgumentException("The composition checkpoint does not match the leased job.", nameof(compositionCheckpoint));
        }
        if (completionFailure is not null)
        {
            ValidateErrorCode(completionFailure.ErrorCode);
        }

        completedAtUtc = completedAtUtc.ToUniversalTime();
        var finalAnalysisState = completionFailure is null
            ? AnalysisState.Completed
            : AnalysisState.NeedsAttention;
        var finalJobState = completionFailure is null
            ? AnalysisJobState.Completed
            : AnalysisJobState.Failed;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureActiveLeaseAsync(
            connection,
            transaction,
            lease.JobId,
            workerId,
            cancellationToken).ConfigureAwait(false);
        await UpsertCheckpointAsync(
            connection,
            transaction,
            compositionCheckpoint,
            cancellationToken).ConfigureAwait(false);

        var draftSource = draft.Provenance.OutputKind == AnalysisOutputKind.ModelGeneratedDraft
            ? ContentFieldSource.ModelSuggested
            : ContentFieldSource.Fallback;
        var updatedWithDraft = await ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE ImageItems
            SET Title = CASE WHEN TitleSource = @userSource THEN Title ELSE @title END,
                Summary = CASE WHEN SummarySource = @userSource THEN Summary ELSE @summary END,
                TitleSource = CASE WHEN TitleSource = @userSource THEN TitleSource ELSE @draftSource END,
                SummarySource = CASE WHEN SummarySource = @userSource THEN SummarySource ELSE @draftSource END,
                AnalysisState = @analysisState,
                Revision = Revision + 1,
                UpdatedAtUtc = @updated
            WHERE Id = @itemId AND Revision = @inputRevision;
            """,
            cancellationToken,
            ("@userSource", (int)ContentFieldSource.User),
            ("@draftSource", (int)draftSource),
            ("@title", draft.Title),
            ("@summary", draft.Summary),
            ("@analysisState", (int)finalAnalysisState),
            ("@updated", ToDb(completedAtUtc)),
            ("@itemId", ToDb(lease.ImageItemId)),
            ("@inputRevision", lease.InputRevision)).ConfigureAwait(false);

        if (updatedWithDraft == 1)
        {
            await ApplyModelSuggestionsAsync(
                connection,
                transaction,
                lease,
                draft,
                completedAtUtc,
                cancellationToken).ConfigureAwait(false);
        }

        if (updatedWithDraft == 0)
        {
            await ExecuteAsync(
                connection,
                transaction,
                "UPDATE ImageItems SET AnalysisState = @analysisState, UpdatedAtUtc = @updated WHERE Id = @itemId;",
                cancellationToken,
                ("@analysisState", (int)finalAnalysisState),
                ("@updated", ToDb(completedAtUtc)),
                ("@itemId", ToDb(lease.ImageItemId))).ConfigureAwait(false);
        }

        var completed = await ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE AnalysisJobs
            SET State = @finalState, CurrentStage = @stage,
                LeaseOwner = NULL, LeaseExpiresAtUtc = NULL,
                LastErrorCode = @errorCode, UpdatedAtUtc = @updated, CompletedAtUtc = @updated
            WHERE Id = @jobId AND State = @running AND LeaseOwner = @worker;
            """,
            cancellationToken,
            ("@finalState", (int)finalJobState),
            ("@stage", (int)AnalysisStage.TextComposition),
            ("@errorCode", completionFailure?.ErrorCode),
            ("@updated", ToDb(completedAtUtc)),
            ("@jobId", ToDb(lease.JobId)),
            ("@running", (int)AnalysisJobState.Running),
            ("@worker", workerId)).ConfigureAwait(false);
        EnsureLeaseOwned(completed);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task FailAsync(
        string workerId,
        AnalysisJobLease lease,
        string errorCode,
        bool retryable,
        DateTimeOffset retryAtUtc,
        int maximumAttempts,
        DateTimeOffset failedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateWorker(workerId);
        ArgumentNullException.ThrowIfNull(lease);
        ValidateErrorCode(errorCode);
        if (maximumAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        }

        var willRetry = retryable && lease.AttemptCount < maximumAttempts;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var affected = await ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE AnalysisJobs
            SET State = @state, NotBeforeUtc = @notBefore,
                LeaseOwner = NULL, LeaseExpiresAtUtc = NULL,
                LastErrorCode = @errorCode, UpdatedAtUtc = @updated,
                CompletedAtUtc = @completed
            WHERE Id = @jobId AND State = @running AND LeaseOwner = @worker;
            """,
            cancellationToken,
            ("@state", willRetry ? (int)AnalysisJobState.Retryable : (int)AnalysisJobState.Failed),
            ("@notBefore", ToDb(willRetry ? retryAtUtc : failedAtUtc)),
            ("@errorCode", errorCode),
            ("@updated", ToDb(failedAtUtc)),
            ("@completed", willRetry ? null : ToDb(failedAtUtc)),
            ("@jobId", ToDb(lease.JobId)),
            ("@running", (int)AnalysisJobState.Running),
            ("@worker", workerId)).ConfigureAwait(false);
        EnsureLeaseOwned(affected);
        await ExecuteAsync(
            connection,
            transaction,
            "UPDATE ImageItems SET AnalysisState = @state, UpdatedAtUtc = @updated WHERE Id = @itemId;",
            cancellationToken,
            ("@state", willRetry ? (int)AnalysisState.Pending : (int)AnalysisState.NeedsAttention),
            ("@updated", ToDb(failedAtUtc)),
            ("@itemId", ToDb(lease.ImageItemId))).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AbandonAsync(
        string workerId,
        AnalysisJobLease lease,
        DateTimeOffset retryAtUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateWorker(workerId);
        ArgumentNullException.ThrowIfNull(lease);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var affected = await ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE AnalysisJobs
            SET State = @retryable, NotBeforeUtc = @retryAt,
                LeaseOwner = NULL, LeaseExpiresAtUtc = NULL,
                LastErrorCode = 'analysis.interrupted', UpdatedAtUtc = @retryAt
            WHERE Id = @jobId AND State = @running AND LeaseOwner = @worker;
            """,
            cancellationToken,
            ("@retryable", (int)AnalysisJobState.Retryable),
            ("@retryAt", ToDb(retryAtUtc)),
            ("@jobId", ToDb(lease.JobId)),
            ("@running", (int)AnalysisJobState.Running),
            ("@worker", workerId)).ConfigureAwait(false);
        EnsureLeaseOwned(affected);
        await ExecuteAsync(
            connection,
            transaction,
            "UPDATE ImageItems SET AnalysisState = @pending, UpdatedAtUtc = @updated WHERE Id = @itemId;",
            cancellationToken,
            ("@pending", (int)AnalysisState.Pending),
            ("@updated", ToDb(retryAtUtc)),
            ("@itemId", ToDb(lease.ImageItemId))).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertCheckpointAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AnalysisStageCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO AnalysisStageResults (
                Id, AnalysisJobId, ImageItemId, Stage, InputRevision,
                ProviderId, ModelId, ModelVersion, ModelFileHashesJson,
                ExecutionLocation, OutputKind, RemoteInputMode,
                StageOutcome,
                LanguageTagsJson, SchemaVersion,
                PayloadJson, FactText, WarningsJson, GeneratedAtUtc)
            VALUES (
                @id, @jobId, @itemId, @stage, @inputRevision,
                @providerId, @modelId, @modelVersion, @hashes,
                @executionLocation, @outputKind, @remoteInputMode,
                @stageOutcome,
                @languages, @schemaVersion, @payload, @factText,
                @warnings, @generated)
            ON CONFLICT(AnalysisJobId, Stage) WHERE AnalysisJobId IS NOT NULL DO UPDATE SET
                InputRevision = excluded.InputRevision,
                ProviderId = excluded.ProviderId,
                ModelId = excluded.ModelId,
                ModelVersion = excluded.ModelVersion,
                ModelFileHashesJson = excluded.ModelFileHashesJson,
                ExecutionLocation = excluded.ExecutionLocation,
                OutputKind = excluded.OutputKind,
                RemoteInputMode = excluded.RemoteInputMode,
                StageOutcome = excluded.StageOutcome,
                LanguageTagsJson = excluded.LanguageTagsJson,
                SchemaVersion = excluded.SchemaVersion,
                PayloadJson = excluded.PayloadJson,
                FactText = excluded.FactText,
                WarningsJson = excluded.WarningsJson,
                GeneratedAtUtc = excluded.GeneratedAtUtc;
            """,
            cancellationToken,
            ("@id", ToDb(checkpoint.Id)),
            ("@jobId", ToDb(checkpoint.JobId)),
            ("@itemId", ToDb(checkpoint.ImageItemId)),
            ("@stage", (int)checkpoint.Stage),
            ("@inputRevision", checkpoint.InputRevision),
            ("@providerId", checkpoint.Provenance.ProviderId),
            ("@modelId", checkpoint.Provenance.ModelId),
            ("@modelVersion", checkpoint.Provenance.ModelVersion),
            ("@hashes", JsonSerializer.Serialize(checkpoint.Provenance.ModelFileHashes, JsonOptions)),
            ("@executionLocation", (int)checkpoint.Provenance.ExecutionLocation),
            ("@outputKind", (int)checkpoint.Provenance.OutputKind),
            ("@remoteInputMode", checkpoint.Provenance.RemoteInputMode is null
                ? null
                : (int)checkpoint.Provenance.RemoteInputMode.Value),
            ("@stageOutcome", (int)checkpoint.Provenance.StageOutcome),
            ("@languages", JsonSerializer.Serialize(checkpoint.LanguageTags, JsonOptions)),
            ("@schemaVersion", checkpoint.Provenance.SchemaVersion),
            ("@payload", checkpoint.PayloadJson),
            ("@factText", checkpoint.FactText),
            ("@warnings", JsonSerializer.Serialize(checkpoint.Warnings, JsonOptions)),
            ("@generated", ToDb(checkpoint.GeneratedAtUtc))).ConfigureAwait(false);
    }

    private static async Task ApplyModelSuggestionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AnalysisJobLease lease,
        ExtractiveContentDraft draft,
        DateTimeOffset generatedAtUtc,
        CancellationToken cancellationToken)
    {
        if (draft.Provenance.OutputKind == AnalysisOutputKind.ModelGeneratedDraft)
        {
            await ExecuteAsync(
                connection,
                transaction,
                "DELETE FROM ImageCategories WHERE ImageItemId = @itemId AND Source = @modelSource;",
                cancellationToken,
                ("@itemId", ToDb(lease.ImageItemId)),
                ("@modelSource", 2)).ConfigureAwait(false);
            foreach (var categoryId in draft.SuggestedCategoryIds.Distinct())
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    INSERT OR IGNORE INTO ImageCategories (ImageItemId, CategoryId, Source, CreatedAtUtc)
                    SELECT @itemId, @categoryId, @modelSource, @created
                    WHERE EXISTS (SELECT 1 FROM Categories WHERE Id = @categoryId)
                      AND NOT EXISTS (
                          SELECT 1 FROM CategoryExclusions
                          WHERE ImageItemId = @itemId AND CategoryId = @categoryId);
                    """,
                    cancellationToken,
                    ("@itemId", ToDb(lease.ImageItemId)),
                    ("@categoryId", ToDb(categoryId)),
                    ("@modelSource", 2),
                    ("@created", ToDb(generatedAtUtc))).ConfigureAwait(false);
            }
        }

        foreach (var entity in draft.EntityCandidates)
        {
            var refreshed = await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE EntityCandidates
                SET AnalysisJobId = @jobId,
                    RawText = @rawText,
                    NormalizedValue = @normalized,
                    Evidence = @evidence,
                    Source = @source,
                    GeneratedAtUtc = @generated,
                    BoundingBoxJson = @boundingBox,
                    ReferenceTimeUtc = @referenceTime,
                    TimeZoneId = @timeZoneId,
                    AmbiguityReason = @ambiguity
                WHERE Id = (
                    SELECT existing.Id
                    FROM EntityCandidates existing
                    WHERE existing.ImageItemId = @itemId
                      AND existing.Kind = @kind
                      AND existing.RawText = @rawText
                      AND COALESCE(existing.NormalizedValue, '') = COALESCE(@normalized, '')
                      AND existing.Evidence = @evidence
                      AND existing.CandidateStatus = 1
                    ORDER BY existing.GeneratedAtUtc DESC
                    LIMIT 1);
                """,
                cancellationToken,
                ("@jobId", ToDb(lease.JobId)),
                ("@itemId", ToDb(lease.ImageItemId)),
                ("@kind", entity.Kind),
                ("@rawText", entity.RawText),
                ("@normalized", entity.NormalizedValue),
                ("@evidence", entity.Evidence),
                ("@source", entity.Source),
                ("@generated", ToDb(generatedAtUtc)),
                ("@boundingBox", entity.BoundingBox is null
                    ? null
                    : JsonSerializer.Serialize(entity.BoundingBox, JsonOptions)),
                ("@referenceTime", entity.ReferenceTimeUtc is null
                    ? null
                    : ToDb(entity.ReferenceTimeUtc.Value)),
                ("@timeZoneId", entity.TimeZoneId),
                ("@ambiguity", entity.AmbiguityReason)).ConfigureAwait(false);
            if (refreshed != 0)
            {
                continue;
            }

            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT OR IGNORE INTO EntityCandidates (
                    Id, AnalysisJobId, ImageItemId, Kind, RawText,
                    NormalizedValue, Evidence, Source, GeneratedAtUtc,
                    CandidateStatus, BoundingBoxJson, ReferenceTimeUtc,
                    TimeZoneId, AmbiguityReason)
                SELECT
                    @id, @jobId, @itemId, @kind, @rawText,
                    @normalized, @evidence, @source, @generated,
                    1, @boundingBox, @referenceTime, @timeZoneId, @ambiguity
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM EntityCandidates existing
                    WHERE existing.ImageItemId = @itemId
                      AND existing.Kind = @kind
                      AND existing.RawText = @rawText
                      AND COALESCE(existing.NormalizedValue, '') = COALESCE(@normalized, '')
                      AND existing.Evidence = @evidence);
                """,
                cancellationToken,
                ("@id", ToDb(Guid.NewGuid())),
                ("@jobId", ToDb(lease.JobId)),
                ("@itemId", ToDb(lease.ImageItemId)),
                ("@kind", entity.Kind),
                ("@rawText", entity.RawText),
                ("@normalized", entity.NormalizedValue),
                ("@evidence", entity.Evidence),
                ("@source", entity.Source),
                ("@generated", ToDb(generatedAtUtc)),
                ("@boundingBox", entity.BoundingBox is null
                    ? null
                    : JsonSerializer.Serialize(entity.BoundingBox, JsonOptions)),
                ("@referenceTime", entity.ReferenceTimeUtc is null
                    ? null
                    : ToDb(entity.ReferenceTimeUtc.Value)),
                ("@timeZoneId", entity.TimeZoneId),
                ("@ambiguity", entity.AmbiguityReason)).ConfigureAwait(false);
        }

        // A successful reanalysis atomically replaces only still-pending
        // suggestions. Confirmed candidates and explicit user dismissals remain
        // immutable; superseded pending rows stay as non-pending audit history.
        await ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE EntityCandidates
            SET CandidateStatus = 3
            WHERE ImageItemId = @itemId
              AND CandidateStatus = 1
              AND AnalysisJobId <> @jobId;
            """,
            cancellationToken,
            ("@itemId", ToDb(lease.ImageItemId)),
            ("@jobId", ToDb(lease.JobId))).ConfigureAwait(false);
    }

    private static async Task EnsureActiveLeaseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid jobId,
        string workerId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT COUNT(*) FROM AnalysisJobs WHERE Id = @id AND State = @running AND LeaseOwner = @worker;";
        command.Parameters.AddWithValue("@id", ToDb(jobId));
        command.Parameters.AddWithValue("@running", (int)AnalysisJobState.Running);
        command.Parameters.AddWithValue("@worker", workerId);
        var count = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        EnsureLeaseOwned(count);
    }

    private static AnalysisStageCheckpoint ReadCheckpoint(SqliteDataReader reader)
    {
        var hashes = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(8), JsonOptions)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var languages = JsonSerializer.Deserialize<string[]>(reader.GetString(13), JsonOptions) ?? [];
        var warnings = JsonSerializer.Deserialize<string[]>(reader.GetString(17), JsonOptions) ?? [];
        return new AnalysisStageCheckpoint(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            Guid.Parse(reader.GetString(2)),
            (AnalysisStage)reader.GetInt32(3),
            reader.GetInt64(4),
            new AnalysisProvenance(
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                hashes,
                reader.GetString(14),
                (AnalysisExecutionLocation)reader.GetInt32(9),
                (AnalysisOutputKind)reader.GetInt32(10),
                reader.IsDBNull(11)
                    ? null
                    : (RemoteInputMode)reader.GetInt32(11),
                (AnalysisStageOutcome)reader.GetInt32(12)),
            languages,
            reader.GetString(15),
            reader.GetString(16),
            warnings,
            ParseDate(reader.GetString(18)));
    }

    private static ModelProfileSnapshot ReadProfileSnapshot(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}")
        {
            return ModelProfileSnapshot.Default;
        }

        try
        {
            return JsonSerializer.Deserialize<ModelProfileSnapshot>(json, JsonOptions)
                ?? ModelProfileSnapshot.Default;
        }
        catch (JsonException)
        {
            throw new InvalidDataException("The analysis job model profile snapshot is invalid.");
        }
    }

    private static async Task<DateTimeOffset?> ReadNextWakeAtAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT MIN(WakeAtUtc) FROM (
                SELECT NotBeforeUtc AS WakeAtUtc FROM AnalysisJobs WHERE State IN (@queued, @retryable)
                UNION ALL
                SELECT LeaseExpiresAtUtc AS WakeAtUtc FROM AnalysisJobs
                WHERE State = @running AND LeaseExpiresAtUtc IS NOT NULL
            );
            """;
        command.Parameters.AddWithValue("@queued", (int)AnalysisJobState.Queued);
        command.Parameters.AddWithValue("@retryable", (int)AnalysisJobState.Retryable);
        command.Parameters.AddWithValue("@running", (int)AnalysisJobState.Running);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : ParseDate(Convert.ToString(value, CultureInfo.InvariantCulture)!);
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
        SqliteTransaction transaction,
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

    private static void ValidateStage(AnalysisStage stage)
    {
        if (stage is <= AnalysisStage.None or > AnalysisStage.TextComposition)
        {
            throw new ArgumentOutOfRangeException(nameof(stage));
        }
    }

    private static void ValidateWorker(string workerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        if (workerId.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(workerId));
        }
    }

    private static void ValidateErrorCode(string errorCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        if (errorCode.Length > 128
            || errorCode.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-')))
        {
            throw new ArgumentException("The analysis error code is invalid.", nameof(errorCode));
        }
    }

    private static void EnsureLeaseOwned(int affected)
    {
        if (affected != 1)
        {
            throw new AnalysisLeaseLostException();
        }
    }

    private static string ToDb(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);

    private static string ToDb(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
