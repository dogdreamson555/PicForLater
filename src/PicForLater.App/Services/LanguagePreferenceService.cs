using PicForLater.App.Models;

namespace PicForLater.App.Services;

/// <summary>
/// Persists the interface language choice without changing the current process language.
/// </summary>
public sealed class LanguagePreferenceService : ILanguagePreferenceService
{
    internal const string PreferenceKey = "Interface.Language";

    private readonly IInt32PreferenceStore _store;
    private string? _systemLanguageTag;

    private LanguagePreferenceService()
        : this(LocalPreferenceStore.Instance)
    {
    }

    internal LanguagePreferenceService(IInt32PreferenceStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        CurrentPreference = ReadPreference();
#if PICFORLATER_UI_VISUAL_FIXTURE
        // The visual fixture intentionally skips the production override and
        // fixes the process/resource culture to Simplified Chinese.
        _systemLanguageTag = "zh-CN";
        CurrentStartupLanguageTag = "zh-CN";
#endif
    }

    public static LanguagePreferenceService Instance { get; } = new();

    public AppLanguagePreference CurrentPreference { get; private set; }

    public string CurrentStartupLanguageTag { get; private set; } =
        StartupLanguageResolver.EnglishLanguageTag;

    public string ResolveEffectiveLanguageTag(AppLanguagePreference preference) =>
        ResolveEffectiveLanguageTag(preference, _systemLanguageTag);

    public void SetPreference(AppLanguagePreference preference)
    {
        if (!Enum.IsDefined(preference))
        {
            throw new ArgumentOutOfRangeException(nameof(preference));
        }

        if (preference == CurrentPreference)
        {
            return;
        }

        // Update the in-memory value only after the durable write succeeds.
        _store.SetInt32(PreferenceKey, (int)preference);
        CurrentPreference = preference;
    }

    internal string ResolveRequestedLanguageTag(
        AppLanguagePreference preference,
        string? systemLanguageTag) =>
        StartupLanguageResolver.ResolvePreferenceLanguage(
            preference,
            systemLanguageTag);

    internal void SetStartupLanguageContext(
        string? systemLanguageTag,
        string activeLanguageTag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activeLanguageTag);
        _systemLanguageTag = systemLanguageTag;
        CurrentStartupLanguageTag = activeLanguageTag;
    }

    private string ResolveEffectiveLanguageTag(
        AppLanguagePreference preference,
        string? systemLanguageTag)
    {
        var requestedLanguageTag = ResolveRequestedLanguageTag(
            preference,
            systemLanguageTag);
        return StartupLanguageResolver.ResolveAvailableApplicationLanguage(
            requestedLanguageTag);
    }

    private AppLanguagePreference ReadPreference()
    {
        if (_store.TryGetInt32(PreferenceKey, out var numericValue)
            && Enum.IsDefined(typeof(AppLanguagePreference), numericValue))
        {
            return (AppLanguagePreference)numericValue;
        }

        return AppLanguagePreference.System;
    }
}
