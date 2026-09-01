using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Windows.ApplicationModel.Resources;
using PicForLater.App.Models;
using PicForLater.App.Services;
using PicForLater.Core.Analysis;

namespace PicForLater.App.ViewModels;

public partial class ApiAnalysisSettingsPageViewModel : ObservableObject
{
    private static readonly ResourceLoader Resources = new();
    private readonly IStorageReadinessService _storageReadinessService;
    private readonly Func<IRemoteApiProfileService?> _profileServiceAccessor;
    private readonly Func<IRemoteApiCredentialService?> _credentialServiceAccessor;
    private readonly Func<IRemoteApiConnectionTester?> _connectionTesterAccessor;
    private RemoteApiProfile? _profile;
    private string? _activeRemoteProfileId;
    private RemoteInputMode? _activeRemoteInputMode;
    private readonly List<RemoteApiProviderOption> _allProviderOptions = [];
    private readonly SemaphoreSlim _outputLanguageSaveGate = new(1, 1);
    private (string ProfileId, string ModelId, RemoteInputMode InputMode)? _lastSuccessfulTest;
    private AnalysisOutputLanguage _persistedOutputLanguage =
        AnalysisOutputLanguage.ModelDefault;
    private int _outputLanguageSaveVersion;
    private bool _credentialExists;
    private bool _loadingAdvancedSettings;

    public ApiAnalysisSettingsPageViewModel(
        IStorageReadinessService storageReadinessService,
        Func<IRemoteApiProfileService?> profileServiceAccessor,
        Func<IRemoteApiCredentialService?> credentialServiceAccessor,
        Func<IRemoteApiConnectionTester?> connectionTesterAccessor)
    {
        _storageReadinessService = storageReadinessService
            ?? throw new ArgumentNullException(nameof(storageReadinessService));
        _profileServiceAccessor = profileServiceAccessor
            ?? throw new ArgumentNullException(nameof(profileServiceAccessor));
        _credentialServiceAccessor = credentialServiceAccessor
            ?? throw new ArgumentNullException(nameof(credentialServiceAccessor));
        _connectionTesterAccessor = connectionTesterAccessor
            ?? throw new ArgumentNullException(nameof(connectionTesterAccessor));
    }

    public ObservableCollection<RemoteApiProviderOption> ProviderOptions { get; } = [];

    public ObservableCollection<RemoteApiCategoryOption> CategoryOptions { get; } = [];

    public ObservableCollection<RemoteReasoningOption> ReasoningOptions { get; } = [];

    [ObservableProperty]
    public partial RemoteApiCategoryOption? SelectedCategoryOption { get; set; }

    [ObservableProperty]
    public partial RemoteApiProviderOption? SelectedProviderOption { get; set; }

    [ObservableProperty]
    public partial string ModelId { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ModelSuggestion { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EndpointHost { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EndpointUriText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int SelectedProtocolIndex { get; set; }

    [ObservableProperty]
    public partial int SelectedAuthenticationIndex { get; set; }

    [ObservableProperty]
    public partial int SelectedStructuredOutputIndex { get; set; }

    [ObservableProperty]
    public partial int SelectedEndpointTrustIndex { get; set; }

    [ObservableProperty]
    public partial int SelectedReasoningWireFormatIndex { get; set; }

    [ObservableProperty]
    public partial RemoteReasoningOption? SelectedReasoningOption { get; set; }

    [ObservableProperty]
    public partial double AdvancedMaxOutputTokens { get; set; } = 1_024;

    [ObservableProperty]
    public partial double AdvancedTimeoutSeconds { get; set; } = 60;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEnableRemote))]
    [NotifyPropertyChangedFor(nameof(CanTestConnection))]
    [NotifyPropertyChangedFor(nameof(CanSaveAdvancedSettings))]
    public partial bool AdvancedSettingsDirty { get; set; }

    [ObservableProperty]
    public partial bool IsCustomProfile { get; set; }

