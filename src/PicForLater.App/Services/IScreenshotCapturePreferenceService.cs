using PicForLater.App.Models;

namespace PicForLater.App.Services;

public interface IScreenshotCapturePreferenceService
{
    ScreenshotCapturePreferences Read();

    void SetEnabled(bool isEnabled);

    void SetHotKey(ScreenshotHotKey hotKey);
}
