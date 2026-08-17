using PicForLater.Core.Analysis;

namespace PicForLater.App.Services;

#if PICFORLATER_UI_TESTING
internal sealed class UiTestRemoteApiCredentialService : IRemoteApiCredentialService
{
    private readonly Dictionary<string, string> _credentials = new(StringComparer.Ordinal);

    public Task StoreAsync(
        string credentialReference,
        string secret,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        _credentials[credentialReference] = secret;
        return Task.CompletedTask;
    }

    public Task<string?> RetrieveAsync(
        string credentialReference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _credentials.TryGetValue(credentialReference, out var secret);
        return Task.FromResult(secret);
    }

    public Task<bool> ExistsAsync(
        string credentialReference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_credentials.ContainsKey(credentialReference));
    }

    public Task DeleteAsync(
        string credentialReference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _credentials.Remove(credentialReference);
        return Task.CompletedTask;
    }
}

internal sealed class UiTestRemoteApiConnectionTester : IRemoteApiConnectionTester
{
    public Task TestAsync(
        RemoteApiProfile profile,
        RemoteInputMode inputMode,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(profile);
        if (!profile.SupportedInputModes.Contains(inputMode))
        {
            throw new RemoteAnalysisProviderException(
                "remote.input-mode-not-supported",
                isRetryable: false);
        }

        return Task.CompletedTask;
    }
}
#endif
