using PicForLater.Core.Analysis;

namespace PicForLater.Infrastructure.Analysis;

// This optimized path is intentionally internal: only the recommended-model downloader can
// establish that the source manifest is trusted and the source lives in managed staging.
internal interface IRecommendedModelPackageInstaller
{
    Task<ModelPackageImportResult> InstallAndSwitchRecommendedAsync(
        string packageDirectoryPath,
        ModelPackageManifest expectedManifest,
        IReadOnlyCollection<ModelCapability> capabilities,
        Action? onReadyToEnable,
        CancellationToken cancellationToken = default);
}
