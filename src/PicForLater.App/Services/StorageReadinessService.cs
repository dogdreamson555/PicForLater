using PicForLater.App.Models;
using PicForLater.Infrastructure.Storage;

namespace PicForLater.App.Services;

/// <summary>
/// Serializes startup/retry attempts and converts storage exceptions into stable,
/// non-sensitive categories suitable for presentation and diagnostics.
/// </summary>
public sealed class StorageReadinessService : IStorageReadinessService
{
    private readonly Func<Task<DatabaseInitializationResult>> _initializationFactory;
    private readonly object _syncRoot = new();
    private Task<DatabaseInitializationResult> _initializationTask;

    public StorageReadinessService(Func<Task<DatabaseInitializationResult>> initializationFactory)
    {
        _initializationFactory = initializationFactory
            ?? throw new ArgumentNullException(nameof(initializationFactory));
        _initializationTask = _initializationFactory();
    }

    public async Task<StorageReadinessResult> EnsureReadyAsync(bool forceRetry = false)
    {
        Task<DatabaseInitializationResult> task;
        lock (_syncRoot)
        {
            if (forceRetry && _initializationTask.IsCompleted)
            {
                _initializationTask = _initializationFactory();
            }

            task = _initializationTask;
        }

        try
        {
            await task.ConfigureAwait(false);
            return new StorageReadinessResult(StorageReadinessStatus.Ready);
        }
        catch (DatabaseSchemaException)
        {
            return new StorageReadinessResult(
                StorageReadinessStatus.Unsupported,
                "DatabaseSchemaIncompatible");
        }
        catch (Exception exception) when (ContainsUnauthorizedAccess(exception))
        {
            return new StorageReadinessResult(
                StorageReadinessStatus.PermissionDenied,
                "StorageAccessDenied");
        }
        catch (DatabaseMigrationException)
        {
            return new StorageReadinessResult(
                StorageReadinessStatus.Error,
                "DatabaseMigrationFailed");
        }
        catch (DatabaseInitializationException)
        {
            return new StorageReadinessResult(
                StorageReadinessStatus.Error,
                "DatabaseInitializationFailed");
        }
        catch (IOException)
        {
            return new StorageReadinessResult(
                StorageReadinessStatus.Error,
                "StorageIoFailed");
        }
        catch (Exception)
        {
            return new StorageReadinessResult(
                StorageReadinessStatus.Error,
                "StorageInitializationFailed");
        }
    }

    private static bool ContainsUnauthorizedAccess(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is UnauthorizedAccessException)
            {
                return true;
            }
        }

        return false;
    }
}
