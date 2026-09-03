using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Windows.ApplicationModel.Resources;
using PicForLater.App.Models;

namespace PicForLater.App.ViewModels;

public partial class ScreenshotCaptureSettingsViewModel : ObservableObject
{
    private static readonly ResourceLoader Resources = new();

    [ObservableProperty]
    public partial bool IsEnabledRequested { get; set; }

    [ObservableProperty]
    public partial bool CanToggle { get; set; }

    [ObservableProperty]
    public partial bool CanChangeHotKey { get; set; }

    [ObservableProperty]
    public partial string HotKeyText { get; set; } = ScreenshotHotKey.Default.ToString();

    [ObservableProperty]
    public partial string StatusText { get; set; } =
        Resources.GetString("ScreenshotCaptureStatusPreparing");

    [ObservableProperty]
    public partial bool IsInfoOpen { get; set; }

    [ObservableProperty]
    public partial SettingsStatusKind InfoKind { get; set; } =
        SettingsStatusKind.Informational;

    [ObservableProperty]
    public partial string InfoMessage { get; set; } = string.Empty;

    public void ApplyPreparing()
    {
        CanToggle = false;
        CanChangeHotKey = false;
        StatusText = Resources.GetString("ScreenshotCaptureStatusPreparing");
        IsInfoOpen = false;
        InfoMessage = string.Empty;
    }

    public void ApplySnapshot(ScreenshotCaptureSnapshot snapshot, bool isWorking = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        IsEnabledRequested = snapshot.IsEnabledRequested;
        HotKeyText = snapshot.HotKey.ToString();
        CanToggle = !isWorking;
        CanChangeHotKey = !isWorking;
        StatusText = ResolveStatus(snapshot);

        if (snapshot.RegistrationState == RegistrationState.Conflict)
        {
            ShowInfo(
                SettingsStatusKind.Error,
                Resources.GetString("ScreenshotCaptureHotKeyConflictMessage"));
        }
        else if (snapshot.RegistrationState == RegistrationState.Faulted)
        {
            ShowInfo(
                SettingsStatusKind.Error,
                Resources.GetString("ScreenshotCaptureRegistrationFailedMessage"));
        }
        else if (snapshot.RegistrationState == RegistrationState.Disabled ||
                 snapshot.CaptureState != CaptureState.Idle)
        {
            ClearInfo();
        }
    }

    public void ApplySettingsFailure(ScreenshotSettingsFailureKind failureKind)
    {
        var resourceKey = failureKind switch
        {
            ScreenshotSettingsFailureKind.HotKeyConflict =>
                "ScreenshotCaptureHotKeyConflictMessage",
            ScreenshotSettingsFailureKind.Preference =>
                "ScreenshotCapturePreferenceFailedMessage",
            ScreenshotSettingsFailureKind.NotStarted =>
                "ScreenshotCaptureNotReadyMessage",
            _ => "ScreenshotCaptureRegistrationFailedMessage",
        };
        ShowInfo(SettingsStatusKind.Error, Resources.GetString(resourceKey));
    }

    public void ApplySettingsSuccess() => ClearInfo();

    public void ApplyCaptureResult(ScreenshotCaptureResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        switch (result.Outcome)
        {
            case CaptureOutcome.Imported:
                ClearInfo();
                break;
            case CaptureOutcome.Duplicate:
                ShowInfo(
                    SettingsStatusKind.Informational,
                    Resources.GetString("ScreenshotCaptureDuplicateMessage"));
                break;
            case CaptureOutcome.TimedOut:
                ShowInfo(
                    SettingsStatusKind.Warning,
                    Resources.GetString("ScreenshotCaptureTimedOutMessage"));
                break;
            default:
                ShowInfo(
                    SettingsStatusKind.Error,
                    Resources.GetString(result.FailureKind switch
                    {
                        ScreenshotCaptureFailureKind.InputInjection =>
                            "ScreenshotCaptureLaunchFailedMessage",
                        ScreenshotCaptureFailureKind.ClipboardUnavailable =>
                            "ScreenshotCaptureClipboardUnavailableMessage",
                        ScreenshotCaptureFailureKind.UnsupportedClipboardImage or
                            ScreenshotCaptureFailureKind.InvalidImage =>
                            "ScreenshotCaptureUnsupportedImageMessage",
                        _ => "ScreenshotCaptureImportFailedMessage",
                    }));
                break;
        }
    }

    public static string SettingsFailureMessage(ScreenshotSettingsFailureKind failureKind) =>
        Resources.GetString(failureKind switch
        {
            ScreenshotSettingsFailureKind.HotKeyConflict =>
                "ScreenshotCaptureHotKeyConflictMessage",
            ScreenshotSettingsFailureKind.Preference =>
                "ScreenshotCapturePreferenceFailedMessage",
            ScreenshotSettingsFailureKind.NotStarted =>
                "ScreenshotCaptureNotReadyMessage",
            _ => "ScreenshotCaptureRegistrationFailedMessage",
        });

    private static string ResolveStatus(ScreenshotCaptureSnapshot snapshot)
    {
        if (snapshot.RegistrationState == RegistrationState.Disabled)
        {
            return Resources.GetString("ScreenshotCaptureStatusDisabled");
        }

        if (snapshot.RegistrationState is RegistrationState.Conflict or RegistrationState.Faulted)
        {
            return Resources.GetString("ScreenshotCaptureStatusUnavailable");
        }

        return snapshot.CaptureState switch
        {
            CaptureState.Capturing => Resources.GetString("ScreenshotCaptureStatusCapturing"),
            CaptureState.Importing => Resources.GetString("ScreenshotCaptureStatusImporting"),
            _ => Resources.GetString("ScreenshotCaptureStatusReady"),
        };
    }

    private void ShowInfo(SettingsStatusKind kind, string message)
    {
        InfoKind = kind;
        InfoMessage = message;
        IsInfoOpen = true;
    }

    private void ClearInfo()
    {
        InfoMessage = string.Empty;
        IsInfoOpen = false;
    }
}
