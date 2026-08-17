using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using WinRT;

namespace PicForLater.App;

public static class Program
{
    internal const string UninstallNotificationsArgument = "--uninstall-notifications";
#if PICFORLATER_UI_TESTING
    private const string InstanceKey = "PicForLater.App.SingleInstance.UiTest.4E3B8D39-7F1F-4652-AA08-816B3DA74A2E";
#else
    private const string InstanceKey = "PicForLater.App.SingleInstance.4E3B8D39-7F1F-4652-AA08-816B3DA74A2E";
#endif
    private const uint CoWaitDefault = 0;
    private const uint Infinite = uint.MaxValue;
    private const int ShowWindowRestore = 9;
    private static AppInstance? _currentInstance;

    [STAThread]
    public static int Main(string[] args)
    {
        ComWrappersSupport.InitializeComWrappers();

        bool isNotificationUninstall = args.Contains(
            UninstallNotificationsArgument,
            StringComparer.Ordinal);
        if (!isNotificationUninstall && ShouldRedirectActivation())
        {
            return 0;
        }

        Application.Start(static initializationCallbackParams =>
        {
            _ = initializationCallbackParams;
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });

        return 0;
    }

    internal static void UnregisterInstanceKey()
    {
        var instance = Interlocked.Exchange(ref _currentInstance, null);
        if (instance is null)
        {
            return;
        }

        instance.Activated -= CurrentInstance_Activated;
        instance.UnregisterKey();
    }

    private static bool ShouldRedirectActivation()
    {
        var activationArguments = AppInstance.GetCurrent().GetActivatedEventArgs();
        var instance = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (instance.IsCurrent)
        {
            _currentInstance = instance;
            instance.Activated += CurrentInstance_Activated;
            return false;
        }

        RedirectActivationTo(instance, activationArguments);
        TryActivatePrimaryWindow(instance);
        return true;
    }

    private static void CurrentInstance_Activated(object? sender, AppActivationArguments args)
    {
        App.RequestForegroundActivation();
    }

    private static void RedirectActivationTo(
        AppInstance instance,
        AppActivationArguments activationArguments)
    {
        nint redirectCompleted = CreateEvent(
            lpEventAttributes: 0,
            bManualReset: true,
            bInitialState: false,
            lpName: null);
        if (redirectCompleted == 0)
        {
            Debug.WriteLine("Single-instance activation redirection could not create its wait event.");
            return;
        }

        Exception? redirectException = null;
        _ = Task.Run(async () =>
        {
            try
            {
                await instance.RedirectActivationToAsync(activationArguments);
            }
            catch (Exception exception)
            {
                redirectException = exception;
            }
            finally
            {
                _ = SetEvent(redirectCompleted);
            }
        });

        try
        {
            nint[] handles = [redirectCompleted];
            uint waitResult = CoWaitForMultipleObjects(
                CoWaitDefault,
                Infinite,
                (ulong)handles.Length,
                handles,
                out _);
            if (waitResult != 0)
            {
                Debug.WriteLine($"Single-instance activation redirection wait failed: 0x{waitResult:X8}.");
            }
            else if (redirectException is not null)
            {
                Debug.WriteLine($"Single-instance activation redirection failed: {redirectException.GetType().Name}.");
            }
        }
        finally
        {
            _ = CloseHandle(redirectCompleted);
        }
    }

    private static void TryActivatePrimaryWindow(AppInstance instance)
    {
        try
        {
            using var process = Process.GetProcessById(checked((int)instance.ProcessId));
            nint windowHandle = process.MainWindowHandle;
            if (windowHandle == 0)
            {
                return;
            }

            if (IsIconic(windowHandle))
            {
                _ = ShowWindow(windowHandle, ShowWindowRestore);
            }

            _ = SetForegroundWindow(windowHandle);
        }
        catch (ArgumentException)
        {
            // The primary process can be closing while a new activation arrives.
        }
        catch (InvalidOperationException)
        {
            // The primary process no longer exposes a usable window handle.
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateEvent(
        nint lpEventAttributes,
        bool bManualReset,
        bool bInitialState,
        string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetEvent(nint hEvent);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint hObject);

    [DllImport("ole32.dll")]
    private static extern uint CoWaitForMultipleObjects(
        uint dwFlags,
        uint dwMilliseconds,
        ulong nHandles,
        nint[] pHandles,
        out uint dwIndex);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);
}
