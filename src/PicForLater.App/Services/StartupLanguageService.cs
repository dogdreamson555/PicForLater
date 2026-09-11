using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Windows.Globalization;

namespace PicForLater.App.Services;

/// <summary>
/// Selects the language used by MRT Core resources for the current app process.
/// </summary>
internal static class StartupLanguageService
{
    internal static void ApplySystemLanguageOverride()
    {
        var systemLanguageTag = TryGetSystemLanguageTag();
        var requestedLanguageTag =
            StartupLanguageResolver.ResolveApplicationLanguage(systemLanguageTag);
        var applicationLanguageTag =
            StartupLanguageResolver.ResolveAvailableApplicationLanguage(systemLanguageTag);

        if (TryApplyLanguageOverride(applicationLanguageTag))
        {
            Debug.WriteLine(
                $"Startup language resolved from '{systemLanguageTag ?? "<unknown>"}' " +
                $"to requested '{requestedLanguageTag}', active '{applicationLanguageTag}'.");
            return;
        }

        if (!string.Equals(
                applicationLanguageTag,
                StartupLanguageResolver.EnglishLanguageTag,
                StringComparison.Ordinal) &&
            TryApplyLanguageOverride(StartupLanguageResolver.EnglishLanguageTag))
        {
            Debug.WriteLine(
                $"Startup language fell back from '{applicationLanguageTag}' " +
                $"to '{StartupLanguageResolver.EnglishLanguageTag}' after the initial override failed.");
        }
    }

    private static string? TryGetSystemLanguageTag()
    {
        try
        {
            return StartupLanguageResolver.ResolveSystemLanguageTag(
                GetUserDefaultUILanguage());
        }
        catch (Exception exception)
        {
            Debug.WriteLine(
                $"System UI language detection failed: {exception.GetType().Name}.");
            return null;
        }
    }

    private static bool TryApplyLanguageOverride(string languageTag)
    {
        try
        {
            ApplicationLanguages.PrimaryLanguageOverride = languageTag;
            return true;
        }
        catch (Exception exception)
        {
            // A resource override must not prevent the app from opening. The
            // caller attempts the explicit en-US fallback when possible.
            Debug.WriteLine(
                $"Startup language override '{languageTag}' failed: " +
                $"{exception.GetType().Name}.");
            return false;
        }
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern ushort GetUserDefaultUILanguage();
}
