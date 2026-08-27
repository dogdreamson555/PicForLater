using PicForLater.Infrastructure.Storage;

namespace PicForLater.LocalInference;

internal sealed class WorkerAnalysisTemporaryDirectory : IAsyncDisposable
{
    private const string InstancePrefix = "worker-";
    private const string OwnerFileName = ".owner.lock";
    private const string CoordinationFileName = ".cleanup.lock";
    private const int CoordinationLockAttempts = 40;
    private static readonly TimeSpan CoordinationLockRetryDelay = TimeSpan.FromMilliseconds(50);
    private readonly AppDataPaths _paths;
    private FileStream? _ownerLock;
    private bool _disposed;

    private WorkerAnalysisTemporaryDirectory(
        AppDataPaths paths,
        string directoryPath,
        FileStream ownerLock)
    {
        _paths = paths;
        DirectoryPath = directoryPath;
        _ownerLock = ownerLock;
    }

    public string DirectoryPath { get; }

    public static async Task<WorkerAnalysisTemporaryDirectory> CreateAsync(
        AppDataPaths paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var root = paths.AnalysisWorkerCacheDirectoryPath;
        paths.EnsureSafePath(root);
        Directory.CreateDirectory(root);
        paths.EnsureSafePath(root);

        using var coordinationLock = await AcquireCoordinationLockAsync(
                paths,
                cancellationToken)
            .ConfigureAwait(false);
        CleanupExpiredDirectories(paths);

        var directoryPath = Path.Combine(root, $"{InstancePrefix}{Guid.NewGuid():N}");
        paths.EnsureSafePath(directoryPath);
        Directory.CreateDirectory(directoryPath);
        paths.EnsureSafePath(directoryPath);
        var ownerPath = Path.Combine(directoryPath, OwnerFileName);
        FileStream? ownerLock = null;
        try
        {
            paths.EnsureSafePath(ownerPath);
            ownerLock = new FileStream(
                ownerPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
            paths.EnsureSafePath(ownerPath);
            return new WorkerAnalysisTemporaryDirectory(paths, directoryPath, ownerLock);
        }
        catch
        {
            ownerLock?.Dispose();
            TryDeleteIncompleteInstance(paths, directoryPath);
            throw;
        }
    }

    internal static void DeleteExpiredDirectory(AppDataPaths paths, string directoryPath)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        var root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(paths.AnalysisWorkerCacheDirectoryPath));
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));
        paths.EnsureSafePath(candidate);
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !IsManagedInstanceName(Path.GetFileName(candidate)))
        {
            throw new InvalidOperationException(
                "Only a managed local-inference worker directory can be deleted.");
        }

        if (!Directory.Exists(candidate))
        {
            return;
        }

        var entries = Directory.EnumerateFileSystemEntries(
                candidate,
                "*",
                SearchOption.TopDirectoryOnly)
            .ToArray();
        foreach (var entry in entries)
        {
            paths.EnsureSafePath(entry);
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    "A worker analysis temporary directory contains a reparse point.");
            }

            if ((attributes & FileAttributes.Directory) != 0
                || !IsManagedInstanceFile(Path.GetFileName(entry)))
            {
                throw new InvalidOperationException(
                    "A worker analysis temporary directory contains an unexpected entry.");
            }
        }

        foreach (var entry in entries)
        {
            File.Delete(entry);
        }

        Directory.Delete(candidate, recursive: false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            using var coordinationLock = await AcquireCoordinationLockAsync(
                    _paths,
                    CancellationToken.None)
                .ConfigureAwait(false);
            _ownerLock?.Dispose();
            _ownerLock = null;
            DeleteExpiredDirectory(_paths, DirectoryPath);
        }
        finally
        {
            _ownerLock?.Dispose();
            _ownerLock = null;
        }
    }

    private static void CleanupExpiredDirectories(AppDataPaths paths)
    {
        foreach (var directoryPath in Directory.EnumerateDirectories(
                     paths.AnalysisWorkerCacheDirectoryPath,
                     $"{InstancePrefix}*",
                     SearchOption.TopDirectoryOnly))
        {
            paths.EnsureSafePath(directoryPath);
            if (!IsManagedInstanceName(Path.GetFileName(directoryPath))
                || IsOwnedByActiveWorker(paths, directoryPath))
            {
                continue;
            }

            DeleteExpiredDirectory(paths, directoryPath);
        }
    }

    private static bool IsOwnedByActiveWorker(AppDataPaths paths, string directoryPath)
    {
        var ownerPath = Path.Combine(directoryPath, OwnerFileName);
        paths.EnsureSafePath(ownerPath);
        try
        {
            using var ownerProbe = new FileStream(
                ownerPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
            paths.EnsureSafePath(ownerPath);
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (IOException exception) when (IsSharingViolation(exception))
        {
            return true;
        }
    }

    private static async Task<FileStream> AcquireCoordinationLockAsync(
        AppDataPaths paths,
        CancellationToken cancellationToken)
    {
        var lockPath = Path.Combine(
            paths.AnalysisWorkerCacheDirectoryPath,
            CoordinationFileName);
        paths.EnsureSafePath(lockPath);
        for (var attempt = 0; attempt < CoordinationLockAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None);
                try
                {
                    paths.EnsureSafePath(lockPath);
                    return stream;
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }
            }
            catch (IOException exception) when (
                IsSharingViolation(exception) && attempt + 1 < CoordinationLockAttempts)
            {
                await Task.Delay(CoordinationLockRetryDelay, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        throw new IOException("The worker analysis cleanup lock is unavailable.");
    }

    private static bool IsManagedInstanceName(string name) =>
        name.Length == InstancePrefix.Length + 32
        && name.StartsWith(InstancePrefix, StringComparison.Ordinal)
        && Guid.TryParseExact(name[InstancePrefix.Length..], "N", out var id)
        && name.Equals($"{InstancePrefix}{id:N}", StringComparison.Ordinal);

    private static bool IsManagedInstanceFile(string name) =>
        name.Equals(OwnerFileName, StringComparison.Ordinal)
        || (name.Length == 36
            && name.EndsWith(".png", StringComparison.Ordinal)
            && Guid.TryParseExact(name[..32], "N", out var id)
            && name.Equals($"{id:N}.png", StringComparison.Ordinal));

    private static bool IsSharingViolation(IOException exception)
    {
        var errorCode = exception.HResult & 0xFFFF;
        return errorCode is 32 or 33;
    }

    private static void TryDeleteIncompleteInstance(AppDataPaths paths, string directoryPath)
    {
        try
        {
            DeleteExpiredDirectory(paths, directoryPath);
        }
        catch
        {
            // The startup failure remains authoritative. A later worker startup
            // can retry this same narrowly scoped cleanup.
        }
    }
}
