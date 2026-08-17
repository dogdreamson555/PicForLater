using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PicForLater.Core.Analysis;
using PicForLater.Core.Images;
using PicForLater.Infrastructure.Analysis;
using PicForLater.Infrastructure.Storage;
using PicForLater.LocalInference.Protocol;

namespace PicForLater.App.Services;

public sealed class LocalInferenceWorkerClient :
    IOcrProvider,
    IVisionCaptionProvider,
    IQwenGenerationRuntime,
    IPpOcrV6InferenceRuntime,
    IAsyncDisposable
{
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CancellationGracePeriod = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ShutdownGracePeriod = TimeSpan.FromSeconds(3);
    private readonly AppDataPaths _paths;
    private readonly IInferenceAccelerationPreferenceService _acceleration;
    private readonly LocalInferenceComponentLocator _componentLocator;
    private readonly TimeSpan _idleTimeout;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private WorkerJobObject? _jobObject;
    private NamedPipeServerStream? _pipe;
    private Process? _process;
    private int _protocolVersion;
    private bool _disposed;

    public LocalInferenceWorkerClient(
        AppDataPaths paths,
        IInferenceAccelerationPreferenceService acceleration,
        LocalInferenceComponentLocator componentLocator,
        TimeSpan? idleTimeout = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _acceleration = acceleration ?? throw new ArgumentNullException(nameof(acceleration));
        _componentLocator = componentLocator
            ?? throw new ArgumentNullException(nameof(componentLocator));
        _idleTimeout = idleTimeout ?? DefaultIdleTimeout;
        if (_idleTimeout <= TimeSpan.Zero
            || _idleTimeout > TimeSpan.FromSeconds(LocalInferenceProtocol.MaximumIdleTimeoutSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(idleTimeout));
        }
    }

    public OcrProviderDescriptor Descriptor { get; } = new(
        "local.worker-ocr",
        "Local inference worker OCR",
        ["und", "zh-Hans", "zh-Hant", "en", "ja"],
        ["Hans", "Hant", "Jpan", "Latn"],
        SupportsMixedLanguages: true);

#if PICFORLATER_CUDA_RUNTIME
    public IReadOnlySet<string> SupportedExecutionProviders { get; } =
        new HashSet<string>(["CPU", "CUDA"], StringComparer.Ordinal);
#else
    public IReadOnlySet<string> SupportedExecutionProviders { get; } =
        new HashSet<string>(["CPU", "DirectML"], StringComparer.Ordinal);
#endif

    public async ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (await _componentLocator.LocateAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            return false;
        }

        try
        {
            var response = await ExecuteAsync<
                    LocalInferenceOcrAvailabilityRequest,
                    LocalInferenceOcrAvailabilityResponse>(
                    LocalInferenceOperations.OcrAvailability,
                    new LocalInferenceOcrAvailabilityRequest(_acceleration.CurrentMode),
                    mapUnavailable: true,
                    cancellationToken)
                .ConfigureAwait(false);
            return response.IsAvailable;
        }
        catch (Exception exception) when (exception is OcrProviderUnavailableException
                                          or OcrProviderException
                                          or IOException
                                          or UnauthorizedAccessException
                                          or InvalidOperationException
                                          or Win32Exception)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public async Task<OcrDocument> RecognizeAsync(
        OcrRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var image = RequireManagedImage(request.ManagedImage);
        var response = await ExecuteAsync<
                LocalInferenceRecognizeRequest,
                LocalInferenceRecognizeResponse>(
                LocalInferenceOperations.Recognize,
                new LocalInferenceRecognizeRequest(
                    image,
                    request.OriginalFileName,
                    request.PixelWidth,
                    request.PixelHeight,
                    request.LanguageHints,
                    _acceleration.CurrentMode),
                mapUnavailable: true,
                cancellationToken)
            .ConfigureAwait(false);
        return response.Document;
    }

    public async Task<bool> IsAvailableAsync(
        ModelProfileSnapshot profileSnapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profileSnapshot);
        if (await _componentLocator.LocateAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            return false;
        }

        try
        {
            var response = await ExecuteAsync<
                    LocalInferenceVisionAvailabilityRequest,
                    LocalInferenceVisionAvailabilityResponse>(
                    LocalInferenceOperations.VisionAvailability,
                    new LocalInferenceVisionAvailabilityRequest(
                        profileSnapshot,
                        _acceleration.CurrentMode),
                    mapUnavailable: true,
                    cancellationToken)
                .ConfigureAwait(false);
            return response.IsAvailable;
        }
        catch (Exception exception) when (exception is OcrProviderUnavailableException
                                          or OcrProviderException
                                          or IOException
                                          or UnauthorizedAccessException
                                          or InvalidOperationException
                                          or Win32Exception)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public async Task<VisionStructuredResult> AnalyzeAsync(
        VisionAnalysisRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var image = RequireManagedImage(request.ManagedImage);
        var response = await ExecuteAsync<
                LocalInferenceAnalyzeVisionRequest,
                LocalInferenceAnalyzeVisionResponse>(
                LocalInferenceOperations.AnalyzeVision,
                new LocalInferenceAnalyzeVisionRequest(
                    image,
                    request.OriginalFileName,
                    request.OcrDocument,
                    request.CompositionContext,
                    request.ProfileSnapshot,
                    request.ReferenceTimeUtc,
                    request.TimeZoneId,
                    _acceleration.CurrentMode),
                mapUnavailable: true,
                cancellationToken)
            .ConfigureAwait(false);
        return response.Result;
    }

    public async Task<string> GenerateAsync(
        string modelDirectoryPath,
        string imageFilePath,
        string prompt,
        string jsonSchema,
        int maximumOutputTokens,
        InferenceAccelerationMode accelerationMode,
        CancellationToken cancellationToken = default)
    {
        var response = await ExecuteAsync<
                LocalInferenceGenerateQwenRequest,
                LocalInferenceGenerateQwenResponse>(
                LocalInferenceOperations.GenerateQwen,
                new LocalInferenceGenerateQwenRequest(
                    ToManagedRelativePath(modelDirectoryPath).Value,
                    ToManagedRelativePath(imageFilePath).Value,
                    prompt,
                    jsonSchema,
                    maximumOutputTokens,
                    accelerationMode),
                mapUnavailable: false,
                cancellationToken)
            .ConfigureAwait(false);
        return response.Output;
    }

    public async Task<OcrTensorResult> RunAsync(
        string modelPath,
        string inputName,
        string outputName,
        float[] input,
        IReadOnlyList<int> dimensions,
        CancellationToken cancellationToken = default,
        InferenceAccelerationMode? accelerationMode = null)
    {
        var response = await ExecuteAsync<
                LocalInferenceRunPpOcrTensorRequest,
                LocalInferenceRunPpOcrTensorResponse>(
                LocalInferenceOperations.RunPpOcrTensor,
                new LocalInferenceRunPpOcrTensorRequest(
                    ToManagedRelativePath(modelPath).Value,
                    inputName,
                    outputName,
                    input,
                    dimensions,
                    accelerationMode ?? _acceleration.CurrentMode),
                mapUnavailable: false,
                cancellationToken)
            .ConfigureAwait(false);
        return new OcrTensorResult(response.Values, response.Dimensions);
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var ownsOperationGate = await _operationGate
            .WaitAsync(ShutdownGracePeriod)
            .ConfigureAwait(false);
        if (ownsOperationGate)
        {
            await StopWorkerAsync(graceful: true).ConfigureAwait(false);
        }
        else
        {
            await StopWorkerAsync(graceful: false).ConfigureAwait(false);
            // Killing the worker breaks the response pipe and lets the in-flight
            // proxy call leave its finally block. Take ownership before disposing
            // the semaphore so that call cannot release an already-disposed gate.
            ownsOperationGate = await _operationGate
                .WaitAsync(ShutdownGracePeriod)
                .ConfigureAwait(false);
        }

        _jobObject?.Dispose();
        if (ownsOperationGate)
        {
            _operationGate.Dispose();
        }
    }

    public async ValueTask<IAsyncDisposable> AcquireComponentMaintenanceAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopWorkerAsync(graceful: true).ConfigureAwait(false);
            return new ComponentMaintenanceLease(_operationGate);
        }
        catch
        {
            _operationGate.Release();
            throw;
        }
    }

    private async Task<TResponse> ExecuteAsync<TRequest, TResponse>(
        string operation,
        TRequest payload,
        bool mapUnavailable,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            var pipe = _pipe ?? throw CreateWorkerFailure("local-worker.pipe-unavailable", true);
            var requestId = Guid.NewGuid();
            var envelope = new LocalInferenceEnvelope(
                _protocolVersion,
                LocalInferenceMessageTypes.Request,
                requestId,
                operation,
                LocalInferenceProtocol.ToPayload(payload));
            try
            {
                await LocalInferenceProtocol.WriteAsync(pipe, envelope, cancellationToken)
                    .ConfigureAwait(false);
                var responseTask = LocalInferenceProtocol.ReadAsync(pipe, CancellationToken.None).AsTask();
                LocalInferenceEnvelope? response;
                if (!cancellationToken.CanBeCanceled)
                {
                    response = await responseTask.ConfigureAwait(false);
                }
                else
                {
                    var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    var completed = await Task.WhenAny(responseTask, cancellationTask).ConfigureAwait(false);
                    if (completed == cancellationTask)
                    {
                        await CancelAndStopIfNeededAsync(requestId, responseTask).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    response = await responseTask.ConfigureAwait(false);
                }

                if (response is null)
                {
                    throw CreateWorkerFailure("local-worker.process-exited", true);
                }
                ValidateResponse(response, requestId, operation, _protocolVersion);
                ApplyExecutionStatus(response.ExecutionStatus);
                if (response.Error is not null)
                {
                    throw MapError(response.Error, mapUnavailable);
                }

                return LocalInferenceProtocol.ReadPayload<TResponse>(response);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OcrProviderException)
            {
                throw;
            }
            catch (OcrProviderUnavailableException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException
                                              or EndOfStreamException
                                              or LocalInferenceProtocolException
                                              or ObjectDisposedException)
            {
                await StopWorkerAsync(graceful: false).ConfigureAwait(false);
                throw CreateWorkerFailure(
                    exception is LocalInferenceProtocolException protocol
                        ? protocol.ErrorCode
                        : "local-worker.pipe-broken",
                    isRetryable: true,
                    exception);
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_pipe is { IsConnected: true } && _process is { HasExited: false })
        {
            return;
        }

        await StopWorkerAsync(graceful: false).ConfigureAwait(false);
        var component = await _componentLocator.LocateAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new OcrProviderUnavailableException("local-worker.component-unavailable");
        _jobObject ??= new WorkerJobObject();

        var pipeName = $"PicForLater.LocalInference.{Environment.ProcessId}.{Guid.NewGuid():N}";
        var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Process? process = null;
        try
        {
            var startInfo = new ProcessStartInfo(component.WorkerPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = component.DirectoryPath,
            };
            startInfo.ArgumentList.Add("--pipe");
            startInfo.ArgumentList.Add(pipeName);
            startInfo.ArgumentList.Add("--parent-pid");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            process = Process.Start(startInfo)
                ?? throw CreateWorkerFailure("local-worker.start-failed", true);
            _jobObject.Assign(process);

            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectTimeout.CancelAfter(ConnectTimeout);
            await pipe.WaitForConnectionAsync(connectTimeout.Token).ConfigureAwait(false);
            var helloId = Guid.NewGuid();
            var hello = new LocalInferenceEnvelope(
                LocalInferenceProtocol.CurrentVersion,
                LocalInferenceMessageTypes.Hello,
                helloId,
                Operation: null,
                LocalInferenceProtocol.ToPayload(new LocalInferenceHelloRequest(
                    LocalInferenceProtocol.MinimumSupportedVersion,
                    LocalInferenceProtocol.CurrentVersion,
                    Environment.ProcessId,
                    _paths.RootPath,
                    checked((int)Math.Ceiling(_idleTimeout.TotalSeconds)))));
            await LocalInferenceProtocol.WriteAsync(pipe, hello, connectTimeout.Token)
                .ConfigureAwait(false);
            var response = await LocalInferenceProtocol.ReadAsync(pipe, connectTimeout.Token)
                .ConfigureAwait(false)
                ?? throw CreateWorkerFailure("local-worker.handshake-missing", true);
            if (response.MessageType != LocalInferenceMessageTypes.HelloResult
                || response.RequestId != helloId)
            {
                throw CreateWorkerFailure("local-worker.handshake-invalid", false);
            }
            if (response.Error is not null)
            {
                throw CreateWorkerFailure(
                    response.Error.ErrorCode,
                    response.Error.IsRetryable);
            }

            var result = LocalInferenceProtocol.ReadPayload<LocalInferenceHelloResponse>(response);
            _protocolVersion = LocalInferenceProtocol.NegotiateVersion(
                result.SelectedVersion,
                result.SelectedVersion);
            if (result.WorkerProcessId != process.Id
                || !SupportedExecutionProviders.SetEquals(result.SupportedExecutionProviders))
            {
                throw CreateWorkerFailure("local-worker.handshake-capability-mismatch", false);
            }

            _pipe = pipe;
            _process = process;
        }
        catch
        {
            pipe.Dispose();
            if (process is { HasExited: false })
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }
            }
            process?.Dispose();
            throw;
        }
    }

    private async Task CancelAndStopIfNeededAsync(
        Guid requestId,
        Task<LocalInferenceEnvelope?> responseTask)
    {
        var pipe = _pipe;
        var process = _process;
        if (pipe is not null && pipe.IsConnected)
        {
            try
            {
                using var sendTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                await LocalInferenceProtocol.WriteAsync(
                    pipe,
                    new LocalInferenceEnvelope(
                        _protocolVersion,
                        LocalInferenceMessageTypes.Cancel,
                        requestId,
                        Operation: null,
                        LocalInferenceProtocol.ToPayload(new { })),
                    sendTimeout.Token).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        var exitTask = process is null
            ? Task.CompletedTask
            : process.WaitForExitAsync(CancellationToken.None);
        var completed = await Task.WhenAny(
            responseTask,
            exitTask,
            Task.Delay(CancellationGracePeriod)).ConfigureAwait(false);
        if (completed != responseTask)
        {
            await StopWorkerAsync(graceful: false).ConfigureAwait(false);
        }
    }

    private async Task StopWorkerAsync(bool graceful)
    {
        var pipe = _pipe;
        var process = _process;
        var protocolVersion = _protocolVersion;
        _pipe = null;
        _process = null;
        _protocolVersion = 0;
        if (graceful && pipe is { IsConnected: true } && process is { HasExited: false })
        {
            try
            {
                using var timeout = new CancellationTokenSource(ShutdownGracePeriod);
                await LocalInferenceProtocol.WriteAsync(
                    pipe,
                    new LocalInferenceEnvelope(
                        protocolVersion <= 0 ? LocalInferenceProtocol.CurrentVersion : protocolVersion,
                        LocalInferenceMessageTypes.Shutdown,
                        Guid.NewGuid(),
                        Operation: null,
                        LocalInferenceProtocol.ToPayload(new { })),
                    timeout.Token).ConfigureAwait(false);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        if (process is { HasExited: false })
        {
            try
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        pipe?.Dispose();
        process?.Dispose();
    }

    private void ApplyExecutionStatus(InferenceExecutionStatus? status)
    {
        if (status is null)
        {
            return;
        }

        _acceleration.ReportExecution(
            status.Workload,
            status.Device,
            status.UsedAutomaticFallback,
            status.FailureCode);
    }

    private static void ValidateResponse(
        LocalInferenceEnvelope response,
        Guid requestId,
        string operation,
        int protocolVersion)
    {
        if (response.ProtocolVersion != protocolVersion
            || response.MessageType != LocalInferenceMessageTypes.Response
            || response.RequestId != requestId
            || response.Operation != operation)
        {
            throw new LocalInferenceProtocolException("local-worker.protocol-response-invalid");
        }
    }

    private static Exception MapError(LocalInferenceError error, bool mapUnavailable)
    {
        if (mapUnavailable && error.Category == "unavailable")
        {
            return new OcrProviderUnavailableException(error.ErrorCode);
        }

        return CreateWorkerFailure(error.ErrorCode, error.IsRetryable);
    }

    private static OcrProviderException CreateWorkerFailure(
        string errorCode,
        bool isRetryable,
        Exception? innerException = null) =>
        new(errorCode, isRetryable, innerException);

    public static string GetProcessArchitecture() =>
        RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException(
                "Local inference components are supported only on x64 and ARM64."),
        };

    private sealed class ComponentMaintenanceLease(SemaphoreSlim operationGate) : IAsyncDisposable
    {
        private SemaphoreSlim? _operationGate = operationGate;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _operationGate, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }

    private static LocalInferenceImageReference RequireManagedImage(ManagedAnalysisImage? image)
    {
        if (image is null)
        {
            throw new OcrProviderException(
                "local-worker.managed-image-required",
                isRetryable: false);
        }

        return new LocalInferenceImageReference(
            image.RelativePath.Value,
            image.ContentHash.Hex);
    }

    private ManagedRelativePath ToManagedRelativePath(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        var fullPath = Path.GetFullPath(absolutePath);
        var relative = Path.GetRelativePath(_paths.RootPath, fullPath);
        var managed = ManagedRelativePath.Parse(relative);
        var resolved = _paths.Resolve(managed);
        if (!resolved.Equals(fullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new OcrProviderException("local-worker.path-outside-root", isRetryable: false);
        }

        return managed;
    }
}

internal sealed class WorkerJobObject : IDisposable
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectExtendedLimitInformationClass = 9;
    private readonly SafeFileHandle _handle;

    public WorkerJobObject()
    {
        _handle = CreateJobObject(nint.Zero, null);
        if (_handle.IsInvalid)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        var information = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose,
            },
        };
        var length = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var pointer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(information, pointer, fDeleteOld: false);
            if (!SetInformationJobObject(
                    _handle,
                    JobObjectExtendedLimitInformationClass,
                    pointer,
                    (uint)length))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    public void Assign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!AssignProcessToJobObject(_handle, process.Handle))
        {
            var errorCode = Marshal.GetLastWin32Error();
            throw new System.ComponentModel.Win32Exception(
                errorCode,
                "Could not assign the local inference worker to its lifetime job.");
        }
    }

    public void Dispose() => _handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(nint jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job,
        int informationClass,
        nint information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, nint process);

}
