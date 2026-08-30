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
    private bool _synchronizingAnalysisSource;
    private bool _synchronizingLocalSendToggle;
    private int _loadGeneration;

    public SettingsHomePageViewModel ViewModel { get; } = new(
        ThemePreferenceService.Instance,
        App.StorageReadiness,
        () => App.RemoteApiProfiles,
        () => App.RemoteApiCredentials,
        App.LocalSendReceivePreference,
        () => App.LocalSendReceiver);

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
            await ViewModel.InitializeAsync();
            if (!IsCurrentLoad(loadGeneration))
            {
                return;
            }

            SynchronizeLocalSendToggle();
            SubscribeLocalSendReceiver();
            UpdateLocalSendPairingTimer();
        }
        finally
        {
            if (loadGeneration == _loadGeneration)
            {
                _synchronizingAnalysisSource = false;
                _synchronizingLocalSendToggle = false;
            }
        }
    }

    private void SettingsHomePage_Unloaded(object sender, RoutedEventArgs e)
    {
        _loadGeneration++;
        _synchronizingAnalysisSource = false;
        _synchronizingLocalSendToggle = false;
        UnsubscribeLocalSendReceiver();
        StopLocalSendPairingTimer();
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
}
