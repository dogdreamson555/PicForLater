using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PicForLater.Core.Analysis;
using PicForLater.Core.Images;
using PicForLater.Infrastructure.Storage;

namespace PicForLater.Infrastructure.Analysis;

public sealed class SqliteAnalysisReanalysisService : IAnalysisReanalysisService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AppDataPaths _paths;
    private readonly IAnalysisProfileSnapshotProvider _profileProvider;
    private readonly IAnalysisQueueNotifier? _queueNotifier;

    public SqliteAnalysisReanalysisService(
        AppDataPaths paths,
        IAnalysisProfileSnapshotProvider profileProvider,
        IAnalysisQueueNotifier? queueNotifier = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _profileProvider = profileProvider ?? throw new ArgumentNullException(nameof(profileProvider));
        _queueNotifier = queueNotifier;
    }

    public async Task<ReanalysisQueueResult> QueueAsync(
        IReadOnlyCollection<Guid> imageItemIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageItemIds);
        var uniqueIds = imageItemIds.Distinct().ToArray();
        var snapshot = await _profileProvider.GetCurrentSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var snapshotJson = JsonSerializer.Serialize(snapshot, JsonOptions);
        var queued = 0;
        foreach (var imageItemId in uniqueIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                queued += await QueueOneAsync(
                    imageItemId,
                    snapshot,
                    snapshotJson,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException)
            {
                // Each selected item is an independent transaction. A damaged or
                // concurrently changed item must not roll back already queued work.
            }
        }

        if (queued > 0)
        {
            try
            {
                _queueNotifier?.Notify();
            }
            catch
            {
                // The durable rows are authoritative; waking the worker is only an optimization.
            }
        }

        return new ReanalysisQueueResult(uniqueIds.Length, queued, uniqueIds.Length - queued);
    }

    private async Task<int> QueueOneAsync(
        Guid imageItemId,
        ModelProfileSnapshot snapshot,
        string snapshotJson,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        long? revision = null;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT Revision FROM ImageItems WHERE Id = @id AND DeletedAtUtc IS NULL;";
            select.Parameters.AddWithValue("@id", ToDb(imageItemId));
            var value = await select.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is not null and not DBNull)
            {
                revision = Convert.ToInt64(value, CultureInfo.InvariantCulture);
            }
        }

        if (revision is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }

        var now = DateTimeOffset.UtcNow;
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT OR IGNORE INTO AnalysisJobs (
                Id, ImageItemId, Kind, InputRevision, State, AttemptCount,
                NotBeforeUtc, LeaseExpiresAtUtc, LastErrorCode,
                CreatedAtUtc, UpdatedAtUtc, CompletedAtUtc,
                CurrentStage, LeaseOwner, AnalysisMode, ProfileRevision, ModelProfileSnapshotJson)
            VALUES (
                @id, @itemId, @kind, @revision, @state, 0,
                @now, NULL, NULL, @now, @now, NULL,
                0, NULL, @mode, @profileRevision, @snapshot);
            """;
        insert.Parameters.AddWithValue("@id", ToDb(Guid.NewGuid()));
        insert.Parameters.AddWithValue("@itemId", ToDb(imageItemId));
        insert.Parameters.AddWithValue("@kind", (int)AnalysisJobKind.Reanalysis);
        insert.Parameters.AddWithValue("@revision", revision.Value);
        insert.Parameters.AddWithValue("@state", (int)AnalysisJobState.Queued);
        insert.Parameters.AddWithValue("@now", ToDb(now));
        insert.Parameters.AddWithValue("@mode", (int)snapshot.AnalysisMode);
        insert.Parameters.AddWithValue("@profileRevision", snapshot.Revision);
        insert.Parameters.AddWithValue("@snapshot", snapshotJson);
        var inserted = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (inserted == 1)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                "UPDATE ImageItems SET AnalysisState = @pending, UpdatedAtUtc = @now WHERE Id = @id;";
            update.Parameters.AddWithValue("@pending", (int)AnalysisState.Pending);
            update.Parameters.AddWithValue("@now", ToDb(now));
            update.Parameters.AddWithValue("@id", ToDb(imageItemId));
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return inserted;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
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

    private static string ToDb(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);

    private static string ToDb(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
