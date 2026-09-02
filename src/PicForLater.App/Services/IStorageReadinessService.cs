using PicForLater.App.Models;

namespace PicForLater.App.Services;

public interface IStorageReadinessService
{
    event EventHandler<StorageReadinessChangedEventArgs>? ReadinessChanged;

    Task<StorageReadinessResult> EnsureReadyAsync(bool forceRetry = false);
}

public sealed class StorageReadinessChangedEventArgs : EventArgs
{
    public StorageReadinessChangedEventArgs(StorageReadinessResult result)
    {
        Result = result ?? throw new ArgumentNullException(nameof(result));
    }

    public StorageReadinessResult Result { get; }
}
