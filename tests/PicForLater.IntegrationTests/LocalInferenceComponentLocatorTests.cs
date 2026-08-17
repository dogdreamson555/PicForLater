using System.Security.Cryptography;
using System.Text.Json;
using PicForLater.Infrastructure.Analysis;
using PicForLater.Infrastructure.Storage;

namespace PicForLater.IntegrationTests;

public sealed class LocalInferenceComponentLocatorTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    [Fact]
    public async Task LocateAsync_ReturnsNull_WhenComponentIsNotInstalled()
    {
        using var root = new TemporaryAppDataRoot();
        var paths = CreatePaths(root);
        var locator = CreateLocator(paths);

        var component = await locator.LocateAsync();

        Assert.Null(component);
    }

    [Fact]
    public async Task LocateAsync_ReturnsValidatedVersionedWorker()
    {
        using var root = new TemporaryAppDataRoot();
        var paths = CreatePaths(root);
        var workerPath = WriteComponent(paths);
        var locator = CreateLocator(paths);

        var component = await locator.LocateAsync();

        Assert.NotNull(component);
        Assert.Equal("1.2.3", component.Version);
        Assert.Equal(workerPath, component.WorkerPath);
        Assert.Equal(Path.GetDirectoryName(workerPath), component.DirectoryPath);
        Assert.Equal(1, component.ProtocolMinimumVersion);
        Assert.Equal(1, component.ProtocolMaximumVersion);
    }

    [Fact]
    public async Task LocateAsync_ReturnsNull_WhenWorkerHashDoesNotMatch()
    {
        using var root = new TemporaryAppDataRoot();
        var paths = CreatePaths(root);
        var workerPath = WriteComponent(paths);
        await File.AppendAllTextAsync(workerPath, "tampered");
        var locator = CreateLocator(paths);

        var component = await locator.LocateAsync();

        Assert.Null(component);
    }

    [Fact]
    public async Task LocateAsync_ReturnsNull_WhenComponentContainsUnlistedFile()
    {
        using var root = new TemporaryAppDataRoot();
        var paths = CreatePaths(root);
        var workerPath = WriteComponent(paths);
        await File.WriteAllTextAsync(
            Path.Combine(Path.GetDirectoryName(workerPath)!, "unlisted.dll"),
            "unexpected");
        var locator = CreateLocator(paths);

        var component = await locator.LocateAsync();

        Assert.Null(component);
    }

    [Fact]
    public async Task LocateAsync_ReturnsNull_WhenManifestPathEscapesComponent()
    {
        using var root = new TemporaryAppDataRoot();
        var paths = CreatePaths(root);
        WriteComponent(paths, filePath: "../PicForLater.LocalInference.exe");
        var locator = CreateLocator(paths);

        var component = await locator.LocateAsync();

        Assert.Null(component);
    }

    [Theory]
    [InlineData("nested//PicForLater.LocalInference.exe")]
    [InlineData("nested\\PicForLater.LocalInference.exe")]
    [InlineData("C:/PicForLater.LocalInference.exe")]
    [InlineData("CON")]
    [InlineData("worker.")]
    public async Task LocateAsync_ReturnsNull_WhenManifestPathIsNotCanonical(string filePath)
    {
        using var root = new TemporaryAppDataRoot();
        var paths = CreatePaths(root);
        WriteComponent(paths, filePath: filePath);
        var locator = CreateLocator(paths);

        var component = await locator.LocateAsync();

        Assert.Null(component);
    }

    [Theory]
    [InlineData("arm64", 1, 1)]
    [InlineData("x64", 2, 2)]
    public async Task LocateAsync_ReturnsNull_WhenManifestIsIncompatible(
        string architecture,
        int protocolMinimumVersion,
        int protocolMaximumVersion)
    {
        using var root = new TemporaryAppDataRoot();
        var paths = CreatePaths(root);
        WriteComponent(
            paths,
            architecture: architecture,
            protocolMinimumVersion: protocolMinimumVersion,
            protocolMaximumVersion: protocolMaximumVersion);
        var locator = CreateLocator(paths);

        var component = await locator.LocateAsync();

        Assert.Null(component);
    }

    private static AppDataPaths CreatePaths(TemporaryAppDataRoot root)
    {
        var paths = root.Paths;
        paths.EnsureCreated();
        return paths;
    }

    private static LocalInferenceComponentLocator CreateLocator(AppDataPaths paths) =>
        new(paths, "x64", minimumProtocolVersion: 1, maximumProtocolVersion: 1);

    private static string WriteComponent(
        AppDataPaths paths,
        string architecture = "x64",
        int protocolMinimumVersion = 1,
        int protocolMaximumVersion = 1,
        string filePath = LocalInferenceComponentLocator.WorkerFileName)
    {
        const string version = "1.2.3";
        var architectureRoot = Path.Combine(paths.LocalInferenceComponentsDirectoryPath, "x64");
        var componentRoot = Path.Combine(architectureRoot, version);
        Directory.CreateDirectory(componentRoot);

        var workerPath = Path.Combine(componentRoot, LocalInferenceComponentLocator.WorkerFileName);
        var workerBytes = "fake-worker"u8.ToArray();
        File.WriteAllBytes(workerPath, workerBytes);
        var fileManifest = new
        {
            path = filePath,
            length = workerBytes.LongLength,
            sha256 = Convert.ToHexString(SHA256.HashData(workerBytes)),
        };
        var componentManifest = new
        {
            schemaVersion = 1,
            componentId = LocalInferenceComponentLocator.ComponentId,
            version,
            architecture,
            protocolMinimumVersion,
            protocolMaximumVersion,
            files = new[] { fileManifest },
        };
        File.WriteAllText(
            Path.Combine(componentRoot, LocalInferenceComponentLocator.ComponentManifestFileName),
            JsonSerializer.Serialize(componentManifest, SerializerOptions));
        File.WriteAllText(
            Path.Combine(architectureRoot, LocalInferenceComponentLocator.ActiveManifestFileName),
            JsonSerializer.Serialize(new { schemaVersion = 1, version }, SerializerOptions));
        return workerPath;
    }
}
