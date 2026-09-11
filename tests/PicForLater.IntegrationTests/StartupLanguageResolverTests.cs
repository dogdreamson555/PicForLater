using PicForLater.App.Models;
using PicForLater.App.Services;

namespace PicForLater.IntegrationTests;

public sealed class StartupLanguageResolverTests
{
    [Theory]
    [InlineData("zh-Hant", "zh-TW")]
    [InlineData("zh-Hans", "zh-CN")]
    [InlineData("zh-TW", "zh-TW")]
    [InlineData("zh-HK", "zh-TW")]
    [InlineData("zh-MO", "zh-TW")]
    [InlineData("zh-CN", "zh-CN")]
    [InlineData("zh-SG", "zh-CN")]
    [InlineData("zh-Hans-TW", "zh-CN")]
    [InlineData("zh", "en-US")]
    [InlineData("fr-FR", "en-US")]
    [InlineData("unknown", "en-US")]
    [InlineData("zh--CN", "en-US")]
    [InlineData(null, "en-US")]
    [InlineData("  ", "en-US")]
    public void ResolveApplicationLanguage_MapsLanguageTags(
        string? languageTag,
        string expected)
    {
        Assert.Equal(
            expected,
            StartupLanguageResolver.ResolveApplicationLanguage(languageTag));
    }

    [Theory]
    [InlineData("zh-Hant", "zh-TW")]
    [InlineData("zh-TW", "zh-TW")]
    [InlineData("zh-Hans", "zh-CN")]
    [InlineData("en-US", "en-US")]
    public void ResolveAvailableApplicationLanguage_UsesAllApplicationResources(
        string languageTag,
        string expected)
    {
        Assert.Equal(
            expected,
            StartupLanguageResolver.ResolveAvailableApplicationLanguage(languageTag));
    }

    [Theory]
    [InlineData(AppLanguagePreference.System, "zh-TW", "zh-TW")]
    [InlineData(AppLanguagePreference.SimplifiedChinese, "zh-TW", "zh-CN")]
    [InlineData(AppLanguagePreference.TraditionalChineseTaiwan, "zh-CN", "zh-TW")]
    [InlineData(AppLanguagePreference.English, "zh-CN", "en-US")]
    public void ResolvePreferenceLanguage_ExplicitChoiceWinsOverSystem(
        AppLanguagePreference preference,
        string systemLanguageTag,
        string expected)
    {
        Assert.Equal(
            expected,
            StartupLanguageResolver.ResolvePreferenceLanguage(
                preference,
                systemLanguageTag));
    }

    [Theory]
    [InlineData(0x0404, "zh-TW")]
    [InlineData(0x0804, "zh-CN")]
    [InlineData(0x0C04, "zh-TW")]
    [InlineData(0x1004, "zh-CN")]
    [InlineData(0x1404, "zh-TW")]
    [InlineData(0x0004, "zh")]
    [InlineData(0x0409, null)]
    [InlineData(0x1400, null)]
    public void ResolveSystemLanguageTag_MapsWindowsLanguageIds(
        int languageId,
        string? expected)
    {
        Assert.Equal(
            expected,
            StartupLanguageResolver.ResolveSystemLanguageTag((ushort)languageId));
    }
}
