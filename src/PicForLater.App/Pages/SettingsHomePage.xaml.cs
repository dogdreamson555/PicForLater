using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;
using PicForLater.App.Models;
using PicForLater.App.Services;
using PicForLater.App.ViewModels;
using PicForLater.Infrastructure.LocalSend;

namespace PicForLater.App.Pages;

public sealed partial class SettingsHomePage : Page
{
    private static readonly ResourceLoader ResourceStrings = new();
    private DispatcherQueueTimer? _localSendPairingTimer;
    private ILocalSendReceiverService? _localSendReceiverSource;
    private IScreenshotCaptureService? _screenshotCaptureSource;
    private bool _synchronizingAnalysisSource;
    private bool _synchronizingLocalSendToggle;
    private bool _synchronizingScreenshotCaptureToggle;
    private int _loadGeneration;
    private CancellationTokenSource? _updateCheckCancellation;

    public SettingsHomePageViewModel ViewModel { get; } = new(
        ThemePreferenceService.Instance,
        App.StorageReadiness,
        () => App.RemoteApiProfiles,
        () => App.RemoteApiCredentials,
        App.LocalSendReceivePreference,
        () => App.LocalSendReceiver,
        App.UpdateCheck,
        App.CurrentVersion);

    public ScreenshotCaptureSettingsViewModel ScreenshotViewModel { get; } = new();

    public SettingsHomePage()
    {
        InitializeComponent();
        Loaded += SettingsHomePage_Loaded;
        Unloaded += SettingsHomePage_Unloaded;
    }

    private async void SettingsHomePage_Loaded(object sender, RoutedEventArgs e)
    {
        var loadGeneration = ++_loadGeneration;
        try
        {
            _synchronizingAnalysisSource = true;
            _synchronizingLocalSendToggle = true;
            _synchronizingScreenshotCaptureToggle = true;
            App.ScreenshotCaptureServiceChanged += App_ScreenshotCaptureServiceChanged;
            SubscribeScreenshotCaptureService(App.ScreenshotCapture);
            await ViewModel.InitializeAsync();
            if (!IsCurrentLoad(loadGeneration))
            {
                return;
            }

            SynchronizeLocalSendToggle();
            SynchronizeScreenshotCaptureToggle();
            SubscribeLocalSendReceiver();
            UpdateLocalSendPairingTimer();
        }
        finally
        {
            if (loadGeneration == _loadGeneration)
            {
                _synchronizingAnalysisSource = false;
                _synchronizingLocalSendToggle = false;
                _synchronizingScreenshotCaptureToggle = false;
            }
        }
    }

    private void SettingsHomePage_Unloaded(object sender, RoutedEventArgs e)
    {
        _loadGeneration++;
        _synchronizingAnalysisSource = false;
        _synchronizingLocalSendToggle = false;
        _synchronizingScreenshotCaptureToggle = false;
        App.ScreenshotCaptureServiceChanged -= App_ScreenshotCaptureServiceChanged;
        UnsubscribeScreenshotCaptureService();
        UnsubscribeLocalSendReceiver();
        StopLocalSendPairingTimer();
        _updateCheckCancellation?.Cancel();
        _updateCheckCancellation = null;
        ViewModel.CancelUpdateCheck();
    }

    public static bool IsSelected(int selectedIndex, int candidateIndex) =>
        selectedIndex == candidateIndex;

    public static Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    public static InfoBarSeverity StatusSeverity(SettingsStatusKind kind) => kind switch
    {
        SettingsStatusKind.Success => InfoBarSeverity.Success,
        SettingsStatusKind.Warning => InfoBarSeverity.Warning,
        SettingsStatusKind.Error => InfoBarSeverity.Error,
        _ => InfoBarSeverity.Informational,
    };

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
                await ViewModel.InitializeAsync();
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

