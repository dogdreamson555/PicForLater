using PicForLater.App.Models;
using PicForLater.App.Services;
using PicForLater.Core.Images;
using PicForLater.Core.Library;

namespace PicForLater.IntegrationTests;

public sealed class ScreenshotCaptureImporterTests
{
    private static readonly DateTimeOffset FixedUtc =
        new(2026, 9, 2, 10, 11, 12, TimeSpan.Zero);

    [Fact]
    public async Task Png_UsesDetachedBytesClipboardSourceExpectedPngAndLocalTimestamp()
    {
        var imageImporter = new FakeImageImporter();
        var normalizer = new FakeNormalizer();
        var adapter = CreateAdapter(imageImporter, normalizer);
        var image = new ScreenshotClipboardImage(
            ScreenshotClipboardImageFormat.Png,
            new byte[] { 1, 2, 3 });

        ScreenshotImportResult result = await adapter.ImportAsync(image);

        Assert.Equal(ScreenshotImportStatus.Imported, result.Status);
        ImportCall call = Assert.Single(imageImporter.Calls);
        Assert.Equal(new byte[] { 1, 2, 3 }, call.Bytes);
        Assert.Equal("Capture 20260902-101112.png", call.FileName);
        Assert.Equal(ImageSourceKind.Clipboard, call.SourceKind);
        Assert.Equal(ManagedImageFormat.Png, call.ExpectedFormat);
        Assert.Equal(0, normalizer.CallCount);
    }

    [Fact]
    public async Task DibV5_NormalizesBeforeImportAndMapsDuplicate()
    {
        var duplicateId = Guid.NewGuid();
        var imageImporter = new FakeImageImporter
        {
            Result = new ImageImportResult(ImageImportStatus.Duplicate, duplicateId),
        };
        var normalizer = new FakeNormalizer { NormalizedBytes = new byte[] { 9, 8, 7 } };
        var adapter = CreateAdapter(imageImporter, normalizer);
        var image = new ScreenshotClipboardImage(
            ScreenshotClipboardImageFormat.DibV5,
            new byte[] { 4, 5, 6 });

        ScreenshotImportResult result = await adapter.ImportAsync(image);

        Assert.Equal(ScreenshotImportStatus.Duplicate, result.Status);
        Assert.Equal(duplicateId, result.ImageItemId);
        Assert.Equal(new byte[] { 4, 5, 6 }, normalizer.SourceBytes);
        Assert.Equal(new byte[] { 9, 8, 7 }, Assert.Single(imageImporter.Calls).Bytes);
    }

    [Theory]
    [InlineData("InvalidImage")]
    [InlineData("FileTypeMismatch")]
    [InlineData("ImageDimensionsUnsupported")]
    public async Task InvalidImageImportCodesMapToStableInvalidImageFailure(string errorCode)
    {
        var imageImporter = new FakeImageImporter
        {
            Exception = new ImageImportException(errorCode, "fixture"),
        };
        var adapter = CreateAdapter(imageImporter, new FakeNormalizer());

        ScreenshotCaptureImportException exception = await Assert.ThrowsAsync<
            ScreenshotCaptureImportException>(() => adapter.ImportAsync(PngImage()));

        Assert.Equal(ScreenshotCaptureFailureKind.InvalidImage, exception.FailureKind);
    }

    [Fact]
    public async Task StorageImportFailureMapsToStableImportFailure()
    {
        var imageImporter = new FakeImageImporter
        {
            Exception = new ImageImportException("StorageIoFailed", "fixture"),
        };
        var adapter = CreateAdapter(imageImporter, new FakeNormalizer());

        ScreenshotCaptureImportException exception = await Assert.ThrowsAsync<
            ScreenshotCaptureImportException>(() => adapter.ImportAsync(PngImage()));

        Assert.Equal(ScreenshotCaptureFailureKind.Import, exception.FailureKind);
    }

