using PicForLater.App.Models;

namespace PicForLater.App.Services;

public interface IScreenshotCapturePlatform
{
    event EventHandler<ScreenshotHotKeyPressedEventArgs>? HotKeyPressed;

    ScreenshotHotKeyRegistrationStatus RegisterHotKey(int hotKeyId, ScreenshotHotKey hotKey);

    bool UnregisterHotKey(int hotKeyId);

    bool AreCaptureKeysReleased(ScreenshotHotKey hotKey);

    bool SendScreenshotShortcut();

    uint GetClipboardSequenceNumber();

    ValueTask<ScreenshotClipboardAccessResult> ProbeClipboardAccessAsync(
        CancellationToken cancellationToken = default);

    ValueTask<ScreenshotClipboardReadResult> ReadClipboardImageAsync(
        CancellationToken cancellationToken = default);
}

public sealed class ScreenshotHotKeyPressedEventArgs : EventArgs
{
    public ScreenshotHotKeyPressedEventArgs(int hotKeyId)
    {
        if (hotKeyId is < 0 or > 0xBFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(hotKeyId));
        }

        HotKeyId = hotKeyId;
    }

    public int HotKeyId { get; }
}

public interface IScreenshotCaptureImporter
{
    Task<ScreenshotImportResult> ImportAsync(
        ScreenshotClipboardImage image,
        CancellationToken cancellationToken = default);
}

public sealed class ScreenshotCaptureImportException : Exception
{
    public ScreenshotCaptureImportException(
        ScreenshotCaptureFailureKind failureKind,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        if (failureKind is not ScreenshotCaptureFailureKind.InvalidImage and
            not ScreenshotCaptureFailureKind.Import)
        {
            throw new ArgumentOutOfRangeException(nameof(failureKind));
        }

        FailureKind = failureKind;
    }

    public ScreenshotCaptureFailureKind FailureKind { get; }
}
