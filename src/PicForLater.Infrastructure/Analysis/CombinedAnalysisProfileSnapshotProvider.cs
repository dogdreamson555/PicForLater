using PicForLater.Core.Analysis;

namespace PicForLater.Infrastructure.Analysis;

public sealed class CombinedAnalysisProfileSnapshotProvider : IAnalysisProfileSnapshotProvider
{
    private const int MaximumConsistencyAttempts = 3;

    private readonly IAnalysisProfileSnapshotProvider _localProfileProvider;
    private readonly IRemoteApiProfileService _remoteProfileService;

    public CombinedAnalysisProfileSnapshotProvider(
        IAnalysisProfileSnapshotProvider localProfileProvider,
        IRemoteApiProfileService remoteProfileService)
    {
        _localProfileProvider = localProfileProvider
            ?? throw new ArgumentNullException(nameof(localProfileProvider));
        _remoteProfileService = remoteProfileService
            ?? throw new ArgumentNullException(nameof(remoteProfileService));
    }

    public async Task<ModelProfileSnapshot> GetCurrentSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < MaximumConsistencyAttempts; attempt++)
        {
            var localSnapshot = await _localProfileProvider.GetCurrentSnapshotAsync(cancellationToken)
                .ConfigureAwait(false);
            var remoteState = await _remoteProfileService.GetExecutionStateAsync(cancellationToken)
                .ConfigureAwait(false);
            if (localSnapshot.Revision != remoteState.Settings.Revision)
            {
                continue;
            }

            return CreateSnapshot(localSnapshot, remoteState);
        }

        throw new InvalidDataException(
            "The analysis profile changed repeatedly while a job snapshot was being created.");
    }

    private static ModelProfileSnapshot CreateSnapshot(
        ModelProfileSnapshot localSnapshot,
        RemoteAnalysisExecutionState remoteState)
    {
        if (remoteState.Settings.Backend == AnalysisExecutionBackend.Local)
        {
            // AnalysisSettings retains the last remote selection so the user can switch
            // back without reconfiguring it. It is deliberately excluded from local jobs.
            return localSnapshot with
            {
                ExecutionBackend = AnalysisExecutionBackend.Local,
                RemoteInputMode = null,
                RemoteApiProfile = null,
            };
        }

        if (remoteState.Settings.Backend != AnalysisExecutionBackend.RemoteApi
            || remoteState.Settings.RemoteInputMode is null
            || string.IsNullOrWhiteSpace(remoteState.Settings.RemoteApiProfileId)
            || remoteState.Profile is null
            || remoteState.Profile.ProfileId != remoteState.Settings.RemoteApiProfileId)
        {
            throw new InvalidDataException("The remote analysis execution settings are incomplete.");
        }

        var profile = remoteState.Profile;
        var inputMode = remoteState.Settings.RemoteInputMode.Value;
        if (!profile.IsEnabled)
        {
            throw new RemoteApiProfileException("remote.profile-disabled");
        }

        if (profile.ValidationState != RemoteApiProfileValidationState.Valid
            || profile.LastVerifiedAtUtc is null)
        {
            throw new RemoteApiProfileException("remote.profile-not-verified");
        }

        if (!profile.SupportedInputModes.Contains(inputMode))
        {
            throw new RemoteApiProfileException("remote.input-mode-not-supported");
        }

        if (profile.ConsentedInputMode != inputMode
            || profile.ConsentGrantedAtUtc is null
            || profile.ConsentedDisclosureVersion != profile.DisclosureVersion)
        {
            throw new RemoteApiProfileException("remote.consent-required");
        }

        return localSnapshot with
        {
            ExecutionBackend = AnalysisExecutionBackend.RemoteApi,
            RemoteInputMode = inputMode,
            RemoteApiProfile = new RemoteApiProfileSnapshot
            {
                ProfileId = profile.ProfileId,
                ProviderId = profile.ProviderId,
                EndpointId = profile.EndpointId,
                BaseUri = profile.BaseUri,
                ModelId = profile.ModelId,
                PromptVersion = profile.PromptVersion,
                OutputSchemaVersion = profile.OutputSchemaVersion,
                MaxTextChars = profile.MaxTextChars,
                MaxImageBytes = profile.MaxImageBytes,
                MaxOutputTokens = profile.MaxOutputTokens,
                TimeoutSeconds = profile.TimeoutSeconds,
                CredentialReference = profile.CredentialReference,
                ConsentVersion = profile.ConsentedDisclosureVersion,
                Protocol = profile.Protocol,
                AuthenticationKind = profile.AuthenticationKind,
                StructuredOutputMode = profile.StructuredOutputMode,
                EndpointTrustMode = profile.EndpointTrustMode,
                ApiVersion = profile.ApiVersion,
                DisableProviderFallbacks = profile.DisableProviderFallbacks,
                DisableExternalSearch = profile.DisableExternalSearch,
                ReasoningMode = profile.ReasoningMode,
                ReasoningWireFormat = profile.ReasoningWireFormat,
            },
        };
    }
}
