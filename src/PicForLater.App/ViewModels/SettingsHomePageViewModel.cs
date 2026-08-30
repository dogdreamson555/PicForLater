using System.Collections.ObjectModel;
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
    private readonly IStorageReadinessService _storageReadinessService;
    private readonly Func<IRemoteApiProfileService?> _profileServiceAccessor;
    private readonly Func<IRemoteApiCredentialService?> _credentialServiceAccessor;
    private readonly Func<ILocalSendReceiverService?> _localSendReceiverAccessor;
    private readonly ILocalSendReceivePreferenceService _localSendReceivePreference;

    public SettingsHomePageViewModel(
        IThemePreferenceService themePreferenceService,
        IStorageReadinessService storageReadinessService,
        Func<IRemoteApiProfileService?> profileServiceAccessor,
        Func<IRemoteApiCredentialService?> credentialServiceAccessor,
        ILocalSendReceivePreferenceService localSendReceivePreference,
        Func<ILocalSendReceiverService?> localSendReceiverAccessor)
    {
        _themePreferenceService = themePreferenceService
            ?? throw new ArgumentNullException(nameof(themePreferenceService));
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
        SelectedThemeIndex = (int)_themePreferenceService.CurrentPreference;
        IsLocalSendEnabled = _localSendReceivePreference.IsEnabled;
    }

    [ObservableProperty]
    public partial int SelectedThemeIndex { get; set; }

    [ObservableProperty]
    public partial string CurrentExecutionTarget { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CurrentExecutionDetail { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ApiConfigurationStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int SelectedAnalysisSourceIndex { get; set; }

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
    public partial bool IsWorking { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeAnalysisSource))]
    public partial bool IsInitialized { get; set; }

    public bool CanChangeAnalysisSource => IsInitialized && !IsWorking;

    public ObservableCollection<LocalSendTrustedDeviceItem> LocalSendTrustedDevices { get; } = [];

    partial void OnSelectedThemeIndexChanged(int value)
    {
        if (Enum.IsDefined(typeof(AppThemePreference), value))
        {
            _themePreferenceService.SetPreference((AppThemePreference)value);
        }
    }

    public async Task InitializeAsync()
    {
        IsInitialized = false;
        try
        {
            var readiness = await _storageReadinessService.EnsureReadyAsync(forceRetry: false)
                .ConfigureAwait(true);
            await InitializeLocalSendAsync(readiness.Status == StorageReadinessStatus.Ready)
                .ConfigureAwait(true);
            var profiles = _profileServiceAccessor();
            if (readiness.Status != StorageReadinessStatus.Ready || profiles is null)
            {
                ApplyUnavailableState();
                return;
            }

            var state = await profiles.GetExecutionStateAsync().ConfigureAwait(true);
            var eligibleProfiles = await GetEligibleProfilesAsync(
                    profiles,
                    _credentialServiceAccessor())
                .ConfigureAwait(true);
            ApiConfigurationStatus = Resources.GetString(
                eligibleProfiles.Count > 0
                    ? "ApiConfigurationReadyStatus"
                    : "ApiConfigurationMissingStatus");
            ApplyExecutionState(state);
            IsInitialized = true;
        }
        catch
        {
            ApplyUnavailableState();
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
            await RefreshLocalSendTrustedDevicesAsync().ConfigureAwait(true);
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

    public async Task RefreshLocalSendTrustedDevicesAsync()
    {
        var receiver = _localSendReceiverAccessor();
        if (receiver is null)
        {
            LocalSendTrustedDevices.Clear();
            UpdateLocalSendTrustedDeviceVisibility();
            return;
        }

        try
        {
            var devices = await receiver.GetTrustedDevicesAsync().ConfigureAwait(true);
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
        await RefreshLocalSendTrustedDevicesAsync().ConfigureAwait(true);
    }

    private void ApplyLocalSendUnavailableState()
    {
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
            if (state.Settings.Backend == AnalysisExecutionBackend.RemoteApi
                && state.Profile is not null)
            {
                ApplyExecutionState(state);
                return AnalysisSourceSelectionOutcome.Applied;
            }

            var eligibleProfiles = await GetEligibleProfilesAsync(profiles, credentials)
                .ConfigureAwait(true);
            ApiConfigurationStatus = Resources.GetString(
                eligibleProfiles.Count > 0
                    ? "ApiConfigurationReadyStatus"
                    : "ApiConfigurationMissingStatus");

            if (state.Settings.RemoteApiProfileId is { Length: > 0 } rememberedProfileId
                && state.Settings.RemoteInputMode is { } rememberedMode)
            {
                var rememberedSelection = eligibleProfiles.FirstOrDefault(selection =>
                    selection.Profile.ProfileId == rememberedProfileId
                    && selection.Mode == rememberedMode);
                if (rememberedSelection.Profile is { } rememberedProfile)
                {
                    await profiles.SelectRemoteAsync(
                            rememberedProfile.ProfileId,
                            rememberedSelection.Mode)
                        .ConfigureAwait(true);
                    ApplyExecutionState(await profiles.GetExecutionStateAsync().ConfigureAwait(true));
                    return AnalysisSourceSelectionOutcome.Applied;
                }
            }

            if (eligibleProfiles.Count != 1)
            {
                ApplyExecutionState(state);
                return AnalysisSourceSelectionOutcome.RequiresApiConfiguration;
            }

            var selection = eligibleProfiles[0];
            await profiles.SelectRemoteAsync(selection.Profile.ProfileId, selection.Mode)
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

    private void ApplyUnavailableState()
    {
        ApiConfigurationStatus = Resources.GetString("ApiConfigurationUnavailableStatus");
        CurrentExecutionTarget = Resources.GetString("ExecutionTargetUnavailable");
        CurrentExecutionDetail = Resources.GetString("ExecutionTargetUnavailableDetail");
    }

    private static async Task<List<(RemoteApiProfile Profile, RemoteInputMode Mode)>>
        GetEligibleProfilesAsync(
            IRemoteApiProfileService profiles,
            IRemoteApiCredentialService? credentials)
    {
        var eligibleProfiles = new List<(RemoteApiProfile Profile, RemoteInputMode Mode)>();
        foreach (var profile in await profiles.GetProfilesAsync().ConfigureAwait(true))
        {
            if (!TryGetEligibleMode(profile, out var mode))
            {
                continue;
            }

            var hasCredential = profile.AuthenticationKind == RemoteApiAuthenticationKind.None
                || credentials is not null
                && await credentials.ExistsAsync(profile.CredentialReference).ConfigureAwait(true);
            if (hasCredential)
            {
                eligibleProfiles.Add((profile, mode));
            }
        }

        return eligibleProfiles;
    }

    private static bool TryGetEligibleMode(RemoteApiProfile profile, out RemoteInputMode mode)
    {
        mode = profile.ConsentedInputMode ?? default;
        return profile.IsEnabled
            && profile.ValidationState == RemoteApiProfileValidationState.Valid
            && profile.LastVerifiedAtUtc is not null
            && profile.ConsentedInputMode is not null
            && profile.ConsentedDisclosureVersion == profile.DisclosureVersion
            && profile.ConsentGrantedAtUtc is not null
            && profile.SupportedInputModes.Contains(mode);
    }
}

public enum AnalysisSourceSelectionOutcome
{
    Applied,
    RequiresApiConfiguration,
}
