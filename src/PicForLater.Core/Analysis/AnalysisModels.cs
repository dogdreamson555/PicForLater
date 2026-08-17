using PicForLater.Core.Images;

namespace PicForLater.Core.Analysis;

public enum AnalysisStage
{
    None = 0,
    Ocr = 1,
    DeterministicEntities = 2,
    Vision = 3,
    TextComposition = 4,
}

public enum AnalysisMode
{
    OcrOnly = 1,
    Balanced = 2,
    AlwaysEnhance = 3,
}

public enum AnalysisExecutionBackend
{
    Local = 0,
    RemoteApi = 1,
}

public enum RemoteInputMode
{
    LocalOcrText = 1,
    DirectImage = 2,
}

public enum ModelCapability
{
    Ocr = 1,
    VisionCaption = 2,
    TextComposition = 3,
    EntityExtraction = 4,
}

public enum InferenceAccelerationMode
{
    Automatic = 0,
    DirectMlGpu = 1,
    Cpu = 2,
    CudaGpu = 3,
}

public enum AnalysisExecutionLocation
{
    Local = 0,
    RemoteApi = 1,
}

public enum AnalysisOutputKind
{
    Unspecified = 0,
    OcrFacts = 1,
    DeterministicEntityCandidates = 2,
    RoutingDecision = 3,
    ModelGeneratedDraft = 4,
    ExtractiveDraft = 5,
}

public enum AnalysisStageOutcome
{
    Completed = 0,
    SkippedByRemoteDirectImage = 1,
}

public sealed record OcrBoundingBox(
    double X,
    double Y,
    double Width,
    double Height);

public sealed record OcrWord(
    string Text,
    OcrBoundingBox BoundingBox,
    double? Confidence);

public sealed record OcrLine(
    string Text,
    OcrBoundingBox BoundingBox,
    IReadOnlyList<OcrWord> Words,
    double? Confidence);

public sealed record AnalysisProvenance(
    string ProviderId,
    string? ModelId,
    string? ModelVersion,
    IReadOnlyDictionary<string, string> ModelFileHashes,
    string SchemaVersion,
    AnalysisExecutionLocation ExecutionLocation = AnalysisExecutionLocation.Local,
    AnalysisOutputKind OutputKind = AnalysisOutputKind.Unspecified,
    RemoteInputMode? RemoteInputMode = null,
    AnalysisStageOutcome StageOutcome = AnalysisStageOutcome.Completed);

public sealed record OcrDocument(
    string Text,
    IReadOnlyList<OcrLine> Lines,
    IReadOnlyList<string> LanguageTags,
    IReadOnlyList<string> Warnings,
    AnalysisProvenance Provenance,
    int ImageWidth,
    int ImageHeight);

public sealed record OcrProviderDescriptor(
    string ProviderId,
    string DisplayName,
    IReadOnlyList<string> SupportedLanguageTags,
    IReadOnlyList<string> SupportedScripts,
    bool SupportsMixedLanguages);

public sealed record OcrRequest(
    Func<CancellationToken, ValueTask<Stream>> OpenImageAsync,
    string OriginalFileName,
    int PixelWidth,
    int PixelHeight,
    IReadOnlyList<string> LanguageHints)
{
    public ManagedAnalysisImage? ManagedImage { get; init; }
}

public sealed record ManagedAnalysisImage(
    ManagedRelativePath RelativePath,
    Sha256Hash ContentHash);

public sealed record ExtractiveContentDraft(
    string Title,
    string Summary,
    IReadOnlyList<string> LanguageTags,
    IReadOnlyList<string> Warnings,
    AnalysisProvenance Provenance)
{
    public IReadOnlyList<Guid> SuggestedCategoryIds { get; init; } = [];

    public IReadOnlyList<EntityCandidateDraft> EntityCandidates { get; init; } = [];
}

public sealed record EntityCandidateDraft(
    string Kind,
    string RawText,
    string? NormalizedValue,
    string Evidence,
    string Source)
{
    public OcrBoundingBox? BoundingBox { get; init; }

    public DateTimeOffset? ReferenceTimeUtc { get; init; }

    public string? TimeZoneId { get; init; }

    public string? AmbiguityReason { get; init; }
}

public sealed record EntityExtractionResult(
    IReadOnlyList<EntityCandidateDraft> Candidates,
    IReadOnlyList<string> LanguageTags,
    IReadOnlyList<string> Warnings,
    AnalysisProvenance Provenance);

public sealed record AnalysisCategoryOption(Guid Id, string Name);

public sealed record AnalysisCompositionContext(
    IReadOnlyList<AnalysisCategoryOption> Categories);

public sealed record ModelSlotSelection(
    ModelCapability Capability,
    string ProviderId,
    string? PackageKey);

