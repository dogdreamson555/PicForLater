using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Windows.ApplicationModel.Resources;
using PicForLater.App.Models;
using PicForLater.App.Services;
using PicForLater.Core.Analysis;
using PicForLater.Infrastructure.LocalSend;

namespace PicForLater.App.ViewModels;

public partial class SettingsHomePageViewModel : ObservableObject
{
    private static readonly ResourceLoader Resources = new();
    private readonly IThemePreferenceService _themePreferenceService;
    private readonly IBackdropPreferenceService _backdropPreferenceService;
    private readonly ILanguagePreferenceService _languagePreferenceService;
    private readonly IStorageReadinessService _storageReadinessService;
    private readonly Func<IRemoteApiProfileService?> _profileServiceAccessor;
    private readonly Func<IRemoteApiCredentialService?> _credentialServiceAccessor;
    private readonly Func<ILocalSendReceiverService?> _localSendReceiverAccessor;
    private readonly ILocalSendReceivePreferenceService _localSendReceivePreference;
    private readonly IUpdateCheckService _updateCheckService;
    private int _updateCheckGeneration;

    public SettingsHomePageViewModel(
        IThemePreferenceService themePreferenceService,
        IBackdropPreferenceService backdropPreferenceService,
        ILanguagePreferenceService languagePreferenceService,
        IStorageReadinessService storageReadinessService,
        Func<IRemoteApiProfileService?> profileServiceAccessor,
        Func<IRemoteApiCredentialService?> credentialServiceAccessor,
        ILocalSendReceivePreferenceService localSendReceivePreference,
        Func<ILocalSendReceiverService?> localSendReceiverAccessor,
        IUpdateCheckService updateCheckService,
        AppReleaseVersion currentVersion)
    {
        _themePreferenceService = themePreferenceService
            ?? throw new ArgumentNullException(nameof(themePreferenceService));
        _backdropPreferenceService = backdropPreferenceService
            ?? throw new ArgumentNullException(nameof(backdropPreferenceService));
        _languagePreferenceService = languagePreferenceService
            ?? throw new ArgumentNullException(nameof(languagePreferenceService));
        _storageReadinessService = storageReadinessService
            ?? throw new ArgumentNullException(nameof(storageReadinessService));
        _profileServiceAccessor = profileServiceAccessor
            ?? throw new ArgumentNullException(nameof(profileServiceAccessor));
        _credentialServiceAccessor = credentialServiceAccessor
            ?? throw new ArgumentNullException(nameof(credentialServiceAccessor));
        _localSendReceivePreference = localSendReceivePreference
            ?? throw new ArgumentNullException(nameof(localSendReceivePreference));
        _localSendReceiverAccessor = localSendReceiverAccessor
            ?? throw new ArgumentNullException(nameof(localSendReceiverAccessor));
        _updateCheckService = updateCheckService
            ?? throw new ArgumentNullException(nameof(updateCheckService));
        SelectedThemeIndex = (int)_themePreferenceService.CurrentPreference;
        SelectedBackdropIndex = (int)_backdropPreferenceService.CurrentPreference;
        SelectedInterfaceLanguageIndex = (int)_languagePreferenceService.CurrentPreference;
        RefreshInterfaceLanguageStatus();
        IsLocalSendEnabled = _localSendReceivePreference.IsEnabled;
        IsKeepInSystemTrayEnabled = App.CloseBehaviorPreference.IsKeepInSystemTrayEnabled;
        CurrentAppVersion = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            Resources.GetString("CurrentAppVersionFormat"),
            currentVersion);
    }

    [ObservableProperty]
    public partial int SelectedThemeIndex { get; set; }

    [ObservableProperty]
    public partial int SelectedBackdropIndex { get; set; }

    [ObservableProperty]
    public partial SettingsStatusKind AppearanceStatusKind { get; set; } =
        SettingsStatusKind.Informational;

    [ObservableProperty]
    public partial string AppearanceStatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsAppearanceStatusOpen { get; set; }

    [ObservableProperty]
    public partial int SelectedInterfaceLanguageIndex { get; set; }

    [ObservableProperty]
    public partial SettingsStatusKind InterfaceLanguageStatusKind { get; set; } =
        SettingsStatusKind.Informational;

    [ObservableProperty]
    public partial string InterfaceLanguageStatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsInterfaceLanguageStatusOpen { get; set; }

    [ObservableProperty]
    public partial string? PendingInterfaceLanguageTag { get; private set; }

    [ObservableProperty]
    public partial string CurrentExecutionTarget { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CurrentExecutionDetail { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LocalAnalysisStatus { get; set; } =
        Resources.GetString("LocalAnalysisUnavailableStatus");

    [ObservableProperty]
    public partial string ApiConfigurationStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int SelectedAnalysisSourceIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSelectLocalAnalysis))]
    public partial bool HasSelectableLocalAnalysis { get; set; }

    [ObservableProperty]
    public partial bool IsLocalSendEnabled { get; set; }

    [ObservableProperty]
    public partial bool CanToggleLocalSend { get; set; }

    [ObservableProperty]
    public partial bool CanPairLocalSend { get; set; }

    [ObservableProperty]
    public partial bool CanManageLocalSendDevices { get; set; }

    [ObservableProperty]
    public partial bool IsLocalSendPairing { get; set; }

    [ObservableProperty]
    public partial bool HasLocalSendTrustedDevices { get; set; }

    [ObservableProperty]
    public partial bool HasNoLocalSendTrustedDevices { get; set; } = true;

    [ObservableProperty]
    public partial bool IsLocalSendInfoOpen { get; set; }

    [ObservableProperty]
    public partial SettingsStatusKind LocalSendInfoKind { get; set; } =
        SettingsStatusKind.Informational;

    [ObservableProperty]
    public partial string LocalSendInfoMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LocalSendReceiverName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LocalSendStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LocalSendPairingPin { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LocalSendPairingRemaining { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLocalSendWorking { get; set; }

    partial void OnIsLocalSendWorkingChanged(bool value) =>
        UpdateLocalSendCapabilities(_localSendReceiverAccessor()?.Snapshot);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeAnalysisSource))]
    [NotifyPropertyChangedFor(nameof(CanSelectLocalAnalysis))]
    [NotifyPropertyChangedFor(nameof(CanSelectApiAnalysis))]
    public partial bool IsWorking { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeAnalysisSource))]
    [NotifyPropertyChangedFor(nameof(CanSelectLocalAnalysis))]
    [NotifyPropertyChangedFor(nameof(CanSelectApiAnalysis))]
    public partial bool IsInitialized { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSelectApiAnalysis))]
    public partial bool HasSelectableApiAnalysis { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeAnalysisSource))]
    [NotifyPropertyChangedFor(nameof(CanSelectLocalAnalysis))]
    [NotifyPropertyChangedFor(nameof(CanSelectApiAnalysis))]
    public partial bool IsSharedAnalysisOperationInProgress { get; set; }

    public bool CanChangeAnalysisSource =>
        IsInitialized && !IsWorking && !IsSharedAnalysisOperationInProgress;

    public bool CanSelectLocalAnalysis =>
        CanChangeAnalysisSource && HasSelectableLocalAnalysis;

    public bool CanSelectApiAnalysis => CanChangeAnalysisSource && HasSelectableApiAnalysis;

    [ObservableProperty]
    public partial bool IsKeepInSystemTrayEnabled { get; set; }

    public string CurrentAppVersion { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheckForUpdates))]
    public partial bool IsCheckingForUpdates { get; set; }

    public bool CanCheckForUpdates => !IsCheckingForUpdates;

    [ObservableProperty]
    public partial SettingsStatusKind UpdateStatusKind { get; set; } =
        SettingsStatusKind.Informational;

    [ObservableProperty]
    public partial string UpdateStatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsUpdateStatusOpen { get; set; }

    [ObservableProperty]
    public partial bool IsReleasePageAvailable { get; set; }

    [ObservableProperty]
    public partial Uri? ReleasePageUri { get; set; }

    public ObservableCollection<LocalSendTrustedDeviceItem> LocalSendTrustedDevices { get; } = [];

    public void SetKeepInSystemTrayEnabled(bool isEnabled)
    {
        if (App.IsShuttingDown)
        {
            IsKeepInSystemTrayEnabled = App.CloseBehaviorPreference.IsKeepInSystemTrayEnabled;
            return;
        }

        try
        {
            App.CloseBehaviorPreference.SetKeepInSystemTrayEnabled(isEnabled);
            IsKeepInSystemTrayEnabled = isEnabled;
        }
        catch
        {
            IsKeepInSystemTrayEnabled =
                App.CloseBehaviorPreference.IsKeepInSystemTrayEnabled;
        }
    }

    partial void OnSelectedThemeIndexChanged(int value)
    {
        if (Enum.IsDefined(typeof(AppThemePreference), value))
        {
            _themePreferenceService.SetPreference((AppThemePreference)value);
        }
    }

    partial void OnSelectedBackdropIndexChanged(int value)
    {
        if (!Enum.IsDefined(typeof(AppBackdropPreference), value))
        {
            SelectedBackdropIndex = (int)_backdropPreferenceService.CurrentPreference;
            return;
        }

        try
        {
            _backdropPreferenceService.SetPreference((AppBackdropPreference)value);
            AppearanceStatusMessage = string.Empty;
            IsAppearanceStatusOpen = false;
        }
        catch (Exception exception)
        {
            Trace.WriteLine(
                $"Backdrop preference persistence failed: {exception.GetType().Name}.");
            // TwoWay binding updates the index first, so restore the persisted value.
            SelectedBackdropIndex = (int)_backdropPreferenceService.CurrentPreference;
            AppearanceStatusKind = SettingsStatusKind.Error;
            AppearanceStatusMessage = Resources.GetString(
                "AppearancePreferenceSaveFailedStatus");
            IsAppearanceStatusOpen = true;
        }
    }

    public void SetInterfaceLanguagePreference(int selectedIndex)
    {
        if (!Enum.IsDefined(typeof(AppLanguagePreference), selectedIndex))
        {
            SelectedInterfaceLanguageIndex = (int)_languagePreferenceService.CurrentPreference;
            return;
        }

        var selectedPreference = (AppLanguagePreference)selectedIndex;
        if (selectedPreference == _languagePreferenceService.CurrentPreference)
        {
            RefreshInterfaceLanguageStatus();
            return;
        }

        try
        {
            _languagePreferenceService.SetPreference(selectedPreference);
            RefreshInterfaceLanguageStatus();
        }
        catch
        {
            SelectedInterfaceLanguageIndex =
                (int)_languagePreferenceService.CurrentPreference;
            RefreshInterfaceLanguageStatus();
            InterfaceLanguageStatusKind = SettingsStatusKind.Error;
            InterfaceLanguageStatusMessage = PendingInterfaceLanguageTag is { } pendingLanguageTag
                ? string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    Resources.GetString("InterfaceLanguageSaveFailedWithPendingStatusFormat"),
                    GetLanguageDisplayName(pendingLanguageTag))
                : Resources.GetString("InterfaceLanguageSaveFailedStatus");
            IsInterfaceLanguageStatusOpen = true;
        }
    }

    private void RefreshInterfaceLanguageStatus()
    {
        var effectiveLanguageTag = _languagePreferenceService.ResolveEffectiveLanguageTag(
            _languagePreferenceService.CurrentPreference);
        if (string.Equals(
                effectiveLanguageTag,
                _languagePreferenceService.CurrentStartupLanguageTag,
                StringComparison.Ordinal))
        {
            PendingInterfaceLanguageTag = null;
            InterfaceLanguageStatusMessage = string.Empty;
            IsInterfaceLanguageStatusOpen = false;
            return;
        }

        PendingInterfaceLanguageTag = effectiveLanguageTag;
        InterfaceLanguageStatusKind = SettingsStatusKind.Success;
        InterfaceLanguageStatusMessage = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            Resources.GetString("InterfaceLanguageSavedStatusFormat"),
            GetLanguageDisplayName(effectiveLanguageTag));
        IsInterfaceLanguageStatusOpen = true;
    }

    private static string GetLanguageDisplayName(string languageTag) => languageTag switch
    {
        "zh-CN" => Resources.GetString("InterfaceLanguageNameSimplifiedChinese"),
        "zh-TW" => Resources.GetString("InterfaceLanguageNameTraditionalChineseTaiwan"),
        _ => Resources.GetString("InterfaceLanguageNameEnglish"),
    };

    public async Task CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        if (IsCheckingForUpdates)
        {
            return;
        }

        var checkGeneration = ++_updateCheckGeneration;
        IsCheckingForUpdates = true;
        UpdateStatusKind = SettingsStatusKind.Informational;
        UpdateStatusMessage = Resources.GetString("UpdateCheckingStatus");
        IsUpdateStatusOpen = true;
        IsReleasePageAvailable = false;
        ReleasePageUri = null;

        try
        {
            var result = await _updateCheckService
                .CheckForUpdatesAsync(cancellationToken)
                .ConfigureAwait(true);
            if (!IsCurrentUpdateCheck(checkGeneration, cancellationToken))
            {
                return;
            }

            ApplyUpdateCheckResult(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (checkGeneration == _updateCheckGeneration)
            {
                UpdateStatusMessage = string.Empty;
                IsUpdateStatusOpen = false;
            }
        }
        catch
        {
            if (checkGeneration == _updateCheckGeneration)
            {
                ShowUpdateCheckUnavailable();
            }
        }
        finally
        {
            if (checkGeneration == _updateCheckGeneration)
            {
                IsCheckingForUpdates = false;
            }
        }
    }

    public void CancelUpdateCheck()
    {
        _updateCheckGeneration++;
        if (!IsCheckingForUpdates)
        {
            return;
        }

        IsCheckingForUpdates = false;
        UpdateStatusMessage = string.Empty;
        IsUpdateStatusOpen = false;
        IsReleasePageAvailable = false;
        ReleasePageUri = null;
    }

    public void ShowReleasePageOpenFailure()
    {
        UpdateStatusKind = SettingsStatusKind.Error;
        UpdateStatusMessage = Resources.GetString("UpdateReleasePageOpenFailedStatus");
        IsUpdateStatusOpen = true;
    }

    private bool IsCurrentUpdateCheck(
        int checkGeneration,
        CancellationToken cancellationToken) =>
        checkGeneration == _updateCheckGeneration
        && !cancellationToken.IsCancellationRequested;

    private void ApplyUpdateCheckResult(UpdateCheckResult result)
    {
        ReleasePageUri = result.Outcome == UpdateCheckOutcome.UpdateAvailable
            ? result.ReleasePageUri
            : null;
        IsReleasePageAvailable = ReleasePageUri is not null;
        UpdateStatusKind = result.Outcome switch
        {
            UpdateCheckOutcome.UpToDate => SettingsStatusKind.Success,
            UpdateCheckOutcome.UpdateAvailable => SettingsStatusKind.Informational,
            UpdateCheckOutcome.LocalAhead => SettingsStatusKind.Warning,
            _ => SettingsStatusKind.Error,
        };
        UpdateStatusMessage = result.Outcome switch
        {
            UpdateCheckOutcome.UpToDate => string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                Resources.GetString("UpdateUpToDateStatusFormat"),
                result.CurrentVersion),
            UpdateCheckOutcome.UpdateAvailable when result.LatestVersion is { } latestVersion =>
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    Resources.GetString("UpdateAvailableStatusFormat"),
                    latestVersion,
                    result.CurrentVersion),
            UpdateCheckOutcome.LocalAhead when result.LatestVersion is { } latestVersion =>
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    Resources.GetString("UpdateLocalAheadStatusFormat"),
                    result.CurrentVersion,
                    latestVersion),
            _ => Resources.GetString("UpdateUnavailableStatus"),
        };
        IsUpdateStatusOpen = true;
    }

    private void ShowUpdateCheckUnavailable()
    {
        ReleasePageUri = null;
        IsReleasePageAvailable = false;
        UpdateStatusKind = SettingsStatusKind.Error;
        UpdateStatusMessage = Resources.GetString("UpdateUnavailableStatus");
        IsUpdateStatusOpen = true;
    }

    public async Task InitializeAsync()
    {
        IsInitialized = false;

        StorageReadinessResult readiness;
        try
        {
            readiness = await _storageReadinessService.EnsureReadyAsync(forceRetry: false)
                .ConfigureAwait(true);
        }
        catch
        {
            App.BusinessFeatures?.SetStorageReady(false);
            ApplyUnavailableState();
            return;
        }

        var storageReady = readiness.Status == StorageReadinessStatus.Ready;
        App.BusinessFeatures?.SetStorageReady(storageReady);
        if (!storageReady)
        {
            ApplyLocalSendUnavailableState();
            ApplyUnavailableState();
            return;
        }

        // Local analysis is independent of the LocalSend and API settings
        // below. A failure in either of those areas must not erase this fact.
        HasSelectableLocalAnalysis = App.LocalAnalysisAvailable;
        LocalAnalysisStatus = Resources.GetString(
            HasSelectableLocalAnalysis
                ? "LocalAnalysisReadyStatus/Text"
                : "LocalAnalysisUnavailableStatus");

        try
        {
            await InitializeLocalSendAsync(storageReady: true).ConfigureAwait(true);
        }
        catch
        {
            ApplyLocalSendUnavailableState();
        }

        try
        {
            var profiles = _profileServiceAccessor();
            if (profiles is null)
            {
                ApplyAnalysisBackendUnavailableState();
                return;
            }

            var state = await profiles.GetExecutionStateAsync().ConfigureAwait(true);
            var eligibleProfiles = await GetEligibleProfilesAsync(
                    profiles,
                    _credentialServiceAccessor(),
                    HasSelectableLocalAnalysis)
                .ConfigureAwait(true);
            ApiConfigurationStatus = Resources.GetString(
                eligibleProfiles.Count > 0
                    ? "ApiConfigurationReadyStatus"
                    : "ApiConfigurationMissingStatus");
            HasSelectableApiAnalysis =
                AnalysisEligibility.IsEligibleCurrentSelection(state, eligibleProfiles)
                || AnalysisEligibility.ResolveEligibleSelection(state, eligibleProfiles)
                    is not null;
            ApplyExecutionState(state);
            IsInitialized = true;
        }
        catch
        {
            // Keep the local capability already established above. Only the
            // API/execution portion is unknown when this branch is reached.
            ApplyAnalysisBackendUnavailableState();
        }
    }

    public void ApplyLocalSendSnapshot(LocalSendReceiverSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        IsLocalSendEnabled = _localSendReceivePreference.IsEnabled;
        LocalSendReceiverName = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            Resources.GetString("LocalSendReceiverNameFormat"),
            LocalSendReceiverService.ReceiverAlias);
        LocalSendStatus = Resources.GetString(snapshot.DiscoveryLimited
            && snapshot.Status == LocalSendReceiverStatus.Listening
                ? "LocalSendStatusDiscoveryLimited"
                : $"LocalSendStatus{snapshot.Status}");
        IsLocalSendPairing = snapshot.Status == LocalSendReceiverStatus.Pairing
                             && !string.IsNullOrWhiteSpace(snapshot.PairingPin);
        LocalSendPairingPin = IsLocalSendPairing ? snapshot.PairingPin! : string.Empty;
        UpdateLocalSendPairingRemaining(snapshot, DateTimeOffset.UtcNow);
        IsLocalSendInfoOpen = snapshot.Status == LocalSendReceiverStatus.Faulted
                              || snapshot.DiscoveryLimited;
        LocalSendInfoKind = snapshot.Status == LocalSendReceiverStatus.Faulted
            ? SettingsStatusKind.Error
            : SettingsStatusKind.Warning;
        LocalSendInfoMessage = snapshot.Status == LocalSendReceiverStatus.Faulted
            ? Resources.GetString("LocalSendReceiverFailedMessage")
            : snapshot.DiscoveryLimited
                ? Resources.GetString("LocalSendDiscoveryLimitedMessage")
                : string.Empty;
        UpdateLocalSendCapabilities(snapshot);
    }

    public void UpdateLocalSendPairingRemaining(
        LocalSendReceiverSnapshot snapshot,
        DateTimeOffset nowUtc)
    {
        if (snapshot.Status != LocalSendReceiverStatus.Pairing
            || snapshot.PairingExpiresAtUtc is not { } expiresAtUtc)
        {
            LocalSendPairingRemaining = string.Empty;
            return;
        }

        var remaining = expiresAtUtc - nowUtc.ToUniversalTime();
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        var display = $"{(int)remaining.TotalMinutes:D2}:{remaining.Seconds:D2}";
        LocalSendPairingRemaining = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            Resources.GetString("LocalSendPairingRemainingFormat"),
            display);
    }

    public async Task SetLocalSendEnabledAsync(bool isEnabled)
    {
        if (IsLocalSendWorking || !CanToggleLocalSend)
        {
            return;
        }

        IsLocalSendWorking = true;
        try
        {
            if (App.BusinessFeatures is { } businessFeatures)
            {
                await businessFeatures.SetLocalSendEnabledAsync(isEnabled)
                    .ConfigureAwait(true);
                var sharedReceiver = _localSendReceiverAccessor();
                if (sharedReceiver is null)
                {
                    ApplyLocalSendUnavailableState();
                }
                else
                {
                    ApplyLocalSendSnapshot(sharedReceiver.Snapshot);
                }

                return;
            }

            _localSendReceivePreference.SetEnabled(isEnabled);
            IsLocalSendEnabled = isEnabled;
            var receiver = _localSendReceiverAccessor();
            if (receiver is null)
            {
                ApplyLocalSendUnavailableState();
                return;
            }

            if (isEnabled)
            {
                await receiver.StartAsync().ConfigureAwait(true);
            }
            else
            {
                await receiver.StopAsync().ConfigureAwait(true);
            }

            ApplyLocalSendSnapshot(receiver.Snapshot);
            await RefreshLocalSendTrustedDevicesAsync().ConfigureAwait(true);
        }
        catch
        {
            IsLocalSendEnabled = _localSendReceivePreference.IsEnabled;
            var receiver = _localSendReceiverAccessor();
            if (receiver is not null)
            {
                ApplyLocalSendSnapshot(receiver.Snapshot);
            }
            else
            {
                ApplyLocalSendUnavailableState();
            }
        }
        finally
        {
            IsLocalSendWorking = false;
            UpdateLocalSendCapabilities(_localSendReceiverAccessor()?.Snapshot);
        }
    }

    public async Task BeginLocalSendPairingAsync()
    {
        var receiver = _localSendReceiverAccessor();
        if (receiver is null || !CanPairLocalSend || IsLocalSendWorking)
        {
            return;
        }

        IsLocalSendWorking = true;
        try
        {
            await receiver.BeginPairingAsync().ConfigureAwait(true);
            ApplyLocalSendSnapshot(receiver.Snapshot);
        }
        catch
        {
            ApplyLocalSendSnapshot(receiver.Snapshot);
        }
        finally
        {
            IsLocalSendWorking = false;
            UpdateLocalSendCapabilities(receiver.Snapshot);
        }
    }

    public async Task CancelLocalSendPairingAsync()
    {
        var receiver = _localSendReceiverAccessor();
        if (receiver is null || !IsLocalSendPairing || IsLocalSendWorking)
        {
            return;
        }

        IsLocalSendWorking = true;
        try
        {
            await receiver.CancelPairingAsync().ConfigureAwait(true);
            ApplyLocalSendSnapshot(receiver.Snapshot);
        }
        catch
        {
            ApplyLocalSendSnapshot(receiver.Snapshot);
        }
        finally
        {
            IsLocalSendWorking = false;
            UpdateLocalSendCapabilities(receiver.Snapshot);
        }
    }

    public async Task<bool> RemoveLocalSendTrustedDeviceAsync(string deviceId)
    {
        var receiver = _localSendReceiverAccessor();
        if (receiver is null || !CanManageLocalSendDevices || IsLocalSendWorking)
        {
            return false;
        }

        IsLocalSendWorking = true;
        try
        {
            var removed = await receiver.RemoveTrustedDeviceAsync(deviceId).ConfigureAwait(true);
            await RefreshLocalSendTrustedDevicesAsync(receiver).ConfigureAwait(true);
            return removed;
        }
        catch
        {
            return false;
        }
        finally
        {
            IsLocalSendWorking = false;
            UpdateLocalSendCapabilities(receiver.Snapshot);
        }
    }

    public async Task RefreshLocalSendTrustedDevicesAsync(
        ILocalSendReceiverService? expectedReceiver = null)
    {
        var receiver = _localSendReceiverAccessor();
        if (receiver is null)
        {
            if (expectedReceiver is not null)
            {
                return;
            }

            LocalSendTrustedDevices.Clear();
            UpdateLocalSendTrustedDeviceVisibility();
            return;
        }

        try
        {
            var devices = await receiver.GetTrustedDevicesAsync().ConfigureAwait(true);
            if (expectedReceiver is not null
                && (!ReferenceEquals(receiver, expectedReceiver)
                    || !ReferenceEquals(receiver, _localSendReceiverAccessor())))
            {
                return;
            }

            LocalSendTrustedDevices.Clear();
            foreach (var device in devices.OrderBy(static device => device.DisplayName))
            {
                LocalSendTrustedDevices.Add(new(
                    device.DeviceId,
                    device.DisplayName,
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        Resources.GetString("LocalSendTrustedDevicePairedFormat"),
                        device.FirstPairedAtUtc.ToLocalTime()),
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        Resources.GetString("LocalSendRemoveDeviceAutomationNameFormat"),
                        device.DisplayName)));
            }
        }
        catch
        {
            LocalSendTrustedDevices.Clear();
        }

        UpdateLocalSendTrustedDeviceVisibility();
    }

    private async Task InitializeLocalSendAsync(bool storageReady)
    {
        IsLocalSendEnabled = _localSendReceivePreference.IsEnabled;
        var receiver = storageReady ? _localSendReceiverAccessor() : null;
        if (receiver is null)
        {
            ApplyLocalSendUnavailableState();
            await RefreshLocalSendTrustedDevicesAsync().ConfigureAwait(true);
            return;
        }

        ApplyLocalSendSnapshot(receiver.Snapshot);
        await RefreshLocalSendTrustedDevicesAsync(receiver).ConfigureAwait(true);
    }

    internal void ApplyLocalSendUnavailableState()
    {
        IsLocalSendEnabled = _localSendReceivePreference.IsEnabled;
        LocalSendTrustedDevices.Clear();
        UpdateLocalSendTrustedDeviceVisibility();
        LocalSendReceiverName = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            Resources.GetString("LocalSendReceiverNameFormat"),
            LocalSendReceiverService.ReceiverAlias);
        LocalSendStatus = Resources.GetString("LocalSendStatusFaulted");
        IsLocalSendPairing = false;
        LocalSendPairingPin = string.Empty;
        LocalSendPairingRemaining = string.Empty;
        IsLocalSendInfoOpen = true;
        LocalSendInfoKind = SettingsStatusKind.Error;
        LocalSendInfoMessage = Resources.GetString("LocalSendReceiverUnavailableMessage");
        CanToggleLocalSend = false;
        CanPairLocalSend = false;
        CanManageLocalSendDevices = false;
    }

    private void UpdateLocalSendCapabilities(LocalSendReceiverSnapshot? snapshot)
    {
        CanToggleLocalSend = snapshot is not null
                             && !IsLocalSendWorking
                             && snapshot.Status is not
                                 LocalSendReceiverStatus.Starting and not
                                 LocalSendReceiverStatus.Stopping;
        CanPairLocalSend = !IsLocalSendWorking
                           && snapshot?.CanPair == true;
        CanManageLocalSendDevices = !IsLocalSendWorking
                                    && snapshot?.CanManageTrustedDevices == true;
    }

    private void UpdateLocalSendTrustedDeviceVisibility()
    {
        HasLocalSendTrustedDevices = LocalSendTrustedDevices.Count > 0;
        HasNoLocalSendTrustedDevices = !HasLocalSendTrustedDevices;
    }

    public async Task<AnalysisSourceSelectionOutcome> SelectAnalysisSourceAsync(int selectedIndex)
    {
        if (!IsInitialized || IsWorking)
        {
            return AnalysisSourceSelectionOutcome.Applied;
        }

        if (App.BusinessFeatures is { } businessFeatures)
        {
            IsWorking = true;
            try
            {
                var result = await businessFeatures.SelectAnalysisBackendAsync(
                        selectedIndex == 0
                            ? AnalysisExecutionBackend.Local
                            : AnalysisExecutionBackend.RemoteApi)
                    .ConfigureAwait(true);
                HasSelectableLocalAnalysis = result.LocalAnalysisAvailable;
                LocalAnalysisStatus = Resources.GetString(
                    result.LocalAnalysisAvailable
                        ? "LocalAnalysisReadyStatus/Text"
                        : "LocalAnalysisUnavailableStatus");
                HasSelectableApiAnalysis = result.RemoteAnalysisSelectable;
                ApiConfigurationStatus = Resources.GetString(
                    result.RemoteAnalysisSelectable
                        ? "ApiConfigurationReadyStatus"
                        : "ApiConfigurationMissingStatus");
                if (result.ExecutionState is { } executionState)
                {
                    ApplyExecutionState(executionState);
                }

                return result.Applied
                    ? AnalysisSourceSelectionOutcome.Applied
                    : AnalysisSourceSelectionOutcome.RequiresApiConfiguration;
            }
            finally
            {
                IsWorking = false;
            }
        }

        var profiles = _profileServiceAccessor();
        var credentials = _credentialServiceAccessor();
        if (profiles is null)
        {
            return AnalysisSourceSelectionOutcome.RequiresApiConfiguration;
        }

        IsWorking = true;
        try
        {
            if (selectedIndex == 0)
            {
                await profiles.SelectLocalAsync().ConfigureAwait(true);
                ApplyExecutionState(await profiles.GetExecutionStateAsync().ConfigureAwait(true));
                return AnalysisSourceSelectionOutcome.Applied;
            }

            var state = await profiles.GetExecutionStateAsync().ConfigureAwait(true);
            var eligibleProfiles = await GetEligibleProfilesAsync(
                    profiles,
                    credentials,
                    HasSelectableLocalAnalysis)
                .ConfigureAwait(true);
            ApiConfigurationStatus = Resources.GetString(
                eligibleProfiles.Count > 0
                    ? "ApiConfigurationReadyStatus"
                    : "ApiConfigurationMissingStatus");

            if (AnalysisEligibility.IsEligibleCurrentSelection(state, eligibleProfiles))
            {
                ApplyExecutionState(state);
                return AnalysisSourceSelectionOutcome.Applied;
            }

            var selection = AnalysisEligibility.ResolveEligibleSelection(state, eligibleProfiles);
            if (selection is null)
            {
                HasSelectableApiAnalysis = false;
                ApplyExecutionState(state);
                return AnalysisSourceSelectionOutcome.RequiresApiConfiguration;
            }

            await profiles.SelectRemoteAsync(
                    selection.Value.Profile.ProfileId,
                    selection.Value.Mode)
                .ConfigureAwait(true);
            ApplyExecutionState(await profiles.GetExecutionStateAsync().ConfigureAwait(true));
            return AnalysisSourceSelectionOutcome.Applied;
        }
        finally
        {
            IsWorking = false;
        }
    }

    private void ApplyExecutionState(RemoteAnalysisExecutionState state)
    {
        SelectedAnalysisSourceIndex = state.Settings.Backend == AnalysisExecutionBackend.Local ? 0 : 1;
        if (state.Settings.Backend == AnalysisExecutionBackend.Local)
        {
            CurrentExecutionTarget = Resources.GetString("ExecutionTargetLocal");
            CurrentExecutionDetail = Resources.GetString("ExecutionTargetLocalDetail");
            return;
        }

        CurrentExecutionTarget = Resources.GetString(
            state.Settings.RemoteInputMode == RemoteInputMode.DirectImage
                ? "ExecutionTargetRemoteVision"
                : "ExecutionTargetRemoteOcrText");
        CurrentExecutionDetail = state.Profile is null
            ? Resources.GetString("ExecutionTargetRemoteMissingProfileDetail")
            : string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                Resources.GetString("ExecutionTargetRemoteDetailFormat"),
                state.Profile.DisplayName,
                state.Profile.ModelId);
    }

    internal void ApplyTrayBusinessState(TrayBusinessState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!state.StorageReady)
        {
            ApplyUnavailableState();
            return;
        }

        HasSelectableLocalAnalysis = state.LocalAnalysisAvailable;
        IsSharedAnalysisOperationInProgress = state.IsAnalysisOperationInProgress;
        LocalAnalysisStatus = Resources.GetString(
            state.LocalAnalysisAvailable
                ? "LocalAnalysisReadyStatus/Text"
                : "LocalAnalysisUnavailableStatus");
        HasSelectableApiAnalysis = state.IsRemoteAnalysisSelectable;
        ApiConfigurationStatus = Resources.GetString(
            state.IsRemoteAnalysisSelectable
                ? "ApiConfigurationReadyStatus"
                : "ApiConfigurationMissingStatus");
        if (state.AnalysisExecutionState is { } executionState)
        {
            ApplyExecutionState(executionState);
        }
        else
        {
            CurrentExecutionTarget = Resources.GetString("ExecutionTargetUnavailable");
            CurrentExecutionDetail = Resources.GetString("ExecutionTargetUnavailableDetail");
        }
    }

    private void ApplyUnavailableState()
    {
        HasSelectableLocalAnalysis = false;
        LocalAnalysisStatus = Resources.GetString("LocalAnalysisUnavailableStatus");
        HasSelectableApiAnalysis = false;
        ApiConfigurationStatus = Resources.GetString("ApiConfigurationUnavailableStatus");
        CurrentExecutionTarget = Resources.GetString("ExecutionTargetUnavailable");
        CurrentExecutionDetail = Resources.GetString("ExecutionTargetUnavailableDetail");
    }

    private void ApplyAnalysisBackendUnavailableState()
    {
        HasSelectableApiAnalysis = false;
        ApiConfigurationStatus = Resources.GetString("ApiConfigurationUnavailableStatus");
        CurrentExecutionTarget = Resources.GetString("ExecutionTargetUnavailable");
        CurrentExecutionDetail = Resources.GetString("ExecutionTargetUnavailableDetail");
    }

    private async Task<List<(RemoteApiProfile Profile, RemoteInputMode Mode)>>
        GetEligibleProfilesAsync(
            IRemoteApiProfileService profiles,
            IRemoteApiCredentialService? credentials,
            bool localOcrAvailable) =>
        await AnalysisEligibility.GetEligibleProfilesAsync(
                profiles,
                credentials,
                localOcrAvailable)
            .ConfigureAwait(true);
}

public enum AnalysisSourceSelectionOutcome
{
    Applied,
    RequiresApiConfiguration,
}
