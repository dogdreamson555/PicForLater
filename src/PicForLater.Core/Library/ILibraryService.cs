using PicForLater.Core.Images;

namespace PicForLater.Core.Library;

public interface ILibraryService
{
    Task<LibraryQueryResult> QueryAsync(
        LibraryQuery query,
        CancellationToken cancellationToken = default);

    Task<LibraryEntry?> GetAsync(
        Guid imageItemId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<Guid, string>> GetSummariesAsync(
        IReadOnlyCollection<Guid> imageItemIds,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Category>> GetCategoriesAsync(
        CancellationToken cancellationToken = default);

    Task<Category> CreateCategoryAsync(
        string name,
        CancellationToken cancellationToken = default);

    Task RenameCategoryAsync(
        Guid categoryId,
        string name,
        CancellationToken cancellationToken = default);

    Task DeleteCategoryAsync(
        Guid categoryId,
        CancellationToken cancellationToken = default);

    Task SetCategoryAssignmentAsync(
        Guid imageItemId,
        Guid categoryId,
        bool isAssigned,
        CancellationToken cancellationToken = default);

    Task UpdateUserFieldsAsync(
        Guid imageItemId,
        string title,
        string summary,
        CancellationToken cancellationToken = default);

    Task SoftDeleteAsync(
        Guid imageItemId,
        CancellationToken cancellationToken = default);

    Task RestoreAsync(
        Guid imageItemId,
        CancellationToken cancellationToken = default);

    Task<PermanentDeleteResult> PermanentlyDeleteAsync(
        Guid imageItemId,
        CancellationToken cancellationToken = default);
}

public interface IImageImportService
{
    Task<ImageImportResult> ImportAsync(
        Stream source,
        string originalFileName,
        ImageSourceKind sourceKind,
        ManagedImageFormat? expectedFormat = null,
        CancellationToken cancellationToken = default);
}

public interface IImageContentProcessor
{
    Task<ImageInspection> InspectAndCreateThumbnailAsync(
        Stream source,
        CancellationToken cancellationToken = default);
}
