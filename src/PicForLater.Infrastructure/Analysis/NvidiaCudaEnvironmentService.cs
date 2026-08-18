using System.Buffers;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PicForLater.Core.Analysis;
using PicForLater.Infrastructure.Storage;

namespace PicForLater.Infrastructure.Analysis;

public sealed record NvidiaCudaRuntimeLocation(
    string CudaDirectoryPath,
    string CudnnDirectoryPath,
    NvidiaCudaRuntimeSource Source);

public static class NvidiaCudaRuntimeLocator
{
    public static readonly IReadOnlyList<string> CudaFiles =
    [
        "cudart64_12.dll",
        "cublas64_12.dll",
        "cublasLt64_12.dll",
        "cufft64_11.dll",
        "nvrtc64_120_0.dll",
        "nvrtc-builtins64_128.dll",
    ];

    public static readonly IReadOnlyList<string> CudnnFiles =
    [
        "cudnn64_9.dll",
        "cudnn_adv64_9.dll",
        "cudnn_cnn64_9.dll",
        "cudnn_engines_precompiled64_9.dll",
        "cudnn_engines_runtime_compiled64_9.dll",
        "cudnn_engines_tensor_ir64_9.dll",
        "cudnn_ext64_9.dll",
        "cudnn_graph64_9.dll",
        "cudnn_heuristic64_9.dll",
        "cudnn_ops64_9.dll",
    ];

    public static NvidiaCudaRuntimeLocation? Locate(string managedRuntimeDirectoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRuntimeDirectoryPath);
        if (ContainsAll(managedRuntimeDirectoryPath, CudaFiles)
            && ContainsAll(managedRuntimeDirectoryPath, CudnnFiles))
        {
            return new NvidiaCudaRuntimeLocation(
                managedRuntimeDirectoryPath,
                managedRuntimeDirectoryPath,
                NvidiaCudaRuntimeSource.AppManaged);
        }

        var directories = EnumerateSystemRuntimeDirectories().ToArray();
        var cudaDirectory = directories.FirstOrDefault(path => ContainsAll(path, CudaFiles));
        var cudnnDirectory = directories.FirstOrDefault(path => ContainsAll(path, CudnnFiles));
        return cudaDirectory is not null && cudnnDirectory is not null
            ? new NvidiaCudaRuntimeLocation(
                cudaDirectory,
                cudnnDirectory,
                NvidiaCudaRuntimeSource.System)
            : null;
    }

    public static IReadOnlyList<string> GetMissingManagedFiles(string managedRuntimeDirectoryPath) =>
        CudaFiles.Concat(CudnnFiles)
            .Where(fileName => !File.Exists(Path.Combine(managedRuntimeDirectoryPath, fileName)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool ContainsAll(string directoryPath, IReadOnlyList<string> fileNames) =>
        Directory.Exists(directoryPath)
        && fileNames.All(fileName => File.Exists(Path.Combine(directoryPath, fileName)));

    private static IEnumerable<string> EnumerateSystemRuntimeDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in ReadEnvironmentValues("CUDA_PATH", "CUDNN_PATH", "CUDNN_HOME"))
        {
            foreach (var candidate in ExpandRuntimeRoot(value))
            {
                if (TryNormalizeDirectory(candidate, out var directory) && seen.Add(directory))
                {
                    yield return directory;
                }
            }
        }

        foreach (var pathValue in ReadEnvironmentValues("PATH"))
        {
            foreach (var candidate in pathValue.Split(
                         Path.PathSeparator,
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (TryNormalizeDirectory(candidate, out var directory) && seen.Add(directory))
                {
                    yield return directory;
                }
            }
        }
    }

    private static IEnumerable<string> ReadEnvironmentValues(params string[] names)
    {
        foreach (var name in names)
        {
            foreach (var target in new[]
                     {
                         EnvironmentVariableTarget.Process,
                         EnvironmentVariableTarget.Machine,
                         EnvironmentVariableTarget.User,
                     })
            {
                string? value;
                try
                {
                    value = Environment.GetEnvironmentVariable(name, target);
                }
                catch (System.Security.SecurityException)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(value))
                {
                    yield return value;
                }
            }
        }
    }

    private static IEnumerable<string> ExpandRuntimeRoot(string value)
    {
        yield return value;
        yield return Path.Combine(value, "bin");
        yield return Path.Combine(value, "bin", "x64");
    }

    private static bool TryNormalizeDirectory(string candidate, out string directory)
    {
        directory = string.Empty;
        try
        {
            candidate = Environment.ExpandEnvironmentVariables(candidate).Trim().Trim('"');
            if (!Path.IsPathFullyQualified(candidate))
            {
                return false;
            }

            var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            if (!Directory.Exists(normalized))
            {
                return false;
            }

            directory = normalized;
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or NotSupportedException
                or System.Security.SecurityException)
        {
            return false;
        }
    }
}

