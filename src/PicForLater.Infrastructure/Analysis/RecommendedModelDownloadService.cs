using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PicForLater.Core.Analysis;
using PicForLater.Infrastructure.Storage;

namespace PicForLater.Infrastructure.Analysis;

public sealed record RecommendedModelDownloadFile(
    string RelativePath,
    Uri DownloadUri,
    long ByteLength,
    string Sha256);

public sealed record RecommendedModelDownloadDefinition(
    RecommendedModelDescriptor Descriptor,
    IReadOnlyList<RecommendedModelDownloadFile> Files,
    ModelPackageManifest? ModelManifest);

public sealed class RecommendedModelDownloadService : IRecommendedModelService
{
    private const long DownloadDiskMarginBytes = 256L * 1024 * 1024;
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };
    private readonly AppDataPaths _paths;
    private readonly HttpClient _httpClient;
    private readonly IModelPackageService _modelPackages;
    private readonly ILocalOcrPackageInstaller _localOcrInstaller;
    private readonly IReadOnlyList<RecommendedModelDownloadDefinition> _catalog;
    private readonly TimeSpan _downloadInactivityTimeout;
    private readonly int _downloadRetryCount;
    private readonly TimeSpan _downloadRetryBaseDelay;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public RecommendedModelDownloadService(
        AppDataPaths paths,
        HttpClient httpClient,
        IModelPackageService modelPackages,
        ILocalOcrPackageInstaller localOcrInstaller,
        IReadOnlyList<RecommendedModelDownloadDefinition>? catalog = null,
        TimeSpan? downloadInactivityTimeout = null,
        int downloadRetryCount = 3,
        TimeSpan? downloadRetryBaseDelay = null,
        IReadOnlySet<string>? availableQwenExecutionProviders = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _modelPackages = modelPackages ?? throw new ArgumentNullException(nameof(modelPackages));
        _localOcrInstaller = localOcrInstaller ?? throw new ArgumentNullException(nameof(localOcrInstaller));
        var sourceCatalog = catalog ?? CreateProductionCatalog();
        _catalog = availableQwenExecutionProviders is null
            ? sourceCatalog
            : sourceCatalog.Where(definition =>
                definition.Descriptor.Kind != RecommendedModelPackageKind.Qwen3Vl2BInstruct
                || definition.ModelManifest is { } manifest
                && (manifest.SupportedExecutionProviders ?? ["CPU"])
                    .Any(availableQwenExecutionProviders.Contains))
                .ToArray();
        _downloadInactivityTimeout = downloadInactivityTimeout ?? TimeSpan.FromMinutes(2);
        if (_downloadInactivityTimeout <= TimeSpan.Zero
            || _downloadInactivityTimeout > TimeSpan.FromMinutes(30))
        {
            throw new ArgumentOutOfRangeException(nameof(downloadInactivityTimeout));
        }

        if (downloadRetryCount is < 1 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(downloadRetryCount));
        }

        _downloadRetryCount = downloadRetryCount;
        _downloadRetryBaseDelay = downloadRetryBaseDelay ?? TimeSpan.FromSeconds(2);
        if (_downloadRetryBaseDelay < TimeSpan.Zero
            || _downloadRetryBaseDelay > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(downloadRetryBaseDelay));
        }

        ValidateCatalog(_catalog);
    }

    public async Task<IReadOnlyList<RecommendedModelDescriptor>> GetCatalogAsync(
        CancellationToken cancellationToken = default)
    {
        var profile = await _modelPackages.GetCurrentSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var packages = (await _modelPackages.GetStateAsync(cancellationToken).ConfigureAwait(false))
            .Packages.ToDictionary(package => package.PackageKey, StringComparer.Ordinal);
        var ppOcrInstalled = await _localOcrInstaller.IsInstalledAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<RecommendedModelDescriptor>(_catalog.Count);
        foreach (var definition in _catalog)
        {
            var descriptor = definition.Descriptor;
            if (descriptor.Kind == RecommendedModelPackageKind.PpOcrV6Small)
            {
                result.Add(descriptor with
                {
                    IsInstalled = ppOcrInstalled,
                    IsEnabled = ppOcrInstalled,
                });
                continue;
            }

            var packageKey = GetPackageKey(definition);
            var installed = packages.ContainsKey(packageKey);
            var enabled = installed && descriptor.Capabilities.All(capability =>
                profile.GetSlot(capability).PackageKey == packageKey);
            result.Add(descriptor with
            {
                IsInstalled = installed,
                IsEnabled = enabled,
            });
        }

        return result;
    }

    public async Task<RecommendedModelInstallResult> DownloadInstallAndEnableAsync(
        string modelId,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        var definition = _catalog.SingleOrDefault(item => item.Descriptor.Id == modelId)
            ?? throw new ArgumentException("The recommended model ID is not in the pinned catalog.", nameof(modelId));
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? stagingDirectoryPath = null;
        var completedDownloads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var packageWasInstalled = false;
        try
        {
            progress?.Report(new ModelDownloadProgress(
                modelId,
                ModelDownloadStage.Preparing,
                0,
                definition.Descriptor.DownloadBytes,
                null));
            var current = (await GetCatalogAsync(cancellationToken).ConfigureAwait(false))
                .Single(item => item.Id == modelId);
            var downloadWasRequired = !current.IsInstalled;
            if (downloadWasRequired)
            {
                EnsureDiskSpace(definition);
                stagingDirectoryPath = CreateStagingDirectory();
                var downloadedBytes = await RestoreVerifiedDownloadsAsync(
                    definition,
                    stagingDirectoryPath,
                    completedDownloads,
                    progress,
                    cancellationToken).ConfigureAwait(false);
                foreach (var file in definition.Files)
                {
                    if (completedDownloads.Contains(file.RelativePath))
                    {
                        continue;
                    }

                    downloadedBytes = await DownloadFileAsync(
                        definition,
                        file,
                        stagingDirectoryPath,
                        downloadedBytes,
                        progress,
                        cancellationToken).ConfigureAwait(false);
                    completedDownloads.Add(file.RelativePath);
                }

                progress?.Report(new ModelDownloadProgress(
                    modelId,
                    ModelDownloadStage.Verifying,
                    downloadedBytes,
                    definition.Descriptor.DownloadBytes,
                    null));
                progress?.Report(new ModelDownloadProgress(
                    modelId,
                    ModelDownloadStage.Installing,
                    downloadedBytes,
                    definition.Descriptor.DownloadBytes,
                    null));
                if (definition.Descriptor.Kind == RecommendedModelPackageKind.PpOcrV6Small)
                {
                    await _localOcrInstaller.InstallAsync(
                        stagingDirectoryPath,
                        cancellationToken).ConfigureAwait(false);
                    packageWasInstalled = true;
                    stagingDirectoryPath = null;
                }
                else
                {
                    var manifest = definition.ModelManifest
                        ?? throw new InvalidDataException("The Qwen catalog entry has no package manifest.");
                    var manifestPath = Path.Combine(stagingDirectoryPath, "manifest.json");
                    await File.WriteAllTextAsync(
                        manifestPath,
                        JsonSerializer.Serialize(manifest, ManifestJsonOptions),
                        cancellationToken).ConfigureAwait(false);
                    await _modelPackages.ImportAsync(manifestPath, cancellationToken).ConfigureAwait(false);
                    packageWasInstalled = true;
                }
            }

            progress?.Report(new ModelDownloadProgress(
                modelId,
                ModelDownloadStage.Enabling,
                definition.Descriptor.DownloadBytes,
                definition.Descriptor.DownloadBytes,
                null));
            if (definition.Descriptor.Kind == RecommendedModelPackageKind.Qwen3Vl2BInstruct)
            {
                await _modelPackages.SwitchManyAsync(
                    definition.Descriptor.Capabilities,
                    GetPackageKey(definition),
                    cancellationToken).ConfigureAwait(false);
            }

            progress?.Report(new ModelDownloadProgress(
                modelId,
                ModelDownloadStage.Completed,
                definition.Descriptor.DownloadBytes,
                definition.Descriptor.DownloadBytes,
                null));
            var installed = (await GetCatalogAsync(cancellationToken).ConfigureAwait(false))
                .Single(item => item.Id == modelId);
            TryDeleteRecoveryDirectory(definition);
            return new RecommendedModelInstallResult(installed, downloadWasRequired);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RecommendedModelInstallException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new RecommendedModelInstallException(
                ClassifyInstallFailure(exception),
                exception);
        }
        finally
        {
            if (stagingDirectoryPath is not null)
            {
                if (!packageWasInstalled)
                {
                    TryPreserveCompletedDownloads(
                        definition,
                        stagingDirectoryPath,
                        completedDownloads);
                }

                TryDeleteStagingDirectory(stagingDirectoryPath);
            }

            _operationGate.Release();
        }
    }

    private async Task<long> DownloadFileAsync(
        RecommendedModelDownloadDefinition definition,
        RecommendedModelDownloadFile file,
        string stagingDirectoryPath,
        long alreadyDownloadedBytes,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        RecommendedModelInstallException? lastTransientError = null;
        for (var attempt = 1; attempt <= _downloadRetryCount; attempt++)
        {
            DeletePartialDownload(stagingDirectoryPath, file.RelativePath);
            using var transferCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            transferCancellation.CancelAfter(_downloadInactivityTimeout);
            try
            {
                return await DownloadFileCoreAsync(
                    definition,
                    file,
                    stagingDirectoryPath,
                    alreadyDownloadedBytes,
                    progress,
                    transferCancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                lastTransientError = new RecommendedModelInstallException(
                    "model.download-timeout",
                    exception);
            }
            catch (HttpRequestException exception)
            {
                lastTransientError = new RecommendedModelInstallException(
                    "model.download-network-failed",
                    exception);
            }
            catch (IOException exception)
            {
                lastTransientError = new RecommendedModelInstallException(
                    "model.download-network-failed",
                    exception);
            }

            if (attempt < _downloadRetryCount)
            {
                var delay = TimeSpan.FromTicks(checked(_downloadRetryBaseDelay.Ticks * attempt));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        throw lastTransientError
            ?? new RecommendedModelInstallException("model.download-network-failed");
    }

    private async Task<long> DownloadFileCoreAsync(
        RecommendedModelDownloadDefinition definition,
        RecommendedModelDownloadFile file,
        string stagingDirectoryPath,
        long alreadyDownloadedBytes,
        IProgress<ModelDownloadProgress>? progress,
        CancellationTokenSource transferCancellation)
    {
        var cancellationToken = transferCancellation.Token;
        EnsureAllowedDownloadUri(file.DownloadUri);
        using var request = new HttpRequestMessage(HttpMethod.Get, file.DownloadUri);
        request.Headers.UserAgent.ParseAdd("PicForLater/1.0 local-model-downloader");
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        EnsureAllowedDownloadUri(response.RequestMessage?.RequestUri ?? file.DownloadUri);
        if (response.Content.Headers.ContentLength is long contentLength
            && contentLength != file.ByteLength)
        {
            throw new RecommendedModelInstallException("model.download-size-mismatch");
        }

        var destinationPath = ResolveStagingFile(stagingDirectoryPath, file.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        var fileBytes = 0L;
        try
        {
            while (true)
            {
                transferCancellation.CancelAfter(_downloadInactivityTimeout);
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                fileBytes = checked(fileBytes + read);
                if (fileBytes > file.ByteLength)
                {
                    throw new RecommendedModelInstallException("model.download-size-mismatch");
                }

                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                progress?.Report(new ModelDownloadProgress(
                    definition.Descriptor.Id,
                    ModelDownloadStage.Downloading,
                    alreadyDownloadedBytes + fileBytes,
                    definition.Descriptor.DownloadBytes,
                    file.RelativePath));
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }

        var actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (fileBytes != file.ByteLength
            || !actualHash.Equals(file.Sha256, StringComparison.Ordinal))
        {
            throw new RecommendedModelInstallException(
                fileBytes != file.ByteLength ? "model.download-size-mismatch" : "model.download-hash-mismatch");
        }

        return checked(alreadyDownloadedBytes + fileBytes);
    }

    private string CreateStagingDirectory()
    {
        Directory.CreateDirectory(_paths.ModelDownloadStagingDirectoryPath);
        var path = Path.Combine(
            _paths.ModelDownloadStagingDirectoryPath,
            Guid.NewGuid().ToString("N"));
        _paths.EnsureSafePath(path);
        Directory.CreateDirectory(path);
        return path;
    }

    private string ResolveStagingFile(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathFullyQualified(relativePath)
            || relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment is "." or ".."))
        {
            throw new InvalidDataException("A pinned model download path is invalid.");
        }

        var candidate = Path.GetFullPath(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A pinned model download path escapes its staging directory.");
        }

        _paths.EnsureSafePath(candidate);
        return candidate;
    }

    private void DeletePartialDownload(string root, string relativePath)
    {
        var path = ResolveStagingFile(root, relativePath);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private async Task<long> RestoreVerifiedDownloadsAsync(
        RecommendedModelDownloadDefinition definition,
        string stagingDirectoryPath,
        HashSet<string> completedDownloads,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var recoveryDirectoryPath = GetRecoveryDirectoryPath(definition);
        if (!Directory.Exists(recoveryDirectoryPath))
        {
            return 0;
        }

        var restoredBytes = 0L;
        foreach (var file in definition.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cachedPath = ResolveStagingFile(recoveryDirectoryPath, file.RelativePath);
            if (!File.Exists(cachedPath))
            {
                continue;
            }

            var info = new FileInfo(cachedPath);
            var valid = info.Length == file.ByteLength;
            if (valid)
            {
                await using var stream = new FileStream(
                    cachedPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var hash = Convert.ToHexString(
                    await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
                    .ToLowerInvariant();
                valid = hash.Equals(file.Sha256, StringComparison.Ordinal);
            }

            if (!valid)
            {
                File.Delete(cachedPath);
                continue;
            }

            var stagingPath = ResolveStagingFile(stagingDirectoryPath, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(stagingPath)!);
            File.Move(cachedPath, stagingPath);
            completedDownloads.Add(file.RelativePath);
            restoredBytes = checked(restoredBytes + file.ByteLength);
            progress?.Report(new ModelDownloadProgress(
                definition.Descriptor.Id,
                ModelDownloadStage.Downloading,
                restoredBytes,
                definition.Descriptor.DownloadBytes,
                file.RelativePath));
        }

        TryDeleteEmptyDirectories(recoveryDirectoryPath);
        return restoredBytes;
    }

    private void TryPreserveCompletedDownloads(
        RecommendedModelDownloadDefinition definition,
        string stagingDirectoryPath,
        IReadOnlySet<string> completedDownloads)
    {
        try
        {
            var recoveryDirectoryPath = GetRecoveryDirectoryPath(definition);
            foreach (var file in definition.Files.Where(file => completedDownloads.Contains(file.RelativePath)))
            {
                var sourcePath = ResolveStagingFile(stagingDirectoryPath, file.RelativePath);
                if (!File.Exists(sourcePath))
                {
                    continue;
                }

                var destinationPath = ResolveStagingFile(recoveryDirectoryPath, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                if (File.Exists(destinationPath))
                {
                    File.Delete(destinationPath);
                }

                File.Move(sourcePath, destinationPath);
            }
        }
        catch
        {
            // Recovery caching must never replace the authoritative download,
            // validation, installation, or cancellation error.
        }
    }

    private string GetRecoveryDirectoryPath(RecommendedModelDownloadDefinition definition)
    {
        var identity = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(definition.Descriptor.Id)))
            .ToLowerInvariant();
        var path = Path.Combine(_paths.ModelDownloadRecoveryDirectoryPath, identity);
        _paths.EnsureSafePath(path);
        return path;
    }

    private void TryDeleteRecoveryDirectory(RecommendedModelDownloadDefinition definition)
    {
        try
        {
            var path = GetRecoveryDirectoryPath(definition);
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // The installed model is authoritative; a stale recovery cache is
            // safe and can be removed by later cache maintenance.
        }
    }

    private static void TryDeleteEmptyDirectories(string path)
    {
        try
        {
            if (Directory.Exists(path)
                && !Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories).Any())
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private void EnsureDiskSpace(RecommendedModelDownloadDefinition definition)
    {
        var pathRoot = Path.GetPathRoot(_paths.RootPath)
            ?? throw new IOException("The local application data volume could not be determined.");
        var drive = new DriveInfo(pathRoot);
        var importCopyBytes = definition.Descriptor.Kind == RecommendedModelPackageKind.Qwen3Vl2BInstruct
            ? definition.Descriptor.InstalledBytes
            : 0;
        var required = checked(
            definition.Descriptor.DownloadBytes + importCopyBytes + DownloadDiskMarginBytes);
        if (drive.AvailableFreeSpace < required)
        {
            throw new RecommendedModelInstallException("model.insufficient-disk-space");
        }
    }

    private void TryDeleteStagingDirectory(string path)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(_paths.ModelDownloadStagingDirectoryPath));
            var candidate = Path.GetFullPath(path);
            _paths.EnsureSafePath(candidate);
            if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (Directory.Exists(candidate))
            {
                Directory.Delete(candidate, recursive: true);
            }
        }
        catch
        {
            // The original error remains authoritative. A later startup can
            // safely retry cleanup inside the dedicated download staging root.
        }
    }

    private static string GetPackageKey(RecommendedModelDownloadDefinition definition)
    {
        var manifest = definition.ModelManifest
            ?? throw new InvalidDataException("The model catalog entry has no package identity.");
        return $"{manifest.Id}@{manifest.Version}";
    }

    private static string ClassifyInstallFailure(Exception exception)
    {
        string? classifiedFailure = null;
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is OcrProviderException providerFailure)
            {
                return providerFailure.ErrorCode;
            }

            if (current is IModelOperationFailure failure)
            {
                classifiedFailure = failure.ErrorCode;
            }
        }

        return classifiedFailure
            ?? "model.download-install-enable-failed";
    }

    private static void EnsureAllowedDownloadUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new RecommendedModelInstallException("model.download-uri-rejected");
        }

        var host = uri.IdnHost;
        var allowed = host.Equals("huggingface.co", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".huggingface.co", StringComparison.OrdinalIgnoreCase)
            || host.Equals("hf.co", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".hf.co", StringComparison.OrdinalIgnoreCase)
            || host.Equals("xethub.hf.co", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".xethub.hf.co", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".amazonaws.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".cloudfront.net", StringComparison.OrdinalIgnoreCase);
        if (!allowed)
        {
            throw new RecommendedModelInstallException("model.download-uri-rejected");
        }
    }

    private static void ValidateCatalog(IReadOnlyList<RecommendedModelDownloadDefinition> catalog)
    {
        if (catalog.Count == 0
            || catalog.Select(item => item.Descriptor.Id).Distinct(StringComparer.Ordinal).Count() != catalog.Count)
        {
            throw new InvalidDataException("The recommended model catalog is empty or contains duplicate IDs.");
        }

        foreach (var definition in catalog)
        {
            if (definition.Files.Count == 0
                || definition.Files.Sum(file => file.ByteLength) != definition.Descriptor.DownloadBytes
                || definition.Files.Any(file =>
                    file.ByteLength <= 0
                    || file.Sha256.Length != 64
                    || file.Sha256.Any(character => !char.IsAsciiHexDigit(character) || char.IsAsciiLetterUpper(character))))
            {
                throw new InvalidDataException("A recommended model catalog entry is inconsistent.");
            }

            foreach (var file in definition.Files)
            {
                EnsureAllowedDownloadUri(file.DownloadUri);
            }

            if (definition.ModelManifest is not null
                && (definition.ModelManifest.InstalledBytes != definition.Descriptor.InstalledBytes
                    || !definition.ModelManifest.Files.SequenceEqual(
                        definition.Files.Select(file => new ModelPackageFile(
                            file.RelativePath,
                            file.ByteLength,
                            file.Sha256)))))
            {
                throw new InvalidDataException("A recommended model manifest does not match its download list.");
            }
        }
    }

    internal static IReadOnlyList<RecommendedModelDownloadDefinition> CreateProductionCatalog()
    {
        const string ppDetRevision = "28fe5895c24fd108c19eb3e8479f4ab385fbfc62";
        const string ppRecRevision = "b8f84f0b80c529de40b4fbb3544b84fa7233a513";
        var ppFiles = new[]
        {
            DownloadFile(
                "detection.onnx",
                $"https://huggingface.co/PaddlePaddle/PP-OCRv6_small_det_onnx/resolve/{ppDetRevision}/inference.onnx",
                9_880_512,
                "d73e0058b7a8086bbd57f3d10b8bcd4ff95363f67e06e2762b5e814fe9c9410e"),
            DownloadFile(
                "recognition.onnx",
                $"https://huggingface.co/PaddlePaddle/PP-OCRv6_small_rec_onnx/resolve/{ppRecRevision}/inference.onnx",
                21_159_378,
                "5435fd747c9e0efe15a96d0b378d5bd157e9492ed8fd80edf08f30d02fa24634"),
            DownloadFile(
                "inference.yml",
                $"https://huggingface.co/PaddlePaddle/PP-OCRv6_small_rec_onnx/resolve/{ppRecRevision}/inference.yml",
                150_579,
                "ab078671bb49f06228eadccd34f1bb501e157f7a047095ffb943ba81512c77d1"),
        };

        const string qwenRevision = "b0ffadcc56e0e736aa1310ff75f7c81147ac50bb";
        const string qwenRepository =
            "https://huggingface.co/DogDreamson/picforlater-qwen3-vl-2b-onnx";
        var cpuQwen = CreateHostedQwenDefinition(
            qwenRepository,
            qwenRevision,
            "cpu-q4f32-rtnlast",
            "qwen3-vl-2b-instruct-picforlater-q4f32-cpu",
            "Qwen3-VL-2B CPU Q4F32",
            "picforlater.qwen3-vl-2b-instruct-q4f32-rtnlast-cpu",
            "0.2.0-q8964489-e697b160-posfix-rtnlast",
            "fp32-int4",
            3_818_973_177,
            12L * 1024 * 1024 * 1024,
            "16 GiB system RAM recommended; measured peak working set was about 6.43 GiB on Windows x64",
            "Position-fixed Q4F32 decoder with Q8 lm_head and FP32 vision/embedding; qualified for CPU testing.",
            "CPU",
            [
                ("added_tokens.json", 735, "79f6ec6fcc423d3a82bfac8b9033b1daac7b7bce06a5f5f441637b480cc605de"),
                ("build-provenance.json", 1_540, "f10c3256f1158bd0f27cfbbbe9717610dcf167cef4f3edd2aadd90f94804cf84"),
                ("chat_template.jinja", 5_412, "24a1eb036569714fc3efe7908495159c19ac5138f652c9e524475e40ce87d716"),
                ("genai_config.json", 1_964, "120db7242d8454efdff277c3e14c3855e97e69cbc627e79ee45324a16cdd95a7"),
                ("merges.txt", 1_671_853, "8831e4f1a044471340f7c0a83d7bd71306a5b867e95fd870f74d0c5308a904d5"),
                ("model.onnx", 922_066, "c26564049e630f0d1fb70d662f1176a0fd3040e347c65022106ed63b80e54679"),
                ("model.onnx.data", 1_231_421_440, "85f78cd787431a4397f0023f33c4f5184b86a40829060b6f2de3440b8f7e74e2"),
                ("qualification-cpu-en-event.json", 2_210, "c24f46659ce841d061272942d7192c995f1b3d79612b864eaa784182350ce9e0"),
                ("qualification-cpu-ja-event.json", 2_400, "db2d5e4213cb8ddf5db419436b071eddfcd301ad158f28b6a62d095aefa9c440"),
                ("qualification-cpu-zh-hans-event.json", 2_275, "bacef0c5e782072ab2ff45790e9217f58f14b7b5c4c1811fa5e8f71ab141d9d3"),
                ("qwen3vl-embedding.onnx", 1_244_665_029, "6789c1747eb58a56ac006fd8afd47bc80f5e1adbd38a5c46fd952708c17efcce"),
                ("qwen3vl-vision.onnx", 1_326_068_872, "9fb174647de813386c41f792bb51123c8a1466afc9e301f3a364627a28e03ce1"),
                ("special_tokens_map.json", 644, "57255613bbe23c9497211ca68561ff429a51e871dbaf5a59998fa4c8f7fe168a"),
                ("tokenizer_config.json", 5_643, "3b17b67dd43b8b9eeb75798501c0be6a52d034ef241cd138c2ae06810bf314cd"),
                ("tokenizer.json", 11_422_654, "aeb13307a71acd8fe81861d94ad54ab689df773318809eed3cbe794b4492dae4"),
                ("vision_processor.json", 1_607, "ca1f7cbefaf86f13ef6eb358cb9fe254a7f900754b5b4ed5bb2d53ca5ce42469"),
                ("vocab.json", 2_776_833, "ca10d7e9fb3ed18575dd1e277a2579c16d108e32f27439684afa0e10b1440910"),
            ]);
        var cudaQwen = CreateHostedQwenDefinition(
            qwenRepository,
            qwenRevision,
            "cuda-q4f16-rtnlast",
            "qwen3-vl-2b-instruct-picforlater-q4f16-cuda",
            "Qwen3-VL-2B CUDA Q4F16",
            "picforlater.qwen3-vl-2b-instruct-q4f16-rtnlast-cuda",
            "0.2.0-q8964489-e697b160-posfix-rtnlast",
            "int4",
            2_426_419_105,
            8L * 1024 * 1024 * 1024,
            "NVIDIA 8 GB-class GPU (7.5 GiB or more reported usable VRAM) and a driver supporting CUDA 12.x; PicForLater can privately install pinned CUDA/cuDNN user-mode libraries without changing PATH; 12 GiB system RAM recommended",
            "Position-fixed Q4F16 decoder with Q8 lm_head and FP16 vision/embedding; qualified for CUDA testing.",
            "CUDA",
            [
                ("added_tokens.json", 735, "79f6ec6fcc423d3a82bfac8b9033b1daac7b7bce06a5f5f441637b480cc605de"),
                ("build-provenance.json", 1_541, "cbe42799c7bafce70675e8f4c52350c3b86f84e652a6e3b27088c52b97722e74"),
                ("chat_template.jinja", 5_412, "24a1eb036569714fc3efe7908495159c19ac5138f652c9e524475e40ce87d716"),
                ("genai_config.json", 2_136, "966cf67b42dd08ef15214c0767f0c52a9af0478e13c10ef521a2ceaf937027ba"),
                ("merges.txt", 1_671_853, "8831e4f1a044471340f7c0a83d7bd71306a5b867e95fd870f74d0c5308a904d5"),
                ("model.onnx", 1_075_389, "99b30916420e9f4793b7274e08d4dd318c9c905651c16fabe88fe2d338405dfa"),
                ("model.onnx.data", 1_123_876_864, "0a3e92a821bf96decf19122e0441b365e9059a4d213d2a5a1a6c866465706add"),
                ("qualification-cuda-en-event.json", 2_262, "0be14fbdb36e0713acecbd0331d0e82a96415f683412dfbd0bba13991c4fce71"),
                ("qualification-cuda-ja-event.json", 2_562, "4ad3b6bcec04840f8f03a39ad6f09806f97e43f3cc967cdcf9b586ef94a8174c"),
                ("qualification-cuda-zh-hans-event.json", 2_373, "ef9e119bc06ff109f81f1d7e34e77af7dc920b215c8969b7befa76ece2980fc0"),
                ("qwen3vl-embedding.onnx", 622_335_173, "b8fdb8af39357e59c26605dac9dee8be79b4e43d12de20d163a5dcaa5c58525a"),
                ("qwen3vl-vision.onnx", 663_235_424, "70d36d50076e1993fa0f5d82c36e12a74c235522353ca2aa26039bfbb9cdbfb0"),
                ("special_tokens_map.json", 644, "57255613bbe23c9497211ca68561ff429a51e871dbaf5a59998fa4c8f7fe168a"),
                ("tokenizer_config.json", 5_643, "3b17b67dd43b8b9eeb75798501c0be6a52d034ef241cd138c2ae06810bf314cd"),
                ("tokenizer.json", 11_422_654, "aeb13307a71acd8fe81861d94ad54ab689df773318809eed3cbe794b4492dae4"),
                ("vision_processor.json", 1_607, "ca1f7cbefaf86f13ef6eb358cb9fe254a7f900754b5b4ed5bb2d53ca5ce42469"),
                ("vocab.json", 2_776_833, "ca10d7e9fb3ed18575dd1e277a2579c16d108e32f27439684afa0e10b1440910"),
            ]);

        return
        [
            new RecommendedModelDownloadDefinition(
                new RecommendedModelDescriptor(
                    "pp-ocrv6-small-official-onnx",
                    RecommendedModelPackageKind.PpOcrV6Small,
                    "PP-OCRv6-small",
                    "6.0.0",
                    "Pinned PaddlePaddle detection and unified multilingual recognition ONNX models.",
                    [ModelCapability.Ocr],
                    31_190_469,
                    31_190_469,
                    2L * 1024 * 1024 * 1024,
                    "2 GiB RAM; x64/ARM64 CPU baseline",
                    "Apache-2.0",
                    "https://www.paddleocr.ai/latest/en/version3.x/algorithm/PP-OCRv6/PP-OCRv6.html",
                    false,
                    "OfficialArtifactBenchmarkPending",
                    false,
                    false),
                ppFiles,
                null),
            cpuQwen,
            cudaQwen,
        ];
    }

    private static RecommendedModelDownloadDefinition CreateHostedQwenDefinition(
        string repository,
        string revision,
        string directory,
        string catalogId,
        string displayName,
        string packageId,
        string version,
        string quantization,
        long packageBytes,
        long minRamBytes,
        string recommendedHardware,
        string description,
        string executionProvider,
        IReadOnlyList<(string Path, long ByteLength, string Sha256)> declaredFiles)
    {
        var resolveBase = $"{repository}/resolve/{revision}/{directory}/";
        var sourceUrl = $"{repository}/tree/{revision}/{directory}";
        var files = declaredFiles
            .Select(file => DownloadFile(
                file.Path,
                resolveBase + file.Path,
                file.ByteLength,
                file.Sha256))
            .ToArray();
        var manifest = new ModelPackageManifest(
            1,
            packageId,
            version,
            "onnxruntime-genai",
            "onnx",
            "qwen3-vl-2b-instruct",
            quantization,
            [ModelCapability.VisionCaption, ModelCapability.TextComposition],
            ["en", "ja", "zh-Hans"],
            ["en", "ja", "zh-Hans"],
            ["Hans", "Jpan", "Latn"],
            true,
            files.Select(file => new ModelPackageFile(
                file.RelativePath,
                file.ByteLength,
                file.Sha256)).ToArray(),
            "Apache-2.0",
            sourceUrl,
            packageBytes,
            packageBytes,
            minRamBytes,
            recommendedHardware,
            "qwen3-vl.image+text.v1",
            "picforlater.analysis.v1",
            [executionProvider]);
        return new RecommendedModelDownloadDefinition(
            new RecommendedModelDescriptor(
                catalogId,
                RecommendedModelPackageKind.Qwen3Vl2BInstruct,
                displayName,
                version,
                description,
                [ModelCapability.VisionCaption, ModelCapability.TextComposition],
                packageBytes,
                packageBytes,
                minRamBytes,
                recommendedHardware,
                manifest.License,
                sourceUrl,
                true,
                "PublisherQualifiedGoldenSetPending",
                false,
                false),
            files,
            manifest);
    }

    private static RecommendedModelDownloadFile DownloadFile(
        string relativePath,
        string uri,
        long byteLength,
        string sha256) => new(relativePath, new Uri(uri, UriKind.Absolute), byteLength, sha256);
}
