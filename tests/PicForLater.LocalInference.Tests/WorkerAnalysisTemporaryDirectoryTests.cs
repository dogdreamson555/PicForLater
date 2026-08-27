using PicForLater.Infrastructure.Storage;

namespace PicForLater.LocalInference.Tests;

public sealed class WorkerAnalysisTemporaryDirectoryTests
{
    [Fact]
    public async Task DisposeAsync_RemovesCurrentWorkerDirectory()
    {
        using var root = new TemporaryRoot();
        var temporaryDirectory = await WorkerAnalysisTemporaryDirectory.CreateAsync(root.Paths);
        var directoryPath = temporaryDirectory.DirectoryPath;
        await File.WriteAllBytesAsync(
            Path.Combine(directoryPath, $"{Guid.NewGuid():N}.png"),
            [1, 2, 3]);

        await temporaryDirectory.DisposeAsync();

        Assert.False(Directory.Exists(directoryPath));
    }

    [Fact]
    public async Task CreateAsync_RemovesDirectoryAbandonedByCrashedOrKilledWorker()
    {
        using var root = new TemporaryRoot();
        var abandonedPath = CreateAbandonedWorkerDirectory(root.Paths);

        await using var current = await WorkerAnalysisTemporaryDirectory.CreateAsync(root.Paths);

        Assert.False(Directory.Exists(abandonedPath));
        Assert.True(Directory.Exists(current.DirectoryPath));
    }

    [Fact]
    public async Task CreateAsync_PreservesDirectoryOwnedByActiveWorker()
    {
        using var root = new TemporaryRoot();
        await using var first = await WorkerAnalysisTemporaryDirectory.CreateAsync(root.Paths);
        var firstImagePath = Path.Combine(first.DirectoryPath, $"{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(firstImagePath, [1, 2, 3]);

        await using var second = await WorkerAnalysisTemporaryDirectory.CreateAsync(root.Paths);

        Assert.True(Directory.Exists(first.DirectoryPath));
        Assert.True(File.Exists(firstImagePath));
        Assert.NotEqual(first.DirectoryPath, second.DirectoryPath);
    }

    [Fact]
    public async Task CreateAsync_ReparsePointInExpiredDirectory_StopsWithoutTouchingTarget()
    {
        using var root = new TemporaryRoot();
        var abandonedPath = CreateAbandonedWorkerDirectory(root.Paths);
        var outsideDirectory = Path.Combine(
            Path.GetTempPath(),
            "PicForLater.Tests.Outside",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideDirectory);
        var outsideFile = Path.Combine(outsideDirectory, "private.png");
        await File.WriteAllBytesAsync(outsideFile, [9, 8, 7]);
        var linkedPath = Path.Combine(abandonedPath, $"{Guid.NewGuid():N}.png");
        File.CreateSymbolicLink(linkedPath, outsideFile);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => WorkerAnalysisTemporaryDirectory.CreateAsync(root.Paths));

            Assert.Equal(new byte[] { 9, 8, 7 }, await File.ReadAllBytesAsync(outsideFile));
            Assert.True(File.Exists(linkedPath));
        }
        finally
        {
            File.Delete(linkedPath);
            Directory.Delete(outsideDirectory, recursive: true);
        }
    }

    [Fact]
    public void DeleteExpiredDirectory_PathOutsideManagedRoot_StopsWithoutDeleting()
    {
        using var root = new TemporaryRoot();
        var outsideDirectory = Path.Combine(
            Path.GetTempPath(),
            "PicForLater.Tests.Outside",
            $"worker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDirectory);
        var outsideFile = Path.Combine(outsideDirectory, "private.png");
        File.WriteAllBytes(outsideFile, [4, 5, 6]);
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                WorkerAnalysisTemporaryDirectory.DeleteExpiredDirectory(
                    root.Paths,
                    outsideDirectory));

            Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(outsideFile));
        }
        finally
        {
            Directory.Delete(outsideDirectory, recursive: true);
        }
    }

    private static string CreateAbandonedWorkerDirectory(AppDataPaths paths)
    {
        var directoryPath = Path.Combine(
            paths.AnalysisWorkerCacheDirectoryPath,
            $"worker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directoryPath);
        File.WriteAllBytes(Path.Combine(directoryPath, ".owner.lock"), []);
        File.WriteAllBytes(Path.Combine(directoryPath, $"{Guid.NewGuid():N}.png"), [1, 2, 3]);
        return directoryPath;
    }

    private sealed class TemporaryRoot : IDisposable
    {
        public TemporaryRoot()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "PicForLater.Tests",
                Guid.NewGuid().ToString("N"));
            Paths = new AppDataPaths(RootPath);
            Paths.EnsureCreated();
        }

        public string RootPath { get; }

        public AppDataPaths Paths { get; }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }
}
