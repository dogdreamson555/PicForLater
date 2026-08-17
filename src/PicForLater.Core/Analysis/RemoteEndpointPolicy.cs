using System.Net;

namespace PicForLater.Core.Analysis;

public static class RemoteEndpointPolicy
{
    public static bool IsAllowed(Uri? endpoint, RemoteEndpointTrustMode trustMode)
    {
        if (endpoint is not { IsAbsoluteUri: true }
            || string.IsNullOrWhiteSpace(endpoint.Host)
            || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            return false;
        }

        if (trustMode == RemoteEndpointTrustMode.LoopbackHttp)
        {
            return (endpoint.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                    || endpoint.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                && IsLoopbackHost(endpoint.Host);
        }

        return (trustMode is RemoteEndpointTrustMode.FixedHttps
                or RemoteEndpointTrustMode.PublicHttps)
            && endpoint.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !IsLoopbackHost(endpoint.Host)
            && !IsForbiddenLiteralAddress(endpoint.Host);
    }

    public static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);

    public static bool IsPublicAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.None)
            || address.Equals(IPAddress.IPv6None))
        {
            return false;
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] != 10
                && bytes[0] != 127
                && !(bytes[0] == 169 && bytes[1] == 254)
                && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                && !(bytes[0] == 192 && bytes[1] == 168)
                && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
                && !(bytes[0] == 0)
                && !(bytes[0] >= 224);
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return !address.IsIPv6LinkLocal
                && !address.IsIPv6SiteLocal
                && !address.IsIPv6Multicast
                && (address.GetAddressBytes()[0] & 0xFE) != 0xFC;
        }

        return false;
    }

    private static bool IsForbiddenLiteralAddress(string host) =>
        IPAddress.TryParse(host, out var address) && !IsPublicAddress(address);
}
