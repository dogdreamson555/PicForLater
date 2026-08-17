using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PicForLater.Core.Analysis;

namespace PicForLater.App.Pages;

public sealed partial class SettingsPage : Page
{
    private static WeakReference<SettingsPage>? _current;
    private bool _initialized;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += SettingsPage_Loaded;
        Unloaded += SettingsPage_Unloaded;
    }

    public static Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility CountToVisibility(int count) =>
        count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility CountToNonEmptyVisibility(int count) =>
        count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public static bool Not(bool value) => !value;

    public static bool HasItems(int count) => count > 0;

    public static InfoBarSeverity StatusSeverity(bool isError) =>
        isError ? InfoBarSeverity.Error : InfoBarSeverity.Success;

    public static InfoBarSeverity NvidiaEnvironmentSeverity(
        NvidiaCudaEnvironmentState? state) => state switch
        {
            NvidiaCudaEnvironmentState.Ready => InfoBarSeverity.Success,
            NvidiaCudaEnvironmentState.RuntimeMissing or
                NvidiaCudaEnvironmentState.RuntimeIncomplete => InfoBarSeverity.Warning,
            null => InfoBarSeverity.Informational,
            _ => InfoBarSeverity.Error,
        };

    public static void RequestNavigation(Type pageType)
    {
        ArgumentNullException.ThrowIfNull(pageType);
        if (_current?.TryGetTarget(out var current) == true)
        {
            current.NavigateTo(pageType);
        }
    }

    private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        _current = new WeakReference<SettingsPage>(this);
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        NavigateTo(typeof(SettingsHomePage));
    }

    private void SettingsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_current?.TryGetTarget(out var current) == true && ReferenceEquals(current, this))
        {
            _current = null;
        }
    }

    private void NavigateTo(Type pageType)
    {
        if (SettingsFrame.CurrentSourcePageType != pageType)
        {
            SettingsFrame.Navigate(pageType);
        }
    }
}
