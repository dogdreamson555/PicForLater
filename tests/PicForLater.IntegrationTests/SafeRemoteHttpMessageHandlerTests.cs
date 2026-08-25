using PicForLater.Infrastructure.Analysis;

namespace PicForLater.IntegrationTests;

public sealed class SafeRemoteHttpMessageHandlerTests
{
    [Fact]
    public void Create_DisablesRedirectsCookiesAndProxiesAndLimitsConcurrency()
    {
        using var handler = SafeRemoteHttpMessageHandler.Create();

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.False(handler.UseProxy);
        Assert.Equal(1, handler.MaxConnectionsPerServer);
        Assert.NotNull(handler.ConnectCallback);
    }

    [Fact]
    public async Task ConnectCallback_RejectsMappedPrivateAddressReturnedByResolution()
    {
        using var handler = SafeRemoteHttpMessageHandler.Create();
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(5),
        };

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync("https://[::ffff:10.0.0.1]/"));

        Assert.Contains(
            "outside its permitted network boundary",
            exception.ToString(),
            StringComparison.Ordinal);
    }
}
