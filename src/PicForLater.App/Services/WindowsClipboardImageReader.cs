using System.Buffers.Binary;
using System.Runtime.InteropServices;
using PicForLater.App.Models;

namespace PicForLater.App.Services;

internal sealed class WindowsClipboardImageReader
{
    internal const int MaximumPngBytes = 64 * 1024 * 1024;
    internal const int MaximumDibBytes = 128 * 1024 * 1024;
    internal const long MaximumPixelCount = 25_000_000;
    internal const long MaximumEstimatedPathBytes = 384L * 1024 * 1024;
    internal const uint CfDibV5 = 17;

    private const int BitmapFileHeaderSize = 14;
    private const int BitmapV5HeaderSize = 124;
    private const uint BiRgb = 0;
    private const uint BiBitFields = 3;
    private const uint ProfileLinked = 0x4C494E4B; // 'LINK'
    private const uint ProfileEmbedded = 0x4D424544; // 'MBED'
    private const uint RegisteredPngFallbackId = 0;

    private static readonly TimeSpan[] DefaultOpenRetryDelays =
    [
        TimeSpan.FromMilliseconds(10),
        TimeSpan.FromMilliseconds(30),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(300),
    ];

    private readonly nint _ownerWindow;
    private readonly IWindowsClipboardNativeMethods _native;
    private readonly IReadOnlyList<TimeSpan> _openRetryDelays;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly uint _pngFormat;

    internal WindowsClipboardImageReader(nint ownerWindow)
        : this(
            ownerWindow,
            WindowsClipboardNativeMethods.Instance,
            DefaultOpenRetryDelays,
            static (delay, cancellationToken) => Task.Delay(delay, cancellationToken))
    {
    }

    internal WindowsClipboardImageReader(
        nint ownerWindow,
        IWindowsClipboardNativeMethods native,
        IReadOnlyList<TimeSpan> openRetryDelays,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        if (ownerWindow == 0)
        {
            throw new ArgumentException("A valid Clipboard owner window is required.", nameof(ownerWindow));
        }

        _ownerWindow = ownerWindow;
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _openRetryDelays = openRetryDelays ?? throw new ArgumentNullException(nameof(openRetryDelays));
        _delayAsync = delayAsync ?? throw new ArgumentNullException(nameof(delayAsync));
        if (_openRetryDelays.Any(static delay => delay < TimeSpan.Zero))
        {
            throw new ArgumentOutOfRangeException(nameof(openRetryDelays));
        }

        _pngFormat = _native.RegisterClipboardFormat("PNG");
    }

    internal async ValueTask<ScreenshotClipboardAccessResult> ProbeAccessAsync(
        CancellationToken cancellationToken)
    {
        if (!await TryOpenClipboardAsync(cancellationToken).ConfigureAwait(false))
        {
            return ScreenshotClipboardAccessResult.Unavailable;
        }

        uint sequenceNumber;
        bool closed;
        try
        {
            sequenceNumber = _native.GetClipboardSequenceNumber();
        }
        finally
        {
            closed = _native.CloseClipboard();
        }

        return closed
            ? ScreenshotClipboardAccessResult.Available(sequenceNumber)
            : ScreenshotClipboardAccessResult.Unavailable;
    }

    internal async ValueTask<ScreenshotClipboardReadResult> ReadImageAsync(
        CancellationToken cancellationToken)
    {
        if (!await TryOpenClipboardAsync(cancellationToken).ConfigureAwait(false))
        {
            return ScreenshotClipboardReadResult.ClipboardUnavailable;
        }

        uint sequenceNumber = 0;
        ScreenshotClipboardReadResult result;
        bool closed;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            sequenceNumber = _native.GetClipboardSequenceNumber();
            if (_pngFormat != RegisteredPngFallbackId &&
                _native.IsClipboardFormatAvailable(_pngFormat))
            {
                result = ScreenshotClipboardReadResult.FromImage(
                    sequenceNumber,
                    ReadPngImage(_pngFormat));
            }
            else if (_native.IsClipboardFormatAvailable(CfDibV5))
            {
                result = ScreenshotClipboardReadResult.FromImage(
                    sequenceNumber,
                    ReadDibV5Image());
            }
            else
            {
                result = ScreenshotClipboardReadResult.NoImage(sequenceNumber);
            }
        }
        catch (ClipboardNativeReadException)
        {
            result = ScreenshotClipboardReadResult.ClipboardUnavailable;
        }
        catch (NotSupportedException)
        {
            result = ScreenshotClipboardReadResult.UnsupportedImage(sequenceNumber);
        }
        catch (InvalidDataException)
        {
            result = ScreenshotClipboardReadResult.InvalidImage(sequenceNumber);
        }
        finally
        {
            closed = _native.CloseClipboard();
        }

