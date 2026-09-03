using PicForLater.App.Models;

namespace PicForLater.App.Services;

internal sealed class ScreenshotCapturePreferenceService : IScreenshotCapturePreferenceService
{
    internal const string EnabledPreferenceKey = "QuickScreenshot.Enabled";
    internal const string HotKeyPreferenceKey = "QuickScreenshot.HotKey";

    private readonly IInt32PreferenceStore _store;

    internal ScreenshotCapturePreferenceService(IInt32PreferenceStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public ScreenshotCapturePreferences Read()
    {
        bool enabled = _store.TryGetInt32(EnabledPreferenceKey, out int enabledValue) &&
            enabledValue == 1;
        ScreenshotHotKey hotKey = _store.TryGetInt32(HotKeyPreferenceKey, out int packedHotKey) &&
            ScreenshotHotKey.TryUnpack(packedHotKey, out ScreenshotHotKey parsedHotKey)
                ? parsedHotKey
                : ScreenshotHotKey.Default;
        return new ScreenshotCapturePreferences(enabled, hotKey);
    }

    public void SetEnabled(bool isEnabled) =>
        _store.SetInt32(EnabledPreferenceKey, isEnabled ? 1 : 0);

    public void SetHotKey(ScreenshotHotKey hotKey)
    {
        if (!ScreenshotHotKey.IsValid(hotKey.Modifiers, hotKey.Key))
        {
            throw new ArgumentOutOfRangeException(nameof(hotKey));
        }

        _store.SetInt32(HotKeyPreferenceKey, hotKey.Pack());
    }
}