    [ObservableProperty]
    public partial bool CanUseDirectImage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTestConnection))]
    [NotifyPropertyChangedFor(nameof(CanDeleteCredential))]
    public partial bool RequiresCredential { get; set; } = true;

    [ObservableProperty]
    public partial Uri? PrivacyUri { get; set; }

    [ObservableProperty]
    public partial Uri? TermsUri { get; set; }

    [ObservableProperty]
    public partial Uri? PricingUri { get; set; }

    [ObservableProperty]
    public partial string RetentionStatement { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PolicyVerifiedText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LimitsText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CredentialStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasCredential { get; set; }

    [ObservableProperty]
    public partial int SelectedInputModeIndex { get; set; }

    [ObservableProperty]
    public partial int SelectedOutputLanguageIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeOutputLanguage))]
    public partial bool IsSavingOutputLanguage { get; set; }

    [ObservableProperty]
    public partial string CurrentExecutionTarget { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CurrentExecutionDetail { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial SettingsStatusKind StatusKind { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    [NotifyPropertyChangedFor(nameof(CanTestConnection))]
    [NotifyPropertyChangedFor(nameof(CanEnableRemote))]
    [NotifyPropertyChangedFor(nameof(CanDeleteCredential))]
    [NotifyPropertyChangedFor(nameof(CanUseLocal))]
    [NotifyPropertyChangedFor(nameof(CanRevokeConsent))]
    [NotifyPropertyChangedFor(nameof(CanSaveAdvancedSettings))]
    public partial bool IsWorking { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEnableRemote))]
    public partial bool IsProfileValid { get; set; }

    [ObservableProperty]
    public partial string ValidationStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ConsentStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUseLocal))]
    public partial bool IsRemoteSelected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRevokeConsent))]
    public partial bool HasConsent { get; set; }

    [ObservableProperty]
    public partial bool IsCurrentProfileSelected { get; set; }

    public bool IsInitialized { get; private set; }

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    public string SavedEndpointUriText => _profile?.BaseUri.AbsoluteUri ?? string.Empty;

    public bool HasPendingEndpointChange => _profile is not null
        && !string.Equals(
            _profile.BaseUri.AbsoluteUri.TrimEnd('/'),
            EndpointUriText.Trim().TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);

    public bool CanEdit => !IsWorking && SelectedProviderOption is not null;

    public bool CanTestConnection => CanEdit && HasCredential && !AdvancedSettingsDirty;

    public bool CanDeleteCredential => !IsWorking && RequiresCredential && HasCredential;

    public bool CanSaveAdvancedSettings => CanEdit && AdvancedSettingsDirty;

    public bool CanUseLocal => !IsWorking && IsRemoteSelected;

    public bool CanRevokeConsent => !IsWorking && HasConsent;

    public bool CanChangeOutputLanguage => IsInitialized && !IsSavingOutputLanguage;

    public bool CanEnableRemote =>
        CanTestConnection
        && !AdvancedSettingsDirty
        && IsProfileValid
        && _profile is not null
        && string.Equals(_profile.ModelId, ModelId.Trim(), StringComparison.Ordinal)
        && (_profile.ConsentedInputMode == SelectedInputMode
            || _lastSuccessfulTest is { } tested
            && tested.ProfileId == _profile.ProfileId
            && tested.ModelId == _profile.ModelId
            && tested.InputMode == SelectedInputMode);

    public RemoteInputMode SelectedInputMode => SelectedInputModeIndex == 1
        ? RemoteInputMode.DirectImage
        : RemoteInputMode.LocalOcrText;

    partial void OnSelectedInputModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(SelectedInputMode));
        OnPropertyChanged(nameof(CanEnableRemote));
        ApplyCurrentExecutionSelectionState();
        ApplyConsentState();
    }

    partial void OnSelectedCategoryOptionChanged(RemoteApiCategoryOption? value)
    {
        if (value is not null)
        {
            ApplyCategoryFilter(value.Category);
        }
    }

    partial void OnSelectedAuthenticationIndexChanged(int value)
    {
        if (!IsCustomProfile)
        {
            return;
        }

        RequiresCredential = value != (int)RemoteApiAuthenticationKind.None;
        HasCredential = !RequiresCredential || _credentialExists;
        CredentialStatusText = Resources.GetString(
            RequiresCredential
                ? HasCredential ? "ApiCredentialSavedState" : "ApiCredentialMissingState"
                : "ApiCredentialNotRequiredState");
        MarkAdvancedSettingsDirty();
    }

    partial void OnSelectedProtocolIndexChanged(int value)
    {
        if (value == (int)RemoteApiProtocol.AnthropicMessages)
        {
            if (SelectedStructuredOutputIndex == (int)RemoteStructuredOutputMode.JsonObject)
            {
                SelectedStructuredOutputIndex = (int)RemoteStructuredOutputMode.JsonSchema;
            }

            SelectedReasoningWireFormatIndex = (int)RemoteReasoningWireFormat.None;
            SelectedReasoningOption = ReasoningOptions.FirstOrDefault(
                option => option.Mode == RemoteReasoningMode.ProviderDefault);
        }

        MarkAdvancedSettingsDirty();
    }

    partial void OnSelectedStructuredOutputIndexChanged(int value) =>
        MarkAdvancedSettingsDirty();

    partial void OnSelectedEndpointTrustIndexChanged(int value) =>
        MarkAdvancedSettingsDirty();

    partial void OnSelectedReasoningWireFormatIndexChanged(int value) =>
        MarkAdvancedSettingsDirty();

    partial void OnSelectedReasoningOptionChanged(RemoteReasoningOption? value) =>
        MarkAdvancedSettingsDirty();

    partial void OnAdvancedMaxOutputTokensChanged(double value) =>
        MarkAdvancedSettingsDirty();

    partial void OnAdvancedTimeoutSecondsChanged(double value) =>
        MarkAdvancedSettingsDirty();

    partial void OnEndpointUriTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasPendingEndpointChange));
        MarkAdvancedSettingsDirty();
    }

    partial void OnHasCredentialChanged(bool value)
    {
        OnPropertyChanged(nameof(CanTestConnection));
        OnPropertyChanged(nameof(CanEnableRemote));
        OnPropertyChanged(nameof(CanDeleteCredential));
    }

    partial void OnModelIdChanged(string value)
    {
        OnPropertyChanged(nameof(CanEnableRemote));
        if (_profile is not null
            && !string.Equals(_profile.ModelId, value.Trim(), StringComparison.Ordinal))
        {
            ValidationStatusText = Resources.GetString("ApiValidationModelChanged");
        }
        else if (_profile is not null)
        {
            ApplyValidationState();
        }
    }

    public async Task InitializeAsync()
    {
        if (IsInitialized)
        {
            return;
        }

        var readiness = await _storageReadinessService.EnsureReadyAsync(forceRetry: false)
            .ConfigureAwait(true);
        if (readiness.Status != StorageReadinessStatus.Ready)
        {
            ShowStatus("ApiSettingsUnavailableStatus", SettingsStatusKind.Error);
            return;
        }

        IsWorking = true;
        try
        {
            var profileService = GetProfileService();
            var options = await RemoteApiProviderCatalog.EnsureProfilesAsync(profileService)
                .ConfigureAwait(true);
            _allProviderOptions.Clear();
            _allProviderOptions.AddRange(options);
            PopulateCategories();

            var execution = await profileService.GetExecutionStateAsync().ConfigureAwait(true);
            ApplyPersistedOutputLanguage(execution.Settings.OutputLanguage);
            var selectedProfileId = await ResolveInitialProfileIdAsync(
                    profileService,
                    execution)
                .ConfigureAwait(true);
            var selected = _allProviderOptions.FirstOrDefault(
                option => option.ProfileId == selectedProfileId)
                ?? _allProviderOptions.FirstOrDefault();
            if (selected is not null)
            {
                SelectedCategoryOption = CategoryOptions.First(
                    category => category.Category == selected.Category);
                await SelectProviderAsync(selected).ConfigureAwait(true);
            }

            IsInitialized = true;
            OnPropertyChanged(nameof(CanChangeOutputLanguage));
        }
        finally
        {
            IsWorking = false;
        }
    }

    public async Task SetOutputLanguageIndexAsync(int selectedIndex)
    {
        var requestedLanguage = OutputLanguageFromIndex(selectedIndex);
        var saveVersion = Interlocked.Increment(ref _outputLanguageSaveVersion);
        SelectedOutputLanguageIndex = selectedIndex;
        if (!IsSavingOutputLanguage && requestedLanguage == _persistedOutputLanguage)
        {
            return;
        }

        IsSavingOutputLanguage = true;
        await _outputLanguageSaveGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (saveVersion != Volatile.Read(ref _outputLanguageSaveVersion))
            {
                return;
            }

            await GetProfileService().SetOutputLanguageAsync(requestedLanguage)
                .ConfigureAwait(true);
            _persistedOutputLanguage = requestedLanguage;
            if (saveVersion == Volatile.Read(ref _outputLanguageSaveVersion))
            {
                ClearOutputLanguageSaveFailure();
            }
        }
        catch
        {
            if (saveVersion == Volatile.Read(ref _outputLanguageSaveVersion))
            {
                SelectedOutputLanguageIndex = OutputLanguageToIndex(
                    _persistedOutputLanguage);
                ShowStatus("ApiOutputLanguageSaveFailedStatus", SettingsStatusKind.Error);
            }
        }
        finally
        {
            _outputLanguageSaveGate.Release();
            if (saveVersion == Volatile.Read(ref _outputLanguageSaveVersion))
            {
                IsSavingOutputLanguage = false;
            }
        }
    }

    private async Task<string?> ResolveInitialProfileIdAsync(
        IRemoteApiProfileService profileService,
        RemoteAnalysisExecutionState execution)
    {
        if (execution.Settings.RemoteApiProfileId is { Length: > 0 } rememberedProfileId)
        {
            return rememberedProfileId;
        }

        var profiles = await profileService.GetProfilesAsync().ConfigureAwait(true);
        var recentlyConsented = profiles
            .Where(profile => profile.ConsentGrantedAtUtc is not null)
            .OrderByDescending(profile => profile.ConsentGrantedAtUtc)
            .FirstOrDefault();
        if (recentlyConsented is not null)
        {
            return recentlyConsented.ProfileId;
        }

        var recentlyVerified = profiles
            .Where(profile => profile.LastVerifiedAtUtc is not null)
            .OrderByDescending(profile => profile.LastVerifiedAtUtc)
            .FirstOrDefault();
        if (recentlyVerified is not null)
        {
            return recentlyVerified.ProfileId;
        }

        if (_credentialServiceAccessor() is { } credentials)
        {
            foreach (var profile in profiles.OrderByDescending(profile => profile.UpdatedAtUtc))
            {
                if (profile.AuthenticationKind != RemoteApiAuthenticationKind.None
                    && await credentials.ExistsAsync(profile.CredentialReference).ConfigureAwait(true))
                {
                    return profile.ProfileId;
                }
            }
        }

        return _allProviderOptions.FirstOrDefault()?.ProfileId;
    }

    public async Task SelectProviderAsync(RemoteApiProviderOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        var profile = await GetProfileService().GetProfileAsync(option.ProfileId)
            .ConfigureAwait(true)
            ?? throw new InvalidOperationException("The selected API profile is unavailable.");
        SelectedProviderOption = option;
        _profile = profile;
        _loadingAdvancedSettings = true;
        try
        {
            ModelId = profile.ModelId;
            ModelSuggestion = option.ModelSuggestion;
            EndpointHost = profile.BaseUri.Host;
            EndpointUriText = profile.BaseUri.AbsoluteUri;
            SelectedProtocolIndex = (int)profile.Protocol;
            SelectedAuthenticationIndex = (int)profile.AuthenticationKind;
            SelectedStructuredOutputIndex = (int)profile.StructuredOutputMode;
            SelectedEndpointTrustIndex = profile.EndpointTrustMode == RemoteEndpointTrustMode.LoopbackHttp ? 1 : 0;
            ReasoningOptions.Clear();
            foreach (var mode in option.SupportedReasoningModes)
            {
                ReasoningOptions.Add(new(mode, GetReasoningModeDisplayName(mode)));
            }

            SelectedReasoningOption = ReasoningOptions.FirstOrDefault(
                item => item.Mode == profile.ReasoningMode)
                ?? ReasoningOptions.FirstOrDefault();
            SelectedReasoningWireFormatIndex = (int)profile.ReasoningWireFormat;
            AdvancedMaxOutputTokens = profile.MaxOutputTokens;
            AdvancedTimeoutSeconds = profile.TimeoutSeconds;
            AdvancedSettingsDirty = false;
        }
        finally
        {
            _loadingAdvancedSettings = false;
        }
        OnPropertyChanged(nameof(SavedEndpointUriText));
        OnPropertyChanged(nameof(HasPendingEndpointChange));
        IsCustomProfile = option.IsCustom;
        CanUseDirectImage = profile.SupportedInputModes.Contains(RemoteInputMode.DirectImage);
        if (!CanUseDirectImage && SelectedInputModeIndex == 1)
        {
            SelectedInputModeIndex = 0;
        }
        RequiresCredential = profile.AuthenticationKind != RemoteApiAuthenticationKind.None;
        PrivacyUri = profile.PrivacyUrl;
        TermsUri = profile.TermsUrl;
        PricingUri = new Uri(option.PricingUrl);
        RetentionStatement = Resources.GetString(option.RetentionResourceName);
        PolicyVerifiedText = string.Format(
            CultureInfo.CurrentCulture,
            Resources.GetString("ApiPolicyVerifiedFormat"),
            profile.RetentionTrainingVerifiedAtUtc.ToLocalTime());
        LimitsText = string.Format(
            CultureInfo.CurrentCulture,
            Resources.GetString("ApiLimitsFormat"),
            profile.MaxTextChars,
            profile.MaxImageBytes / (1024 * 1024),
            profile.TimeoutSeconds);
        SelectedInputModeIndex = profile.ConsentedInputMode == RemoteInputMode.DirectImage
            ? 1
            : 0;
        await RefreshCredentialAsync().ConfigureAwait(true);
        await RefreshStateAsync().ConfigureAwait(true);
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanTestConnection));
        OnPropertyChanged(nameof(CanEnableRemote));
    }

    public async Task SaveCredentialAsync(string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        var profile = GetCurrentProfile();
        IsWorking = true;
        try
        {
            await GetProfileService().SelectLocalAsync().ConfigureAwait(true);
            await GetCredentialService().StoreAsync(profile.CredentialReference, secret)
                .ConfigureAwait(true);
            _profile = await GetProfileService().SaveProfileAsync(profile with
            {
                IsEnabled = true,
                ValidationState = RemoteApiProfileValidationState.Unverified,
                LastVerifiedAtUtc = null,
            }).ConfigureAwait(true);
            _lastSuccessfulTest = null;
            await RefreshCredentialAsync().ConfigureAwait(true);
            await RefreshStateAsync().ConfigureAwait(true);
            ShowStatus("ApiCredentialSavedStatus", SettingsStatusKind.Success);
        }
        finally
        {
            IsWorking = false;
        }
    }

    public async Task DeleteCredentialAsync()
    {
        var profile = GetCurrentProfile();
        IsWorking = true;
        try
        {
            await GetProfileService().SelectLocalAsync().ConfigureAwait(true);
            await GetCredentialService().DeleteAsync(profile.CredentialReference)
                .ConfigureAwait(true);
            _profile = await GetProfileService().SaveProfileAsync(profile with
            {
                IsEnabled = false,
                ValidationState = RemoteApiProfileValidationState.Unverified,
                LastVerifiedAtUtc = null,
                ConsentedInputMode = null,
                ConsentedDisclosureVersion = null,
                ConsentGrantedAtUtc = null,
            }).ConfigureAwait(true);
            _lastSuccessfulTest = null;
            await RefreshCredentialAsync().ConfigureAwait(true);
            await RefreshStateAsync().ConfigureAwait(true);
            ShowStatus("ApiCredentialDeletedStatus", SettingsStatusKind.Warning);
        }
        finally
        {
            IsWorking = false;
        }
    }

    public async Task TestConnectionAsync()
    {
        if (IsWorking)
        {
            return;
        }

        if (AdvancedSettingsDirty)
        {
            ShowStatus("ApiAdvancedSettingsSaveRequiredStatus", SettingsStatusKind.Warning);
            return;
        }

        IsWorking = true;
        try
        {
            var profile = IsCustomProfile
                ? await SaveCustomProfileAsync(showSuccessStatus: false).ConfigureAwait(true)
                : await SaveModelIfChangedAsync().ConfigureAwait(true);
            if (!HasCredential)
            {
                ShowStatus("ApiCredentialRequiredStatus", SettingsStatusKind.Warning);
                return;
            }

            try
            {
                await GetConnectionTester().TestAsync(profile, SelectedInputMode)
                    .ConfigureAwait(true);
                _profile = await GetProfileService().SaveProfileAsync(profile with
                {
                    IsEnabled = true,
                    ValidationState = RemoteApiProfileValidationState.Valid,
                    LastVerifiedAtUtc = DateTimeOffset.UtcNow,
                }).ConfigureAwait(true);
                _lastSuccessfulTest = (
                    _profile.ProfileId,
                    _profile.ModelId,
                    SelectedInputMode);
                ApplyValidationState();
                ShowStatus("ApiConnectionSucceededStatus", SettingsStatusKind.Success);
            }
            catch (RemoteAnalysisProviderException exception)
            {
                await GetProfileService().SelectLocalAsync().ConfigureAwait(true);
                _profile = await GetProfileService().SaveProfileAsync(profile with
                {
                    IsEnabled = true,
                    ValidationState = RemoteApiProfileValidationState.Invalid,
                    LastVerifiedAtUtc = null,
                }).ConfigureAwait(true);
                _lastSuccessfulTest = null;
                ApplyValidationState();
                await RefreshStateAsync().ConfigureAwait(true);
                ShowRemoteFailure(exception.ErrorCode);
            }
        }
        finally
        {
            IsWorking = false;
        }
    }

    public async Task EnableRemoteAsync()
    {
        var profile = GetCurrentProfile();
        if (!CanEnableRemote)
        {
            ShowStatus("ApiConnectionTestRequiredStatus", SettingsStatusKind.Warning);
            return;
        }

        IsWorking = true;
        try
        {
            await GetProfileService().SelectLocalAsync().ConfigureAwait(true);
            profile = await GetProfileService().SaveProfileAsync(profile with
            {
                IsEnabled = true,
                ConsentedInputMode = SelectedInputMode,
                ConsentedDisclosureVersion = profile.DisclosureVersion,
                ConsentGrantedAtUtc = DateTimeOffset.UtcNow,
            }).ConfigureAwait(true);
            await GetProfileService().SelectRemoteAsync(profile.ProfileId, SelectedInputMode)
                .ConfigureAwait(true);
            _profile = profile;
            await RefreshStateAsync().ConfigureAwait(true);
            ShowStatus("ApiRemoteEnabledStatus", SettingsStatusKind.Success);
        }
        finally
        {
            IsWorking = false;
        }
    }

    public async Task SelectLocalAsync()
    {
        IsWorking = true;
        try
        {
            await GetProfileService().SelectLocalAsync().ConfigureAwait(true);
            await RefreshStateAsync().ConfigureAwait(true);
            ShowStatus("ApiLocalSelectedStatus", SettingsStatusKind.Success);
        }
        finally
        {
            IsWorking = false;
        }
    }

    public async Task RevokeConsentAsync()
    {
        var profile = GetCurrentProfile();
        IsWorking = true;
        try
        {
            await GetProfileService().SelectLocalAsync().ConfigureAwait(true);
            _profile = await GetProfileService().SaveProfileAsync(profile with
            {
                IsEnabled = false,
                ConsentedInputMode = null,
                ConsentedDisclosureVersion = null,
                ConsentGrantedAtUtc = null,
            }).ConfigureAwait(true);
            await RefreshStateAsync().ConfigureAwait(true);
            ShowStatus("ApiConsentRevokedStatus", SettingsStatusKind.Warning);
        }
        finally
        {
            IsWorking = false;
        }
    }

    public async Task SaveAdvancedSettingsAsync()
    {
        if (IsCustomProfile)
        {
            await SaveCustomProfileAsync(showSuccessStatus: true).ConfigureAwait(true);
            return;
        }

        var profile = GetCurrentProfile();
        if (!Uri.TryCreate(EndpointUriText.Trim(), UriKind.Absolute, out var endpoint))
        {
            throw new RemoteApiProfileException("remote.base-uri-invalid");
        }

        var preset = RemoteApiProviderCatalog.GetPreset(profile.ProfileId);
        var isPresetEndpoint = endpoint == preset.BaseUri;
        var trustMode = isPresetEndpoint
            ? preset.EndpointTrustMode
            : RemoteEndpointPolicy.IsLoopbackHost(endpoint.Host)
                ? RemoteEndpointTrustMode.LoopbackHttp
                : RemoteEndpointTrustMode.PublicHttps;
        if (!RemoteEndpointPolicy.IsAllowed(endpoint, trustMode))
        {
            throw new RemoteApiProfileException("remote.base-uri-invalid");
        }

        var (reasoningMode, reasoningWireFormat, maxOutputTokens, timeoutSeconds) =
            ValidateAdvancedSettings();
        if (profile.BaseUri == endpoint
            && profile.EndpointTrustMode == trustMode
            && profile.ReasoningMode == reasoningMode
            && profile.ReasoningWireFormat == reasoningWireFormat
            && profile.MaxOutputTokens == maxOutputTokens
            && profile.TimeoutSeconds == timeoutSeconds)
        {
            AdvancedSettingsDirty = false;
            return;
        }

        IsWorking = true;
        try
        {
            await GetProfileService().SelectLocalAsync().ConfigureAwait(true);
            _profile = await GetProfileService().SaveProfileAsync(profile with
            {
                EndpointId = RemoteApiProviderCatalog.GetEndpointId(preset, endpoint),
                BaseUri = endpoint,
                EndpointTrustMode = trustMode,
                ReasoningMode = reasoningMode,
                ReasoningWireFormat = reasoningWireFormat,
                MaxOutputTokens = maxOutputTokens,
                TimeoutSeconds = timeoutSeconds,
                ValidationState = RemoteApiProfileValidationState.Unverified,
                LastVerifiedAtUtc = null,
                ConsentedInputMode = null,
                ConsentedDisclosureVersion = null,
                ConsentGrantedAtUtc = null,
            }).ConfigureAwait(true);
            _lastSuccessfulTest = null;
            EndpointHost = _profile.BaseUri.Host;
            EndpointUriText = _profile.BaseUri.AbsoluteUri;
            AdvancedSettingsDirty = false;
            OnPropertyChanged(nameof(SavedEndpointUriText));
            OnPropertyChanged(nameof(HasPendingEndpointChange));
            await RefreshStateAsync().ConfigureAwait(true);
            ApplyValidationState();
            ShowStatus("ApiAdvancedSettingsSavedStatus", SettingsStatusKind.Success);
        }
        finally
        {
            IsWorking = false;
        }
    }

    private async Task<RemoteApiProfile> SaveCustomProfileAsync(bool showSuccessStatus)
    {
        var profile = GetCurrentProfile();
        if (!IsCustomProfile
            || !Uri.TryCreate(EndpointUriText.Trim(), UriKind.Absolute, out var endpoint))
        {
            throw new RemoteApiProfileException("remote.base-uri-invalid");
        }

        var trustMode = SelectedEndpointTrustIndex == 1
            ? RemoteEndpointTrustMode.LoopbackHttp
            : RemoteEndpointTrustMode.PublicHttps;
        if (!RemoteEndpointPolicy.IsAllowed(endpoint, trustMode))
        {
            throw new RemoteApiProfileException("remote.base-uri-invalid");
        }

        var protocol = SelectedProtocolIndex == (int)RemoteApiProtocol.AnthropicMessages
            ? RemoteApiProtocol.AnthropicMessages
            : RemoteApiProtocol.OpenAiChatCompletions;
        var authentication = Enum.IsDefined((RemoteApiAuthenticationKind)SelectedAuthenticationIndex)
            ? (RemoteApiAuthenticationKind)SelectedAuthenticationIndex
            : RemoteApiAuthenticationKind.Bearer;
        var selectedOutputMode = Enum.IsDefined(
                (RemoteStructuredOutputMode)SelectedStructuredOutputIndex)
            ? (RemoteStructuredOutputMode)SelectedStructuredOutputIndex
            : RemoteStructuredOutputMode.JsonSchema;
        var outputMode = protocol == RemoteApiProtocol.AnthropicMessages
            && selectedOutputMode == RemoteStructuredOutputMode.JsonObject
                ? RemoteStructuredOutputMode.JsonSchema
                : selectedOutputMode;
        var (reasoningMode, reasoningWireFormat, maxOutputTokens, timeoutSeconds) =
            ValidateAdvancedSettings();
        if (protocol == RemoteApiProtocol.AnthropicMessages)
        {
            reasoningMode = RemoteReasoningMode.ProviderDefault;
            reasoningWireFormat = RemoteReasoningWireFormat.None;
        }
        var modelId = ValidateModelId();
        await GetProfileService().SelectLocalAsync().ConfigureAwait(true);
        _profile = await GetProfileService().SaveProfileAsync(profile with
        {
            EndpointId = protocol == RemoteApiProtocol.AnthropicMessages
                ? "custom.anthropic-messages"
                : "custom.openai-chat-completions",
            BaseUri = endpoint,
            ModelId = modelId,
            Protocol = protocol,
            AuthenticationKind = authentication,
            StructuredOutputMode = outputMode,
            EndpointTrustMode = trustMode,
            ApiVersion = protocol == RemoteApiProtocol.AnthropicMessages ? "2023-06-01" : null,
            DisableProviderFallbacks = false,
            DisableExternalSearch = false,
            ReasoningMode = reasoningMode,
            ReasoningWireFormat = reasoningWireFormat,
            MaxOutputTokens = maxOutputTokens,
            TimeoutSeconds = timeoutSeconds,
            IsEnabled = true,
            ValidationState = RemoteApiProfileValidationState.Unverified,
            LastVerifiedAtUtc = null,
            ConsentedInputMode = null,
            ConsentedDisclosureVersion = null,
            ConsentGrantedAtUtc = null,
        }).ConfigureAwait(true);
        _lastSuccessfulTest = null;
        EndpointHost = _profile.BaseUri.Host;
        EndpointUriText = _profile.BaseUri.AbsoluteUri;
        AdvancedSettingsDirty = false;
        ModelId = _profile.ModelId;
        OnPropertyChanged(nameof(SavedEndpointUriText));
        OnPropertyChanged(nameof(HasPendingEndpointChange));
        RequiresCredential = authentication != RemoteApiAuthenticationKind.None;
        await RefreshCredentialAsync().ConfigureAwait(true);
        await RefreshStateAsync().ConfigureAwait(true);
        ApplyValidationState();
        if (showSuccessStatus)
        {
            ShowStatus("ApiCustomProfileSavedStatus", SettingsStatusKind.Success);
        }

        return _profile;
    }

    private async Task<RemoteApiProfile> SaveModelIfChangedAsync()
    {
        var profile = GetCurrentProfile();
        var normalizedModelId = ValidateModelId();

        if (profile.ModelId == normalizedModelId)
        {
            return profile;
        }

        await GetProfileService().SelectLocalAsync().ConfigureAwait(true);
        _profile = await GetProfileService().SaveProfileAsync(profile with
        {
            ModelId = normalizedModelId,
            IsEnabled = true,
            ValidationState = RemoteApiProfileValidationState.Unverified,
            LastVerifiedAtUtc = null,
            ConsentedInputMode = null,
            ConsentedDisclosureVersion = null,
            ConsentGrantedAtUtc = null,
        }).ConfigureAwait(true);
        ModelId = _profile.ModelId;
        _lastSuccessfulTest = null;
        await RefreshStateAsync().ConfigureAwait(true);
        ApplyValidationState();
        return _profile;
    }

    private (RemoteReasoningMode Mode, RemoteReasoningWireFormat WireFormat,
        int MaxOutputTokens, int TimeoutSeconds) ValidateAdvancedSettings()
    {
        var mode = SelectedReasoningOption?.Mode
            ?? throw new RemoteApiProfileException("remote.protocol-settings-invalid");
        var wireFormat = IsCustomProfile
            && Enum.IsDefined((RemoteReasoningWireFormat)SelectedReasoningWireFormatIndex)
                ? (RemoteReasoningWireFormat)SelectedReasoningWireFormatIndex
                : GetCurrentProfile().ReasoningWireFormat;
        if (SelectedProviderOption is null
            || !SelectedProviderOption.SupportedReasoningModes.Contains(mode))
        {
            throw new RemoteApiProfileException("remote.protocol-settings-invalid");
        }

        if ((wireFormat == RemoteReasoningWireFormat.None
                && mode != RemoteReasoningMode.ProviderDefault)
            || (wireFormat is RemoteReasoningWireFormat.ThinkingObject
                    or RemoteReasoningWireFormat.EnableThinkingBoolean
                    or RemoteReasoningWireFormat.ReasoningEnabledObject
                && mode is not RemoteReasoningMode.ProviderDefault
                    and not RemoteReasoningMode.Disabled))
        {
            throw new RemoteApiProfileException("remote.protocol-settings-invalid");
        }

        var maxOutputTokens = checked((int)Math.Round(
            AdvancedMaxOutputTokens,
            MidpointRounding.AwayFromZero));
        var timeoutSeconds = checked((int)Math.Round(
            AdvancedTimeoutSeconds,
            MidpointRounding.AwayFromZero));
        if (maxOutputTokens is < 128 or > 32_768
            || timeoutSeconds is < 5 or > 600)
        {
            throw new RemoteApiProfileException("remote.profile-limits-invalid");
        }

        return (mode, wireFormat, maxOutputTokens, timeoutSeconds);
    }

    private static string GetReasoningModeDisplayName(RemoteReasoningMode mode) =>
        Resources.GetString(mode switch
        {
            RemoteReasoningMode.Disabled => "ApiReasoningDisabled",
            RemoteReasoningMode.Low => "ApiReasoningLow",
            RemoteReasoningMode.Medium => "ApiReasoningMedium",
            RemoteReasoningMode.High => "ApiReasoningHigh",
            _ => "ApiReasoningProviderDefault",
        });

    private void MarkAdvancedSettingsDirty()
    {
        if (_loadingAdvancedSettings || _profile is null)
        {
            return;
        }

        AdvancedSettingsDirty = true;
        OnPropertyChanged(nameof(CanTestConnection));
        OnPropertyChanged(nameof(CanEnableRemote));
    }

    private async Task RefreshCredentialAsync()
    {
        var profile = GetCurrentProfile();
        RequiresCredential = profile.AuthenticationKind != RemoteApiAuthenticationKind.None;
        _credentialExists = await GetCredentialService().ExistsAsync(profile.CredentialReference)
            .ConfigureAwait(true);
        HasCredential = !RequiresCredential || _credentialExists;
        CredentialStatusText = Resources.GetString(
            !RequiresCredential
                ? "ApiCredentialNotRequiredState"
                : HasCredential ? "ApiCredentialSavedState" : "ApiCredentialMissingState");
    }

    private async Task RefreshStateAsync()
    {
        var state = await GetProfileService().GetExecutionStateAsync().ConfigureAwait(true);
        IsRemoteSelected = state.Settings.Backend == AnalysisExecutionBackend.RemoteApi;
        _activeRemoteProfileId = IsRemoteSelected
            ? state.Settings.RemoteApiProfileId
            : null;
        _activeRemoteInputMode = IsRemoteSelected
            ? state.Settings.RemoteInputMode
            : null;
        ApplyCurrentExecutionSelectionState();
        if (!IsRemoteSelected)
        {
            CurrentExecutionTarget = Resources.GetString("ExecutionTargetLocal");
            CurrentExecutionDetail = Resources.GetString("ExecutionTargetLocalDetail");
        }
        else
        {
            CurrentExecutionTarget = Resources.GetString(
                state.Settings.RemoteInputMode == RemoteInputMode.DirectImage
                    ? "ExecutionTargetRemoteVision"
                    : "ExecutionTargetRemoteOcrText");
            CurrentExecutionDetail = state.Profile is null
                ? Resources.GetString("ExecutionTargetRemoteMissingProfileDetail")
                : string.Format(
                    CultureInfo.CurrentCulture,
                    Resources.GetString("ExecutionTargetRemoteDetailFormat"),
                    state.Profile.DisplayName,
                    state.Profile.ModelId);
        }

        ApplyValidationState();
    }

    private void ApplyCurrentExecutionSelectionState()
    {
        IsCurrentProfileSelected = IsRemoteSelected
            && string.Equals(
                _activeRemoteProfileId,
                _profile?.ProfileId,
                StringComparison.Ordinal)
            && _activeRemoteInputMode == SelectedInputMode;
    }

    private void ApplyValidationState()
    {
        var profile = GetCurrentProfile();
        IsProfileValid = profile.ValidationState == RemoteApiProfileValidationState.Valid
            && profile.LastVerifiedAtUtc is not null;
        ValidationStatusText = profile.ValidationState switch
        {
            RemoteApiProfileValidationState.Valid when profile.LastVerifiedAtUtc is { } verified =>
                string.Format(
                    CultureInfo.CurrentCulture,
                    Resources.GetString("ApiValidationValidFormat"),
                    verified.ToLocalTime()),
            RemoteApiProfileValidationState.Invalid =>
                Resources.GetString("ApiValidationInvalid"),
            _ => Resources.GetString("ApiValidationUnverified"),
        };
        ApplyConsentState();
        OnPropertyChanged(nameof(CanEnableRemote));
    }

    private void ApplyConsentState()
    {
        if (_profile is not { } profile)
        {
            HasConsent = false;
            ConsentStatusText = Resources.GetString("ApiConsentMissingState");
            return;
        }

        HasConsent = profile.ConsentedInputMode is not null
            && profile.ConsentedDisclosureVersion == profile.DisclosureVersion
            && profile.ConsentGrantedAtUtc is not null;
        if (!HasConsent)
        {
            ConsentStatusText = Resources.GetString("ApiConsentMissingState");
            return;
        }

        ConsentStatusText = profile.ConsentedInputMode == SelectedInputMode
            ? string.Format(
                CultureInfo.CurrentCulture,
                Resources.GetString("ApiConsentGrantedFormat"),
                Resources.GetString(SelectedInputMode == RemoteInputMode.DirectImage
                    ? "ApiRemoteVisionModeName"
                    : "ApiRemoteOcrTextModeName"))
            : Resources.GetString("ApiConsentDifferentModeState");
    }

    private void ShowRemoteFailure(string errorCode)
    {
        var resourceName = errorCode switch
        {
            "remote.credential-rejected" => "ApiConnectionCredentialRejectedStatus",
            "remote.rate-limited" => "ApiConnectionRateLimitedStatus",
            "remote.timeout" => "ApiConnectionTimedOutStatus",
            "remote.network-failure" => "ApiConnectionNetworkFailedStatus",
            "remote.model-or-schema-rejected" => "ApiConnectionModelRejectedStatus",
            "remote.invalid-content-draft" => "ApiConnectionContentFailedStatus",
            "remote.invalid-response" or "remote.invalid-structured-output" =>
                "ApiConnectionSchemaFailedStatus",
            _ => "ApiConnectionFailedStatus",
        };
        ShowStatus(resourceName, SettingsStatusKind.Error);
    }

    private void ShowStatus(string resourceName, SettingsStatusKind kind)
    {
        StatusMessage = Resources.GetString(resourceName);
        StatusKind = kind;
    }

    private void ApplyPersistedOutputLanguage(AnalysisOutputLanguage outputLanguage)
    {
        _persistedOutputLanguage = outputLanguage;
        SelectedOutputLanguageIndex = OutputLanguageToIndex(outputLanguage);
    }

    private void ClearOutputLanguageSaveFailure()
    {
        if (StatusKind == SettingsStatusKind.Error
            && string.Equals(
                StatusMessage,
                Resources.GetString("ApiOutputLanguageSaveFailedStatus"),
                StringComparison.Ordinal))
        {
            StatusMessage = string.Empty;
            StatusKind = default;
        }
    }

    private static AnalysisOutputLanguage OutputLanguageFromIndex(int index) => index switch
    {
        0 => AnalysisOutputLanguage.ModelDefault,
        1 => AnalysisOutputLanguage.SimplifiedChinese,
        2 => AnalysisOutputLanguage.TraditionalChineseTaiwan,
        3 => AnalysisOutputLanguage.English,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    private static int OutputLanguageToIndex(AnalysisOutputLanguage outputLanguage) =>
        outputLanguage switch
        {
            AnalysisOutputLanguage.ModelDefault => 0,
            AnalysisOutputLanguage.SimplifiedChinese => 1,
            AnalysisOutputLanguage.TraditionalChineseTaiwan => 2,
            AnalysisOutputLanguage.English => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(outputLanguage)),
        };

    public void ShowOperationFailure() =>
        ShowStatus("ApiOperationFailedStatus", SettingsStatusKind.Error);

    private string ValidateModelId()
    {
        var normalized = ModelId.Trim();
        if (string.IsNullOrWhiteSpace(normalized)
            || normalized.Length > 200
            || normalized.Any(char.IsControl))
        {
            throw new RemoteApiProfileException("remote.model-id-invalid");
        }

        return normalized;
    }

    private void PopulateCategories()
    {
        CategoryOptions.Clear();
        CategoryOptions.Add(new(RemoteApiProviderCategory.InternationalOfficial,
            Resources.GetString("ApiCategoryInternational")));
        CategoryOptions.Add(new(RemoteApiProviderCategory.ChinaOfficial,
            Resources.GetString("ApiCategoryChina")));
        CategoryOptions.Add(new(RemoteApiProviderCategory.Aggregator,
            Resources.GetString("ApiCategoryAggregator")));
        CategoryOptions.Add(new(RemoteApiProviderCategory.LocalPrivate,
            Resources.GetString("ApiCategoryLocalPrivate")));
        CategoryOptions.Add(new(RemoteApiProviderCategory.Custom,
            Resources.GetString("ApiCategoryCustom")));
    }

    private void ApplyCategoryFilter(RemoteApiProviderCategory category)
    {
        ProviderOptions.Clear();
        foreach (var option in _allProviderOptions.Where(option => option.Category == category))
        {
            ProviderOptions.Add(option);
        }
    }

    private RemoteApiProfile GetCurrentProfile() =>
        _profile ?? throw new InvalidOperationException("No remote API profile is selected.");

    private IRemoteApiProfileService GetProfileService() =>
        _profileServiceAccessor()
        ?? throw new InvalidOperationException("The remote API profile service is unavailable.");

    private IRemoteApiCredentialService GetCredentialService() =>
        _credentialServiceAccessor()
        ?? throw new InvalidOperationException("The remote API credential service is unavailable.");

    private IRemoteApiConnectionTester GetConnectionTester() =>
        _connectionTesterAccessor()
        ?? throw new InvalidOperationException("The remote API connection tester is unavailable.");
}
