using PicForLater.Core.Analysis;

namespace PicForLater.LocalInference.Tests;

public sealed class OnnxRuntimeGenAiQwenRuntimeTests
{
    [Fact]
    public async Task GenerateAsync_SameModelAndProviderThreeTimes_CreatesOneContext()
    {
        var factory = new RecordingContextFactory();
        await using var runtime = new OnnxRuntimeGenAiQwenRuntime(
            new RecordingExecutionContext(),
            factory);

        for (var index = 0; index < 3; index++)
        {
            var output = await GenerateAsync(runtime, ModelPath("model-a"), InferenceAccelerationMode.Cpu);
            Assert.Equal("{}", output);
        }

        var context = Assert.Single(factory.Contexts);
        Assert.Equal(3, context.GenerateCount);
        Assert.False(context.IsDisposed);
        Assert.Equal(1, factory.MaximumActiveContextCount);
    }

    [Fact]
    public async Task GenerateAsync_ModelOrResolvedProviderChanges_ReplacesPreviousContext()
    {
        var factory = new RecordingContextFactory();
        await using var runtime = new OnnxRuntimeGenAiQwenRuntime(
            new RecordingExecutionContext(),
            factory);

        await GenerateAsync(runtime, ModelPath("model-a"), InferenceAccelerationMode.Cpu);
        await GenerateAsync(runtime, ModelPath("model-b"), InferenceAccelerationMode.Cpu);

        Assert.True(factory.Contexts[0].IsDisposed);
        Assert.False(factory.Contexts[1].IsDisposed);

        var resolvedAcceleratedProvider = Assert.Single(
            runtime.SupportedExecutionProviders,
            provider => provider != OnnxRuntimeGenAiQwenRuntime.CpuProvider);
        var explicitAcceleratedMode = resolvedAcceleratedProvider switch
        {
            OnnxRuntimeGenAiQwenRuntime.CudaProvider => InferenceAccelerationMode.CudaGpu,
            OnnxRuntimeGenAiQwenRuntime.DirectMlProvider => InferenceAccelerationMode.DirectMlGpu,
            _ => throw new InvalidOperationException("The test runtime reported an unexpected provider."),
        };

        await GenerateAsync(runtime, ModelPath("model-b"), InferenceAccelerationMode.Automatic);
        await GenerateAsync(runtime, ModelPath("model-b"), explicitAcceleratedMode);

        Assert.Equal(3, factory.Contexts.Count);
        Assert.True(factory.Contexts[1].IsDisposed);
        Assert.False(factory.Contexts[2].IsDisposed);
        Assert.Equal(resolvedAcceleratedProvider, factory.Contexts[2].Provider);
        Assert.Equal(2, factory.Contexts[2].GenerateCount);
        Assert.Equal(1, factory.MaximumActiveContextCount);
    }

    [Fact]
    public async Task GenerateAsync_CanceledRequest_KeepsContextUntilRuntimeExit()
    {
        var factory = new RecordingContextFactory();
        var runtime = new OnnxRuntimeGenAiQwenRuntime(
            new RecordingExecutionContext(),
            factory);
        var modelPath = ModelPath("model-a");

        await GenerateAsync(runtime, modelPath, InferenceAccelerationMode.Cpu);
        var context = Assert.Single(factory.Contexts);
        context.CancelNextGeneration = true;

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => GenerateAsync(runtime, modelPath, InferenceAccelerationMode.Cpu));

        Assert.Single(factory.Contexts);
        Assert.False(context.IsDisposed);

        await runtime.DisposeAsync();

        Assert.True(context.IsDisposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => GenerateAsync(runtime, modelPath, InferenceAccelerationMode.Cpu));
    }

    private static Task<string> GenerateAsync(
        OnnxRuntimeGenAiQwenRuntime runtime,
        string modelPath,
        InferenceAccelerationMode mode) =>
        runtime.GenerateAsync(
            modelPath,
            imageFilePath: "image.png",
            prompt: "prompt",
            jsonSchema: "{}",
            maximumOutputTokens: 32,
            mode);

    private static string ModelPath(string name) =>
        Path.Combine(Path.GetTempPath(), "PicForLater-Qwen-Context-Tests", name);

    private sealed class RecordingContextFactory : IQwenGenerationContextFactory
    {
        private int _activeContextCount;

        public List<RecordingContext> Contexts { get; } = [];

        public int MaximumActiveContextCount { get; private set; }

        public IQwenGenerationContext Create(string modelDirectoryPath, string provider)
        {
            var context = new RecordingContext(modelDirectoryPath, provider, OnContextDisposed);
            Contexts.Add(context);
            _activeContextCount++;
            MaximumActiveContextCount = Math.Max(MaximumActiveContextCount, _activeContextCount);
            return context;
        }

        private void OnContextDisposed() => _activeContextCount--;
    }

    private sealed class RecordingContext(
        string modelDirectoryPath,
        string provider,
        Action onDisposed) : IQwenGenerationContext
    {
        public string ModelDirectoryPath { get; } = modelDirectoryPath;

        public string Provider { get; } = provider;

        public int GenerateCount { get; private set; }

        public bool CancelNextGeneration { get; set; }

        public bool IsDisposed { get; private set; }

        public string Generate(
            string imageFilePath,
            string prompt,
            string jsonSchema,
            int maximumOutputTokens,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            GenerateCount++;
            if (CancelNextGeneration)
            {
                CancelNextGeneration = false;
                throw new OperationCanceledException(cancellationToken);
            }

            return "{}";
        }

        public void Dispose()
        {
            if (IsDisposed)
            {
                return;
            }

            IsDisposed = true;
            onDisposed();
        }
    }

    private sealed class RecordingExecutionContext : IInferenceExecutionContext
    {
        public InferenceAccelerationMode CurrentMode => InferenceAccelerationMode.Automatic;

        public InferenceExecutionStatus? LastExecutionStatus { get; private set; }

        public void ReportExecution(
            string workload,
            InferenceExecutionDevice device,
            bool usedAutomaticFallback = false,
            string? failureCode = null)
        {
            LastExecutionStatus = new InferenceExecutionStatus(
                workload,
                device,
                usedAutomaticFallback,
                failureCode,
                DateTimeOffset.UtcNow);
        }
    }
}
