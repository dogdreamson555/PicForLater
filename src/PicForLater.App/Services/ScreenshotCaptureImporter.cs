using System.Globalization;
using PicForLater.App.Models;
using PicForLater.Core.Images;
using PicForLater.Core.Library;

namespace PicForLater.App.Services;

internal sealed class ScreenshotCaptureImporter : IScreenshotCaptureImporter
{
    private readonly Func<IImageImportService?> _importerAccessor;
    private readonly Func<Stream, CancellationToken, Task<MemoryStream>> _normalizeToPngAsync;
    private readonly string _fileNameFormat;
    private readonly TimeProvider _timeProvider;

    internal ScreenshotCaptureImporter(
        Func<IImageImportService?> importerAccessor,
        Func<Stream, CancellationToken, Task<MemoryStream>> normalizeToPngAsync,
        string fileNameFormat,
        TimeProvider? timeProvider = null)
    {
        _importerAccessor = importerAccessor ?? throw new ArgumentNullException(nameof(importerAccessor));
        _normalizeToPngAsync = normalizeToPngAsync
            ?? throw new ArgumentNullException(nameof(normalizeToPngAsync));
        ArgumentException.ThrowIfNullOrWhiteSpace(fileNameFormat);
        _fileNameFormat = fileNameFormat;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ScreenshotImportResult> ImportAsync(
        ScreenshotClipboardImage image,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();
        var importer = _importerAccessor();
        if (importer is null)
        {
            throw CreateFailure(ScreenshotCaptureFailureKind.Import);
        }

        string fileName;
        try
        {
            fileName = string.Format(
                CultureInfo.CurrentCulture,
                _fileNameFormat,
                _timeProvider.GetLocalNow().DateTime);
        }
        catch (FormatException exception)
        {
            throw CreateFailure(ScreenshotCaptureFailureKind.Import, exception);
        }

        try
        {
            ImageImportResult result;
            using Stream source = image.OpenReadStream();
            if (image.Format == ScreenshotClipboardImageFormat.Png)
            {
                result = await ImportPngAsync(importer, source, fileName, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (image.Format == ScreenshotClipboardImageFormat.DibV5)
            {
                await using MemoryStream normalized = await NormalizeDibV5Async(
                    source,
                    cancellationToken).ConfigureAwait(false);
                normalized.Position = 0;
                result = await ImportPngAsync(importer, normalized, fileName, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                throw CreateFailure(ScreenshotCaptureFailureKind.InvalidImage);
            }

            return result.Status switch
            {
                ImageImportStatus.Imported => new ScreenshotImportResult(
                    ScreenshotImportStatus.Imported,
                    result.ImageItemId),
                ImageImportStatus.Duplicate => new ScreenshotImportResult(
                    ScreenshotImportStatus.Duplicate,
                    result.ImageItemId),
                _ => throw CreateFailure(ScreenshotCaptureFailureKind.Import),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ScreenshotCaptureImportException)
        {
            throw;
        }
        catch (ImageImportException exception) when (IsInvalidImageError(exception.ErrorCode))
        {
            throw CreateFailure(ScreenshotCaptureFailureKind.InvalidImage, exception);
        }
        catch (ImageImportException exception)
        {
            throw CreateFailure(ScreenshotCaptureFailureKind.Import, exception);
        }
        catch (Exception exception)
        {
            throw CreateFailure(ScreenshotCaptureFailureKind.Import, exception);
        }
    }

    private async Task<MemoryStream> NormalizeDibV5Async(
        Stream source,
        CancellationToken cancellationToken)
    {
        try
        {
            MemoryStream normalized = await _normalizeToPngAsync(source, cancellationToken)
                .ConfigureAwait(false);
            if (!normalized.CanRead || !normalized.CanSeek)
            {
                normalized.Dispose();
                throw new InvalidDataException("The normalized screenshot stream is unavailable.");
            }

            return normalized;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // DIBV5 has already passed native bounds validation. Any remaining
            // WIC normalization failure is still an invalid Clipboard image, not
            // a storage or database failure.
            throw CreateFailure(ScreenshotCaptureFailureKind.InvalidImage, exception);
        }
    }

    private static Task<ImageImportResult> ImportPngAsync(
        IImageImportService importer,
        Stream source,
        string fileName,
        CancellationToken cancellationToken) =>
        importer.ImportAsync(
            source,
            fileName,
            ImageSourceKind.Clipboard,
            ManagedImageFormat.Png,
            cancellationToken);

    private static bool IsInvalidImageError(string errorCode) => errorCode is
        "InvalidImage" or "FileTypeMismatch" or "ImageDimensionsUnsupported";

    private static ScreenshotCaptureImportException CreateFailure(
        ScreenshotCaptureFailureKind failureKind,
        Exception? innerException = null) =>
        new(
            failureKind,
            failureKind == ScreenshotCaptureFailureKind.InvalidImage
                ? "The captured Clipboard image is invalid."
                : "The captured image could not be imported.",
            innerException);
}
