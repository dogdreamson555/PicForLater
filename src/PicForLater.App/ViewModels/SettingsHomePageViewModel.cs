using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Windows.ApplicationModel.Resources;
using PicForLater.App.Models;
using PicForLater.App.Services;
using PicForLater.Core.Analysis;

namespace PicForLater.App.ViewModels;

public partial class SettingsHomePageViewModel : ObservableObject
{
    private static readonly ResourceLoader Resources = new();
    private readonly IThemePreferenceService _themePreferenceService;
    private readonly IStorageReadinessService _storageReadinessService;
    private readonly Func<IRemoteApiProfileService?> _profileServiceAccessor;
    private readonly Func<IRemoteApiCredentialService?> _credentialServiceAccessor;

    public SettingsHomePageViewModel(
        IThemePreferenceService themePreferenceService,
        IStorageReadinessService storageReadinessService,
        Func<IRemoteApiProfileService?> profileServiceAccessor,
        Func<IRemoteApiCredentialService?> credentialServiceAccessor)
    {
        _themePreferenceService = themePreferenceService
            ?? throw new ArgumentNullException(nameof(themePreferenceService));
        _storageReadinessService = storageReadinessService
            ?? throw new ArgumentNullException(nameof(storageReadinessService));
        _profileServiceAccessor = profileServiceAccessor
            ?? throw new ArgumentNullException(nameof(profileServiceAccessor));
        _credentialServiceAccessor = credentialServiceAccessor
            ?? throw new ArgumentNullException(nameof(credentialServiceAccessor));
        SelectedThemeIndex = (int)_themePreferenceService.CurrentPreference;
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
    [NotifyPropertyChangedFor(nameof(CanChangeAnalysisSource))]
    public partial bool IsWorking { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeAnalysisSource))]
    public partial bool IsInitialized { get; set; }

    public bool CanChangeAnalysisSource => IsInitialized && !IsWorking;

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
