using PicForLater.Infrastructure.Analysis;

namespace PicForLater.IntegrationTests;

public sealed class LocalInferenceComponentStoreTests
{
    [Fact]
    public async Task RemoveAllAsync_RemovesOnlyCurrentArchitectureComponents()
    {
        using var root = new TemporaryAppDataRoot();
        root.Paths.EnsureCreated();
        var x64Root = Path.Combine(
            root.Paths.LocalInferenceComponentsDirectoryPath,
            "x64");
        var arm64Root = Path.Combine(
            root.Paths.LocalInferenceComponentsDirectoryPath,
            "arm64");
        var modelFile = Path.Combine(root.Paths.ModelPackagesDirectoryPath, "keep.txt");
        Directory.CreateDirectory(Path.Combine(x64Root, "1.0.0"));
        Directory.CreateDirectory(Path.Combine(arm64Root, "1.0.0"));
        await File.WriteAllTextAsync(Path.Combine(x64Root, "1.0.0", "worker.exe"), "x64");
        await File.WriteAllTextAsync(Path.Combine(arm64Root, "1.0.0", "worker.exe"), "arm64");
        await File.WriteAllTextAsync(modelFile, "model");
        var locator = new LocalInferenceComponentLocator(root.Paths, "x64", 1, 1);
        var store = new LocalInferenceComponentStore(root.Paths, locator, "x64");

        var removed = await store.RemoveAllAsync();

        Assert.True(removed);
        Assert.False(Directory.Exists(x64Root));
        Assert.True(Directory.Exists(arm64Root));
        Assert.True(File.Exists(modelFile));
    }

    [Fact]
    public async Task RemoveAllAsync_ReturnsFalse_WhenArchitectureIsNotInstalled()
    {
        using var root = new TemporaryAppDataRoot();
        root.Paths.EnsureCreated();
        var locator = new LocalInferenceComponentLocator(root.Paths, "x64", 1, 1);
        var store = new LocalInferenceComponentStore(root.Paths, locator, "x64");

        var removed = await store.RemoveAllAsync();

        Assert.False(removed);
    }
}
