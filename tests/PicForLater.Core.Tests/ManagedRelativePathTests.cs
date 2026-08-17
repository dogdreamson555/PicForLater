using PicForLater.Core.Images;

namespace PicForLater.Core.Tests;

public sealed class ManagedRelativePathTests
{
    [Fact]
    public void Parse_NormalizesDirectorySeparators()
    {
        var path = ManagedRelativePath.Parse(@"assets\originals\ab\image.png");

        Assert.Equal("assets/originals/ab/image.png", path.Value);
        Assert.True(path.IsUnder("assets"));
        Assert.False(path.IsUnder("asset"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/assets/image.png")]
    [InlineData(@"C:\assets\image.png")]
    [InlineData("../image.png")]
    [InlineData("assets/../image.png")]
    [InlineData("assets//image.png")]
    [InlineData("assets/CON.png")]
    [InlineData("assets/image.png/")]
    [InlineData("assets/image. ")]
    public void Parse_RejectsUnsafePaths(string value)
    {
        Assert.ThrowsAny<ArgumentException>(() => ManagedRelativePath.Parse(value));
    }

    [Fact]
    public void IsUnder_RequiresACompleteFirstSegment()
    {
        var path = ManagedRelativePath.Parse("staging2/image.tmp");

        Assert.False(path.IsUnder("staging"));
    }
}
