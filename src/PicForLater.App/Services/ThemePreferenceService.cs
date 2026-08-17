using Microsoft.UI.Xaml;
using PicForLater.App.Models;

namespace PicForLater.App.Services;

/// <summary>
/// Applies and persists the local theme preference for the unpackaged app.
/// </summary>
public sealed class ThemePreferenceService : IThemePreferenceService
{
    private const string ThemePreferenceKey = "Appearance.Theme";
    private FrameworkElement? _themeRoot;

    private ThemePreferenceService()
    {
#if PICFORLATER_UI_VISUAL_FIXTURE
        CurrentPreference = AppThemePreference.Light;
        LocalPreferenceStore.Instance.SetInt32(
            ThemePreferenceKey,
            (int)AppThemePreference.Light);
#else
        CurrentPreference = ReadPreference();
#endif
    }

    public static ThemePreferenceService Instance { get; } = new();

    public AppThemePreference CurrentPreference { get; private set; }

    public void Initialize(FrameworkElement themeRoot)
    {
        _themeRoot = themeRoot ?? throw new ArgumentNullException(nameof(themeRoot));
        ApplyPreference();
    }

    public void SetPreference(AppThemePreference preference)
    {
        if (!Enum.IsDefined(preference))
        {
            throw new ArgumentOutOfRangeException(nameof(preference));
        }

        LocalPreferenceStore.Instance.SetInt32(ThemePreferenceKey, (int)preference);
        CurrentPreference = preference;
        ApplyPreference();
    }

    private static AppThemePreference ReadPreference()
    {
        if (!LocalPreferenceStore.Instance.TryGetInt32(
                ThemePreferenceKey,
                out var numericValue))
        {
            return AppThemePreference.System;
        }

        if (Enum.IsDefined(typeof(AppThemePreference), numericValue))
        {
            return (AppThemePreference)numericValue;
        }

        return AppThemePreference.System;
    }

    private void ApplyPreference()
    {
        if (_themeRoot is null)
        {
            return;
        }

        _themeRoot.RequestedTheme = CurrentPreference switch
        {
            AppThemePreference.Light => ElementTheme.Light,
            AppThemePreference.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }
}
