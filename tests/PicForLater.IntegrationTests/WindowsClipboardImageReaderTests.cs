using System.Buffers.Binary;
using System.Runtime.InteropServices;
using PicForLater.App.Models;
using PicForLater.App.Services;

namespace PicForLater.IntegrationTests;

public sealed class WindowsClipboardImageReaderTests
{
    private const uint PngFormat = 0xC001;

    [Fact]
    public async Task Probe_RetriesWithBoundedScheduleAndClosesAfterSuccess()
    {
        using var native = new FakeClipboardNativeMethods();
        native.OpenResults.Enqueue(false);
        native.OpenResults.Enqueue(false);
        native.OpenResults.Enqueue(true);
        native.SequenceNumber = 42;
        var delays = new List<TimeSpan>();
        var reader = CreateReader(native, delays);

        ScreenshotClipboardAccessResult result = await reader.ProbeAccessAsync(CancellationToken.None);

        Assert.True(result.IsAvailable);
        Assert.Equal(42u, result.SequenceNumber);
        Assert.Equal(3, native.OpenCalls);
        Assert.Equal([10, 30], delays.Select(static delay => (int)delay.TotalMilliseconds));
        Assert.Equal(1, native.CloseCalls);
    }

    [Fact]
    public async Task Probe_RetryExhaustionDoesNotPretendClipboardWasOpened()
    {
        using var native = new FakeClipboardNativeMethods();
        for (var index = 0; index < 5; index++)
        {
            native.OpenResults.Enqueue(false);
        }

        var delays = new List<TimeSpan>();
        var reader = CreateReader(native, delays);

        ScreenshotClipboardAccessResult result = await reader.ProbeAccessAsync(CancellationToken.None);

        Assert.False(result.IsAvailable);
        Assert.Equal(5, native.OpenCalls);
        Assert.Equal([10, 30, 100, 300], delays.Select(static delay => (int)delay.TotalMilliseconds));
        Assert.Equal(0, native.CloseCalls);
    }

    [Fact]
    public async Task Read_PngHasPriorityAndReturnedBytesOutliveClipboardClose()
    {
        using var native = new FakeClipboardNativeMethods { CloseClearsSources = true };
        byte[] png = CreatePngHeader(320, 200);
        native.SetFormat(PngFormat, png);
        native.SetFormat(WindowsClipboardImageReader.CfDibV5, CreateDibV5(2, 2));
        native.SequenceNumber = 77;
        var reader = CreateReader(native);

        ScreenshotClipboardReadResult result = await reader.ReadImageAsync(CancellationToken.None);

        Assert.Equal(ScreenshotClipboardReadStatus.Image, result.Status);
        Assert.Equal(77u, result.SequenceNumber);
        Assert.Equal(ScreenshotClipboardImageFormat.Png, result.Image!.Format);
        Assert.Equal(0x89, result.Image.Bytes.Span[0]);
        Assert.All(png, static value => Assert.Equal(0, value));
        Assert.Equal([PngFormat], native.DataRequests);
        Assert.Equal(1, native.CloseCalls);
        Assert.Equal(1, native.UnlockCalls);
    }

    [Fact]
    public async Task Read_DibV5FallbackBuildsAValidBitmapFileHeader()
    {
        using var native = new FakeClipboardNativeMethods();
        byte[] dib = CreateDibV5(3, 2);
        native.SetFormat(WindowsClipboardImageReader.CfDibV5, dib);
        native.SequenceNumber = 9;
        var reader = CreateReader(native);

        ScreenshotClipboardReadResult result = await reader.ReadImageAsync(CancellationToken.None);

        ReadOnlySpan<byte> bitmap = result.Image!.Bytes.Span;
        Assert.Equal(ScreenshotClipboardReadStatus.Image, result.Status);
        Assert.Equal(ScreenshotClipboardImageFormat.DibV5, result.Image.Format);
        Assert.Equal("BM"u8.ToArray(), bitmap[..2].ToArray());
        Assert.Equal((uint)bitmap.Length, BinaryPrimitives.ReadUInt32LittleEndian(bitmap[2..6]));
        Assert.Equal(138u, BinaryPrimitives.ReadUInt32LittleEndian(bitmap[10..14]));
        Assert.Equal(124u, BinaryPrimitives.ReadUInt32LittleEndian(bitmap[14..18]));
    }

    [Fact]
    public async Task Read_OnlyCfBitmapIsANonImageChangeForThisFeature()
    {
        using var native = new FakeClipboardNativeMethods();
        native.SetFormat(2, new byte[] { 1 });
        native.SequenceNumber = 13;
        var reader = CreateReader(native);

        ScreenshotClipboardReadResult result = await reader.ReadImageAsync(CancellationToken.None);

        Assert.Equal(ScreenshotClipboardReadStatus.NoImage, result.Status);
        Assert.Equal(13u, result.SequenceNumber);
        Assert.Empty(native.DataRequests);
        Assert.Equal(1, native.CloseCalls);
    }

