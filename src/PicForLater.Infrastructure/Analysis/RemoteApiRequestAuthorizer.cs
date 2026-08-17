using PicForLater.Core.Analysis;

namespace PicForLater.Infrastructure.Analysis;

public sealed class RemoteApiRequestAuthorizer(
    IRemoteApiProfileService profileService)
    : IRemoteApiRequestAuthorizer
{
    private readonly IRemoteApiProfileService _profileService = profileService
        ?? throw new ArgumentNullException(nameof(profileService));

    public async Task EnsureAuthorizedAsync(
        RemoteApiProfileSnapshot profileSnapshot,
        RemoteInputMode inputMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profileSnapshot);
        var current = await _profileService.GetProfileAsync(
            profileSnapshot.ProfileId,
            cancellationToken).ConfigureAwait(false)
            ?? throw Rejected("remote.profile-unavailable");
        if (!current.IsEnabled)
        {
            throw Rejected("remote.profile-disabled");
        }

        if (current.ValidationState != RemoteApiProfileValidationState.Valid
            || current.LastVerifiedAtUtc is null)
        {
            throw Rejected("remote.profile-not-verified");
        }

        if (!current.SupportedInputModes.Contains(inputMode))
        {
            throw Rejected("remote.input-mode-not-supported");
        }

        if (current.ConsentedInputMode != inputMode
            || current.ConsentGrantedAtUtc is null
            || !string.Equals(
                current.ConsentedDisclosureVersion,
                current.DisclosureVersion,
                StringComparison.Ordinal)
            || !string.Equals(
                current.ConsentedDisclosureVersion,
                profileSnapshot.ConsentVersion,
                StringComparison.Ordinal))
        {
            throw Rejected("remote.consent-required");
        }

        if (!SnapshotScopeMatches(current, profileSnapshot))
        {
            throw Rejected("remote.profile-snapshot-stale");
        }
    }

    private static bool SnapshotScopeMatches(
        RemoteApiProfile current,
        RemoteApiProfileSnapshot snapshot) =>
        string.Equals(current.ProviderId, snapshot.ProviderId, StringComparison.Ordinal)
        && string.Equals(current.EndpointId, snapshot.EndpointId, StringComparison.Ordinal)
        && current.BaseUri == snapshot.BaseUri
        && string.Equals(current.ModelId, snapshot.ModelId, StringComparison.Ordinal)
        && string.Equals(current.PromptVersion, snapshot.PromptVersion, StringComparison.Ordinal)
        && string.Equals(
            current.OutputSchemaVersion,
            snapshot.OutputSchemaVersion,
            StringComparison.Ordinal)
        && current.MaxTextChars == snapshot.MaxTextChars
        && current.MaxImageBytes == snapshot.MaxImageBytes
        && current.MaxOutputTokens == snapshot.MaxOutputTokens
        && current.TimeoutSeconds == snapshot.TimeoutSeconds
        && current.Protocol == snapshot.Protocol
        && current.AuthenticationKind == snapshot.AuthenticationKind
        && current.StructuredOutputMode == snapshot.StructuredOutputMode
        && current.EndpointTrustMode == snapshot.EndpointTrustMode
        && string.Equals(current.ApiVersion, snapshot.ApiVersion, StringComparison.Ordinal)
        && current.DisableProviderFallbacks == snapshot.DisableProviderFallbacks
        && current.DisableExternalSearch == snapshot.DisableExternalSearch
        && current.ReasoningMode == snapshot.ReasoningMode
        && current.ReasoningWireFormat == snapshot.ReasoningWireFormat
        && string.Equals(
            current.CredentialReference,
            snapshot.CredentialReference,
            StringComparison.Ordinal);

    private static RemoteAnalysisProviderException Rejected(string errorCode) =>
        new(errorCode, isRetryable: false);
}
