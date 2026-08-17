using System.Net;
using System.Security.Cryptography;
using PicForLater.Core.Analysis;
using PicForLater.Infrastructure.Analysis;

namespace PicForLater.IntegrationTests;

public sealed class RecommendedModelDownloadServiceTests
{
    [Fact]
    public void ProductionCatalog_PinsBothPublishedPicForLaterQwenVariants()
    {
        var catalog = RecommendedModelDownloadService.CreateProductionCatalog();
        var qwen = catalog
            .Where(item => item.Descriptor.Kind == RecommendedModelPackageKind.Qwen3Vl2BInstruct)
            .ToArray();

        Assert.Equal(2, qwen.Length);
        Assert.Equal(
            ["qwen3-vl-2b-instruct-picforlater-q4f32-cpu", "qwen3-vl-2b-instruct-picforlater-q4f16-cuda"],
            qwen.Select(item => item.Descriptor.Id));
        Assert.All(qwen, definition =>
        {
            Assert.Contains(
                "DogDreamson/picforlater-qwen3-vl-2b-onnx",
                definition.Descriptor.SourceUrl,
                StringComparison.Ordinal);
            Assert.Contains(
                "b0ffadcc56e0e736aa1310ff75f7c81147ac50bb",
                definition.Descriptor.SourceUrl,
                StringComparison.Ordinal);
            Assert.Equal(
                definition.Descriptor.DownloadBytes,
                definition.Files.Sum(file => file.ByteLength));
            Assert.Equal(
                definition.Descriptor.InstalledBytes,
                definition.ModelManifest?.InstalledBytes);
            Assert.All(definition.Files, file =>
            {
                Assert.Contains(
                    "b0ffadcc56e0e736aa1310ff75f7c81147ac50bb",
                    file.DownloadUri.AbsoluteUri,
                    StringComparison.Ordinal);
                Assert.Equal(64, file.Sha256.Length);
            });
        });
        Assert.Equal(["CPU"], qwen[0].ModelManifest?.SupportedExecutionProviders);
        Assert.Equal(["CUDA"], qwen[1].ModelManifest?.SupportedExecutionProviders);
        Assert.Equal(3_818_973_177, qwen[0].Descriptor.DownloadBytes);
        Assert.Equal(2_426_419_105, qwen[1].Descriptor.DownloadBytes);
    }

    [Fact]
    public async Task DownloadInstallAndEnable_VerifiesBytesAndConsumesTheStagingDirectory()
    {
        using var root = new TemporaryAppDataRoot();
        root.Paths.EnsureCreated();
        var payload = "pinned-pp-ocr-model"u8.ToArray();
        var installer = new RecordingOcrInstaller();
        using var httpClient = new HttpClient(new StaticResponseHandler(payload));
        var definition = CreateDefinition(payload, Hash(payload));
        var service = new RecommendedModelDownloadService(
            root.Paths,
            httpClient,
            new EmptyModelPackageService(),
            installer,
            [definition]);
        var progress = new InlineProgress<ModelDownloadProgress>();

        var result = await service.DownloadInstallAndEnableAsync(definition.Descriptor.Id, progress);

        Assert.True(result.DownloadWasRequired);
        Assert.True(result.Model.IsInstalled);
        Assert.True(result.Model.IsEnabled);
        Assert.Equal(payload, installer.InstalledBytes);
        Assert.Contains(progress.Values, item => item.Stage == ModelDownloadStage.Downloading);
        Assert.Equal(ModelDownloadStage.Completed, progress.Values[^1].Stage);
        Assert.Empty(Directory.EnumerateFileSystemEntries(root.Paths.ModelDownloadStagingDirectoryPath));
    }

