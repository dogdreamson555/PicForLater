using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using PicForLater.Core.Analysis;
using PicForLater.Infrastructure.Analysis;
using PicForLater.Infrastructure.Storage;

namespace PicForLater.IntegrationTests;

public sealed class NvidiaCudaEnvironmentServiceTests
{
    private const long DiskMarginBytes = 256L * 1024 * 1024;

    [Fact]
    public async Task Detect_QualifiedGpuWithoutRuntime_OffersPrivateInstallation()
    {
        using var root = new TemporaryAppDataRoot();
        root.Paths.EnsureCreated();
        using var httpClient = new HttpClient(new StaticArchiveHandler([]));
        var service = new NvidiaCudaEnvironmentService(
            root.Paths,
            httpClient,
            new FakeHardwareProbe(),
            runtimeLocator: _ => null);

        var status = await service.DetectAsync();

        Assert.Equal(NvidiaCudaEnvironmentState.RuntimeMissing, status.State);
        Assert.True(status.CanInstallRuntime);
        Assert.False(status.CanUseCudaModel);
        Assert.Equal("Test NVIDIA GPU", status.Device?.Name);
        Assert.Equal(12L * 1024 * 1024 * 1024, status.Device?.DedicatedMemoryBytes);
    }

    [Fact]
    public async Task Detect_NominalEightGigabyteGpuReportedAsSevenPointNineGiB_IsAccepted()
    {
        using var root = new TemporaryAppDataRoot();
        root.Paths.EnsureCreated();
        using var httpClient = new HttpClient(new StaticArchiveHandler([]));
        var reportedMemory = (long)(7.9 * 1024 * 1024 * 1024);
        var service = new NvidiaCudaEnvironmentService(
            root.Paths,
            httpClient,
            new FakeHardwareProbe(reportedMemory),
            runtimeLocator: _ => null);

        var status = await service.DetectAsync();

        Assert.Equal(NvidiaCudaEnvironmentState.RuntimeMissing, status.State);
        Assert.True(status.CanInstallRuntime);
    }

