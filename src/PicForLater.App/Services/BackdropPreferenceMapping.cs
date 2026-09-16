using PicForLater.App.Models;

namespace PicForLater.App.Services;

internal static class BackdropPreferenceMapping
{
    internal const uint DwmSystemBackdropMainWindow = 2;
    internal const uint DwmSystemBackdropTabbedWindow = 4;

    internal static uint ToDwmSystemBackdropType(AppBackdropPreference preference) =>
        preference switch
        {
            AppBackdropPreference.Mica => DwmSystemBackdropMainWindow,
            AppBackdropPreference.MicaAlt => DwmSystemBackdropTabbedWindow,
            _ => throw new ArgumentOutOfRangeException(nameof(preference)),
        };
}
