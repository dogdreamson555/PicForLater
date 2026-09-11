using PicForLater.App.Models;

namespace PicForLater.App.Services;

/// <summary>
/// Resolves Windows language identifiers and BCP-47 tags to the app's language tags.
/// </summary>
internal static class StartupLanguageResolver
{
    internal const string EnglishLanguageTag = "en-US";

    private const string SimplifiedChineseLanguageTag = "zh-CN";
    private const string TraditionalChineseLanguageTag = "zh-TW";
    private const string NeutralChineseLanguageTag = "zh";
    private const ushort PrimaryLanguageIdMask = 0x03FF;
    private const int SubLanguageIdShift = 10;
    private const ushort ChinesePrimaryLanguageId = 0x0004;
    private const ushort CustomUserInterfaceLanguage = 0x1400;
    private const int ChineseTraditionalSubLanguageId = 1;
    private const int ChineseHongKongSubLanguageId = 3;
    private const int ChineseMacauSubLanguageId = 5;
    private const int ChineseSimplifiedSubLanguageId = 2;
    private const int ChineseSingaporeSubLanguageId = 4;

    internal static string ResolveApplicationLanguage(string? languageTag)
    {
        if (string.IsNullOrWhiteSpace(languageTag))
        {
            return EnglishLanguageTag;
        }

        var subtags = languageTag.Trim().Split('-');
        if (subtags.Length == 0 ||
            subtags.Any(static subtag => subtag.Length == 0) ||
            !string.Equals(subtags[0], "zh", StringComparison.OrdinalIgnoreCase))
        {
            return EnglishLanguageTag;
        }

        // The script is authoritative even when the region is unusual, for
        // example zh-Hans-TW must still use the simplified Chinese resources.
        var subtagIndex = 1;
        string? script = subtags.Length > subtagIndex &&
                         subtags[subtagIndex].Length == 4
            ? subtags[subtagIndex++]
            : null;
        if (string.Equals(script, "Hans", StringComparison.OrdinalIgnoreCase))
        {
            return SimplifiedChineseLanguageTag;
        }

        if (string.Equals(script, "Hant", StringComparison.OrdinalIgnoreCase))
        {
            return TraditionalChineseLanguageTag;
        }

        string? region = subtags.Length > subtagIndex &&
                         subtags[subtagIndex].Length == 2
            ? subtags[subtagIndex]
            : null;
        return region?.ToUpperInvariant() switch
        {
            "CN" or "SG" => SimplifiedChineseLanguageTag,
            "TW" or "HK" or "MO" => TraditionalChineseLanguageTag,
            _ => EnglishLanguageTag,
        };
    }

    internal static string ResolvePreferenceLanguage(
        AppLanguagePreference preference,
        string? systemLanguageTag) =>
        preference switch
        {
            AppLanguagePreference.SimplifiedChinese => SimplifiedChineseLanguageTag,
            AppLanguagePreference.TraditionalChineseTaiwan => TraditionalChineseLanguageTag,
            AppLanguagePreference.English => EnglishLanguageTag,
            _ => ResolveApplicationLanguage(systemLanguageTag),
        };

    internal static string ResolveAvailableApplicationLanguage(string? languageTag)
    {
        var requestedLanguageTag = ResolveApplicationLanguage(languageTag);

        // Step 1 deliberately uses only the existing en-US and zh-CN resource
        // files. Enable zh-TW here when the complete Taiwan resource is added.
        return requestedLanguageTag switch
        {
            SimplifiedChineseLanguageTag or EnglishLanguageTag => requestedLanguageTag,
            _ => EnglishLanguageTag,
        };
    }

    internal static string? ResolveSystemLanguageTag(ushort languageId)
    {
        if (languageId == 0 || languageId == CustomUserInterfaceLanguage)
        {
            return null;
        }

        // LANGID stores the primary language in the low 10 bits and the
        // sublanguage in the remaining 6 bits. This avoids treating a LANGID
        // as an LCID merely to obtain a locale name.
        if ((languageId & PrimaryLanguageIdMask) != ChinesePrimaryLanguageId)
        {
            return null;
        }

        return (languageId >> SubLanguageIdShift) switch
        {
            ChineseTraditionalSubLanguageId or
                ChineseHongKongSubLanguageId or
                ChineseMacauSubLanguageId => TraditionalChineseLanguageTag,
            ChineseSimplifiedSubLanguageId or
                ChineseSingaporeSubLanguageId => SimplifiedChineseLanguageTag,
            _ => NeutralChineseLanguageTag,
        };
    }
}