        cancellationToken.ThrowIfCancellationRequested();
        return closed ? result : ScreenshotClipboardReadResult.ClipboardUnavailable;
    }

    internal static PngLayout ValidatePngHeader(
        ReadOnlySpan<byte> header,
        long totalBytes)
    {
        if (totalBytes < 33 || header.Length < 24)
        {
            throw new InvalidDataException("The PNG Clipboard payload is truncated.");
        }

        ReadOnlySpan<byte> signature =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (!header[..8].SequenceEqual(signature) ||
            BinaryPrimitives.ReadUInt32BigEndian(header[8..12]) != 13 ||
            !header[12..16].SequenceEqual("IHDR"u8))
        {
            throw new InvalidDataException("The registered PNG payload has an invalid signature or IHDR.");
        }

        uint width = BinaryPrimitives.ReadUInt32BigEndian(header[16..20]);
        uint height = BinaryPrimitives.ReadUInt32BigEndian(header[20..24]);
        ulong unsignedPixelCount = (ulong)width * height;
        if (width == 0 ||
            height == 0 ||
            unsignedPixelCount > checked((ulong)MaximumPixelCount))
        {
            throw new InvalidDataException("The PNG dimensions exceed the Clipboard limit.");
        }

        long pixelCount = checked((long)unsignedPixelCount);
        ValidateEstimatedPath(totalBytes, pixelCount);
        return new PngLayout(width, height, pixelCount);
    }

    internal static DibV5Layout ValidateDibV5Header(
        ReadOnlySpan<byte> header,
        long totalBytes)
    {
        if (totalBytes < BitmapV5HeaderSize || header.Length < BitmapV5HeaderSize)
        {
            throw new InvalidDataException("The DIBV5 header is truncated.");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != BitmapV5HeaderSize)
        {
            throw new InvalidDataException("The CF_DIBV5 payload does not contain a BITMAPV5HEADER.");
        }

        int width = BinaryPrimitives.ReadInt32LittleEndian(header[4..8]);
        int signedHeight = BinaryPrimitives.ReadInt32LittleEndian(header[8..12]);
        ushort planes = BinaryPrimitives.ReadUInt16LittleEndian(header[12..14]);
        ushort bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(header[14..16]);
        uint compression = BinaryPrimitives.ReadUInt32LittleEndian(header[16..20]);
        uint declaredImageBytes = BinaryPrimitives.ReadUInt32LittleEndian(header[20..24]);
        uint colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(header[32..36]);

        if (width <= 0 || signedHeight == 0 || signedHeight == int.MinValue || planes != 1)
        {
            throw new InvalidDataException("The DIBV5 dimensions or plane count are invalid.");
        }

        if (bitsPerPixel != 32 || compression is not (BiRgb or BiBitFields))
        {
            throw new NotSupportedException("Only uncompressed 32-bit CF_DIBV5 images are supported.");
        }

        uint redMask = BinaryPrimitives.ReadUInt32LittleEndian(header[40..44]);
        uint greenMask = BinaryPrimitives.ReadUInt32LittleEndian(header[44..48]);
        uint blueMask = BinaryPrimitives.ReadUInt32LittleEndian(header[48..52]);
        uint alphaMask = BinaryPrimitives.ReadUInt32LittleEndian(header[52..56]);
        if (compression == BiBitFields)
        {
            ValidateBitFieldMasks(redMask, greenMask, blueMask, alphaMask);
        }

        long absoluteHeight = Math.Abs((long)signedHeight);
        long pixelCount = checked((long)width * absoluteHeight);
        if (pixelCount > MaximumPixelCount)
        {
            throw new InvalidDataException("The DIBV5 dimensions exceed the Clipboard limit.");
        }

        long stride = checked(((checked((long)width * bitsPerPixel) + 31) / 32) * 4);
        long minimumPixelBytes = checked(stride * absoluteHeight);
        if (declaredImageBytes != 0 && declaredImageBytes < minimumPixelBytes)
        {
            throw new InvalidDataException("The DIBV5 image byte count is smaller than its stride and height.");
        }

        long paletteBytes = checked((long)colorsUsed * 4);
        long pixelOffset = checked(BitmapV5HeaderSize + paletteBytes);
        long pixelBytes = declaredImageBytes == 0 ? minimumPixelBytes : declaredImageBytes;
        long pixelEnd = checked(pixelOffset + pixelBytes);
        if (pixelEnd > totalBytes)
        {
            throw new InvalidDataException("The DIBV5 pixel range exceeds the Clipboard buffer.");
        }

        long copyLength = pixelEnd;
        uint colorSpaceType = BinaryPrimitives.ReadUInt32LittleEndian(header[56..60]);
        uint profileOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[112..116]);
        uint profileSize = BinaryPrimitives.ReadUInt32LittleEndian(header[116..120]);
        if (colorSpaceType == ProfileLinked)
        {
            // A linked profile can name a local or network file. The screenshot
            // path never follows external profile paths from Clipboard data.
            throw new NotSupportedException("Linked DIBV5 color profiles are not supported.");
        }

        if (colorSpaceType == ProfileEmbedded)
        {
            if (profileOffset < pixelEnd || profileSize == 0)
            {
                throw new InvalidDataException("The embedded DIBV5 profile overlaps or precedes the pixel data.");
            }

            long profileEnd = checked((long)profileOffset + profileSize);
            if (profileEnd > totalBytes)
            {
                throw new InvalidDataException("The embedded DIBV5 profile exceeds the Clipboard buffer.");
            }

            copyLength = profileEnd;
        }

        ValidateEstimatedPath(totalBytes, pixelCount);
        return new DibV5Layout(
            width,
            checked((int)absoluteHeight),
            signedHeight < 0,
            stride,
            pixelOffset,
            pixelBytes,
            copyLength,
            alphaMask);
    }

    internal static byte[] WrapDibV5AsBitmapFile(ReadOnlySpan<byte> dib)
    {
        DibV5Layout layout = ValidateDibV5Header(dib, dib.Length);
        int dibLength = checked((int)layout.CopyLength);
        byte[] bitmap = GC.AllocateUninitializedArray<byte>(
            checked(BitmapFileHeaderSize + dibLength));
        WriteBitmapFileHeader(bitmap, dibLength, layout.PixelOffset);
        dib[..dibLength].CopyTo(bitmap.AsSpan(BitmapFileHeaderSize));
        return bitmap;
    }

    private ScreenshotClipboardImage ReadPngImage(uint format)
    {
        nint handle = GetClipboardHandle(format);
        int size = GetBoundedGlobalSize(handle, MaximumPngBytes);
        nint source = LockClipboardHandle(handle);
        try
        {
            var header = new byte[Math.Min(33, size)];
            Marshal.Copy(source, header, 0, header.Length);
            _ = ValidatePngHeader(header, size);
            byte[] bytes = GC.AllocateUninitializedArray<byte>(size);
            Marshal.Copy(source, bytes, 0, size);
            return new ScreenshotClipboardImage(ScreenshotClipboardImageFormat.Png, bytes);
        }
        finally
        {
            _ = _native.GlobalUnlock(handle);
        }
    }

    private ScreenshotClipboardImage ReadDibV5Image()
    {
        nint handle = GetClipboardHandle(CfDibV5);
        int globalSize = GetBoundedGlobalSize(handle, MaximumDibBytes);
        nint source = LockClipboardHandle(handle);
        try
        {
            var header = new byte[Math.Min(BitmapV5HeaderSize, globalSize)];
            Marshal.Copy(source, header, 0, header.Length);
            DibV5Layout layout = ValidateDibV5Header(header, globalSize);
            int dibLength = checked((int)layout.CopyLength);
            byte[] bitmap = GC.AllocateUninitializedArray<byte>(
                checked(BitmapFileHeaderSize + dibLength));
            WriteBitmapFileHeader(bitmap, dibLength, layout.PixelOffset);
            Marshal.Copy(source, bitmap, BitmapFileHeaderSize, dibLength);
            return new ScreenshotClipboardImage(ScreenshotClipboardImageFormat.DibV5, bitmap);
        }
        finally
        {
            _ = _native.GlobalUnlock(handle);
        }
    }

    private async ValueTask<bool> TryOpenClipboardAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_native.OpenClipboard(_ownerWindow))
        {
            return true;
        }

        foreach (TimeSpan delay in _openRetryDelays)
        {
            await _delayAsync(delay, cancellationToken).ConfigureAwait(false);
            if (_native.OpenClipboard(_ownerWindow))
            {
                return true;
            }
        }

        return false;
    }

    private nint GetClipboardHandle(uint format)
    {
        nint handle = _native.GetClipboardData(format);
        return handle != 0
            ? handle
            : throw new ClipboardNativeReadException();
    }

    private int GetBoundedGlobalSize(nint handle, int maximumBytes)
    {
        nuint nativeSize = _native.GlobalSize(handle);
        ulong size = nativeSize;
        if (size == 0)
        {
            throw new ClipboardNativeReadException();
        }

        if (size > checked((ulong)maximumBytes) || size > int.MaxValue)
        {
            throw new InvalidDataException("The Clipboard image exceeds its format-specific byte limit.");
        }

        return checked((int)size);
    }

    private nint LockClipboardHandle(nint handle)
    {
        nint source = _native.GlobalLock(handle);
        return source != 0
            ? source
            : throw new ClipboardNativeReadException();
    }

    private static void WriteBitmapFileHeader(
        Span<byte> destination,
        int dibLength,
        long dibPixelOffset)
    {
        destination[0] = (byte)'B';
        destination[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(
            destination[2..6],
            checked((uint)(BitmapFileHeaderSize + dibLength)));
        destination[6..10].Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(
            destination[10..14],
            checked((uint)(BitmapFileHeaderSize + dibPixelOffset)));
    }

    private static void ValidateBitFieldMasks(
        uint redMask,
        uint greenMask,
        uint blueMask,
        uint alphaMask)
    {
        if (!IsContiguousMask(redMask) ||
            !IsContiguousMask(greenMask) ||
            !IsContiguousMask(blueMask) ||
            (redMask & greenMask) != 0 ||
            (redMask & blueMask) != 0 ||
            (greenMask & blueMask) != 0)
        {
            throw new InvalidDataException("The DIBV5 RGB masks are invalid.");
        }

        uint rgbMask = redMask | greenMask | blueMask;
        if (alphaMask != 0 &&
            (!IsContiguousMask(alphaMask) || (alphaMask & rgbMask) != 0))
        {
            throw new InvalidDataException("The DIBV5 alpha mask is invalid.");
        }
    }

    private static bool IsContiguousMask(uint mask)
    {
        if (mask == 0)
        {
            return false;
        }

        uint shifted = mask >> System.Numerics.BitOperations.TrailingZeroCount(mask);
        return (shifted & (shifted + 1)) == 0;
    }

    private static void ValidateEstimatedPath(long sourceBytes, long pixelCount)
    {
        long estimatedBytes = checked(sourceBytes + checked(pixelCount * 8));
        if (estimatedBytes > MaximumEstimatedPathBytes)
        {
            throw new InvalidDataException("The estimated Clipboard image path exceeds its memory budget.");
        }
    }

    internal readonly record struct PngLayout(uint Width, uint Height, long PixelCount);

    internal readonly record struct DibV5Layout(
        int Width,
        int Height,
        bool IsTopDown,
        long Stride,
        long PixelOffset,
        long PixelBytes,
        long CopyLength,
        uint AlphaMask);

    private sealed class ClipboardNativeReadException : Exception;
}

