namespace PicForLater.Core.Analysis;

public interface IOcrProvider
{
    OcrProviderDescriptor Descriptor { get; }

    ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    Task<OcrDocument> RecognizeAsync(
        OcrRequest request,
        CancellationToken cancellationToken = default);
}

public interface IOcrImageDecoder
{
    Task<DecodedOcrImage> DecodeAsync(
        Stream source,
        CancellationToken cancellationToken = default);
}

public interface IPpOcrV6InferenceRuntime : IDisposable
{
    Task<OcrTensorResult> RunAsync(
        string modelPath,
        string inputName,
        string outputName,
        float[] input,
        IReadOnlyList<int> dimensions,
        CancellationToken cancellationToken = default,
        InferenceAccelerationMode? accelerationMode = null);
}

public interface ITextComposer
{
    ExtractiveContentDraft Compose(string originalFileName, OcrDocument ocrDocument);
}

public interface IEntityExtractor
{
    EntityExtractionResult Extract(
        OcrDocument ocrDocument,
        DateTimeOffset referenceTimeUtc,
        string timeZoneId);
}

public interface IAnalysisRouter
{
    AnalysisRoutingDecision Decide(AnalysisRoutingRequest request);
}

public interface IVisionImagePreprocessor
{
    Task<Stream> CreateAnalysisCopyAsync(
        Stream source,
        CancellationToken cancellationToken = default);
}

public interface IRemoteVisionImagePreprocessor
{
    Task<RemoteVisionImageCopy> CreateRemoteAnalysisCopyAsync(
        Stream source,
        long maximumBytes,
        CancellationToken cancellationToken = default);
}

public interface IVisionCaptionProvider
{
    Task<bool> IsAvailableAsync(
        ModelProfileSnapshot profileSnapshot,
        CancellationToken cancellationToken = default);

    Task<VisionStructuredResult> AnalyzeAsync(
        VisionAnalysisRequest request,
        CancellationToken cancellationToken = default);
}

public interface IAnalysisProfileSnapshotProvider
{
    Task<ModelProfileSnapshot> GetCurrentSnapshotAsync(
        CancellationToken cancellationToken = default);
}

public interface IInferenceAccelerationModeProvider
{
    InferenceAccelerationMode CurrentMode { get; }
}

public enum InferenceExecutionDevice
{
    Cpu = 1,
    DirectMlGpu = 2,
    CudaGpu = 3,
}

public sealed record InferenceExecutionStatus(
    string Workload,
    InferenceExecutionDevice Device,
    bool UsedAutomaticFallback,
    string? FailureCode,
    DateTimeOffset UpdatedAtUtc);

public interface IInferenceExecutionContext : IInferenceAccelerationModeProvider
{
    InferenceExecutionStatus? LastExecutionStatus { get; }

    void ReportExecution(
        string workload,
        InferenceExecutionDevice device,
        bool usedAutomaticFallback = false,
        string? failureCode = null);
}

public interface IAnalysisQueueNotifier
{
    void Notify();
}

public interface IAnalysisJobStore
{
    Task<AnalysisLeaseAttempt> TryLeaseNextAsync(
        string workerId,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        int maximumAttempts,
        CancellationToken cancellationToken = default);

    Task<AnalysisStageCheckpoint?> GetCheckpointAsync(
        Guid jobId,
        AnalysisStage stage,
        CancellationToken cancellationToken = default);

    Task<AnalysisCompositionContext> GetCompositionContextAsync(
        Guid imageItemId,
        CancellationToken cancellationToken = default);

    Task SaveCheckpointAsync(
        string workerId,
        AnalysisStageCheckpoint checkpoint,
        DateTimeOffset leaseExpiresAtUtc,
        CancellationToken cancellationToken = default);

    Task CompleteAsync(
        string workerId,
        AnalysisJobLease lease,
        AnalysisStageCheckpoint compositionCheckpoint,
        ExtractiveContentDraft draft,
        DateTimeOffset completedAtUtc,
        AnalysisCompletionFailure? completionFailure = null,
        CancellationToken cancellationToken = default);

    Task FailAsync(
        string workerId,
        AnalysisJobLease lease,
        string errorCode,
        bool retryable,
        DateTimeOffset retryAtUtc,
        int maximumAttempts,
        DateTimeOffset failedAtUtc,
        CancellationToken cancellationToken = default);

    Task AbandonAsync(
        string workerId,
        AnalysisJobLease lease,
        DateTimeOffset retryAtUtc,
        CancellationToken cancellationToken = default);
}

public sealed class OcrProviderUnavailableException : Exception
{
    public OcrProviderUnavailableException(string errorCode)
        : base("The local OCR provider is unavailable.")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}

public sealed class OcrProviderException : Exception, IModelOperationFailure
{
    public OcrProviderException(string errorCode, bool isRetryable, Exception? innerException = null)
        : base("The local OCR provider failed.", innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ErrorCode = errorCode;
        IsRetryable = isRetryable;
    }

    public string ErrorCode { get; }

    public bool IsRetryable { get; }
}

public sealed class AnalysisLeaseLostException : Exception
{
    public AnalysisLeaseLostException()
        : base("The analysis lease is no longer owned by this worker.")
    {
    }
}
