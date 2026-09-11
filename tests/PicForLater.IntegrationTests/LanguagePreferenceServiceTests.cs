using PicForLater.App.Models;
using PicForLater.App.Services;

namespace PicForLater.IntegrationTests;

public sealed class LanguagePreferenceServiceTests
{
    [Fact]
    public void MissingOrInvalidValueFallsBackToSystem()
    {
        var missing = new LanguagePreferenceService(new MemoryInt32Store());
        var invalidStore = new MemoryInt32Store();
        invalidStore.Values[LanguagePreferenceService.PreferenceKey] = 99;
        var invalid = new LanguagePreferenceService(invalidStore);

        Assert.Equal(AppLanguagePreference.System, missing.CurrentPreference);
        Assert.Equal(AppLanguagePreference.System, invalid.CurrentPreference);
    }

    [Theory]
    [InlineData(AppLanguagePreference.System)]
    [InlineData(AppLanguagePreference.SimplifiedChinese)]
    [InlineData(AppLanguagePreference.TraditionalChineseTaiwan)]
    [InlineData(AppLanguagePreference.English)]
    public void SetPreference_PersistsStableEnumValue(AppLanguagePreference preference)
    {
        var store = new MemoryInt32Store();
        store.Values[LanguagePreferenceService.PreferenceKey] =
            (int)(preference == AppLanguagePreference.System
                ? AppLanguagePreference.English
                : AppLanguagePreference.System);
        var service = new LanguagePreferenceService(store);

        service.SetPreference(preference);

        Assert.Equal((int)preference, store.Values[LanguagePreferenceService.PreferenceKey]);
        Assert.Equal(preference, service.CurrentPreference);
    }

    [Fact]
    public void SetPreference_SameValueDoesNotWrite()
    {
        var store = new MemoryInt32Store();
        var service = new LanguagePreferenceService(store);

        service.SetPreference(AppLanguagePreference.System);

        Assert.Equal(0, store.SetCalls);
    }

    [Fact]
    public void SetPreference_WriteFailureKeepsPreviousValue()
    {
        var store = new MemoryInt32Store { ThrowOnSet = true };
        var service = new LanguagePreferenceService(store);

        Assert.Throws<IOException>(
            () => service.SetPreference(AppLanguagePreference.English));
        Assert.Equal(AppLanguagePreference.System, service.CurrentPreference);
    }

    [Theory]
    [InlineData(AppLanguagePreference.System, "zh-CN")]
    [InlineData(AppLanguagePreference.SimplifiedChinese, "zh-CN")]
    [InlineData(AppLanguagePreference.TraditionalChineseTaiwan, "zh-TW")]
    [InlineData(AppLanguagePreference.English, "en-US")]
    public void ResolveEffectiveLanguageTag_UsesPreferenceAndAllResources(
        AppLanguagePreference preference,
        string expected)
    {
        var service = new LanguagePreferenceService(new MemoryInt32Store());
        service.SetStartupLanguageContext("zh-CN", "zh-CN");

        Assert.Equal(expected, service.ResolveEffectiveLanguageTag(preference));
    }

    private sealed class MemoryInt32Store : IInt32PreferenceStore
    {
        internal Dictionary<string, int> Values { get; } = new(StringComparer.Ordinal);

        internal bool ThrowOnSet { get; init; }

        internal int SetCalls { get; private set; }

        public bool TryGetInt32(string key, out int value) => Values.TryGetValue(key, out value);

        public void SetInt32(string key, int value)
        {
            SetCalls++;
            if (ThrowOnSet)
            {
                throw new IOException("Test storage failure.");
            }

            Values[key] = value;
        }
    }
}
