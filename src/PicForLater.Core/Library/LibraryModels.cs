using PicForLater.Core.Images;

namespace PicForLater.Core.Library;

public enum CategoryAssignmentSource
{
    Manual = 1,
    ModelSuggested = 2,
}

public enum ImageImportStatus
{
    Imported,
    Duplicate,
}

public enum PermanentDeleteStatus
{
    Completed,
    RetryRequired,
    NotFound,
}

public enum LibrarySortField
{
    CreatedAt = 1,
    Title = 2,
    ByteLength = 3,
    Category = 4,
}

public enum LibrarySortDirection
{
    Ascending = 1,
    Descending = 2,
}

public sealed record Category(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record ImageCategory(
    Category Category,
    CategoryAssignmentSource Source);

public sealed record LibraryEntry(
    ImageItem Item,
    ImageAsset Asset,
    IReadOnlyList<ImageCategory> Categories);

public sealed record LibraryQuery(
    string? SearchText = null,
    Guid? CategoryId = null,
    bool IsDeleted = false,
    LibrarySortField SortField = LibrarySortField.CreatedAt,
    LibrarySortDirection SortDirection = LibrarySortDirection.Descending,
    int Offset = 0,
    int Limit = 100);

public sealed record LibraryQueryResult(
    IReadOnlyList<LibraryEntry> Items,
    bool HasMore);

public sealed record ImageInspection(
    ManagedImageFormat Format,
    string MediaType,
    int PixelWidth,
    int PixelHeight,
    ReadOnlyMemory<byte> ThumbnailPng);

public sealed record ImageImportResult(
    ImageImportStatus Status,
    Guid ImageItemId);

public sealed record PermanentDeleteResult(
    PermanentDeleteStatus Status,
    string? ErrorCode = null);

public sealed class ImageImportException : Exception
{
    public ImageImportException(string errorCode, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ErrorCode = errorCode;
    }

    public ImageImportException(string errorCode, string message, Exception innerException)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}
