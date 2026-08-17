using System.Net;
using System.Net.Sockets;
using PicForLater.Core.Analysis;

namespace PicForLater.Infrastructure.Analysis;

public static class SafeRemoteHttpMessageHandler
{
    public static SocketsHttpHandler Create() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        MaxConnectionsPerServer = 1,
        ConnectCallback = ConnectAsync,
    };

    private static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken)
            .ConfigureAwait(false);
        var allowLoopback = RemoteEndpointPolicy.IsLoopbackHost(host);
        var allowed = addresses.Where(address => allowLoopback
                ? IPAddress.IsLoopback(address)
                : RemoteEndpointPolicy.IsPublicAddress(address))
            .ToArray();
        if (allowed.Length == 0)
        {
            throw new HttpRequestException(
                "The remote endpoint resolved outside its permitted network boundary.");
        }

        Exception? lastFailure = null;
        foreach (var address in allowed)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };
            try
            {
                await socket.ConnectAsync(
                    new IPEndPoint(address, context.DnsEndPoint.Port),
                    cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                lastFailure = exception;
                if (exception is OperationCanceledException)
                {
                    throw;
                }
            }
        }

        throw new HttpRequestException("The remote endpoint could not be reached.", lastFailure);
    }
}
