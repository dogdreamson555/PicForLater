using System.Net;
using PicForLater.Core.Analysis;

namespace PicForLater.Core.Tests;

public sealed class RemoteEndpointPolicyTests
{
    [Theory]
    [InlineData("https://api.example.com/v1/chat/completions", RemoteEndpointTrustMode.PublicHttps, true)]
    [InlineData("http://api.example.com/v1/chat/completions", RemoteEndpointTrustMode.PublicHttps, false)]
    [InlineData("https://127.0.0.1/v1/chat/completions", RemoteEndpointTrustMode.PublicHttps, false)]
    [InlineData("http://127.0.0.1:11434/v1/chat/completions", RemoteEndpointTrustMode.LoopbackHttp, true)]
    [InlineData("http://127.0.0.1:8000/v1/chat/completions", RemoteEndpointTrustMode.LoopbackHttp, true)]
    [InlineData("http://localhost:8000/v1/chat/completions", RemoteEndpointTrustMode.LoopbackHttp, true)]
    [InlineData("http://192.168.1.2:8000/v1/chat/completions", RemoteEndpointTrustMode.LoopbackHttp, false)]
    [InlineData("https://[::ffff:10.0.0.1]/v1/chat/completions", RemoteEndpointTrustMode.PublicHttps, false)]
    [InlineData("https://[::ffff:172.16.0.1]/v1/chat/completions", RemoteEndpointTrustMode.PublicHttps, false)]
    [InlineData("https://[::ffff:192.168.0.1]/v1/chat/completions", RemoteEndpointTrustMode.PublicHttps, false)]
    [InlineData("https://[::ffff:127.0.0.1]/v1/chat/completions", RemoteEndpointTrustMode.PublicHttps, false)]
    [InlineData("https://[::ffff:169.254.169.254]/v1/chat/completions", RemoteEndpointTrustMode.PublicHttps, false)]
    [InlineData("https://[::ffff:100.64.0.1]/v1/chat/completions", RemoteEndpointTrustMode.PublicHttps, false)]
    [InlineData("https://[::ffff:8.8.8.8]/v1/chat/completions", RemoteEndpointTrustMode.PublicHttps, true)]
    [InlineData("https://api.example.com/v1/chat/completions?key=secret", RemoteEndpointTrustMode.PublicHttps, false)]
    public void EndpointBoundary_IsExplicit(string value, RemoteEndpointTrustMode trustMode, bool expected) =>
        Assert.Equal(expected, RemoteEndpointPolicy.IsAllowed(new Uri(value), trustMode));

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("10.0.0.1", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("192.168.0.1", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("::ffff:172.16.0.1", false)]
    [InlineData("::ffff:192.168.0.1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("::ffff:169.254.169.254", false)]
    [InlineData("::ffff:100.64.0.1", false)]
    [InlineData("::ffff:8.8.8.8", true)]
    public void AddressBoundary_RejectsNonPublicDestinations(string value, bool expected) =>
        Assert.Equal(expected, RemoteEndpointPolicy.IsPublicAddress(IPAddress.Parse(value)));
}
