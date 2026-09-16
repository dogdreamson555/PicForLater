using PicForLater.App.Models;

namespace PicForLater.App.Services;

public interface IBackdropPreferenceService
{
    AppBackdropPreference CurrentPreference { get; }

    event EventHandler? PreferenceChanged;

    void SetPreference(AppBackdropPreference preference);
}
