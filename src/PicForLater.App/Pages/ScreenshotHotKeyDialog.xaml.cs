using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.ApplicationModel.Resources;
using PicForLater.App.Models;
using PicForLater.App.ViewModels;

namespace PicForLater.App.Pages;

public sealed partial class ScreenshotHotKeyDialog : ContentDialog, INotifyPropertyChanged
{
    private static readonly ResourceLoader ResourceStrings = new();
    private readonly Func<ScreenshotHotKey, Task<ScreenshotSettingsOperationResult>> _saveAsync;
    private bool _initializing;
    private ScreenshotHotKeyKey _selectedKey = ScreenshotHotKey.Default.Key;
    private string _keyText = ScreenshotHotKey.FormatKey(ScreenshotHotKey.Default.Key);
    private string _previewText = string.Empty;

    public ScreenshotHotKeyDialog(
        ScreenshotHotKey currentHotKey,
        Func<ScreenshotHotKey, Task<ScreenshotSettingsOperationResult>> saveAsync)
    {
        _saveAsync = saveAsync ?? throw new ArgumentNullException(nameof(saveAsync));

        _initializing = true;
        InitializeComponent();
        KeyCaptureTextBox.AddHandler(
            UIElement.KeyDownEvent,
            new KeyEventHandler(KeyCaptureTextBox_KeyDown),
            handledEventsToo: true);
        ApplyForm(currentHotKey);
        _initializing = false;
        UpdatePreview();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string KeyText
    {
        get => _keyText;
        private set
        {
            if (_keyText == value)
            {
                return;
            }

            _keyText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(KeyText)));
        }
    }

    public string PreviewText
    {
        get => _previewText;
        private set
        {
            if (_previewText == value)
            {
                return;
            }

            _previewText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PreviewText)));
        }
    }

    private void ModifierCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initializing)
        {
            UpdatePreview();
        }
    }

    private void KeyCaptureTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        int originalKey = (int)e.OriginalKey;
        if (originalKey == 0)
        {
            originalKey = (int)e.Key;
        }
        if (originalKey is 0x09 or 0x14)
        {
            // Tab remains normal focus navigation and Caps Lock keeps its normal
            // system behavior. Both are available as explicit buttons below.
            return;
        }

        if (originalKey is 0x0D or 0x1B || IsModifierKey(originalKey))
        {
            return;
        }

        var key = (ScreenshotHotKeyKey)originalKey;
        if (!ScreenshotHotKey.IsSupportedKey(key))
        {
            ShowError(ResourceStrings.GetString("ScreenshotHotKeyUnsupportedKeyMessage"));
            e.Handled = true;
            return;
        }

        SelectKey(key);
        e.Handled = true;
    }

    private void ScreenshotHotKeyTabOption_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initializing)
        {
            SelectKey(ScreenshotHotKeyKey.Tab);
        }
    }

    private void ScreenshotHotKeyCapsLockOption_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initializing)
        {
            SelectKey(ScreenshotHotKeyKey.CapitalLock);
        }
    }

    private void ResetScreenshotHotKeyButton_Click(object sender, RoutedEventArgs e)
    {
        _initializing = true;
        ApplyForm(ScreenshotHotKey.Default);
        _initializing = false;
        HideError();
        UpdatePreview();
    }

    private async void DialogRoot_PrimaryButtonClick(
        ContentDialog sender,
        ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        var deferral = args.GetDeferral();
        try
        {
            if (!TryCreateCandidate(out var candidate))
            {
                ShowError(ResourceStrings.GetString("ScreenshotHotKeyInvalidMessage"));
                return;
            }

            IsPrimaryButtonEnabled = false;
            HideError();
            ScreenshotSettingsOperationResult result;
            try
            {
                result = await _saveAsync(candidate);
            }
            catch
            {
                result = ScreenshotSettingsOperationResult.Failed(
                    ScreenshotSettingsFailureKind.Registration);
            }

            if (!result.Succeeded)
            {
                ShowError(ScreenshotCaptureSettingsViewModel.SettingsFailureMessage(
                    result.FailureKind));
                return;
            }

            args.Cancel = false;
        }
        finally
        {
            IsPrimaryButtonEnabled = true;
            deferral.Complete();
        }
    }

    private void ApplyForm(ScreenshotHotKey hotKey)
    {
        WinModifierCheckBox.IsChecked = hotKey.Modifiers.HasFlag(ScreenshotHotKeyModifiers.Win);
        ControlModifierCheckBox.IsChecked =
            hotKey.Modifiers.HasFlag(ScreenshotHotKeyModifiers.Control);
        AltModifierCheckBox.IsChecked = hotKey.Modifiers.HasFlag(ScreenshotHotKeyModifiers.Alt);
        ShiftModifierCheckBox.IsChecked =
            hotKey.Modifiers.HasFlag(ScreenshotHotKeyModifiers.Shift);
        SelectKey(hotKey.Key);
    }

    private bool TryCreateCandidate(out ScreenshotHotKey candidate)
    {
        var modifiers = GetModifiers();
        if (!ScreenshotHotKey.IsValid(modifiers, _selectedKey))
        {
            candidate = default;
            return false;
        }

        candidate = new ScreenshotHotKey(modifiers, _selectedKey);
        return true;
    }

    private ScreenshotHotKeyModifiers GetModifiers()
    {
        var modifiers = ScreenshotHotKeyModifiers.None;
        AddIfChecked(WinModifierCheckBox, ScreenshotHotKeyModifiers.Win, ref modifiers);
        AddIfChecked(ControlModifierCheckBox, ScreenshotHotKeyModifiers.Control, ref modifiers);
        AddIfChecked(AltModifierCheckBox, ScreenshotHotKeyModifiers.Alt, ref modifiers);
        AddIfChecked(ShiftModifierCheckBox, ScreenshotHotKeyModifiers.Shift, ref modifiers);
        return modifiers;
    }

    private void UpdatePreview()
    {
        var parts = new List<string>(5);
        var modifiers = GetModifiers();
        AddPreviewPart(modifiers, ScreenshotHotKeyModifiers.Win, "Win", parts);
        AddPreviewPart(modifiers, ScreenshotHotKeyModifiers.Control, "Ctrl", parts);
        AddPreviewPart(modifiers, ScreenshotHotKeyModifiers.Alt, "Alt", parts);
        AddPreviewPart(modifiers, ScreenshotHotKeyModifiers.Shift, "Shift", parts);
        string keyText = ScreenshotHotKey.FormatKey(_selectedKey, modifiers);
        KeyText = keyText;
        parts.Add(keyText);

        PreviewText = string.Join(" + ", parts);
        HideError();
    }

    private void SelectKey(ScreenshotHotKeyKey key)
    {
        bool wasInitializing = _initializing;
        _initializing = true;
        TabKeyOption.IsChecked = key == ScreenshotHotKeyKey.Tab;
        CapsLockKeyOption.IsChecked = key == ScreenshotHotKeyKey.CapitalLock;
        _initializing = wasInitializing;
        _selectedKey = key;
        UpdatePreview();
    }

    private void ShowError(string message)
    {
        DialogErrorInfoBar.Message = message;
        DialogErrorInfoBar.IsOpen = true;
    }

    private void HideError()
    {
        DialogErrorInfoBar.Message = string.Empty;
        DialogErrorInfoBar.IsOpen = false;
    }

    private static void AddIfChecked(
        CheckBox checkBox,
        ScreenshotHotKeyModifiers modifier,
        ref ScreenshotHotKeyModifiers modifiers)
    {
        if (checkBox.IsChecked == true)
        {
            modifiers |= modifier;
        }
    }

    private static void AddPreviewPart(
        ScreenshotHotKeyModifiers modifiers,
        ScreenshotHotKeyModifiers candidate,
        string label,
        ICollection<string> parts)
    {
        if (modifiers.HasFlag(candidate))
        {
            parts.Add(label);
        }
    }

    private static bool IsModifierKey(int virtualKey) =>
        virtualKey is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or
            >= 0xA0 and <= 0xA5;
}
