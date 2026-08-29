using PicForLater.Core.Images;
using PicForLater.Core.Library;
using PicForLater.Infrastructure.Library;
using PicForLater.Infrastructure.Storage;

namespace PicForLater.IntegrationTests;

public sealed class LocalSendInboxImportServiceTests
{
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public void CreateTargetFileName_UsesOnlyTheLeafAndProducesABoundedSafeName()
    {
        var target = LocalSendInboxImportService.CreateTargetFileName(
            "sender/path/CON<bad>\u0001" + new string('界', 100) + ".JPEG");

        Assert.Equal(LocalSendInboxImportService.MaximumInboxFileNameLength, target.Length);
        Assert.Equal('-', target[32]);
        Assert.True(Guid.TryParseExact(target[..32], "N", out _));
        Assert.EndsWith(".jpg", target, StringComparison.Ordinal);
        Assert.DoesNotContain("sender", target, StringComparison.Ordinal);
        Assert.DoesNotContain('<', target);
        Assert.DoesNotContain('>', target);
        Assert.DoesNotContain('\u0001', target);

        var reserved = LocalSendInboxImportService.CreateTargetFileName(@"C:\phone\CON.png");
        Assert.EndsWith("-_CON.png", reserved, StringComparison.Ordinal);
        var reservedWithExtraDot = LocalSendInboxImportService.CreateTargetFileName("CON.archive.png");
        Assert.EndsWith("-_CON.archive.png", reservedWithExtraDot, StringComparison.Ordinal);
        var numberedDeviceWithExtraDot = LocalSendInboxImportService.CreateTargetFileName("COM1.foo.png");
        Assert.EndsWith("-_COM1.foo.png", numberedDeviceWithExtraDot, StringComparison.Ordinal);
        Assert.EndsWith(
            ".png",
            LocalSendInboxImportService.CreateTargetFileName("\uD800.png"),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_ImportsAndRemovesCompleteFilesIncludingDuplicates()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var storage = new ManagedImageStorage(root.Paths);
        using var importer = new ImageImportService(root.Paths, storage, new FakeImageProcessor());
        var service = new LocalSendInboxImportService(root.Paths, importer);
        var library = new LibraryService(root.Paths, storage);
        var firstPath = WriteInboxFile(
            root.Paths,
            LocalSendInboxImportService.CreateTargetFileName("phone/photos/first.png"),
            TinyPng);
        var secondPath = WriteInboxFile(
            root.Paths,
            LocalSendInboxImportService.CreateTargetFileName("second.png"),
            TinyPng);

        var first = await service.ImportAsync(firstPath);
        var duplicate = await service.ImportAsync(secondPath);

        Assert.Equal(LocalSendInboxImportStatus.Imported, first.Status);
        Assert.True(first.InboxFileRemoved);
        Assert.False(File.Exists(firstPath));
        Assert.Equal(LocalSendInboxImportStatus.Duplicate, duplicate.Status);
        Assert.Equal(first.ImageItemId, duplicate.ImageItemId);
        Assert.True(duplicate.InboxFileRemoved);
        Assert.False(File.Exists(secondPath));
        var entry = await library.GetAsync(first.ImageItemId!.Value);
        Assert.NotNull(entry);
        Assert.Equal("first.png", entry.Item.OriginalFileName);
        Assert.Equal(ImageSourceKind.LocalSend, entry.Item.SourceKind);
    }

    [Fact]
    public async Task Import_DeletesInvalidAndUnsupportedFilesWithoutImportingUnsupportedContent()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var storage = new ManagedImageStorage(root.Paths);
        using var realImporter = new ImageImportService(root.Paths, storage, new FakeImageProcessor());
        var realService = new LocalSendInboxImportService(root.Paths, realImporter);
        var invalidPath = WriteInboxFile(
            root.Paths,
            LocalSendInboxImportService.CreateTargetFileName("broken.png"),
            "not an image"u8.ToArray());

        var invalid = await realService.ImportAsync(invalidPath);

        Assert.Equal(LocalSendInboxImportStatus.Invalid, invalid.Status);
        Assert.Equal("InvalidImage", invalid.ErrorCode);
        Assert.True(invalid.InboxFileRemoved);
        Assert.False(File.Exists(invalidPath));

        var recordingImporter = new RecordingImporter();
        var unsupportedService = new LocalSendInboxImportService(root.Paths, recordingImporter);
        var unsupportedPath = WriteInboxFile(root.Paths, $"{Guid.NewGuid():N}-notes.txt", [1, 2, 3]);

        var unsupported = await unsupportedService.ImportAsync(unsupportedPath);

        Assert.Equal(LocalSendInboxImportStatus.Unsupported, unsupported.Status);
        Assert.Equal("UnsupportedImageExtension", unsupported.ErrorCode);
        Assert.True(unsupported.InboxFileRemoved);
        Assert.Empty(recordingImporter.Calls);
    }

