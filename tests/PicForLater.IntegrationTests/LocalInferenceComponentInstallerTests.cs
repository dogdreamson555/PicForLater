using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PicForLater.Infrastructure.Analysis;
using PicForLater.Infrastructure.Storage;

namespace PicForLater.IntegrationTests;

public sealed class LocalInferenceComponentInstallerTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    [Fact]
    public async Task InstallOrRepairAsync_InstallsSignedArchiveAndSkipsRepeatedDownload()
    {
        using var root = new TemporaryAppDataRoot();
        root.Paths.EnsureCreated();
        using var signingKey = RSA.Create(3072);
        var bundle = CreateBundle(signingKey, "1.2.3");
        using var handler = bundle.CreateHandler();
        using var httpClient = new HttpClient(handler);
        var locator = CreateLocator(root.Paths);
        var installer = CreateInstaller(root.Paths, httpClient, locator, bundle, signingKey);

        var first = await installer.InstallOrRepairAsync();
        var second = await installer.InstallOrRepairAsync();

        Assert.True(first.DownloadWasRequired);
        Assert.False(second.DownloadWasRequired);
        Assert.Equal("1.2.3", first.Component.Version);
        Assert.Equal("1.2.3", second.Component.Version);
        Assert.Equal(1, handler.RequestCount(bundle.ArchiveUri));
        Assert.Equal(2, handler.RequestCount(bundle.ManifestUri));
        Assert.Equal(2, handler.RequestCount(bundle.SignatureUri));
        Assert.Equal(
            "fake-worker",
            await File.ReadAllTextAsync(first.Component.WorkerPath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(
            root.Paths.LocalInferenceComponentStagingDirectoryPath));
    }

    [Fact]
    public async Task InstallOrRepairAsync_RejectsInvalidSignatureBeforeArchiveDownload()
    {
        using var root = new TemporaryAppDataRoot();
        root.Paths.EnsureCreated();
        using var signingKey = RSA.Create(3072);
        var bundle = CreateBundle(signingKey, "1.2.3") with
        {
            SignatureBytes = Encoding.UTF8.GetBytes(Convert.ToBase64String(new byte[384])),
        };
        using var handler = bundle.CreateHandler();
        using var httpClient = new HttpClient(handler);
        var installer = CreateInstaller(
            root.Paths,
            httpClient,
            CreateLocator(root.Paths),
            bundle,
            signingKey);

        var failure = await Assert.ThrowsAsync<LocalInferenceComponentInstallException>(
            () => installer.InstallOrRepairAsync());

        Assert.Equal("component.signature-invalid", failure.ErrorCode);
        Assert.Equal(0, handler.RequestCount(bundle.ArchiveUri));
        Assert.Null(await CreateLocator(root.Paths).LocateAsync());
    }

    [Fact]
    public async Task InstallOrRepairAsync_RedownloadsWhenCachedComponentWasTampered()
    {
        using var root = new TemporaryAppDataRoot();
        root.Paths.EnsureCreated();
        using var signingKey = RSA.Create(3072);
        var bundle = CreateBundle(signingKey, "1.2.3");
        using var handler = bundle.CreateHandler();
        using var httpClient = new HttpClient(handler);
        var locator = CreateLocator(root.Paths);
        var installer = CreateInstaller(root.Paths, httpClient, locator, bundle, signingKey);
        var first = await installer.InstallOrRepairAsync();
        await File.WriteAllTextAsync(first.Component.WorkerPath, "tampered");

        var repaired = await installer.InstallOrRepairAsync();

        Assert.True(repaired.DownloadWasRequired);
        Assert.Equal(2, handler.RequestCount(bundle.ArchiveUri));
        Assert.Equal("fake-worker", await File.ReadAllTextAsync(repaired.Component.WorkerPath));
    }

    [Fact]
    public async Task InstallOrRepairAsync_RejectsPathTraversalAndKeepsCurrentVersion()
    {
        using var root = new TemporaryAppDataRoot();
        root.Paths.EnsureCreated();
        using var signingKey = RSA.Create(3072);
        var initialBundle = CreateBundle(signingKey, "1.0.0");
        using (var initialHandler = initialBundle.CreateHandler())
        using (var initialClient = new HttpClient(initialHandler))
        {
            var initialLocator = CreateLocator(root.Paths);
            var initialInstaller = CreateInstaller(
                root.Paths,
                initialClient,
                initialLocator,
                initialBundle,
                signingKey);
            _ = await initialInstaller.InstallOrRepairAsync();
        }

        var activePath = Path.Combine(
            root.Paths.LocalInferenceComponentsDirectoryPath,
            "x64",
            LocalInferenceComponentLocator.ActiveManifestFileName);
        var previousActiveBytes = await File.ReadAllBytesAsync(activePath);
        var maliciousBundle = CreateBundle(
            signingKey,
            "2.0.0",
            archiveEntries: [new("../escaped.exe", "malicious"u8.ToArray())]);
        using var handler = maliciousBundle.CreateHandler();
        using var httpClient = new HttpClient(handler);
        var installer = CreateInstaller(
            root.Paths,
            httpClient,
            CreateLocator(root.Paths),
            maliciousBundle,
            signingKey);

        var failure = await Assert.ThrowsAsync<LocalInferenceComponentInstallException>(
            () => installer.InstallOrRepairAsync());

        Assert.Equal("component.install-failed", failure.ErrorCode);
        Assert.Equal(previousActiveBytes, await File.ReadAllBytesAsync(activePath));
        Assert.Equal("1.0.0", (await CreateLocator(root.Paths).LocateAsync())?.Version);
        Assert.False(File.Exists(Path.Combine(
            root.Paths.LocalInferenceComponentStagingDirectoryPath,
            "escaped.exe")));
    }

    [Fact]
    public async Task InstallOrRepairAsync_RejectsArchiveHashMismatchAndKeepsCurrentVersion()
    {
        using var root = new TemporaryAppDataRoot();
        root.Paths.EnsureCreated();
        using var signingKey = RSA.Create(3072);
        var initialBundle = CreateBundle(signingKey, "1.0.0");
        using (var initialHandler = initialBundle.CreateHandler())
        using (var initialClient = new HttpClient(initialHandler))
        {
            var initialLocator = CreateLocator(root.Paths);
            _ = await CreateInstaller(
                    root.Paths,
                    initialClient,
                    initialLocator,
                    initialBundle,
                    signingKey)
                .InstallOrRepairAsync();
        }

        var invalidBundle = CreateBundle(
            signingKey,
            "2.0.0",
            overrideArchiveHash: new string('0', 64));
        using var handler = invalidBundle.CreateHandler();
        using var httpClient = new HttpClient(handler);
        var installer = CreateInstaller(
            root.Paths,
            httpClient,
            CreateLocator(root.Paths),
            invalidBundle,
            signingKey);

        var failure = await Assert.ThrowsAsync<LocalInferenceComponentInstallException>(
            () => installer.InstallOrRepairAsync());

        Assert.Equal("component.archive-hash-mismatch", failure.ErrorCode);
        Assert.Equal("1.0.0", (await CreateLocator(root.Paths).LocateAsync())?.Version);
    }

    private static LocalInferenceComponentInstaller CreateInstaller(
        AppDataPaths paths,
        HttpClient httpClient,
        LocalInferenceComponentLocator locator,
        ReleaseBundle bundle,
        RSA signingKey) =>
        new(
            paths,
            httpClient,
            locator,
            new LocalInferenceComponentReleaseSource(
                bundle.ManifestUri,
                bundle.SignatureUri,
                signingKey.ExportSubjectPublicKeyInfoPem()),
            "x64");

    private static LocalInferenceComponentLocator CreateLocator(AppDataPaths paths) =>
        new(paths, "x64", minimumProtocolVersion: 1, maximumProtocolVersion: 1);

    private static ReleaseBundle CreateBundle(
        RSA signingKey,
        string version,
        IReadOnlyList<ArchiveEntry>? archiveEntries = null,
        string? overrideArchiveHash = null)
    {
        var workerBytes = "fake-worker"u8.ToArray();
        var componentManifestBytes = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                schemaVersion = 1,
                componentId = LocalInferenceComponentLocator.ComponentId,
                version,
                architecture = "x64",
                protocolMinimumVersion = 1,
                protocolMaximumVersion = 1,
                files = new[]
                {
                    new
                    {
                        path = LocalInferenceComponentLocator.WorkerFileName,
                        length = workerBytes.LongLength,
                        sha256 = Convert.ToHexString(SHA256.HashData(workerBytes)),
                    },
                },
            },
            SerializerOptions);
        var archiveName = $"PicForLater.LocalInference-x64-{version}.zip";
        var archiveBytes = CreateArchive(
            componentManifestBytes,
            workerBytes,
            archiveEntries ?? []);
        var releaseManifestBytes = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                schemaVersion = 1,
                componentId = LocalInferenceComponentLocator.ComponentId,
                version,
                architecture = "x64",
                protocolMinimumVersion = 1,
                protocolMaximumVersion = 1,
                archiveFileName = archiveName,
                archiveLength = archiveBytes.LongLength,
                componentLength = componentManifestBytes.LongLength + workerBytes.LongLength,
                archiveSha256 = overrideArchiveHash
                                ?? Convert.ToHexString(SHA256.HashData(archiveBytes)),
                componentManifestSha256 = Convert.ToHexString(
                    SHA256.HashData(componentManifestBytes)),
            },
            SerializerOptions);
        var signatureBytes = Encoding.UTF8.GetBytes(Convert.ToBase64String(signingKey.SignData(
            releaseManifestBytes,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pss)));
        var releaseRoot = new Uri(
            "https://github.com/dogdreamson555/PicForLater/releases/latest/download/");
        return new ReleaseBundle(
            new Uri(releaseRoot, "local-inference-x64.release.json"),
            new Uri(releaseRoot, "local-inference-x64.release.json.sig"),
            new Uri(releaseRoot, archiveName),
            releaseManifestBytes,
            signatureBytes,
            archiveBytes);
    }

    private static byte[] CreateArchive(
        byte[] componentManifestBytes,
        byte[] workerBytes,
        IReadOnlyList<ArchiveEntry> extraEntries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(
                archive,
                LocalInferenceComponentLocator.ComponentManifestFileName,
                componentManifestBytes);
            WriteEntry(
                archive,
                LocalInferenceComponentLocator.WorkerFileName,
                workerBytes);
            foreach (var entry in extraEntries)
            {
                WriteEntry(archive, entry.Path, entry.Bytes);
            }
        }

        return stream.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string path, byte[] bytes)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var destination = entry.Open();
        destination.Write(bytes);
    }

    private sealed record ArchiveEntry(string Path, byte[] Bytes);

    private sealed record ReleaseBundle(
        Uri ManifestUri,
        Uri SignatureUri,
        Uri ArchiveUri,
        byte[] ManifestBytes,
        byte[] SignatureBytes,
        byte[] ArchiveBytes)
    {
        public RecordingHttpMessageHandler CreateHandler() => new(new Dictionary<Uri, byte[]>
        {
            [ManifestUri] = ManifestBytes,
            [SignatureUri] = SignatureBytes,
            [ArchiveUri] = ArchiveBytes,
        });
    }

    private sealed class RecordingHttpMessageHandler(
        IReadOnlyDictionary<Uri, byte[]> responses) : HttpMessageHandler
    {
        private readonly Dictionary<Uri, int> _requestCounts = [];

        public int RequestCount(Uri uri) => _requestCounts.GetValueOrDefault(uri);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("A request URI is required.");
            _requestCounts[uri] = RequestCount(uri) + 1;
            if (!responses.TryGetValue(uri, out var bytes))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    RequestMessage = request,
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes),
                RequestMessage = request,
            });
        }
    }
}
