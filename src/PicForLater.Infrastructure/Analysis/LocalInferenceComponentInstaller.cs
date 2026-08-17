using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PicForLater.Infrastructure.Storage;

namespace PicForLater.Infrastructure.Analysis;

public enum LocalInferenceComponentInstallStage
{
    Preparing = 0,
    Downloading = 1,
    Verifying = 2,
    Installing = 3,
    Completed = 4,
}

public sealed record LocalInferenceComponentInstallProgress(
    LocalInferenceComponentInstallStage Stage,
    long DownloadedBytes,
    long TotalBytes);

public sealed record LocalInferenceComponentInstallResult(
    LocalInferenceComponent Component,
    bool DownloadWasRequired);

public sealed record LocalInferenceComponentReleaseSource(
    Uri ManifestUri,
    Uri SignatureUri,
    string RsaPublicKeyPem);

public sealed class LocalInferenceComponentInstallException : Exception
{
    public LocalInferenceComponentInstallException(string errorCode, Exception? innerException = null)
        : base("The optional local inference component could not be installed.", innerException)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}

public sealed class LocalInferenceComponentInstaller
{
    private const int ReleaseManifestSchemaVersion = 1;
    private const long MaximumReleaseManifestLength = 64 * 1024;
    private const long MaximumSignatureLength = 16 * 1024;
    private const long MaximumArchiveLength = 1536L * 1024 * 1024;
    private const long MaximumExpandedLength = 2L * 1024 * 1024 * 1024;
    private const int MaximumArchiveEntryCount = 513;
    private const int MaximumRedirectCount = 10;
    private const long DiskSpaceMarginBytes = 256L * 1024 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 16,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly AppDataPaths _paths;
    private readonly HttpClient _httpClient;
    private readonly LocalInferenceComponentLocator _locator;
    private readonly LocalInferenceComponentReleaseSource _source;
    private readonly string _architecture;
    private readonly TimeSpan _downloadInactivityTimeout;
    private readonly Func<CancellationToken, ValueTask<IAsyncDisposable>>? _acquireActivationLease;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public LocalInferenceComponentInstaller(
        AppDataPaths paths,
        HttpClient httpClient,
        LocalInferenceComponentLocator locator,
        LocalInferenceComponentReleaseSource source,
        string architecture,
        TimeSpan? downloadInactivityTimeout = null,
        Func<CancellationToken, ValueTask<IAsyncDisposable>>? acquireActivationLease = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        ArgumentException.ThrowIfNullOrWhiteSpace(architecture);
        if (!LocalInferenceComponentLocator.IsSafeName(architecture))
        {
            throw new ArgumentException("The component architecture is invalid.", nameof(architecture));
        }

        EnsureAllowedReleaseUri(_source.ManifestUri, allowRedirectTarget: false);
        EnsureAllowedReleaseUri(_source.SignatureUri, allowRedirectTarget: false);
        if (string.IsNullOrWhiteSpace(_source.RsaPublicKeyPem))
        {
            throw new ArgumentException("A component release public key is required.", nameof(source));
        }

        _architecture = architecture;
        _acquireActivationLease = acquireActivationLease;
        _downloadInactivityTimeout = downloadInactivityTimeout ?? TimeSpan.FromMinutes(2);
        if (_downloadInactivityTimeout <= TimeSpan.Zero
            || _downloadInactivityTimeout > TimeSpan.FromMinutes(30))
        {
            throw new ArgumentOutOfRangeException(nameof(downloadInactivityTimeout));
        }
    }