internal sealed record NvidiaCudaRuntimeArchiveDefinition(
    string FileName,
    Uri DownloadUri,
    long ByteLength,
    string Sha256,
    IReadOnlyList<string> RequiredDllNames);

public sealed class NvidiaCudaEnvironmentService : INvidiaCudaEnvironmentService
{
    private const string RuntimeId = "nvidia-cuda-runtime";
    private static readonly TimeSpan DownloadProgressInterval = TimeSpan.FromMilliseconds(250);
    private const int MinimumDriverCudaVersion = 12_000;
    // NVIDIA's nominal 8 GB cards can report about 7.9 GiB through the driver
    // after reserved regions are excluded. Keep the qualified 8 GB class while
    // avoiding a byte-exact 8 GiB rejection for common 3060/4060/5060 devices.
    private const long MinimumVramBytes = 15L * 1024 * 1024 * 1024 / 2;
    private const long DiskMarginBytes = 256L * 1024 * 1024;
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private static readonly IReadOnlyList<NvidiaCudaRuntimeArchiveDefinition> ProductionArchives =
    [
        new(
            "cuda-cudart.zip",
            new Uri("https://developer.download.nvidia.com/compute/cuda/redist/cuda_cudart/windows-x86_64/cuda_cudart-windows-x86_64-12.8.90-archive.zip"),
            3_037_735,
            "4a39058fd8519444a81cfc7ae055d136f48d1a31ffa41ae255b35b2edd61e13b",
            ["cudart64_12.dll"]),
        new(
            "cuda-cublas.zip",
            new Uri("https://developer.download.nvidia.com/compute/cuda/redist/libcublas/windows-x86_64/libcublas-windows-x86_64-12.8.5.5-archive.zip"),
            563_633_310,
            "0a2beedd7c1203cb9de5e5ab11943d27e41ee5d18dc3810b21bcd75be7e57a05",
            ["cublas64_12.dll", "cublasLt64_12.dll"]),
        new(
            "cuda-cufft.zip",
            new Uri("https://developer.download.nvidia.com/compute/cuda/redist/libcufft/windows-x86_64/libcufft-windows-x86_64-11.3.3.83-archive.zip"),
            190_568_498,
            "cc6e0ba958cf23387b462017a24464c72bd901549046133f3d1ebcc3d7444c90",
            ["cufft64_11.dll"]),
        new(
            "cuda-nvrtc.zip",
            new Uri("https://developer.download.nvidia.com/compute/cuda/redist/cuda_nvrtc/windows-x86_64/cuda_nvrtc-windows-x86_64-12.8.93-archive.zip"),
            305_588_898,
            "a63302a077f0248a743a1a7caa7dbd80d0fac56c6cfa9c41fa05fac9b7e5eda5",
            ["nvrtc64_120_0.dll", "nvrtc-builtins64_128.dll"]),
        new(
            "cudnn.zip",
            new Uri("https://developer.download.nvidia.com/compute/cudnn/redist/cudnn/windows-x86_64/cudnn-windows-x86_64-9.25.0.15_cuda12-archive.zip"),
            1_904_452_100,
            "06e94f70c52d7335b7ed8044eed28ce963b7fd59d8c2c446ffc60e695fccad91",
            NvidiaCudaRuntimeLocator.CudnnFiles),
    ];
    private static readonly NvidiaCudaRuntimePackageInfo ProductionRuntimePackage = new(
        "12.8.2",
        "9.25.0.15",
        ProductionArchives.Sum(archive => archive.ByteLength),
        2_350_000_000,
        "https://docs.nvidia.com/cuda/eula/index.html",
        "https://docs.nvidia.com/deeplearning/cudnn/latest/reference/eula.html",
        "https://developer.download.nvidia.com/compute/cuda/redist/");