    [Fact]
    public async Task Import_RetainsFilesForRetryAndCancellation()
    {
        using var root = new TemporaryAppDataRoot();
        var retryImporter = new RecordingImporter((_, _) =>
            throw new ImageImportException("StorageIoFailed", "Temporary storage failure."));
        var service = new LocalSendInboxImportService(root.Paths, retryImporter);
        var retryPath = WriteInboxFile(
            root.Paths,
            LocalSendInboxImportService.CreateTargetFileName("retry.png"),
            TinyPng);

        var retry = await service.ImportAsync(retryPath);

        Assert.Equal(LocalSendInboxImportStatus.RetryPending, retry.Status);
        Assert.Equal("StorageIoFailed", retry.ErrorCode);
        Assert.False(retry.InboxFileRemoved);
        Assert.True(File.Exists(retryPath));

        var invalidResultImporter = new RecordingImporter((_, _) => Task.FromResult(
            new ImageImportResult((ImageImportStatus)999, Guid.NewGuid())));
        var invalidResultService = new LocalSendInboxImportService(root.Paths, invalidResultImporter);
        var invalidResultPath = WriteInboxFile(
            root.Paths,
            LocalSendInboxImportService.CreateTargetFileName("invalid-result.png"),
            TinyPng);

        var invalidResult = await invalidResultService.ImportAsync(invalidResultPath);

        Assert.Equal(LocalSendInboxImportStatus.RetryPending, invalidResult.Status);
        Assert.Equal("InboxImportResultInvalid", invalidResult.ErrorCode);
        Assert.True(File.Exists(invalidResultPath));

        var argumentFailureImporter = new RecordingImporter((_, _) =>
            throw new ArgumentException("Unexpected importer configuration failure."));
        var argumentFailureService = new LocalSendInboxImportService(root.Paths, argumentFailureImporter);
        var argumentFailurePath = WriteInboxFile(
            root.Paths,
            LocalSendInboxImportService.CreateTargetFileName("argument-failure.png"),
            TinyPng);

        var argumentFailure = await argumentFailureService.ImportAsync(argumentFailurePath);

        Assert.Equal(LocalSendInboxImportStatus.RetryPending, argumentFailure.Status);
        Assert.Equal("InboxImportFailed", argumentFailure.ErrorCode);
        Assert.True(File.Exists(argumentFailurePath));

        using var cancellation = new CancellationTokenSource();
        var cancellingImporter = new RecordingImporter((_, token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<ImageImportResult>(token);
        });
        var cancellingService = new LocalSendInboxImportService(root.Paths, cancellingImporter);
        var cancelledPath = WriteInboxFile(
            root.Paths,
            LocalSendInboxImportService.CreateTargetFileName("cancelled.png"),
            TinyPng);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cancellingService.ImportAsync(cancelledPath, cancellation.Token));
        Assert.True(File.Exists(cancelledPath));
    }

    [Fact]
    public async Task Import_RejectsEscapeDirectoryAndReparsePointWithoutTouchingTargets()
    {
        using var root = new TemporaryAppDataRoot();
        var importer = new RecordingImporter();
        var service = new LocalSendInboxImportService(root.Paths, importer);
        var outsidePath = Path.Combine(
            Path.GetTempPath(),
            "PicForLater.Tests",
            $"outside-{Guid.NewGuid():N}.png");
        Directory.CreateDirectory(Path.GetDirectoryName(outsidePath)!);
        await File.WriteAllBytesAsync(outsidePath, TinyPng);

        try
        {
            var escaped = await service.ImportAsync(outsidePath);

            Assert.Equal(LocalSendInboxImportStatus.Rejected, escaped.Status);
            Assert.Equal("InboxPathRejected", escaped.ErrorCode);
            Assert.True(File.Exists(outsidePath));

            var alternateDataStreamPath = Path.Combine(
                root.Paths.LocalSendInboxDirectoryPath,
                "host.png:stream");
            var alternateDataStream = await service.ImportAsync(alternateDataStreamPath);

            Assert.Equal(LocalSendInboxImportStatus.Rejected, alternateDataStream.Status);
            Assert.Equal("InboxFileNameRejected", alternateDataStream.ErrorCode);

            var overlongPath = WriteInboxFile(
                root.Paths,
                new string('a', 117) + ".png",
                TinyPng);
            var overlong = await service.ImportAsync(overlongPath);

            Assert.Equal(LocalSendInboxImportStatus.Rejected, overlong.Status);
            Assert.Equal("InboxFileNameRejected", overlong.ErrorCode);
            Assert.True(File.Exists(overlongPath));

            var directoryPath = Path.Combine(root.Paths.LocalSendInboxDirectoryPath, "folder.png");
            Directory.CreateDirectory(directoryPath);
            var directory = await service.ImportAsync(directoryPath);

            Assert.Equal(LocalSendInboxImportStatus.Rejected, directory.Status);
            Assert.Equal("InboxDirectoryRejected", directory.ErrorCode);

            var linkPath = Path.Combine(root.Paths.LocalSendInboxDirectoryPath, "linked.png");
            File.CreateSymbolicLink(linkPath, outsidePath);
            var linked = await service.ImportAsync(linkPath);

            Assert.Equal(LocalSendInboxImportStatus.Rejected, linked.Status);
            Assert.Equal("InboxReparsePointRejected", linked.ErrorCode);
            Assert.True(File.Exists(outsidePath));
            Assert.Empty(importer.Calls);
        }
        finally
        {
            File.Delete(outsidePath);
        }
    }

    [Fact]
    public async Task Recover_DeletesOnlyOldExactPartialFilesAndImportsLookalikes()
    {
        using var root = new TemporaryAppDataRoot();
        var now = new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
        var importer = new RecordingImporter();
        var service = new LocalSendInboxImportService(
            root.Paths,
            importer,
            new FrozenTimeProvider(now));
        var maximumLengthTarget = LocalSendInboxImportService.CreateTargetFileName(
            new string('x', 100) + ".png");
        Assert.Equal(LocalSendInboxImportService.MaximumInboxFileNameLength, maximumLengthTarget.Length);
        var oldPartial = WriteInboxFile(
            root.Paths,
            $"{maximumLengthTarget}.part-{Guid.NewGuid():N}",
            [1]);
        var activePartial = WriteInboxFile(
            root.Paths,
            $"active.png.part-{Guid.NewGuid():N}",
            [2]);
        var lookalike = WriteInboxFile(root.Paths, "holiday.part-notes.png", TinyPng);
        File.SetLastWriteTimeUtc(oldPartial, now.AddHours(-25).UtcDateTime);
        File.SetLastWriteTimeUtc(activePartial, now.AddHours(-1).UtcDateTime);

        var recovery = await service.RecoverAsync();

        Assert.Contains(recovery.Items, item =>
            item.Status == LocalSendInboxImportStatus.StalePartialRemoved);
        Assert.Contains(recovery.Items, item =>
            item.Status == LocalSendInboxImportStatus.ActivePartialSkipped);
        Assert.Contains(recovery.Items, item => item.Status == LocalSendInboxImportStatus.Imported);
        Assert.False(File.Exists(oldPartial));
        Assert.True(File.Exists(activePartial));
        Assert.False(File.Exists(lookalike));
        var call = Assert.Single(importer.Calls);
        Assert.Equal("holiday.part-notes.png", call.OriginalFileName);
    }

    [Fact]
    public async Task Recover_IsolatesFailuresAndRestoresOnlyValidGuidPrefixes()
    {
        using var root = new TemporaryAppDataRoot();
        var importer = new RecordingImporter((call, _) =>
        {
            if (call.OriginalFileName == "retry.png")
            {
                throw new ImageImportException("StorageIoFailed", "Temporary failure.");
            }

            var status = call.OriginalFileName == "duplicate.png"
                ? ImageImportStatus.Duplicate
                : ImageImportStatus.Imported;
            return Task.FromResult(new ImageImportResult(status, Guid.NewGuid()));
        });
        var service = new LocalSendInboxImportService(root.Paths, importer);
        var importedPath = WriteInboxFile(
            root.Paths,
            LocalSendInboxImportService.CreateTargetFileName("imported.png"),
            TinyPng);
        var retryPath = WriteInboxFile(
            root.Paths,
            LocalSendInboxImportService.CreateTargetFileName("retry.png"),
            TinyPng);
        var duplicatePath = WriteInboxFile(
            root.Paths,
            LocalSendInboxImportService.CreateTargetFileName("duplicate.png"),
            TinyPng);
        var malformedName = "not-a-guid-prefix.png";
        var malformedPath = WriteInboxFile(root.Paths, malformedName, TinyPng);

        var recovery = await service.RecoverAsync();

        Assert.Equal(2, recovery.ImportedCount);
        Assert.Equal(1, recovery.DuplicateCount);
        Assert.Equal(1, recovery.RetryPendingCount);
        Assert.False(File.Exists(importedPath));
        Assert.True(File.Exists(retryPath));
        Assert.False(File.Exists(duplicatePath));
        Assert.False(File.Exists(malformedPath));
        Assert.Contains(importer.Calls, call => call.OriginalFileName == malformedName);
        Assert.All(importer.Calls, call => Assert.Equal(ImageSourceKind.LocalSend, call.SourceKind));
        Assert.All(importer.Calls, call => Assert.Equal(ManagedImageFormat.Png, call.ExpectedFormat));
    }

    [Fact]
    public void Constructor_RejectsAnInboxDirectoryReparsePoint()
    {
        using var root = new TemporaryAppDataRoot();
        root.Paths.EnsureCreated();
        var outsideDirectory = Path.Combine(
            Path.GetTempPath(),
            "PicForLater.Tests",
            $"outside-inbox-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDirectory);
        Directory.Delete(root.Paths.LocalSendInboxDirectoryPath);
        Directory.CreateSymbolicLink(root.Paths.LocalSendInboxDirectoryPath, outsideDirectory);

        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                new LocalSendInboxImportService(root.Paths, new RecordingImporter()));
        }
        finally
        {
            Directory.Delete(root.Paths.LocalSendInboxDirectoryPath);
            Directory.Delete(outsideDirectory);
        }
    }

    private static string WriteInboxFile(AppDataPaths paths, string fileName, byte[] content)
    {
        paths.EnsureCreated();
        var path = Path.Combine(paths.LocalSendInboxDirectoryPath, fileName);
        File.WriteAllBytes(path, content);
        return path;
    }

    private sealed record ImportCall(
        string OriginalFileName,
        ImageSourceKind SourceKind,
        ManagedImageFormat? ExpectedFormat,
        byte[] Content);

    private sealed class RecordingImporter(
        Func<ImportCall, CancellationToken, Task<ImageImportResult>>? behavior = null)
        : IImageImportService
    {
        private readonly Func<ImportCall, CancellationToken, Task<ImageImportResult>> _behavior =
            behavior ?? ((_, _) => Task.FromResult(
                new ImageImportResult(ImageImportStatus.Imported, Guid.NewGuid())));

        public List<ImportCall> Calls { get; } = [];

        public async Task<ImageImportResult> ImportAsync(
            Stream source,
            string originalFileName,
            ImageSourceKind sourceKind,
            ManagedImageFormat? expectedFormat = null,
            CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, cancellationToken);
            var call = new ImportCall(
                originalFileName,
                sourceKind,
                expectedFormat,
                buffer.ToArray());
            Calls.Add(call);
            return await _behavior(call, cancellationToken);
        }
    }

    private sealed class FakeImageProcessor : IImageContentProcessor
    {
        public async Task<ImageInspection> InspectAndCreateThumbnailAsync(
            Stream source,
            CancellationToken cancellationToken = default)
        {
            var header = new byte[8];
            await source.ReadExactlyAsync(header, cancellationToken);
            if (!header.AsSpan().SequenceEqual(TinyPng.AsSpan(0, 8)))
            {
                throw new InvalidDataException("Test image is not PNG.");
            }

            return new ImageInspection(
                ManagedImageFormat.Png,
                "image/png",
                1,
                1,
                TinyPng);
        }
    }

    private sealed class FrozenTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
