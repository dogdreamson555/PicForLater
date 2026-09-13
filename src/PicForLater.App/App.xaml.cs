using System.Diagnostics;
using System.Reflection;
using CommunityToolkit.WinUI.Notifications;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PicForLater.Analysis;
using PicForLater.Analysis.PpOcr;
using PicForLater.App.Models;
using PicForLater.App.Services;
using PicForLater.Core.Analysis;
using PicForLater.Core.Images;
using PicForLater.Core.Library;
using PicForLater.Core.Reminders;
using PicForLater.Core.Runtime;
using PicForLater.Infrastructure.Analysis;
using PicForLater.Infrastructure.Library;
using PicForLater.Infrastructure.LocalSend;
using PicForLater.Infrastructure.Reminders;
using PicForLater.Infrastructure.Storage;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace PicForLater.App;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private static readonly TimeSpan ApplicationShutdownTimeout =
        TimeSpan.FromSeconds(30);

    private enum WindowLifecycleState
    {
        Running,
        ExitConfirmation,
        CleaningUp,
        Closed,
    }

    private static readonly CancellationTokenSource AnalysisCancellation = new();
    private static readonly object ForegroundActivationLock = new();
    private static readonly object LocalSendStartupLock = new();
    private static readonly object NotificationActivationLock = new();
    private static readonly object ScreenshotCaptureLifecycleLock = new();
    private static readonly object WindowLifecycleLock = new();
    private static AnalysisQueueWakeSignal? _analysisWakeSignal;
    private static HttpClient? _modelDownloadHttpClient;
    private static HttpClient? _componentDownloadHttpClient;
    private static HttpClient? _remoteAnalysisHttpClient;
    private static HttpClient? _updateCheckHttpClient;
    private static Task? _localSendStartupTask;
    private static ILocalSendReceiverService? _localSendStartupReceiver;
    private static MainWindow? _screenshotCaptureWindow;
    private static IScreenshotCaptureService? _screenshotCapture;
    private static Task _screenshotCaptureStartupTask = Task.CompletedTask;
    private static Task _screenshotCaptureStopTask = Task.CompletedTask;
    private static long _screenshotCaptureWindowGeneration;
    private static bool _screenshotCaptureShuttingDown;
#if PICFORLATER_UI_TESTING
    private static UiTestLocalInferenceRuntime? _uiTestInferenceRuntime;
#else
    private static LocalInferenceWorkerClient? _localInferenceWorker;
    private static BackgroundFailureCircuit? _localInferenceFailureCircuit;
#endif
#if !PICFORLATER_UI_TESTING
    private static bool _windowsOcrAvailable;
#endif
    private static BackgroundWorkerSupervisor? _analysisWorkerSupervisor;
    private static BackgroundWorkerSupervisor? _reminderWorkerSupervisor;
    private static bool _isMainWindowReady;
    private static bool _isForegroundActivationPending;
    private static SystemTrayIconAdapter? _systemTrayIcon;
    private static BusinessFeatureCoordinator? _businessFeatures;
    private static int _observedInferenceAccelerationMode = -1;
    private static WindowLifecycleState _windowLifecycleState = WindowLifecycleState.Running;
    private static Task? _applicationExitTask;
    private static Task? _windowCloseTask;
    private static bool _windowClosed;
    private static bool _skipExitConfirmation;
#if !PICFORLATER_UI_TESTING
    private static bool _toastNotificationsRegistered;
