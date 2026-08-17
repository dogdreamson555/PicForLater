using System.Buffers;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using PicForLater.Core.Images;

namespace PicForLater.Infrastructure.Storage;

/// <summary>
/// File-system implementation that stages data and atomically promotes validated
/// content into immutable, content-addressed originals on the same volume.
/// </summary>
public sealed class ManagedImageStorage : IManagedImageStorage
{
    private const int BufferSize = 128 * 1024;
    private const int MaximumThumbnailBytes = 16 * 1024 * 1024;
    public const long DefaultMaximumStagedBytes = 512L * 1024 * 1024;
    private readonly AppDataPaths _paths;

    public ManagedImageStorage(
        AppDataPaths paths,
        long maximumStagedBytes = DefaultMaximumStagedBytes)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        if (maximumStagedBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumStagedBytes));
        }

        MaximumStagedBytes = maximumStagedBytes;
        _paths.EnsureCreated();
    }

    public long MaximumStagedBytes { get; }

    public async Task<StagedImage> StageAsync(
        Stream source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException("The source stream must be readable.", nameof(source));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var relativePath = ManagedRelativePath.Parse($"staging/import-{Guid.NewGuid():N}.tmp");
        var absolutePath = _paths.Resolve(relativePath);
        byte[]? buffer = null;

        try
        {
            buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long byteLength = 0;

            await using (var destination = new FileStream(
                absolutePath,
                new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    BufferSize = BufferSize,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                }))
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var bytesRead = await source.ReadAsync(
                        buffer.AsMemory(0, BufferSize),
                        cancellationToken).ConfigureAwait(false);

                    if (bytesRead == 0)
                    {
                        break;
                    }

                    if (byteLength > MaximumStagedBytes - bytesRead)
                    {
                        throw new InvalidDataException("The image exceeds the maximum staged file size.");
                    }

                    byteLength += bytesRead;
                    hasher.AppendData(buffer, 0, bytesRead);
                    await destination.WriteAsync(
                        buffer.AsMemory(0, bytesRead),
                        cancellationToken).ConfigureAwait(false);
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                destination.Flush(flushToDisk: true);
            }

            return new StagedImage(
                relativePath,
                Sha256Hash.FromBytes(hasher.GetHashAndReset()),
                byteLength);
        }
        catch
        {
            TryDelete(relativePath);
            throw;
        }
        finally
        {
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
        }
    }

    public async Task<PromotedImage> PromoteAsync(
        StagedImage stagedImage,
        ManagedImageFormat format,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stagedImage);
        if (!stagedImage.RelativePath.IsUnder("staging"))
        {
            throw new ArgumentException("Only files in the managed staging area can be promoted.", nameof(stagedImage));
        }

        if (stagedImage.ByteLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stagedImage), "The staged byte length cannot be negative.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var stagedAbsolutePath = _paths.Resolve(stagedImage.RelativePath);
        var stagedContent = await ComputeHashAsync(stagedAbsolutePath, cancellationToken).ConfigureAwait(false);
        if (stagedContent.ByteLength != stagedImage.ByteLength
            || stagedContent.Hash != stagedImage.ContentHash)
        {
            throw new InvalidDataException("The staged file no longer matches its recorded content hash and length.");
        }

        stagedAbsolutePath = _paths.Resolve(stagedImage.RelativePath);
        var detectedFormat = await DetectFormatAsync(stagedAbsolutePath, cancellationToken).ConfigureAwait(false);
        if (detectedFormat != format)
        {
            throw new InvalidDataException("The staged file signature does not match the requested managed image format.");
        }

        var extension = format switch
        {
            ManagedImageFormat.Png => "png",
            ManagedImageFormat.Jpeg => "jpg",
            ManagedImageFormat.WebP => "webp",
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported managed image format."),
        };

        var hashText = stagedImage.ContentHash.Hex;
        var finalRelativePath = ManagedRelativePath.Parse(
            $"assets/originals/{hashText[..2]}/{hashText}.{extension}");
        var finalAbsolutePath = _paths.Resolve(finalRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(finalAbsolutePath)!);
        finalAbsolutePath = _paths.Resolve(finalRelativePath);

        if (File.Exists(finalAbsolutePath))
        {
            return await CompleteDuplicateAsync(
                stagedImage,
                finalRelativePath,
                cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        stagedAbsolutePath = _paths.Resolve(stagedImage.RelativePath);
        try
        {
            File.Move(stagedAbsolutePath, finalAbsolutePath, overwrite: false);
        }
        catch (IOException moveException)
        {
            finalAbsolutePath = _paths.Resolve(finalRelativePath);
            if (!File.Exists(finalAbsolutePath))
            {
                ExceptionDispatchInfo.Capture(moveException).Throw();
            }

            return await CompleteDuplicateAsync(
                stagedImage,
                finalRelativePath,
                cancellationToken,
                moveException).ConfigureAwait(false);
        }

        var promotedContent = await ComputeHashAsync(
            _paths.Resolve(finalRelativePath),
            cancellationToken).ConfigureAwait(false);
        if (promotedContent.Hash != stagedImage.ContentHash
            || promotedContent.ByteLength != stagedImage.ByteLength)
        {
            TryDelete(finalRelativePath);
            throw new InvalidDataException(
                "The promoted original failed post-move integrity verification and was not accepted.");
        }

        return new PromotedImage(
            finalRelativePath,
            stagedImage.ContentHash,
            stagedImage.ByteLength,
            AlreadyExisted: false);
    }

    public Task<Stream> OpenReadAsync(
        ManagedRelativePath relativePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        cancellationToken.ThrowIfCancellationRequested();

        Stream stream = new FileStream(
            _paths.Resolve(relativePath),
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = BufferSize,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });

        return Task.FromResult(stream);
    }

    public async Task<bool> VerifyAsync(
        ManagedRelativePath relativePath,
        Sha256Hash expectedHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(expectedHash);

        var absolutePath = _paths.Resolve(relativePath);
        if (!File.Exists(absolutePath))
        {
            return false;
        }

        try
        {
            var content = await ComputeHashAsync(absolutePath, cancellationToken).ConfigureAwait(false);
            return content.Hash == expectedHash;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    public Task DeleteStagingAsync(
        ManagedRelativePath relativePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        if (!relativePath.IsUnder("staging"))
        {
            throw new ArgumentException("Only managed staging files can be deleted by this operation.", nameof(relativePath));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var absolutePath = _paths.Resolve(relativePath);
        if (File.Exists(absolutePath))
        {
            File.Delete(absolutePath);
        }

        return Task.CompletedTask;
    }

    public async Task<ManagedRelativePath> StoreThumbnailAsync(
        Sha256Hash contentHash,
        ReadOnlyMemory<byte> pngBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contentHash);
        if (pngBytes.Length is < 8 or > MaximumThumbnailBytes
            || !pngBytes.Span[..8].SequenceEqual(
                new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            throw new InvalidDataException("The generated thumbnail is not a supported PNG payload.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var hashText = contentHash.Hex;
        var relativePath = ManagedRelativePath.Parse(
            $"cache/thumbnails/{hashText[..2]}/{hashText}.png");
        var absolutePath = _paths.Resolve(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        absolutePath = _paths.Resolve(relativePath);
        if (File.Exists(absolutePath))
        {
            return relativePath;
        }

        var temporaryRelativePath = ManagedRelativePath.Parse(
            $"staging/thumbnail-{Guid.NewGuid():N}.tmp");
        var temporaryPath = _paths.Resolve(temporaryRelativePath);
        try
        {
            await using (var destination = new FileStream(
                temporaryPath,
                new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    BufferSize = BufferSize,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                }))
            {
                await destination.WriteAsync(pngBytes, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                destination.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temporaryPath, absolutePath, overwrite: false);
            }
            catch (IOException) when (File.Exists(absolutePath))
            {
                File.Delete(temporaryPath);
            }

            return relativePath;
        }
        catch
        {
            TryDelete(temporaryRelativePath);
            throw;
        }
    }

    public Task DeleteManagedAsync(
        ManagedRelativePath relativePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        if (!relativePath.IsUnder("assets/originals")
            && !relativePath.IsUnder("cache/thumbnails"))
        {
            throw new ArgumentException(
                "Only managed originals and thumbnail cache files can be deleted by this operation.",
                nameof(relativePath));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var absolutePath = _paths.Resolve(relativePath);
        if (File.Exists(absolutePath))
        {
            File.Delete(absolutePath);
        }

        return Task.CompletedTask;
    }

    private static async Task<(Sha256Hash Hash, long ByteLength)> ComputeHashAsync(
        string absolutePath,
        CancellationToken cancellationToken)
    {
        byte[]? buffer = null;
        try
        {
            buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long byteLength = 0;

            await using var stream = new FileStream(
                absolutePath,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    BufferSize = BufferSize,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                });

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytesRead = await stream.ReadAsync(
                    buffer.AsMemory(0, BufferSize),
                    cancellationToken).ConfigureAwait(false);

                if (bytesRead == 0)
                {
                    break;
                }

                byteLength = checked(byteLength + bytesRead);
                hasher.AppendData(buffer, 0, bytesRead);
            }

            return (Sha256Hash.FromBytes(hasher.GetHashAndReset()), byteLength);
        }
        finally
        {
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
        }
    }

    private static async Task<ManagedImageFormat> DetectFormatAsync(
        string absolutePath,
        CancellationToken cancellationToken)
    {
        var header = new byte[12];
        await using var stream = new FileStream(
            absolutePath,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = header.Length,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });

        var bytesRead = 0;
        while (bytesRead < header.Length)
        {
            var read = await stream.ReadAsync(
                header.AsMemory(bytesRead, header.Length - bytesRead),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            bytesRead += read;
        }

        if (bytesRead >= 8
            && header.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return ManagedImageFormat.Png;
        }

        if (bytesRead >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            return ManagedImageFormat.Jpeg;
        }

        if (bytesRead >= 12
            && header.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            && header.AsSpan(8, 4).SequenceEqual("WEBP"u8))
        {
            return ManagedImageFormat.WebP;
        }

        throw new InvalidDataException("The staged file does not have a supported PNG, JPEG, or WebP signature.");
    }

    private async Task<PromotedImage> CompleteDuplicateAsync(
        StagedImage stagedImage,
        ManagedRelativePath finalRelativePath,
        CancellationToken cancellationToken,
        Exception? innerException = null)
    {
        var finalAbsolutePath = _paths.Resolve(finalRelativePath);
        var existingContent = await ComputeHashAsync(finalAbsolutePath, cancellationToken).ConfigureAwait(false);
        if (existingContent.Hash != stagedImage.ContentHash
            || existingContent.ByteLength != stagedImage.ByteLength)
        {
            throw new InvalidDataException(
                "An existing managed original does not match its content-addressed path. It was not overwritten.",
                innerException);
        }

        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(_paths.Resolve(stagedImage.RelativePath));
        return new PromotedImage(
            finalRelativePath,
            stagedImage.ContentHash,
            stagedImage.ByteLength,
            AlreadyExisted: true);
    }

    private void TryDelete(ManagedRelativePath relativePath)
    {
        try
        {
            var path = _paths.Resolve(relativePath);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A startup reconciliation pass can remove an abandoned staging file.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the original failure; never hide it behind best-effort cleanup.
        }
        catch (InvalidOperationException)
        {
            // Never follow a reparse point during best-effort cleanup.
        }
    }
}