    [Fact]
    public async Task DownloadAndInstallRuntime_VerifiesArchiveAndInstallsOnlyRequiredDlls()
    {
        using var root = new TemporaryAppDataRoot();
        root.Paths.EnsureCreated();
        var requiredFiles = NvidiaCudaRuntimeLocator.CudaFiles
            .Concat(NvidiaCudaRuntimeLocator.CudnnFiles)
            .ToArray();
        var payload = CreateArchive(requiredFiles);
        var definition = new NvidiaCudaRuntimeArchiveDefinition(
            "runtime.zip",
            new Uri("https://developer.download.nvidia.com/compute/cuda/redist/test/runtime.zip"),
            payload.LongLength,
            Hash(payload),
            requiredFiles);
        var handler = new StaticArchiveHandler(payload);
        using var httpClient = new HttpClient(handler);
        var package = new NvidiaCudaRuntimePackageInfo(
            "12.8-test",
            "9-test",
            payload.LongLength,
            requiredFiles.Length,
            "https://example.invalid/cuda-license",
            "https://example.invalid/cudnn-license",
            "https://developer.download.nvidia.com/compute/cuda/redist/");
        NvidiaCudaRuntimeLocation? LocateManaged(string path) =>
            requiredFiles.All(fileName => File.Exists(Path.Combine(path, fileName)))
                ? new NvidiaCudaRuntimeLocation(
                    path,
                    path,
                    NvidiaCudaRuntimeSource.AppManaged)
                : null;
        var service = new NvidiaCudaEnvironmentService(
            root.Paths,
            httpClient,
            new FakeHardwareProbe(),
            archives: [definition],
            runtimePackage: package,
            runtimeLocator: LocateManaged);

        var result = await service.DownloadAndInstallRuntimeAsync();

        Assert.True(result.DownloadWasRequired);
        Assert.True(result.Status.CanUseCudaModel);
        Assert.Equal(NvidiaCudaRuntimeSource.AppManaged, result.Status.RuntimeSource);
        Assert.Equal(1, handler.RequestCount);
        Assert.All(requiredFiles, fileName =>
            Assert.True(File.Exists(Path.Combine(service.ManagedRuntimeDirectoryPath, fileName))));
        Assert.True(File.Exists(Path.Combine(
            service.ManagedRuntimeDirectoryPath,
            "runtime-manifest.json")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root.Paths.ModelRuntimeStagingDirectoryPath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(
            root.Paths.ModelRuntimeDownloadRecoveryDirectoryPath));
    }

    [Fact]
    public async Task DownloadAndInstallRuntime_UsesVerifiedRecoveryArchivesForDiskPreflight()
    {
        using var root = new TemporaryAppDataRoot();
        root.Paths.EnsureCreated();
        var fixture = CreateRuntimeFixture();
        var firstArchive = fixture.Archives[0];
        var failedHandler = new ArchiveMapHandler(
            fixture.ArchivePayloads,
            fixture.Archives[1].FileName);
        using (var failedClient = new HttpClient(failedHandler))
        {
            var failedService = CreateRuntimeService(
                root.Paths,
                failedClient,
                fixture,
                _ => long.MaxValue);
            await Assert.ThrowsAsync<RecommendedModelInstallException>(
                () => failedService.DownloadAndInstallRuntimeAsync());
        }

        var cachedPath = Path.Combine(
            GetRuntimeRecoveryDirectoryPath(root.Paths),
            firstArchive.FileName);
        Assert.True(File.Exists(cachedPath));
        var retryHandler = new ArchiveMapHandler(fixture.ArchivePayloads);
        using var retryClient = new HttpClient(retryHandler);
        var retryService = CreateRuntimeService(
            root.Paths,
            retryClient,
            fixture,
            _ => fixture.Archives[1].ByteLength
                + fixture.Package.InstalledBytes
                + DiskMarginBytes);

        var result = await retryService.DownloadAndInstallRuntimeAsync();

        Assert.True(result.Status.CanUseCudaModel);
        Assert.Equal([fixture.Archives[1].FileName], retryHandler.RequestedFileNames);
        Assert.Empty(Directory.EnumerateFileSystemEntries(
            root.Paths.ModelRuntimeDownloadRecoveryDirectoryPath));
    }

    [Fact]
    public async Task DownloadAndInstallRuntime_DoesNotCreditCorruptedRecoveryArchives()
    {
        using var root = new TemporaryAppDataRoot();
        root.Paths.EnsureCreated();
        var fixture = CreateRuntimeFixture();
        var firstArchive = fixture.Archives[0];
        var cachedPath = Path.Combine(
            GetRuntimeRecoveryDirectoryPath(root.Paths),
            firstArchive.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(cachedPath)!);
        var corruptedPayload = fixture.ArchivePayloads[firstArchive.FileName].ToArray();
        corruptedPayload[^1] ^= 0xff;
        await File.WriteAllBytesAsync(cachedPath, corruptedPayload);
        var handler = new ArchiveMapHandler(fixture.ArchivePayloads);
        using var httpClient = new HttpClient(handler);
        var service = CreateRuntimeService(
            root.Paths,
            httpClient,
            fixture,
            _ => fixture.Archives[1].ByteLength
                + fixture.Package.InstalledBytes
                + DiskMarginBytes);

        var exception = await Assert.ThrowsAsync<RecommendedModelInstallException>(
            () => service.DownloadAndInstallRuntimeAsync());

        Assert.Equal("model.insufficient-disk-space", exception.ErrorCode);
        Assert.Empty(handler.RequestedFileNames);
        Assert.False(File.Exists(cachedPath));
    }

    [Fact]
    public async Task DownloadAndInstallRuntime_RejectsInsufficientSpaceWithoutRecoveryArchivesBeforeNetwork()
    {
        using var root = new TemporaryAppDataRoot();
        root.Paths.EnsureCreated();
        var fixture = CreateRuntimeFixture();
        var handler = new ArchiveMapHandler(fixture.ArchivePayloads);
        using var httpClient = new HttpClient(handler);
        var service = CreateRuntimeService(
            root.Paths,
            httpClient,
            fixture,
            _ => fixture.Package.DownloadBytes
                + fixture.Package.InstalledBytes
                + DiskMarginBytes - 1);

        var exception = await Assert.ThrowsAsync<RecommendedModelInstallException>(
            () => service.DownloadAndInstallRuntimeAsync());

        Assert.Equal("model.insufficient-disk-space", exception.ErrorCode);
        Assert.Empty(handler.RequestedFileNames);
    }

    [Fact]
    public async Task DownloadAndInstallRuntime_ThrottlesRapidDownloadProgressUpdates()
    {
        using var root = new TemporaryAppDataRoot();
        root.Paths.EnsureCreated();
        var requiredFiles = NvidiaCudaRuntimeLocator.CudaFiles
            .Concat(NvidiaCudaRuntimeLocator.CudnnFiles)
            .ToArray();
        var payload = CreateArchive(requiredFiles, firstFileBytes: 8 * 1024 * 1024);
        var definition = new NvidiaCudaRuntimeArchiveDefinition(
            "runtime.zip",
            new Uri("https://developer.download.nvidia.com/compute/cuda/redist/test/runtime.zip"),
            payload.LongLength,
            Hash(payload),
            requiredFiles);
        using var httpClient = new HttpClient(new StaticArchiveHandler(payload));
        var package = new NvidiaCudaRuntimePackageInfo(
            "12.8-test",
            "9-test",
            payload.LongLength,
            payload.LongLength,
            "https://example.invalid/cuda-license",
            "https://example.invalid/cudnn-license",
            "https://developer.download.nvidia.com/compute/cuda/redist/");
        NvidiaCudaRuntimeLocation? LocateManaged(string path) =>
            requiredFiles.All(fileName => File.Exists(Path.Combine(path, fileName)))
                ? new NvidiaCudaRuntimeLocation(path, path, NvidiaCudaRuntimeSource.AppManaged)
                : null;
        var service = new NvidiaCudaEnvironmentService(
            root.Paths,
            httpClient,
            new FakeHardwareProbe(),
            archives: [definition],
            runtimePackage: package,
            runtimeLocator: LocateManaged,
            timeProvider: new FrozenTimeProvider());
        var reports = new List<ModelDownloadProgress>();

        await service.DownloadAndInstallRuntimeAsync(new CallbackProgress(reports.Add));

        var downloadReports = reports.Where(report => report.Stage == ModelDownloadStage.Downloading).ToArray();
        Assert.Equal(2, downloadReports.Length);
        Assert.Equal(payload.LongLength, downloadReports[^1].DownloadedBytes);
    }

    [Fact]
    public async Task DownloadAndInstallRuntime_HashMismatchDoesNotReplaceExistingRuntimeDirectory()
    {
        using var root = new TemporaryAppDataRoot();
        root.Paths.EnsureCreated();
        var payload = CreateArchive(["cudart64_12.dll"]);
        var definition = new NvidiaCudaRuntimeArchiveDefinition(
            "runtime.zip",
            new Uri("https://developer.download.nvidia.com/compute/cuda/redist/test/runtime.zip"),
            payload.LongLength,
            new string('0', 64),
            ["cudart64_12.dll"]);
        using var httpClient = new HttpClient(new StaticArchiveHandler(payload));
        var package = new NvidiaCudaRuntimePackageInfo(
            "12.8-test",
            "9-test",
            payload.LongLength,
            1,
            "https://example.invalid/cuda-license",
            "https://example.invalid/cudnn-license",
            "https://developer.download.nvidia.com/compute/cuda/redist/");
        var service = new NvidiaCudaEnvironmentService(
            root.Paths,
            httpClient,
            new FakeHardwareProbe(),
            archives: [definition],
            runtimePackage: package,
            runtimeLocator: _ => null);
        Directory.CreateDirectory(service.ManagedRuntimeDirectoryPath);
        var markerPath = Path.Combine(service.ManagedRuntimeDirectoryPath, "existing.marker");
        await File.WriteAllTextAsync(markerPath, "keep");

        var exception = await Assert.ThrowsAsync<RecommendedModelInstallException>(
            () => service.DownloadAndInstallRuntimeAsync());

        Assert.Equal("model.download-hash-mismatch", exception.ErrorCode);
        Assert.Equal("keep", await File.ReadAllTextAsync(markerPath));
    }

    private static byte[] CreateArchive(IReadOnlyList<string> fileNames, int firstFileBytes = 0)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var index = 0; index < fileNames.Count; index++)
            {
                var fileName = fileNames[index];
                var entry = archive.CreateEntry($"runtime/bin/{fileName}", CompressionLevel.NoCompression);
                using var destination = entry.Open();
                if (index == 0 && firstFileBytes > 0)
                {
                    destination.Write(new byte[firstFileBytes]);
                }
                else
                {
                    destination.Write("test-runtime"u8);
                }
            }

