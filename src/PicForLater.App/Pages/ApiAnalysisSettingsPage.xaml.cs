using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;
using PicForLater.App.Models;
using PicForLater.App.ViewModels;
using PicForLater.Core.Analysis;
using Windows.Foundation;

namespace PicForLater.App.Pages;

public sealed partial class ApiAnalysisSettingsPage : Page
{
    private const double BackButtonLayoutWidth = 52;
    private const double MultiColumnMinimumWidth = 760;
    private const double SettingsControlColumnWidth = 400;
    private const double SettingsStackedRowSpacing = 8;
    private static readonly ResourceLoader ResourceStrings = new();
    private bool _synchronizing;

    public ApiAnalysisSettingsPageViewModel ViewModel { get; } = new(
        App.StorageReadiness,
        () => App.RemoteApiProfiles,
        () => App.RemoteApiCredentials,
        () => App.RemoteApiConnectionTester);

    public ApiAnalysisSettingsPage()
    {
        InitializeComponent();
        Loaded += ApiAnalysisSettingsPage_Loaded;
        SizeChanged += ApiAnalysisSettingsPage_SizeChanged;
    }

    public static string PayloadDisclosure(int selectedInputModeIndex) =>
        ResourceStrings.GetString(selectedInputModeIndex == 1
            ? "ApiRemoteVisionPayloadDisclosure"
            : "ApiRemoteOcrTextPayloadDisclosure");

    public static InfoBarSeverity StatusSeverity(SettingsStatusKind kind) => kind switch
    {
        SettingsStatusKind.Success => InfoBarSeverity.Success,
        SettingsStatusKind.Warning => InfoBarSeverity.Warning,
        SettingsStatusKind.Error => InfoBarSeverity.Error,
        _ => InfoBarSeverity.Informational,
    };

    public static Visibility ElementVisibility(bool visible) =>
        visible ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility InverseElementVisibility(bool visible) =>
        visible ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility CredentialInputVisibility(bool requiresCredential, bool hasCredential) =>
        ElementVisibility(requiresCredential && !hasCredential);

    public static Visibility CredentialSavedVisibility(bool requiresCredential, bool hasCredential) =>
        ElementVisibility(requiresCredential && hasCredential);

    public static Visibility NoCredentialVisibility(bool requiresCredential) =>
        InverseElementVisibility(requiresCredential);

