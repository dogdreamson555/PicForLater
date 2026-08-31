using PicForLater.App.Models;

namespace PicForLater.App.Services;

#if PICFORLATER_UI_TESTING
internal sealed class UiTestUpdateCheckService(AppReleaseVersion currentVersion)
    : IUpdateCheckService
{
    private int _completedChecks;

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(1500), cancellationToken);
        var outcomeIndex = _completedChecks++ % 4;
        return outcomeIndex switch
        {
            0 => new UpdateCheckResult(
                currentVersion,
                new AppReleaseVersion(
                    currentVersion.Major,
                    currentVersion.Minor,
                    currentVersion.Patch + 1),
                UpdateCheckOutcome.UpdateAvailable,
                ReleasePageUri: new Uri(
                    $"https://github.com/dogdreamson555/PicForLater/releases/tag/v{currentVersion.Major}.{currentVersion.Minor}.{currentVersion.Patch + 1}")),
            1 => new UpdateCheckResult(
                currentVersion,
                currentVersion,
                UpdateCheckOutcome.UpToDate),
            2 => new UpdateCheckResult(
                currentVersion,
                new AppReleaseVersion(0, 0, 0),
                UpdateCheckOutcome.LocalAhead),
            _ => new UpdateCheckResult(
                currentVersion,
                LatestVersion: null,
                UpdateCheckOutcome.Unavailable,
                UpdateCheckFailureKind.Network),
        };
    }
}
#endif
