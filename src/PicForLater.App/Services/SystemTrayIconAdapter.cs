using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.ApplicationModel.Resources;
using PicForLater.Core.Analysis;

namespace PicForLater.App.Services;

/// <summary>
/// Owns the process-lifetime tray icon and its native PopupMenu integration.
///
/// Business state is still updated by the application coordinator rather than
/// by a page, so hiding the window does not affect the tray object's lifetime.
/// </summary>
internal sealed class SystemTrayIconAdapter : IDisposable
{
    private const string TrayIconName = "PicForLater.SystemTray";

    private readonly DispatcherQueue _dispatcherQueue;
    private readonly ResourceLoader _resources = new();
    private readonly TaskbarIcon _taskbarIcon;
    private readonly ToggleMenuFlyoutItem _localSendItem;
    private readonly ToggleMenuFlyoutItem _localAnalysisItem;
    private readonly ToggleMenuFlyoutItem _remoteAnalysisItem;
    private readonly ToggleMenuFlyoutItem _quickScreenshotItem;
    private bool _disposed;

    public SystemTrayIconAdapter()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException(
                "The system tray adapter must be created on the UI thread.");

        var localSendItem = new ToggleMenuFlyoutItem
        {
            Text = _resources.GetString("TrayLocalSendText"),
            IsChecked = false,
            IsEnabled = false,
        };
        localSendItem.Command = new AsyncRelayCommand(() =>
            App.SetLocalSendEnabledFromTrayAsync(localSendItem.IsChecked));
        AutomationProperties.SetAutomationId(localSendItem, "TrayLocalSendItem");
        _localSendItem = localSendItem;

        _localAnalysisItem = new ToggleMenuFlyoutItem
        {
            Text = _resources.GetString("TrayLocalAnalysisText"),
            IsChecked = true,
            Command = new AsyncRelayCommand(() =>
                App.SelectAnalysisBackendFromTrayAsync(AnalysisExecutionBackend.Local)),
        };
        AutomationProperties.SetAutomationId(_localAnalysisItem, "TrayLocalAnalysisItem");
        _remoteAnalysisItem = new ToggleMenuFlyoutItem
        {
            Text = _resources.GetString("TrayRemoteAnalysisText"),
            IsChecked = false,
            Visibility = Visibility.Collapsed,
            Command = new AsyncRelayCommand(() =>
                App.SelectAnalysisBackendFromTrayAsync(AnalysisExecutionBackend.RemoteApi)),
        };
        AutomationProperties.SetAutomationId(_remoteAnalysisItem, "TrayRemoteAnalysisItem");

        var analysisModeItem = new MenuFlyoutSubItem
        {
            Text = _resources.GetString("TrayAnalysisModeText"),
        };
        AutomationProperties.SetAutomationId(analysisModeItem, "TrayAnalysisModeItem");
        analysisModeItem.Items.Add(_localAnalysisItem);
        analysisModeItem.Items.Add(_remoteAnalysisItem);

        var quickScreenshotItem = new ToggleMenuFlyoutItem
        {
            Text = _resources.GetString("TrayQuickScreenshotText"),
            IsChecked = false,
            IsEnabled = false,
        };
        quickScreenshotItem.Command = new AsyncRelayCommand(() =>
            App.SetScreenshotEnabledFromTrayAsync(quickScreenshotItem.IsChecked));
        AutomationProperties.SetAutomationId(quickScreenshotItem, "TrayQuickScreenshotItem");
        _quickScreenshotItem = quickScreenshotItem;

        var contextMenu = new MenuFlyout();
        contextMenu.Opening += (_, _) => App.RefreshSystemTrayBusinessState();
        contextMenu.Items.Add(_localSendItem);
        contextMenu.Items.Add(analysisModeItem);
        contextMenu.Items.Add(_quickScreenshotItem);
        contextMenu.Items.Add(new MenuFlyoutSeparator());

        var closeItem = new MenuFlyoutItem
        {
            Text = _resources.GetString("TrayCloseText"),
            Command = new RelayCommand(CloseApplication),
        };
        AutomationProperties.SetAutomationId(closeItem, "TrayCloseItem");
        contextMenu.Items.Add(closeItem);