    private readonly AppDataPaths _paths;
    private readonly HttpClient _httpClient;
    private readonly INvidiaCudaHardwareProbe _hardwareProbe;
    private readonly IReadOnlyList<NvidiaCudaRuntimeArchiveDefinition> _archives;
    private readonly Func<string, NvidiaCudaRuntimeLocation?> _runtimeLocator;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly TimeSpan _downloadInactivityTimeout;
    private readonly TimeProvider _timeProvider;

    public NvidiaCudaEnvironmentService(
        AppDataPaths paths,
        HttpClient httpClient,
        TimeSpan? downloadInactivityTimeout = null)
        : this(paths, httpClient, new NvidiaCudaHardwareProbe(), downloadInactivityTimeout)
    {
    }

    internal NvidiaCudaEnvironmentService(
        AppDataPaths paths,
        HttpClient httpClient,
        INvidiaCudaHardwareProbe hardwareProbe,
        TimeSpan? downloadInactivityTimeout = null,
        IReadOnlyList<NvidiaCudaRuntimeArchiveDefinition>? archives = null,
        NvidiaCudaRuntimePackageInfo? runtimePackage = null,
        Func<string, NvidiaCudaRuntimeLocation?>? runtimeLocator = null,
        TimeProvider? timeProvider = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _hardwareProbe = hardwareProbe ?? throw new ArgumentNullException(nameof(hardwareProbe));
        _archives = archives ?? ProductionArchives;
        RuntimePackage = runtimePackage ?? ProductionRuntimePackage;
        _runtimeLocator = runtimeLocator ?? NvidiaCudaRuntimeLocator.Locate;
        _timeProvider = timeProvider ?? TimeProvider.System;
        if (_archives.Count == 0
            || _archives.Sum(archive => archive.ByteLength) != RuntimePackage.DownloadBytes)
        {
            throw new ArgumentException("The NVIDIA runtime download catalog is inconsistent.", nameof(archives));
        }

        var declaredDlls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var archive in _archives)
        {
            EnsureAllowedDownloadUri(archive.DownloadUri);
            if (archive.ByteLength <= 0
                || archive.Sha256.Length != 64
                || archive.Sha256.Any(character =>
                    !char.IsAsciiHexDigit(character) || char.IsAsciiLetterUpper(character))
                || string.IsNullOrWhiteSpace(archive.FileName)
                || Path.GetFileName(archive.FileName) != archive.FileName
                || archive.RequiredDllNames.Count == 0
                || archive.RequiredDllNames.Any(fileName =>
                    Path.GetFileName(fileName) != fileName
                    || !fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    || !declaredDlls.Add(fileName)))
            {
                throw new ArgumentException(
                    "An NVIDIA runtime archive declaration is invalid.",
                    nameof(archives));
            }
        }

        _downloadInactivityTimeout = downloadInactivityTimeout ?? TimeSpan.FromMinutes(2);
        if (_downloadInactivityTimeout <= TimeSpan.Zero
            || _downloadInactivityTimeout > TimeSpan.FromMinutes(30))
        {
            throw new ArgumentOutOfRangeException(nameof(downloadInactivityTimeout));
        }

