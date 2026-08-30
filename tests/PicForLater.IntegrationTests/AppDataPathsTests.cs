using PicForLater.Infrastructure.Storage;

namespace PicForLater.IntegrationTests;

public sealed class AppDataPathsTests
{
    [Fact]
    public void Constructor_DefinesFixedLocalSendPathsInsideTheManagedRoot()
    {
        using var root = new TemporaryAppDataRoot();

        Assert.Equal(
            Path.Combine(root.RootPath, "identity"),
            root.Paths.IdentityDirectoryPath);
        Assert.Equal(
            Path.Combine(root.RootPath, "identity", "localsend"),
            root.Paths.LocalSendIdentityDirectoryPath);
        Assert.Equal(
            Path.Combine(root.RootPath, "inbox"),
            root.Paths.InboxDirectoryPath);
        Assert.Equal(
            Path.Combine(root.RootPath, "inbox", "localsend"),
            root.Paths.LocalSendInboxDirectoryPath);
        Assert.Equal(
            Path.Combine(root.RootPath, "data", "localsend-trusted-devices.json"),
            root.Paths.LocalSendTrustedDevicesFilePath);
    }

    [Fact]
    public void EnsureCreated_CreatesLocalSendDirectoriesWithoutCreatingTheTrustFile()
    {
        using var root = new TemporaryAppDataRoot();

        root.Paths.EnsureCreated();

        Assert.True(Directory.Exists(root.Paths.LocalSendIdentityDirectoryPath));
        Assert.True(Directory.Exists(root.Paths.LocalSendInboxDirectoryPath));
        Assert.False(File.Exists(root.Paths.LocalSendTrustedDevicesFilePath));
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("inbox")]
    public void EnsureCreated_RejectsAReparsePointInALocalSendDirectory(string directoryKind)
    {
        using var root = new TemporaryAppDataRoot();
        root.Paths.EnsureCreated();
        var managedPath = directoryKind == "identity"
            ? root.Paths.LocalSendIdentityDirectoryPath
            : root.Paths.LocalSendInboxDirectoryPath;
        var outsidePath = Path.Combine(
            Path.GetTempPath(),
            "PicForLater.Tests.Outside",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsidePath);
        Directory.Delete(managedPath);
        Directory.CreateSymbolicLink(managedPath, outsidePath);

        try
        {
            Assert.Throws<InvalidOperationException>(() => root.Paths.EnsureCreated());
            Assert.Empty(Directory.EnumerateFileSystemEntries(outsidePath));
        }
        finally
        {
            Directory.Delete(managedPath);
            Directory.Delete(outsidePath, recursive: true);
        }
    }

    [Fact]
    public void EnsureSafePath_RejectsASiblingThatSharesTheRootPrefix()
    {
        using var root = new TemporaryAppDataRoot();
        var outsidePath = root.RootPath + "-outside";

        Assert.Throws<InvalidOperationException>(() => root.Paths.EnsureSafePath(outsidePath));
    }
}