    private async void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanCheckForUpdates)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _updateCheckCancellation = cancellation;
        try
        {
            await ViewModel.CheckForUpdatesAsync(cancellation.Token);
        }
        finally
        {
            if (ReferenceEquals(_updateCheckCancellation, cancellation))
            {
                _updateCheckCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private async void ViewReleasePageButton_Click(object sender, RoutedEventArgs e)
    {
        var releasePageUri = ViewModel.ReleasePageUri;
        if (releasePageUri is null)
        {
            return;
        }

        try
        {
            if (await Windows.System.Launcher.LaunchUriAsync(releasePageUri))
            {
                return;
            }
        }
        catch
        {
            // Browser activation failures are presented inline below.
        }

        ViewModel.ShowReleasePageOpenFailure();
    }

    private async void LocalSendReceiveToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_synchronizingLocalSendToggle || sender is not ToggleSwitch toggle)
        {
            return;
        }

        try
        {
            _synchronizingLocalSendToggle = true;
            await ViewModel.SetLocalSendEnabledAsync(toggle.IsOn);
        }
        finally
        {
            SynchronizeLocalSendToggle();
            _synchronizingLocalSendToggle = false;
        }
    }

    private async void ScreenshotCaptureToggle_Toggled(object sender, RoutedEventArgs e)
    {
        var source = _screenshotCaptureSource;
        if (_synchronizingScreenshotCaptureToggle
            || source is null
            || sender is not ToggleSwitch toggle
            || !ScreenshotViewModel.CanToggle)
        {
            return;
        }

        var loadGeneration = _loadGeneration;
        try
        {
            _synchronizingScreenshotCaptureToggle = true;
            ScreenshotViewModel.ApplySnapshot(source.Snapshot, isWorking: true);
            ScreenshotSettingsOperationResult result =
                await source.SetEnabledAsync(toggle.IsOn);
            if (!IsCurrentScreenshotSource(source, loadGeneration))
            {
                return;
            }

            ScreenshotViewModel.ApplySnapshot(source.Snapshot);
            if (result.Succeeded)
            {
                ScreenshotViewModel.ApplySettingsSuccess();
            }
            else
            {
                ScreenshotViewModel.ApplySettingsFailure(result.FailureKind);
            }
        }
        catch
        {
            if (IsCurrentScreenshotSource(source, loadGeneration))
            {
                ScreenshotViewModel.ApplySnapshot(source.Snapshot);
                ScreenshotViewModel.ApplySettingsFailure(
                    ScreenshotSettingsFailureKind.Registration);
            }
        }
        finally
        {
            if (IsCurrentScreenshotSource(source, loadGeneration))
            {
                SynchronizeScreenshotCaptureToggle();
                RefreshScreenshotCaptureBindings();
                _synchronizingScreenshotCaptureToggle = false;
            }
        }
    }

    private async void ChangeScreenshotHotKeyButton_Click(object sender, RoutedEventArgs e)
    {
        var source = _screenshotCaptureSource;
        if (source is null || !ScreenshotViewModel.CanChangeHotKey)
        {
            return;
        }

        var loadGeneration = _loadGeneration;
        var dialog = new ScreenshotHotKeyDialog(
            source.Snapshot.HotKey,
            hotKey => SaveScreenshotHotKeyAsync(source, loadGeneration, hotKey))
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
        };
        await dialog.ShowAsync();
    }

    private async Task<ScreenshotSettingsOperationResult> SaveScreenshotHotKeyAsync(
        IScreenshotCaptureService source,
        int loadGeneration,
        ScreenshotHotKey hotKey)
    {
        if (!IsCurrentScreenshotSource(source, loadGeneration))
        {
            return ScreenshotSettingsOperationResult.Failed(
                ScreenshotSettingsFailureKind.NotStarted);
        }

        ScreenshotViewModel.ApplySnapshot(source.Snapshot, isWorking: true);
        try
        {
            ScreenshotSettingsOperationResult result = await source.UpdateHotKeyAsync(hotKey);
            if (IsCurrentScreenshotSource(source, loadGeneration))
            {
                ScreenshotViewModel.ApplySnapshot(source.Snapshot);
                if (result.Succeeded)
                {
                    ScreenshotViewModel.ApplySettingsSuccess();
                }
                else
                {
                    ScreenshotViewModel.ApplySettingsFailure(result.FailureKind);
                }

                RefreshScreenshotCaptureBindings();
            }

            return result;
        }
        catch
        {
            if (IsCurrentScreenshotSource(source, loadGeneration))
            {
                ScreenshotViewModel.ApplySnapshot(source.Snapshot);
                ScreenshotViewModel.ApplySettingsFailure(
                    ScreenshotSettingsFailureKind.Registration);
                RefreshScreenshotCaptureBindings();
            }

            return ScreenshotSettingsOperationResult.Failed(
                ScreenshotSettingsFailureKind.Registration);
        }
    }

    private async void BeginLocalSendPairingButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.BeginLocalSendPairingAsync();
        UpdateLocalSendPairingTimer();
    }

    private async void CancelLocalSendPairingButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.CancelLocalSendPairingAsync();
        UpdateLocalSendPairingTimer();
    }

    private async void RemoveLocalSendTrustedDeviceButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not FrameworkElement
            {
                DataContext: LocalSendTrustedDeviceItem device,
            })
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = ResourceStrings.GetString("LocalSendRemoveDeviceDialogTitle"),
            Content = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                ResourceStrings.GetString("LocalSendRemoveDeviceDialogMessageFormat"),
                device.DisplayName),
            PrimaryButtonText = ResourceStrings.GetString("LocalSendRemoveDeviceDialogRemove"),
            CloseButtonText = ResourceStrings.GetString("CancelButtonText"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.RemoveLocalSendTrustedDeviceAsync(device.DeviceId);
        }
    }

    private void SubscribeLocalSendReceiver()
    {
        var source = App.LocalSendReceiver;
        if (ReferenceEquals(source, _localSendReceiverSource))
        {
            return;
        }

        UnsubscribeLocalSendReceiver();
        _localSendReceiverSource = source;
        if (source is not null)
        {
            source.SnapshotChanged += LocalSendReceiver_SnapshotChanged;
            source.TransferCompleted += LocalSendReceiver_TransferCompleted;
            ViewModel.ApplyLocalSendSnapshot(source.Snapshot);
        }
    }

    private void App_ScreenshotCaptureServiceChanged(IScreenshotCaptureService? source)
    {
        var loadGeneration = _loadGeneration;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsCurrentLoad(loadGeneration))
            {
                return;
            }

            _synchronizingScreenshotCaptureToggle = true;
            SubscribeScreenshotCaptureService(source);
            SynchronizeScreenshotCaptureToggle();
            RefreshScreenshotCaptureBindings();
            _synchronizingScreenshotCaptureToggle = false;
        });
    }

    private void SubscribeScreenshotCaptureService(IScreenshotCaptureService? source)
    {
        if (!ReferenceEquals(source, _screenshotCaptureSource))
        {
            UnsubscribeScreenshotCaptureService();
            _screenshotCaptureSource = source;
            if (source is not null)
            {
                source.SnapshotChanged += ScreenshotCapture_SnapshotChanged;
                source.CaptureCompleted += ScreenshotCapture_CaptureCompleted;
            }
        }

        if (source is null)
        {
            ScreenshotViewModel.ApplyPreparing();
        }
        else
        {
            ScreenshotViewModel.ApplySnapshot(source.Snapshot);
        }
    }

    private void UnsubscribeScreenshotCaptureService()
    {
        var source = _screenshotCaptureSource;
        _screenshotCaptureSource = null;
        if (source is not null)
        {
            source.SnapshotChanged -= ScreenshotCapture_SnapshotChanged;
            source.CaptureCompleted -= ScreenshotCapture_CaptureCompleted;
        }
    }

    private void ScreenshotCapture_SnapshotChanged(
        object? sender,
        ScreenshotCaptureSnapshotChangedEventArgs e)
    {
        var source = sender as IScreenshotCaptureService;
        var loadGeneration = _loadGeneration;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (source is null || !IsCurrentScreenshotSource(source, loadGeneration))
            {
                return;
            }

            _synchronizingScreenshotCaptureToggle = true;
            ScreenshotViewModel.ApplySnapshot(e.Snapshot);
            SynchronizeScreenshotCaptureToggle();
            RefreshScreenshotCaptureBindings();
            _synchronizingScreenshotCaptureToggle = false;
        });
    }

    private void ScreenshotCapture_CaptureCompleted(
        object? sender,
        ScreenshotCaptureCompletedEventArgs e)
    {
        var source = sender as IScreenshotCaptureService;
        var loadGeneration = _loadGeneration;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (source is null || !IsCurrentScreenshotSource(source, loadGeneration))
            {
                return;
            }

            ScreenshotViewModel.ApplyCaptureResult(e.Result);
            RefreshScreenshotCaptureBindings();
        });
    }

    private void UnsubscribeLocalSendReceiver()
    {
        var source = _localSendReceiverSource;
        _localSendReceiverSource = null;
        if (source is not null)
        {
            source.SnapshotChanged -= LocalSendReceiver_SnapshotChanged;
            source.TransferCompleted -= LocalSendReceiver_TransferCompleted;
        }
    }

    private void LocalSendReceiver_SnapshotChanged(LocalSendReceiverSnapshot snapshot)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsLoaded || !ReferenceEquals(_localSendReceiverSource, App.LocalSendReceiver))
            {
                return;
            }

            _synchronizingLocalSendToggle = true;
            ViewModel.ApplyLocalSendSnapshot(snapshot);
            SynchronizeLocalSendToggle();
            _synchronizingLocalSendToggle = false;
            UpdateLocalSendPairingTimer();
        });
    }

    private void LocalSendReceiver_TransferCompleted(LocalSendReceiveSummary summary)
    {
        var source = _localSendReceiverSource;
        var loadGeneration = _loadGeneration;
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (!IsCurrentLoad(loadGeneration)
                || !ReferenceEquals(source, _localSendReceiverSource))
            {
                return;
            }

            await ViewModel.RefreshLocalSendTrustedDevicesAsync();
            if (IsLoaded
                && (!IsCurrentLoad(loadGeneration)
                    || !ReferenceEquals(source, _localSendReceiverSource)))
            {
                // The page was reloaded or the receiver changed while the old
                // query was in flight. Re-read from the current source so stale
                // results cannot win over a removal or replacement.
                await ViewModel.RefreshLocalSendTrustedDevicesAsync();
            }
        });
    }

    private void SynchronizeLocalSendToggle()
    {
        LocalSendReceiveToggle.IsOn = ViewModel.IsLocalSendEnabled;
    }

    private void SynchronizeScreenshotCaptureToggle()
    {
        Bindings.Update();
        ScreenshotCaptureToggle.IsOn = ScreenshotViewModel.IsEnabledRequested;
    }

    private void RefreshScreenshotCaptureBindings() => Bindings.Update();

    private void UpdateLocalSendPairingTimer()
    {
        if (!ViewModel.IsLocalSendPairing)
        {
            StopLocalSendPairingTimer();
            return;
        }

        _localSendPairingTimer ??= DispatcherQueue.CreateTimer();
        _localSendPairingTimer.Interval = TimeSpan.FromSeconds(1);
        _localSendPairingTimer.Tick -= LocalSendPairingTimer_Tick;
        _localSendPairingTimer.Tick += LocalSendPairingTimer_Tick;
        if (!_localSendPairingTimer.IsRunning)
        {
            _localSendPairingTimer.Start();
        }
    }

    private void StopLocalSendPairingTimer()
    {
        if (_localSendPairingTimer is null)
        {
            return;
        }

        _localSendPairingTimer.Stop();
        _localSendPairingTimer.Tick -= LocalSendPairingTimer_Tick;
    }

    private void LocalSendPairingTimer_Tick(
        DispatcherQueueTimer sender,
        object args)
    {
        var snapshot = _localSendReceiverSource?.Snapshot;
        if (snapshot is null || snapshot.Status != LocalSendReceiverStatus.Pairing)
        {
            StopLocalSendPairingTimer();
            return;
        }

        ViewModel.UpdateLocalSendPairingRemaining(snapshot, DateTimeOffset.UtcNow);
    }

    private bool IsCurrentLoad(int loadGeneration) =>
        IsLoaded && loadGeneration == _loadGeneration;

    private bool IsCurrentScreenshotSource(
        IScreenshotCaptureService source,
        int loadGeneration) =>
        IsCurrentLoad(loadGeneration)
        && ReferenceEquals(source, _screenshotCaptureSource)
        && ReferenceEquals(source, App.ScreenshotCapture);
}