internal interface IWindowsClipboardNativeMethods
{
    uint RegisterClipboardFormat(string formatName);

    bool OpenClipboard(nint ownerWindow);

    bool CloseClipboard();

    bool IsClipboardFormatAvailable(uint format);

    nint GetClipboardData(uint format);

    nuint GlobalSize(nint handle);

    nint GlobalLock(nint handle);

    bool GlobalUnlock(nint handle);

    uint GetClipboardSequenceNumber();
}

internal sealed class WindowsClipboardNativeMethods : IWindowsClipboardNativeMethods
{
    internal static WindowsClipboardNativeMethods Instance { get; } = new();

    private WindowsClipboardNativeMethods()
    {
    }

    public uint RegisterClipboardFormat(string formatName) =>
        NativeMethods.RegisterClipboardFormat(formatName);

    public bool OpenClipboard(nint ownerWindow) => NativeMethods.OpenClipboard(ownerWindow);

    public bool CloseClipboard() => NativeMethods.CloseClipboard();

    public bool IsClipboardFormatAvailable(uint format) =>
        NativeMethods.IsClipboardFormatAvailable(format);

    public nint GetClipboardData(uint format) => NativeMethods.GetClipboardData(format);

    public nuint GlobalSize(nint handle) => NativeMethods.GlobalSize(handle);

    public nint GlobalLock(nint handle) => NativeMethods.GlobalLock(handle);

    public bool GlobalUnlock(nint handle) => NativeMethods.GlobalUnlock(handle);

    public uint GetClipboardSequenceNumber() => NativeMethods.GetClipboardSequenceNumber();

    private static class NativeMethods
    {
        [DllImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", CharSet = CharSet.Unicode)]
        internal static extern uint RegisterClipboardFormat(string formatName);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenClipboard(nint ownerWindow);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsClipboardFormatAvailable(uint format);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern nint GetClipboardData(uint format);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern nuint GlobalSize(nint handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern nint GlobalLock(nint handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GlobalUnlock(nint handle);

        [DllImport("user32.dll")]
        internal static extern uint GetClipboardSequenceNumber();
    }
}
