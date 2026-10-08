using Microsoft.Data.Sqlite;
using PicForLater.Core.Images;
using PicForLater.Core.Library;
using PicForLater.Infrastructure.Library;
using PicForLater.Infrastructure.Storage;

namespace PicForLater.IntegrationTests;

public sealed class LibraryDeletionRecoveryTests
{
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public async Task PermanentDelete_PreparationFailureReturnsRetryAndKeepsImageRestorable()
    {
        using var root = new TemporaryAppDataRoot();
        var (library, entry) = await ImportAndRecycleAsync(root);
        await ExecuteAsync(root.Paths.DatabasePath,
            "CREATE TRIGGER FailPreparation BEFORE INSERT ON DeletionJobs BEGIN SELECT RAISE(FAIL, 'synthetic preparation failure'); END;");

        var result = await library.PermanentlyDeleteAsync(entry.Item.Id);

        Assert.Equal(PermanentDeleteStatus.RetryRequired, result.Status);
        Assert.Equal("DeletionDatabaseFailed", result.ErrorCode);
        Assert.True(File.Exists(root.Paths.Resolve(entry.Asset.OriginalRelativePath)));
        await library.RestoreAsync(entry.Item.Id);
        Assert.Null((await library.GetAsync(entry.Item.Id))!.Item.DeletedAtUtc);
    }