            var ignored = archive.CreateEntry("runtime/bin/not-allowlisted.dll");
            using var ignoredDestination = ignored.Open();
            ignoredDestination.Write("must-not-install"u8);
        }

        return stream.ToArray();
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static RuntimeArchiveFixture CreateRuntimeFixture()
    {
        var requiredFiles = NvidiaCudaRuntimeLocator.CudaFiles
            .Concat(NvidiaCudaRuntimeLocator.CudnnFiles)
            .ToArray();
        var split = requiredFiles.Length / 2;
        var fileGroups = new[]
        {
            requiredFiles[..split],
            requiredFiles[split..],
        };
        var archives = new List<NvidiaCudaRuntimeArchiveDefinition>();
        var archivePayloads = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < fileGroups.Length; index++)
        {
            var fileName = $"runtime-{index + 1}.zip";
            var payload = CreateArchive(fileGroups[index]);
            archives.Add(new NvidiaCudaRuntimeArchiveDefinition(
                fileName,
                new Uri($"https://developer.download.nvidia.com/compute/cuda/redist/test/{fileName}"),
                payload.LongLength,
                Hash(payload),
                fileGroups[index]));
            archivePayloads.Add(fileName, payload);
        }

        var package = new NvidiaCudaRuntimePackageInfo(
            "12.8-test",
            "9-test",
            archives.Sum(archive => archive.ByteLength),
            12L * requiredFiles.LongLength,
            "https://example.invalid/cuda-license",
            "https://example.invalid/cudnn-license",
            "https://developer.download.nvidia.com/compute/cuda/redist/");
        return new RuntimeArchiveFixture(requiredFiles, archives, archivePayloads, package);
    }

    private static NvidiaCudaEnvironmentService CreateRuntimeService(
        AppDataPaths paths,
        HttpClient httpClient,
        RuntimeArchiveFixture fixture,
        Func<string, long> availableFreeSpaceProvider)
    {
        NvidiaCudaRuntimeLocation? LocateManaged(string path) =>
            fixture.RequiredFiles.All(fileName => File.Exists(Path.Combine(path, fileName)))
                ? new NvidiaCudaRuntimeLocation(path, path, NvidiaCudaRuntimeSource.AppManaged)
                : null;
        return new NvidiaCudaEnvironmentService(
            paths,
            httpClient,
            new FakeHardwareProbe(),
            archives: fixture.Archives,
            runtimePackage: fixture.Package,
            runtimeLocator: LocateManaged,
            availableFreeSpaceProvider: availableFreeSpaceProvider);
    }

    private static string GetRuntimeRecoveryDirectoryPath(AppDataPaths paths) => Path.Combine(
        paths.ModelRuntimeDownloadRecoveryDirectoryPath,
        "nvidia-cuda-12.8.2-cudnn-9.25.0.15");

    private sealed record RuntimeArchiveFixture(
        IReadOnlyList<string> RequiredFiles,
        IReadOnlyList<NvidiaCudaRuntimeArchiveDefinition> Archives,
        IReadOnlyDictionary<string, byte[]> ArchivePayloads,
        NvidiaCudaRuntimePackageInfo Package);

    private sealed class FakeHardwareProbe(
        long dedicatedMemoryBytes = 12L * 1024 * 1024 * 1024) : INvidiaCudaHardwareProbe
    {
        public NvidiaCudaHardwareProbeResult Probe() => new(
            true,
            13_000,
            [new NvidiaGpuDevice("Test NVIDIA GPU", dedicatedMemoryBytes, 8, 9)]);
    }

    private sealed class StaticArchiveHandler(byte[] payload) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(payload),
            });
        }
    }

    private sealed class ArchiveMapHandler(
        IReadOnlyDictionary<string, byte[]> archivePayloads,
        string? failOnFileName = null) : HttpMessageHandler
    {
        public List<string> RequestedFileNames { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(request.RequestUri!.AbsolutePath);
            RequestedFileNames.Add(fileName);
            if (fileName.Equals(failOnFileName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Simulated interrupted archive download.");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(archivePayloads[fileName]),
            });
        }
    }

    private sealed class CallbackProgress(Action<ModelDownloadProgress> callback)
        : IProgress<ModelDownloadProgress>
    {
        public void Report(ModelDownloadProgress value) => callback(value);
    }

    private sealed class FrozenTimeProvider : TimeProvider
    {
        public override long GetTimestamp() => 0;
    }
}