        ManagedRuntimeDirectoryPath = Path.Combine(
            _paths.ModelRuntimesDirectoryPath,
            "nvidia-cuda-12.8.2-cudnn-9.25.0.15");
        _paths.EnsureSafePath(ManagedRuntimeDirectoryPath);
    }

    public NvidiaCudaRuntimePackageInfo RuntimePackage { get; }

    public string ManagedRuntimeDirectoryPath { get; }

    public Task<NvidiaCudaEnvironmentStatus> DetectAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()
            || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return Task.FromResult(Status(NvidiaCudaEnvironmentState.UnsupportedArchitecture));
        }

        var hardware = _hardwareProbe.Probe();
        if (!hardware.DriverAvailable)
        {
            return Task.FromResult(Status(NvidiaCudaEnvironmentState.DriverMissing));
        }

        if (hardware.Devices.Count == 0)
        {
            return Task.FromResult(Status(
                NvidiaCudaEnvironmentState.NoCudaDevice,
                driverCudaVersion: hardware.DriverCudaVersion));
        }

        var device = hardware.Devices.MaxBy(candidate => candidate.DedicatedMemoryBytes)!;
        if (hardware.DriverCudaVersion < MinimumDriverCudaVersion)
        {
            return Task.FromResult(Status(
                NvidiaCudaEnvironmentState.DriverTooOld,
                device,
                hardware.DriverCudaVersion));
        }

        if (device.DedicatedMemoryBytes < MinimumVramBytes)
        {
            return Task.FromResult(Status(
                NvidiaCudaEnvironmentState.InsufficientVram,
                device,
                hardware.DriverCudaVersion));
        }

        var location = _runtimeLocator(ManagedRuntimeDirectoryPath);
        if (location is not null)
        {
            return Task.FromResult(new NvidiaCudaEnvironmentStatus(
                NvidiaCudaEnvironmentState.Ready,
                location.Source,
                device,
                hardware.DriverCudaVersion,
                []));
        }

        var missing = NvidiaCudaRuntimeLocator.GetMissingManagedFiles(
            ManagedRuntimeDirectoryPath);
        var state = Directory.Exists(ManagedRuntimeDirectoryPath)
            && missing.Count < NvidiaCudaRuntimeLocator.CudaFiles.Count
                + NvidiaCudaRuntimeLocator.CudnnFiles.Count
            ? NvidiaCudaEnvironmentState.RuntimeIncomplete
            : NvidiaCudaEnvironmentState.RuntimeMissing;
        return Task.FromResult(new NvidiaCudaEnvironmentStatus(
            state,
            NvidiaCudaRuntimeSource.None,
            device,
            hardware.DriverCudaVersion,
            missing));
    }

    public async Task<NvidiaCudaRuntimeInstallResult> DownloadAndInstallRuntimeAsync(
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? stagingDirectoryPath = null;
        try
        {
            var current = await DetectAsync(cancellationToken).ConfigureAwait(false);
            if (current.CanUseCudaModel)
            {
                return new NvidiaCudaRuntimeInstallResult(current, false);
            }

            if (!current.CanInstallRuntime)
            {
                throw new RecommendedModelInstallException(
                    current.State switch
                    {
                        NvidiaCudaEnvironmentState.DriverMissing => "nvidia.driver-missing",
                        NvidiaCudaEnvironmentState.DriverTooOld => "nvidia.driver-too-old",
                        NvidiaCudaEnvironmentState.InsufficientVram => "nvidia.insufficient-vram",
                        NvidiaCudaEnvironmentState.NoCudaDevice => "nvidia.cuda-device-missing",
                        _ => "nvidia.cuda-runtime-unsupported",
                    });
            }

            EnsureDiskSpace();
            progress?.Report(Progress(ModelDownloadStage.Preparing, 0));
            var recoveryDirectoryPath = GetRecoveryDirectoryPath();
            Directory.CreateDirectory(recoveryDirectoryPath);
            var downloadedBytes = 0L;
            foreach (var archive in _archives)
            {
                var archivePath = Path.Combine(recoveryDirectoryPath, archive.FileName);
                if (!await IsVerifiedArchiveAsync(
                        archivePath,
                        archive,
                        cancellationToken).ConfigureAwait(false))
                {
                    await DownloadArchiveWithRetriesAsync(
                        archive,
                        archivePath,
                        downloadedBytes,
                        progress,
                        cancellationToken).ConfigureAwait(false);
                }

                downloadedBytes = checked(downloadedBytes + archive.ByteLength);
                progress?.Report(Progress(
                    ModelDownloadStage.Downloading,
                    downloadedBytes,
                    archive.FileName));
            }

            progress?.Report(Progress(ModelDownloadStage.Verifying, downloadedBytes));
            stagingDirectoryPath = CreateStagingDirectory();
            var extractedDirectoryPath = Path.Combine(stagingDirectoryPath, "runtime");
            Directory.CreateDirectory(extractedDirectoryPath);
            var installedFiles = new List<InstalledRuntimeFile>();
            foreach (var archive in _archives)
            {
                await ExtractRequiredFilesAsync(
                    Path.Combine(recoveryDirectoryPath, archive.FileName),
                    archive,
                    extractedDirectoryPath,
                    installedFiles,
                    cancellationToken).ConfigureAwait(false);
            }

            var missing = NvidiaCudaRuntimeLocator.CudaFiles
                .Concat(NvidiaCudaRuntimeLocator.CudnnFiles)
                .Where(name => !File.Exists(Path.Combine(extractedDirectoryPath, name)))
                .ToArray();
            if (missing.Length != 0)
            {
                throw new RecommendedModelInstallException("nvidia.runtime-archive-invalid");
            }

            var manifest = new InstalledRuntimeManifest(
                1,
                RuntimePackage.CudaVersion,
                RuntimePackage.CudnnVersion,
                DateTimeOffset.UtcNow,
                _archives.Select(archive => new InstalledRuntimeArchive(
                    archive.DownloadUri.AbsoluteUri,
                    archive.ByteLength,
                    archive.Sha256)).ToArray(),
                installedFiles.OrderBy(file => file.FileName, StringComparer.Ordinal).ToArray());
            await File.WriteAllTextAsync(
                Path.Combine(extractedDirectoryPath, "runtime-manifest.json"),
                JsonSerializer.Serialize(manifest, ManifestJsonOptions),
                cancellationToken).ConfigureAwait(false);

            progress?.Report(Progress(ModelDownloadStage.Installing, downloadedBytes));
            InstallAtomically(extractedDirectoryPath);
            TryDeleteDirectory(stagingDirectoryPath);
            stagingDirectoryPath = null;
            TryDeleteDirectory(recoveryDirectoryPath);
            var installed = await DetectAsync(cancellationToken).ConfigureAwait(false);
            if (!installed.CanUseCudaModel)
            {
                throw new RecommendedModelInstallException("nvidia.runtime-install-incomplete");
            }

            progress?.Report(Progress(ModelDownloadStage.Completed, downloadedBytes));
            return new NvidiaCudaRuntimeInstallResult(installed, true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RecommendedModelInstallException)
        {
            throw;
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new RecommendedModelInstallException("model.file-access-denied", exception);
        }
        catch (IOException exception)
        {
            throw new RecommendedModelInstallException("model.file-io-failed", exception);
        }
        catch (Exception exception)
        {
            throw new RecommendedModelInstallException("nvidia.runtime-install-failed", exception);
        }
        finally
        {
            if (stagingDirectoryPath is not null)
            {
                TryDeleteDirectory(stagingDirectoryPath);
            }

            _operationGate.Release();
        }
    }

    private NvidiaCudaEnvironmentStatus Status(
        NvidiaCudaEnvironmentState state,
        NvidiaGpuDevice? device = null,
        int driverCudaVersion = 0) => new(
        state,
        NvidiaCudaRuntimeSource.None,
        device,
        driverCudaVersion,
        NvidiaCudaRuntimeLocator.GetMissingManagedFiles(ManagedRuntimeDirectoryPath));

    private void EnsureDiskSpace()
    {
        var root = Path.GetPathRoot(_paths.RootPath)
            ?? throw new IOException("The application data volume could not be determined.");
        var required = checked(RuntimePackage.DownloadBytes + RuntimePackage.InstalledBytes + DiskMarginBytes);
        if (new DriveInfo(root).AvailableFreeSpace < required)
        {
            throw new RecommendedModelInstallException("model.insufficient-disk-space");
        }
    }

    private string GetRecoveryDirectoryPath()
    {
        var path = Path.Combine(
            _paths.ModelRuntimeDownloadRecoveryDirectoryPath,
            "nvidia-cuda-12.8.2-cudnn-9.25.0.15");
        _paths.EnsureSafePath(path);
        return path;
    }

    private string CreateStagingDirectory()
    {
        var path = Path.Combine(
            _paths.ModelRuntimeStagingDirectoryPath,
            Guid.NewGuid().ToString("N"));
        _paths.EnsureSafePath(path);
        Directory.CreateDirectory(path);
        return path;
    }

    private async Task DownloadArchiveAsync(
        NvidiaCudaRuntimeArchiveDefinition archive,
        string destinationPath,
        long alreadyDownloadedBytes,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        EnsureAllowedDownloadUri(archive.DownloadUri);
        var partialPath = destinationPath + ".partial";
        if (File.Exists(partialPath))
        {
            File.Delete(partialPath);
        }

        using var transferCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        transferCancellation.CancelAfter(_downloadInactivityTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, archive.DownloadUri);
            request.Headers.UserAgent.ParseAdd("PicForLater/1.0 nvidia-runtime-downloader");
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                transferCancellation.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            EnsureAllowedDownloadUri(response.RequestMessage?.RequestUri ?? archive.DownloadUri);
            if (response.Content.Headers.ContentLength is long contentLength
                && contentLength != archive.ByteLength)
            {
                throw new RecommendedModelInstallException("model.download-size-mismatch");
            }

            await using var source = await response.Content.ReadAsStreamAsync(
                transferCancellation.Token).ConfigureAwait(false);
            await using var destination = new FileStream(
                partialPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
            var fileBytes = 0L;
            var lastProgressTimestamp = _timeProvider.GetTimestamp();
            try
            {
                while (true)
                {
                    transferCancellation.CancelAfter(_downloadInactivityTimeout);
                    var read = await source.ReadAsync(
                        buffer.AsMemory(0, buffer.Length),
                        transferCancellation.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    fileBytes = checked(fileBytes + read);
                    if (fileBytes > archive.ByteLength)
                    {
                        throw new RecommendedModelInstallException("model.download-size-mismatch");
                    }

                    hash.AppendData(buffer, 0, read);
                    await destination.WriteAsync(
                        buffer.AsMemory(0, read),
                        transferCancellation.Token).ConfigureAwait(false);
                    var progressTimestamp = _timeProvider.GetTimestamp();
                    if (progress is not null
                        && (fileBytes == read
                            || _timeProvider.GetElapsedTime(lastProgressTimestamp, progressTimestamp)
                                >= DownloadProgressInterval))
                    {
                        progress.Report(Progress(
                            ModelDownloadStage.Downloading,
                            alreadyDownloadedBytes + fileBytes,
                            archive.FileName));
                        lastProgressTimestamp = progressTimestamp;
                    }
                }

                await destination.FlushAsync(transferCancellation.Token).ConfigureAwait(false);
                await destination.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }

            var actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (fileBytes != archive.ByteLength
                || !actualHash.Equals(archive.Sha256, StringComparison.Ordinal))
            {
                throw new RecommendedModelInstallException(
                    fileBytes != archive.ByteLength
                        ? "model.download-size-mismatch"
                        : "model.download-hash-mismatch");
            }

            File.Move(partialPath, destinationPath, overwrite: true);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RecommendedModelInstallException("model.download-timeout", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new RecommendedModelInstallException("model.download-network-failed", exception);
        }
        finally
        {
            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }
        }
    }

    private async Task DownloadArchiveWithRetriesAsync(
        NvidiaCudaRuntimeArchiveDefinition archive,
        string destinationPath,
        long alreadyDownloadedBytes,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        RecommendedModelInstallException? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await DownloadArchiveAsync(
                    archive,
                    destinationPath,
                    alreadyDownloadedBytes,
                    progress,
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (RecommendedModelInstallException exception) when (
                exception.ErrorCode is "model.download-timeout"
                    or "model.download-network-failed")
            {
                lastError = exception;
                if (attempt < 3)
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        throw lastError
            ?? new RecommendedModelInstallException("model.download-network-failed");
    }

    private static async Task<bool> IsVerifiedArchiveAsync(
        string path,
        NvidiaCudaRuntimeArchiveDefinition archive,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != archive.ByteLength)
        {
            return false;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actualHash = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();
        if (actualHash.Equals(archive.Sha256, StringComparison.Ordinal))
        {
            return true;
        }

        File.Delete(path);
        return false;
    }

    private static async Task ExtractRequiredFilesAsync(
        string archivePath,
        NvidiaCudaRuntimeArchiveDefinition definition,
        string destinationDirectoryPath,
        ICollection<InstalledRuntimeFile> installedFiles,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var expected = definition.RequiredDllNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(entry.FullName.Replace('\\', '/'));
            if (!expected.Remove(fileName))
            {
                continue;
            }

            if (entry.Length <= 0 || entry.Length > 1024L * 1024 * 1024)
            {
                throw new RecommendedModelInstallException("nvidia.runtime-archive-invalid");
            }

            var destinationPath = Path.Combine(destinationDirectoryPath, fileName);
            await using var source = entry.Open();
            await using var destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
            var written = 0L;
            try
            {
                while (true)
                {
                    var read = await source.ReadAsync(
                        buffer.AsMemory(0, buffer.Length),
                        cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    written = checked(written + read);
                    if (written > entry.Length)
                    {
                        throw new RecommendedModelInstallException("nvidia.runtime-archive-invalid");
                    }

                    hash.AppendData(buffer, 0, read);
                    await destination.WriteAsync(
                        buffer.AsMemory(0, read),
                        cancellationToken).ConfigureAwait(false);
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }

            if (written != entry.Length)
            {
                throw new RecommendedModelInstallException("nvidia.runtime-archive-invalid");
            }

            installedFiles.Add(new InstalledRuntimeFile(
                fileName,
                written,
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()));
        }

        if (expected.Count != 0)
        {
            throw new RecommendedModelInstallException("nvidia.runtime-archive-invalid");
        }
    }

    private void InstallAtomically(string extractedDirectoryPath)
    {
        _paths.EnsureSafePath(extractedDirectoryPath);
        _paths.EnsureSafePath(ManagedRuntimeDirectoryPath);
        if (Directory.Exists(ManagedRuntimeDirectoryPath))
        {
            Directory.Delete(ManagedRuntimeDirectoryPath, recursive: true);
        }

        Directory.Move(extractedDirectoryPath, ManagedRuntimeDirectoryPath);
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            _paths.EnsureSafePath(path);
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Verified runtime files are authoritative. Cache/staging cleanup can
            // be retried without changing the selected model or user content.
        }
    }

    private ModelDownloadProgress Progress(
        ModelDownloadStage stage,
        long downloadedBytes,
        string? currentFile = null) => new(
        RuntimeId,
        stage,
        downloadedBytes,
        _archives.Sum(archive => archive.ByteLength),
        currentFile);

    private static void EnsureAllowedDownloadUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals("developer.download.nvidia.com", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.StartsWith("/compute/", StringComparison.Ordinal))
        {
            throw new RecommendedModelInstallException("model.download-uri-rejected");
        }
    }

    private sealed record InstalledRuntimeArchive(
        string SourceUrl,
        long ByteLength,
        string Sha256);

    private sealed record InstalledRuntimeFile(
        string FileName,
        long ByteLength,
        string Sha256);

    private sealed record InstalledRuntimeManifest(
        int ManifestVersion,
        string CudaVersion,
        string CudnnVersion,
        DateTimeOffset InstalledAtUtc,
        IReadOnlyList<InstalledRuntimeArchive> Archives,
        IReadOnlyList<InstalledRuntimeFile> Files);
}

internal sealed record NvidiaCudaHardwareProbeResult(
    bool DriverAvailable,
    int DriverCudaVersion,
    IReadOnlyList<NvidiaGpuDevice> Devices);

internal interface INvidiaCudaHardwareProbe
{
    NvidiaCudaHardwareProbeResult Probe();
}

internal sealed class NvidiaCudaHardwareProbe : INvidiaCudaHardwareProbe
{
    private const int ComputeCapabilityMajorAttribute = 75;
    private const int ComputeCapabilityMinorAttribute = 76;

    public NvidiaCudaHardwareProbeResult Probe()
    {
        try
        {
            if (CuInit(0) != 0
                || CuDriverGetVersion(out var driverVersion) != 0
                || CuDeviceGetCount(out var deviceCount) != 0)
            {
                return new NvidiaCudaHardwareProbeResult(true, 0, []);
            }

            var devices = new List<NvidiaGpuDevice>(Math.Max(0, deviceCount));
            for (var index = 0; index < deviceCount; index++)
            {
                var nameBytes = new byte[256];
                if (CuDeviceGetName(nameBytes, nameBytes.Length, index) != 0
                    || CuDeviceTotalMem(out var memory, index) != 0
                    || CuDeviceGetAttribute(
                        out var major,
                        ComputeCapabilityMajorAttribute,
                        index) != 0
                    || CuDeviceGetAttribute(
                        out var minor,
                        ComputeCapabilityMinorAttribute,
                        index) != 0)
                {
                    continue;
                }

                var terminator = Array.IndexOf(nameBytes, (byte)0);
                var name = Encoding.UTF8.GetString(
                    nameBytes,
                    0,
                    terminator >= 0 ? terminator : nameBytes.Length);
                devices.Add(new NvidiaGpuDevice(
                    string.IsNullOrWhiteSpace(name) ? "NVIDIA CUDA GPU" : name,
                    checked((long)memory.ToUInt64()),
                    major,
                    minor));
            }

            return new NvidiaCudaHardwareProbeResult(true, driverVersion, devices);
        }
        catch (DllNotFoundException)
        {
            return new NvidiaCudaHardwareProbeResult(false, 0, []);
        }
        catch (EntryPointNotFoundException)
        {
            return new NvidiaCudaHardwareProbeResult(false, 0, []);
        }
        catch (BadImageFormatException)
        {
            return new NvidiaCudaHardwareProbeResult(false, 0, []);
        }
    }

    [DllImport("nvcuda.dll", EntryPoint = "cuInit")]
    private static extern int CuInit(uint flags);

    [DllImport("nvcuda.dll", EntryPoint = "cuDriverGetVersion")]
    private static extern int CuDriverGetVersion(out int driverVersion);

    [DllImport("nvcuda.dll", EntryPoint = "cuDeviceGetCount")]
    private static extern int CuDeviceGetCount(out int count);

    [DllImport("nvcuda.dll", EntryPoint = "cuDeviceGetName")]
    private static extern int CuDeviceGetName(
        [Out] byte[] name,
        int length,
        int device);

    [DllImport("nvcuda.dll", EntryPoint = "cuDeviceTotalMem_v2")]
    private static extern int CuDeviceTotalMem(out UIntPtr bytes, int device);

    [DllImport("nvcuda.dll", EntryPoint = "cuDeviceGetAttribute")]
    private static extern int CuDeviceGetAttribute(
        out int value,
        int attribute,
        int device);
}