    [Fact]
    public async Task Read_MalformedPriorityPngDoesNotFallThroughToDibV5()
    {
        using var native = new FakeClipboardNativeMethods();
        native.SetFormat(PngFormat, new byte[33]);
        native.SetFormat(WindowsClipboardImageReader.CfDibV5, CreateDibV5(2, 2));
        var reader = CreateReader(native);

        ScreenshotClipboardReadResult result = await reader.ReadImageAsync(CancellationToken.None);

        Assert.Equal(ScreenshotClipboardReadStatus.InvalidImage, result.Status);
        Assert.Equal([PngFormat], native.DataRequests);
        Assert.Equal(1, native.CloseCalls);
    }

    [Fact]
    public async Task Read_PngByteLimitIsCheckedBeforeLockOrAllocation()
    {
        using var native = new FakeClipboardNativeMethods();
        native.SetFormat(PngFormat, CreatePngHeader(1, 1));
        native.GlobalSizeOverrides[PngFormat] =
            checked((nuint)(WindowsClipboardImageReader.MaximumPngBytes + 1));
        var reader = CreateReader(native);

        ScreenshotClipboardReadResult result = await reader.ReadImageAsync(CancellationToken.None);

        Assert.Equal(ScreenshotClipboardReadStatus.InvalidImage, result.Status);
        Assert.Equal(0, native.LockCalls);
        Assert.Equal(1, native.CloseCalls);
    }

    [Fact]
    public async Task Read_OpenRetryExhaustionReturnsClipboardUnavailable()
    {
        using var native = new FakeClipboardNativeMethods();
        for (var index = 0; index < 5; index++)
        {
            native.OpenResults.Enqueue(false);
        }

        var reader = CreateReader(native);

        ScreenshotClipboardReadResult result = await reader.ReadImageAsync(CancellationToken.None);

        Assert.Equal(ScreenshotClipboardReadStatus.ClipboardUnavailable, result.Status);
        Assert.Equal(0, native.CloseCalls);
    }

