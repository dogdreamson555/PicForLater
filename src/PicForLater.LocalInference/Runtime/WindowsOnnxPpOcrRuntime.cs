using System.Collections.Concurrent;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PicForLater.Core.Analysis;

namespace PicForLater.LocalInference;

/// <summary>
/// Uses the local worker's ONNX Runtime payload. It never starts another
/// executable, server, script, or network request.
/// </summary>
public sealed class WindowsOnnxPpOcrRuntime : IPpOcrV6InferenceRuntime
{
    private static readonly object EnvironmentGate = new();
    private static bool _environmentConfigured;
    private readonly ConcurrentDictionary<string, Lazy<InferenceSession>> _sessions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly IInferenceExecutionContext _acceleration;
    private bool _disposed;

    public WindowsOnnxPpOcrRuntime(IInferenceExecutionContext acceleration)
    {
        _acceleration = acceleration ?? throw new ArgumentNullException(nameof(acceleration));
    }

    public Task<OcrTensorResult> RunAsync(
        string modelPath,
        string inputName,
        string outputName,
        float[] input,
        IReadOnlyList<int> dimensions,
        CancellationToken cancellationToken = default,
        InferenceAccelerationMode? accelerationMode = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputName);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputName);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(dimensions);
        cancellationToken.ThrowIfCancellationRequested();

        var mode = accelerationMode ?? _acceleration.CurrentMode;
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(accelerationMode));
        }

        if (mode == InferenceAccelerationMode.Cpu)
        {
            try
            {
                var result = RunCore(
                    modelPath,
                    inputName,
                    outputName,
                    input,
                    dimensions,
                    useGpu: false,
                    cancellationToken);
                _acceleration.ReportExecution("PpOcr", InferenceExecutionDevice.Cpu);
                return Task.FromResult(result);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _acceleration.ReportExecution(
                    "PpOcr",
                    InferenceExecutionDevice.Cpu,
                    failureCode: exception.GetType().Name);
                throw;
            }
        }

        var gpuDevice = GetGpuExecutionDevice(mode);
        try
        {
            var result = RunCore(
                modelPath,
                inputName,
                outputName,
                input,
                dimensions,
                useGpu: true,
                cancellationToken);
            _acceleration.ReportExecution("PpOcr", gpuDevice);
            return Task.FromResult(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception) when (mode == InferenceAccelerationMode.Automatic)
        {
            RemoveSession(modelPath, useGpu: true);
            try
            {
                var result = RunCore(
                    modelPath,
                    inputName,
                    outputName,
                    input,
                    dimensions,
                    useGpu: false,
                    cancellationToken);
                _acceleration.ReportExecution(
                    "PpOcr",
                    InferenceExecutionDevice.Cpu,
                    usedAutomaticFallback: true);
                return Task.FromResult(result);
            }
            catch (Exception cpuException) when (cpuException is not OperationCanceledException)
            {
                _acceleration.ReportExecution(
                    "PpOcr",
                    InferenceExecutionDevice.Cpu,
                    usedAutomaticFallback: true,
                    failureCode: cpuException.GetType().Name);
                throw;
            }
        }
        catch (Exception exception)
        {
            _acceleration.ReportExecution(
                "PpOcr",
                gpuDevice,
                failureCode: exception.GetType().Name);
            throw;
        }
    }

    private OcrTensorResult RunCore(
        string modelPath,
        string inputName,
        string outputName,
        float[] input,
        IReadOnlyList<int> dimensions,
        bool useGpu,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(modelPath);
        var sessionKey = CreateSessionKey(fullPath, useGpu);
        var session = _sessions.GetOrAdd(
            sessionKey,
            _ => new Lazy<InferenceSession>(
                () => CreateSession(fullPath, useGpu),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        if (!session.InputMetadata.ContainsKey(inputName)
            || !session.OutputMetadata.ContainsKey(outputName))
        {
            throw new InvalidDataException("The ONNX model signature does not match its package manifest.");
        }

        var tensor = new DenseTensor<float>(input, dimensions.ToArray());
        using var results = session.Run(
            [NamedOnnxValue.CreateFromTensor(inputName, tensor)],
            [outputName]);
        cancellationToken.ThrowIfCancellationRequested();
        var output = results.Single().AsTensor<float>();
        return new OcrTensorResult(
            output.ToArray(),
            output.Dimensions.ToArray());
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var session in _sessions.Values)
        {
            if (session.IsValueCreated)
            {
                session.Value.Dispose();
            }
        }

        _sessions.Clear();
    }

    private static InferenceSession CreateSession(string path, bool useGpu)
    {
        lock (EnvironmentGate)
        {
            if (!_environmentConfigured)
            {
                // ORT telemetry events are enabled by default on applicable
                // platforms. Configure the environment lazily so an app without
                // an enhanced OCR package does not load the runtime at startup.
                OrtEnv.Instance().DisableTelemetryEvents();
                _environmentConfigured = true;
            }
        }

        if (!useGpu)
        {
            return new InferenceSession(path);
        }

        using var options = new SessionOptions
        {
            EnableMemoryPattern = false,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
#if PICFORLATER_CUDA_RUNTIME
        CudaRuntimeDependencyLoader.Prepare();
        options.AppendExecutionProvider_CUDA(0);
#else
        options.AppendExecutionProvider_DML(0);
#endif
        return new InferenceSession(path, options);
    }

    private void RemoveSession(string modelPath, bool useGpu)
    {
        if (_sessions.TryRemove(
                CreateSessionKey(Path.GetFullPath(modelPath), useGpu),
                out var session)
            && session.IsValueCreated)
        {
            session.Value.Dispose();
        }
    }

    private static string CreateSessionKey(string path, bool useGpu) =>
        $"{(useGpu ? NativeGpuSessionKey : "cpu")}|{path}";

    private static InferenceExecutionDevice GetGpuExecutionDevice(InferenceAccelerationMode mode)
    {
#if PICFORLATER_CUDA_RUNTIME
        if (mode == InferenceAccelerationMode.DirectMlGpu)
        {
            throw new OcrProviderException("ocr.directml-runtime-unavailable", isRetryable: false);
        }

        return InferenceExecutionDevice.CudaGpu;
#else
        if (mode == InferenceAccelerationMode.CudaGpu)
        {
            throw new OcrProviderException("ocr.cuda-runtime-unavailable", isRetryable: false);
        }

        return InferenceExecutionDevice.DirectMlGpu;
#endif
    }

#if PICFORLATER_CUDA_RUNTIME
    private const string NativeGpuSessionKey = "cuda";
#else
    private const string NativeGpuSessionKey = "dml";
#endif
}
