using PicForLater.App.Models;

namespace PicForLater.App.Services;

public interface IThemePreferenceService
{
    AppThemePreference CurrentPreference { get; }

    void SetPreference(AppThemePreference preference);
}