    [Fact]
    public async Task Read_CancellationDuringBusyRetryStopsWithoutOpeningOrClosingClipboard()
    {
        using var native = new FakeClipboardNativeMethods();
        native.OpenResults.Enqueue(false);
        var delayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new WindowsClipboardImageReader(
            (nint)1,
            native,
            [TimeSpan.FromMilliseconds(10)],
            async (_, cancellationToken) =>
            {
                delayStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });
        using var cancellation = new CancellationTokenSource();

        ValueTask<ScreenshotClipboardReadResult> read =
            reader.ReadImageAsync(cancellation.Token);
        await delayStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.AsTask());
        Assert.Equal(1, native.OpenCalls);
        Assert.Equal(0, native.CloseCalls);
    }

    [Fact]
    public async Task Read_DibByteLimitIsCheckedBeforeLockOrAllocation()
    {
        using var native = new FakeClipboardNativeMethods();
        native.SetFormat(WindowsClipboardImageReader.CfDibV5, CreateDibV5(1, 1));
        native.GlobalSizeOverrides[WindowsClipboardImageReader.CfDibV5] =
            checked((nuint)(WindowsClipboardImageReader.MaximumDibBytes + 1));
        var reader = CreateReader(native);

        ScreenshotClipboardReadResult result = await reader.ReadImageAsync(CancellationToken.None);

        Assert.Equal(ScreenshotClipboardReadStatus.InvalidImage, result.Status);
        Assert.Equal(0, native.LockCalls);
        Assert.Equal(1, native.CloseCalls);
    }

    [Fact]
    public async Task Read_CloseFailureOverridesAnOtherwiseSuccessfulCopy()
    {
        using var native = new FakeClipboardNativeMethods { CloseSucceeds = false };
        native.SetFormat(PngFormat, CreatePngHeader(1, 1));
        var reader = CreateReader(native);

        ScreenshotClipboardReadResult result = await reader.ReadImageAsync(CancellationToken.None);

        Assert.Equal(ScreenshotClipboardReadStatus.ClipboardUnavailable, result.Status);
        Assert.Equal(1, native.CloseCalls);
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(-2, true)]
    public void DibV5_ValidatesBottomUpTopDownStrideAndAlpha(int signedHeight, bool isTopDown)
    {
        byte[] dib = CreateDibV5(3, signedHeight);

        WindowsClipboardImageReader.DibV5Layout layout =
            WindowsClipboardImageReader.ValidateDibV5Header(dib, dib.Length);
        byte[] bitmap = WindowsClipboardImageReader.WrapDibV5AsBitmapFile(dib);

        Assert.Equal(3, layout.Width);
        Assert.Equal(2, layout.Height);
        Assert.Equal(isTopDown, layout.IsTopDown);
        Assert.Equal(12, layout.Stride);
        Assert.Equal(0xFF000000u, layout.AlphaMask);
        Assert.Equal(signedHeight, BinaryPrimitives.ReadInt32LittleEndian(bitmap.AsSpan(22, 4)));
    }

    [Fact]
    public void DibV5_ValidatesARepresentative4KLayoutWithoutLargeAllocation()
    {
        byte[] header = CreateDibV5Header(3840, -2160);
        long pixelBytes = 3840L * 2160 * 4;

        WindowsClipboardImageReader.DibV5Layout layout =
            WindowsClipboardImageReader.ValidateDibV5Header(
                header,
                checked(124 + pixelBytes));

        Assert.Equal(8_294_400, layout.Width * (long)layout.Height);
        Assert.Equal(15_360, layout.Stride);
        Assert.True(layout.IsTopDown);
    }

    [Fact]
    public void DibV5_RejectsTruncationBadRangesOversizedPixelsAndOverlappingMasks()
    {
        Assert.Throws<InvalidDataException>(() =>
            WindowsClipboardImageReader.ValidateDibV5Header(new byte[123], 123));

        byte[] shortImage = CreateDibV5Header(10, 10);
        BinaryPrimitives.WriteUInt32LittleEndian(shortImage.AsSpan(20, 4), 1);
        Assert.Throws<InvalidDataException>(() =>
            WindowsClipboardImageReader.ValidateDibV5Header(shortImage, 525));

        byte[] tooManyPixels = CreateDibV5Header(5001, 5000);
        Assert.Throws<InvalidDataException>(() =>
            WindowsClipboardImageReader.ValidateDibV5Header(
                tooManyPixels,
                124L + 5001L * 5000 * 4));

        byte[] overlappingMasks = CreateDibV5Header(2, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(overlappingMasks.AsSpan(44, 4), 0x00FF0000);
        Assert.Throws<InvalidDataException>(() =>
            WindowsClipboardImageReader.ValidateDibV5Header(overlappingMasks, 140));
    }

    [Fact]
    public void DibV5_EmbeddedProfileIsBoundedAndLinkedProfileIsRejected()
    {
        byte[] embedded = CreateDibV5(2, 2, embeddedProfileBytes: 4);
        WindowsClipboardImageReader.DibV5Layout layout =
            WindowsClipboardImageReader.ValidateDibV5Header(embedded, embedded.Length);
        Assert.Equal(embedded.Length, layout.CopyLength);

        byte[] truncated = CreateDibV5(2, 2, embeddedProfileBytes: 4);
        BinaryPrimitives.WriteUInt32LittleEndian(
            truncated.AsSpan(112, 4),
            checked((uint)(truncated.Length - 2)));
        Assert.Throws<InvalidDataException>(() =>
            WindowsClipboardImageReader.ValidateDibV5Header(truncated, truncated.Length));

        byte[] linked = CreateDibV5Header(2, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(linked.AsSpan(56, 4), 0x4C494E4B);
        Assert.Throws<NotSupportedException>(() =>
            WindowsClipboardImageReader.ValidateDibV5Header(linked, 140));
    }

    [Fact]
    public void Png_RejectsTruncatedInvalidAndOversizedDimensionHeaders()
    {
        Assert.Throws<InvalidDataException>(() =>
            WindowsClipboardImageReader.ValidatePngHeader(new byte[23], 23));
        Assert.Throws<InvalidDataException>(() =>
            WindowsClipboardImageReader.ValidatePngHeader(new byte[33], 33));
        Assert.Throws<InvalidDataException>(() =>
            WindowsClipboardImageReader.ValidatePngHeader(
                CreatePngHeader(5001, 5000),
                33));
        Assert.Throws<InvalidDataException>(() =>
            WindowsClipboardImageReader.ValidatePngHeader(
                CreatePngHeader(uint.MaxValue, uint.MaxValue),
                33));
    }

    [Fact]
    public void FormatLimits_AcceptTheirExactByteAndPixelBoundaries()
    {
        WindowsClipboardImageReader.PngLayout png =
            WindowsClipboardImageReader.ValidatePngHeader(
                CreatePngHeader(5000, 5000),
                WindowsClipboardImageReader.MaximumPngBytes);

        byte[] dibHeader = CreateDibV5Header(5000, 5000);
        WindowsClipboardImageReader.DibV5Layout dib =
            WindowsClipboardImageReader.ValidateDibV5Header(
                dibHeader,
                WindowsClipboardImageReader.MaximumDibBytes);

        Assert.Equal(WindowsClipboardImageReader.MaximumPixelCount, png.PixelCount);
        Assert.Equal(WindowsClipboardImageReader.MaximumPixelCount, dib.Width * (long)dib.Height);
    }

    private static WindowsClipboardImageReader CreateReader(
        FakeClipboardNativeMethods native,
        List<TimeSpan>? observedDelays = null) =>
        new(
            (nint)1,
            native,
            [
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromMilliseconds(30),
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(300),
            ],
            (delay, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                observedDelays?.Add(delay);
                return Task.CompletedTask;
            });

    private static byte[] CreatePngHeader(uint width, uint height)
    {
        var bytes = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }
            .CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12, 4));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20, 4), height);
        bytes[24] = 8;
        bytes[25] = 6;
        return bytes;
    }

    private static byte[] CreateDibV5(
        int width,
        int signedHeight,
        int embeddedProfileBytes = 0)
    {
        byte[] header = CreateDibV5Header(width, signedHeight);
        int pixelBytes = checked(width * Math.Abs(signedHeight) * 4);
        var dib = new byte[checked(header.Length + pixelBytes + embeddedProfileBytes)];
        header.CopyTo(dib, 0);
        if (embeddedProfileBytes > 0)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(56, 4), 0x4D424544);
            BinaryPrimitives.WriteUInt32LittleEndian(
                dib.AsSpan(112, 4),
                checked((uint)(header.Length + pixelBytes)));
            BinaryPrimitives.WriteUInt32LittleEndian(
                dib.AsSpan(116, 4),
                checked((uint)embeddedProfileBytes));
        }

        return dib;
    }

    private static byte[] CreateDibV5Header(int width, int signedHeight)
    {
        var header = new byte[124];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 124);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), signedHeight);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14, 2), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16, 4), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(20, 4),
            checked((uint)(width * (long)Math.Abs(signedHeight) * 4)));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40, 4), 0x00FF0000);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(44, 4), 0x0000FF00);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(48, 4), 0x000000FF);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(52, 4), 0xFF000000);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(56, 4), 0x73524742);
        return header;
    }

    private sealed class FakeClipboardNativeMethods : IWindowsClipboardNativeMethods, IDisposable
    {
        private readonly Dictionary<uint, byte[]> _formats = [];
        private readonly Dictionary<uint, GCHandle> _pins = [];

        internal Queue<bool> OpenResults { get; } = new();
        internal Dictionary<uint, nuint> GlobalSizeOverrides { get; } = [];
        internal List<uint> DataRequests { get; } = [];
        internal uint SequenceNumber { get; set; } = 1;
        internal bool CloseSucceeds { get; init; } = true;
        internal bool CloseClearsSources { get; init; }
        internal bool LockFails { get; init; }
        internal int OpenCalls { get; private set; }
        internal int CloseCalls { get; private set; }
        internal int LockCalls { get; private set; }
        internal int UnlockCalls { get; private set; }

        public uint RegisterClipboardFormat(string formatName) => PngFormat;

        public bool OpenClipboard(nint ownerWindow)
        {
            OpenCalls++;
            return OpenResults.Count == 0 || OpenResults.Dequeue();
        }

        public bool CloseClipboard()
        {
            CloseCalls++;
            if (CloseClearsSources)
            {
                foreach (byte[] source in _formats.Values)
                {
                    Array.Clear(source);
                }
            }

            return CloseSucceeds;
        }

        public bool IsClipboardFormatAvailable(uint format) => _formats.ContainsKey(format);

        public nint GetClipboardData(uint format)
        {
            DataRequests.Add(format);
            return _formats.ContainsKey(format) ? checked((nint)format) : 0;
        }

        public nuint GlobalSize(nint handle)
        {
            uint format = checked((uint)handle);
            return GlobalSizeOverrides.TryGetValue(format, out nuint size)
                ? size
                : checked((nuint)_formats[format].Length);
        }

        public nint GlobalLock(nint handle)
        {
            LockCalls++;
            return LockFails ? 0 : _pins[checked((uint)handle)].AddrOfPinnedObject();
        }

        public bool GlobalUnlock(nint handle)
        {
            UnlockCalls++;
            return true;
        }

        public uint GetClipboardSequenceNumber() => SequenceNumber;

        internal void SetFormat(uint format, byte[] bytes)
        {
            _formats.Add(format, bytes);
            _pins.Add(format, GCHandle.Alloc(bytes, GCHandleType.Pinned));
        }

        public void Dispose()
        {
            foreach (GCHandle pin in _pins.Values)
            {
                if (pin.IsAllocated)
                {
                    pin.Free();
                }
            }
        }
    }
}
