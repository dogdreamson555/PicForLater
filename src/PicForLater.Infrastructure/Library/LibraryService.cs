using Microsoft.Data.Sqlite;
using PicForLater.Core.Images;
using PicForLater.Core.Library;
using PicForLater.Core.Reminders;
using PicForLater.Infrastructure.Storage;

namespace PicForLater.Infrastructure.Library;

public sealed class LibraryService : ILibraryService
{
    private const int MaximumCategoryNameLength = 80;
    private const int MaximumTitleLength = 300;
    private const int MaximumSummaryLength = 4_000;

    private readonly SqliteLibraryStore _store;
    private readonly IManagedImageStorage _storage;
    private readonly IReminderOutboxNotifier? _reminderOutboxNotifier;

    public LibraryService(
        AppDataPaths paths,
        IManagedImageStorage storage,
        IReminderOutboxNotifier? reminderOutboxNotifier = null)
    {
        _store = new SqliteLibraryStore(paths ?? throw new ArgumentNullException(nameof(paths)));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _reminderOutboxNotifier = reminderOutboxNotifier;
    }

    public Task<LibraryQueryResult> QueryAsync(
        LibraryQuery query,
        CancellationToken cancellationToken = default) =>
        _store.QueryAsync(query, cancellationToken);

    public Task<LibraryEntry?> GetAsync(
        Guid imageItemId,
        CancellationToken cancellationToken = default) =>
        _store.GetAsync(imageItemId, cancellationToken);

    public Task<IReadOnlyDictionary<Guid, string>> GetSummariesAsync(
        IReadOnlyCollection<Guid> imageItemIds,
        CancellationToken cancellationToken = default) =>
        _store.GetSummariesAsync(imageItemIds, cancellationToken);

    public Task<IReadOnlyList<Category>> GetCategoriesAsync(
        CancellationToken cancellationToken = default) =>
        _store.GetCategoriesAsync(cancellationToken);

    public async Task<Category> CreateCategoryAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = NormalizeCategoryName(name);
        try
        {
            return await _store.CreateCategoryAsync(
                normalizedName,
                DateTimeOffset.UtcNow,
                cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException("A category with the same name already exists.", exception);
        }
    }

    public async Task RenameCategoryAsync(
        Guid categoryId,
        string name,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = NormalizeCategoryName(name);
        try
        {
            await _store.RenameCategoryAsync(
                categoryId,
                normalizedName,
                DateTimeOffset.UtcNow,
                cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException("A category with the same name already exists.", exception);
        }
    }

    public Task DeleteCategoryAsync(
        Guid categoryId,
        CancellationToken cancellationToken = default) =>
        _store.DeleteCategoryAsync(categoryId, cancellationToken);

    public Task SetCategoryAssignmentAsync(
        Guid imageItemId,
        Guid categoryId,
        bool isAssigned,
        CancellationToken cancellationToken = default) =>
        _store.SetCategoryAssignmentAsync(
            imageItemId,
            categoryId,
            isAssigned,
            DateTimeOffset.UtcNow,
            cancellationToken);

    public Task UpdateUserFieldsAsync(
        Guid imageItemId,
        string title,
        string summary,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        title = title.Trim();
        summary = (summary ?? string.Empty).Trim();
        if (title.Length > MaximumTitleLength)
        {
            throw new ArgumentException("The title is too long.", nameof(title));
        }

        if (summary.Length > MaximumSummaryLength)
        {
            throw new ArgumentException("The summary is too long.", nameof(summary));
        }

        return _store.UpdateUserFieldsAsync(
            imageItemId,
            title,
            summary,
            DateTimeOffset.UtcNow,
            cancellationToken);
    }

    public async Task SoftDeleteAsync(
        Guid imageItemId,
        CancellationToken cancellationToken = default)
    {
        await _store.SoftDeleteAsync(
            imageItemId,
            DateTimeOffset.UtcNow,
            cancellationToken).ConfigureAwait(false);
        TryNotifyReminderOutbox();
    }

    public Task RestoreAsync(
        Guid imageItemId,
        CancellationToken cancellationToken = default) =>
        _store.RestoreAsync(imageItemId, DateTimeOffset.UtcNow, cancellationToken);

    public async Task<PermanentDeleteResult> PermanentlyDeleteAsync(
        Guid imageItemId,
        CancellationToken cancellationToken = default)
    {
        var plan = await _store.PrepareDeletionAsync(
            imageItemId,
            DateTimeOffset.UtcNow,
            cancellationToken).ConfigureAwait(false);
        if (plan is null)
        {
            return new PermanentDeleteResult(PermanentDeleteStatus.NotFound);
        }

        try
        {
            if (plan.DeleteAssetFiles)
            {
                if (plan.ThumbnailRelativePath is not null)
                {
                    await _storage.DeleteManagedAsync(
                        plan.ThumbnailRelativePath,
                        cancellationToken).ConfigureAwait(false);
                }

                await _storage.DeleteManagedAsync(
                    plan.OriginalRelativePath,
                    cancellationToken).ConfigureAwait(false);
            }

            await _store.CompleteDeletionAsync(
                plan,
                DateTimeOffset.UtcNow,
                cancellationToken).ConfigureAwait(false);
            TryNotifyReminderOutbox();
            return new PermanentDeleteResult(PermanentDeleteStatus.Completed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or SqliteException)
        {
            var errorCode = exception switch
            {
                UnauthorizedAccessException => "DeletionAccessDenied",
                SqliteException => "DeletionDatabaseFailed",
                InvalidOperationException => "DeletionPathRejected",
                _ => "DeletionIoFailed",
            };
            try
            {
                await _store.FailDeletionAsync(
                    plan.JobId,
                    errorCode,
                    DateTimeOffset.UtcNow,
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // The original item remains soft-deleted. A later reconciliation pass
                // can inspect the persistent deletion plan and finish safely.
            }

            return new PermanentDeleteResult(PermanentDeleteStatus.RetryRequired, errorCode);
        }
    }

    private static string NormalizeCategoryName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normalized = name.Trim();
        if (normalized.Length > MaximumCategoryNameLength)
        {
            throw new ArgumentException("The category name is too long.", nameof(name));
        }

        return normalized;
    }

    private void TryNotifyReminderOutbox()
    {
        try
        {
            _reminderOutboxNotifier?.Notify();
        }
        catch
        {
            // SQLite owns the durable cancellation operation. The wake signal
            // only reduces latency; startup reconciliation remains authoritative.
        }
    }
}
