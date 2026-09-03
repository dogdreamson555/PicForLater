using System.Reflection;
using System.Runtime.InteropServices;
using CommunityToolkit.WinUI.Notifications;
using Microsoft.UI.Xaml;
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
    private const int ShowWindowRestore = 9;
    private static readonly CancellationTokenSource AnalysisCancellation = new();
    private static readonly object ForegroundActivationLock = new();
    private static readonly object NotificationActivationLock = new();
    private static readonly object ScreenshotCaptureLifecycleLock = new();
    private static AnalysisQueueWakeSignal? _analysisWakeSignal;
    private static HttpClient? _modelDownloadHttpClient;
    private static HttpClient? _componentDownloadHttpClient;
    private static HttpClient? _remoteAnalysisHttpClient;
    private static HttpClient? _updateCheckHttpClient;
    private static Task? _localSendStartupTask;
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
    private static BackgroundWorkerSupervisor? _analysisWorkerSupervisor;
    private static BackgroundWorkerSupervisor? _reminderWorkerSupervisor;
    private static bool _isMainWindowReady;
    private static bool _isForegroundActivationPending;
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
        StorageReadiness = new StorageReadinessService(StartStorageInitialization);
        StorageReadiness.ReadinessChanged += StorageReadiness_ReadinessChanged;
        var mainWindow = new MainWindow();
        Window = mainWindow;
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
        if (e.Result.Status != StorageReadinessStatus.Ready)
        {
            return;
        }

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
        AnalysisCancellation.Cancel();
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

    internal static void RequestForegroundActivation()
    {
        lock (ForegroundActivationLock)
        {
            if (!_isMainWindowReady)
            {
                _isForegroundActivationPending = true;
                return;
            }
        }

        _ = DispatcherQueue.TryEnqueue(BringMainWindowToForeground);
    }

    private static void BringMainWindowToForeground()
    {
        nint windowHandle = WindowHandle;
        if (IsIconic(windowHandle))
        {
            _ = ShowWindow(windowHandle, ShowWindowRestore);
        }

        Window.Activate();
        _ = SetForegroundWindow(windowHandle);
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

    private static async void OnWindowClosed(object sender, WindowEventArgs args)
    {
        BeginScreenshotCaptureShutdown();
        lock (ForegroundActivationLock)
        {
            _isMainWindowReady = false;
            _isForegroundActivationPending = false;
        }

        Program.UnregisterInstanceKey();
        AnalysisCancellation.Cancel();
        Task screenshotStartupTask;
        Task screenshotStopTask;
        lock (ScreenshotCaptureLifecycleLock)
        {
            screenshotStartupTask = _screenshotCaptureStartupTask;
            screenshotStopTask = _screenshotCaptureStopTask;
        }

        try
        {
            await screenshotStartupTask.ConfigureAwait(false);
        }
        catch
        {
            // The shutdown generation gate rejects a startup that completed late.
        }

        try
        {
            await screenshotStopTask.ConfigureAwait(false);
        }
        catch
        {
            // StopScreenshotCaptureAsync is best effort and normally absorbs this.
        }

        try
        {
            await StorageReadiness.EnsureReadyAsync(forceRetry: false).ConfigureAwait(false);
        }
        catch
        {
            // Cancellation or a storage failure still completes the initialization
            // task, preventing it from publishing a receiver after shutdown proceeds.
        }

        if (_localSendStartupTask is { } localSendStartupTask)
        {
            try
            {
                await localSendStartupTask.ConfigureAwait(false);
            }
            catch
            {
                // Startup is best-effort and reports failures through the receiver
                // snapshot. Shutdown must still release any partially started node.
            }
        }

        var localSendReceiver = LocalSendReceiver;
        LocalSendReceiver = null;
        if (localSendReceiver is not null)
        {
            try
            {
                await localSendReceiver.StopAsync().ConfigureAwait(false);
            }
            catch
            {
                // Receiver state fails closed when a LocalSend node cannot be stopped.
                // Process shutdown remains the final listener containment boundary.
            }

            try
            {
                await localSendReceiver.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Process shutdown is the final containment boundary for a listener
                // that could not complete best-effort disposal.
            }
        }

        var supervisedTasks = new[]
        {
            _analysisWorkerSupervisor?.Completion,
            _reminderWorkerSupervisor?.Completion,
        }.OfType<Task>().ToArray();
        if (supervisedTasks.Length > 0)
        {
            await Task.WhenAll(supervisedTasks).ConfigureAwait(false);
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
        lock (ScreenshotCaptureLifecycleLock)
        {
            _screenshotCapture = null;
            _screenshotCaptureWindow = null;
        }

        (ImageImporter as IDisposable)?.Dispose();
        (Reminders as IDisposable)?.Dispose();
        (RemoteApiProfiles as IDisposable)?.Dispose();
        _remoteAnalysisHttpClient?.Dispose();
        _modelDownloadHttpClient?.Dispose();
        _componentDownloadHttpClient?.Dispose();
        _updateCheckHttpClient?.Dispose();
#if PICFORLATER_UI_TESTING
        _uiTestInferenceRuntime?.Dispose();
#else
        if (_localInferenceWorker is not null)
        {
            await _localInferenceWorker.DisposeAsync().ConfigureAwait(false);
        }
        _localInferenceFailureCircuit?.Stop();
#endif
        _analysisWakeSignal?.Dispose();
        AnalysisCancellation.Dispose();
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
            if (LocalSendReceivePreference.IsEnabled)
            {
                _localSendStartupTask = StartLocalSendReceiverAsync(
                    receiver,
                    cancellationToken);
            }
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
            // Keep node construction, certificate setup, and listener startup out of
            // the storage-readiness continuation even when their first awaits complete
            // synchronously.
            await Task.Yield();
            await receiver.StartAsync(cancellationToken).ConfigureAwait(false);
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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

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
            Window.Activate();
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
            Window.Activate();
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
