using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using Microsoft.Windows.ApplicationModel.Resources;
using PicForLater.App.Services;
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

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint windowHandle);

    public MainWindow()
    {
        InitializeComponent();

#if PICFORLATER_UI_VISUAL_FIXTURE
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(
            AppTitleBar,
            $"fixture:{UiTestVisualFixtureSeeder.FixtureId}");
#endif

        Title = new ResourceLoader().GetString("MainWindowTitle");

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }
        ConfigureSizeForCurrentDisplay();
        AppWindow.Changed += AppWindow_Changed;
        Activated += MainWindow_Activated;
        ThemePreferenceService.Instance.Initialize(WindowRoot);

        // Navigate the root frame to the main page on startup.
        RootFrame.Navigate(typeof(MainPage));
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
        if (_minimumSizeConfiguredAfterActivation ||
            args.WindowActivationState == WindowActivationState.Deactivated)
        {
            return;
        }

        _minimumSizeConfiguredAfterActivation = true;
        _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        UpdateMinimumSize(GetDpiForWindow(_windowHandle));
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
