using PicForLater.Core.Analysis;

namespace PicForLater.Analysis.Tests;

public sealed class Qwen3VlProviderTests
{
    [Fact]
    public async Task Analyze_UsesBoundedAnalysisCopyAndDeletesTemporaryImage()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "PicForLater.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var package = CreatePackage(workingDirectory);
            var runtime = new RecordingRuntime();
            var preprocessor = new RecordingPreprocessor();
            var provider = new Qwen3VlProvider(
                new FakeModelPackageService(package),
                runtime,
                preprocessor,
                workingDirectory);
            var profile = new ModelProfileSnapshot(
                AnalysisMode.AlwaysEnhance,
                2,
                [
                    new ModelSlotSelection(ModelCapability.Ocr, "test.ocr", null),
                    new ModelSlotSelection(ModelCapability.VisionCaption, "local.qwen3-vl", package.PackageKey),
                    new ModelSlotSelection(ModelCapability.TextComposition, "local.qwen3-vl", package.PackageKey),
                    new ModelSlotSelection(ModelCapability.EntityExtraction, "test.entities", null),
                ]);
            var ocr = new OcrDocument(
                "Event",
                [],
                ["en"],
                [],
                new AnalysisProvenance("test.ocr", null, null, new Dictionary<string, string>(), "test.v1"),
                4000,
                3000);

            var result = await provider.AnalyzeAsync(new VisionAnalysisRequest(
                _ => ValueTask.FromResult<Stream>(new MemoryStream([1, 2, 3], writable: false)),
                "large.jpg",
                ocr,
                new AnalysisCompositionContext([]),
                profile));

