using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;
using Microsoft.Windows.Storage.Pickers;
using PicForLater.App.Models;
using PicForLater.App.Services;
using PicForLater.App.ViewModels;
using PicForLater.Core.Analysis;
using Windows.Foundation;

namespace PicForLater.App.Pages;

public sealed partial class LocalAnalysisSettingsPage : Page
{
    private const double BackButtonLayoutWidth = 52;
    private const string ChevronDownGlyph = "\uE70D";
    private const string ChevronUpGlyph = "\uE70E";
    private bool _areInstalledModelsExpanded;
    private static readonly ResourceLoader _resources = new();
    private bool _synchronizing;
    private bool _accelerationSubscribed;

    public SettingsPageViewModel ViewModel { get; } =
        new(
            ThemePreferenceService.Instance,
            App.InferenceAcceleration,
            App.StorageReadiness,
            () => App.ModelPackages,
            () => App.RecommendedModels,
            () => App.NvidiaCudaEnvironment,
            () => App.LocalInferenceComponentInstaller,
            () => App.LocalInferenceComponentStore);

    public LocalAnalysisSettingsPage()
    {
        InitializeComponent();
        Loaded += LocalAnalysisSettingsPage_Loaded;
        Unloaded += LocalAnalysisSettingsPage_Unloaded;
        SizeChanged += LocalAnalysisSettingsPage_SizeChanged;
    }

    public static Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility ModelOperationStatusVisibility(bool isWorking, bool isStatusOpen) =>
        isWorking || isStatusOpen ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility CountToVisibility(int count) =>
        count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public static bool Not(bool value) => !value;

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

    private async void LocalAnalysisSettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyHeaderLayout();
        if (!_accelerationSubscribed)
        {
            App.InferenceAcceleration.StateChanged += InferenceAcceleration_StateChanged;
            _accelerationSubscribed = true;
        }

