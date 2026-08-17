using Windows.Security.Credentials;
using PicForLater.Core.Analysis;

namespace PicForLater.App.Services;

public sealed class WindowsCredentialLockerService : IRemoteApiCredentialService
{
    private const string ResourceName = "PicForLater.RemoteApi";
    private const int ElementNotFoundHResult = unchecked((int)0x80070490);
    private const int MaximumCredentialReferenceLength = 200;
    private const int MaximumSecretLength = 8_192;

    private readonly PasswordVault _vault = new();
    private readonly object _vaultLock = new();

    public Task StoreAsync(
        string credentialReference,
        string secret,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        credentialReference = ValidateCredentialReference(credentialReference);
        ValidateSecret(secret);

        lock (_vaultLock)
        {
            var existing = TryRetrieveCredential(credentialReference);
            if (existing is not null)
            {
                _vault.Remove(existing);
            }

            _vault.Add(new PasswordCredential(ResourceName, credentialReference, secret));
        }

        return Task.CompletedTask;
    }

    public Task<string?> RetrieveAsync(
        string credentialReference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        credentialReference = ValidateCredentialReference(credentialReference);
        lock (_vaultLock)
        {
            var credential = TryRetrieveCredential(credentialReference);
            if (credential is null)
            {
                return Task.FromResult<string?>(null);
            }

            credential.RetrievePassword();
            return Task.FromResult<string?>(credential.Password);
        }
    }

    public Task<bool> ExistsAsync(
        string credentialReference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        credentialReference = ValidateCredentialReference(credentialReference);
        lock (_vaultLock)
        {
            return Task.FromResult(TryRetrieveCredential(credentialReference) is not null);
        }
    }

    public Task DeleteAsync(
        string credentialReference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        credentialReference = ValidateCredentialReference(credentialReference);
        lock (_vaultLock)
        {
            var credential = TryRetrieveCredential(credentialReference);
            if (credential is not null)
            {
                _vault.Remove(credential);
            }
        }

        return Task.CompletedTask;
    }

    private PasswordCredential? TryRetrieveCredential(string credentialReference)
    {
        try
        {
            return _vault.Retrieve(ResourceName, credentialReference);
        }
        catch (Exception exception) when (exception.HResult == ElementNotFoundHResult)
        {
            return null;
        }
    }

    private static string ValidateCredentialReference(string credentialReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialReference);
        var normalized = credentialReference.Trim();
        if (normalized.Length > MaximumCredentialReferenceLength
            || normalized.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The credential reference is invalid.",
                nameof(credentialReference));
        }

        return normalized;
    }

    private static void ValidateSecret(string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        if (secret.Length > MaximumSecretLength
            || secret.IndexOfAny(['\r', '\n']) >= 0)
        {
            throw new ArgumentException("The credential is invalid.", nameof(secret));
        }
    }
}