    [Fact]
    public async Task DibNormalizationFailureMapsToInvalidImageButImporterFailureMapsToImport()
    {
        var normalizer = new FakeNormalizer { Exception = new InvalidDataException("fixture") };
        var adapter = CreateAdapter(new FakeImageImporter(), normalizer);
        var dib = new ScreenshotClipboardImage(
            ScreenshotClipboardImageFormat.DibV5,
            new byte[] { 1 });

        ScreenshotCaptureImportException invalid = await Assert.ThrowsAsync<
            ScreenshotCaptureImportException>(() => adapter.ImportAsync(dib));
        Assert.Equal(ScreenshotCaptureFailureKind.InvalidImage, invalid.FailureKind);

        var importer = new FakeImageImporter { Exception = new InvalidOperationException("fixture") };
        adapter = CreateAdapter(importer, new FakeNormalizer());
        ScreenshotCaptureImportException failed = await Assert.ThrowsAsync<
            ScreenshotCaptureImportException>(() => adapter.ImportAsync(dib));
        Assert.Equal(ScreenshotCaptureFailureKind.Import, failed.FailureKind);
    }

    [Fact]
    public async Task MissingPublishedImporterFailsClosedWithoutNormalizing()
    {
        var normalizer = new FakeNormalizer();
        var adapter = new ScreenshotCaptureImporter(
            () => null,
            normalizer.NormalizeToPngAsync,
            "Capture {0:yyyyMMdd-HHmmss}.png",
            new FixedTimeProvider(FixedUtc));

        ScreenshotCaptureImportException exception = await Assert.ThrowsAsync<
            ScreenshotCaptureImportException>(() => adapter.ImportAsync(PngImage()));

        Assert.Equal(ScreenshotCaptureFailureKind.Import, exception.FailureKind);
        Assert.Equal(0, normalizer.CallCount);
    }

    [Fact]
    public async Task CancellationDuringNormalizationPropagatesWithoutImport()
    {
        var importer = new FakeImageImporter();
        var normalizer = new FakeNormalizer { WaitForCancellation = true };
        var adapter = CreateAdapter(importer, normalizer);
        using var cancellation = new CancellationTokenSource();
        var dib = new ScreenshotClipboardImage(
            ScreenshotClipboardImageFormat.DibV5,
            new byte[] { 1 });

        Task<ScreenshotImportResult> task = adapter.ImportAsync(dib, cancellation.Token);
        await normalizer.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Empty(importer.Calls);
    }

    private static ScreenshotCaptureImporter CreateAdapter(
        FakeImageImporter importer,
        FakeNormalizer normalizer) =>
        new(
            () => importer,
            normalizer.NormalizeToPngAsync,
            "Capture {0:yyyyMMdd-HHmmss}.png",
            new FixedTimeProvider(FixedUtc));

    private static ScreenshotClipboardImage PngImage() =>
        new(ScreenshotClipboardImageFormat.Png, new byte[] { 1 });

    private sealed class FakeImageImporter : IImageImportService
    {
        internal List<ImportCall> Calls { get; } = [];
        internal ImageImportResult Result { get; init; } =
            new(ImageImportStatus.Imported, Guid.NewGuid());
        internal Exception? Exception { get; init; }

        public async Task<ImageImportResult> ImportAsync(
            Stream source,
            string originalFileName,
            ImageSourceKind sourceKind,
            ManagedImageFormat? expectedFormat = null,
            CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, cancellationToken);
            Calls.Add(new ImportCall(
                buffer.ToArray(),
                originalFileName,
                sourceKind,
                expectedFormat));
            if (Exception is not null)
            {
                throw Exception;
            }

            return Result;
        }
    }

    private sealed class FakeNormalizer
    {
        internal int CallCount { get; private set; }
        internal byte[] SourceBytes { get; private set; } = [];
        internal byte[] NormalizedBytes { get; init; } = new byte[] { 9 };
        internal Exception? Exception { get; init; }
        internal bool WaitForCancellation { get; init; }
        internal TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<MemoryStream> NormalizeToPngAsync(
            Stream source,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, cancellationToken);
            SourceBytes = buffer.ToArray();
            Started.TrySetResult();
            if (WaitForCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (Exception is not null)
            {
                throw Exception;
            }

            return new MemoryStream(NormalizedBytes, writable: false);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed record ImportCall(
        byte[] Bytes,
        string FileName,
        ImageSourceKind SourceKind,
        ManagedImageFormat? ExpectedFormat);
}
