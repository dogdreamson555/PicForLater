using PicForLater.Core.Analysis;
using PicForLater.Core.Images;
using PicForLater.Core.Library;
using PicForLater.Infrastructure.Storage;

namespace PicForLater.Infrastructure.Library;

public sealed class ImageImportService : IImageImportService, IDisposable
{
    private const int MaximumFileNameLength = 260;
    private const long MaximumPixelCount = 100_000_000;

    private readonly SqliteLibraryStore _store;
    private readonly IManagedImageStorage _storage;
    private readonly IImageContentProcessor _processor;
    private readonly IAnalysisQueueNotifier? _analysisQueueNotifier;
    private readonly IAnalysisProfileSnapshotProvider? _analysisProfileSnapshotProvider;
    private readonly SemaphoreSlim _importGate = new(1, 1);
    private bool _disposed;

    public ImageImportService(
        AppDataPaths paths,
        IManagedImageStorage storage,
        IImageContentProcessor processor,
        IAnalysisQueueNotifier? analysisQueueNotifier = null,
        IAnalysisProfileSnapshotProvider? analysisProfileSnapshotProvider = null)
    {
        _store = new SqliteLibraryStore(paths ?? throw new ArgumentNullException(nameof(paths)));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _analysisQueueNotifier = analysisQueueNotifier;
        _analysisProfileSnapshotProvider = analysisProfileSnapshotProvider;
    }

    public async Task<ImageImportResult> ImportAsync(
        Stream source,
        string originalFileName,
        ImageSourceKind sourceKind,
        ManagedImageFormat? expectedFormat = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException("The import stream must be readable.", nameof(source));
        }

        var safeFileName = NormalizeFileName(originalFileName);
        await _importGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ImportCoreAsync(
                source,
                safeFileName,
                sourceKind,
                expectedFormat,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _importGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _importGate.Dispose();
    }

