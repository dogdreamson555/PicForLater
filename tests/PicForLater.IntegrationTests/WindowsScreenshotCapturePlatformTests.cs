using PicForLater.App.Models;
using PicForLater.App.Services;

namespace PicForLater.IntegrationTests;

public sealed class WindowsScreenshotCapturePlatformTests
{
    [Theory]
    [InlineData(ScreenshotHotKeyModifiers.None, 0x4000u)]
    [InlineData(ScreenshotHotKeyModifiers.Win | ScreenshotHotKeyModifiers.Alt, 0x4009u)]
    public void NativeModifiers_AddNoRepeatWithoutPersistingItInTheModel(
        ScreenshotHotKeyModifiers source,
        uint expected)
    {
        uint modifiers = WindowsScreenshotCapturePlatform.GetNativeModifiers(
            source);

        Assert.Equal(expected, modifiers);
        Assert.Equal(source, (ScreenshotHotKeyModifiers)(modifiers & ~0x4000u));
    }

    [Fact]
    public void ScreenshotShortcutInputs_AreCompleteOrderedDownUpSequence()
    {
        WindowsScreenshotCapturePlatform.NativeMethods.Input[] inputs =
            WindowsScreenshotCapturePlatform.CreateScreenshotShortcutInputs();

        Assert.Equal(6, inputs.Length);
        Assert.Equal([0x5B, 0x10, 0x53, 0x53, 0x10, 0x5B],
            inputs.Select(input => (int)input.Data.Keyboard.VirtualKey));
        Assert.Equal([0u, 0u, 0u, 0x0002u, 0x0002u, 0x0002u],
            inputs.Select(input => input.Data.Keyboard.Flags));
        Assert.All(inputs, input => Assert.Equal(1u, input.Type));
    }

    [Fact]
    public void InputLayout_MatchesWin32SizeForCurrentArchitecture()
    {
        int expected = Environment.Is64BitProcess ? 40 : 28;

        Assert.Equal(
            expected,
            System.Runtime.InteropServices.Marshal.SizeOf<
                WindowsScreenshotCapturePlatform.NativeMethods.Input>());
    }

    [Fact]
    public void PartialSendCleanup_ReleasesOnlyKeysSynthesizedByTheScreenshotChord()
    {
        WindowsScreenshotCapturePlatform.NativeMethods.Input[] inputs =
            WindowsScreenshotCapturePlatform.CreateScreenshotShortcutCleanupInputs();

        Assert.Equal([0x53, 0x10, 0x5B],
            inputs.Select(input => (int)input.Data.Keyboard.VirtualKey));
        Assert.All(inputs, input => Assert.Equal(0x0002u, input.Data.Keyboard.Flags));
        Assert.All(inputs, input => Assert.Equal(1u, input.Type));
    }
}
