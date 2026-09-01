using PicForLater.App.Models;

namespace PicForLater.App.Services;

public interface IScreenshotCapturePreferenceService
{
    ScreenshotCapturePreferences Read();

    void SetEnabled(bool isEnabled);

    void SetHotKey(ScreenshotHotKey hotKey);
}

internal interface IInt32PreferenceStore
{
    bool TryGetInt32(string key, out int value);

    void SetInt32(string key, int value);
}