#endif
    private static (Guid ReminderId, Guid ImageItemId)? _pendingNotificationActivation;

    /// <summary>
    /// The main application window. Use <c>App.Window</c> from any class that needs
    /// the window reference (for dialogs, pickers, interop, etc.).
    /// </summary>
    public static Window Window { get; private set; } = null!;

    /// <summary>
    /// The UI thread dispatcher. Use <c>App.DispatcherQueue</c> to marshal calls
    /// to the UI thread. Fully qualified to avoid CS0104 ambiguity with
    /// <see cref="Windows.System.DispatcherQueue"/>.
    /// </summary>
    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;

    /// <summary>
    /// Coordinates local metadata readiness and recoverable retries without exposing
    /// sensitive exception text to the presentation layer.
    /// </summary>
    public static IStorageReadinessService StorageReadiness { get; private set; } = null!;

    /// <summary>
    /// The immutable-original store. It remains unavailable when the app-private data root
    /// could not be created, and callers must first observe <see cref="StorageInitialization"/>.
    /// </summary>
    public static IManagedImageStorage? ManagedImageStorage { get; private set; }

    public static AppDataPaths? DataPaths { get; private set; }

    public static ILibraryService? Library { get; private set; }

    public static IImageImportService? ImageImporter { get; private set; }

    public static ILocalSendReceiverService? LocalSendReceiver { get; private set; }

    public static ILocalSendReceivePreferenceService LocalSendReceivePreference { get; } =
        LocalSendReceivePreferenceService.Instance;

    internal static BusinessFeatureCoordinator? BusinessFeatures => _businessFeatures;

    internal static bool LocalAnalysisAvailable
    {
        get
        {
#if PICFORLATER_UI_TESTING
            return true;
#else
            return _windowsOcrAvailable
                || _localInferenceWorker?.CachedOcrAvailability == true;
#endif
        }
    }

    internal static CloseBehaviorPreferenceService CloseBehaviorPreference { get; } =
        CloseBehaviorPreferenceService.Instance;

    internal static bool IsShuttingDown
    {
        get
        {
            lock (WindowLifecycleLock)
            {
                return _windowLifecycleState is
                    WindowLifecycleState.CleaningUp or
                    WindowLifecycleState.Closed;
            }
        }
    }

    public static IReminderService? Reminders { get; private set; }

    public static IModelPackageService? ModelPackages { get; private set; }

    public static IRemoteApiProfileService? RemoteApiProfiles { get; private set; }

    public static IRemoteApiCredentialService? RemoteApiCredentials { get; private set; }

    public static IRemoteApiConnectionTester? RemoteApiConnectionTester { get; private set; }

    public static IAnalysisProfileSnapshotProvider? AnalysisProfiles { get; private set; }

    public static IRecommendedModelService? RecommendedModels { get; private set; }

    public static INvidiaCudaEnvironmentService? NvidiaCudaEnvironment { get; private set; }

    public static IAnalysisReanalysisService? Reanalysis { get; private set; }

    public static AppReleaseVersion CurrentVersion { get; private set; }

    public static IUpdateCheckService UpdateCheck { get; private set; } = null!;

    public static LocalInferenceComponentLocator? LocalInferenceComponents { get; private set; }

    public static LocalInferenceComponentInstaller? LocalInferenceComponentInstaller { get; private set; }

    public static LocalInferenceComponentStore? LocalInferenceComponentStore { get; private set; }

    public static IInferenceAccelerationPreferenceService InferenceAcceleration { get; } =
        CreateInferenceAccelerationPreference();

    public static AnalysisQueueWakeSignal? AnalysisUpdates => _analysisWakeSignal;

    public static IScreenshotCaptureService? ScreenshotCapture
    {
        get
        {
            lock (ScreenshotCaptureLifecycleLock)
            {
                return _screenshotCapture;
            }
        }
    }

    public static event Action<IScreenshotCaptureService?>? ScreenshotCaptureServiceChanged;

    internal static event Action<ILocalSendReceiverService?>? LocalSendReceiverServiceChanged;

    internal static event Action<bool>? LocalAnalysisAvailabilityChanged;

    public static event Action<Guid>? NotificationImageRequested;

    public static Guid? PendingNotificationImageItemId { get; private set; }

    public static event Action<Guid>? ReminderCreationRequested;

    public static Guid? PendingReminderCreationImageItemId { get; private set; }

    public static event Action<BackgroundWorkerStatus>? BackgroundWorkerStatusChanged;

    public static WindowsImageContentProcessor ImageProcessor { get; } = new();

    /// <summary>
    /// The native window handle (HWND). Use for file pickers,
    /// <c>DataTransferManager</c>, and any WinRT interop that requires
    /// <c>InitializeWithWindow</c>.
    /// </summary>
    public static nint WindowHandle =>
        WinRT.Interop.WindowNative.GetWindowHandle(Window);

    /// <summary>
    /// Initializes the singleton application object.
    /// </summary>
    public App()
    {
#if PICFORLATER_UI_VISUAL_FIXTURE
        UiTestVisualFixtureSeeder.ConfigureProcessCulture();
#endif
#if !PICFORLATER_UI_TESTING
        if (Environment.GetCommandLineArgs().Contains(
                Program.UninstallNotificationsArgument,
                StringComparer.Ordinal))
        {
            try
            {
                ToastNotificationManagerCompat.Uninstall();
            }
            finally
            {
                Environment.Exit(0);
            }
        }

        RegisterToastNotifications();
#endif
        InitializeComponent();
        CurrentVersion = ReadCurrentVersion();
        _updateCheckHttpClient = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
#if PICFORLATER_UI_TESTING
        UpdateCheck = new UiTestUpdateCheckService(CurrentVersion);
#else
        UpdateCheck = new GitHubUpdateCheckService(
            _updateCheckHttpClient,
            CurrentVersion);
#endif
    }

    private static AppReleaseVersion ReadCurrentVersion()
    {
        var informationalVersion = typeof(App).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (!AppReleaseVersion.TryParseLocal(informationalVersion, out var version))
        {
            throw new InvalidOperationException(
                "The PicForLater App informational version must use M.m.p with optional build metadata.");
        }

        return version;
    }

    private static bool DetectWindowsOcrAvailability()
    {
        try
        {
            return new WindowsMediaOcrProvider()
                .Descriptor
                .SupportedLanguageTags
                .Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static IInferenceAccelerationPreferenceService CreateInferenceAccelerationPreference()
    {
        var preference = InferenceAccelerationPreferenceService.Instance;
#if PICFORLATER_UI_VISUAL_FIXTURE
        preference.SetMode(InferenceAccelerationMode.Automatic);
#endif
        return preference;
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        lock (WindowLifecycleLock)
        {
            _windowLifecycleState = WindowLifecycleState.Running;
            _applicationExitTask = null;
            _windowCloseTask = null;
            _windowClosed = false;
            _skipExitConfirmation = false;
        }

        StorageReadiness = new StorageReadinessService(StartStorageInitialization);
        StorageReadiness.ReadinessChanged += StorageReadiness_ReadinessChanged;
        _observedInferenceAccelerationMode = (int)InferenceAcceleration.CurrentMode;
        InferenceAcceleration.StateChanged += InferenceAcceleration_StateChanged;
        _businessFeatures = new BusinessFeatureCoordinator(
            DispatcherQueue,
            () => LocalSendReceiver,
            () => ScreenshotCapture,
            () => RemoteApiProfiles,
            () => RemoteApiCredentials,
            LocalSendReceivePreference,
            () => LocalAnalysisAvailable);
        _businessFeatures.StateChanged += BusinessFeatures_StateChanged;
        // Storage initialization starts from the StorageReadiness constructor,
        // so a fast initialization can publish a receiver before the
        // coordinator is constructed. Attach the current facts as a
        // compensation for that startup ordering window.
        _businessFeatures.AttachLocalSendReceiver(LocalSendReceiver);
        _businessFeatures.AttachScreenshotCapture(ScreenshotCapture);
        var mainWindow = new MainWindow();
        Window = mainWindow;
        try
        {
            _systemTrayIcon = new SystemTrayIconAdapter();
        }
        catch (Exception exception)
        {
            // Tray registration is optional until a close-to-tray request is
            // made. A Shell/Explorer failure must not prevent the main window
            // from starting or silently change the user's close policy.
            Debug.WriteLine($"System tray registration failed: {exception.GetType().Name}.");
        }
        mainWindow.Activated += MainWindow_ActivatedForTray;

        long windowGeneration;
        lock (ScreenshotCaptureLifecycleLock)
        {
            _screenshotCaptureWindow = mainWindow;
            _screenshotCaptureShuttingDown = false;
            windowGeneration = ++_screenshotCaptureWindowGeneration;
        }

        mainWindow.NativeClosing += MainWindow_NativeClosing;
        Window.Closed += OnWindowClosed;
        TrackScreenshotCaptureStartup(InitializeScreenshotCaptureAsync(
            mainWindow,
            windowGeneration));
        Window.Activate();

        bool activateAgain;
        lock (ForegroundActivationLock)
        {
            _isMainWindowReady = true;
            activateAgain = _isForegroundActivationPending;
            _isForegroundActivationPending = false;
        }

        if (activateAgain)
        {
            BringMainWindowToForeground();
        }
    }

    private static async Task InitializeScreenshotCaptureAsync(
        MainWindow mainWindow,
        long windowGeneration)
    {
        StorageReadinessResult readiness = await StorageReadiness
            .EnsureReadyAsync(forceRetry: false)
            .ConfigureAwait(true);
        if (readiness.Status == StorageReadinessStatus.Ready)
        {
            await TryStartScreenshotCaptureAsync(mainWindow, windowGeneration)
                .ConfigureAwait(true);
        }
    }

    private static void StorageReadiness_ReadinessChanged(
        object? sender,
        StorageReadinessChangedEventArgs e)
    {
        _businessFeatures?.SetStorageReady(
            e.Result.Status == StorageReadinessStatus.Ready);
        if (e.Result.Status != StorageReadinessStatus.Ready)
        {
            return;
        }

        StartLocalSendReceiverIfRequested();

        MainWindow? mainWindow;
        long windowGeneration;
        lock (ScreenshotCaptureLifecycleLock)
        {
            if (_screenshotCaptureShuttingDown || _screenshotCaptureWindow is null)
            {
                return;
            }

            mainWindow = _screenshotCaptureWindow;
            windowGeneration = _screenshotCaptureWindowGeneration;
        }

        _ = DispatcherQueue.TryEnqueue(() =>
        {
            Task startupTask = TryStartScreenshotCaptureAsync(mainWindow, windowGeneration);
            TrackScreenshotCaptureStartup(startupTask);
        });
    }

    private static async Task TryStartScreenshotCaptureAsync(
        MainWindow mainWindow,
        long windowGeneration)
    {
        lock (ScreenshotCaptureLifecycleLock)
        {
            if (_screenshotCaptureShuttingDown ||
                _screenshotCaptureWindowGeneration != windowGeneration ||
                !ReferenceEquals(_screenshotCaptureWindow, mainWindow) ||
                _screenshotCapture is not null)
            {
                return;
            }
        }

        if (ImageImporter is null)
        {
            return;
        }

#if PICFORLATER_UI_TESTING
        IScreenshotCaptureService service = new UiTestScreenshotCaptureService();
#else
        var fileNameFormat = new Microsoft.Windows.ApplicationModel.Resources.ResourceLoader()
            .GetString("ClipboardFileNameFormat");
        var importer = new ScreenshotCaptureImporter(
            () => ImageImporter,
            ImageProcessor.NormalizeToPngAsync,
            fileNameFormat);
        IScreenshotCaptureService service = new ScreenshotCaptureService(
            mainWindow.ScreenshotCapturePlatform,
            new ScreenshotCapturePreferenceService(LocalPreferenceStore.Instance),
            importer);
#endif

        lock (ScreenshotCaptureLifecycleLock)
        {
            if (_screenshotCaptureShuttingDown ||
                _screenshotCaptureWindowGeneration != windowGeneration ||
                !ReferenceEquals(_screenshotCaptureWindow, mainWindow) ||
                _screenshotCapture is not null ||
                ImageImporter is null)
            {
                return;
            }

            _screenshotCapture = service;
        }

        NotifyScreenshotCaptureServiceChanged(service);
        try
        {
            await service.StartAsync(AnalysisCancellation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (AnalysisCancellation.IsCancellationRequested)
        {
        }
        catch
        {
            // Quick Screenshot is optional. Its stable Snapshot reports normal
            // registration failures; an unexpected startup exception must not
            // block access to the local library.
        }
    }

    private static void TrackScreenshotCaptureStartup(Task startupTask)
    {
        lock (ScreenshotCaptureLifecycleLock)
        {
            _screenshotCaptureStartupTask = Task.WhenAll(
                _screenshotCaptureStartupTask,
                startupTask);
        }
    }

    private static void MainWindow_NativeClosing(object? sender, EventArgs e)
    {
        if (sender is MainWindow mainWindow)
        {
            mainWindow.NativeClosing -= MainWindow_NativeClosing;
        }

        BeginScreenshotCaptureShutdown();
    }

    internal static void RequestWindowClose()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            _ = DispatcherQueue.TryEnqueue(RequestWindowClose);
            return;
        }

        if (Window is not MainWindow mainWindow)
        {
            return;
        }

        if (!CloseBehaviorPreference.IsKeepInSystemTrayEnabled)
        {
            _ = RequestApplicationExitAsync();
            return;
        }

        TaskCompletionSource? completionSource = null;
        lock (WindowLifecycleLock)
        {
            if (_windowLifecycleState is
                    WindowLifecycleState.CleaningUp or
                    WindowLifecycleState.Closed ||
                _windowCloseTask is not null ||
                _applicationExitTask is not null)
            {
                return;
            }

            completionSource = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _windowCloseTask = completionSource.Task;
            _windowLifecycleState = WindowLifecycleState.ExitConfirmation;
        }

        _ = HandleWindowCloseAsync(mainWindow, completionSource);
    }

    internal static void RequestSystemSessionShutdown()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            _ = DispatcherQueue.TryEnqueue(RequestSystemSessionShutdown);
            return;
        }

        lock (WindowLifecycleLock)
        {
            if (_windowLifecycleState is
                    WindowLifecycleState.CleaningUp or
                    WindowLifecycleState.Closed)
            {
                return;
            }

            _skipExitConfirmation = true;
        }

        _ = RequestApplicationExitAsync(skipPageConfirmation: true);
    }

    internal static Task RequestApplicationExitAsync(
        bool skipPageConfirmation = false)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            var marshalledCompletion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!DispatcherQueue.TryEnqueue(() =>
            {
                _ = RequestApplicationExitAsync(skipPageConfirmation).ContinueWith(
                    completedTask =>
                    {
                        if (completedTask.IsFaulted)
                        {
                            marshalledCompletion.TrySetException(
                                completedTask.Exception!.InnerExceptions);
                        }
                        else if (completedTask.IsCanceled)
                        {
                            marshalledCompletion.TrySetCanceled();
                        }
                        else
                        {
                            marshalledCompletion.TrySetResult();
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }))
            {
                marshalledCompletion.TrySetResult();
            }

            return marshalledCompletion.Task;
        }

        if (Window is not MainWindow mainWindow)
        {
            return Task.CompletedTask;
        }

        TaskCompletionSource? completionSource = null;
        lock (WindowLifecycleLock)
        {
            if (_windowLifecycleState == WindowLifecycleState.Closed)
            {
                return Task.CompletedTask;
            }

            if (skipPageConfirmation)
            {
                _skipExitConfirmation = true;
            }

            if (_applicationExitTask is not null)
            {
                return _applicationExitTask;
            }

            completionSource = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _applicationExitTask = completionSource.Task;
            _windowLifecycleState = WindowLifecycleState.ExitConfirmation;
        }

        _ = CompleteApplicationExitAsync(mainWindow, completionSource);
        return completionSource.Task;
    }

    internal static bool TryEnsureSystemTrayIcon()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            return false;
        }

        lock (WindowLifecycleLock)
        {
            if (_windowLifecycleState is
                    WindowLifecycleState.CleaningUp or
                    WindowLifecycleState.Closed)
            {
                return false;
            }
        }

        var trayIcon = _systemTrayIcon;
        if (trayIcon is null)
        {
            try
            {
                var newTrayIcon = new SystemTrayIconAdapter();
                var existingTrayIcon = Interlocked.CompareExchange(
                    ref _systemTrayIcon,
                    newTrayIcon,
                    comparand: null);
                if (existingTrayIcon is not null)
                {
                    newTrayIcon.Dispose();
                    trayIcon = existingTrayIcon;
                }
                else
                {
                    trayIcon = newTrayIcon;
                }
            }
            catch (Exception exception)
            {
                Debug.WriteLine(
                    $"System tray registration retry could not create an adapter: {exception.GetType().Name}.");
                return false;
            }
        }

        if (_businessFeatures is { } businessFeatures)
        {
            ApplySystemTrayBusinessState(businessFeatures.CurrentState);
        }

        return trayIcon.TryEnsureCreated();
    }

    private static async Task HandleWindowCloseAsync(
        MainWindow mainWindow,
        TaskCompletionSource completionSource)
    {
        try
        {
            while (true)
            {
                bool hidden;
                if (TryEnsureSystemTrayIcon())
                {
                    try
                    {
                        mainWindow.HideToTray();
                        hidden = true;
                    }
                    catch (Exception exception)
                    {
                        Debug.WriteLine(
                            $"System tray hide failed: {exception.GetType().Name}.");
                        hidden = false;
                    }
                }
                else
                {
                    hidden = false;
                }

                if (hidden)
                {
                    return;
                }

                var decision = await ShowTrayUnavailableDialogAsync(mainWindow);
                if (decision == ContentDialogResult.Primary)
                {
                    continue;
                }

                if (decision == ContentDialogResult.Secondary)
                {
                    await RequestApplicationExitAsync();
                }

                return;
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine(
                $"System tray close decision failed: {exception.GetType().Name}.");
        }
        finally
        {
            lock (WindowLifecycleLock)
            {
                if (ReferenceEquals(_windowCloseTask, completionSource.Task))
                {
                    _windowCloseTask = null;
                }

                if (_windowLifecycleState == WindowLifecycleState.ExitConfirmation)
                {
                    _windowLifecycleState = WindowLifecycleState.Running;
                }
            }

            completionSource.TrySetResult();
        }
    }

    private static async Task<ContentDialogResult> ShowTrayUnavailableDialogAsync(
        MainWindow mainWindow)
    {
        if (mainWindow.Content is not FrameworkElement root
            || root.XamlRoot is null)
        {
            return ContentDialogResult.None;
        }

        var resources = new Microsoft.Windows.ApplicationModel.Resources.ResourceLoader();
        var dialog = new ContentDialog
        {
            XamlRoot = root.XamlRoot,
            Title = resources.GetString("TrayUnavailableDialogTitle"),
            Content = resources.GetString("TrayUnavailableDialogContent"),
            PrimaryButtonText = resources.GetString("TrayUnavailableDialogRetryButtonText"),
            SecondaryButtonText = resources.GetString("TrayUnavailableDialogExitButtonText"),
            CloseButtonText = resources.GetString("TrayUnavailableDialogCancelButtonText"),
            DefaultButton = ContentDialogButton.Primary,
        };

        try
        {
            return await dialog.ShowAsync();
        }
        catch (Exception exception)
        {
            Debug.WriteLine(
                $"System tray recovery dialog failed: {exception.GetType().Name}.");
            return ContentDialogResult.None;
        }
    }

    private static async Task CompleteApplicationExitAsync(
        MainWindow mainWindow,
        TaskCompletionSource completionSource)
    {
        bool windowClosed = IsWindowClosed();
        bool skipExitConfirmation = ShouldSkipExitConfirmation();
        bool restored = windowClosed || skipExitConfirmation;
        if (!restored)
        {
            try
            {
                // A hidden window cannot host an edit-confirmation dialog. Showing it
                // here also makes tray-initiated exit observable while the decision is
                // pending; the window is disabled again once cleanup starts.
                mainWindow.RestoreAndActivate();
                restored = true;
            }
            catch (Exception exception)
            {
                Debug.WriteLine(
                    $"Window restore before exit confirmation failed: {exception.GetType().Name}.");
            }
        }

        // Window.Closed may race with the restore or confirmation task. Once the
        // HWND is gone, cleanup takes precedence over a failed/ cancelled dialog.
        if (!restored && !IsWindowClosed())
        {
            if (CancelApplicationExit(completionSource))
            {
                return;
            }
        }

        bool canLeave = skipExitConfirmation || IsWindowClosed();
        if (!canLeave)
        {
            try
            {
                canLeave = mainWindow.CurrentMainPage is not MainPage mainPage
                    || await mainPage.TryLeaveCurrentPageAsync();
            }
            catch (Exception exception)
            {
                Debug.WriteLine(
                    $"Exit confirmation failed: {exception.GetType().Name}.");
                canLeave = false;
            }

            // A system-session request or an unexpected native close supersedes
            // an ordinary page-confirmation result.
            canLeave |= ShouldSkipExitConfirmation() || IsWindowClosed();
        }

        if (!canLeave)
        {
            if (CancelApplicationExit(completionSource))
            {
                return;
            }

            // The window closed while the confirmation result was being
            // processed. Continue into cleanup rather than reviving a dead app.
            canLeave = true;
        }

        lock (WindowLifecycleLock)
        {
            if (_windowLifecycleState != WindowLifecycleState.ExitConfirmation)
            {
                completionSource.TrySetResult();
                return;
            }

            _windowLifecycleState = WindowLifecycleState.CleaningUp;
        }

        bool cleanupCompleted = false;
        try
        {
            if (!IsWindowClosed())
            {
                mainWindow.DisableInteractionForShutdown();
            }

            BeginScreenshotCaptureShutdown();
            mainWindow.PrepareForFinalClose();
            cleanupCompleted = await PerformApplicationCleanupAsync();
        }
        catch (Exception exception)
        {
            // Cleanup is best effort. The process must still leave the normal
            // running state even when an optional service fails to stop.
            Debug.WriteLine(
                $"Application shutdown encountered an unexpected error: {exception.GetType().Name}.");
        }
        finally
        {
            FinalizeApplicationExit(mainWindow, cleanupCompleted);
            completionSource.TrySetResult();
        }
    }

    private static bool IsWindowClosed()
    {
        lock (WindowLifecycleLock)
        {
            return _windowClosed;
        }
    }

    private static bool ShouldSkipExitConfirmation()
    {
        lock (WindowLifecycleLock)
        {
            return _skipExitConfirmation;
        }
    }

    private static bool CancelApplicationExit(TaskCompletionSource completionSource)
    {
        lock (WindowLifecycleLock)
        {
            if (_windowClosed)
            {
                return false;
            }

            if (ReferenceEquals(_applicationExitTask, completionSource.Task))
            {
                _applicationExitTask = null;
            }

            if (_windowLifecycleState == WindowLifecycleState.ExitConfirmation)
            {
                _windowLifecycleState = WindowLifecycleState.Running;
            }

            _skipExitConfirmation = false;
        }

        completionSource.TrySetResult();
        return true;
    }

    private static void FinalizeApplicationExit(
        MainWindow mainWindow,
        bool cleanupCompleted)
    {
        DisposeBusinessFeatures();
        DisposeSystemTrayIcon();
        UnregisterInstanceKeySafely();

        bool windowClosed;
        lock (WindowLifecycleLock)
        {
            windowClosed = _windowClosed;
            _windowLifecycleState = WindowLifecycleState.Closed;
        }

        bool finalCloseFailed = false;
        if (!windowClosed)
        {
            try
            {
                mainWindow.CloseAfterFinalCleanup();
            }
            catch (Exception exception)
            {
                finalCloseFailed = true;
                Debug.WriteLine(
                    $"Final window close failed: {exception.GetType().Name}.");
            }
        }

        if (!cleanupCompleted || finalCloseFailed)
        {
            Debug.WriteLine(
                "Application shutdown exceeded its safe completion path; terminating the process.");
            Environment.Exit(1);
        }
    }

    private static void BeginScreenshotCaptureShutdown()
    {
        IScreenshotCaptureService? service;
        lock (ScreenshotCaptureLifecycleLock)
        {
            if (_screenshotCaptureShuttingDown)
            {
                return;
            }

            _screenshotCaptureShuttingDown = true;
            _screenshotCaptureWindowGeneration++;
            service = _screenshotCapture;
        }

        StorageReadiness.ReadinessChanged -= StorageReadiness_ReadinessChanged;
        InferenceAcceleration.StateChanged -= InferenceAcceleration_StateChanged;
        try
        {
            AnalysisCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        Task stopTask = service is null
            ? Task.CompletedTask
            : StopScreenshotCaptureAsync(service);
        lock (ScreenshotCaptureLifecycleLock)
        {
            _screenshotCaptureStopTask = stopTask;
        }
    }

    private static async Task StopScreenshotCaptureAsync(IScreenshotCaptureService service)
    {
        try
        {
            await service.StopAsync().ConfigureAwait(false);
        }
        catch
        {
            // WM_NCDESTROY/platform disposal is the final native containment
            // boundary. Business shutdown still proceeds to importer disposal.
        }
    }

    private static void NotifyScreenshotCaptureServiceChanged(
        IScreenshotCaptureService? service)
    {
        _businessFeatures?.AttachScreenshotCapture(service);
        Delegate[] handlers = ScreenshotCaptureServiceChanged?.GetInvocationList() ?? [];
        foreach (Action<IScreenshotCaptureService?> handler in handlers.Cast<
                     Action<IScreenshotCaptureService?>>())
        {
            try
            {
                handler(service);
            }
            catch
            {
                // A page that is navigating away cannot make capture startup fail.
            }
        }
    }

    private static void NotifyLocalSendReceiverServiceChanged(
        ILocalSendReceiverService? service)
    {
        _businessFeatures?.AttachLocalSendReceiver(service);
        Delegate[] handlers = LocalSendReceiverServiceChanged?.GetInvocationList() ?? [];
        foreach (Action<ILocalSendReceiverService?> handler in handlers.Cast<
                     Action<ILocalSendReceiverService?>>())
        {
            try
            {
                handler(service);
            }
            catch
            {
                // A page that is navigating away cannot make receiver startup
                // or replacement fail.
            }
        }
    }

    internal static void RequestForegroundActivation()
    {
        lock (WindowLifecycleLock)
        {
            if (_windowLifecycleState is
                    WindowLifecycleState.CleaningUp or
                    WindowLifecycleState.Closed)
            {
                return;
            }
        }

        lock (ForegroundActivationLock)
        {
            if (!_isMainWindowReady)
            {
                _isForegroundActivationPending = true;
                return;
            }
        }

        _ = DispatcherQueue.TryEnqueue(() => _ = BringMainWindowToForeground());
    }

    private static bool BringMainWindowToForeground()
    {
        if (!IsMainWindowReady())
        {
            return false;
        }

        lock (WindowLifecycleLock)
        {
            if (_windowLifecycleState is
                    WindowLifecycleState.CleaningUp or
                    WindowLifecycleState.Closed)
            {
                return false;
            }
        }

        if (Window is MainWindow mainWindow)
        {
            try
            {
                mainWindow.RestoreAndActivate();
                return true;
            }
            catch (Exception exception)
            {
                Debug.WriteLine(
                    $"Window restore failed: {exception.GetType().Name}.");
            }
        }

        return false;
    }

    private static bool IsMainWindowReady()
    {
        lock (ForegroundActivationLock)
        {
            return _isMainWindowReady;
        }
    }

    internal static void RefreshSystemTrayBusinessState()
    {
        var businessFeatures = _businessFeatures;
        if (businessFeatures is null || IsShuttingDown)
        {
            return;
        }

        _ = businessFeatures.RefreshAnalysisAsync();
        ApplySystemTrayBusinessState(businessFeatures.CurrentState);
    }

    internal static void NotifyAnalysisConfigurationChanged()
    {
        if (IsShuttingDown)
        {
            return;
        }

        _ = _businessFeatures?.RefreshAnalysisAsync();
    }

    internal static void InvalidateLocalAnalysisAvailability(
        bool refreshAfterInvalidation = true)
    {
#if !PICFORLATER_UI_TESTING
        _localInferenceWorker?.InvalidateCachedOcrAvailability();
#endif
        NotifyLocalAnalysisAvailabilityChanged();
        var businessFeatures = _businessFeatures;
        if (businessFeatures is not null && !IsShuttingDown)
        {
            businessFeatures.SetLocalAnalysisAvailability(LocalAnalysisAvailable);
            _ = businessFeatures.RefreshAnalysisAsync();
        }

        if (refreshAfterInvalidation)
        {
            _ = RefreshLocalAnalysisAvailabilityAsync();
        }
    }

    /// <summary>
    /// Rebuilds the worker OCR capability cache after component or acceleration
    /// changes. The probe is intentionally asynchronous because it may launch the
    /// local worker and perform a named-pipe request.
    /// </summary>
    internal static async Task RefreshLocalAnalysisAvailabilityAsync()
    {
        bool isAvailable;
#if PICFORLATER_UI_TESTING
        isAvailable = true;
#else
        isAvailable = _windowsOcrAvailable;
        if (_localInferenceWorker is { } worker)
        {
            try
            {
                var workerAvailable = await worker.IsAvailableAsync(AnalysisCancellation.Token)
                    .ConfigureAwait(false);
                isAvailable |= workerAvailable;
            }
            catch (OperationCanceledException) when (AnalysisCancellation.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                isAvailable = false;
            }
        }
#endif

        NotifyLocalAnalysisAvailabilityChanged(isAvailable);
        var businessFeatures = _businessFeatures;
        if (businessFeatures is null || IsShuttingDown)
        {
            return;
        }

        businessFeatures.SetLocalAnalysisAvailability(isAvailable);
        _ = businessFeatures.RefreshAnalysisAsync();
    }

    internal static async Task SetLocalSendEnabledFromTrayAsync(bool isEnabled)
    {
        if (IsShuttingDown || _businessFeatures is null)
        {
            return;
        }

        await _businessFeatures.SetLocalSendEnabledAsync(isEnabled)
            .ConfigureAwait(true);
    }

    internal static async Task SelectAnalysisBackendFromTrayAsync(
        AnalysisExecutionBackend backend)
    {
        if (IsShuttingDown || _businessFeatures is null)
        {
            return;
        }

        try
        {
            var result = await _businessFeatures.SelectAnalysisBackendAsync(backend)
                .ConfigureAwait(true);
            if (!result.Applied)
            {
                // ToggleMenuFlyoutItem changes IsChecked before its async command
                // completes. Re-apply the coordinator's reconciled factual
                // snapshot on every rejected/failed request so optimistic UI
                // state cannot survive a failed operation.
                ApplySystemTrayBusinessState(_businessFeatures.CurrentState);
            }
        }
        catch
        {
            // The command surface must never leave a toggled menu item behind
            // when an unexpected coordinator failure escapes its normal result.
            _businessFeatures.ClearAnalysisStateAfterFailedSelection();
            ApplySystemTrayBusinessState(_businessFeatures.CurrentState);
        }
    }

    internal static async Task SetScreenshotEnabledFromTrayAsync(bool isEnabled)
    {
        if (IsShuttingDown || _businessFeatures is null)
        {
            return;
        }

        await _businessFeatures.SetScreenshotEnabledAsync(isEnabled)
            .ConfigureAwait(true);
    }

    private static void BusinessFeatures_StateChanged(TrayBusinessState state)
    {
        if (IsShuttingDown)
        {
            return;
        }

        ApplySystemTrayBusinessState(state);
    }

    private static void InferenceAcceleration_StateChanged(
        object? sender,
        EventArgs e)
    {
        _ = sender;
        _ = e;
        var currentMode = (int)InferenceAcceleration.CurrentMode;
        if (Interlocked.Exchange(ref _observedInferenceAccelerationMode, currentMode)
            == currentMode)
        {
            return;
        }

        InvalidateLocalAnalysisAvailability();
    }

#if !PICFORLATER_UI_TESTING
    private static void LocalInferenceWorker_OcrAvailabilityChanged(bool isAvailable)
    {
        NotifyLocalAnalysisAvailabilityChanged(_windowsOcrAvailable || isAvailable);
        var businessFeatures = _businessFeatures;
        if (businessFeatures is null || IsShuttingDown)
        {
            return;
        }

        businessFeatures.SetLocalAnalysisAvailability(
            _windowsOcrAvailable || isAvailable);
        _ = businessFeatures.RefreshAnalysisAsync();
    }
#endif

    private static void NotifyLocalAnalysisAvailabilityChanged(bool? isAvailable = null)
    {
        Delegate[] handlers = LocalAnalysisAvailabilityChanged?.GetInvocationList() ?? [];
        var availability = isAvailable ?? LocalAnalysisAvailable;
        foreach (Action<bool> handler in handlers.Cast<Action<bool>>())
        {
            try
            {
                handler(availability);
            }
            catch (Exception exception)
            {
                Debug.WriteLine(
                    $"Local analysis availability observer failed: {exception.GetType().Name}.");
            }
        }
    }

    private static void ApplySystemTrayBusinessState(TrayBusinessState state)
    {
        var trayIcon = _systemTrayIcon;
        if (trayIcon is null)
        {
            return;
        }

        var localSendSnapshot = state.LocalSendSnapshot;
        var localSendEnabled = state.StorageReady
            && localSendSnapshot is not null
            && !state.IsLocalSendOperationInProgress
            && localSendSnapshot.Status is not
                LocalSendReceiverStatus.Starting and not LocalSendReceiverStatus.Stopping;
        var localSendUnavailable = !state.StorageReady
            || localSendSnapshot is null
            || localSendSnapshot.Status is
                LocalSendReceiverStatus.Starting or
                LocalSendReceiverStatus.Stopping or
                LocalSendReceiverStatus.Faulted;
        trayIcon.SetLocalSendState(
            localSendEnabled,
            state.IsLocalSendRequested,
            localSendUnavailable);

        var localAnalysisEnabled = state.StorageReady
            && state.LocalAnalysisAvailable
            && !state.IsAnalysisOperationInProgress;
        var remoteAnalysisEnabled = state.StorageReady
            && state.IsRemoteAnalysisSelectable
            && !state.IsAnalysisOperationInProgress;
        // Keep the parent visible while an available backend is temporarily
        // busy; hide it only when there is no executable backend to choose.
        var analysisModeVisible = state.StorageReady
            && (state.LocalAnalysisAvailable || state.IsRemoteAnalysisSelectable);
        trayIcon.SetAnalysisState(
            localAnalysisEnabled,
            remoteAnalysisEnabled,
            state.IsRemoteAnalysisVisible,
            analysisModeVisible,
            state.AnalysisBackend == AnalysisExecutionBackend.Local,
            state.AnalysisBackend == AnalysisExecutionBackend.RemoteApi);

        var screenshotSnapshot = state.ScreenshotSnapshot;
        var canToggleScreenshot = state.StorageReady
            && screenshotSnapshot is not null
            && !state.IsScreenshotOperationInProgress
            && (screenshotSnapshot.RegistrationState is
                    not RegistrationState.Conflict and not RegistrationState.Faulted
                && screenshotSnapshot.CaptureState == CaptureState.Idle
                || screenshotSnapshot.IsEnabledRequested);
        trayIcon.SetQuickScreenshotState(
            canToggleScreenshot,
            screenshotSnapshot?.IsEnabledRequested == true,
            !state.StorageReady
            || screenshotSnapshot is null
            || screenshotSnapshot.RegistrationState is
                RegistrationState.Conflict or RegistrationState.Faulted
            || screenshotSnapshot.CaptureState is not CaptureState.Idle);
    }

    private static Task<DatabaseInitializationResult> StartStorageInitialization()
    {
        try
        {
            return Task.Run(async () =>
            {
                var paths = AppRuntimePaths.Paths;
                var storage = new ManagedImageStorage(paths);
                var result = await new SqliteDatabaseInitializer(paths).InitializeAsync().ConfigureAwait(false);
                var remoteApiProfiles = new SqliteRemoteApiProfileService(paths);
                try
                {
                    // Startup safety gate: do not publish remote services or construct
                    // background workers until every built-in preset is synchronized.
                    await RemoteApiProviderCatalog.EnsureProfilesAsync(
                            remoteApiProfiles,
                            AnalysisCancellation.Token)
                        .ConfigureAwait(false);
                }
                catch
                {
                    remoteApiProfiles.Dispose();
                    throw;
                }

                DataPaths = paths;
                ManagedImageStorage = storage;
                IReminderNotificationScheduler reminderScheduler;
#if PICFORLATER_UI_TESTING
                reminderScheduler = new InMemoryReminderNotificationScheduler();
#else
                reminderScheduler = new WindowsReminderNotificationScheduler();
#endif
#if PICFORLATER_UI_VISUAL_FIXTURE
                TimeProvider? workflowTimeProvider = UiTestVisualFixtureSeeder.Clock;
#else
                TimeProvider? workflowTimeProvider = null;
#endif
                var reminderService = new SqliteReminderService(
                    paths,
                    reminderScheduler,
                    workflowTimeProvider);
                Reminders = reminderService;
                var library = new LibraryService(paths, storage, reminderService);
                Library = library;
                _analysisWakeSignal = new AnalysisQueueWakeSignal();
#if PICFORLATER_UI_TESTING
                _uiTestInferenceRuntime = new UiTestLocalInferenceRuntime();
#endif
                _modelDownloadHttpClient = new HttpClient(new HttpClientHandler
                {
                    AllowAutoRedirect = true,
                    MaxAutomaticRedirections = 10,
                    UseCookies = false,
                })
                {
                    Timeout = Timeout.InfiniteTimeSpan,
                };
                _componentDownloadHttpClient = new HttpClient(new HttpClientHandler
                {
                    AllowAutoRedirect = false,
                    UseCookies = false,
                })
                {
                    Timeout = Timeout.InfiniteTimeSpan,
                };
#if PICFORLATER_UI_VISUAL_FIXTURE
                INvidiaCudaEnvironmentService nvidiaCudaEnvironment =
                    new UiTestNvidiaCudaEnvironmentService(paths);
#else
                INvidiaCudaEnvironmentService nvidiaCudaEnvironment =
                    new NvidiaCudaEnvironmentService(paths, _modelDownloadHttpClient);
#endif
                NvidiaCudaEnvironment = nvidiaCudaEnvironment;
#if PICFORLATER_UI_TESTING
                IPpOcrV6InferenceRuntime ppOcrRuntime = _uiTestInferenceRuntime;
                IQwenGenerationRuntime qwenRuntime = _uiTestInferenceRuntime;
#else
                var localInferenceComponents = new LocalInferenceComponentLocator(
                    paths,
                    LocalInferenceWorkerClient.GetProcessArchitecture(),
                    PicForLater.LocalInference.Protocol.LocalInferenceProtocol.MinimumSupportedVersion,
                    PicForLater.LocalInference.Protocol.LocalInferenceProtocol.CurrentVersion);
                LocalInferenceComponents = localInferenceComponents;
                _localInferenceWorker = new LocalInferenceWorkerClient(
                    paths,
                    InferenceAcceleration,
                    localInferenceComponents,
                    LocalInferenceWorkerClient.DefaultIdleTimeout);
                _localInferenceWorker.OcrAvailabilityChanged +=
                    LocalInferenceWorker_OcrAvailabilityChanged;
                _localInferenceFailureCircuit = _localInferenceWorker.FailureCircuit;
                _localInferenceFailureCircuit.StatusChanged += OnBackgroundWorkerStatusChanged;
                var localInferenceArchitecture = LocalInferenceWorkerClient.GetProcessArchitecture();
                LocalInferenceComponentStore = new LocalInferenceComponentStore(
                    paths,
                    localInferenceComponents,
                    localInferenceArchitecture,
                    _localInferenceWorker.AcquireComponentMaintenanceAsync);
                if (LocalInferenceComponentReleaseTrust.TryCreateSource(
                        localInferenceArchitecture,
                        out var componentReleaseSource))
                {
                    LocalInferenceComponentInstaller = new LocalInferenceComponentInstaller(
                        paths,
                        _componentDownloadHttpClient,
                        localInferenceComponents,
                        componentReleaseSource!,
                        localInferenceArchitecture,
                        acquireActivationLease:
                            _localInferenceWorker.AcquireComponentMaintenanceAsync);
                }
                IPpOcrV6InferenceRuntime ppOcrRuntime = _localInferenceWorker;
                IQwenGenerationRuntime qwenRuntime = _localInferenceWorker;
#endif
                var modelPackages = new SqliteModelPackageService(
                    paths,
                    new QwenModelPackageValidator(
                        qwenRuntime,
                        paths.AnalysisCacheDirectoryPath));
                ModelPackages = modelPackages;
                RemoteApiProfiles = remoteApiProfiles;
                _ = _businessFeatures?.RefreshAnalysisAsync();
                IRemoteApiCredentialService remoteApiCredentials;
#if PICFORLATER_UI_TESTING
                remoteApiCredentials = new UiTestRemoteApiCredentialService();
#else
                remoteApiCredentials = new WindowsCredentialLockerService();
#endif
                RemoteApiCredentials = remoteApiCredentials;
                var remoteApiRequestAuthorizer = new RemoteApiRequestAuthorizer(
                    remoteApiProfiles);
                _remoteAnalysisHttpClient = new HttpClient(
                    SafeRemoteHttpMessageHandler.Create())
                {
                    Timeout = Timeout.InfiniteTimeSpan,
                };
#if PICFORLATER_UI_TESTING
                RemoteApiConnectionTester = new UiTestRemoteApiConnectionTester();
#else
                RemoteApiConnectionTester = new OpenAiCompatibleRemoteApiConnectionTester(
                    _remoteAnalysisHttpClient,
                    remoteApiCredentials);
#endif
                var analysisProfiles = new CombinedAnalysisProfileSnapshotProvider(
                    modelPackages,
                    remoteApiProfiles);
                AnalysisProfiles = analysisProfiles;
                var ppOcrPackagePath = Path.Combine(
                    paths.ModelPackagesDirectoryPath,
                    "pp-ocrv6-small");
                RecommendedModels = new RecommendedModelDownloadService(
                    paths,
                    _modelDownloadHttpClient,
                    modelPackages,
                    new PpOcrRecommendedPackageInstaller(ppOcrPackagePath, ppOcrRuntime),
                    availableQwenExecutionProviders: qwenRuntime.SupportedExecutionProviders);
                IOcrProvider localOcr;
                IVisionCaptionProvider localVision;
#if PICFORLATER_UI_TESTING
                localOcr = new UiTestOcrProvider();
                localVision = new Qwen3VlProvider(
                    modelPackages,
                    qwenRuntime,
                    ImageProcessor,
                    paths.AnalysisCacheDirectoryPath,
                    InferenceAcceleration);
#else
                _windowsOcrAvailable = DetectWindowsOcrAvailability();
                localOcr = new FallbackOcrProvider(
                    [_localInferenceWorker, new WindowsMediaOcrProvider()]);
                localVision = _localInferenceWorker;
#endif
                var worker = new AnalysisWorker(
                    $"app-{Environment.ProcessId}-{Guid.NewGuid():N}",
                    new SqliteAnalysisJobStore(paths),
                    storage,
                    localOcr,
                    new ExtractiveTextComposer(),
                    _analysisWakeSignal,
                    localVision,
                    new ConditionalAnalysisRouter(),
                    timeProvider: workflowTimeProvider,
                    remoteOcrTextProvider: new OpenAiCompatibleRemoteOcrTextProvider(
                        _remoteAnalysisHttpClient,
                        remoteApiCredentials,
                        remoteApiRequestAuthorizer),
                    remoteVisionProvider: new OpenAiCompatibleRemoteVisionProvider(
                        _remoteAnalysisHttpClient,
                        remoteApiCredentials,
                        remoteApiRequestAuthorizer,
                        ImageProcessor));
                Reanalysis = new SqliteAnalysisReanalysisService(
                    paths,
                    analysisProfiles,
                    _analysisWakeSignal);
                var imageImporter = new ImageImportService(
                    paths,
                    storage,
                    ImageProcessor,
                    _analysisWakeSignal,
                    analysisProfiles);
                ImageImporter = imageImporter;
                await InitializeLocalSendAsync(
                        paths,
                        imageImporter,
                        AnalysisCancellation.Token)
                    .ConfigureAwait(false);
                _analysisWorkerSupervisor = CreateBackgroundWorkerSupervisor(
                    BackgroundWorkerKind.Analysis,
                    worker.RunAsync,
                    "background.analysis.unexpected");
                _reminderWorkerSupervisor = CreateBackgroundWorkerSupervisor(
                    BackgroundWorkerKind.Reminders,
                    reminderService.RunAsync,
                    "background.reminders.unexpected");
                _analysisWorkerSupervisor.StatusChanged += OnBackgroundWorkerStatusChanged;
                _reminderWorkerSupervisor.StatusChanged += OnBackgroundWorkerStatusChanged;
                _ = _analysisWorkerSupervisor.Start(AnalysisCancellation.Token);
                _ = _reminderWorkerSupervisor.Start(AnalysisCancellation.Token);
#if PICFORLATER_UI_VISUAL_FIXTURE
                await UiTestVisualFixtureSeeder.SeedAsync(
                        paths,
                        imageImporter,
                        library,
                        reminderService,
                        AnalysisCancellation.Token)
                    .ConfigureAwait(false);
#endif
                _ = ProcessPendingNotificationActivationAsync();
                return result;
            });
        }
        catch (Exception exception)
        {
            return Task.FromException<DatabaseInitializationResult>(exception);
        }
    }

    private static void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _ = args;
        if (sender is not MainWindow mainWindow)
        {
            return;
        }

        TaskCompletionSource? completionSource = null;
        lock (WindowLifecycleLock)
        {
            _windowClosed = true;
            if (_windowLifecycleState == WindowLifecycleState.Closed
                || _applicationExitTask is not null)
            {
                return;
            }

            completionSource = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _applicationExitTask = completionSource.Task;
            _windowLifecycleState = WindowLifecycleState.CleaningUp;
        }

        _ = CompleteCleanupAfterWindowClosedAsync(mainWindow, completionSource);
    }

    private static async Task CompleteCleanupAfterWindowClosedAsync(
        MainWindow mainWindow,
        TaskCompletionSource completionSource)
    {
        bool cleanupCompleted = false;
        try
        {
            BeginScreenshotCaptureShutdown();
            mainWindow.PrepareForFinalClose();
            cleanupCompleted = await PerformApplicationCleanupAsync();
        }
        catch (Exception exception)
        {
            Debug.WriteLine(
                $"Fallback application shutdown encountered an unexpected error: {exception.GetType().Name}.");
        }
        finally
        {
            FinalizeApplicationExit(mainWindow, cleanupCompleted);
            completionSource.TrySetResult();
        }
    }

    private static async Task<bool> PerformApplicationCleanupAsync()
    {
        using var shutdownDeadline = new CancellationTokenSource(
            ApplicationShutdownTimeout);
        CancellationToken deadlineToken = shutdownDeadline.Token;

        lock (ForegroundActivationLock)
        {
            _isMainWindowReady = false;
            _isForegroundActivationPending = false;
        }

        try
        {
            AnalysisCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        // Close coordinator admission and drain every operation before any
        // receiver, screenshot, profile, or importer resource is released.
        if (!await TryAwaitShutdownTaskAsync(
                _businessFeatures?.WaitForOperationsAsync(),
                deadlineToken))
        {
            return false;
        }

        Task screenshotStartupTask;
        Task screenshotStopTask;
        lock (ScreenshotCaptureLifecycleLock)
        {
            screenshotStartupTask = _screenshotCaptureStartupTask;
            screenshotStopTask = _screenshotCaptureStopTask;
        }

        if (!await TryAwaitShutdownTaskAsync(screenshotStartupTask, deadlineToken)
            || !await TryAwaitShutdownTaskAsync(screenshotStopTask, deadlineToken))
        {
            return false;
        }

        if (!await TryAwaitShutdownTaskAsync(
                StorageReadiness.EnsureReadyAsync(forceRetry: false),
                deadlineToken))
        {
            return false;
        }

        if (_localSendStartupTask is { } localSendStartupTask)
        {
            if (!await TryAwaitShutdownTaskAsync(localSendStartupTask, deadlineToken))
            {
                return false;
            }
        }

        var localSendReceiver = LocalSendReceiver;
        LocalSendReceiver = null;
        NotifyLocalSendReceiverServiceChanged(null);
        if (localSendReceiver is not null)
        {
            try
            {
                if (!await TryAwaitShutdownTaskAsync(
                        localSendReceiver.StopAsync(deadlineToken),
                        deadlineToken))
                {
                    return false;
                }
            }
            catch
            {
                // Receiver state fails closed when a LocalSend node cannot be stopped.
                // Process shutdown remains the final listener containment boundary.
            }

            try
            {
                if (!await TryAwaitShutdownTaskAsync(
                        localSendReceiver.DisposeAsync().AsTask(),
                        deadlineToken))
                {
                    return false;
                }
            }
            catch
            {
                // Process shutdown is the final containment boundary for a listener
                // that could not complete best-effort disposal.
            }
        }

        var analysisWorkerSupervisor = _analysisWorkerSupervisor;
        var reminderWorkerSupervisor = _reminderWorkerSupervisor;
        if (!await TryAwaitShutdownTaskAsync(
                analysisWorkerSupervisor?.Completion,
                deadlineToken)
            || !await TryAwaitShutdownTaskAsync(
                reminderWorkerSupervisor?.Completion,
                deadlineToken))
        {
            return false;
        }
        if (analysisWorkerSupervisor is not null)
        {
            analysisWorkerSupervisor.StatusChanged -= OnBackgroundWorkerStatusChanged;
        }

        if (reminderWorkerSupervisor is not null)
        {
            reminderWorkerSupervisor.StatusChanged -= OnBackgroundWorkerStatusChanged;
        }

#if !PICFORLATER_UI_TESTING
        if (_toastNotificationsRegistered)
        {
            try
            {
                ToastNotificationManagerCompat.OnActivated -= ToastNotificationManagerCompat_OnActivated;
            }
            catch
            {
                // The process is already closing. SQLite and the durable outbox
                // remain the reminder facts even if notification teardown fails.
            }

            _toastNotificationsRegistered = false;
        }
#endif
        if (deadlineToken.IsCancellationRequested)
        {
            return false;
        }

        lock (ScreenshotCaptureLifecycleLock)
        {
            _screenshotCapture = null;
            _screenshotCaptureWindow = null;
        }

        DisposeResource(ImageImporter);
        ImageImporter = null;
        DisposeResource(Reminders);
        Reminders = null;
        DisposeResource(RemoteApiProfiles);
        RemoteApiProfiles = null;
        DisposeResource(_remoteAnalysisHttpClient);
        _remoteAnalysisHttpClient = null;
        DisposeResource(_modelDownloadHttpClient);
        _modelDownloadHttpClient = null;
        DisposeResource(_componentDownloadHttpClient);
        _componentDownloadHttpClient = null;
        DisposeResource(_updateCheckHttpClient);
        _updateCheckHttpClient = null;
#if PICFORLATER_UI_TESTING
        DisposeResource(_uiTestInferenceRuntime);
        _uiTestInferenceRuntime = null;
#else
        var localInferenceWorker = _localInferenceWorker;
        _localInferenceWorker = null;
        if (localInferenceWorker is not null)
        {
            localInferenceWorker.OcrAvailabilityChanged -=
                LocalInferenceWorker_OcrAvailabilityChanged;
            try
            {
                if (!await TryAwaitShutdownTaskAsync(
                        localInferenceWorker.DisposeAsync().AsTask(),
                        deadlineToken))
                {
                    return false;
                }
            }
            catch
            {
                // LocalInferenceWorkerClient bounds its graceful shutdown and
                // terminates the worker process when needed.
            }
        }

        try
        {
            _localInferenceFailureCircuit?.Stop();
        }
        catch
        {
            // The circuit is diagnostic state; it must not prevent the remaining
            // process resources from reaching their final disposal.
        }
        _localInferenceFailureCircuit = null;
#endif
        DisposeResource(_analysisWakeSignal);
        _analysisWakeSignal = null;
        try
        {
            AnalysisCancellation.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }

        return !deadlineToken.IsCancellationRequested;
    }

    private static async Task<bool> TryAwaitShutdownTaskAsync(
        Task? task,
        CancellationToken deadlineToken)
    {
        if (task is null)
        {
            return true;
        }

        try
        {
            Task completedTask = await Task.WhenAny(
                    task,
                    Task.Delay(Timeout.InfiniteTimeSpan, deadlineToken))
                .ConfigureAwait(false);
            if (!ReferenceEquals(completedTask, task))
            {
                Debug.WriteLine("Application shutdown deadline expired while awaiting a task.");
                return false;
            }

            await task.ConfigureAwait(false);
        }
        catch
        {
            // Independent shutdown steps must continue even when one task fails.
        }

        return true;
    }

    private static void DisposeResource(object? resource)
    {
        try
        {
            (resource as IDisposable)?.Dispose();
        }
        catch
        {
            // Resource disposal is best effort during process shutdown.
        }
    }

    private static void UnregisterInstanceKeySafely()
    {
        try
        {
            Program.UnregisterInstanceKey();
        }
        catch (Exception exception)
        {
            Debug.WriteLine(
                $"Single-instance registration cleanup failed: {exception.GetType().Name}.");
        }
    }

    private static void DisposeSystemTrayIcon()
    {
        if (Window is MainWindow mainWindow)
        {
            mainWindow.Activated -= MainWindow_ActivatedForTray;
        }

        var trayIcon = Interlocked.Exchange(ref _systemTrayIcon, null);
        if (trayIcon is null)
        {
            return;
        }

        try
        {
            trayIcon.Dispose();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"System tray disposal failed: {exception.GetType().Name}.");
        }
    }

    private static void DisposeBusinessFeatures()
    {
        var businessFeatures = Interlocked.Exchange(ref _businessFeatures, null);
        if (businessFeatures is null)
        {
            return;
        }

        try
        {
            businessFeatures.Dispose();
        }
        catch (Exception exception)
        {
            Debug.WriteLine(
                $"Business feature coordinator disposal failed: {exception.GetType().Name}.");
        }
    }

    private static void MainWindow_ActivatedForTray(
        object sender,
        WindowActivatedEventArgs args)
    {
        _ = sender;
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            return;
        }

        _ = TryEnsureSystemTrayIcon();
    }

    private static async Task InitializeLocalSendAsync(
        AppDataPaths paths,
        IImageImportService imageImporter,
        CancellationToken cancellationToken)
    {
        ILocalSendReceiverService? receiver = null;
        try
        {
            var inboxImporter = new LocalSendInboxImportService(paths, imageImporter);
            await inboxImporter.RecoverAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
#if PICFORLATER_UI_TESTING
            receiver = new UiTestLocalSendReceiverService();
#else
            receiver = new LocalSendReceiverService(
                paths,
                new LocalSendNodeFactory(),
                new LocalSendTrustedDeviceStore(paths),
                inboxImporter);
#endif
            cancellationToken.ThrowIfCancellationRequested();
            LocalSendReceiver = receiver;
            NotifyLocalSendReceiverServiceChanged(receiver);
        }
        catch
        {
            if (receiver is not null)
            {
                try
                {
                    await receiver.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                }
            }

            if (ReferenceEquals(LocalSendReceiver, receiver))
            {
                LocalSendReceiver = null;
                NotifyLocalSendReceiverServiceChanged(null);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    private static async Task StartLocalSendReceiverAsync(
        ILocalSendReceiverService receiver,
        CancellationToken cancellationToken)
    {
        try
        {
            // Keep listener startup out of the storage-readiness continuation even
            // when the coordinator can acquire its gate synchronously.
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(receiver, LocalSendReceiver)
                || !LocalSendReceivePreference.IsEnabled
                || _businessFeatures is not { } businessFeatures)
            {
                return;
            }

            await businessFeatures.StartLocalSendIfRequestedAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // The receiver publishes a safe Faulted snapshot. Network startup is
            // independent from storage readiness and must not hide the library.
        }
    }

    private static void StartLocalSendReceiverIfRequested()
    {
        var receiver = LocalSendReceiver;
        if (receiver is null || !LocalSendReceivePreference.IsEnabled)
        {
            return;
        }

        lock (LocalSendStartupLock)
        {
            if (ReferenceEquals(_localSendStartupReceiver, receiver)
                && _localSendStartupTask is not null)
            {
                return;
            }

            _localSendStartupReceiver = receiver;
            _localSendStartupTask = StartLocalSendReceiverAsync(
                receiver,
                AnalysisCancellation.Token);
        }
    }

    public static IReadOnlyList<BackgroundWorkerStatus> GetBackgroundWorkerStatuses()
    {
        var statuses = new List<BackgroundWorkerStatus>(3);
        if (_analysisWorkerSupervisor is { } analysis)
        {
            statuses.Add(analysis.CurrentStatus);
        }

        if (_reminderWorkerSupervisor is { } reminders)
        {
            statuses.Add(reminders.CurrentStatus);
        }

#if !PICFORLATER_UI_TESTING
        if (_localInferenceFailureCircuit is { } localInference)
        {
            statuses.Add(localInference.CurrentStatus);
        }
#endif
        return statuses;
    }

    public static async Task RetryFaultedBackgroundWorkersAsync()
    {
#if !PICFORLATER_UI_TESTING
        if (_localInferenceWorker is not null)
        {
            _ = await _localInferenceWorker.ResetFailureCircuitAsync().ConfigureAwait(false);
        }
#endif
        _ = _analysisWorkerSupervisor?.Retry();
        _ = _reminderWorkerSupervisor?.Retry();
    }

    private static BackgroundWorkerSupervisor CreateBackgroundWorkerSupervisor(
        BackgroundWorkerKind kind,
        Func<CancellationToken, Task> runWorker,
        string unexpectedFailureCode) =>
        new(
            kind,
            runWorker,
            exception => BackgroundWorkerTransientErrorPolicy.IsTransient(exception)
                ? new BackgroundWorkerFailure(
                    kind == BackgroundWorkerKind.Analysis
                        ? "background.analysis.storage-busy"
                        : "background.reminders.storage-busy",
                    IsTransient: true)
                : new BackgroundWorkerFailure(unexpectedFailureCode, IsTransient: false),
            unexpectedFailureCode);

    private static void OnBackgroundWorkerStatusChanged(BackgroundWorkerStatus status)
    {
        var handlers = BackgroundWorkerStatusChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (Action<BackgroundWorkerStatus> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(status);
            }
            catch
            {
                // A UI or diagnostic subscriber must not affect worker supervision.
            }
        }
    }

#if !PICFORLATER_UI_TESTING
    private static void RegisterToastNotifications()
    {
        if (_toastNotificationsRegistered)
        {
            return;
        }

        try
        {
            ToastNotificationManagerCompat.OnActivated += ToastNotificationManagerCompat_OnActivated;
            _ = ToastNotificationManagerCompat.WasCurrentProcessToastActivated();
            _toastNotificationsRegistered = true;
        }
        catch
        {
            ToastNotificationManagerCompat.OnActivated -= ToastNotificationManagerCompat_OnActivated;
            // Notification registration must never prevent access to locally
            // stored images or reminder records. The Reminders page reports
            // the unsupported/disabled state and reconciliation can retry.
        }
    }

    private static void ToastNotificationManagerCompat_OnActivated(
        ToastNotificationActivatedEventArgsCompat args)
    {
        ToastArguments arguments;
        try
        {
            arguments = ToastArguments.Parse(args.Argument);
        }
        catch
        {
            return;
        }

        if (!arguments.TryGetValue("reminderId", out var reminderText)
            || !Guid.TryParse(reminderText, out var reminderId)
            || !arguments.TryGetValue("imageItemId", out var imageItemText)
            || !Guid.TryParse(imageItemText, out var imageItemId))
        {
            return;
        }

        lock (NotificationActivationLock)
        {
            _pendingNotificationActivation = (reminderId, imageItemId);
        }

        _ = ProcessPendingNotificationActivationAsync();
    }
#endif

    private static async Task ProcessPendingNotificationActivationAsync()
    {
        var reminderService = Reminders;
        if (reminderService is null)
        {
            return;
        }

        (Guid ReminderId, Guid ImageItemId)? activation;
        lock (NotificationActivationLock)
        {
            activation = _pendingNotificationActivation;
            _pendingNotificationActivation = null;
        }

        if (activation is null)
        {
            return;
        }

        bool activationAccepted;
        try
        {
            activationAccepted = await reminderService.MarkActivatedAsync(
                    activation.Value.ReminderId,
                    activation.Value.ImageItemId)
                .ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        if (!activationAccepted)
        {
            return;
        }

        RequestLibraryImageNavigation(activation.Value.ImageItemId);
    }

    public static void RequestLibraryImageNavigation(Guid imageItemId)
    {
        PendingNotificationImageItemId = imageItemId;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsMainWindowReady())
            {
                return;
            }

            if (!BringMainWindowToForeground())
            {
                return;
            }
            NotificationImageRequested?.Invoke(imageItemId);
        });
    }

    public static void ClearPendingNotificationNavigation(Guid imageItemId)
    {
        if (PendingNotificationImageItemId == imageItemId)
        {
            PendingNotificationImageItemId = null;
        }
    }

    public static void RequestReminderCreation(Guid imageItemId)
    {
        PendingReminderCreationImageItemId = imageItemId;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsMainWindowReady())
            {
                return;
            }

            if (!BringMainWindowToForeground())
            {
                return;
            }
            ReminderCreationRequested?.Invoke(imageItemId);
        });
    }

    public static void ClearPendingReminderCreation(Guid imageItemId)
    {
        if (PendingReminderCreationImageItemId == imageItemId)
        {
            PendingReminderCreationImageItemId = null;
        }
    }
}