    [Fact]
    public async Task DownloadInstallAndEnable_HashMismatchCleansStagingAndDoesNotInstall()
    {
        using var root = new TemporaryAppDataRoot();
        root.Paths.EnsureCreated();
        var payload = "tampered-model"u8.ToArray();
        var installer = new RecordingOcrInstaller();
        using var httpClient = new HttpClient(new StaticResponseHandler(payload));
        var definition = CreateDefinition(payload, new string('0', 64));
        var service = new RecommendedModelDownloadService(
            root.Paths,
            httpClient,
            new EmptyModelPackageService(),
            installer,
            [definition]);

        var exception = await Assert.ThrowsAsync<RecommendedModelInstallException>(() =>
            service.DownloadInstallAndEnableAsync(definition.Descriptor.Id));

        Assert.Equal("model.download-hash-mismatch", exception.ErrorCode);
        Assert.False(installer.IsInstalled);
        Assert.Empty(Directory.EnumerateFileSystemEntries(root.Paths.ModelDownloadStagingDirectoryPath));
    }

    [Fact]
    public async Task DownloadInstallAndEnable_InactivityTimeoutIsNotReportedAsUserCancellation()
    {
        using var root = new TemporaryAppDataRoot();
        root.Paths.EnsureCreated();
        var payload = "never-delivered"u8.ToArray();
        using var httpClient = new HttpClient(new StalledResponseHandler());
        var definition = CreateDefinition(payload, Hash(payload));
        var service = new RecommendedModelDownloadService(
            root.Paths,
            httpClient,
            new EmptyModelPackageService(),
            new RecordingOcrInstaller(),
            [definition],
            TimeSpan.FromMilliseconds(50),
            downloadRetryCount: 1);

        var exception = await Assert.ThrowsAsync<RecommendedModelInstallException>(() =>
            service.DownloadInstallAndEnableAsync(definition.Descriptor.Id));

        Assert.Equal("model.download-timeout", exception.ErrorCode);
        Assert.Empty(Directory.EnumerateFileSystemEntries(root.Paths.ModelDownloadStagingDirectoryPath));
    }

