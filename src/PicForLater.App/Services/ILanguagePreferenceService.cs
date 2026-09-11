using PicForLater.App.Models;

namespace PicForLater.App.Services;

public interface ILanguagePreferenceService
{
    AppLanguagePreference CurrentPreference { get; }

    string CurrentStartupLanguageTag { get; }

    string ResolveEffectiveLanguageTag(AppLanguagePreference preference);

    void SetPreference(AppLanguagePreference preference);
}