        try
        {
            _synchronizing = true;
            await ViewModel.InitializeAsync();
        }
        catch
        {
            ViewModel.ShowStatus(_resources.GetString("ModelManagementUnavailableStatus"));
        }
        finally
        {
            _synchronizing = false;
        }
    }

    private void LocalAnalysisSettingsPage_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyHeaderLayout();

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

    private void LocalAnalysisSettingsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_accelerationSubscribed)
        {
            App.InferenceAcceleration.StateChanged -= InferenceAcceleration_StateChanged;
            _accelerationSubscribed = false;
        }
    }

    private void InferenceAcceleration_StateChanged(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(ViewModel.RefreshInferenceAccelerationStatus);

    private void SettingsBackButton_Click(object sender, RoutedEventArgs e) =>
        SettingsPage.RequestNavigation(typeof(SettingsHomePage));

    private void InstalledModelsExpander_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.InstalledPackages.Count == 0)
        {
            return;
        }

        _areInstalledModelsExpanded = !_areInstalledModelsExpanded;
        InstalledModelsContent.Visibility = _areInstalledModelsExpanded
            ? Visibility.Visible
            : Visibility.Collapsed;
        InstalledModelsChevron.Glyph = _areInstalledModelsExpanded
            ? ChevronUpGlyph
            : ChevronDownGlyph;
        InstalledModelsExpander.SetValue(
            AutomationProperties.HelpTextProperty,
            _areInstalledModelsExpanded
                ? _resources.GetString("InstalledModelsCollapseHelpText")
                : _resources.GetString("InstalledModelsExpandHelpText"));
    }

    private void InstalledModelsRepeater_ElementPrepared(
        ItemsRepeater sender,
        ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not Border border)
        {
            return;
        }

        var index = sender.GetElementIndex(args.Element);
        border.BorderThickness = index == ViewModel.InstalledPackages.Count - 1
            ? new Thickness(0)
            : new Thickness(0, 0, 0, 1);
    }

    private async void AnalysisModeOptions_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_synchronizing || !ViewModel.IsInitialized || AnalysisModeOptions.SelectedIndex < 0)
        {
            return;
        }

        try
        {
            _synchronizing = true;
            await ViewModel.SetAnalysisModeAsync(AnalysisModeOptions.SelectedIndex);
        }
        catch
        {
            ViewModel.ShowStatus(_resources.GetString("AnalysisModeSaveFailedStatus"));
            await ViewModel.ReloadAsync();
        }
        finally
        {
            _synchronizing = false;
        }
    }

    private void InferenceAccelerationOptions_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_synchronizing
            || !ViewModel.IsInitialized
            || sender is not RadioButtons { SelectedIndex: >= 0 } options)
        {
            return;
        }

        try
        {
            _synchronizing = true;
            ViewModel.SetInferenceAccelerationMode(options.SelectedIndex);
        }
        catch
        {
            ViewModel.ShowStatus(_resources.GetString("InferenceAccelerationSaveFailedStatus"));
        }
        finally
        {
            _synchronizing = false;
        }
    }

    private async void ImportModelPackageButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                CommitButtonText = _resources.GetString("ImportModelPickerCommitText"),
            };
            picker.FileTypeFilter.Add(".json");
            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            await ViewModel.ImportAsync(file.Path);
        }
        catch (Exception exception)
        {
            ViewModel.ShowModelOperationFailure(exception, "ModelImportFailedStatus");
        }
    }

    private async void RecommendedModelActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsWorking
            || sender is not Button { Tag: string modelId }
            || ViewModel.RecommendedModels.FirstOrDefault(item => item.Id == modelId) is not { } item
            || item.Descriptor.IsEnabled)
        {
            return;
        }

        var installNvidiaRuntimeFirst = false;
        if (item.RequiresNvidiaCudaRuntime)
        {
            var environment = await ViewModel.RefreshNvidiaEnvironmentAsync();
            if (environment is null)
            {
                ViewModel.ShowStatus(_resources.GetString("NvidiaEnvironmentDetectionFailedStatus"));
                return;
            }

            if (!environment.CanUseCudaModel)
            {
                if (!environment.CanInstallRuntime)
                {
                    ViewModel.ShowStatus(ViewModel.NvidiaEnvironmentStatusMessage);
                    return;
                }

                installNvidiaRuntimeFirst = true;
            }
        }

        var confirmationMessage = installNvidiaRuntimeFirst
            ? string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                _resources.GetString("NvidiaRuntimeAndModelConfirmationFormat"),
                ViewModel.GetNvidiaRuntimeConfirmationMessage(),
                item.ConfirmationMessage)
            : item.ConfirmationMessage;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                _resources.GetString("RecommendedModelConfirmationTitleFormat"),
                item.Name),
            Content = new TextBlock
            {
                Text = confirmationMessage,
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = installNvidiaRuntimeFirst
                ? _resources.GetString("NvidiaRuntimeAndModelInstallAction")
                : item.Descriptor.IsInstalled
                ? _resources.GetString("RecommendedModelEnableAction")
                : _resources.GetString("RecommendedModelDownloadAction"),
            CloseButtonText = _resources.GetString("CancelButtonText"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await ViewModel.DownloadInstallAndEnableAsync(item, installNvidiaRuntimeFirst);
    }

    private void RecommendedModelInfoButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string modelId } button
            || ViewModel.RecommendedModels.FirstOrDefault(item => item.Id == modelId) is not { } item)
        {
            return;
        }

        ShowModelDetails(
            button,
            item.Name,
            item.TechnicalName,
            item.Details,
            item.Hardware,
            item.LicenseAndSource,
            item.BenchmarkStatus);
    }

    private void InstalledModelInfoButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string packageKey } button
            || ViewModel.InstalledPackages.FirstOrDefault(
                item => item.PackageKey == packageKey) is not { } item)
        {
            return;
        }

        ShowModelDetails(
            button,
            item.DisplayName,
            item.Name,
            item.Details,
            item.Languages,
            item.Hardware,
            item.LicenseAndSource,
            item.BenchmarkStatus);
    }

    private static void ShowModelDetails(Button anchor, string title, params string[] details)
    {
        var content = new StackPanel
        {
            Width = 520,
            MaxWidth = 520,
            Spacing = 8,
        };
        content.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });

        foreach (var detail in details.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            content.Children.Add(new TextBlock
            {
                Text = detail,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        var presenterStyle = new Style(typeof(FlyoutPresenter));
        presenterStyle.Setters.Add(new Setter
        {
            Property = FrameworkElement.MaxWidthProperty,
            Value = 560d,
        });

        new Flyout
        {
            Content = content,
            FlyoutPresenterStyle = presenterStyle,
        }.ShowAt(anchor);
    }

    private async void DetectNvidiaEnvironmentButton_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.RefreshNvidiaEnvironmentAsync();

    private async void InstallNvidiaRuntimeButton_Click(object sender, RoutedEventArgs e)
    {
        var environment = await ViewModel.RefreshNvidiaEnvironmentAsync();
        if (environment is not { CanInstallRuntime: true })
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = _resources.GetString("NvidiaRuntimeInstallDialogTitle"),
            Content = new TextBlock
            {
                Text = ViewModel.GetNvidiaRuntimeConfirmationMessage(),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = _resources.GetString("NvidiaRuntimeInstallAction"),
            CloseButtonText = _resources.GetString("CancelButtonText"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await ViewModel.InstallNvidiaRuntimeAsync();
    }

    private async void InstallLocalInferenceComponentButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!ViewModel.CanInstallLocalInferenceComponent)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = _resources.GetString("LocalInferenceComponentInstallDialogTitle"),
            Content = new TextBlock
            {
                Text = _resources.GetString("LocalInferenceComponentInstallConfirmation"),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = ViewModel.LocalInferenceComponentActionText,
            CloseButtonText = _resources.GetString("CancelButtonText"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await ViewModel.InstallOrRepairLocalInferenceComponentAsync();
    }

    private async void RemoveLocalInferenceComponentButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!ViewModel.CanRemoveLocalInferenceComponent)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = _resources.GetString("LocalInferenceComponentRemoveDialogTitle"),
            Content = new TextBlock
            {
                Text = string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    _resources.GetString("LocalInferenceComponentRemoveConfirmationFormat"),
                    ViewModel.LocalInferenceComponentVersion),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = _resources.GetString("LocalInferenceComponentRemoveAction"),
            CloseButtonText = _resources.GetString("CancelButtonText"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await ViewModel.RemoveLocalInferenceComponentAsync();
    }

    private void CancelModelOperationButton_Click(object sender, RoutedEventArgs e) =>
        ViewModel.CancelModelOperation();

    private async void VisionModelComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e) =>
        await ConfirmAndSwitchAsync(
            ModelCapability.VisionCaption,
            VisionModelComboBox,
            ViewModel.SelectedVisionOption);

    private async void TextCompositionModelComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e) =>
        await ConfirmAndSwitchAsync(
            ModelCapability.TextComposition,
            TextCompositionModelComboBox,
            ViewModel.SelectedTextCompositionOption);

    private async Task ConfirmAndSwitchAsync(
        ModelCapability capability,
        ComboBox comboBox,
        ModelPackageOption? currentOption)
    {
        if (_synchronizing
            || !ViewModel.IsInitialized
            || comboBox.SelectedItem is not ModelPackageOption selectedOption
            || selectedOption.PackageKey == currentOption?.PackageKey)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = _resources.GetString("ModelSwitchDialogTitle"),
            Content = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                _resources.GetString("ModelSwitchDialogMessageFormat"),
                selectedOption.DisplayName),
            PrimaryButtonText = _resources.GetString("ModelSwitchDialogPrimary"),
            CloseButtonText = _resources.GetString("CancelButtonText"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            _synchronizing = true;
            comboBox.SelectedItem = currentOption;
            _synchronizing = false;
            return;
        }

        try
        {
            _synchronizing = true;
            await ViewModel.SwitchAsync(capability, selectedOption);
        }
        catch (Exception exception)
        {
            ViewModel.ShowModelOperationFailure(exception, "ModelSwitchFailedStatus");
            await ViewModel.ReloadAsync();
        }
        finally
        {
            _synchronizing = false;
        }
    }
}
