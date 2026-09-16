using Microsoft.UI.Xaml;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.ApplicationModel.Resources;
using PicForLater.App.Models;
using PicForLater.App.Services;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Graphics;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace PicForLater.App;

/// <summary>
/// The application window. This hosts a Frame that displays pages. Add your
/// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
/// can use Page features such as navigation events and the Loaded lifecycle.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const double InitialWidthInDips = 1200;
    private const double InitialHeightInDips = 800;
    // Keep the client area above the 641 epx medium-layout breakpoint after
    // accounting for the overlapped window's left and right resize borders.
    private const double MinimumWidthInDips = 660;
    private const double MinimumHeightInDips = 480;
    private int _minimumWidthInPixels;
    private int _minimumHeightInPixels;
    private uint _minimumSizeDpi;
    private nint _windowHandle;
    private bool _minimumSizeConfiguredAfterActivation;
    private bool _allowClosing;
    private bool _windowCloseRequestQueued;
    private bool _nativeClosingRaised;
    private bool _xamlBackdropApplyPending;
    private bool _nativeBackdropApplyPending;
    private readonly IScreenshotCapturePlatform _screenshotCapturePlatform;
    private readonly IBackdropPreferenceService _backdropPreferenceService;
    private WindowSessionMessageMonitor? _sessionMessageMonitor;

    private const uint DwmAttributeUseImmersiveDarkMode = 20;
    private const uint DwmAttributeSystemBackdropType = 38;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint windowHandle);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(
        nint windowHandle,
        uint attribute,
        ref uint value,
        uint valueSize);

    private const int ShowWindowRestore = 9;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint windowHandle, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint windowHandle);

    public MainWindow()
    {
        InitializeComponent();
        _backdropPreferenceService = BackdropPreferenceService.Instance;

#if PICFORLATER_UI_VISUAL_FIXTURE
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(
            WindowRoot,
            $"fixture:{UiTestVisualFixtureSeeder.FixtureId}");
#endif

        Title = new ResourceLoader().GetString("MainWindowTitle");

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }
        ConfigureSizeForCurrentDisplay();
#if PICFORLATER_UI_TESTING
        _screenshotCapturePlatform = new UiTestScreenshotCapturePlatform();
#else
        _screenshotCapturePlatform = WindowsScreenshotCapturePlatform.Create(_windowHandle);
