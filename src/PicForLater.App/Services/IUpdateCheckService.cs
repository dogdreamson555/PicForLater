using PicForLater.App.Models;

namespace PicForLater.App.Services;

public interface IUpdateCheckService
{
    Task<UpdateCheckResult> CheckForUpdatesAsync(
        CancellationToken cancellationToken = default);
}
