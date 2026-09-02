using PicForLater.App.Models;
using PicForLater.App.Services;
using PicForLater.Infrastructure.Storage;

namespace PicForLater.IntegrationTests;

public sealed class StorageReadinessServiceTests
{
    [Fact]
    public async Task ForceRetry_DoesNotRepeatSuccessfulInitialization()
    {
        var attempts = 0;
        var service = new StorageReadinessService(() =>
        {
            attempts++;
            return Task.FromResult(new DatabaseInitializationResult(14, 14, null));
        });

        var first = await service.EnsureReadyAsync();
        var retry = await service.EnsureReadyAsync(forceRetry: true);

        Assert.Equal(StorageReadinessStatus.Ready, first.Status);
        Assert.Equal(StorageReadinessStatus.Ready, retry.Status);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ForceRetry_RepeatsFailedInitialization()
    {
        var attempts = 0;
        var service = new StorageReadinessService(() =>
        {
            attempts++;
            return attempts == 1
                ? Task.FromException<DatabaseInitializationResult>(new IOException("fixture"))
                : Task.FromResult(new DatabaseInitializationResult(14, 14, null));
        });

        var first = await service.EnsureReadyAsync();
        var retry = await service.EnsureReadyAsync(forceRetry: true);

        Assert.Equal(StorageReadinessStatus.Error, first.Status);
        Assert.Equal("StorageIoFailed", first.ErrorCode);
        Assert.Equal(StorageReadinessStatus.Ready, retry.Status);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task ReadinessChanged_PublishesFailureThenSuccessfulRetry()
    {
        var attempts = 0;
        var service = new StorageReadinessService(() =>
        {
            attempts++;
            return attempts == 1
                ? Task.FromException<DatabaseInitializationResult>(new IOException("fixture"))
                : Task.FromResult(new DatabaseInitializationResult(14, 14, null));
        });
        var observed = new List<StorageReadinessResult>();
        service.ReadinessChanged += (_, args) => observed.Add(args.Result);

        await service.EnsureReadyAsync();
        await service.EnsureReadyAsync(forceRetry: true);

        Assert.Equal(
            [StorageReadinessStatus.Error, StorageReadinessStatus.Ready],
            observed.Select(static result => result.Status));
    }

    [Fact]
    public async Task ReadinessChanged_ObserverFailureDoesNotChangeStorageResult()
    {
        var service = new StorageReadinessService(() =>
            Task.FromResult(new DatabaseInitializationResult(14, 14, null)));
        service.ReadinessChanged += (_, _) => throw new InvalidOperationException("fixture");

        StorageReadinessResult result = await service.EnsureReadyAsync();

        Assert.Equal(StorageReadinessStatus.Ready, result.Status);
    }
}
