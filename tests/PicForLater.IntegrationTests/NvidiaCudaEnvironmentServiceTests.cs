using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using PicForLater.Core.Analysis;
using PicForLater.Infrastructure.Analysis;

namespace PicForLater.IntegrationTests;

public sealed class NvidiaCudaEnvironmentServiceTests
{
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

    private static byte[] CreateArchive(IReadOnlyList<string> fileNames)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var fileName in fileNames)
            {
                var entry = archive.CreateEntry($"runtime/bin/{fileName}", CompressionLevel.NoCompression);
                using var destination = entry.Open();
                destination.Write("test-runtime"u8);
            }

            var ignored = archive.CreateEntry("runtime/bin/not-allowlisted.dll");
            using var ignoredDestination = ignored.Open();
            ignoredDestination.Write("must-not-install"u8);
        }

        return stream.ToArray();
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

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
}