    public async Task<LocalInferenceComponentInstallResult> InstallOrRepairAsync(
        IProgress<LocalInferenceComponentInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? stagingRoot = null;
        try
        {
            progress?.Report(new LocalInferenceComponentInstallProgress(
                LocalInferenceComponentInstallStage.Preparing,
                0,
                0));

            var releaseManifestBytes = await DownloadSmallFileAsync(
                    _source.ManifestUri,
                    MaximumReleaseManifestLength,
                    cancellationToken)
                .ConfigureAwait(false);
            var signatureText = await DownloadSmallFileAsync(
                    _source.SignatureUri,
                    MaximumSignatureLength,
                    cancellationToken)
                .ConfigureAwait(false);
            VerifyReleaseManifestSignature(releaseManifestBytes, signatureText);

            var release = DeserializeAndValidateReleaseManifest(releaseManifestBytes);
            _locator.Invalidate();
            var existing = await _locator.LocateAsync(cancellationToken).ConfigureAwait(false);
            if (existing is not null
                && string.Equals(existing.Version, release.Version, StringComparison.Ordinal))
            {
                var existingManifestPath = Path.Combine(
                    existing.DirectoryPath,
                    LocalInferenceComponentLocator.ComponentManifestFileName);
                if (await FileHasHashAsync(
                        existingManifestPath,
                        release.ComponentManifestSha256,
                        cancellationToken)
                    .ConfigureAwait(false))
                {
                    progress?.Report(new LocalInferenceComponentInstallProgress(
                        LocalInferenceComponentInstallStage.Completed,
                        release.ArchiveLength,
                        release.ArchiveLength));
                    return new LocalInferenceComponentInstallResult(existing, DownloadWasRequired: false);
                }
            }

            EnsureDiskSpace(release.ArchiveLength, release.ComponentLength);
            stagingRoot = CreateStagingDirectory();
            var archivePath = Path.Combine(stagingRoot, release.ArchiveFileName);
            var archiveUri = new Uri(_source.ManifestUri, Uri.EscapeDataString(release.ArchiveFileName));
            EnsureAllowedReleaseUri(archiveUri, allowRedirectTarget: false);
            await DownloadArchiveAsync(
                    archiveUri,
                    archivePath,
                    release,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);

            progress?.Report(new LocalInferenceComponentInstallProgress(
                LocalInferenceComponentInstallStage.Verifying,
                release.ArchiveLength,
                release.ArchiveLength));
            var candidateRoot = Path.Combine(stagingRoot, "payload");
            Directory.CreateDirectory(candidateRoot);
            _paths.EnsureSafePath(candidateRoot);
            var expandedLength = await ExtractArchiveAsync(
                    archivePath,
                    candidateRoot,
                    cancellationToken)
                .ConfigureAwait(false);
            if (expandedLength != release.ComponentLength)
            {
                throw new LocalInferenceComponentInstallException(
                    "component.expanded-size-mismatch");
            }

            var componentManifestPath = Path.Combine(
                candidateRoot,
                LocalInferenceComponentLocator.ComponentManifestFileName);
            if (!await FileHasHashAsync(
                    componentManifestPath,
                    release.ComponentManifestSha256,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                throw new LocalInferenceComponentInstallException(
                    "component.component-manifest-hash-mismatch");
            }

            _ = await _locator.ValidateComponentDirectoryAsync(
                    candidateRoot,
                    release.Version,
                    cancellationToken)
                .ConfigureAwait(false);

            progress?.Report(new LocalInferenceComponentInstallProgress(
                LocalInferenceComponentInstallStage.Installing,
                release.ArchiveLength,
                release.ArchiveLength));
            IAsyncDisposable? activationLease = null;
            try
            {
                if (_acquireActivationLease is not null)
                {
                    activationLease = await _acquireActivationLease(cancellationToken)
                        .ConfigureAwait(false);
                }

                var installed = await ActivateCandidateAsync(
                        candidateRoot,
                        release.Version,
                        cancellationToken)
                    .ConfigureAwait(false);
                progress?.Report(new LocalInferenceComponentInstallProgress(
                    LocalInferenceComponentInstallStage.Completed,
                    release.ArchiveLength,
                    release.ArchiveLength));
                return new LocalInferenceComponentInstallResult(installed, DownloadWasRequired: true);
            }
            finally
            {
                if (activationLease is not null)
                {
                    await activationLease.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LocalInferenceComponentInstallException)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw new LocalInferenceComponentInstallException(
                "component.download-network-failed",
                exception);
        }
        catch (JsonException exception)
        {
            throw new LocalInferenceComponentInstallException(
                "component.release-manifest-invalid",
                exception);
        }
        catch (CryptographicException exception)
        {
            throw new LocalInferenceComponentInstallException(
                "component.signature-invalid",
                exception);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidDataException
                                          or InvalidOperationException
                                          or NotSupportedException)
        {
            throw new LocalInferenceComponentInstallException(
                "component.install-failed",
                exception);
        }
        finally
        {
            if (stagingRoot is not null)
            {
                TryDeleteDirectory(stagingRoot);
            }

            _operationGate.Release();
        }
    }

    private async Task<LocalInferenceComponent> ActivateCandidateAsync(
        string candidateRoot,
        string version,
        CancellationToken cancellationToken)
    {
        var architectureRoot = Path.Combine(
            _paths.LocalInferenceComponentsDirectoryPath,
            _architecture);
        _paths.EnsureSafePath(architectureRoot);
        Directory.CreateDirectory(architectureRoot);
        _paths.EnsureSafePath(architectureRoot);
        var targetRoot = Path.Combine(architectureRoot, version);
        _paths.EnsureSafePath(targetRoot);
        var activePath = Path.Combine(
            architectureRoot,
            LocalInferenceComponentLocator.ActiveManifestFileName);
        byte[]? previousActiveManifest = File.Exists(activePath)
            ? await File.ReadAllBytesAsync(activePath, cancellationToken).ConfigureAwait(false)
            : null;
        string? backupRoot = null;
        var candidateWasMoved = false;
        try
        {
            if (Directory.Exists(targetRoot))
            {
                backupRoot = Path.Combine(
                    architectureRoot,
                    $"{version}.repair-{Guid.NewGuid():N}");
                _paths.EnsureSafePath(backupRoot);
                Directory.Move(targetRoot, backupRoot);
            }

            Directory.Move(candidateRoot, targetRoot);
            candidateWasMoved = true;
            await WriteActiveManifestAsync(architectureRoot, version, cancellationToken)
                .ConfigureAwait(false);
            _locator.Invalidate();
            var installed = await _locator.LocateAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    "The activated local inference component failed final validation.");
            if (backupRoot is not null)
            {
                TryDeleteDirectory(backupRoot);
            }

            DeleteInactiveVersions(architectureRoot, targetRoot);

            return installed;
        }
        catch
        {
            string? failedCandidateRoot = null;
            if (candidateWasMoved && Directory.Exists(targetRoot))
            {
                failedCandidateRoot = Path.Combine(
                    architectureRoot,
                    $"{version}.failed-{Guid.NewGuid():N}");
                _paths.EnsureSafePath(failedCandidateRoot);
                Directory.Move(targetRoot, failedCandidateRoot);
            }

            if (backupRoot is not null
                && Directory.Exists(backupRoot)
                && !Directory.Exists(targetRoot))
            {
                Directory.Move(backupRoot, targetRoot);
            }

            await RestoreActiveManifestAsync(
                    architectureRoot,
                    activePath,
                    previousActiveManifest)
                .ConfigureAwait(false);

            _locator.Invalidate();
            if (failedCandidateRoot is not null)
            {
                TryDeleteDirectory(failedCandidateRoot);
            }

            throw;
        }
    }

    private void DeleteInactiveVersions(string architectureRoot, string activeComponentRoot)
    {
        foreach (var directoryPath in Directory.EnumerateDirectories(
                     architectureRoot,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            if (directoryPath.Equals(activeComponentRoot, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            TryDeleteDirectory(directoryPath);
        }
    }

    private async Task RestoreActiveManifestAsync(
        string architectureRoot,
        string activePath,
        byte[]? previousActiveManifest)
    {
        if (previousActiveManifest is null)
        {
            if (File.Exists(activePath))
            {
                File.Delete(activePath);
            }

            return;
        }

        var temporaryPath = Path.Combine(
            architectureRoot,
            $"active-rollback-{Guid.NewGuid():N}.tmp");
        _paths.EnsureSafePath(temporaryPath);
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, previousActiveManifest)
                .ConfigureAwait(false);
            File.Move(temporaryPath, activePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private async Task WriteActiveManifestAsync(
        string architectureRoot,
        string version,
        CancellationToken cancellationToken)
    {
        var activePath = Path.Combine(
            architectureRoot,
            LocalInferenceComponentLocator.ActiveManifestFileName);
        var temporaryPath = Path.Combine(
            architectureRoot,
            $"active-{Guid.NewGuid():N}.tmp");
        _paths.EnsureSafePath(activePath);
        _paths.EnsureSafePath(temporaryPath);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new ActiveComponentManifest(ReleaseManifestSchemaVersion, version),
            SerializerOptions);
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, activePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private async Task DownloadArchiveAsync(
        Uri uri,
        string destinationPath,
        ReleaseManifest release,
        IProgress<LocalInferenceComponentInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var transferCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        transferCancellation.CancelAfter(_downloadInactivityTimeout);
        try
        {
            using var response = await SendWithAllowedRedirectsAsync(
                    uri,
                    transferCancellation.Token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long contentLength
                && contentLength != release.ArchiveLength)
            {
                throw new LocalInferenceComponentInstallException(
                    "component.archive-size-mismatch");
            }

            await using var source = await response.Content.ReadAsStreamAsync(
                    transferCancellation.Token)
                .ConfigureAwait(false);
            await using var destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
            var downloaded = 0L;
            try
            {
                while (true)
                {
                    transferCancellation.CancelAfter(_downloadInactivityTimeout);
                    var read = await source.ReadAsync(
                            buffer.AsMemory(0, buffer.Length),
                            transferCancellation.Token)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    downloaded = checked(downloaded + read);
                    if (downloaded > release.ArchiveLength)
                    {
                        throw new LocalInferenceComponentInstallException(
                            "component.archive-size-mismatch");
                    }

                    hash.AppendData(buffer, 0, read);
                    await destination.WriteAsync(
                            buffer.AsMemory(0, read),
                            cancellationToken)
                        .ConfigureAwait(false);
                    progress?.Report(new LocalInferenceComponentInstallProgress(
                        LocalInferenceComponentInstallStage.Downloading,
                        downloaded,
                        release.ArchiveLength));
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }

            var actualHash = Convert.ToHexString(hash.GetHashAndReset());
            if (downloaded != release.ArchiveLength
                || !actualHash.Equals(release.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new LocalInferenceComponentInstallException(
                    downloaded == release.ArchiveLength
                        ? "component.archive-hash-mismatch"
                        : "component.archive-size-mismatch");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LocalInferenceComponentInstallException("component.download-timeout");
        }
    }

    private async Task<byte[]> DownloadSmallFileAsync(
        Uri uri,
        long maximumLength,
        CancellationToken cancellationToken)
    {
        using var transferCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        transferCancellation.CancelAfter(_downloadInactivityTimeout);
        try
        {
            using var response = await SendWithAllowedRedirectsAsync(
                    uri,
                    transferCancellation.Token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long contentLength
                && (contentLength <= 0 || contentLength > maximumLength))
            {
                throw new InvalidDataException("The component release metadata is too large.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(
                    transferCancellation.Token)
                .ConfigureAwait(false);
            using var destination = new MemoryStream();
            var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
            try
            {
                while (true)
                {
                    transferCancellation.CancelAfter(_downloadInactivityTimeout);
                    var read = await stream.ReadAsync(
                            buffer.AsMemory(0, buffer.Length),
                            transferCancellation.Token)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    if (destination.Length + read > maximumLength)
                    {
                        throw new InvalidDataException(
                            "The component release metadata is too large.");
                    }

                    destination.Write(buffer, 0, read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }

            if (destination.Length == 0)
            {
                throw new InvalidDataException("The component release metadata is empty.");
            }

            return destination.ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LocalInferenceComponentInstallException("component.download-timeout");
        }
    }

    private void VerifyReleaseManifestSignature(byte[] manifestBytes, byte[] signatureText)
    {
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(Encoding.UTF8.GetString(signatureText).Trim());
        }
        catch (FormatException exception)
        {
            throw new CryptographicException("The component signature is invalid.", exception);
        }

        using var rsa = RSA.Create();
        rsa.ImportFromPem(_source.RsaPublicKeyPem);
        if (!rsa.VerifyData(
                manifestBytes,
                signature,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss))
        {
            throw new CryptographicException("The component release manifest signature is invalid.");
        }
    }

    private ReleaseManifest DeserializeAndValidateReleaseManifest(byte[] bytes)
    {
        var release = JsonSerializer.Deserialize<ReleaseManifest>(bytes, SerializerOptions)
                      ?? throw new InvalidDataException("The component release manifest is empty.");
        var expectedArchiveName = $"PicForLater.LocalInference-{_architecture}-{release.Version}.zip";
        if (release.SchemaVersion != ReleaseManifestSchemaVersion
            || !string.Equals(
                release.ComponentId,
                LocalInferenceComponentLocator.ComponentId,
                StringComparison.Ordinal)
            || !LocalInferenceComponentLocator.IsSafeName(release.Version)
            || !string.Equals(release.Architecture, _architecture, StringComparison.Ordinal)
            || release.ProtocolMinimumVersion <= 0
            || release.ProtocolMaximumVersion < release.ProtocolMinimumVersion
            || !_locator.IsProtocolCompatible(
                release.ProtocolMinimumVersion,
                release.ProtocolMaximumVersion)
            || !string.Equals(release.ArchiveFileName, expectedArchiveName, StringComparison.Ordinal)
            || release.ArchiveLength <= 0
            || release.ArchiveLength > MaximumArchiveLength
            || release.ComponentLength <= 0
            || release.ComponentLength > MaximumExpandedLength
            || !IsSha256(release.ArchiveSha256)
            || !IsSha256(release.ComponentManifestSha256))
        {
            throw new LocalInferenceComponentInstallException(
                "component.release-manifest-incompatible");
        }

        return release;
    }

    private async Task<long> ExtractArchiveAsync(
        string archivePath,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count is 0 or > MaximumArchiveEntryCount)
        {
            throw new InvalidDataException("The component archive contains an invalid number of entries.");
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var expandedLength = 0L;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = entry.FullName;
            var isDirectory = relativePath.EndsWith('/');
            var canonicalPath = isDirectory ? relativePath.TrimEnd('/') : relativePath;
            if (!LocalInferenceComponentLocator.IsSafeRelativePath(canonicalPath)
                || !paths.Add(canonicalPath)
                || IsSymbolicLink(entry))
            {
                throw new InvalidDataException("The component archive contains an unsafe entry.");
            }

            var destinationPath = Path.GetFullPath(Path.Combine(
                destinationRoot,
                canonicalPath.Replace('/', Path.DirectorySeparatorChar)));
            if (!destinationPath.StartsWith(
                    destinationRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("A component archive entry escapes the staging root.");
            }

            _paths.EnsureSafePath(destinationPath);
            if (isDirectory)
            {
                if (entry.Length != 0)
                {
                    throw new InvalidDataException("A component archive directory has payload data.");
                }

                Directory.CreateDirectory(destinationPath);
                continue;
            }

            if (entry.Length > MaximumExpandedLength - expandedLength)
            {
                throw new InvalidDataException("The expanded component archive is too large.");
            }

            expandedLength += entry.Length;

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            _paths.EnsureSafePath(destinationPath);
            await using var source = entry.Open();
            await using var destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
            var actualEntryLength = 0L;
            try
            {
                while (true)
                {
                    var read = await source.ReadAsync(
                            buffer.AsMemory(0, buffer.Length),
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    actualEntryLength = checked(actualEntryLength + read);
                    if (actualEntryLength > entry.Length)
                    {
                        throw new InvalidDataException(
                            "A component archive entry exceeds its declared length.");
                    }

                    await destination.WriteAsync(
                            buffer.AsMemory(0, read),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }

            if (actualEntryLength != entry.Length)
            {
                throw new InvalidDataException(
                    "A component archive entry does not match its declared length.");
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return expandedLength;
    }

    private static bool IsSymbolicLink(ZipArchiveEntry entry)
    {
        var unixFileType = (entry.ExternalAttributes >> 16) & 0xF000;
        return unixFileType == 0xA000
               || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0;
    }

    private async Task<HttpResponseMessage> SendWithAllowedRedirectsAsync(
        Uri initialUri,
        CancellationToken cancellationToken)
    {
        var uri = initialUri;
        for (var redirectCount = 0; redirectCount <= MaximumRedirectCount; redirectCount++)
        {
            using var request = CreateRequest(uri, allowRedirectTarget: redirectCount > 0);
            var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            if ((int)response.StatusCode is < 300 or >= 400)
            {
                return response;
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null || redirectCount == MaximumRedirectCount)
            {
                throw new LocalInferenceComponentInstallException(
                    "component.download-redirect-rejected");
            }

            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
            EnsureAllowedReleaseUri(uri, allowRedirectTarget: true);
        }

        throw new LocalInferenceComponentInstallException(
            "component.download-redirect-rejected");
    }

    private static HttpRequestMessage CreateRequest(Uri uri, bool allowRedirectTarget)
    {
        EnsureAllowedReleaseUri(uri, allowRedirectTarget);
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("PicForLater/1.0 local-component-downloader");
        return request;
    }

    private static void EnsureAllowedReleaseUri(Uri uri, bool allowRedirectTarget)
    {
        if (!uri.IsAbsoluteUri
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !uri.IsDefaultPort)
        {
            throw new LocalInferenceComponentInstallException("component.download-uri-rejected");
        }

        var isRepositoryRelease = uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                                  && uri.AbsolutePath.StartsWith(
                                      "/dogdreamson555/PicForLater/releases/",
                                      StringComparison.Ordinal);
        var isRedirectTarget = allowRedirectTarget
                               && (uri.Host.Equals(
                                       "objects.githubusercontent.com",
                                       StringComparison.OrdinalIgnoreCase)
                                   || uri.Host.Equals(
                                       "release-assets.githubusercontent.com",
                                       StringComparison.OrdinalIgnoreCase));
        if (!isRepositoryRelease && !isRedirectTarget)
        {
            throw new LocalInferenceComponentInstallException("component.download-uri-rejected");
        }
    }

    private static async Task<bool> FileHasHashAsync(
        string path,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).Equals(expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    private void EnsureDiskSpace(long archiveLength, long componentLength)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(_paths.RootPath))
                   ?? throw new InvalidOperationException("The component staging volume is unavailable.");
        var required = checked(archiveLength + componentLength + DiskSpaceMarginBytes);
        if (new DriveInfo(root).AvailableFreeSpace < required)
        {
            throw new LocalInferenceComponentInstallException(
                "component.insufficient-disk-space");
        }
    }

    private string CreateStagingDirectory()
    {
        Directory.CreateDirectory(_paths.LocalInferenceComponentStagingDirectoryPath);
        _paths.EnsureSafePath(_paths.LocalInferenceComponentStagingDirectoryPath);
        var path = Path.Combine(
            _paths.LocalInferenceComponentStagingDirectoryPath,
            Guid.NewGuid().ToString("N"));
        _paths.EnsureSafePath(path);
        Directory.CreateDirectory(path);
        _paths.EnsureSafePath(path);
        return path;
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                ValidateTreeForDeletion(path);
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (InvalidDataException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void ValidateTreeForDeletion(string root)
    {
        var directories = new Stack<string>();
        directories.Push(root);
        while (directories.Count > 0)
        {
            var directoryPath = directories.Pop();
            _paths.EnsureSafePath(directoryPath);
            foreach (var path in Directory.EnumerateFileSystemEntries(
                         directoryPath,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                _paths.EnsureSafePath(path);
                if (Directory.Exists(path))
                {
                    directories.Push(path);
                }
            }
        }
    }

    private sealed record ActiveComponentManifest(int SchemaVersion, string Version);

    private sealed record ReleaseManifest(
        int SchemaVersion,
        string ComponentId,
        string Version,
        string Architecture,
        int ProtocolMinimumVersion,
        int ProtocolMaximumVersion,
        string ArchiveFileName,
        long ArchiveLength,
        long ComponentLength,
        string ArchiveSha256,
        string ComponentManifestSha256);
}
