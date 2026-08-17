using System.Globalization;
using Microsoft.Data.Sqlite;
using PicForLater.Core.Analysis;
using PicForLater.Core.Images;
using PicForLater.Core.Library;
using PicForLater.Infrastructure.Storage;

namespace PicForLater.Infrastructure.Library;

internal sealed record DeletionPlan(
    Guid JobId,
    Guid ImageItemId,
    Guid AssetId,
    ManagedRelativePath OriginalRelativePath,
    ManagedRelativePath? ThumbnailRelativePath,
    bool DeleteAssetFiles);

internal sealed class SqliteLibraryStore
{
    private const string EntryColumns =
        """
        i.Id, i.AssetId, i.OriginalFileName, i.SourceKind, i.Title, i.Summary,
        i.TitleSource, i.SummarySource, i.AnalysisState, i.Revision,
        i.CreatedAtUtc, i.UpdatedAtUtc, i.DeletedAtUtc,
        a.Id, a.ContentHash, a.OriginalRelativePath, a.ThumbnailRelativePath,
        a.MediaType, a.ByteLength, a.PixelWidth, a.PixelHeight, a.CreatedAtUtc
        """;

    private readonly AppDataPaths _paths;

    public SqliteLibraryStore(AppDataPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public async Task<LibraryQueryResult> QueryAsync(
        LibraryQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Offset < 0 || query.Limit is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(query));
        }

