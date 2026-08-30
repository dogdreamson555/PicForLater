using PicForLater.App.Models;

namespace PicForLater.IntegrationTests;

public sealed class AppReleaseVersionTests
{
    [Theory]
    [InlineData("1.1.1", 1, 1, 1)]
    [InlineData("1.1.1+commit", 1, 1, 1)]
    [InlineData("1.1.1+commit.123-abc", 1, 1, 1)]
    public void LocalVersion_AcceptsStrictThreePartVersionWithOptionalMetadata(
        string value,
        int major,
        int minor,
        int patch)
    {
        var parsed = AppReleaseVersion.TryParseLocal(value, out var version);

        Assert.True(parsed);
        Assert.Equal(new AppReleaseVersion(major, minor, patch), version);
        Assert.Equal($"{major}.{minor}.{patch}", version.ToString());
    }

    [Theory]
    [InlineData("v1.1.1", 1, 1, 1)]
    [InlineData("v0.0.0", 0, 0, 0)]
    public void ReleaseTag_AcceptsLowercaseVAndExactlyThreeNumericParts(
        string value,
        int major,
        int minor,
        int patch)
    {
        Assert.True(AppReleaseVersion.TryParseReleaseTag(value, out var version));
        Assert.Equal(new AppReleaseVersion(major, minor, patch), version);
    }

    [Theory]
    [InlineData("1.1.1")]
    [InlineData("V1.1.1")]
    [InlineData("v1.1.1.0")]
    [InlineData("v1.1")]
    [InlineData("v1..1")]
    [InlineData("v1.1.1-beta")]
    [InlineData("v01.1.1")]
    [InlineData("v2147483648.1.1")]
    public void ReleaseTag_RejectsNonContractValues(string value)
    {
        Assert.False(AppReleaseVersion.TryParseReleaseTag(value, out _));
    }

    [Theory]
    [InlineData("1.1.1+")]
    [InlineData("1.1.1+commit..hash")]
    [InlineData("1.1.1-beta")]
    [InlineData("1.1.1.0")]
    [InlineData("1.01.1")]
    [InlineData(" 1.1.1")]
    public void LocalVersion_RejectsNonContractValues(string value)
    {
        Assert.False(AppReleaseVersion.TryParseLocal(value, out _));
    }

    [Fact]
    public void CompareTo_UsesMajorMinorPatchOrder()
    {
        var current = new AppReleaseVersion(1, 1, 1);

        Assert.Equal(0, current.CompareTo(new AppReleaseVersion(1, 1, 1)));
        Assert.True(current.CompareTo(new AppReleaseVersion(1, 2, 0)) < 0);
        Assert.True(current.CompareTo(new AppReleaseVersion(1, 0, 9)) > 0);
    }

    [Fact]
    public void Constructor_RejectsNegativeComponents()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AppReleaseVersion(-1, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AppReleaseVersion(0, -1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AppReleaseVersion(0, 0, -1));
    }
}