    private async Task<ImageImportResult> ImportCoreAsync(
        Stream source,
        string originalFileName,
        ImageSourceKind sourceKind,
        ManagedImageFormat? expectedFormat,
        CancellationToken cancellationToken)
    {
        StagedImage? staged = null;
        PromotedImage? promoted = null;
        ManagedRelativePath? thumbnailPath = null;
        Guid? jobId = null;
        try
        {
            staged = await _storage.StageAsync(source, cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var job = new ImportJob(
                Guid.NewGuid(),
                staged.RelativePath,
                null,
                originalFileName,
                sourceKind,
                ImportJobState.Staged,
                staged.ContentHash,
                null,
                0,
                null,
                null,
                now,
                now,
                null);
            await _store.CreateImportJobAsync(job, cancellationToken).ConfigureAwait(false);
            jobId = job.Id;

            var duplicate = await _store.FindByHashAsync(
                staged.ContentHash,
                cancellationToken).ConfigureAwait(false);
            if (duplicate is not null)
            {
                await _storage.DeleteStagingAsync(staged.RelativePath, cancellationToken).ConfigureAwait(false);
                staged = null;
                await _store.MarkImportDuplicateAsync(
                    job.Id,
                    duplicate.Item.Id,
                    DateTimeOffset.UtcNow,
                    cancellationToken).ConfigureAwait(false);
                return new ImageImportResult(ImageImportStatus.Duplicate, duplicate.Item.Id);
            }

            ImageInspection inspection;
            await using (var stagedStream = await _storage.OpenReadAsync(
                staged.RelativePath,
                cancellationToken).ConfigureAwait(false))
            {
                inspection = await _processor.InspectAndCreateThumbnailAsync(
                    stagedStream,
                    cancellationToken).ConfigureAwait(false);
            }
            ValidateInspection(inspection, expectedFormat);

            promoted = await _storage.PromoteAsync(
                staged,
                inspection.Format,
                cancellationToken).ConfigureAwait(false);
            staged = null;
            thumbnailPath = await _storage.StoreThumbnailAsync(
                promoted.ContentHash,
                inspection.ThumbnailPng,
                cancellationToken).ConfigureAwait(false);

            now = DateTimeOffset.UtcNow;
            var asset = new ImageAsset(
                Guid.NewGuid(),
                promoted.ContentHash,
                promoted.RelativePath,
                thumbnailPath,
                inspection.MediaType,
                promoted.ByteLength,
                inspection.PixelWidth,
                inspection.PixelHeight,
                now);
            var imageItem = new ImageItem(
                Guid.NewGuid(),
                asset.Id,
                originalFileName,
                sourceKind,
                CreateFallbackTitle(originalFileName),
                string.Empty,
                ContentFieldSource.Fallback,
                ContentFieldSource.Fallback,
                AnalysisState.Pending,
                0,
                now,
                now,
                null);
            var completedJob = job with
            {
                FinalRelativePath = promoted.RelativePath,
                State = ImportJobState.Completed,
                ImageItemId = imageItem.Id,
                UpdatedAtUtc = now,
                CompletedAtUtc = now,
            };
            var analysisJob = new AnalysisJob(
                Guid.NewGuid(),
                imageItem.Id,
                AnalysisJobKind.Initial,
                imageItem.Revision,
                AnalysisJobState.Queued,
                0,
                now,
                null,
                null,
                now,
                now,
                null,
                _analysisProfileSnapshotProvider is null
                    ? ModelProfileSnapshot.Default
                    : await _analysisProfileSnapshotProvider.GetCurrentSnapshotAsync(cancellationToken)
                        .ConfigureAwait(false));

            await _store.CompleteImportAsync(
                asset,
                imageItem,
                completedJob,
                analysisJob,
                cancellationToken).ConfigureAwait(false);
            TryNotifyAnalysisQueue();
            return new ImageImportResult(ImageImportStatus.Imported, imageItem.Id);
        }
        catch (OperationCanceledException)
        {
            await TryCleanupAsync(staged, promoted, thumbnailPath, CancellationToken.None).ConfigureAwait(false);
            if (jobId is not null)
            {
                await TryMarkImportAsync(jobId.Value, "ImportCancelled", cancelled: true).ConfigureAwait(false);
            }

            throw;
        }
        catch (Exception exception)
        {
            await TryCleanupAsync(staged, promoted, thumbnailPath, CancellationToken.None).ConfigureAwait(false);
            var importException = MapImportException(exception);
            if (jobId is not null)
            {
                await TryMarkImportAsync(jobId.Value, importException.ErrorCode, cancelled: false).ConfigureAwait(false);
            }

            throw importException;
        }
    }

    private void TryNotifyAnalysisQueue()
    {
        try
        {
            _analysisQueueNotifier?.Notify();
        }
        catch
        {
            // The durable queue row is already committed. A notification is only
            // a latency optimization; startup/lease reconciliation remains the
            // source of recovery and import success must not depend on it.
        }
    }

    private async Task TryCleanupAsync(
        StagedImage? staged,
        PromotedImage? promoted,
        ManagedRelativePath? thumbnailPath,
        CancellationToken cancellationToken)
    {
        try
        {
            if (staged is not null)
            {
                await _storage.DeleteStagingAsync(staged.RelativePath, cancellationToken).ConfigureAwait(false);
            }

            if (promoted is not null && !promoted.AlreadyExisted)
            {
                var existing = await _store.FindByHashAsync(
                    promoted.ContentHash,
                    cancellationToken).ConfigureAwait(false);
                if (existing is null)
                {
                    if (thumbnailPath is not null)
                    {
                        await _storage.DeleteManagedAsync(thumbnailPath, cancellationToken).ConfigureAwait(false);
                    }

                    await _storage.DeleteManagedAsync(
                        promoted.RelativePath,
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            // Preserve the import failure. Managed paths and persistent jobs let a
            // startup reconciliation pass clean up an abandoned staging/cache file.
        }
    }

    private async Task TryMarkImportAsync(Guid jobId, string errorCode, bool cancelled)
    {
        try
        {
            if (cancelled)
            {
                await _store.MarkImportCancelledAsync(
                    jobId,
                    DateTimeOffset.UtcNow,
                    CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await _store.MarkImportFailedAsync(
                    jobId,
                    errorCode,
                    DateTimeOffset.UtcNow,
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch
        {
            // Do not replace the user-visible import error with bookkeeping failure.
        }
    }

    private static void ValidateInspection(
        ImageInspection inspection,
        ManagedImageFormat? expectedFormat)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        if (expectedFormat is not null && inspection.Format != expectedFormat)
        {
            throw new ImageImportException(
                "FileTypeMismatch",
                "The image file extension does not match its decoded content.");
        }

        if (inspection.PixelWidth <= 0 || inspection.PixelHeight <= 0
            || (long)inspection.PixelWidth * inspection.PixelHeight > MaximumPixelCount)
        {
            throw new ImageImportException(
                "ImageDimensionsUnsupported",
                "The decoded image dimensions exceed the supported safety limit.");
        }
    }

    private static string NormalizeFileName(string originalFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalFileName);
        var fileName = Path.GetFileName(originalFileName.Trim());
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName.Length > MaximumFileNameLength
            || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("The original file name is invalid.", nameof(originalFileName));
        }

        return fileName;
    }

    private static string CreateFallbackTitle(string fileName)
    {
        var title = Path.GetFileNameWithoutExtension(fileName).Trim();
        return string.IsNullOrWhiteSpace(title) ? fileName : title;
    }

    private static ImageImportException MapImportException(Exception exception) => exception switch
    {
        ImageImportException importException => importException,
        UnauthorizedAccessException => new ImageImportException(
            "StorageAccessDenied",
            "The image could not be saved to managed storage.",
            exception),
        InvalidDataException => new ImageImportException(
            "InvalidImage",
            "The file is not a supported decodable image.",
            exception),
        IOException => new ImageImportException(
            "StorageIoFailed",
            "The image import could not be completed because local storage failed.",
            exception),
        _ => new ImageImportException(
            "ImageImportFailed",
            "The image import could not be completed.",
            exception),
    };
}
