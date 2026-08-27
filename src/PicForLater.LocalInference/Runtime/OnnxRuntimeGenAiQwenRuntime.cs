using PicForLater.Analysis;
using PicForLater.Core.Analysis;

namespace PicForLater.LocalInference;

public sealed class OnnxRuntimeGenAiQwenRuntime : IQwenGenerationRuntime, IAsyncDisposable
{
    internal const string CpuProvider = "CPU";
    internal const string DirectMlProvider = "DirectML";
    internal const string CudaProvider = "CUDA";
    private readonly SemaphoreSlim _inferenceGate = new(1, 1);
    private readonly IInferenceExecutionContext _acceleration;
    private readonly IQwenGenerationContextFactory _contextFactory;
    private IQwenGenerationContext? _cachedContext;
    private QwenGenerationContextKey? _cachedContextKey;
    private bool _disposed;

    public OnnxRuntimeGenAiQwenRuntime(IInferenceExecutionContext acceleration)
        : this(acceleration, new OnnxRuntimeGenAiQwenContextFactory())
    {
    }

    internal OnnxRuntimeGenAiQwenRuntime(
        IInferenceExecutionContext acceleration,
        IQwenGenerationContextFactory contextFactory)
    {
        _acceleration = acceleration ?? throw new ArgumentNullException(nameof(acceleration));
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

#if PICFORLATER_CUDA_RUNTIME
    public IReadOnlySet<string> SupportedExecutionProviders { get; } =
        new HashSet<string>([CpuProvider, CudaProvider], StringComparer.Ordinal);
#else
    public IReadOnlySet<string> SupportedExecutionProviders { get; } =
        new HashSet<string>([CpuProvider, DirectMlProvider], StringComparer.Ordinal);
#endif

    public async Task<string> GenerateAsync(
        string modelDirectoryPath,
        string imageFilePath,
        string prompt,
        string jsonSchema,
        int maximumOutputTokens,
        InferenceAccelerationMode accelerationMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(imageFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonSchema);
        if (maximumOutputTokens is <= 0 or > 2048)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumOutputTokens));
        }

        await _inferenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(
                () => GenerateWithPolicy(
                    modelDirectoryPath,
                    imageFilePath,
                    prompt,
                    jsonSchema,
                    maximumOutputTokens,
                    accelerationMode,
                    cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _inferenceGate.Release();
        }
    }

    private string GenerateWithPolicy(
        string modelDirectoryPath,
        string imageFilePath,
        string prompt,
        string jsonSchema,
        int maximumOutputTokens,
        InferenceAccelerationMode accelerationMode,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(accelerationMode))
        {
            throw new ArgumentOutOfRangeException(nameof(accelerationMode));
        }

        var provider = ResolveProvider(accelerationMode);
        var executionDevice = provider switch
        {
            CpuProvider => InferenceExecutionDevice.Cpu,
            DirectMlProvider => InferenceExecutionDevice.DirectMlGpu,
            CudaProvider => InferenceExecutionDevice.CudaGpu,
            _ => throw new InvalidOperationException("The selected execution provider is unsupported."),
        };
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var context = GetOrCreateContext(modelDirectoryPath, provider);
            var output = context.Generate(
                imageFilePath,
                prompt,
                jsonSchema,
                maximumOutputTokens,
                cancellationToken);
            _acceleration.ReportExecution("Qwen3Vl", executionDevice);
            return output;
        }
        catch (OcrProviderException exception)
        {
            _acceleration.ReportExecution(
                "Qwen3Vl",
                executionDevice,
                failureCode: exception.ErrorCode);
            throw;
        }
    }

    private IQwenGenerationContext GetOrCreateContext(string modelDirectoryPath, string provider)
    {
        var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modelDirectoryPath));
        var requestedKey = new QwenGenerationContextKey(normalizedPath, provider);
        if (_cachedContext is not null && _cachedContextKey == requestedKey)
        {
            return _cachedContext;
        }

        var previousContext = _cachedContext;
        _cachedContext = null;
        _cachedContextKey = null;
        previousContext?.Dispose();

        var context = _contextFactory.Create(normalizedPath, provider);
        _cachedContext = context;
        _cachedContextKey = requestedKey;
        return context;
    }

    private string ResolveProvider(InferenceAccelerationMode accelerationMode)
    {
        var provider = accelerationMode switch
        {
            InferenceAccelerationMode.Cpu => CpuProvider,
            InferenceAccelerationMode.DirectMlGpu => DirectMlProvider,
            InferenceAccelerationMode.CudaGpu => CudaProvider,
#if PICFORLATER_CUDA_RUNTIME
            InferenceAccelerationMode.Automatic => CudaProvider,
#else
            InferenceAccelerationMode.Automatic => DirectMlProvider,
#endif
            _ => throw new ArgumentOutOfRangeException(nameof(accelerationMode)),
        };
        if (!SupportedExecutionProviders.Contains(provider))
        {
            throw new OcrProviderException(
                provider == CudaProvider
                    ? "qwen.cuda-runtime-unavailable"
                    : "qwen.directml-runtime-unavailable",
                isRetryable: false);
        }

        return provider;
    }

    public async ValueTask DisposeAsync()
    {
        await _inferenceGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            var context = _cachedContext;
            _cachedContext = null;
            _cachedContextKey = null;
            context?.Dispose();
        }
        finally
        {
            _inferenceGate.Release();
        }
    }
}

internal readonly record struct QwenGenerationContextKey
{
    public QwenGenerationContextKey(string modelDirectoryPath, string provider)
    {
        ModelDirectoryPath = modelDirectoryPath;
        Provider = provider;
    }

    public string ModelDirectoryPath { get; }

    public string Provider { get; }

    public bool Equals(QwenGenerationContextKey other) =>
        StringComparer.OrdinalIgnoreCase.Equals(ModelDirectoryPath, other.ModelDirectoryPath)
        && StringComparer.Ordinal.Equals(Provider, other.Provider);

    public override int GetHashCode() => HashCode.Combine(
        StringComparer.OrdinalIgnoreCase.GetHashCode(ModelDirectoryPath),
        StringComparer.Ordinal.GetHashCode(Provider));
}
