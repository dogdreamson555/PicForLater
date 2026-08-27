using System.Diagnostics;
using System.Threading.Channels;
using PicForLater.Analysis;
using PicForLater.Analysis.PpOcr;
using PicForLater.App.Services;
using PicForLater.Core.Analysis;
using PicForLater.Core.Images;
using PicForLater.Infrastructure.Analysis;
using PicForLater.Infrastructure.Storage;
using PicForLater.LocalInference.Protocol;

namespace PicForLater.LocalInference;

internal sealed class LocalInferenceWorkerHost : IAsyncDisposable
{
    private readonly Stream _pipe;
    private readonly int _expectedParentProcessId;
    private readonly WorkerInferenceExecutionContext _acceleration = new();
    private AppDataPaths? _paths;
    private ManagedImageStorage? _storage;
    private WindowsOnnxPpOcrRuntime? _ppOcrRuntime;
    private OnnxRuntimeGenAiQwenRuntime? _qwenRuntime;
    private WorkerAnalysisTemporaryDirectory? _analysisTemporaryDirectory;
    private IOcrProvider? _ocrProvider;
    private IVisionCaptionProvider? _visionProvider;
    private TimeSpan _idleTimeout;
    private int _protocolVersion;

    public LocalInferenceWorkerHost(Stream pipe, int expectedParentProcessId)
    {
        _pipe = pipe ?? throw new ArgumentNullException(nameof(pipe));
        if (expectedParentProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedParentProcessId));
        }

        _expectedParentProcessId = expectedParentProcessId;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await HandshakeAsync(cancellationToken).ConfigureAwait(false);
        var channel = Channel.CreateBounded<LocalInferenceEnvelope>(new BoundedChannelOptions(8)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        using var readerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var readerTask = ReadMessagesAsync(channel.Writer, readerCancellation.Token);
        Task<LocalInferenceEnvelope>? pendingRead = null;
        Task<LocalInferenceEnvelope>? activeOperation = null;
        CancellationTokenSource? activeCancellation = null;
        Guid activeRequestId = Guid.Empty;
        // The parent serializes inference calls, so activeOperation plus the bounded
        // channel are the complete worker-local request queue. Start the warm-window
        // countdown only when that queue has no active request.
        var idleSinceUtc = DateTimeOffset.UtcNow;
        try
        {
            while (true)
            {
                pendingRead ??= channel.Reader.ReadAsync(cancellationToken).AsTask();
                if (activeOperation is null)
                {
                    var remaining = _idleTimeout - (DateTimeOffset.UtcNow - idleSinceUtc);
                    if (remaining <= TimeSpan.Zero)
                    {
                        return;
                    }

                    var idleDelay = Task.Delay(remaining, cancellationToken);
                    var completed = await Task.WhenAny(pendingRead, idleDelay).ConfigureAwait(false);
                    if (completed == idleDelay)
                    {
                        await idleDelay.ConfigureAwait(false);
                        return;
                    }
                }
                else
                {
                    var completed = await Task.WhenAny(pendingRead, activeOperation).ConfigureAwait(false);
                    if (completed == activeOperation)
                    {
                        var response = await activeOperation.ConfigureAwait(false);
                        await LocalInferenceProtocol.WriteAsync(
                            _pipe,
                            response,
                            cancellationToken).ConfigureAwait(false);
                        activeCancellation?.Dispose();
                        activeCancellation = null;
                        activeOperation = null;
                        activeRequestId = Guid.Empty;
                        idleSinceUtc = DateTimeOffset.UtcNow;
                        continue;
                    }
                }

                LocalInferenceEnvelope message;
                try
                {
                    message = await pendingRead.ConfigureAwait(false);
                    pendingRead = null;
                }
                catch (ChannelClosedException)
                {
                    activeCancellation?.Cancel();
                    return;
                }

                ValidateEnvelope(message);
                switch (message.MessageType)
                {
                    case LocalInferenceMessageTypes.Request:
                        if (activeOperation is not null)
                        {
                            await WriteErrorAsync(
                                message,
                                "local-worker.busy",
                                isRetryable: true,
                                "protocol",
                                cancellationToken).ConfigureAwait(false);
                            break;
                        }

                        activeRequestId = message.RequestId;
                        activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        activeOperation = ExecuteRequestAsync(message, activeCancellation.Token);
                        break;
                    case LocalInferenceMessageTypes.Cancel:
                        if (message.RequestId == activeRequestId)
                        {
                            activeCancellation?.Cancel();
                        }
                        break;
                    case LocalInferenceMessageTypes.Shutdown:
                        activeCancellation?.Cancel();
                        if (activeOperation is not null)
                        {
                            await Task.WhenAny(
                                activeOperation,
                                Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None))
                                .ConfigureAwait(false);
                        }
                        return;
                    default:
                        throw new LocalInferenceProtocolException(
                            "local-worker.protocol-message-type-invalid");
                }
            }
        }
        finally
        {
            readerCancellation.Cancel();
            activeCancellation?.Cancel();
            activeCancellation?.Dispose();
            try
            {
                await readerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task HandshakeAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var envelope = await LocalInferenceProtocol.ReadAsync(_pipe, timeout.Token).ConfigureAwait(false)
            ?? throw new LocalInferenceProtocolException("local-worker.protocol-handshake-missing");
        if (envelope.MessageType != LocalInferenceMessageTypes.Hello
            || envelope.RequestId == Guid.Empty
            || envelope.Operation is not null)
        {
            throw new LocalInferenceProtocolException("local-worker.protocol-handshake-invalid");
        }

        var hello = LocalInferenceProtocol.ReadPayload<LocalInferenceHelloRequest>(envelope);
        _protocolVersion = LocalInferenceProtocol.NegotiateVersion(
            hello.MinimumVersion,
            hello.MaximumVersion);
        if (hello.ParentProcessId != _expectedParentProcessId
            || !IsProcessAlive(hello.ParentProcessId)
            || !Path.IsPathFullyQualified(hello.AppDataRootPath)
            || hello.IdleTimeoutSeconds is <= 0 or > LocalInferenceProtocol.MaximumIdleTimeoutSeconds)
        {
            throw new LocalInferenceProtocolException("local-worker.protocol-handshake-invalid");
        }

        _idleTimeout = TimeSpan.FromSeconds(hello.IdleTimeoutSeconds);
        _paths = new AppDataPaths(hello.AppDataRootPath);
        _storage = new ManagedImageStorage(_paths);
        _analysisTemporaryDirectory = await WorkerAnalysisTemporaryDirectory.CreateAsync(
                _paths,
                timeout.Token)
            .ConfigureAwait(false);
        CudaRuntimeDependencyLoader.ConfigureManagedRuntimeDirectory(
            _paths.ModelRuntimesDirectoryPath);
        _ppOcrRuntime = new WindowsOnnxPpOcrRuntime(_acceleration);
        _qwenRuntime = new OnnxRuntimeGenAiQwenRuntime(_acceleration);
        var modelPackages = new SqliteModelPackageService(
            _paths,
            new QwenModelPackageValidator(_qwenRuntime, _paths.AnalysisCacheDirectoryPath));
        var ppOcr = new PpOcrV6SmallProvider(
            Path.Combine(_paths.ModelPackagesDirectoryPath, "pp-ocrv6-small"),
            new WindowsOcrImageDecoder(),
            _ppOcrRuntime);
        _ocrProvider = new FallbackOcrProvider([ppOcr, new WindowsMediaOcrProvider()]);
        _visionProvider = new Qwen3VlProvider(
            modelPackages,
            _qwenRuntime,
            new WindowsImageContentProcessor(),
            _analysisTemporaryDirectory.DirectoryPath,
            _acceleration);

        var response = new LocalInferenceEnvelope(
            _protocolVersion,
            LocalInferenceMessageTypes.HelloResult,
            envelope.RequestId,
            Operation: null,
            LocalInferenceProtocol.ToPayload(new LocalInferenceHelloResponse(
                _protocolVersion,
                Environment.ProcessId,
                _qwenRuntime.SupportedExecutionProviders.Order(StringComparer.Ordinal).ToArray())));
        await LocalInferenceProtocol.WriteAsync(_pipe, response, timeout.Token).ConfigureAwait(false);
    }

    private async Task ReadMessagesAsync(
        ChannelWriter<LocalInferenceEnvelope> writer,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            while (true)
            {
                var message = await LocalInferenceProtocol.ReadAsync(_pipe, cancellationToken)
                    .ConfigureAwait(false);
                if (message is null)
                {
                    break;
                }

                await writer.WriteAsync(message, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            writer.TryComplete(failure);
        }
    }

    private async Task<LocalInferenceEnvelope> ExecuteRequestAsync(
        LocalInferenceEnvelope request,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload = request.Operation switch
            {
                LocalInferenceOperations.OcrAvailability =>
                    await HandleOcrAvailabilityAsync(request, cancellationToken).ConfigureAwait(false),
                LocalInferenceOperations.Recognize =>
                    await HandleRecognizeAsync(request, cancellationToken).ConfigureAwait(false),
                LocalInferenceOperations.VisionAvailability =>
                    await HandleVisionAvailabilityAsync(request, cancellationToken).ConfigureAwait(false),
                LocalInferenceOperations.AnalyzeVision =>
                    await HandleAnalyzeVisionAsync(request, cancellationToken).ConfigureAwait(false),
                LocalInferenceOperations.GenerateQwen =>
                    await HandleGenerateQwenAsync(request, cancellationToken).ConfigureAwait(false),
                LocalInferenceOperations.RunPpOcrTensor =>
                    await HandleRunPpOcrTensorAsync(request, cancellationToken).ConfigureAwait(false),
                _ => throw new LocalInferenceProtocolException("local-worker.protocol-operation-invalid"),
            };
            return new LocalInferenceEnvelope(
                _protocolVersion,
                LocalInferenceMessageTypes.Response,
                request.RequestId,
                request.Operation,
                payload)
            {
                ExecutionStatus = _acceleration.LastExecutionStatus,
            };
        }
        catch (Exception exception)
        {
            var error = Classify(exception);
            return new LocalInferenceEnvelope(
                _protocolVersion,
                LocalInferenceMessageTypes.Response,
                request.RequestId,
                request.Operation,
                LocalInferenceProtocol.ToPayload(new { }))
            {
                Error = error,
                ExecutionStatus = _acceleration.LastExecutionStatus,
            };
        }
    }

    private async Task<System.Text.Json.JsonElement> HandleOcrAvailabilityAsync(
        LocalInferenceEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var request = LocalInferenceProtocol.ReadPayload<LocalInferenceOcrAvailabilityRequest>(envelope);
        _acceleration.Begin(request.AccelerationMode);
        var available = await RequireOcr().IsAvailableAsync(cancellationToken).ConfigureAwait(false);
        return LocalInferenceProtocol.ToPayload(new LocalInferenceOcrAvailabilityResponse(available));
    }

    private async Task<System.Text.Json.JsonElement> HandleRecognizeAsync(
        LocalInferenceEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var request = LocalInferenceProtocol.ReadPayload<LocalInferenceRecognizeRequest>(envelope);
        _acceleration.Begin(request.AccelerationMode);
        var (relativePath, hash) = await ValidateImageAsync(request.Image, cancellationToken)
            .ConfigureAwait(false);
        var document = await RequireOcr().RecognizeAsync(
            new OcrRequest(
                token => new ValueTask<Stream>(RequireStorage().OpenReadAsync(relativePath, token)),
                request.OriginalFileName,
                request.PixelWidth,
                request.PixelHeight,
                request.LanguageHints)
            {
                ManagedImage = new ManagedAnalysisImage(relativePath, hash),
            },
            cancellationToken).ConfigureAwait(false);
        return LocalInferenceProtocol.ToPayload(new LocalInferenceRecognizeResponse(document));
    }

    private async Task<System.Text.Json.JsonElement> HandleVisionAvailabilityAsync(
        LocalInferenceEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var request = LocalInferenceProtocol.ReadPayload<LocalInferenceVisionAvailabilityRequest>(envelope);
        _acceleration.Begin(request.AccelerationMode);
        var available = await RequireVision().IsAvailableAsync(
            request.ProfileSnapshot,
            cancellationToken).ConfigureAwait(false);
        return LocalInferenceProtocol.ToPayload(new LocalInferenceVisionAvailabilityResponse(available));
    }

    private async Task<System.Text.Json.JsonElement> HandleAnalyzeVisionAsync(
        LocalInferenceEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var request = LocalInferenceProtocol.ReadPayload<LocalInferenceAnalyzeVisionRequest>(envelope);
        _acceleration.Begin(request.AccelerationMode);
        var (relativePath, hash) = await ValidateImageAsync(request.Image, cancellationToken)
            .ConfigureAwait(false);
        var result = await RequireVision().AnalyzeAsync(
            new VisionAnalysisRequest(
                token => new ValueTask<Stream>(RequireStorage().OpenReadAsync(relativePath, token)),
                request.OriginalFileName,
                request.OcrDocument,
                request.CompositionContext,
                request.ProfileSnapshot)
            {
                ReferenceTimeUtc = request.ReferenceTimeUtc,
                TimeZoneId = request.TimeZoneId,
                ManagedImage = new ManagedAnalysisImage(relativePath, hash),
            },
            cancellationToken).ConfigureAwait(false);
        return LocalInferenceProtocol.ToPayload(new LocalInferenceAnalyzeVisionResponse(result));
    }

    private async Task<System.Text.Json.JsonElement> HandleGenerateQwenAsync(
        LocalInferenceEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var request = LocalInferenceProtocol.ReadPayload<LocalInferenceGenerateQwenRequest>(envelope);
        _acceleration.Begin(request.AccelerationMode);
        var modelPath = ResolveAllowedPath(request.ModelDirectoryRelativePath, "model-packages");
        var imagePath = ResolveAllowedPath(request.ImageRelativePath, "cache");
        var output = await RequireQwenRuntime().GenerateAsync(
            modelPath,
            imagePath,
            request.Prompt,
            request.JsonSchema,
            request.MaximumOutputTokens,
            request.AccelerationMode,
            cancellationToken).ConfigureAwait(false);
        return LocalInferenceProtocol.ToPayload(new LocalInferenceGenerateQwenResponse(output));
    }

    private async Task<System.Text.Json.JsonElement> HandleRunPpOcrTensorAsync(
        LocalInferenceEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var request = LocalInferenceProtocol.ReadPayload<LocalInferenceRunPpOcrTensorRequest>(envelope);
        _acceleration.Begin(request.AccelerationMode);
        var relativePath = ManagedRelativePath.Parse(request.ModelRelativePath);
        if (!relativePath.IsUnder("model-packages")
            && !relativePath.IsUnder("staging"))
        {
            throw new InvalidDataException("The PP-OCR model path is outside an allowed root.");
        }

        var result = await RequirePpOcrRuntime().RunAsync(
            RequirePaths().Resolve(relativePath),
            request.InputName,
            request.OutputName,
            request.Input,
            request.Dimensions,
            cancellationToken,
            request.AccelerationMode).ConfigureAwait(false);
        return LocalInferenceProtocol.ToPayload(new LocalInferenceRunPpOcrTensorResponse(
            result.Values,
            result.Dimensions));
    }

    private async Task<(ManagedRelativePath Path, Sha256Hash Hash)> ValidateImageAsync(
        LocalInferenceImageReference image,
        CancellationToken cancellationToken)
    {
        var relativePath = ManagedRelativePath.Parse(image.RelativePath);
        if (!relativePath.IsUnder("assets"))
        {
            throw new InvalidDataException("The analysis image path is outside the managed assets root.");
        }
        var hash = Sha256Hash.Parse(image.ContentHash);
        if (!await RequireStorage().VerifyAsync(relativePath, hash, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("The analysis image failed integrity verification.");
        }

        return (relativePath, hash);
    }

    private string ResolveAllowedPath(string relativeValue, string firstSegment)
    {
        var relativePath = ManagedRelativePath.Parse(relativeValue);
        if (!relativePath.IsUnder(firstSegment))
        {
            throw new InvalidDataException("The inference path is outside an allowed managed root.");
        }

        return RequirePaths().Resolve(relativePath);
    }

    private void ValidateEnvelope(LocalInferenceEnvelope envelope)
    {
        if (envelope.ProtocolVersion != _protocolVersion || envelope.RequestId == Guid.Empty)
        {
            throw new LocalInferenceProtocolException("local-worker.protocol-envelope-invalid");
        }
    }

    private async Task WriteErrorAsync(
        LocalInferenceEnvelope request,
        string errorCode,
        bool isRetryable,
        string category,
        CancellationToken cancellationToken)
    {
        var response = new LocalInferenceEnvelope(
            _protocolVersion,
            LocalInferenceMessageTypes.Response,
            request.RequestId,
            request.Operation,
            LocalInferenceProtocol.ToPayload(new { }))
        {
            Error = new LocalInferenceError(errorCode, isRetryable, category),
        };
        await LocalInferenceProtocol.WriteAsync(_pipe, response, cancellationToken).ConfigureAwait(false);
    }

    private static LocalInferenceError Classify(Exception exception)
    {
        if (ContainsNativeRuntimeFailure(exception))
        {
            return new LocalInferenceError(
                "local-worker.native-runtime-missing",
                false,
                "dependency");
        }

        return exception switch
        {
            OcrProviderUnavailableException value => new(value.ErrorCode, false, "unavailable"),
            OcrProviderException value => new(value.ErrorCode, value.IsRetryable, "provider"),
            ModelPackageValidationException value => new(value.ErrorCode, false, "model-package"),
            QwenStructuredOutputException value => new(value.ErrorCode, false, "model-output"),
            LocalInferenceProtocolException value => new(value.ErrorCode, false, "protocol"),
            OperationCanceledException => new("local-worker.operation-canceled", true, "canceled"),
            FileNotFoundException => new("local-worker.file-not-found", false, "input"),
            InvalidDataException => new("local-worker.invalid-input", false, "input"),
            IOException => new("local-worker.io-failed", true, "io"),
            _ => new("local-worker.unexpected-failure", true, "unexpected"),
        };
    }

    private static bool ContainsNativeRuntimeFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            return !Process.GetProcessById(processId).HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private AppDataPaths RequirePaths() =>
        _paths ?? throw new InvalidOperationException("The worker handshake is incomplete.");

    private ManagedImageStorage RequireStorage() =>
        _storage ?? throw new InvalidOperationException("The worker handshake is incomplete.");

    private IOcrProvider RequireOcr() =>
        _ocrProvider ?? throw new InvalidOperationException("The worker handshake is incomplete.");

    private IVisionCaptionProvider RequireVision() =>
        _visionProvider ?? throw new InvalidOperationException("The worker handshake is incomplete.");

    private WindowsOnnxPpOcrRuntime RequirePpOcrRuntime() =>
        _ppOcrRuntime ?? throw new InvalidOperationException("The worker handshake is incomplete.");

    private OnnxRuntimeGenAiQwenRuntime RequireQwenRuntime() =>
        _qwenRuntime ?? throw new InvalidOperationException("The worker handshake is incomplete.");

    public async ValueTask DisposeAsync()
    {
        try
        {
            _ppOcrRuntime?.Dispose();
        }
        finally
        {
            try
            {
                if (_qwenRuntime is not null)
                {
                    await _qwenRuntime.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                try
                {
                    if (_analysisTemporaryDirectory is not null)
                    {
                        await _analysisTemporaryDirectory.DisposeAsync().ConfigureAwait(false);
                    }
                }
                finally
                {
                    _pipe.Dispose();
                }
            }
        }
    }
}