            Assert.Equal("Generated title", result.Draft.Title);
            Assert.Equal(AnalysisExecutionLocation.Local, result.Provenance.ExecutionLocation);
            Assert.Equal(AnalysisOutputKind.ModelGeneratedDraft, result.Provenance.OutputKind);
            Assert.Equal(result.Provenance, result.Draft.Provenance);
            Assert.Equal(1, preprocessor.CallCount);
            Assert.Equal(RecordingPreprocessor.AnalysisBytes, runtime.ImageBytes);
            Assert.Equal(InferenceAccelerationMode.Cpu, runtime.AccelerationMode);
            Assert.Contains("Reference time UTC:", runtime.Prompt, StringComparison.Ordinal);
            Assert.Contains(
                "checking both the image itself and the supplied OCR facts",
                runtime.Prompt,
                StringComparison.Ordinal);
            Assert.Contains(
                "even when OCR omitted that text",
                runtime.Prompt,
                StringComparison.Ordinal);
            Assert.Contains("\"entities\"", runtime.JsonSchema, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFiles(workingDirectory));
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Analyze_CanceledGeneration_DeletesTemporaryImage()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "PicForLater.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var package = CreatePackage(workingDirectory);
            var provider = new Qwen3VlProvider(
                new FakeModelPackageService(package),
                new RecordingRuntime { CancelGeneration = true },
                new RecordingPreprocessor(),
                workingDirectory);

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                provider.AnalyzeAsync(CreateRequest(package)));

            Assert.Empty(Directory.EnumerateFiles(workingDirectory));
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Analyze_RejectsDirectMlOnlyModeBeforeLoadingCpuOnlyPackage()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "PicForLater.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var package = CreatePackage(workingDirectory, ["CPU"]);
            var runtime = new RecordingRuntime();
            var preprocessor = new RecordingPreprocessor();
            var provider = new Qwen3VlProvider(
                new FakeModelPackageService(package),
                runtime,
                preprocessor,
                workingDirectory,
                new FixedAccelerationModeProvider(InferenceAccelerationMode.DirectMlGpu));

            var exception = await Assert.ThrowsAsync<OcrProviderException>(() =>
                provider.AnalyzeAsync(CreateRequest(package)));

            Assert.Equal("qwen.directml-package-not-supported", exception.ErrorCode);
            Assert.Null(runtime.AccelerationMode);
            Assert.Equal(0, preprocessor.CallCount);
            Assert.Empty(Directory.EnumerateFiles(workingDirectory));
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Analyze_PassesDirectMlModeToPackageThatDeclaresDirectMlSupport()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "PicForLater.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var package = CreatePackage(workingDirectory, ["CPU", "DirectML"]);
            var runtime = new RecordingRuntime();
            var provider = new Qwen3VlProvider(
                new FakeModelPackageService(package),
                runtime,
                new RecordingPreprocessor(),
                workingDirectory,
                new FixedAccelerationModeProvider(InferenceAccelerationMode.DirectMlGpu));

            await provider.AnalyzeAsync(CreateRequest(package));

            Assert.Equal(InferenceAccelerationMode.DirectMlGpu, runtime.AccelerationMode);
            Assert.Empty(Directory.EnumerateFiles(workingDirectory));
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Analyze_AutomaticUsesCudaForCudaOnlyPackage()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "PicForLater.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var package = CreatePackage(workingDirectory, ["CUDA"]);
            var runtime = new RecordingRuntime();
            var provider = new Qwen3VlProvider(
                new FakeModelPackageService(package),
                runtime,
                new RecordingPreprocessor(),
                workingDirectory);

            await provider.AnalyzeAsync(CreateRequest(package));

            Assert.Equal(InferenceAccelerationMode.CudaGpu, runtime.AccelerationMode);
            Assert.Empty(Directory.EnumerateFiles(workingDirectory));
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Analyze_RejectsCpuOnlyModeBeforeLoadingCudaOnlyPackage()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "PicForLater.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var package = CreatePackage(workingDirectory, ["CUDA"]);
            var runtime = new RecordingRuntime();
            var preprocessor = new RecordingPreprocessor();
            var provider = new Qwen3VlProvider(
                new FakeModelPackageService(package),
                runtime,
                preprocessor,
                workingDirectory,
                new FixedAccelerationModeProvider(InferenceAccelerationMode.Cpu));

            var exception = await Assert.ThrowsAsync<OcrProviderException>(() =>
                provider.AnalyzeAsync(CreateRequest(package)));

            Assert.Equal("qwen.cpu-package-not-supported", exception.ErrorCode);
            Assert.Null(runtime.AccelerationMode);
            Assert.Equal(0, preprocessor.CallCount);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    private static InstalledModelPackage CreatePackage(
        string directory,
        IReadOnlyList<string>? supportedExecutionProviders = null)
    {
        var manifest = new ModelPackageManifest(
            1,
            "picforlater.qwen3-vl-2b-instruct-int4",
            "0.1.0",
            "onnxruntime-genai",
            "onnx",
            "qwen3-vl-2b-instruct",
            "int4",
            [ModelCapability.VisionCaption, ModelCapability.TextComposition],
            ["und"],
            ["en"],
            ["Latn"],
            true,
            [new ModelPackageFile("model.onnx", 1, new string('a', 64))],
            "Apache-2.0",
            "https://example.invalid/model",
            1,
            1,
            1,
            "test",
            "qwen3-vl.image+text.v1",
            QwenStructuredOutputParser.SchemaVersion,
            supportedExecutionProviders);
        return new InstalledModelPackage(
            $"{manifest.Id}@{manifest.Version}",
            manifest,
            directory,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            "SelfTestPassed");
    }

    private static VisionAnalysisRequest CreateRequest(InstalledModelPackage package)
    {
        var profile = new ModelProfileSnapshot(
            AnalysisMode.AlwaysEnhance,
            2,
            [
                new ModelSlotSelection(ModelCapability.Ocr, "test.ocr", null),
                new ModelSlotSelection(ModelCapability.VisionCaption, "local.qwen3-vl", package.PackageKey),
                new ModelSlotSelection(ModelCapability.TextComposition, "local.qwen3-vl", package.PackageKey),
                new ModelSlotSelection(ModelCapability.EntityExtraction, "test.entities", null),
            ]);
        var ocr = new OcrDocument(
            "Event",
            [],
            ["en"],
            [],
            new AnalysisProvenance("test.ocr", null, null, new Dictionary<string, string>(), "test.v1"),
            4000,
            3000);
        return new VisionAnalysisRequest(
            _ => ValueTask.FromResult<Stream>(new MemoryStream([1, 2, 3], writable: false)),
            "large.jpg",
            ocr,
            new AnalysisCompositionContext([]),
            profile);
    }

    private sealed class FixedAccelerationModeProvider(InferenceAccelerationMode mode)
        : IInferenceAccelerationModeProvider
    {
        public InferenceAccelerationMode CurrentMode { get; } = mode;
    }

    private sealed class RecordingPreprocessor : IVisionImagePreprocessor
    {
        public static byte[] AnalysisBytes { get; } = [9, 8, 7, 6];

        public int CallCount { get; private set; }

        public Task<Stream> CreateAnalysisCopyAsync(Stream source, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult<Stream>(new MemoryStream(AnalysisBytes, writable: false));
        }
    }

    private sealed class RecordingRuntime : IQwenGenerationRuntime
    {
        public IReadOnlySet<string> SupportedExecutionProviders { get; } =
            new HashSet<string>(["CPU", "DirectML", "CUDA"], StringComparer.Ordinal);

        public byte[]? ImageBytes { get; private set; }

        public InferenceAccelerationMode? AccelerationMode { get; private set; }

        public string? Prompt { get; private set; }

        public string? JsonSchema { get; private set; }

        public bool CancelGeneration { get; init; }

        public async Task<string> GenerateAsync(
            string modelDirectoryPath,
            string imageFilePath,
            string prompt,
            string jsonSchema,
            int maximumOutputTokens,
            InferenceAccelerationMode accelerationMode,
            CancellationToken cancellationToken = default)
        {
            ImageBytes = await File.ReadAllBytesAsync(imageFilePath, cancellationToken);
            AccelerationMode = accelerationMode;
            Prompt = prompt;
            JsonSchema = jsonSchema;
            if (CancelGeneration)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            return """{"schemaVersion":"picforlater.analysis.v1","visualFacts":["Poster"],"title":"Generated title","summary":"Generated summary","categoryIds":[],"entities":[],"detectedLanguages":["en"],"warnings":[]}""";
        }
    }

    private sealed class FakeModelPackageService(InstalledModelPackage package) : IModelPackageService
    {
        public Task<InstalledModelPackage?> ResolveAsync(string packageKey, CancellationToken cancellationToken = default) =>
            Task.FromResult<InstalledModelPackage?>(packageKey == package.PackageKey ? package : null);

        public Task<ModelProfileSnapshot> GetCurrentSnapshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(ModelProfileSnapshot.Default);

        public Task<ModelManagementState> GetStateAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ModelPackageImportResult> ImportAsync(string manifestFilePath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SwitchAsync(ModelCapability capability, string? packageKey, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SwitchManyAsync(
            IReadOnlyCollection<ModelCapability> capabilities,
            string? packageKey,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SetAnalysisModeAsync(AnalysisMode mode, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
