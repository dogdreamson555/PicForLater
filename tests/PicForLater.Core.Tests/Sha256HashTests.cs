using PicForLater.Core.Images;

namespace PicForLater.Core.Tests;

public sealed class Sha256HashTests
{
    [Fact]
    public void Parse_NormalizesHexAndRoundTripsBytes()
    {
        var bytes = Enumerable.Range(0, Sha256Hash.ByteLength).Select(value => (byte)value).ToArray();
        var upperHex = Convert.ToHexString(bytes);

        var hash = Sha256Hash.Parse(upperHex);

        Assert.Equal(upperHex.ToLowerInvariant(), hash.Hex);
        Assert.Equal(bytes, hash.ToByteArray());
    }

    [Fact]
    public void FromBytes_RejectsNonSha256Length()
    {
        Assert.Throws<ArgumentException>(() => Sha256Hash.FromBytes(new byte[31]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("00")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void TryParse_RejectsInvalidValues(string? value)
    {
        Assert.False(Sha256Hash.TryParse(value, out var hash));
        Assert.Null(hash);
    }
}
