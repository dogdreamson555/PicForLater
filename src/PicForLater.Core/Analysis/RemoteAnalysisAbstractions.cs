namespace PicForLater.Core.Analysis;

public interface IRemoteApiProfileService
{
    Task<RemoteAnalysisExecutionState> GetExecutionStateAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RemoteApiProfile>> GetProfilesAsync(
        CancellationToken cancellationToken = default);

    Task<RemoteApiProfile?> GetProfileAsync(
        string profileId,
        CancellationToken cancellationToken = default);

    Task<RemoteApiProfile> SaveProfileAsync(
        RemoteApiProfile profile,
        CancellationToken cancellationToken = default);

    Task DeleteProfileAsync(
        string profileId,
        CancellationToken cancellationToken = default);

    Task SetOutputLanguageAsync(
        AnalysisOutputLanguage outputLanguage,
        CancellationToken cancellationToken = default);

    Task SelectLocalAsync(CancellationToken cancellationToken = default);

    Task SelectRemoteAsync(
        string profileId,
        RemoteInputMode inputMode,
        CancellationToken cancellationToken = default);
}

public interface IRemoteApiCredentialService
{
    Task StoreAsync(
        string credentialReference,
        string secret,
        CancellationToken cancellationToken = default);

    Task<string?> RetrieveAsync(
        string credentialReference,
        CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(
        string credentialReference,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        string credentialReference,
        CancellationToken cancellationToken = default);
}

public interface IRemoteApiConnectionTester
{
    Task TestAsync(
        RemoteApiProfile profile,
        RemoteInputMode inputMode,
        CancellationToken cancellationToken = default);
}

public interface IRemoteApiRequestAuthorizer
{
    Task EnsureAuthorizedAsync(
        RemoteApiProfileSnapshot profileSnapshot,
        RemoteInputMode inputMode,
        CancellationToken cancellationToken = default);
}
