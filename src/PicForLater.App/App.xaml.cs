using System.Runtime.InteropServices;
using CommunityToolkit.WinUI.Notifications;
using Microsoft.UI.Xaml;
using PicForLater.Analysis;
using PicForLater.Analysis.PpOcr;
using PicForLater.App.Services;
using PicForLater.Core.Analysis;
using PicForLater.Core.Images;
using PicForLater.Core.Library;
using PicForLater.Core.Reminders;
using PicForLater.Infrastructure.Analysis;
using PicForLater.Infrastructure.Library;
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
    private static AnalysisQueueWakeSignal? _analysisWakeSignal;
    private static HttpClient? _modelDownloadHttpClient;
    private static HttpClient? _componentDownloadHttpClient;
    private static HttpClient? _remoteAnalysisHttpClient;
#if PICFORLATER_UI_TESTING
    private static UiTestLocalInferenceRuntime? _uiTestInferenceRuntime;
#else
    private static LocalInferenceWorkerClient? _localInferenceWorker;
#endif
    private static Task? _analysisWorkerTask;
    private static Task? _reminderWorkerTask;
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

    public static IReminderService? Reminders { get; private set; }

    public static IModelPackageService? ModelPackages { get; private set; }

    public static IRemoteApiProfileService? RemoteApiProfiles { get; private set; }

    public static IRemoteApiCredentialService? RemoteApiCredentials { get; private set; }

    public static IRemoteApiConnectionTester? RemoteApiConnectionTester { get; private set; }

    public static IAnalysisProfileSnapshotProvider? AnalysisProfiles { get; private set; }

    public static IRecommendedModelService? RecommendedModels { get; private set; }

    public static INvidiaCudaEnvironmentService? NvidiaCudaEnvironment { get; private set; }

    public static IAnalysisReanalysisService? Reanalysis { get; private set; }

    public static LocalInferenceComponentLocator? LocalInferenceComponents { get; private set; }

    public static LocalInferenceComponentInstaller? LocalInferenceComponentInstaller { get; private set; }

    public static LocalInferenceComponentStore? LocalInferenceComponentStore { get; private set; }

    public static IInferenceAccelerationPreferenceService InferenceAcceleration { get; } =
        CreateInferenceAccelerationPreference();

    public static AnalysisQueueWakeSignal? AnalysisUpdates => _analysisWakeSignal;

    public static event Action<Guid>? NotificationImageRequested;

    public static Guid? PendingNotificationImageItemId { get; private set; }

    public static event Action<Guid>? ReminderCreationRequested;

    public static Guid? PendingReminderCreationImageItemId { get; private set; }

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
        Window = new MainWindow();
        Window.Closed += OnWindowClosed;
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
                var remoteApiProfiles = new SqliteRemoteApiProfileService(paths);
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
                _analysisWorkerTask = worker.RunAsync(AnalysisCancellation.Token);
                _reminderWorkerTask = reminderService.RunAsync(AnalysisCancellation.Token);
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
        lock (ForegroundActivationLock)
        {
            _isMainWindowReady = false;
            _isForegroundActivationPending = false;
        }

        Program.UnregisterInstanceKey();
        AnalysisCancellation.Cancel();
        if (_analysisWorkerTask is not null)
        {
            try
            {
                await _analysisWorkerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
                // Observe a worker fault during shutdown without surfacing raw
                // storage or model details through an async event exception.
            }
        }

        if (_reminderWorkerTask is not null)
        {
            try
            {
                await _reminderWorkerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
                // Durable outbox rows remain recoverable on the next launch.
            }
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
        (ImageImporter as IDisposable)?.Dispose();
        (Reminders as IDisposable)?.Dispose();
        (RemoteApiProfiles as IDisposable)?.Dispose();
        _remoteAnalysisHttpClient?.Dispose();
        _modelDownloadHttpClient?.Dispose();
        _componentDownloadHttpClient?.Dispose();
#if PICFORLATER_UI_TESTING
        _uiTestInferenceRuntime?.Dispose();
#else
        if (_localInferenceWorker is not null)
        {
            await _localInferenceWorker.DisposeAsync().ConfigureAwait(false);
        }
#endif
        _analysisWakeSignal?.Dispose();
        AnalysisCancellation.Dispose();
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
