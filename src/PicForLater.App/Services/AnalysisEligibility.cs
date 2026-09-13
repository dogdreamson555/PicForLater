using PicForLater.Core.Analysis;

namespace PicForLater.App.Services;

/// <summary>
/// Contains the side-effect-free qualification rules shared by the settings page
/// and the system-tray analysis menu.
/// </summary>
internal static class AnalysisEligibility
{
    internal static async Task<List<(RemoteApiProfile Profile, RemoteInputMode Mode)>>
        GetEligibleProfilesAsync(
            IRemoteApiProfileService profiles,
            IRemoteApiCredentialService? credentials,
            bool localOcrAvailable,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        var eligibleProfiles = new List<(RemoteApiProfile Profile, RemoteInputMode Mode)>();
        foreach (var profile in await profiles.GetProfilesAsync(cancellationToken)
                     .ConfigureAwait(false))
        {
            if (!TryGetEligibleMode(profile, out var mode)
                || !IsInputModeAvailable(mode, localOcrAvailable))
            {
                continue;
            }

            var hasCredential = profile.AuthenticationKind == RemoteApiAuthenticationKind.None
                || credentials is not null
                && await credentials.ExistsAsync(
                        profile.CredentialReference,
                        cancellationToken)
                    .ConfigureAwait(false);
            if (hasCredential)
            {
                eligibleProfiles.Add((profile, mode));
            }
        }

        return eligibleProfiles;
    }

    internal static (RemoteApiProfile Profile, RemoteInputMode Mode)?
        ResolveEligibleSelection(
            RemoteAnalysisExecutionState state,
            IReadOnlyList<(RemoteApiProfile Profile, RemoteInputMode Mode)> eligibleProfiles)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(eligibleProfiles);

        if (state.Settings.RemoteApiProfileId is { Length: > 0 } rememberedProfileId
            && state.Settings.RemoteInputMode is { } rememberedMode)
        {
            foreach (var selection in eligibleProfiles)
            {
                if (selection.Profile.ProfileId == rememberedProfileId
                    && selection.Mode == rememberedMode)
                {
                    return selection;
                }
            }
        }

        return eligibleProfiles.Count == 1 ? eligibleProfiles[0] : null;
    }

    internal static bool IsEligibleCurrentSelection(
        RemoteAnalysisExecutionState state,
        IReadOnlyList<(RemoteApiProfile Profile, RemoteInputMode Mode)> eligibleProfiles)
    {
        if (state.Settings.Backend != AnalysisExecutionBackend.RemoteApi
            || state.Settings.RemoteApiProfileId is not { Length: > 0 } profileId
            || state.Settings.RemoteInputMode is not { } inputMode)
        {
            return false;
        }

        return eligibleProfiles.Any(selection =>
            selection.Profile.ProfileId == profileId
            && selection.Mode == inputMode);
    }

    internal static bool IsInputModeAvailable(
        RemoteInputMode mode,
        bool localOcrAvailable) =>
        mode != RemoteInputMode.LocalOcrText || localOcrAvailable;

    internal static bool TryGetEligibleMode(
        RemoteApiProfile profile,
        out RemoteInputMode mode)
    {
        ArgumentNullException.ThrowIfNull(profile);
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