        if (!Enum.IsDefined(query.SortField) || !Enum.IsDefined(query.SortDirection))
        {
            throw new ArgumentOutOfRangeException(nameof(query));
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT {EntryColumns}
            FROM ImageItems i
            INNER JOIN ImageAssets a ON a.Id = i.AssetId
            WHERE i.DeletedAtUtc IS {(query.IsDeleted ? "NOT NULL" : "NULL")}
              AND (@categoryId IS NULL OR EXISTS (
                    SELECT 1 FROM ImageCategories ic
                    WHERE ic.ImageItemId = i.Id AND ic.CategoryId = @categoryId))
              AND (@search = ''
                   OR i.Title LIKE @pattern ESCAPE '\' COLLATE NOCASE
                   OR i.Summary LIKE @pattern ESCAPE '\' COLLATE NOCASE
                   OR EXISTS (
                        SELECT 1 FROM AnalysisStageResults ar
                        WHERE ar.ImageItemId = i.Id
                          AND ar.Stage = 1
                          AND ar.FactText LIKE @pattern ESCAPE '\' COLLATE NOCASE)
                   OR EXISTS (
                        SELECT 1 FROM ImageCategories sic
                        INNER JOIN Categories sc ON sc.Id = sic.CategoryId
                        WHERE sic.ImageItemId = i.Id
                          AND sc.Name LIKE @pattern ESCAPE '\' COLLATE NOCASE)
                   OR EXISTS (
                        SELECT 1 FROM Reminders r
                        WHERE r.ImageItemId = i.Id
                          AND r.ConfirmedLocation LIKE @pattern ESCAPE '\' COLLATE NOCASE))
            ORDER BY {CreateOrderByClause(query.SortField, query.SortDirection)}
            LIMIT @limit OFFSET @offset;
            """;

        var search = query.SearchText?.Trim() ?? string.Empty;
        command.Parameters.AddWithValue("@categoryId",
            query.CategoryId?.ToString("D", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@search", search);
        command.Parameters.AddWithValue("@pattern", $"%{EscapeLike(search)}%");
        command.Parameters.AddWithValue("@limit", query.Limit + 1);
        command.Parameters.AddWithValue("@offset", query.Offset);

        var entries = new List<LibraryEntry>(query.Limit + 1);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                entries.Add(ReadEntry(reader));
            }
        }

        var hasMore = entries.Count > query.Limit;
        if (hasMore)
        {
            entries.RemoveAt(entries.Count - 1);
        }

        return new LibraryQueryResult(
            await AttachCategoriesAsync(connection, entries, cancellationToken).ConfigureAwait(false),
            hasMore);
    }

    private static string CreateOrderByClause(
        LibrarySortField field,
        LibrarySortDirection direction)
    {
        var sqlDirection = direction == LibrarySortDirection.Ascending ? "ASC" : "DESC";
        return field switch
        {
            LibrarySortField.CreatedAt => $"i.CreatedAtUtc {sqlDirection}, i.Id ASC",
            LibrarySortField.Title =>
                $"i.Title COLLATE NOCASE {sqlDirection}, i.CreatedAtUtc DESC, i.Id ASC",
            LibrarySortField.ByteLength =>
                $"a.ByteLength {sqlDirection}, i.CreatedAtUtc DESC, i.Id ASC",
            LibrarySortField.Category =>
                $"""
                CASE WHEN EXISTS (
                    SELECT 1 FROM ImageCategories oic WHERE oic.ImageItemId = i.Id
                ) THEN 0 ELSE 1 END ASC,
                (SELECT MIN(oc.Name) FROM ImageCategories oic
                 INNER JOIN Categories oc ON oc.Id = oic.CategoryId
                 WHERE oic.ImageItemId = i.Id) COLLATE NOCASE {sqlDirection},
                i.CreatedAtUtc DESC, i.Id ASC
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
    }

    public async Task<LibraryEntry?> GetAsync(Guid imageItemId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var entry = await GetEntryAsync(
            connection,
            "i.Id = @value",
            imageItemId.ToString("D", CultureInfo.InvariantCulture),
            cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return null;
        }

        return (await AttachCategoriesAsync(connection, [entry], cancellationToken).ConfigureAwait(false))[0];
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetSummariesAsync(
        IReadOnlyCollection<Guid> imageItemIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(imageItemIds);
        var distinctIds = imageItemIds.Distinct().ToArray();
        if (distinctIds.Length == 0)
        {
            return new Dictionary<Guid, string>();
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var summaries = new Dictionary<Guid, string>(distinctIds.Length);
        foreach (var idBatch in distinctIds.Chunk(200))
        {
            await using var command = connection.CreateCommand();
            var parameterNames = new string[idBatch.Length];
            for (var index = 0; index < idBatch.Length; index++)
            {
                var parameterName = $"@id{index}";
                parameterNames[index] = parameterName;
                command.Parameters.AddWithValue(
                    parameterName,
                    idBatch[index].ToString("D", CultureInfo.InvariantCulture));
            }

            command.CommandText =
                $"SELECT Id, COALESCE(Summary, '') FROM ImageItems WHERE Id IN ({string.Join(", ", parameterNames)});";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                summaries[Guid.Parse(reader.GetString(0))] = reader.GetString(1);
            }
        }

        return summaries;
    }

    public async Task<LibraryEntry?> FindByHashAsync(
        Sha256Hash contentHash,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contentHash);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var entry = await GetEntryAsync(
            connection,
            "a.ContentHash = @value",
            contentHash.Hex,
            cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return null;
        }

        return (await AttachCategoriesAsync(connection, [entry], cancellationToken).ConfigureAwait(false))[0];
    }

    public async Task CreateImportJobAsync(ImportJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO ImportJobs (
                Id, StagingRelativePath, FinalRelativePath, OriginalFileName, SourceKind,
                State, ContentHash, ImageItemId, AttemptCount, LeaseExpiresAtUtc,
                LastErrorCode, CreatedAtUtc, UpdatedAtUtc, CompletedAtUtc)
            VALUES (
                @id, @staging, NULL, @fileName, @sourceKind,
                @state, @hash, NULL, 0, NULL,
                NULL, @created, @updated, NULL);
            """;
        command.Parameters.AddWithValue("@id", ToDb(job.Id));
        command.Parameters.AddWithValue("@staging", job.StagingRelativePath?.Value ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@fileName", job.OriginalFileName);
        command.Parameters.AddWithValue("@sourceKind", (int)job.SourceKind);
        command.Parameters.AddWithValue("@state", (int)job.State);
        command.Parameters.AddWithValue("@hash", job.ContentHash?.Hex ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@created", ToDb(job.CreatedAtUtc));
        command.Parameters.AddWithValue("@updated", ToDb(job.UpdatedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CompleteImportAsync(
        ImageAsset asset,
        ImageItem item,
        ImportJob job,
        AnalysisJob analysisJob,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO ImageAssets (
                Id, ContentHash, OriginalRelativePath, ThumbnailRelativePath, MediaType,
                ByteLength, PixelWidth, PixelHeight, CreatedAtUtc)
            VALUES (@id, @hash, @original, @thumbnail, @mediaType,
                    @byteLength, @width, @height, @created);
            """,
            cancellationToken,
            ("@id", ToDb(asset.Id)),
            ("@hash", asset.ContentHash.Hex),
            ("@original", asset.OriginalRelativePath.Value),
            ("@thumbnail", asset.ThumbnailRelativePath?.Value),
            ("@mediaType", asset.MediaType),
            ("@byteLength", asset.ByteLength),
            ("@width", asset.PixelWidth),
            ("@height", asset.PixelHeight),
            ("@created", ToDb(asset.CreatedAtUtc))).ConfigureAwait(false);

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO ImageItems (
                Id, AssetId, OriginalFileName, SourceKind, Title, Summary,
                TitleSource, SummarySource, AnalysisState, Revision,
                CreatedAtUtc, UpdatedAtUtc, DeletedAtUtc)
            VALUES (@id, @assetId, @fileName, @sourceKind, @title, @summary,
                    @titleSource, @summarySource, @analysisState, @revision,
                    @created, @updated, NULL);
            """,
            cancellationToken,
            ("@id", ToDb(item.Id)),
            ("@assetId", ToDb(item.AssetId)),
            ("@fileName", item.OriginalFileName),
            ("@sourceKind", (int)item.SourceKind),
            ("@title", item.Title),
            ("@summary", item.Summary),
            ("@titleSource", (int)item.TitleSource),
            ("@summarySource", (int)item.SummarySource),
            ("@analysisState", (int)item.AnalysisState),
            ("@revision", item.Revision),
            ("@created", ToDb(item.CreatedAtUtc)),
            ("@updated", ToDb(item.UpdatedAtUtc))).ConfigureAwait(false);

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO AnalysisJobs (
                Id, ImageItemId, Kind, InputRevision, State, AttemptCount,
                NotBeforeUtc, LeaseExpiresAtUtc, LastErrorCode,
                CreatedAtUtc, UpdatedAtUtc, CompletedAtUtc,
                AnalysisMode, ProfileRevision, ModelProfileSnapshotJson)
            VALUES (@id, @itemId, @kind, @revision, @state, 0,
                    @notBefore, NULL, NULL, @created, @updated, NULL,
                    @analysisMode, @profileRevision, @profileSnapshot);
            """,
            cancellationToken,
            ("@id", ToDb(analysisJob.Id)),
            ("@itemId", ToDb(analysisJob.ImageItemId)),
            ("@kind", (int)analysisJob.Kind),
            ("@revision", analysisJob.InputRevision),
            ("@state", (int)analysisJob.State),
            ("@notBefore", ToDb(analysisJob.NotBeforeUtc)),
            ("@created", ToDb(analysisJob.CreatedAtUtc)),
            ("@updated", ToDb(analysisJob.UpdatedAtUtc)),
            ("@analysisMode", (int)(analysisJob.ProfileSnapshot ?? ModelProfileSnapshot.Default).AnalysisMode),
            ("@profileRevision", (analysisJob.ProfileSnapshot ?? ModelProfileSnapshot.Default).Revision),
            ("@profileSnapshot", System.Text.Json.JsonSerializer.Serialize(
                analysisJob.ProfileSnapshot ?? ModelProfileSnapshot.Default,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)))).ConfigureAwait(false);

        await ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE ImportJobs
            SET FinalRelativePath = @final, State = @state, ImageItemId = @itemId,
                UpdatedAtUtc = @updated, CompletedAtUtc = @completed
            WHERE Id = @id;
            """,
            cancellationToken,
            ("@final", job.FinalRelativePath?.Value),
            ("@state", (int)ImportJobState.Completed),
            ("@itemId", ToDb(item.Id)),
            ("@updated", ToDb(job.UpdatedAtUtc)),
            ("@completed", ToDb(job.CompletedAtUtc ?? job.UpdatedAtUtc)),
            ("@id", ToDb(job.Id))).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task MarkImportDuplicateAsync(
        Guid jobId,
        Guid existingItemId,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken) =>
        UpdateImportJobAsync(
            jobId,
            ImportJobState.Duplicate,
            existingItemId,
            null,
            completedAtUtc,
            cancellationToken);

    public Task MarkImportFailedAsync(
        Guid jobId,
        string errorCode,
        DateTimeOffset failedAtUtc,
        CancellationToken cancellationToken) =>
        UpdateImportJobAsync(
            jobId,
            ImportJobState.Failed,
            null,
            errorCode,
            failedAtUtc,
            cancellationToken);

    public Task MarkImportCancelledAsync(
        Guid jobId,
        DateTimeOffset cancelledAtUtc,
        CancellationToken cancellationToken) =>
        UpdateImportJobAsync(
            jobId,
            ImportJobState.Cancelled,
            null,
            "ImportCancelled",
            cancelledAtUtc,
            cancellationToken);

    public async Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Id, Name, CreatedAtUtc, UpdatedAtUtc FROM Categories ORDER BY Name COLLATE NOCASE;";
        var categories = new List<Category>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            categories.Add(ReadCategory(reader));
        }

        return categories;
    }

    public async Task<Category> CreateCategoryAsync(
        string name,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var category = new Category(Guid.NewGuid(), name, now, now);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            transaction: null,
            "INSERT INTO Categories (Id, Name, CreatedAtUtc, UpdatedAtUtc) VALUES (@id, @name, @created, @updated);",
            cancellationToken,
            ("@id", ToDb(category.Id)),
            ("@name", category.Name),
            ("@created", ToDb(category.CreatedAtUtc)),
            ("@updated", ToDb(category.UpdatedAtUtc))).ConfigureAwait(false);
        return category;
    }

    public async Task RenameCategoryAsync(
        Guid categoryId,
        string name,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var affected = await ExecuteAsync(
            connection,
            transaction: null,
            "UPDATE Categories SET Name = @name, UpdatedAtUtc = @updated WHERE Id = @id;",
            cancellationToken,
            ("@name", name),
            ("@updated", ToDb(now)),
            ("@id", ToDb(categoryId))).ConfigureAwait(false);
        EnsureFound(affected);
    }

    public async Task DeleteCategoryAsync(Guid categoryId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var affected = await ExecuteAsync(
            connection,
            transaction: null,
            "DELETE FROM Categories WHERE Id = @id;",
            cancellationToken,
            ("@id", ToDb(categoryId))).ConfigureAwait(false);
        EnsureFound(affected);
    }

    public async Task SetCategoryAssignmentAsync(
        Guid imageItemId,
        Guid categoryId,
        bool isAssigned,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (isAssigned)
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO ImageCategories (ImageItemId, CategoryId, Source, CreatedAtUtc)
                VALUES (@itemId, @categoryId, @source, @created)
                ON CONFLICT(ImageItemId, CategoryId)
                DO UPDATE SET Source = excluded.Source;
                """,
                cancellationToken,
                ("@itemId", ToDb(imageItemId)),
                ("@categoryId", ToDb(categoryId)),
                ("@source", (int)CategoryAssignmentSource.Manual),
                ("@created", ToDb(now))).ConfigureAwait(false);
            await ExecuteAsync(
                connection,
                transaction,
                "DELETE FROM CategoryExclusions WHERE ImageItemId = @itemId AND CategoryId = @categoryId;",
                cancellationToken,
                ("@itemId", ToDb(imageItemId)),
                ("@categoryId", ToDb(categoryId))).ConfigureAwait(false);
        }
        else
        {
            await ExecuteAsync(
                connection,
                transaction,
                "DELETE FROM ImageCategories WHERE ImageItemId = @itemId AND CategoryId = @categoryId;",
                cancellationToken,
                ("@itemId", ToDb(imageItemId)),
                ("@categoryId", ToDb(categoryId))).ConfigureAwait(false);
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO CategoryExclusions (ImageItemId, CategoryId, CreatedAtUtc)
                VALUES (@itemId, @categoryId, @created)
                ON CONFLICT(ImageItemId, CategoryId) DO NOTHING;
                """,
                cancellationToken,
                ("@itemId", ToDb(imageItemId)),
                ("@categoryId", ToDb(categoryId)),
                ("@created", ToDb(now))).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateUserFieldsAsync(
        Guid imageItemId,
        string title,
        string summary,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var affected = await ExecuteAsync(
            connection,
            transaction: null,
            """
            UPDATE ImageItems
            SET Title = @title, Summary = @summary,
                TitleSource = @source, SummarySource = @source,
                Revision = Revision + 1, UpdatedAtUtc = @updated
            WHERE Id = @id;
            """,
            cancellationToken,
            ("@title", title),
            ("@summary", summary),
            ("@source", (int)ContentFieldSource.User),
            ("@updated", ToDb(now)),
            ("@id", ToDb(imageItemId))).ConfigureAwait(false);
        EnsureFound(affected);
    }

    public async Task SoftDeleteAsync(
        Guid imageItemId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var affected = await ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE ImageItems
            SET DeletedAtUtc = @deleted, UpdatedAtUtc = @deleted, Revision = Revision + 1
            WHERE Id = @id AND DeletedAtUtc IS NULL;
            """,
            cancellationToken,
            ("@deleted", ToDb(now)),
            ("@id", ToDb(imageItemId))).ConfigureAwait(false);
        EnsureFound(affected);
        await ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE ReminderNotificationOutbox
            SET State = 3, LastErrorCode = 'SupersededByDeletion',
                UpdatedAtUtc = @updated, CompletedAtUtc = @updated
            WHERE ReminderId IN (
                SELECT Id FROM Reminders WHERE ImageItemId = @id AND State = 1)
              AND Operation = 1
              AND State IN (1, 4);
            """,
            cancellationToken,
            ("@updated", ToDb(now)),
            ("@id", ToDb(imageItemId))).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO ReminderNotificationOutbox (
                Id, ReminderId, SchedulerId, Operation, DueAtUtc,
                Title, Body, Location, State, AttemptCount, NotBeforeUtc,
                LastErrorCode, CreatedAtUtc, UpdatedAtUtc, CompletedAtUtc)
            SELECT
                lower(hex(randomblob(16))), r.Id, r.SchedulerId, 2, NULL,
                NULL, NULL, NULL, 1, 0, @updated,
                NULL, @updated, @updated, NULL
            FROM Reminders r
            WHERE r.ImageItemId = @id
              AND r.State = 1
              AND NOT EXISTS (
                  SELECT 1 FROM ReminderNotificationOutbox pending
                  WHERE pending.SchedulerId = r.SchedulerId
                    AND pending.Operation = 2
                    AND pending.State IN (1, 2, 4));
            """,
            cancellationToken,
            ("@updated", ToDb(now)),
            ("@id", ToDb(imageItemId))).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE Reminders
            SET State = 3, NotificationState = 1, UpdatedAtUtc = @updated
            WHERE ImageItemId = @id AND State = 1;
            """,
            cancellationToken,
            ("@updated", ToDb(now)),
            ("@id", ToDb(imageItemId))).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RestoreAsync(
        Guid imageItemId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var affected = await ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE ImageItems
            SET DeletedAtUtc = NULL, UpdatedAtUtc = @updated, Revision = Revision + 1
            WHERE Id = @id AND DeletedAtUtc IS NOT NULL;
            """,
            cancellationToken,
            ("@updated", ToDb(now)),
            ("@id", ToDb(imageItemId))).ConfigureAwait(false);
        EnsureFound(affected);
        await ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE Reminders
            SET State = CASE WHEN DueAtUtc > @updated THEN 5 ELSE 4 END,
                NotificationState = 4, UpdatedAtUtc = @updated
            WHERE ImageItemId = @id AND State = 3;
            """,
            cancellationToken,
            ("@updated", ToDb(now)),
            ("@id", ToDb(imageItemId))).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeletionPlan?> PrepareDeletionAsync(
        Guid imageItemId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT i.AssetId, a.OriginalRelativePath, a.ThumbnailRelativePath,
                   (SELECT COUNT(*) FROM ImageItems refs WHERE refs.AssetId = i.AssetId)
            FROM ImageItems i
            INNER JOIN ImageAssets a ON a.Id = i.AssetId
            WHERE i.Id = @id AND i.DeletedAtUtc IS NOT NULL;
            """;
        command.Parameters.AddWithValue("@id", ToDb(imageItemId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var assetId = Guid.Parse(reader.GetString(0));
        var original = ManagedRelativePath.Parse(reader.GetString(1));
        var thumbnail = reader.IsDBNull(2) ? null : ManagedRelativePath.Parse(reader.GetString(2));
        var deleteAssetFiles = reader.GetInt64(3) == 1;
        await reader.DisposeAsync().ConfigureAwait(false);

        var jobId = Guid.NewGuid();
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO DeletionJobs (
                Id, ImageItemId, AssetId, OriginalRelativePath, ThumbnailRelativePath,
                State, AttemptCount, LastErrorCode, CreatedAtUtc, UpdatedAtUtc, CompletedAtUtc)
            VALUES (@id, @itemId, @assetId, @original, @thumbnail,
                    1, 0, NULL, @created, @updated, NULL);
            """,
            cancellationToken,
            ("@id", ToDb(jobId)),
            ("@itemId", ToDb(imageItemId)),
            ("@assetId", ToDb(assetId)),
            ("@original", original.Value),
            ("@thumbnail", thumbnail?.Value),
            ("@created", ToDb(now)),
            ("@updated", ToDb(now))).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new DeletionPlan(jobId, imageItemId, assetId, original, thumbnail, deleteAssetFiles);
    }

    public async Task CompleteDeletionAsync(
        DeletionPlan plan,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            transaction,
            "DELETE FROM ImageItems WHERE Id = @id AND DeletedAtUtc IS NOT NULL;",
            cancellationToken,
            ("@id", ToDb(plan.ImageItemId))).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            transaction,
            "DELETE FROM ImageAssets WHERE Id = @id AND NOT EXISTS (SELECT 1 FROM ImageItems WHERE AssetId = @id);",
            cancellationToken,
            ("@id", ToDb(plan.AssetId))).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE DeletionJobs
            SET State = 2, UpdatedAtUtc = @updated, CompletedAtUtc = @updated
            WHERE Id = @id;
            """,
            cancellationToken,
            ("@updated", ToDb(now)),
            ("@id", ToDb(plan.JobId))).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task FailDeletionAsync(
        Guid jobId,
        string errorCode,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            transaction: null,
            """
            UPDATE DeletionJobs
            SET State = 3, AttemptCount = AttemptCount + 1,
                LastErrorCode = @error, UpdatedAtUtc = @updated
            WHERE Id = @id;
            """,
            cancellationToken,
            ("@error", errorCode),
            ("@updated", ToDb(now)),
            ("@id", ToDb(jobId))).ConfigureAwait(false);
    }

    private async Task UpdateImportJobAsync(
        Guid jobId,
        ImportJobState state,
        Guid? imageItemId,
        string? errorCode,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            transaction: null,
            """
            UPDATE ImportJobs
            SET State = @state, ImageItemId = @itemId, LastErrorCode = @error,
                AttemptCount = AttemptCount + 1, UpdatedAtUtc = @updated, CompletedAtUtc = @updated
            WHERE Id = @id;
            """,
            cancellationToken,
            ("@state", (int)state),
            ("@itemId", imageItemId is null ? null : ToDb(imageItemId.Value)),
            ("@error", errorCode),
            ("@updated", ToDb(completedAtUtc)),
            ("@id", ToDb(jobId))).ConfigureAwait(false);
    }

    private async Task<LibraryEntry?> GetEntryAsync(
        SqliteConnection connection,
        string predicate,
        string value,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT {EntryColumns}
            FROM ImageItems i
            INNER JOIN ImageAssets a ON a.Id = i.AssetId
            WHERE {predicate}
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@value", value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadEntry(reader)
            : null;
    }

    private static async Task<IReadOnlyList<LibraryEntry>> AttachCategoriesAsync(
        SqliteConnection connection,
        IReadOnlyList<LibraryEntry> entries,
        CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
        {
            return entries;
        }

        await using var command = connection.CreateCommand();
        var parameterNames = new string[entries.Count];
        for (var index = 0; index < entries.Count; index++)
        {
            parameterNames[index] = $"@id{index}";
            command.Parameters.AddWithValue(parameterNames[index], ToDb(entries[index].Item.Id));
        }

        command.CommandText =
            $"""
            SELECT ic.ImageItemId, c.Id, c.Name, c.CreatedAtUtc, c.UpdatedAtUtc, ic.Source
            FROM ImageCategories ic
            INNER JOIN Categories c ON c.Id = ic.CategoryId
            WHERE ic.ImageItemId IN ({string.Join(",", parameterNames)})
            ORDER BY c.Name COLLATE NOCASE;
            """;
        var categoriesByItem = new Dictionary<Guid, List<ImageCategory>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var itemId = Guid.Parse(reader.GetString(0));
            var category = new Category(
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                ParseDate(reader.GetString(3)),
                ParseDate(reader.GetString(4)));
            if (!categoriesByItem.TryGetValue(itemId, out var list))
            {
                list = [];
                categoriesByItem[itemId] = list;
            }

            list.Add(new ImageCategory(category, (CategoryAssignmentSource)reader.GetInt32(5)));
        }

        return entries
            .Select(entry => entry with
            {
                Categories = categoriesByItem.TryGetValue(entry.Item.Id, out var categories)
                    ? categories
                    : [],
            })
            .ToArray();
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

    private static LibraryEntry ReadEntry(SqliteDataReader reader)
    {
        var item = new ImageItem(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            reader.GetString(2),
            (ImageSourceKind)reader.GetInt32(3),
            reader.GetString(4),
            reader.GetString(5),
            (ContentFieldSource)reader.GetInt32(6),
            (ContentFieldSource)reader.GetInt32(7),
            (AnalysisState)reader.GetInt32(8),
            reader.GetInt64(9),
            ParseDate(reader.GetString(10)),
            ParseDate(reader.GetString(11)),
            reader.IsDBNull(12) ? null : ParseDate(reader.GetString(12)));
        var asset = new ImageAsset(
            Guid.Parse(reader.GetString(13)),
            Sha256Hash.Parse(reader.GetString(14)),
            ManagedRelativePath.Parse(reader.GetString(15)),
            reader.IsDBNull(16) ? null : ManagedRelativePath.Parse(reader.GetString(16)),
            reader.GetString(17),
            reader.GetInt64(18),
            reader.GetInt32(19),
            reader.GetInt32(20),
            ParseDate(reader.GetString(21)));
        return new LibraryEntry(item, asset, []);
    }

    private static Category ReadCategory(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        ParseDate(reader.GetString(2)),
        ParseDate(reader.GetString(3)));

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string ToDb(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);

    private static string ToDb(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static void EnsureFound(int affected)
    {
        if (affected == 0)
        {
            throw new KeyNotFoundException("The requested library record was not found.");
        }
    }
}
