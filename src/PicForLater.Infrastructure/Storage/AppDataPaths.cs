using PicForLater.Core.Images;

namespace PicForLater.Infrastructure.Storage;

/// <summary>
/// Defines the app-private on-disk layout. The caller supplies the unpackaged app's
/// stable per-user root in production and an isolated temporary root in tests.
/// </summary>
public sealed class AppDataPaths
{
    public AppDataPaths(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (!Path.IsPathFullyQualified(rootPath))
        {
            throw new ArgumentException("The application data root must be an absolute path.", nameof(rootPath));
        }

        RootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        SettingsFilePath = Path.Combine(RootPath, "settings.json");
        DatabaseDirectoryPath = Path.Combine(RootPath, "data");
        DatabasePath = Path.Combine(DatabaseDirectoryPath, "picforlater.db");
        BackupDirectoryPath = Path.Combine(DatabaseDirectoryPath, "backups");
        StagingDirectoryPath = Path.Combine(RootPath, "staging");
        ModelDownloadStagingDirectoryPath = Path.Combine(StagingDirectoryPath, "model-downloads");
        OriginalDirectoryPath = Path.Combine(RootPath, "assets", "originals");
        ThumbnailDirectoryPath = Path.Combine(RootPath, "cache", "thumbnails");
        AnalysisCacheDirectoryPath = Path.Combine(RootPath, "cache", "analysis");
        ModelDownloadRecoveryDirectoryPath = Path.Combine(RootPath, "cache", "model-download-recovery");
        ModelPackagesDirectoryPath = Path.Combine(RootPath, "model-packages");
        ModelRuntimesDirectoryPath = Path.Combine(RootPath, "model-runtimes");
        ModelRuntimeStagingDirectoryPath = Path.Combine(StagingDirectoryPath, "model-runtimes");
        ModelRuntimeDownloadRecoveryDirectoryPath = Path.Combine(
            RootPath,
            "cache",
            "model-runtime-download-recovery");
        ComponentsDirectoryPath = Path.Combine(RootPath, "components");
        LocalInferenceComponentsDirectoryPath = Path.Combine(
            ComponentsDirectoryPath,
            "local-inference");
        LocalInferenceComponentStagingDirectoryPath = Path.Combine(
            StagingDirectoryPath,
            "local-inference-components");
    }

    public string RootPath { get; }

    public string SettingsFilePath { get; }

    public string DatabaseDirectoryPath { get; }

    public string DatabasePath { get; }

    public string BackupDirectoryPath { get; }

    public string StagingDirectoryPath { get; }

    public string ModelDownloadStagingDirectoryPath { get; }

    public string OriginalDirectoryPath { get; }

    public string ThumbnailDirectoryPath { get; }

    public string AnalysisCacheDirectoryPath { get; }

    public string ModelDownloadRecoveryDirectoryPath { get; }

    public string ModelPackagesDirectoryPath { get; }

    public string ModelRuntimesDirectoryPath { get; }

    public string ModelRuntimeStagingDirectoryPath { get; }

    public string ModelRuntimeDownloadRecoveryDirectoryPath { get; }

    public string ComponentsDirectoryPath { get; }

    public string LocalInferenceComponentsDirectoryPath { get; }

    public string LocalInferenceComponentStagingDirectoryPath { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(RootPath);
        EnsureSafePath(RootPath);
        EnsureManagedDirectory(DatabaseDirectoryPath);
        EnsureManagedDirectory(BackupDirectoryPath);
        EnsureManagedDirectory(StagingDirectoryPath);
        EnsureManagedDirectory(ModelDownloadStagingDirectoryPath);
        EnsureManagedDirectory(OriginalDirectoryPath);
        EnsureManagedDirectory(ThumbnailDirectoryPath);
        EnsureManagedDirectory(AnalysisCacheDirectoryPath);
        EnsureManagedDirectory(ModelDownloadRecoveryDirectoryPath);
        EnsureManagedDirectory(ModelPackagesDirectoryPath);
        EnsureManagedDirectory(ModelRuntimesDirectoryPath);
        EnsureManagedDirectory(ModelRuntimeStagingDirectoryPath);
        EnsureManagedDirectory(ModelRuntimeDownloadRecoveryDirectoryPath);
        EnsureManagedDirectory(ComponentsDirectoryPath);
        EnsureManagedDirectory(LocalInferenceComponentsDirectoryPath);
        EnsureManagedDirectory(LocalInferenceComponentStagingDirectoryPath);
    }

    public string Resolve(ManagedRelativePath relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        var platformRelativePath = relativePath.Value.Replace('/', Path.DirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(RootPath, platformRelativePath));
        EnsureSafePath(candidate);
        return candidate;
    }

    public void EnsureSafePath(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);

        var candidate = Path.GetFullPath(absolutePath);
        var rootPrefix = RootPath + Path.DirectorySeparatorChar;
        if (!candidate.Equals(RootPath, StringComparison.OrdinalIgnoreCase)
            && !candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The managed path resolves outside the application data root.");
        }

        EnsureExistingSegmentsAreNotReparsePoints(candidate);
    }

    private void EnsureManagedDirectory(string path)
    {
        EnsureSafePath(path);
        Directory.CreateDirectory(path);
        EnsureSafePath(path);
    }

    private void EnsureExistingSegmentsAreNotReparsePoints(string candidate)
    {
        ThrowIfReparsePoint(RootPath);
        if (candidate.Equals(RootPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var relativePath = Path.GetRelativePath(RootPath, candidate);
        var currentPath = RootPath;
        foreach (var segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, segment);
            if (!TryGetAttributes(currentPath, out var attributes))
            {
                break;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    "A managed path contains a reparse point and cannot be used safely.");
            }
        }
    }

    private static void ThrowIfReparsePoint(string path)
    {
        if (TryGetAttributes(path, out var attributes)
            && (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "The application data root cannot be a reparse point.");
        }
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }
}