    [Fact]
    public async Task DownloadInstallAndEnable_RetriesATransientRequestFailure()
    {
        using var root = new TemporaryAppDataRoot();
        root.Paths.EnsureCreated();
        var payload = "retry-succeeds"u8.ToArray();
        var handler = new FailOnceResponseHandler(payload);
        using var httpClient = new HttpClient(handler);
        var definition = CreateDefinition(payload, Hash(payload));
        var service = new RecommendedModelDownloadService(
            root.Paths,
            httpClient,
            new EmptyModelPackageService(),
            new RecordingOcrInstaller(),
            [definition],
            downloadRetryBaseDelay: TimeSpan.Zero);

        var result = await service.DownloadInstallAndEnableAsync(definition.Descriptor.Id);

        Assert.True(result.Model.IsEnabled);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task DownloadInstallAndEnable_ReusesVerifiedFilesAfterAFailedAttempt()
    {
        using var root = new TemporaryAppDataRoot();
        root.Paths.EnsureCreated();
        var firstPayload = "already-verified"u8.ToArray();
        var secondPayload = "download-on-retry"u8.ToArray();
        var definition = CreateTwoFileDefinition(firstPayload, secondPayload);
        using (var firstClient = new HttpClient(new DelegateResponseHandler(request =>
               request.RequestUri!.AbsolutePath.EndsWith("model.onnx", StringComparison.Ordinal)
                   ? Response(request, firstPayload)
                   : throw new HttpRequestException("Simulated second-file failure."))))
        {
            var firstService = new RecommendedModelDownloadService(
                root.Paths,
                firstClient,
                new EmptyModelPackageService(),
                new RecordingOcrInstaller(),
                [definition],
                downloadRetryCount: 1);
            await Assert.ThrowsAsync<RecommendedModelInstallException>(() =>
                firstService.DownloadInstallAndEnableAsync(definition.Descriptor.Id));
        }

        Assert.Single(Directory.EnumerateFiles(
            root.Paths.ModelDownloadRecoveryDirectoryPath,
            "*",
            SearchOption.AllDirectories));
        var firstFileWasRequestedAgain = false;
        var installer = new RecordingOcrInstaller();
        using var retryClient = new HttpClient(new DelegateResponseHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("model.onnx", StringComparison.Ordinal))
            {
                firstFileWasRequestedAgain = true;
                throw new InvalidOperationException("A verified file must not be requested again.");
            }

            return Response(request, secondPayload);
        }));
        var retryService = new RecommendedModelDownloadService(
            root.Paths,
            retryClient,
            new EmptyModelPackageService(),
            installer,
            [definition],
            downloadRetryBaseDelay: TimeSpan.Zero);

        var result = await retryService.DownloadInstallAndEnableAsync(definition.Descriptor.Id);

        Assert.True(result.Model.IsEnabled);
        Assert.False(firstFileWasRequestedAgain);
        Assert.Equal(firstPayload, installer.InstalledBytes);
        Assert.Empty(Directory.EnumerateFileSystemEntries(root.Paths.ModelDownloadRecoveryDirectoryPath));
    }

    [Fact]
    public async Task DownloadInstallAndEnable_RedownloadsOnlyTheCatalogFileWhosePinnedHashChanged()
    {
        using var root = new TemporaryAppDataRoot();
        root.Paths.EnsureCreated();
        var oldFirstPayload = "old-config"u8.ToArray();
        var newFirstPayload = "new-config"u8.ToArray();
        var secondPayload = "stable-weight"u8.ToArray();
        var oldDefinition = CreateTwoFileDefinition(oldFirstPayload, secondPayload);
        using (var firstClient = new HttpClient(new DelegateResponseHandler(request =>
               request.RequestUri!.AbsolutePath.EndsWith("model.onnx", StringComparison.Ordinal)
                   ? Response(request, oldFirstPayload)
                   : throw new HttpRequestException("Simulated interrupted package."))))
        {
            var firstService = new RecommendedModelDownloadService(
                root.Paths,
                firstClient,
                new EmptyModelPackageService(),
                new RecordingOcrInstaller(),
                [oldDefinition],
                downloadRetryCount: 1);
            await Assert.ThrowsAsync<RecommendedModelInstallException>(() =>
                firstService.DownloadInstallAndEnableAsync(oldDefinition.Descriptor.Id));
        }

        var updatedDefinition = CreateTwoFileDefinition(newFirstPayload, secondPayload);
        var requestedPaths = new List<string>();
        var installer = new RecordingOcrInstaller();
        using var retryClient = new HttpClient(new DelegateResponseHandler(request =>
        {
            requestedPaths.Add(request.RequestUri!.AbsolutePath);
            return request.RequestUri.AbsolutePath.EndsWith("model.onnx", StringComparison.Ordinal)
                ? Response(request, newFirstPayload)
                : Response(request, secondPayload);
        }));
        var retryService = new RecommendedModelDownloadService(
            root.Paths,
            retryClient,
            new EmptyModelPackageService(),
            installer,
            [updatedDefinition],
            downloadRetryBaseDelay: TimeSpan.Zero);

        var result = await retryService.DownloadInstallAndEnableAsync(updatedDefinition.Descriptor.Id);

        Assert.True(result.Model.IsEnabled);
        Assert.Equal(newFirstPayload, installer.InstalledBytes);
        Assert.Equal(2, requestedPaths.Count);
        Assert.Contains(requestedPaths, path => path.EndsWith("model.onnx", StringComparison.Ordinal));
        Assert.Contains(requestedPaths, path => path.EndsWith("second.onnx", StringComparison.Ordinal));
    }

    private static RecommendedModelDownloadDefinition CreateDefinition(byte[] payload, string sha256)
    {
        const string id = "test-pp-ocr-pinned";
        var descriptor = new RecommendedModelDescriptor(
            id,
            RecommendedModelPackageKind.PpOcrV6Small,
            "Test PP-OCR",
            "1.0.0",
            "Test-only pinned package.",
            [ModelCapability.Ocr],
            payload.LongLength,
            payload.LongLength,
            1,
            "Test CPU",
            "Apache-2.0",
            "https://huggingface.co/PaddlePaddle/test",
            false,
            "TestOnly",
            false,
            false);
        return new RecommendedModelDownloadDefinition(
            descriptor,
            [new RecommendedModelDownloadFile(
                "model.onnx",
                new Uri("https://huggingface.co/PaddlePaddle/test/resolve/main/model.onnx"),
                payload.LongLength,
                sha256)],
            null);
    }

    private static RecommendedModelDownloadDefinition CreateTwoFileDefinition(
        byte[] firstPayload,
        byte[] secondPayload)
    {
        var first = new RecommendedModelDownloadFile(
            "model.onnx",
            new Uri("https://huggingface.co/PaddlePaddle/test/resolve/main/model.onnx"),
            firstPayload.LongLength,
            Hash(firstPayload));
        var second = new RecommendedModelDownloadFile(
            "second.onnx",
            new Uri("https://huggingface.co/PaddlePaddle/test/resolve/main/second.onnx"),
            secondPayload.LongLength,
            Hash(secondPayload));
        var descriptor = new RecommendedModelDescriptor(
            "test-pp-ocr-recovery",
            RecommendedModelPackageKind.PpOcrV6Small,
            "Test PP-OCR recovery",
            "1.0.0",
            "Test-only recovery package.",
            [ModelCapability.Ocr],
            first.ByteLength + second.ByteLength,
            first.ByteLength + second.ByteLength,
            1,
            "Test CPU",
            "Apache-2.0",
            "https://huggingface.co/PaddlePaddle/test",
            false,
            "TestOnly",
            false,
            false);
        return new RecommendedModelDownloadDefinition(descriptor, [first, second], null);
    }

    private static HttpResponseMessage Response(HttpRequestMessage request, byte[] bytes) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes),
        RequestMessage = request,
    };

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class StaticResponseHandler(byte[] responseBytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(responseBytes),
                RequestMessage = request,
            });
        }
    }

    private sealed class StalledResponseHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class FailOnceResponseHandler(byte[] responseBytes) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            if (RequestCount == 1)
            {
                throw new HttpRequestException("Simulated transient connection failure.");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(responseBytes),
                RequestMessage = request,
            });
        }
    }

    private sealed class DelegateResponseHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class RecordingOcrInstaller : ILocalOcrPackageInstaller
    {
        public bool IsInstalled { get; private set; }

        public byte[]? InstalledBytes { get; private set; }

        public Task<bool> IsInstalledAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(IsInstalled);

        public async Task<LocalOcrPackageInstallResult> InstallAsync(
            string downloadedPackageDirectoryPath,
            CancellationToken cancellationToken = default)
        {
            InstalledBytes = await File.ReadAllBytesAsync(
                Path.Combine(downloadedPackageDirectoryPath, "model.onnx"),
                cancellationToken);
            Directory.Delete(downloadedPackageDirectoryPath, recursive: true);
            IsInstalled = true;
            return new LocalOcrPackageInstallResult(AlreadyInstalled: false);
        }
    }

    private sealed class EmptyModelPackageService : IModelPackageService
    {
        public Task<ModelProfileSnapshot> GetCurrentSnapshotAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(ModelProfileSnapshot.Default);

        public Task<ModelManagementState> GetStateAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(
                new ModelManagementState(ModelProfileSnapshot.Default, []));

        public Task<ModelPackageImportResult> ImportAsync(
            string manifestFilePath,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SwitchAsync(
            ModelCapability capability,
            string? packageKey,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SwitchManyAsync(
            IReadOnlyCollection<ModelCapability> capabilities,
            string? packageKey,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SetAnalysisModeAsync(
            AnalysisMode mode,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<InstalledModelPackage?> ResolveAsync(
            string packageKey,
            CancellationToken cancellationToken = default) => Task.FromResult<InstalledModelPackage?>(null);
    }

    private sealed class InlineProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];

        public void Report(T value) => Values.Add(value);
    }
}
