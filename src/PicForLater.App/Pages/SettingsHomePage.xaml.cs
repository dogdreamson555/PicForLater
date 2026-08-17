using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PicForLater.App.Services;
using PicForLater.App.ViewModels;

namespace PicForLater.App.Pages;

public sealed partial class SettingsHomePage : Page
{
    private bool _synchronizingAnalysisSource;

    public SettingsHomePageViewModel ViewModel { get; } = new(
        ThemePreferenceService.Instance,
        App.StorageReadiness,
        () => App.RemoteApiProfiles,
        () => App.RemoteApiCredentials);

    public SettingsHomePage()
    {
        InitializeComponent();
        Loaded += SettingsHomePage_Loaded;
    }

    private async void SettingsHomePage_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _synchronizingAnalysisSource = true;
            await ViewModel.InitializeAsync();
        }
        finally
        {
            _synchronizingAnalysisSource = false;
        }
    }

    public static bool IsSelected(int selectedIndex, int candidateIndex) =>
        selectedIndex == candidateIndex;

    private async void AnalysisSourceRadioButton_Checked(object sender, RoutedEventArgs e)
    {
        if (_synchronizingAnalysisSource
            || !ViewModel.IsInitialized
            || sender is not RadioButton { Tag: string selectedIndexText }
            || !int.TryParse(selectedIndexText, out var selectedIndex))
        {
            return;
        }

        try
        {
            _synchronizingAnalysisSource = true;
            var outcome = await ViewModel.SelectAnalysisSourceAsync(selectedIndex);
            if (outcome == AnalysisSourceSelectionOutcome.RequiresApiConfiguration)
            {
                SettingsPage.RequestNavigation(typeof(ApiAnalysisSettingsPage));
            }
        }
        catch
        {
            await ViewModel.InitializeAsync();
        }
        finally
        {
            _synchronizingAnalysisSource = false;
        }
    }

    private void OpenLocalAnalysisSettingsButton_Click(object sender, RoutedEventArgs e) =>
        SettingsPage.RequestNavigation(typeof(LocalAnalysisSettingsPage));

    private void OpenApiAnalysisSettingsButton_Click(object sender, RoutedEventArgs e) =>
        SettingsPage.RequestNavigation(typeof(ApiAnalysisSettingsPage));
}
