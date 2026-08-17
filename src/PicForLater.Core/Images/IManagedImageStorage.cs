namespace PicForLater.Core.Images;

/// <summary>
/// Stores immutable originals beneath the application's managed-data root.
/// Implementations must never overwrite an existing original.
/// </summary>
public interface IManagedImageStorage
{
    long MaximumStagedBytes { get; }

    Task<StagedImage> StageAsync(Stream source, CancellationToken cancellationToken = default);

    Task<PromotedImage> PromoteAsync(
        StagedImage stagedImage,
        ManagedImageFormat format,
        CancellationToken cancellationToken = default);

    Task<Stream> OpenReadAsync(
        ManagedRelativePath relativePath,
        CancellationToken cancellationToken = default);

    Task<bool> VerifyAsync(
        ManagedRelativePath relativePath,
        Sha256Hash expectedHash,
        CancellationToken cancellationToken = default);

    Task DeleteStagingAsync(
        ManagedRelativePath relativePath,
        CancellationToken cancellationToken = default);

    Task<ManagedRelativePath> StoreThumbnailAsync(
        Sha256Hash contentHash,
        ReadOnlyMemory<byte> pngBytes,
        CancellationToken cancellationToken = default);

    Task DeleteManagedAsync(
        ManagedRelativePath relativePath,
        CancellationToken cancellationToken = default);
}