    [Fact]
    public async Task PermanentDelete_CompletionFailureBlocksRestoreAndReusesPlanOnRetry()
    {
        using var root = new TemporaryAppDataRoot();
        var (library, entry) = await ImportAndRecycleAsync(root);
        await ExecuteAsync(root.Paths.DatabasePath,
            "CREATE TRIGGER FailCompletion BEFORE DELETE ON ImageItems BEGIN SELECT RAISE(FAIL, 'synthetic completion failure'); END;");

        var result = await library.PermanentlyDeleteAsync(entry.Item.Id);

        Assert.Equal(PermanentDeleteStatus.RetryRequired, result.Status);
        Assert.False(File.Exists(root.Paths.Resolve(entry.Asset.OriginalRelativePath)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => library.RestoreAsync(entry.Item.Id));
        Assert.NotNull((await library.GetAsync(entry.Item.Id))!.Item.DeletedAtUtc);
        await ExecuteAsync(root.Paths.DatabasePath, "DROP TRIGGER FailCompletion;");

        var retry = await library.PermanentlyDeleteAsync(entry.Item.Id);

        Assert.Equal(PermanentDeleteStatus.Completed, retry.Status);
        Assert.Null(await library.GetAsync(entry.Item.Id));
        Assert.Equal(1L, await ScalarAsync(root.Paths.DatabasePath, "SELECT COUNT(*) FROM DeletionJobs;"));
        Assert.Equal(1L, await ScalarAsync(root.Paths.DatabasePath, "SELECT COUNT(*) FROM DeletionJobs WHERE State = 2;"));
    }

    [Fact]
    public async Task Restore_AfterDeletionPlanIsCommittedIsRejectedBeforeFilesAreRemoved()
    {
        using var root = new TemporaryAppDataRoot();
        var (library, entry) = await ImportAndRecycleAsync(root);
        var store = new SqliteLibraryStore(root.Paths);
        var plan = await store.PrepareDeletionAsync(entry.Item.Id, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.NotNull(plan);

        await Assert.ThrowsAsync<InvalidOperationException>(() => library.RestoreAsync(entry.Item.Id));

        Assert.True(File.Exists(root.Paths.Resolve(entry.Asset.OriginalRelativePath)));
        Assert.NotNull((await library.GetAsync(entry.Item.Id))!.Item.DeletedAtUtc);
        Assert.Equal(PermanentDeleteStatus.Completed, (await library.PermanentlyDeleteAsync(entry.Item.Id)).Status);
        Assert.Equal(1L, await ScalarAsync(root.Paths.DatabasePath, "SELECT COUNT(*) FROM DeletionJobs;"));
    }

    [Fact]
    public async Task Restore_BeforeDeletionPlanWinsAndKeepsOriginal()
    {
        using var root = new TemporaryAppDataRoot();
        var (library, entry) = await ImportAndRecycleAsync(root);
        await library.RestoreAsync(entry.Item.Id);

        var result = await library.PermanentlyDeleteAsync(entry.Item.Id);

        Assert.Equal(PermanentDeleteStatus.NotFound, result.Status);
        Assert.Null((await library.GetAsync(entry.Item.Id))!.Item.DeletedAtUtc);
        Assert.True(File.Exists(root.Paths.Resolve(entry.Asset.OriginalRelativePath)));
        Assert.Equal(0L, await ScalarAsync(root.Paths.DatabasePath, "SELECT COUNT(*) FROM DeletionJobs;"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconcilePendingDeletions_ResumesInterruptedAndFailedPlans(bool failed)
    {
        using var root = new TemporaryAppDataRoot();
        var (_, entry) = await ImportAndRecycleAsync(root);
        var store = new SqliteLibraryStore(root.Paths);
        var plan = (await store.PrepareDeletionAsync(entry.Item.Id, DateTimeOffset.UtcNow, CancellationToken.None))!;
        var storage = new ManagedImageStorage(root.Paths);
        if (failed)
        {
            await storage.DeleteManagedAsync(entry.Asset.OriginalRelativePath);
            await store.FailDeletionAsync(plan.JobId, "DeletionDatabaseFailed", DateTimeOffset.UtcNow, CancellationToken.None);
        }

        var restarted = new LibraryService(root.Paths, storage);
        await restarted.ReconcilePendingDeletionsAsync();
        await restarted.ReconcilePendingDeletionsAsync();

        Assert.Null(await restarted.GetAsync(entry.Item.Id));
        Assert.False(File.Exists(root.Paths.Resolve(entry.Asset.OriginalRelativePath)));
        Assert.False(File.Exists(root.Paths.Resolve(entry.Asset.ThumbnailRelativePath!)));
        Assert.Equal(1L, await ScalarAsync(root.Paths.DatabasePath, "SELECT COUNT(*) FROM DeletionJobs;"));
        Assert.Equal(1L, await ScalarAsync(root.Paths.DatabasePath, "SELECT COUNT(*) FROM DeletionJobs WHERE State = 2;"));
    }

    [Fact]
    public async Task ReconcilePendingDeletions_PreservesAnActiveImageWithALegacyPlan()
    {
        using var root = new TemporaryAppDataRoot();
        var (library, entry) = await ImportAndRecycleAsync(root);
        var store = new SqliteLibraryStore(root.Paths);
        await store.PrepareDeletionAsync(entry.Item.Id, DateTimeOffset.UtcNow, CancellationToken.None);
        await ExecuteAsync(root.Paths.DatabasePath, "UPDATE ImageItems SET DeletedAtUtc = NULL;");

        await library.ReconcilePendingDeletionsAsync();

        Assert.Null((await library.GetAsync(entry.Item.Id))!.Item.DeletedAtUtc);
        Assert.True(File.Exists(root.Paths.Resolve(entry.Asset.OriginalRelativePath)));
        Assert.Equal(0L, await ScalarAsync(root.Paths.DatabasePath, "SELECT COUNT(*) FROM DeletionJobs;"));
        await library.SoftDeleteAsync(entry.Item.Id);
        await library.RestoreAsync(entry.Item.Id);
        Assert.Null((await library.GetAsync(entry.Item.Id))!.Item.DeletedAtUtc);
    }

    [Fact]
    public async Task Restore_RejectsMissingOriginalAndKeepsTheItemRecycled()
    {
        using var root = new TemporaryAppDataRoot();
        var (library, entry) = await ImportAndRecycleAsync(root);
        await new ManagedImageStorage(root.Paths).DeleteManagedAsync(entry.Asset.OriginalRelativePath);

        await Assert.ThrowsAsync<InvalidOperationException>(() => library.RestoreAsync(entry.Item.Id));

        Assert.NotNull((await library.GetAsync(entry.Item.Id))!.Item.DeletedAtUtc);
    }

    [Fact]
    public async Task ReconcilePendingDeletions_ContinuesWhenAnOriginalIsLocked()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = new TemporaryAppDataRoot();
        var (library, first) = await ImportAndRecycleAsync(root, 1);
        var (_, second) = await ImportAndRecycleAsync(root, 2);
        var store = new SqliteLibraryStore(root.Paths);
        await store.PrepareDeletionAsync(first.Item.Id, DateTimeOffset.UtcNow, CancellationToken.None);
        await store.PrepareDeletionAsync(second.Item.Id, DateTimeOffset.UtcNow, CancellationToken.None);
        using var locked = new FileStream(root.Paths.Resolve(first.Asset.OriginalRelativePath),
            FileMode.Open, FileAccess.Read, FileShare.Read);

        await library.ReconcilePendingDeletionsAsync();

        Assert.NotNull((await library.GetAsync(first.Item.Id))!.Item.DeletedAtUtc);
        Assert.Null(await library.GetAsync(second.Item.Id));
        Assert.Equal(1L, await ScalarAsync(root.Paths.DatabasePath, "SELECT COUNT(*) FROM DeletionJobs WHERE State = 3;"));
        Assert.Equal(1L, await ScalarAsync(root.Paths.DatabasePath, "SELECT COUNT(*) FROM DeletionJobs WHERE State = 2;"));
    }

    private static async Task<(LibraryService Library, LibraryEntry Entry)> ImportAndRecycleAsync(
        TemporaryAppDataRoot root, byte suffix = 0)
    {
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var storage = new ManagedImageStorage(root.Paths);
        using var importer = new ImageImportService(root.Paths, storage, new FakeImageProcessor());
        var library = new LibraryService(root.Paths, storage);
        var imported = await importer.ImportAsync(new MemoryStream(TinyPng.Concat([suffix]).ToArray(), writable: false),
            "audit.png", ImageSourceKind.File, ManagedImageFormat.Png);
        await library.SoftDeleteAsync(imported.ImageItemId);
        return (library, (await library.GetAsync(imported.ImageItemId))!);
    }

    private static async Task ExecuteAsync(string databasePath, string sql)
    {
        await using var connection = await TemporaryAppDataRoot.OpenAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(string databasePath, string sql)
    {
        await using var connection = await TemporaryAppDataRoot.OpenAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private sealed class FakeImageProcessor : IImageContentProcessor
    {
        public Task<ImageInspection> InspectAndCreateThumbnailAsync(Stream source,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ImageInspection(ManagedImageFormat.Png, "image/png", 1, 1, TinyPng));
    }
}
