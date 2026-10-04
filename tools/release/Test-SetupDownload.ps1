[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'setup\Install-Prerequisites.ps1')
Add-Type -Path (Join-Path $PSScriptRoot 'setup\Download-Prerequisite.cs') -ReferencedAssemblies System.Net.Http
Add-Type @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

public sealed class DownloadTestServer : IDisposable
{
    private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
    private readonly byte[] body;
    private readonly string mode;
    private readonly Task acceptLoop;
    private readonly List<Task> responses = new List<Task>();
    private int active;
    public int Requests;
    public int PeakConnections;
    public string CancelPath;
    public string Uri { get { return "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/file"; } }

    public DownloadTestServer(byte[] body, string mode)
    {
        this.body = body;
        this.mode = mode;
        listener.Start();
        acceptLoop = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                var client = await listener.AcceptTcpClientAsync();
                responses.Add(RespondAsync(client));
            }
        }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    private async Task RespondAsync(TcpClient client)
    {
        int current = Interlocked.Increment(ref active);
        lock (this) PeakConnections = Math.Max(PeakConnections, current);
        Interlocked.Increment(ref Requests);
        try
        {
            using (client)
            using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true))
            {
                string request = await reader.ReadLineAsync();
                string line;
                long start = 0, end = body.Length - 1;
                while (!String.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                    if (line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase))
                    {
                        var range = line.Substring(13).Split('-');
                        start = Int64.Parse(range[0]);
                        end = Int64.Parse(range[1]);
                    }
                if (mode == "redirect" && request.Contains("/file "))
                {
                    var redirect = Encoding.ASCII.GetBytes("HTTP/1.1 302 Found\r\nLocation: " +
                        Uri.Replace("/file", "/resolved") + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(redirect, 0, redirect.Length);
                    return;
                }
                bool whole = mode == "whole";
                if (whole) { start = 0; end = body.Length - 1; }
                long length = end - start + 1;
                string headers = whole ? "HTTP/1.1 200 OK\r\n" : "HTTP/1.1 206 Partial Content\r\n";
                if (!whole) headers += "Content-Range: bytes " + (mode == "bad-range" ? start + 1 : start) +
                    "-" + end + "/" + body.Length + "\r\n";
                if (mode != "overrun") headers += "Content-Length: " + length + "\r\n";
                var headerBytes = Encoding.ASCII.GetBytes(headers + "Connection: close\r\n\r\n");
                await stream.WriteAsync(headerBytes, 0, headerBytes.Length);
                if (!whole && mode != "bad-range")
                    for (int i = 0; i < 100 && Volatile.Read(ref active) < 8; i++) await Task.Delay(20);
                if (mode == "cancel")
                {
                    if (start == 0) File.WriteAllText(CancelPath, "cancel");
                    await Task.Delay(2000);
                }
                if (mode == "truncated") length /= 2;
                if (mode == "overrun") length++;
                long sent = 0;
                while (sent < length)
                {
                    int count = (int)Math.Min(16384, length - sent);
                    var bytes = new byte[count];
                    for (int i = 0; i < count; i++) bytes[i] = body[(start + sent + i) % body.Length];
                    if (mode == "corrupt") bytes[0] ^= 1;
                    await stream.WriteAsync(bytes, 0, count);
                    sent += count;
                    await Task.Delay(10);
                }
            }
        }
        catch (IOException) { }
        catch (SocketException) { }
        finally { Interlocked.Decrement(ref active); }
    }

    public void Dispose()
    {
        listener.Stop();
        acceptLoop.GetAwaiter().GetResult();
        Task.WhenAll(responses).GetAwaiter().GetResult();
    }
}
'@

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$testRoot = Join-Path $repositoryRoot ('artifacts\setup-download-tests\' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $testRoot
$payload = New-Object byte[] (1048576 + 37)
(New-Object Random 42).NextBytes($payload)
$sourcePath = Join-Path $testRoot 'source.zip'
[IO.File]::WriteAllBytes($sourcePath, $payload)
$hash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
$checksPassed = $false
try {
    foreach ($mode in @('ranges', 'redirect', 'whole', 'bad-range', 'truncated', 'overrun', 'corrupt', 'cancel')) {
        $server = New-Object DownloadTestServer($payload, $mode)
        $path = Join-Path $testRoot "$mode.zip"
        $progressPath = "$path.ini"
        $server.CancelPath = "$progressPath.cancel"
        $item = [pscustomobject]@{
            uri = $server.Uri; length = $payload.Length; sha256 = $hash; kind = 'msixZip'; name = 'Test runtime'
        }
        $threw = $false
        try {
            Get-PrerequisiteDownload $item $path $progressPath
            Assert-Payload $item $path
        } catch { $threw = $true; $failure = $_.Exception.Message }
        finally { $server.Dispose() }
        $shouldFail = $mode -in @('bad-range', 'truncated', 'overrun', 'corrupt', 'cancel')
        if ($threw -ne $shouldFail) { throw "Unexpected download outcome: $mode (failed=$threw): $failure" }
        if ($mode -in @('ranges', 'redirect')) {
            $expectedRequests = if ($mode -eq 'redirect') { 9 } else { 8 }
            if ($server.PeakConnections -ne 8 -or $server.Requests -ne $expectedRequests) {
                throw "Expected eight concurrent ranges: peak=$($server.PeakConnections), requests=$($server.Requests)"
            }
            if ((Get-Content -LiteralPath $progressPath -Raw) -notmatch "(?m)^Bytes=$($payload.Length)\s*$") {
                throw 'Download progress did not reach the full file length.'
            }
        }
        if ($mode -eq 'whole' -and $server.Requests -ne 1) { throw 'A server ignoring Range must only download once.' }
        if ($mode -eq 'cancel' -and $server.PeakConnections -ne 8) { throw 'Cancellation check never reached eight connections.' }
        if ($mode -ne 'corrupt' -and $shouldFail -and (Test-Path -LiteralPath $path)) {
            throw 'An unsuccessful download must not publish a payload.'
        }
        if (@(Get-ChildItem -LiteralPath $testRoot -Filter "$mode.zip.part*").Count -ne 0 -or
            (Test-Path -LiteralPath "$path.download")) { throw 'Temporary download parts were not removed.' }
        "$($mode): passed"
    }
    $checksPassed = $true
    '8 download checks passed. Only loopback HTTP was used; no components were installed.'
}
finally {
    $allowedRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts\setup-download-tests')).TrimEnd('\') + '\'
    if (-not [IO.Path]::GetFullPath($testRoot).StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Test cleanup path escaped its artifacts directory.'
    }
    if ($checksPassed) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
    else { "Failed test artifacts: $testRoot" }
}
