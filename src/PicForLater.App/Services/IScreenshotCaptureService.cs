using PicForLater.App.Models;

namespace PicForLater.App.Services;

public interface IScreenshotCaptureService
{
    ScreenshotCaptureSnapshot Snapshot { get; }

    event EventHandler<ScreenshotCaptureSnapshotChangedEventArgs>? SnapshotChanged;

    event EventHandler<ScreenshotCaptureCompletedEventArgs>? CaptureCompleted;

    Task StartAsync(CancellationToken cancellationToken = default);

    Task<ScreenshotSettingsOperationResult> SetEnabledAsync(
        bool isEnabled,
        CancellationToken cancellationToken = default);

    Task<ScreenshotSettingsOperationResult> UpdateHotKeyAsync(
        ScreenshotHotKey hotKey,
        CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}

public sealed class ScreenshotCaptureSnapshotChangedEventArgs : EventArgs
{
    public ScreenshotCaptureSnapshotChangedEventArgs(ScreenshotCaptureSnapshot snapshot)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    }

    public ScreenshotCaptureSnapshot Snapshot { get; }
}

public sealed class ScreenshotCaptureCompletedEventArgs : EventArgs
{
    public ScreenshotCaptureCompletedEventArgs(ScreenshotCaptureResult result)
    {
        Result = result ?? throw new ArgumentNullException(nameof(result));
    }

    public ScreenshotCaptureResult Result { get; }
}
