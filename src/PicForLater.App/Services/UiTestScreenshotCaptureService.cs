#if PICFORLATER_UI_TESTING
using PicForLater.App.Models;

namespace PicForLater.App.Services;

internal sealed class UiTestScreenshotCaptureService : IScreenshotCaptureService
{
    private ScreenshotCaptureSnapshot _snapshot = ScreenshotCaptureSnapshot.Default;
    private bool _started;

    public ScreenshotCaptureSnapshot Snapshot => _snapshot;

    public event EventHandler<ScreenshotCaptureSnapshotChangedEventArgs>? SnapshotChanged;

    public event EventHandler<ScreenshotCaptureCompletedEventArgs>? CaptureCompleted;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _started = true;
        Publish(_snapshot);
        return Task.CompletedTask;
    }

    public Task<ScreenshotSettingsOperationResult> SetEnabledAsync(
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_started)
        {
            return Task.FromResult(ScreenshotSettingsOperationResult.Failed(
                ScreenshotSettingsFailureKind.NotStarted));
        }

        Publish(_snapshot with
        {
            IsEnabledRequested = isEnabled,
            RegistrationState = isEnabled ? RegistrationState.Ready : RegistrationState.Disabled,
            CaptureState = CaptureState.Idle,
        });
        return Task.FromResult(ScreenshotSettingsOperationResult.Success);
    }

    public Task<ScreenshotSettingsOperationResult> UpdateHotKeyAsync(
        ScreenshotHotKey hotKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_started)
        {
            return Task.FromResult(ScreenshotSettingsOperationResult.Failed(
                ScreenshotSettingsFailureKind.NotStarted));
        }

        // Q is deterministic contention for dialog UI automation. No global
        // hotkey is registered and no user preference is read or written.
        if (hotKey.Key == ScreenshotHotKeyKey.Q)
        {
            return Task.FromResult(ScreenshotSettingsOperationResult.Failed(
                ScreenshotSettingsFailureKind.HotKeyConflict));
        }

        Publish(_snapshot with { HotKey = hotKey });
        return Task.FromResult(ScreenshotSettingsOperationResult.Success);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _started = false;
        Publish(_snapshot with
        {
            RegistrationState = RegistrationState.Disabled,
            CaptureState = CaptureState.Idle,
        });
        return Task.CompletedTask;
    }

    private void Publish(ScreenshotCaptureSnapshot snapshot)
    {
        _snapshot = snapshot;
        SnapshotChanged?.Invoke(this, new ScreenshotCaptureSnapshotChangedEventArgs(snapshot));
    }

    // Kept to satisfy the same surface as production. The fake never completes
    // a capture because it never touches SendInput or the Clipboard.
    private void PublishCapture(ScreenshotCaptureResult result) =>
        CaptureCompleted?.Invoke(this, new ScreenshotCaptureCompletedEventArgs(result));
}
#endif
