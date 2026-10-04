using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

public sealed class PrerequisiteDownloader : IDisposable
{
    private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
    private long downloaded;
    public long BytesDownloaded { get { return Interlocked.Read(ref downloaded); } }
    public volatile string Status = "Connecting...";

    public void Cancel() { cancellation.Cancel(); }
    public void Dispose() { cancellation.Dispose(); }

    public async Task DownloadAsync(string uri, string path, long length)
    {
        if (length < 8) throw new ArgumentOutOfRangeException("length");
        string staging = path + ".download";
        string[] parts = new string[8];
        for (int i = 0; i < parts.Length; i++) parts[i] = path + ".part" + i;
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        try
        {
            using (var client = new HttpClient(new HttpClientHandler { MaxConnectionsPerServer = 8 }))
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token))
            using (var request = CreateRequest(uri, 0, length / 8 - 1))
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.DefaultRequestHeaders.UserAgent.ParseAdd("PicForLater-Setup");
                idle.CancelAfter(60000);
                using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, idle.Token))
                {
                    CheckRedirect(uri, response);
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        Status = "Downloading (server does not support parallel ranges)...";
                        await SaveResponseAsync(response, staging, length, idle);
                    }
                    else
                    {
                        CheckRange(response, 0, length / 8 - 1, length);
                        Status = "Downloading (8 segments)...";
                        var tasks = new Task[8];
                        tasks[0] = SaveResponseAsync(response, parts[0], length / 8, idle);
                        string resolvedUri = response.RequestMessage.RequestUri.AbsoluteUri;
                        for (int i = 1; i < tasks.Length; i++)
                        {
                            long start = length / 8 * i;
                            long end = i == 7 ? length - 1 : start + length / 8 - 1;
                            tasks[i] = DownloadRangeAsync(client, resolvedUri, parts[i], start, end, length);
                        }
                        await Task.WhenAll(tasks);
                        Status = "Merging downloaded segments...";
                        using (var output = File.Create(staging))
                            foreach (string part in parts)
                                using (var input = File.OpenRead(part))
                                    await input.CopyToAsync(output, 65536, cancellation.Token);
                    }
                }
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (File.Exists(path)) File.Delete(path);
            File.Move(staging, path);
        }
        finally
        {
            foreach (string part in parts) File.Delete(part);
            File.Delete(staging);
        }
    }

    private static HttpRequestMessage CreateRequest(string uri, long start, long end)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Range = new RangeHeaderValue(start, end);
        request.Headers.AcceptEncoding.ParseAdd("identity");
        return request;
    }

    private static void CheckRedirect(string uri, HttpResponseMessage response)
    {
        if (new Uri(uri).Scheme == "https" && response.RequestMessage.RequestUri.Scheme != "https")
            throw new IOException("The component download redirected to an insecure connection.");
    }

    private static void CheckRange(HttpResponseMessage response, long start, long end, long length)
    {
        var range = response.Content.Headers.ContentRange;
        if (response.StatusCode != HttpStatusCode.PartialContent || range == null ||
            !range.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase) ||
            range.From != start || range.To != end || range.Length != length)
            throw new IOException("Invalid component range response (HTTP " + (int)response.StatusCode + ").");
    }

    private async Task DownloadRangeAsync(HttpClient client, string uri, string path, long start, long end, long length)
    {
        try
        {
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token))
            using (var request = CreateRequest(uri, start, end))
            {
                idle.CancelAfter(60000);
                using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, idle.Token))
                {
                    CheckRedirect(uri, response);
                    CheckRange(response, start, end, length);
                    await SaveResponseAsync(response, path, end - start + 1, idle);
                }
            }
        }
        catch { cancellation.Cancel(); throw; }
    }

    private async Task SaveResponseAsync(HttpResponseMessage response, string path, long length, CancellationTokenSource idle)
    {
        try
        {
            if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength != length)
                throw new IOException("Component download length does not match the manifest.");
            foreach (string encoding in response.Content.Headers.ContentEncoding)
                if (!encoding.Equals("identity", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Unexpected component content encoding.");
            using (idle.Token.Register(response.Dispose))
            using (var input = await response.Content.ReadAsStreamAsync())
            using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
            {
                var buffer = new byte[65536];
                long received = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, 0, buffer.Length, idle.Token)) != 0)
                {
                    received += count;
                    if (received > length) throw new IOException("Component download exceeded the expected length.");
                    await output.WriteAsync(buffer, 0, count, idle.Token);
                    Interlocked.Add(ref downloaded, count);
                    idle.CancelAfter(60000);
                }
                if (received != length) throw new IOException("Component download ended before all bytes arrived.");
            }
        }
        catch { cancellation.Cancel(); throw; }
    }
}
