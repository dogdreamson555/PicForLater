using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PicForLater.Infrastructure.Storage;

namespace PicForLater.Infrastructure.Analysis;

public sealed class LocalInferenceComponentLocator
{
    public const string ComponentId = "PicForLater.LocalInference";
    public const string ActiveManifestFileName = "active.json";
    public const string ComponentManifestFileName = "component.json";
    public const string WorkerFileName = "PicForLater.LocalInference.exe";

    private const int ManifestSchemaVersion = 1;
    private const int MaximumComponentFileCount = 512;
    private const long MaximumComponentLength = 2L * 1024 * 1024 * 1024;
    private const long MaximumActiveManifestLength = 16 * 1024;
    private const long MaximumComponentManifestLength = 1024 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        MaxDepth = 16,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly AppDataPaths _paths;
    private readonly string _architecture;
    private readonly int _minimumProtocolVersion;
    private readonly int _maximumProtocolVersion;
    private readonly SemaphoreSlim _validationGate = new(1, 1);
    private volatile LocalInferenceComponent? _validatedComponent;

    public LocalInferenceComponentLocator(
        AppDataPaths paths,
        string architecture,
        int minimumProtocolVersion,
        int maximumProtocolVersion)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        ArgumentException.ThrowIfNullOrWhiteSpace(architecture);
        if (!IsSafeName(architecture))
        {
            throw new ArgumentException("The component architecture is invalid.", nameof(architecture));
        }
        if (minimumProtocolVersion <= 0 || maximumProtocolVersion < minimumProtocolVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumProtocolVersion));
        }

        _architecture = architecture;
        _minimumProtocolVersion = minimumProtocolVersion;
        _maximumProtocolVersion = maximumProtocolVersion;
    }

    public async Task<LocalInferenceComponent?> LocateAsync(
        CancellationToken cancellationToken = default)
    {
        if (_validatedComponent is not null)
        {
            return _validatedComponent;
        }

        await _validationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_validatedComponent is not null)
            {
                return _validatedComponent;
            }

            _validatedComponent = await ValidateActiveComponentAsync(cancellationToken)
                .ConfigureAwait(false);
            return _validatedComponent;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or JsonException
                                          or CryptographicException
                                          or InvalidDataException
                                          or InvalidOperationException
                                          or NotSupportedException)
        {
            return null;
        }
        finally
        {
            _validationGate.Release();
        }
    }

    private async Task<LocalInferenceComponent?> ValidateActiveComponentAsync(
        CancellationToken cancellationToken)
    {
        var architectureRoot = Path.Combine(
            _paths.LocalInferenceComponentsDirectoryPath,
            _architecture);
        var activeManifestPath = Path.Combine(architectureRoot, ActiveManifestFileName);
        if (!File.Exists(activeManifestPath))
        {
            return null;
        }

        _paths.EnsureSafePath(activeManifestPath);
        var activeManifest = await ReadManifestAsync<ActiveComponentManifest>(
                activeManifestPath,
                MaximumActiveManifestLength,
                cancellationToken)
            .ConfigureAwait(false);
        if (activeManifest.SchemaVersion != ManifestSchemaVersion
            || !IsSafeName(activeManifest.Version))
        {
            throw new InvalidDataException("The active local inference manifest is invalid.");
        }

        var componentRoot = Path.Combine(architectureRoot, activeManifest.Version);
        return await ValidateComponentDirectoryAsync(
                componentRoot,
                activeManifest.Version,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task<LocalInferenceComponent> ValidateComponentDirectoryAsync(
        string componentRoot,
        string expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(componentRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedVersion);
        if (!Path.IsPathFullyQualified(componentRoot) || !IsSafeName(expectedVersion))
        {
            throw new InvalidDataException("The local inference component location is invalid.");
        }

        componentRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(componentRoot));
        _paths.EnsureSafePath(componentRoot);
        var componentManifestPath = Path.Combine(componentRoot, ComponentManifestFileName);
        _paths.EnsureSafePath(componentManifestPath);
        var manifest = await ReadManifestAsync<ComponentManifest>(
                componentManifestPath,
                MaximumComponentManifestLength,
                cancellationToken)
            .ConfigureAwait(false);
        ValidateManifest(manifest, expectedVersion);

        var validatedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!validatedFiles.Add(file.Path))
            {
                throw new InvalidDataException("The local inference manifest contains a duplicate path.");
            }

            var absolutePath = ResolveComponentFile(componentRoot, file.Path);
            if (!File.Exists(absolutePath))
            {
                throw new InvalidDataException("A local inference component file is missing.");
            }

            var fileInfo = new FileInfo(absolutePath);
            if (fileInfo.Length != file.Length)
            {
                throw new InvalidDataException("A local inference component file has the wrong length.");
            }

            await using var stream = new FileStream(
                absolutePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actualHash = await SHA256.HashDataAsync(stream, cancellationToken)
                .ConfigureAwait(false);
            if (!Convert.ToHexString(actualHash).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("A local inference component file failed validation.");
            }
        }

        if (!validatedFiles.Contains(WorkerFileName))
        {
            throw new InvalidDataException("The local inference worker is not listed in the manifest.");
        }

        ValidateNoUnlistedFiles(componentRoot, validatedFiles);

        var workerPath = ResolveComponentFile(componentRoot, WorkerFileName);
        return new LocalInferenceComponent(
            manifest.Version,
            componentRoot,
            workerPath,
            manifest.ProtocolMinimumVersion,
            manifest.ProtocolMaximumVersion);
    }

    public void Invalidate() => _validatedComponent = null;

    internal bool IsProtocolCompatible(int minimumVersion, int maximumVersion) =>
        minimumVersion > 0
        && maximumVersion >= minimumVersion
        && minimumVersion <= _maximumProtocolVersion
        && maximumVersion >= _minimumProtocolVersion;

    private void ValidateNoUnlistedFiles(
        string componentRoot,
        IReadOnlySet<string> validatedFiles)
    {
        var directories = new Stack<string>();
        directories.Push(componentRoot);
        while (directories.Count > 0)
        {
            var directoryPath = directories.Pop();
            _paths.EnsureSafePath(directoryPath);
            foreach (var path in Directory.EnumerateFileSystemEntries(
                         directoryPath,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                _paths.EnsureSafePath(path);
                if (Directory.Exists(path))
                {
                    directories.Push(path);
                    continue;
                }

                var relativePath = Path.GetRelativePath(componentRoot, path)
                    .Replace(Path.DirectorySeparatorChar, '/');
                if (!relativePath.Equals(
                        ComponentManifestFileName,
                        StringComparison.OrdinalIgnoreCase)
                    && !validatedFiles.Contains(relativePath))
                {
                    throw new InvalidDataException(
                        "The local inference component contains a file that is not listed in its manifest.");
                }
            }
        }
    }

    private void ValidateManifest(ComponentManifest manifest, string activeVersion)
    {
        if (manifest.SchemaVersion != ManifestSchemaVersion
            || !string.Equals(manifest.ComponentId, ComponentId, StringComparison.Ordinal)
            || !string.Equals(manifest.Version, activeVersion, StringComparison.Ordinal)
            || !string.Equals(manifest.Architecture, _architecture, StringComparison.Ordinal)
            || manifest.ProtocolMinimumVersion <= 0
            || manifest.ProtocolMaximumVersion < manifest.ProtocolMinimumVersion
            || manifest.ProtocolMinimumVersion > _maximumProtocolVersion
            || manifest.ProtocolMaximumVersion < _minimumProtocolVersion
            || manifest.Files is null
            || manifest.Files.Count is 0 or > MaximumComponentFileCount)
        {
            throw new InvalidDataException("The local inference component manifest is incompatible.");
        }

        var totalLength = 0L;
        foreach (var file in manifest.Files)
        {
            if (file is null
                || file.Length < 0
                || file.Path is null
                || file.Sha256 is null
                || file.Sha256.Length != 64
                || !file.Sha256.All(Uri.IsHexDigit)
                || !IsSafeRelativePath(file.Path))
            {
                throw new InvalidDataException("The local inference component file entry is invalid.");
            }

            if (file.Length > MaximumComponentLength - totalLength)
            {
                throw new InvalidDataException("The local inference component is too large.");
            }

            totalLength += file.Length;
        }
    }

    private string ResolveComponentFile(string componentRoot, string relativePath)
    {
        if (!IsSafeRelativePath(relativePath))
        {
            throw new InvalidDataException("The local inference component path is invalid.");
        }

        var candidate = Path.GetFullPath(Path.Combine(
            componentRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var componentPrefix = Path.TrimEndingDirectorySeparator(componentRoot)
                              + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(componentPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The local inference component path escapes its root.");
        }

        _paths.EnsureSafePath(candidate);
        return candidate;
    }

    private static async Task<T> ReadManifestAsync<T>(
        string path,
        long maximumLength,
        CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(path);
        if (fileInfo.Length <= 0 || fileInfo.Length > maximumLength)
        {
            throw new InvalidDataException("The local inference manifest has an invalid length.");
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<T>(stream, SerializerOptions, cancellationToken)
                   .ConfigureAwait(false)
               ?? throw new InvalidDataException("The local inference manifest is empty.");
    }

    internal static bool IsSafeName(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 64
        && value is not "." and not ".."
        && !value.EndsWith('.')
        && !IsReservedWindowsName(value)
        && value.All(character => char.IsAsciiLetterOrDigit(character)
                                  || character is '.' or '-' or '_');

    private static bool IsReservedWindowsName(string value)
    {
        var deviceName = value.Split('.', 2)[0];
        return deviceName.Equals("CON", StringComparison.OrdinalIgnoreCase)
               || deviceName.Equals("PRN", StringComparison.OrdinalIgnoreCase)
               || deviceName.Equals("AUX", StringComparison.OrdinalIgnoreCase)
               || deviceName.Equals("NUL", StringComparison.OrdinalIgnoreCase)
               || IsNumberedDevice(deviceName, "COM")
               || IsNumberedDevice(deviceName, "LPT");
    }

    private static bool IsNumberedDevice(string value, string prefix) =>
        value.Length == 4
        && value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        && value[3] is >= '1' and <= '9';

    internal static bool IsSafeRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 260
            || Path.IsPathFullyQualified(value)
            || value.Contains('\\'))
        {
            return false;
        }

        var segments = value.Split('/');
        return segments.Length > 0
               && segments.All(segment => IsSafeName(segment));
    }

    private sealed record ActiveComponentManifest(int SchemaVersion, string Version);

    private sealed record ComponentManifest(
        int SchemaVersion,
        string ComponentId,
        string Version,
        string Architecture,
        int ProtocolMinimumVersion,
        int ProtocolMaximumVersion,
        IReadOnlyList<ComponentFileManifest> Files);

    private sealed record ComponentFileManifest(string Path, long Length, string Sha256);
}

public sealed record LocalInferenceComponent(
    string Version,
    string DirectoryPath,
    string WorkerPath,
    int ProtocolMinimumVersion,
    int ProtocolMaximumVersion);
