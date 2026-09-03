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
    [InlineData(ScreenshotHotKeyModifiers.None, ScreenshotHotKeyKey.X)]
    [InlineData(ScreenshotHotKeyModifiers.Shift, ScreenshotHotKeyKey.X)]
    [InlineData(ScreenshotHotKeyModifiers.None, ScreenshotHotKeyKey.F1)]
    [InlineData(ScreenshotHotKeyModifiers.None, ScreenshotHotKeyKey.F12)]
    [InlineData(ScreenshotHotKeyModifiers.None, ScreenshotHotKeyKey.Tab)]
    [InlineData(ScreenshotHotKeyModifiers.None, ScreenshotHotKeyKey.OemQuestion)]
    [InlineData(ScreenshotHotKeyModifiers.Control, ScreenshotHotKeyKey.A)]
    [InlineData(ScreenshotHotKeyModifiers.Alt | ScreenshotHotKeyModifiers.Shift, ScreenshotHotKeyKey.D0)]
    [InlineData(ScreenshotHotKeyModifiers.Win | ScreenshotHotKeyModifiers.Control, ScreenshotHotKeyKey.Z)]
    [InlineData(ScreenshotHotKeyModifiers.Control | ScreenshotHotKeyModifiers.Alt, ScreenshotHotKeyKey.Tab)]
    [InlineData(ScreenshotHotKeyModifiers.Win, ScreenshotHotKeyKey.CapitalLock)]
    [InlineData(ScreenshotHotKeyModifiers.Alt, ScreenshotHotKeyKey.NumberPad1)]
    [InlineData(ScreenshotHotKeyModifiers.Control, ScreenshotHotKeyKey.Decimal)]
    [InlineData(ScreenshotHotKeyModifiers.Win | ScreenshotHotKeyModifiers.Shift, ScreenshotHotKeyKey.F12)]
    [InlineData(ScreenshotHotKeyModifiers.Control, ScreenshotHotKeyKey.OemQuestion)]
    [InlineData(ScreenshotHotKeyModifiers.Alt | ScreenshotHotKeyModifiers.Shift, ScreenshotHotKeyKey.OemPipe)]
    public void Pack_RoundTripsSupportedCombinations(
        ScreenshotHotKeyModifiers modifiers,
        ScreenshotHotKeyKey key)
    {
        var expected = new ScreenshotHotKey(modifiers, key);

        Assert.True(ScreenshotHotKey.TryUnpack(expected.Pack(), out ScreenshotHotKey actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(ScreenshotHotKeyKey.Tab, "Ctrl + Tab")]
    [InlineData(ScreenshotHotKeyKey.CapitalLock, "Ctrl + Caps Lock")]
    [InlineData(ScreenshotHotKeyKey.NumberPad1, "Ctrl + Num 1")]
    [InlineData(ScreenshotHotKeyKey.Decimal, "Ctrl + Num .")]
    [InlineData(ScreenshotHotKeyKey.F1, "Ctrl + F1")]
    [InlineData(ScreenshotHotKeyKey.F12, "Ctrl + F12")]
    public void ToString_FormatsExtendedPrimaryKeys(
        ScreenshotHotKeyKey key,
        string expected)
    {
        var hotKey = new ScreenshotHotKey(ScreenshotHotKeyModifiers.Control, key);

        Assert.Equal(expected, hotKey.ToString());
    }

    [Theory]
    [InlineData(ScreenshotHotKeyModifiers.Control, ScreenshotHotKeyKey.OemComma, "Ctrl + ,")]
    [InlineData(ScreenshotHotKeyModifiers.Control | ScreenshotHotKeyModifiers.Shift, ScreenshotHotKeyKey.OemComma, "Ctrl + Shift + <")]
    [InlineData(ScreenshotHotKeyModifiers.Control, ScreenshotHotKeyKey.OemPeriod, "Ctrl + .")]
    [InlineData(ScreenshotHotKeyModifiers.Control | ScreenshotHotKeyModifiers.Shift, ScreenshotHotKeyKey.OemPeriod, "Ctrl + Shift + >")]
    [InlineData(ScreenshotHotKeyModifiers.Control, ScreenshotHotKeyKey.OemQuestion, "Ctrl + /")]
    [InlineData(ScreenshotHotKeyModifiers.Control | ScreenshotHotKeyModifiers.Shift, ScreenshotHotKeyKey.OemQuestion, "Ctrl + Shift + ?")]
    [InlineData(ScreenshotHotKeyModifiers.Control, ScreenshotHotKeyKey.OemSemicolon, "Ctrl + ;")]
    [InlineData(ScreenshotHotKeyModifiers.Control | ScreenshotHotKeyModifiers.Shift, ScreenshotHotKeyKey.OemSemicolon, "Ctrl + Shift + :")]
    [InlineData(ScreenshotHotKeyModifiers.Control, ScreenshotHotKeyKey.OemQuotes, "Ctrl + '")]
    [InlineData(ScreenshotHotKeyModifiers.Control | ScreenshotHotKeyModifiers.Shift, ScreenshotHotKeyKey.OemQuotes, "Ctrl + Shift + \"")]
    [InlineData(ScreenshotHotKeyModifiers.Control, ScreenshotHotKeyKey.OemOpenBrackets, "Ctrl + [")]
    [InlineData(ScreenshotHotKeyModifiers.Control | ScreenshotHotKeyModifiers.Shift, ScreenshotHotKeyKey.OemOpenBrackets, "Ctrl + Shift + {")]
    [InlineData(ScreenshotHotKeyModifiers.Control, ScreenshotHotKeyKey.OemCloseBrackets, "Ctrl + ]")]
    [InlineData(ScreenshotHotKeyModifiers.Control | ScreenshotHotKeyModifiers.Shift, ScreenshotHotKeyKey.OemCloseBrackets, "Ctrl + Shift + }")]
    [InlineData(ScreenshotHotKeyModifiers.Control, ScreenshotHotKeyKey.OemPipe, "Ctrl + \u005C")]
    [InlineData(ScreenshotHotKeyModifiers.Control | ScreenshotHotKeyModifiers.Shift, ScreenshotHotKeyKey.OemPipe, "Ctrl + Shift + |")]
    public void ToString_FormatsOemPunctuationForShiftState(
        ScreenshotHotKeyModifiers modifiers,
        ScreenshotHotKeyKey key,
        string expected)
    {
        var hotKey = new ScreenshotHotKey(modifiers, key);

        Assert.Equal(expected, hotKey.ToString());
    }

    [Theory]
    [InlineData(0x10, (int)ScreenshotHotKeyKey.A)]
    [InlineData((int)ScreenshotHotKeyModifiers.Win, 0x7C)]
    [InlineData((int)ScreenshotHotKeyModifiers.Win, 0x6D)]
    [InlineData((int)ScreenshotHotKeyModifiers.Win, 0x20)]
    [InlineData((int)ScreenshotHotKeyModifiers.Win, 0xFFFF)]
    public void TryUnpack_RejectsInvalidModifiersAndUnknownKeys(int modifiers, int virtualKey)
    {
        int packed = unchecked((modifiers << 16) | (virtualKey & 0xFFFF));

        Assert.False(ScreenshotHotKey.TryUnpack(packed, out _));
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
