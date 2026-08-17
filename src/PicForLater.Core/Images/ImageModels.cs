using PicForLater.Core.Analysis;

namespace PicForLater.Core.Images;

public enum ManagedImageFormat
{
    Png = 1,
    Jpeg = 2,
    WebP = 3,
}

public enum ImageSourceKind
{
    File = 1,
    Clipboard = 2,
}

public enum ContentFieldSource
{
    Fallback = 1,
    ModelSuggested = 2,
    User = 3,
}

public enum AnalysisState
{
    Pending = 1,
    Running = 2,
    NeedsAttention = 3,
    Completed = 4,
}

public enum ImportJobState
{
    Staged = 1,
    Prepared = 2,
    Completed = 3,
    Duplicate = 4,
    Failed = 5,
    Cancelled = 6,
}

public enum AnalysisJobState
{
    Queued = 1,
    Running = 2,
    Retryable = 3,
    Completed = 4,
    Failed = 5,
    Cancelled = 6,
}

public enum AnalysisJobKind
{
    Initial = 1,
    Reanalysis = 2,
}

public sealed record StagedImage(
    ManagedRelativePath RelativePath,
    Sha256Hash ContentHash,
    long ByteLength);

public sealed record PromotedImage(
    ManagedRelativePath RelativePath,
    Sha256Hash ContentHash,
    long ByteLength,
    bool AlreadyExisted);

public sealed record ImageAsset(
    Guid Id,
    Sha256Hash ContentHash,
    ManagedRelativePath OriginalRelativePath,
    ManagedRelativePath? ThumbnailRelativePath,
    string MediaType,
    long ByteLength,
    int PixelWidth,
    int PixelHeight,
    DateTimeOffset CreatedAtUtc);

public sealed record ImageItem(
    Guid Id,
    Guid AssetId,
    string OriginalFileName,
    ImageSourceKind SourceKind,
    string Title,
    string Summary,
    ContentFieldSource TitleSource,
    ContentFieldSource SummarySource,
    AnalysisState AnalysisState,
    long Revision,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? DeletedAtUtc);

public sealed record ImportJob(
    Guid Id,
    ManagedRelativePath? StagingRelativePath,
    ManagedRelativePath? FinalRelativePath,
    string OriginalFileName,
    ImageSourceKind SourceKind,
    ImportJobState State,
    Sha256Hash? ContentHash,
    Guid? ImageItemId,
    int AttemptCount,
    DateTimeOffset? LeaseExpiresAtUtc,
    string? LastErrorCode,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc);

public sealed record AnalysisJob(
    Guid Id,
    Guid ImageItemId,
    AnalysisJobKind Kind,
    long InputRevision,
    AnalysisJobState State,
    int AttemptCount,
    DateTimeOffset NotBeforeUtc,
    DateTimeOffset? LeaseExpiresAtUtc,
    string? LastErrorCode,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    ModelProfileSnapshot? ProfileSnapshot = null);