        _taskbarIcon = new TaskbarIcon
        {
            CustomName = TrayIconName,
            ToolTipText = _resources.GetString("AppDisplayName"),
            IconSource = new BitmapImage(new Uri("ms-appx:///Assets/AppIcon.ico")),
            ContextFlyout = contextMenu,
            ContextMenuMode = ContextMenuMode.PopupMenu,
            MenuActivation = PopupActivationMode.RightClick,
            NoLeftClickDelay = true,
            LeftClickCommand = new RelayCommand(App.RequestForegroundActivation),
        };

        // ForceCreate() defaults to enabling Windows 11 Efficiency Mode. The
        // tray icon itself must not change scheduling or process QoS.
        try
        {
            _taskbarIcon.ForceCreate(enablesEfficiencyMode: false);
        }
        catch
        {
            // Keep the message window and menu alive so a later retry can
            // recover from a transient Shell/Explorer registration failure.
            Debug.WriteLine("System tray registration failed during startup.");
        }
    }

    public bool IsCreated => !_disposed && _taskbarIcon.IsCreated;

    internal void SetLocalSendState(
        bool isEnabled,
        bool isChecked,
        bool isUnavailable)
    {
        UpdateMenuOnUiThread(() =>
        {
            _localSendItem.Text = _resources.GetString(
                isUnavailable ? "TrayLocalSendUnavailableText" : "TrayLocalSendText");
            _localSendItem.IsEnabled = isEnabled;
            _localSendItem.IsChecked = isChecked;
        });
    }

    internal void SetAnalysisState(
        bool localEnabled,
        bool remoteEnabled,
        bool remoteVisible,
        bool localSelected,
        bool remoteSelected)
    {
        UpdateMenuOnUiThread(() =>
        {
            _localAnalysisItem.Text = _resources.GetString(
                localEnabled ? "TrayLocalAnalysisText" : "TrayLocalAnalysisUnavailableText");
            _remoteAnalysisItem.Text = _resources.GetString(
                remoteEnabled
                    ? "TrayRemoteAnalysisText"
                    : "TrayRemoteAnalysisUnavailableText");
            _localAnalysisItem.IsChecked = localSelected;
            _localAnalysisItem.IsEnabled = localEnabled;
            _remoteAnalysisItem.IsChecked = remoteSelected;
            _remoteAnalysisItem.IsEnabled = remoteEnabled;
            _remoteAnalysisItem.Visibility = remoteVisible
                ? Visibility.Visible
                : Visibility.Collapsed;
        });
    }

    internal void SetQuickScreenshotState(
        bool isEnabled,
        bool isChecked,
        bool isUnavailable)
    {
        UpdateMenuOnUiThread(() =>
        {
            _quickScreenshotItem.Text = _resources.GetString(
                isUnavailable
                    ? "TrayQuickScreenshotUnavailableText"
                    : "TrayQuickScreenshotText");
            _quickScreenshotItem.IsEnabled = isEnabled;
            _quickScreenshotItem.IsChecked = isChecked;
        });
    }

    internal bool TryEnsureCreated()
    {
        if (_disposed)
        {
            return false;
        }

        if (!_dispatcherQueue.HasThreadAccess)
        {
            return false;
        }

        try
        {
            _taskbarIcon.ForceCreate(enablesEfficiencyMode: false);
            return _taskbarIcon.IsCreated;
        }
        catch
        {
            Debug.WriteLine("System tray registration retry failed.");
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _taskbarIcon.Dispose();
    }

    private void CloseApplication()
    {
        UpdateMenuOnUiThread(() => _ = App.RequestApplicationExitAsync());
    }

    private void UpdateMenuOnUiThread(Action update)
    {
        if (_disposed)
        {
            return;
        }

        if (_dispatcherQueue.HasThreadAccess)
        {
            update();
            return;
        }

        _ = _dispatcherQueue.TryEnqueue(() =>
        {
            if (!_disposed)
            {
                update();
            }
        });
    }
}
