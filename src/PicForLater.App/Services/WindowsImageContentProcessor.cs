using System.Runtime.InteropServices.WindowsRuntime;
using PicForLater.Core.Analysis;
using PicForLater.Core.Images;
using PicForLater.Core.Library;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace PicForLater.App.Services;

/// <summary>
/// Uses Windows Imaging Component through Windows.Graphics.Imaging. It decodes
/// untrusted image bytes locally, respects EXIF orientation, and emits a bounded
/// PNG thumbnail without modifying the immutable original.
/// </summary>
public sealed class WindowsImageContentProcessor :
    IImageContentProcessor,
    IVisionImagePreprocessor,
    IRemoteVisionImagePreprocessor
{
    private const uint ThumbnailLongestEdge = 320;
    private const uint LocalVisionAnalysisLongestEdge = 1280;
    private const ulong RemoteVisionMaximumPixelCount = 16_000_000;
    private const int MaximumRemoteEncodingAttempts = 8;
    private const ulong MaximumPixelCount = 100_000_000;

    public async Task<ImageInspection> InspectAndCreateThumbnailAsync(
        Stream source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek)
        {
            throw new ArgumentException("The image stream must be readable and seekable.", nameof(source));
        }

        var format = await DetectFormatAsync(source, cancellationToken).ConfigureAwait(false);
        source.Position = 0;
        using var randomAccessStream = source.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(randomAccessStream).AsTask(cancellationToken);
        ValidateDimensions(decoder);
        var width = checked((int)decoder.OrientedPixelWidth);
        var height = checked((int)decoder.OrientedPixelHeight);
        var thumbnail = await EncodePngAsync(
            decoder,
            ThumbnailLongestEdge,
            preserveSourceDpi: true,
            cancellationToken).ConfigureAwait(false);
        return new ImageInspection(
            format,
            format switch
            {
                ManagedImageFormat.Png => "image/png",
                ManagedImageFormat.Jpeg => "image/jpeg",
                ManagedImageFormat.WebP => "image/webp",
                _ => throw new InvalidDataException("Unsupported decoded image format."),
            },
            width,
            height,
            thumbnail);
    }

    public async Task<MemoryStream> NormalizeToPngAsync(
        Stream source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var randomAccessStream = source.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(randomAccessStream).AsTask(cancellationToken);
        ValidateDimensions(decoder);
        var bytes = await EncodePngAsync(
            decoder,
            longestEdge: null,
            preserveSourceDpi: true,
            cancellationToken).ConfigureAwait(false);
        return new MemoryStream(bytes, writable: false);
    }

    public async Task<Stream> CreateAnalysisCopyAsync(
        Stream source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var randomAccessStream = source.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(randomAccessStream).AsTask(cancellationToken);
        ValidateDimensions(decoder);
        var bytes = await EncodePngAsync(
            decoder,
            LocalVisionAnalysisLongestEdge,
            preserveSourceDpi: true,
            cancellationToken).ConfigureAwait(false);
        return new MemoryStream(bytes, writable: false);
    }

    public async Task<RemoteVisionImageCopy> CreateRemoteAnalysisCopyAsync(
        Stream source,
        long maximumBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (maximumBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }
        if (!source.CanRead || !source.CanSeek)
        {
            throw new ArgumentException(
                "The source image stream must be readable and seekable.",
                nameof(source));
        }

        source.Position = 0;
        using var randomAccessStream = source.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(randomAccessStream).AsTask(cancellationToken);
        ValidateDimensions(decoder);
        var (width, height) = CalculateScaledDimensionsToPixelLimit(
            decoder.OrientedPixelWidth,
            decoder.OrientedPixelHeight,
            RemoteVisionMaximumPixelCount);

        for (var attempt = 0; attempt < MaximumRemoteEncodingAttempts; attempt++)
        {
            var bytes = await EncodePngAtDimensionsAsync(
                decoder,
                width,
                height,
                preserveSourceDpi: false,
                cancellationToken).ConfigureAwait(false);
            if (bytes.LongLength <= maximumBytes)
            {
                return new RemoteVisionImageCopy(
                    new MemoryStream(bytes, writable: false),
                    "image/png",
                    checked((int)width),
                    checked((int)height),
                    bytes.LongLength);
            }

            var nextDimensions = CalculateScaledDimensionsToByteLimit(
                width,
                height,
                bytes.LongLength,
                maximumBytes);
            if (nextDimensions == (width, height))
            {
                break;
            }

            (width, height) = nextDimensions;
        }

        throw new RemoteAnalysisProviderException(
            "remote.image-copy-too-large",
            isRetryable: false);
    }

    private static void ValidateDimensions(BitmapDecoder decoder)
    {
        var width = decoder.OrientedPixelWidth;
        var height = decoder.OrientedPixelHeight;
        if (width == 0
            || height == 0
            || (ulong)width * height > MaximumPixelCount)
        {
            throw new InvalidDataException("The decoded image dimensions exceed the supported limit.");
        }
    }

    private static async Task<byte[]> EncodePngAsync(
        BitmapDecoder decoder,
        uint? longestEdge,
        bool preserveSourceDpi,
        CancellationToken cancellationToken)
    {
        var sourceWidth = decoder.OrientedPixelWidth;
        var sourceHeight = decoder.OrientedPixelHeight;
        if (sourceWidth == 0 || sourceHeight == 0)
        {
            throw new InvalidDataException("The decoded image has invalid dimensions.");
        }

        var (width, height) = CalculateScaledDimensions(
            sourceWidth,
            sourceHeight,
            longestEdge);
        return await EncodePngAtDimensionsAsync(
            decoder,
            width,
            height,
            preserveSourceDpi,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> EncodePngAtDimensionsAsync(
        BitmapDecoder decoder,
        uint width,
        uint height,
        bool preserveSourceDpi,
        CancellationToken cancellationToken)
    {
        var transform = new BitmapTransform
        {
            ScaledWidth = width,
            ScaledHeight = height,
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        var pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb)
            .AsTask(cancellationToken);

        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output)
            .AsTask(cancellationToken);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            width,
            height,
            preserveSourceDpi ? decoder.DpiX : 96,
            preserveSourceDpi ? decoder.DpiY : 96,
            pixelData.DetachPixelData());
        await encoder.FlushAsync().AsTask(cancellationToken);

        output.Seek(0);
        var result = new byte[checked((int)output.Size)];
        using var resultStream = output.AsStreamForRead();
        await resultStream.ReadExactlyAsync(result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static (uint Width, uint Height) CalculateScaledDimensionsToPixelLimit(
        uint sourceWidth,
        uint sourceHeight,
        ulong maximumPixelCount)
    {
        var sourcePixelCount = (ulong)sourceWidth * sourceHeight;
        if (sourcePixelCount <= maximumPixelCount)
        {
            return (sourceWidth, sourceHeight);
        }

        var scale = Math.Sqrt(maximumPixelCount / (double)sourcePixelCount);
        return (
            Math.Max(1u, checked((uint)Math.Floor(sourceWidth * scale))),
            Math.Max(1u, checked((uint)Math.Floor(sourceHeight * scale))));
    }

    private static (uint Width, uint Height) CalculateScaledDimensionsToByteLimit(
        uint currentWidth,
        uint currentHeight,
        long currentBytes,
        long maximumBytes)
    {
        // Encoded PNG size is not perfectly proportional to pixel count, so keep
        // a small margin and retry against the actual encoded size.
        var scale = Math.Min(
            0.95d,
            Math.Sqrt(maximumBytes / (double)currentBytes) * 0.95d);
        var width = Math.Max(1u, checked((uint)Math.Floor(currentWidth * scale)));
        var height = Math.Max(1u, checked((uint)Math.Floor(currentHeight * scale)));
        return (width, height);
    }

    private static (uint Width, uint Height) CalculateScaledDimensions(
        uint sourceWidth,
        uint sourceHeight,
        uint? longestEdge)
    {
        var scale = longestEdge is null
            ? 1d
            : Math.Min(1d, longestEdge.Value / (double)Math.Max(sourceWidth, sourceHeight));
        return (
            Math.Max(1u, checked((uint)Math.Round(sourceWidth * scale))),
            Math.Max(1u, checked((uint)Math.Round(sourceHeight * scale))));
    }

    private static async Task<ManagedImageFormat> DetectFormatAsync(
        Stream source,
        CancellationToken cancellationToken)
    {
        var header = new byte[12];
        var total = 0;
        while (total < header.Length)
        {
            var read = await source.ReadAsync(
                header.AsMemory(total, header.Length - total),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        if (total >= 8
            && header.AsSpan(0, 8).SequenceEqual(
                new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return ManagedImageFormat.Png;
        }

        if (total >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            return ManagedImageFormat.Jpeg;
        }

        if (total >= 12
            && header.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            && header.AsSpan(8, 4).SequenceEqual("WEBP"u8))
        {
            return ManagedImageFormat.WebP;
        }

        throw new InvalidDataException("The image signature is not PNG, JPEG, or WebP.");
    }
}
