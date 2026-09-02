#if PICFORLATER_UI_TESTING
using PicForLater.App.Models;

namespace PicForLater.App.Services;

internal sealed class UiTestScreenshotCapturePlatform : IScreenshotCapturePlatform, IDisposable
{
    private readonly HashSet<int> _registeredIds = [];

    public event EventHandler<ScreenshotHotKeyPressedEventArgs>? HotKeyPressed;

    public ScreenshotHotKeyRegistrationStatus RegisterHotKey(int hotKeyId, ScreenshotHotKey hotKey)
    {
        if (!_registeredIds.Add(hotKeyId))
        {
            return ScreenshotHotKeyRegistrationStatus.Failed;
        }

        return ScreenshotHotKeyRegistrationStatus.Registered;
    }

    public bool UnregisterHotKey(int hotKeyId) => _registeredIds.Remove(hotKeyId);

    public bool AreCaptureKeysReleased(ScreenshotHotKey hotKey) => true;

    public bool SendScreenshotShortcut() => true;

    public uint GetClipboardSequenceNumber() => 1;

    public ValueTask<ScreenshotClipboardReadResult> ReadClipboardImageAsync(
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(ScreenshotClipboardReadResult.NoImage);

    internal void RaiseHotKeyForTest(int hotKeyId)
    {
        if (_registeredIds.Contains(hotKeyId))
        {
            HotKeyPressed?.Invoke(this, new ScreenshotHotKeyPressedEventArgs(hotKeyId));
        }
    }

    public void Dispose()
    {
        _registeredIds.Clear();
        HotKeyPressed = null;
    }
}
#endif
