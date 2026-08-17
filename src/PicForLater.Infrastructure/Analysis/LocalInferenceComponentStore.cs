using PicForLater.Infrastructure.Storage;

namespace PicForLater.Infrastructure.Analysis;

public sealed class LocalInferenceComponentStore
{
    private readonly AppDataPaths _paths;
    private readonly LocalInferenceComponentLocator _locator;
    private readonly string _architecture;
    private readonly Func<CancellationToken, ValueTask<IAsyncDisposable>>? _acquireMaintenanceLease;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public LocalInferenceComponentStore(
        AppDataPaths paths,
        LocalInferenceComponentLocator locator,
        string architecture,
        Func<CancellationToken, ValueTask<IAsyncDisposable>>? acquireMaintenanceLease = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        ArgumentException.ThrowIfNullOrWhiteSpace(architecture);
        if (!LocalInferenceComponentLocator.IsSafeName(architecture))
        {
            throw new ArgumentException("The component architecture is invalid.", nameof(architecture));
        }

        _architecture = architecture;
        _acquireMaintenanceLease = acquireMaintenanceLease;
    }

    public Task<LocalInferenceComponent?> GetActiveAsync(
        CancellationToken cancellationToken = default) =>
        _locator.LocateAsync(cancellationToken);

    public async Task<bool> RemoveAllAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IAsyncDisposable? maintenanceLease = null;
        try
        {
            if (_acquireMaintenanceLease is not null)
            {
                maintenanceLease = await _acquireMaintenanceLease(cancellationToken)
                    .ConfigureAwait(false);
            }

            var architectureRoot = Path.Combine(
                _paths.LocalInferenceComponentsDirectoryPath,
                _architecture);
            _paths.EnsureSafePath(architectureRoot);
            if (!Directory.Exists(architectureRoot))
            {
                _locator.Invalidate();
                return false;
            }

            ValidateTreeForRemoval(architectureRoot);
            cancellationToken.ThrowIfCancellationRequested();
            var tombstoneRoot = Path.Combine(
                _paths.LocalInferenceComponentsDirectoryPath,
                $"{_architecture}.removing-{Guid.NewGuid():N}");
            _paths.EnsureSafePath(tombstoneRoot);
            Directory.Move(architectureRoot, tombstoneRoot);
            _locator.Invalidate();
            try
            {
                Directory.Delete(tombstoneRoot, recursive: true);
                return true;
            }
            catch
            {
                if (Directory.Exists(tombstoneRoot) && !Directory.Exists(architectureRoot))
                {
                    Directory.Move(tombstoneRoot, architectureRoot);
                }

                _locator.Invalidate();
                throw;
            }
        }
        finally
        {
            if (maintenanceLease is not null)
            {
                await maintenanceLease.DisposeAsync().ConfigureAwait(false);
            }

            _operationGate.Release();
        }
    }

    private void ValidateTreeForRemoval(string architectureRoot)
    {
        _paths.EnsureSafePath(architectureRoot);
        if ((File.GetAttributes(architectureRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "The local inference component root cannot be a reparse point.");
        }

        var directories = new Stack<string>();
        directories.Push(architectureRoot);
        while (directories.Count > 0)
        {
            var directoryPath = directories.Pop();
            foreach (var path in Directory.EnumerateFileSystemEntries(
                         directoryPath,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                _paths.EnsureSafePath(path);
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        "A local inference component path cannot be a reparse point.");
                }

                if (Directory.Exists(path))
                {
                    directories.Push(path);
                }
            }
        }
    }
}
