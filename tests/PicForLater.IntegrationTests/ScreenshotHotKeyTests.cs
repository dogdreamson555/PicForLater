using PicForLater.App.Models;
using PicForLater.App.Services;

namespace PicForLater.IntegrationTests;

public sealed class ScreenshotHotKeyTests
{
    [Fact]
    public void Default_IsDisabledPreferenceCandidateAndFormatsWithoutVirtualKeyNumbers()
    {
        ScreenshotHotKey hotKey = ScreenshotHotKey.Default;

        Assert.Equal(ScreenshotHotKeyModifiers.Win | ScreenshotHotKeyModifiers.Alt, hotKey.Modifiers);
        Assert.Equal(ScreenshotHotKeyKey.X, hotKey.Key);
        Assert.Equal("Win + Alt + X", hotKey.ToString());
    }

    [Theory]
    [InlineData(ScreenshotHotKeyModifiers.Control, ScreenshotHotKeyKey.A)]
    [InlineData(ScreenshotHotKeyModifiers.Alt | ScreenshotHotKeyModifiers.Shift, ScreenshotHotKeyKey.D0)]
    [InlineData(ScreenshotHotKeyModifiers.Win | ScreenshotHotKeyModifiers.Control, ScreenshotHotKeyKey.Z)]
    public void Pack_RoundTripsSupportedCombinations(
        ScreenshotHotKeyModifiers modifiers,
        ScreenshotHotKeyKey key)
    {
        var expected = new ScreenshotHotKey(modifiers, key);

        Assert.True(ScreenshotHotKey.TryUnpack(expected.Pack(), out ScreenshotHotKey actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData((int)ScreenshotHotKeyModifiers.Shift, (int)ScreenshotHotKeyKey.A)]
    [InlineData(0x10, (int)ScreenshotHotKeyKey.A)]
    [InlineData((int)ScreenshotHotKeyModifiers.Win, 0x7B)]
    [InlineData((int)ScreenshotHotKeyModifiers.Win, 0x20)]
    [InlineData((int)ScreenshotHotKeyModifiers.Win, 0xFFFF)]
    public void TryUnpack_RejectsInvalidModifiersF12AndUnknownKeys(int modifiers, int virtualKey)
    {
        int packed = unchecked((modifiers << 16) | (virtualKey & 0xFFFF));

        Assert.False(ScreenshotHotKey.TryUnpack(packed, out _));
    }

    [Fact]
    public void Constructor_RejectsNoEffectiveModifier()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ScreenshotHotKey(ScreenshotHotKeyModifiers.None, ScreenshotHotKeyKey.X));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ScreenshotHotKey(ScreenshotHotKeyModifiers.Shift, ScreenshotHotKeyKey.X));
    }
}

public sealed class ScreenshotCapturePreferenceServiceTests
{
    [Fact]
    public void Read_MissingValuesUsesDisabledAndDefaultHotKey()
    {
        var service = new ScreenshotCapturePreferenceService(new MemoryInt32Store());

        Assert.Equal(
            new ScreenshotCapturePreferences(false, ScreenshotHotKey.Default),
            service.Read());
    }

    [Fact]
    public void Read_CorruptValuesNeverEnablesAndFallsBackToDefaultHotKey()
    {
        var store = new MemoryInt32Store();
        store.Values[ScreenshotCapturePreferenceService.EnabledPreferenceKey] = 2;
        store.Values[ScreenshotCapturePreferenceService.HotKeyPreferenceKey] =
            unchecked((0x10 << 16) | 0x7B);
        var service = new ScreenshotCapturePreferenceService(store);

        Assert.Equal(
            new ScreenshotCapturePreferences(false, ScreenshotHotKey.Default),
            service.Read());
    }

    [Fact]
    public void Setters_RoundTripEnabledAndOnePackedHotKeyValue()
    {
        var store = new MemoryInt32Store();
        var service = new ScreenshotCapturePreferenceService(store);
        var hotKey = new ScreenshotHotKey(
            ScreenshotHotKeyModifiers.Control | ScreenshotHotKeyModifiers.Alt,
            ScreenshotHotKeyKey.D8);

        service.SetHotKey(hotKey);
        service.SetEnabled(true);

        Assert.Equal(1, store.Values[ScreenshotCapturePreferenceService.EnabledPreferenceKey]);
        Assert.Equal(hotKey.Pack(), store.Values[ScreenshotCapturePreferenceService.HotKeyPreferenceKey]);
        Assert.Equal(new ScreenshotCapturePreferences(true, hotKey), service.Read());
    }

    private sealed class MemoryInt32Store : IInt32PreferenceStore
    {
        internal Dictionary<string, int> Values { get; } = new(StringComparer.Ordinal);

        public bool TryGetInt32(string key, out int value) => Values.TryGetValue(key, out value);

        public void SetInt32(string key, int value) => Values[key] = value;
    }
}