    private async void ApiAnalysisSettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyProviderLayout(ActualWidth >= MultiColumnMinimumWidth);
        ApplyHeaderLayout();
        try
        {
            _synchronizing = true;
            await ViewModel.InitializeAsync();
        }
        catch
        {
            ViewModel.ShowOperationFailure();
        }
        finally
        {
            _synchronizing = false;
        }
    }

    private void ApiAnalysisSettingsPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyProviderLayout(e.NewSize.Width >= MultiColumnMinimumWidth);
        ApplyHeaderLayout();
    }

    private void ApplyHeaderLayout()
    {
        if (PageRoot.ActualWidth <= 0 || PageHeader.ActualWidth <= 0)
        {
            return;
        }

        var headerOrigin = PageHeader.TransformToVisual(PageRoot).TransformPoint(new Point(0, 0));
        var useExternalBackButton = headerOrigin.X >= BackButtonLayoutWidth;
        PageHeaderContent.Margin = useExternalBackButton
            ? new Thickness(0)
            : new Thickness(BackButtonLayoutWidth, 0, 0, 0);
        SettingsBackButton.Margin = useExternalBackButton
            ? new Thickness(-BackButtonLayoutWidth, 0, 0, 0)
            : new Thickness(0);
    }

    private void ApplyProviderLayout(bool useMultipleColumns)
    {
        ApplySettingsRowLayout(ApiCategoryRow, ApiCategoryControlColumn, ApiCategoryComboBox, useMultipleColumns);
        ApplySettingsRowLayout(ApiProviderRow, ApiProviderControlColumn, ApiProviderComboBox, useMultipleColumns);
        ApplySettingsRowLayout(ApiModelRow, ApiModelControlColumn, ApiModelIdTextBox, useMultipleColumns);
        ApplySettingsRowLayout(ApiEndpointRow, ApiEndpointControlColumn, ApiEndpointTextBox, useMultipleColumns);
        ApplySettingsRowLayout(ApiReasoningRow, ApiReasoningControlColumn, ApiReasoningModeComboBox, useMultipleColumns);
        ApplySettingsRowLayout(ApiMaxOutputTokensRow, ApiMaxOutputTokensControlColumn, ApiMaxOutputTokensNumberBox, useMultipleColumns);
        ApplySettingsRowLayout(ApiTimeoutRow, ApiTimeoutControlColumn, ApiTimeoutSecondsNumberBox, useMultipleColumns);

        ApiCustomSecondaryColumn.Width = useMultipleColumns
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);
        Grid.SetRow(ApiCustomProtocolComboBox, 0);
        Grid.SetColumn(ApiCustomProtocolComboBox, 0);
        Grid.SetRow(ApiCustomAuthenticationComboBox, useMultipleColumns ? 0 : 1);
        Grid.SetColumn(ApiCustomAuthenticationComboBox, useMultipleColumns ? 1 : 0);
        Grid.SetRow(ApiCustomStructuredOutputComboBox, useMultipleColumns ? 1 : 2);
        Grid.SetColumn(ApiCustomStructuredOutputComboBox, 0);
        Grid.SetRow(ApiCustomEndpointTrustComboBox, useMultipleColumns ? 1 : 3);
        Grid.SetColumn(ApiCustomEndpointTrustComboBox, useMultipleColumns ? 1 : 0);
        Grid.SetRow(ApiReasoningWireFormatComboBox, useMultipleColumns ? 2 : 4);
        Grid.SetColumn(ApiReasoningWireFormatComboBox, 0);
    }

    private static void ApplySettingsRowLayout(
        Grid row,
        ColumnDefinition controlColumn,
        FrameworkElement control,
        bool useSideBySide)
    {
        controlColumn.Width = useSideBySide
            ? new GridLength(SettingsControlColumnWidth)
            : new GridLength(0);
        row.RowDefinitions[0].Height = useSideBySide
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(1, GridUnitType.Auto);
        row.RowDefinitions[1].Height = useSideBySide
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Auto);
        row.RowSpacing = useSideBySide ? 0 : SettingsStackedRowSpacing;
        Grid.SetRow(control, useSideBySide ? 0 : 1);
        Grid.SetColumn(control, useSideBySide ? 1 : 0);
    }

    private void SettingsBackButton_Click(object sender, RoutedEventArgs e) =>
        SettingsPage.RequestNavigation(typeof(SettingsHomePage));

    private async void ApiProviderComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_synchronizing
            || !ViewModel.IsInitialized
            || ApiProviderComboBox.SelectedItem is not RemoteApiProviderOption option
            || option.ProfileId == ViewModel.SelectedProviderOption?.ProfileId)
        {
            return;
        }

        try
        {
            _synchronizing = true;
            await ViewModel.SelectProviderAsync(option);
        }
        catch
        {
            ViewModel.ShowOperationFailure();
        }
        finally
        {
            _synchronizing = false;
        }
    }

    private async void ApiCategoryComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_synchronizing
            || !ViewModel.IsInitialized
            || ApiCategoryComboBox.SelectedItem is not RemoteApiCategoryOption category)
        {
            return;
        }

        ViewModel.SelectedCategoryOption = category;
        if (ViewModel.ProviderOptions.FirstOrDefault() is { } first)
        {
            try
            {
                _synchronizing = true;
                await ViewModel.SelectProviderAsync(first);
            }
            catch
            {
                ViewModel.ShowOperationFailure();
            }
            finally
            {
                _synchronizing = false;
            }
        }
    }

    private async void SaveApiAdvancedSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmEndpointChangeAsync())
        {
            return;
        }

        try
        {
            await ViewModel.SaveAdvancedSettingsAsync();
        }
        catch
        {
            ViewModel.ShowOperationFailure();
        }
    }

    private async Task<bool> ConfirmEndpointChangeAsync()
    {
        if (!ViewModel.HasPendingEndpointChange)
        {
            return true;
        }

        var message = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            ResourceStrings.GetString("ApiEndpointChangeDialogMessageFormat"),
            ViewModel.SavedEndpointUriText,
            ViewModel.EndpointUriText.Trim());
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = ResourceStrings.GetString("ApiEndpointChangeDialogTitle"),
            Content = message,
            PrimaryButtonText = ResourceStrings.GetString("ApiEndpointChangeDialogPrimary"),
            CloseButtonText = ResourceStrings.GetString("CancelButtonText"),
            DefaultButton = ContentDialogButton.Close,
        };
        AutomationProperties.SetAutomationId(dialog, "ApiEndpointChangeDialog");
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void SaveApiCredentialButton_Click(object sender, RoutedEventArgs e)
    {
        var secret = ApiCredentialPasswordBox.Password;
        ApiCredentialPasswordBox.Password = string.Empty;
        if (string.IsNullOrWhiteSpace(secret))
        {
            return;
        }

        try
        {
            await ViewModel.SaveCredentialAsync(secret);
        }
        catch
        {
            ViewModel.ShowOperationFailure();
        }
    }

    private async void DeleteApiCredentialButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.DeleteCredentialAsync();
        }
        catch
        {
            ViewModel.ShowOperationFailure();
        }
    }

    private async void TestApiConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.TestConnectionAsync();
        }
        catch
        {
            ViewModel.ShowOperationFailure();
        }
    }

    private async void EnableRemoteApiButton_Click(object sender, RoutedEventArgs e)
    {
        var modeName = ResourceStrings.GetString(ViewModel.SelectedInputMode == RemoteInputMode.DirectImage
            ? "ApiRemoteVisionModeName"
            : "ApiRemoteOcrTextModeName");
        var consentCheckBox = new CheckBox
        {
            Content = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                ResourceStrings.GetString("ApiConsentCheckBoxFormat"),
                ViewModel.SelectedProviderOption?.DisplayName,
                modeName),
            IsChecked = false,
        };
        AutomationProperties.SetAutomationId(consentCheckBox, "ApiConsentCheckBox");
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = PayloadDisclosure(ViewModel.SelectedInputModeIndex),
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new TextBlock
        {
            Text = ResourceStrings.GetString("ApiConsentConsequencesText"),
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(consentCheckBox);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = ResourceStrings.GetString("ApiConsentDialogTitle"),
            Content = content,
            PrimaryButtonText = ResourceStrings.GetString("ApiConsentDialogPrimary"),
            CloseButtonText = ResourceStrings.GetString("CancelButtonText"),
            IsPrimaryButtonEnabled = false,
            DefaultButton = ContentDialogButton.Primary,
        };
        consentCheckBox.Checked += (_, _) => dialog.IsPrimaryButtonEnabled = true;
        consentCheckBox.Unchecked += (_, _) => dialog.IsPrimaryButtonEnabled = false;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await ViewModel.EnableRemoteAsync();
        }
        catch
        {
            ViewModel.ShowOperationFailure();
        }
    }

    private async void RevokeApiConsentButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.RevokeConsentAsync();
        }
        catch
        {
            ViewModel.ShowOperationFailure();
        }
    }
}
