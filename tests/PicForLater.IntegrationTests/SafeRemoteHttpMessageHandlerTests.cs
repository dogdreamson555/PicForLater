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
}
