using PicForLater.App.Models;

namespace PicForLater.App.Services;

public interface IStorageReadinessService
{
    Task<StorageReadinessResult> EnsureReadyAsync(bool forceRetry = false);
}
