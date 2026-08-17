using System.Security.Cryptography;
using PicForLater.Core.Images;
using PicForLater.Infrastructure.Storage;

namespace PicForLater.IntegrationTests;

public sealed class ManagedImageStorageTests
{
    // Self-contained 1x1 PNG fixture generated for these tests; it contains no user data.
    private static readonly byte[] PngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public async Task StageAndPromote_StoresImmutableContentAddressedOriginal()
    {
        using var temporaryRoot = new TemporaryAppDataRoot();
        var storage = new ManagedImageStorage(temporaryRoot.Paths);

        var staged = await storage.StageAsync(new MemoryStream(PngBytes));

        Assert.Equal(PngBytes.Length, staged.ByteLength);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(PngBytes)).ToLowerInvariant(),
            staged.ContentHash.Hex);
        Assert.True(File.Exists(temporaryRoot.Paths.Resolve(staged.RelativePath)));

        var promoted = await storage.PromoteAsync(staged, ManagedImageFormat.Png);

        Assert.False(promoted.AlreadyExisted);
        Assert.EndsWith(".png", promoted.RelativePath.Value, StringComparison.Ordinal);
        Assert.False(File.Exists(temporaryRoot.Paths.Resolve(staged.RelativePath)));
        Assert.True(await storage.VerifyAsync(promoted.RelativePath, promoted.ContentHash));

        await using var original = await storage.OpenReadAsync(promoted.RelativePath);
        using var copy = new MemoryStream();
        await original.CopyToAsync(copy);
        Assert.Equal(PngBytes, copy.ToArray());
    }

    [Fact]
    public async Task Promote_DeduplicatesWithoutOverwritingExistingOriginal()
    {
        using var temporaryRoot = new TemporaryAppDataRoot();
        var storage = new ManagedImageStorage(temporaryRoot.Paths);
        var first = await storage.StageAsync(new MemoryStream(PngBytes));
        var second = await storage.StageAsync(new MemoryStream(PngBytes));

        var firstPromotion = await storage.PromoteAsync(first, ManagedImageFormat.Png);
        var secondPromotion = await storage.PromoteAsync(second, ManagedImageFormat.Png);

        Assert.False(firstPromotion.AlreadyExisted);
        Assert.True(secondPromotion.AlreadyExisted);
        Assert.Equal(firstPromotion.RelativePath, secondPromotion.RelativePath);
        Assert.False(File.Exists(temporaryRoot.Paths.Resolve(second.RelativePath)));
        Assert.Single(Directory.EnumerateFiles(temporaryRoot.Paths.OriginalDirectoryPath, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Promote_RejectsAFormatThatDoesNotMatchTheFileSignature()
    {
        using var temporaryRoot = new TemporaryAppDataRoot();
        var storage = new ManagedImageStorage(temporaryRoot.Paths);
        var staged = await storage.StageAsync(new MemoryStream(PngBytes));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => storage.PromoteAsync(staged, ManagedImageFormat.Jpeg));

        Assert.True(File.Exists(temporaryRoot.Paths.Resolve(staged.RelativePath)));
        Assert.Empty(Directory.EnumerateFiles(temporaryRoot.Paths.OriginalDirectoryPath, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Promote_RejectsTamperedStagingContent()
    {
        using var temporaryRoot = new TemporaryAppDataRoot();
        var storage = new ManagedImageStorage(temporaryRoot.Paths);
        var staged = await storage.StageAsync(new MemoryStream(PngBytes));
        await File.AppendAllTextAsync(temporaryRoot.Paths.Resolve(staged.RelativePath), "tampered");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => storage.PromoteAsync(staged, ManagedImageFormat.Png));

        Assert.Empty(Directory.EnumerateFiles(temporaryRoot.Paths.OriginalDirectoryPath, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Stage_CancellationRemovesPartialFile()
    {
        using var temporaryRoot = new TemporaryAppDataRoot();
        var storage = new ManagedImageStorage(temporaryRoot.Paths);
        using var cancellationSource = new CancellationTokenSource();
        using var source = new CancelAfterFirstReadStream(PngBytes, cancellationSource);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => storage.StageAsync(source, cancellationSource.Token));

        Assert.Empty(Directory.EnumerateFiles(temporaryRoot.Paths.StagingDirectoryPath));
    }

    [Fact]
    public async Task Stage_SizeLimitRemovesPartialFile()
    {
        using var temporaryRoot = new TemporaryAppDataRoot();
        var storage = new ManagedImageStorage(temporaryRoot.Paths, maximumStagedBytes: 16);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => storage.StageAsync(new MemoryStream(PngBytes)));

        Assert.Empty(Directory.EnumerateFiles(temporaryRoot.Paths.StagingDirectoryPath));
    }

    [Fact]
    public void Constructor_RejectsAReparsePointInsideTheManagedDirectoryTree()
    {
        using var temporaryRoot = new TemporaryAppDataRoot();
        temporaryRoot.Paths.EnsureCreated();
        var outsidePath = Path.Combine(
            Path.GetTempPath(),
            "PicForLater.Tests.Outside",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsidePath);
        Directory.Delete(temporaryRoot.Paths.StagingDirectoryPath, recursive: true);
        Directory.CreateSymbolicLink(temporaryRoot.Paths.StagingDirectoryPath, outsidePath);

        try
        {
            Assert.Throws<InvalidOperationException>(
                () => new ManagedImageStorage(temporaryRoot.Paths));
            Assert.Empty(Directory.EnumerateFileSystemEntries(outsidePath));
        }
        finally
        {
            Directory.Delete(temporaryRoot.Paths.StagingDirectoryPath);
            Directory.Delete(outsidePath, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteStaging_RejectsAFileSymbolicLinkAndPreservesItsTarget()
    {
        using var temporaryRoot = new TemporaryAppDataRoot();
        var storage = new ManagedImageStorage(temporaryRoot.Paths);
        var outsideDirectory = Path.Combine(
            Path.GetTempPath(),
            "PicForLater.Tests.Outside",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideDirectory);
        var outsideFile = Path.Combine(outsideDirectory, "outside.tmp");
        await File.WriteAllTextAsync(outsideFile, "must remain");
        var linkedRelativePath = ManagedRelativePath.Parse("staging/linked.tmp");
        var linkedPath = Path.Combine(temporaryRoot.Paths.StagingDirectoryPath, "linked.tmp");
        File.CreateSymbolicLink(linkedPath, outsideFile);

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => storage.DeleteStagingAsync(linkedRelativePath));
            Assert.Equal("must remain", await File.ReadAllTextAsync(outsideFile));
        }
        finally
        {
            File.Delete(linkedPath);
            Directory.Delete(outsideDirectory, recursive: true);
        }
    }

    private sealed class CancelAfterFirstReadStream : MemoryStream
    {
        private readonly CancellationTokenSource _cancellationSource;
        private bool _hasCancelled;

        public CancelAfterFirstReadStream(byte[] bytes, CancellationTokenSource cancellationSource)
            : base(bytes)
        {
            _cancellationSource = cancellationSource;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var bytesRead = Read(buffer.Span);
            if (!_hasCancelled)
            {
                _hasCancelled = true;
                _cancellationSource.Cancel();
            }

            return ValueTask.FromResult(bytesRead);
        }
    }
}