#endif
        try
        {
            _sessionMessageMonitor = new WindowSessionMessageMonitor(_windowHandle);
            _sessionMessageMonitor.SessionEnding +=
                SessionMessageMonitor_SessionEnding;
        }
        catch (Exception exception)
        {
            Debug.WriteLine(
                $"Windows session message monitoring could not start: {exception.GetType().Name}.");
        }

        AppWindow.Changed += AppWindow_Changed;
        AppWindow.Closing += AppWindow_Closing;
        Activated += MainWindow_Activated;
        WindowRoot.ActualThemeChanged += WindowRoot_ActualThemeChanged;
        ThemePreferenceService.Instance.Initialize(WindowRoot);
        _backdropPreferenceService.PreferenceChanged +=
            BackdropPreferenceService_PreferenceChanged;
        ApplyWindowBackdrop();

        // Navigate the root frame to the main page on startup.
        RootFrame.Navigate(typeof(MainPage));
    }

    internal IScreenshotCapturePlatform ScreenshotCapturePlatform =>
        _screenshotCapturePlatform;

    internal MainPage? CurrentMainPage => RootFrame.Content as MainPage;

    internal event EventHandler? NativeClosing;

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        _ = sender;
        if (args.Cancel)
        {
            return;
        }

        if (_allowClosing)
        {
            return;
        }

        args.Cancel = true;
        if (_windowCloseRequestQueued)
        {
            return;
        }

        // Let the native close-button handler finish before changing visibility.
        _windowCloseRequestQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                _windowCloseRequestQueued = false;
                if (!_allowClosing && !_nativeClosingRaised)
                {
                    App.RequestWindowClose();
                }
            }))
        {
            _windowCloseRequestQueued = false;
            // Do not leave the native close canceled with no follow-up if the
            // dispatcher is already winding down.
            App.RequestWindowClose();
        }
    }

    private void SessionMessageMonitor_SessionEnding(
        object? sender,
        EventArgs e)
    {
        _ = sender;
        _ = e;
        App.RequestSystemSessionShutdown();
    }

    internal void HideToTray()
    {
        AppWindow.Hide();
    }

    internal void RestoreAndActivate()
    {
        // Restore visibility and window placement before activating the window.
        AppWindow.Show(activateWindow: false);
        EnsureWindowHandle();
        if (IsIconic(_windowHandle))
        {
            _ = ShowWindow(_windowHandle, ShowWindowRestore);
        }

        Activate();
        _ = SetForegroundWindow(_windowHandle);
    }

    private void EnsureWindowHandle()
    {
        if (_windowHandle == 0)
        {
            _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        }
    }

    internal void DisableInteractionForShutdown() => RootFrame.IsEnabled = false;

    internal void PrepareForFinalClose()
    {
        AppWindow.Changed -= AppWindow_Changed;
        AppWindow.Closing -= AppWindow_Closing;
        Activated -= MainWindow_Activated;
        WindowRoot.ActualThemeChanged -= WindowRoot_ActualThemeChanged;
        _backdropPreferenceService.PreferenceChanged -=
            BackdropPreferenceService_PreferenceChanged;
        DisposeSessionMessageMonitor();
        if (_nativeClosingRaised)
        {
            return;
        }

        _nativeClosingRaised = true;
        try
        {
            NativeClosing?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // Shutdown must still reach the final Window.Close call if an optional
            // cleanup subscriber fails.
        }
        finally
        {
            NativeClosing = null;
            try
            {
                (_screenshotCapturePlatform as IDisposable)?.Dispose();
            }
            catch (Exception exception)
            {
                Debug.WriteLine(
                    $"Screenshot capture platform disposal failed: {exception.GetType().Name}.");
            }
        }
    }

    private void DisposeSessionMessageMonitor()
    {
        var monitor = _sessionMessageMonitor;
        _sessionMessageMonitor = null;
        if (monitor is null)
        {
            return;
        }

        monitor.SessionEnding -= SessionMessageMonitor_SessionEnding;
        try
        {
            monitor.Dispose();
        }
        catch (Exception exception)
        {
            Debug.WriteLine(
                $"Windows session message monitor disposal failed: {exception.GetType().Name}.");
        }
    }

    internal void CloseAfterFinalCleanup()
    {
        _allowClosing = true;
        Close();
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPositionChange && !args.DidSizeChange && !args.DidPresenterChange)
        {
            return;
        }

        UpdateMinimumSize(GetDpiForWindow(_windowHandle));
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            return;
        }

        if (!_minimumSizeConfiguredAfterActivation)
        {
            _minimumSizeConfiguredAfterActivation = true;
            _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            UpdateMinimumSize(GetDpiForWindow(_windowHandle));
        }

        if (BackdropApplyPending)
        {
            ApplyWindowBackdrop();
            return;
        }

        TryApplySystemTitleBarTheme();
    }

    private void WindowRoot_ActualThemeChanged(FrameworkElement sender, object args)
    {
        _ = sender;
        _ = args;
        TryApplySystemTitleBarTheme();
    }

    private void BackdropPreferenceService_PreferenceChanged(
        object? sender,
        EventArgs e)
    {
        _ = sender;
        _ = e;
        ApplyWindowBackdrop();
    }

    private void ApplyWindowBackdrop()
    {
        try
        {
            SystemBackdrop = new MicaBackdrop
            {
                Kind = _backdropPreferenceService.CurrentPreference == AppBackdropPreference.Mica
                    ? MicaKind.Base
                    : MicaKind.BaseAlt,
            };
            _xamlBackdropApplyPending = false;
        }
        catch (Exception exception)
        {
            _xamlBackdropApplyPending = true;
            Trace.WriteLine(
                $"Window Mica backdrop application failed: {exception.GetType().Name}.");
        }

        TryApplySystemTitleBarTheme();
    }

    private bool BackdropApplyPending =>
        _xamlBackdropApplyPending || _nativeBackdropApplyPending;

    private void TryApplySystemTitleBarTheme()
    {
        try
        {
            _nativeBackdropApplyPending = !ApplySystemTitleBarTheme();
        }
        catch (Exception exception)
        {
            _nativeBackdropApplyPending = true;
            Trace.WriteLine(
                $"Native Mica backdrop application failed: {exception.GetType().Name}.");
        }
    }

    private bool ApplySystemTitleBarTheme()
    {
        if (_windowHandle == 0 || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return true;
        }

        var isDark = WindowRoot.ActualTheme == ElementTheme.Dark;
        uint immersiveDarkMode = isDark ? 1u : 0u;
        var darkModeResult = DwmSetWindowAttribute(
            _windowHandle,
            DwmAttributeUseImmersiveDarkMode,
            ref immersiveDarkMode,
            sizeof(uint));
        var nativeBackdropApplied = darkModeResult >= 0;
        if (darkModeResult < 0)
        {
            Trace.WriteLine(
                $"Native title-bar theme could not be applied: 0x{darkModeResult:X8}.");
        }

        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            // DWM styles the native caption; SystemBackdrop still owns the client area.
            uint backdropType = BackdropPreferenceMapping.ToDwmSystemBackdropType(
                _backdropPreferenceService.CurrentPreference);
            var result = DwmSetWindowAttribute(
                _windowHandle,
                DwmAttributeSystemBackdropType,
                ref backdropType,
                sizeof(uint));
            if (result < 0)
            {
                nativeBackdropApplied = false;
                Trace.WriteLine(
                    $"Native {_backdropPreferenceService.CurrentPreference} backdrop " +
                    $"could not be applied: 0x{result:X8}.");
            }
        }

        return nativeBackdropApplied;
    }

    private void ConfigureSizeForCurrentDisplay()
    {
        _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi = GetDpiForWindow(_windowHandle);
        var scale = dpi == 0 ? 1.0 : dpi / 96.0;
        var requestedWidth = (int)Math.Round(InitialWidthInDips * scale);
        var requestedHeight = (int)Math.Round(InitialHeightInDips * scale);

        var displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var workArea = displayArea.WorkArea;
        UpdateMinimumSize(dpi);

        var width = Math.Max(
            _minimumWidthInPixels,
            Math.Min(requestedWidth, (int)Math.Floor(workArea.Width * 0.9)));
        var height = Math.Max(
            _minimumHeightInPixels,
            Math.Min(requestedHeight, (int)Math.Floor(workArea.Height * 0.9)));
        AppWindow.Resize(new SizeInt32(width, height));
    }

    private void UpdateMinimumSize(uint dpi)
    {
        dpi = dpi == 0 ? 96u : dpi;
        if (_minimumSizeDpi == dpi &&
            _minimumWidthInPixels > 0 &&
            _minimumHeightInPixels > 0)
        {
            return;
        }

        _minimumSizeDpi = dpi;
        var scale = dpi / 96.0;
        var workArea = DisplayArea
            .GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary)
            .WorkArea;
        _minimumWidthInPixels = Math.Min(
            (int)Math.Round(MinimumWidthInDips * scale),
            workArea.Width);
        _minimumHeightInPixels = Math.Min(
            (int)Math.Round(MinimumHeightInDips * scale),
            workArea.Height);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = _minimumWidthInPixels;
            presenter.PreferredMinimumHeight = _minimumHeightInPixels;
        }
    }
}
