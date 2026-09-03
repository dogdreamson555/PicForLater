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

    public event EventHandler<StorageReadinessChangedEventArgs>? ReadinessChanged;

    public async Task<StorageReadinessResult> EnsureReadyAsync(bool forceRetry = false)
    {
        Task<DatabaseInitializationResult> task;
        lock (_syncRoot)
        {
            if (forceRetry
                && _initializationTask.IsCompleted
                && !_initializationTask.IsCompletedSuccessfully)
            {
                _initializationTask = _initializationFactory();
            }

            task = _initializationTask;
        }

        StorageReadinessResult result;
        try
        {
            await task.ConfigureAwait(false);
            result = new StorageReadinessResult(StorageReadinessStatus.Ready);
        }
        catch (DatabaseSchemaException)
        {
            result = new StorageReadinessResult(
                StorageReadinessStatus.Unsupported,
                "DatabaseSchemaIncompatible");
        }
        catch (Exception exception) when (ContainsUnauthorizedAccess(exception))
        {
            result = new StorageReadinessResult(
                StorageReadinessStatus.PermissionDenied,
                "StorageAccessDenied");
        }
        catch (DatabaseMigrationException)
        {
            result = new StorageReadinessResult(
                StorageReadinessStatus.Error,
                "DatabaseMigrationFailed");
        }
        catch (DatabaseInitializationException)
        {
            result = new StorageReadinessResult(
                StorageReadinessStatus.Error,
                "DatabaseInitializationFailed");
        }
        catch (IOException)
        {
            result = new StorageReadinessResult(
                StorageReadinessStatus.Error,
                "StorageIoFailed");
        }
        catch (Exception)
        {
            result = new StorageReadinessResult(
                StorageReadinessStatus.Error,
                "StorageInitializationFailed");
        }

        var eventArgs = new StorageReadinessChangedEventArgs(result);
        Delegate[] handlers = ReadinessChanged?.GetInvocationList() ?? [];
        foreach (EventHandler<StorageReadinessChangedEventArgs> handler in handlers.Cast<
                     EventHandler<StorageReadinessChangedEventArgs>>())
        {
            try
            {
                handler(this, eventArgs);
            }
            catch
            {
                // Readiness is a storage fact. A page or optional feature that is
                // navigating away cannot change the result observed by callers.
            }
        }

        return result;
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
