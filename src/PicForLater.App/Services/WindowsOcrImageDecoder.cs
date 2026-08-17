using System.Runtime.InteropServices.WindowsRuntime;
using PicForLater.Core.Analysis;
using Windows.Graphics.Imaging;

namespace PicForLater.App.Services;

public sealed class WindowsOcrImageDecoder : IOcrImageDecoder
{
    private const ulong MaximumPixelCount = 100_000_000;

    public async Task<DecodedOcrImage> DecodeAsync(
        Stream source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException("The OCR image stream must be readable.", nameof(source));
        }

        if (source.CanSeek)
        {
            source.Position = 0;
        }

        using var randomAccessStream = source.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(randomAccessStream).AsTask(cancellationToken);
        var width = decoder.OrientedPixelWidth;
        var height = decoder.OrientedPixelHeight;
        if (width == 0 || height == 0 || (ulong)width * height > MaximumPixelCount)
        {
            throw new InvalidDataException("The decoded OCR image dimensions exceed the supported limit.");
        }

        var pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                new BitmapTransform(),
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb)
            .AsTask(cancellationToken);
        var bgra = pixelData.DetachPixelData();
        var rgba = new byte[bgra.Length];
        for (var index = 0; index < bgra.Length; index += 4)
        {
            rgba[index] = bgra[index + 2];
            rgba[index + 1] = bgra[index + 1];
            rgba[index + 2] = bgra[index];
            rgba[index + 3] = bgra[index + 3];
        }

        return new DecodedOcrImage(rgba, checked((int)width), checked((int)height));
    }
}