public sealed record ModelProfileSnapshot(
    AnalysisMode AnalysisMode,
    long Revision,
    IReadOnlyList<ModelSlotSelection> Slots)
{
    public AnalysisExecutionBackend ExecutionBackend { get; init; } =
        AnalysisExecutionBackend.Local;

    public RemoteInputMode? RemoteInputMode { get; init; }

    public RemoteApiProfileSnapshot? RemoteApiProfile { get; init; }

    public static ModelProfileSnapshot Default { get; } = new(
        AnalysisMode.Balanced,
        Revision: 0,
        [
            new ModelSlotSelection(ModelCapability.Ocr, "local.fallback-ocr", PackageKey: null),
            new ModelSlotSelection(ModelCapability.VisionCaption, "local.none", PackageKey: null),
            new ModelSlotSelection(ModelCapability.TextComposition, "local.extractive-text", PackageKey: null),
            new ModelSlotSelection(ModelCapability.EntityExtraction, "local.deterministic-entities", PackageKey: null),
        ]);

    public ModelSlotSelection GetSlot(ModelCapability capability) =>
        Slots.FirstOrDefault(slot => slot.Capability == capability)
        ?? throw new InvalidDataException("The model profile snapshot is missing a capability slot.");
}

public sealed record AnalysisRoutingRequest(
    AnalysisMode Mode,
    bool EnhancedProviderAvailable,
    OcrDocument OcrDocument);

public sealed record AnalysisRoutingDecision(
    bool RunEnhancedAnalysis,
    string ReasonCode,
    int OcrTextElementCount,
    double? MeanOcrConfidence);

public sealed record VisionAnalysisRequest(
    Func<CancellationToken, ValueTask<Stream>> OpenImageAsync,
    string OriginalFileName,
    OcrDocument OcrDocument,
    AnalysisCompositionContext CompositionContext,
    ModelProfileSnapshot ProfileSnapshot)
{
    public DateTimeOffset ReferenceTimeUtc { get; init; } = DateTimeOffset.UtcNow;

    public string TimeZoneId { get; init; } = TimeZoneInfo.Utc.Id;

    public ManagedAnalysisImage? ManagedImage { get; init; }
}

public sealed record VisionStructuredResult(
    IReadOnlyList<string> VisualFacts,
    ExtractiveContentDraft Draft,
    IReadOnlyList<string> LanguageTags,
    IReadOnlyList<string> Warnings,
    AnalysisProvenance Provenance);

public sealed record VisionStagePayload(
    AnalysisRoutingDecision Routing,
    VisionStructuredResult? Result)
{
    public string? FailureCode { get; init; }
}

public sealed record AnalysisJobLease(
    Guid JobId,
    Guid ImageItemId,
    AnalysisJobKind Kind,
    long InputRevision,
    int AttemptCount,
    AnalysisStage CurrentStage,
    ManagedRelativePath OriginalRelativePath,
    Sha256Hash ContentHash,
    string OriginalFileName,
    int PixelWidth,
    int PixelHeight,
    DateTimeOffset LeaseExpiresAtUtc,
    ModelProfileSnapshot ProfileSnapshot);

public sealed record AnalysisLeaseAttempt(
    AnalysisJobLease? Lease,
    DateTimeOffset? NextWakeAtUtc);

public sealed record AnalysisStageCheckpoint(
    Guid Id,
    Guid JobId,
    Guid ImageItemId,
    AnalysisStage Stage,
    long InputRevision,
    AnalysisProvenance Provenance,
    IReadOnlyList<string> LanguageTags,
    string PayloadJson,
    string FactText,
    IReadOnlyList<string> Warnings,
    DateTimeOffset GeneratedAtUtc);

public sealed record AnalysisCompletionFailure(string ErrorCode);

public sealed record DecodedOcrImage(
    byte[] RgbaPixels,
    int Width,
    int Height);

public sealed class RemoteVisionImageCopy : IAsyncDisposable
{
    public RemoteVisionImageCopy(
        Stream content,
        string mediaType,
        int pixelWidth,
        int pixelHeight,
        long byteLength)
    {
        Content = content ?? throw new ArgumentNullException(nameof(content));
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        if (!content.CanRead)
        {
            throw new ArgumentException("The image-copy stream must be readable.", nameof(content));
        }

        if (pixelWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelWidth));
        }

        if (pixelHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelHeight));
        }

        if (byteLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteLength));
        }

        MediaType = mediaType;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        ByteLength = byteLength;
    }

    public Stream Content { get; }

    public string MediaType { get; }

    public int PixelWidth { get; }

    public int PixelHeight { get; }

    public long ByteLength { get; }

    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

public sealed record OcrTensorResult(
    float[] Values,
    IReadOnlyList<int> Dimensions);
