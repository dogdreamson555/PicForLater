using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PicForLater.Analysis;
using PicForLater.Core.Analysis;
using PicForLater.Infrastructure.Analysis;

namespace PicForLater.IntegrationTests;

public sealed class RemoteFakeHttpContractTests
{
    [Fact]
    public async Task JsonObjectContract_RoundTripsThroughActualLoopbackHttp()
    {
        await using var server = new LoopbackFakeHttpServer(CreateSuccessEnvelope());
        using var client = new HttpClient(SafeRemoteHttpMessageHandler.Create());
        var tester = new OpenAiCompatibleRemoteApiConnectionTester(
            client,
            NoCredentialService.Instance);

        await tester.TestAsync(
            CreateProfile(server.Endpoint),
            RemoteInputMode.LocalOcrText);

        var observed = await server.Request;
        Assert.Equal("POST", observed.Method);
        Assert.Equal("/v1/chat/completions", observed.Path);
        Assert.False(observed.Headers.ContainsKey("Authorization"));
        Assert.True(observed.Headers.ContainsKey("Idempotency-Key"));
        using var payload = JsonDocument.Parse(observed.Body);
        Assert.Equal("json_object", payload.RootElement
            .GetProperty("response_format").GetProperty("type").GetString());
        var systemPrompt = payload.RootElement
            .GetProperty("messages")[0].GetProperty("content").GetString();
        Assert.Contains("exactly these eight root keys", systemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("image_url", observed.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("base64", observed.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvalidStructuredResult_FromActualLoopbackHttp_IsRejected()
    {
        var invalidContent = JsonSerializer.Serialize(new { title = "missing contract fields" });
        var envelope = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = invalidContent } } },
        });
        await using var server = new LoopbackFakeHttpServer(envelope);
        using var client = new HttpClient(SafeRemoteHttpMessageHandler.Create());
        var tester = new OpenAiCompatibleRemoteApiConnectionTester(
            client,
            NoCredentialService.Instance);

        var exception = await Assert.ThrowsAsync<RemoteAnalysisProviderException>(() =>
            tester.TestAsync(CreateProfile(server.Endpoint), RemoteInputMode.LocalOcrText));

        Assert.Equal("remote.invalid-structured-output", exception.ErrorCode);
        Assert.False(exception.IsRetryable);
        _ = await server.Request;
    }

    private static RemoteApiProfile CreateProfile(Uri endpoint) => new()
    {
        ProfileId = "fake-loopback",
        ProviderId = "fake.loopback",
        DisplayName = "Fake loopback",
        EndpointId = "fake.openai-chat",
        BaseUri = endpoint,
        ModelId = "fake-model",
        SupportedInputModes = [RemoteInputMode.LocalOcrText],
        PromptVersion = "fake-contract.v1",
        OutputSchemaVersion = QwenStructuredOutputParser.SchemaVersion,
        MaxTextChars = 4_096,
        MaxImageBytes = 1_024,
        MaxOutputTokens = 512,
        TimeoutSeconds = 10,
        PrivacyUrl = new Uri("https://example.invalid/privacy"),
        TermsUrl = new Uri("https://example.invalid/terms"),
        RetentionTrainingStatement = "Fake HTTP integration test.",
        RetentionTrainingVerifiedAtUtc = DateTimeOffset.UtcNow,
        CredentialReference = "unused",
        DisclosureVersion = "fake-disclosure.v1",
        Protocol = RemoteApiProtocol.OpenAiChatCompletions,
        AuthenticationKind = RemoteApiAuthenticationKind.None,
        StructuredOutputMode = RemoteStructuredOutputMode.JsonObject,
        EndpointTrustMode = RemoteEndpointTrustMode.LoopbackHttp,
        IsEnabled = true,
        ValidationState = RemoteApiProfileValidationState.Unverified,
        UpdatedAtUtc = DateTimeOffset.UtcNow,
    };

    private static string CreateSuccessEnvelope()
    {
        var content = JsonSerializer.Serialize(new
        {
            schemaVersion = QwenStructuredOutputParser.SchemaVersion,
            title = "Synthetic test",
            summary = "The fake HTTP contract completed.",
            visualFacts = Array.Empty<string>(),
            categoryIds = Array.Empty<string>(),
            entities = Array.Empty<object>(),
            detectedLanguages = new[] { "en" },
            warnings = Array.Empty<string>(),
        });
        return JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content } } },
        });
    }

    private sealed class NoCredentialService : IRemoteApiCredentialService
    {
        public static NoCredentialService Instance { get; } = new();

        public Task StoreAsync(string credentialReference, string secret, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No credential operation is expected.");

        public Task<bool> ExistsAsync(string credentialReference, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No credential operation is expected.");

        public Task<string?> RetrieveAsync(string credentialReference, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No credential operation is expected.");

        public Task DeleteAsync(string credentialReference, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No credential operation is expected.");
    }

    private sealed class LoopbackFakeHttpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly byte[] _responseBody;
        private readonly Task<ObservedRequest> _request;

        public LoopbackFakeHttpServer(string responseBody)
        {
            _responseBody = Encoding.UTF8.GetBytes(responseBody);
            _listener.Start();
            var endpoint = (IPEndPoint)_listener.LocalEndpoint;
            Endpoint = new Uri($"http://127.0.0.1:{endpoint.Port}/v1/chat/completions");
            _request = ServeOnceAsync();
        }

        public Uri Endpoint { get; }

        public Task<ObservedRequest> Request => _request;

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            try
            {
                await _request.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is SocketException
                                              or ObjectDisposedException
                                              or OperationCanceledException)
            {
            }
        }

        private async Task<ObservedRequest> ServeOnceAsync()
        {
            using var connection = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
            await using var stream = connection.GetStream();
            var headerBytes = await ReadHeadersAsync(stream).ConfigureAwait(false);
            var headerText = Encoding.ASCII.GetString(headerBytes);
            var headerLines = headerText.Split("\r\n", StringSplitOptions.None);
            var requestLine = headerLines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var headers = headerLines.Skip(1)
                .Where(line => line.Contains(':', StringComparison.Ordinal))
                .Select(line => line.Split(':', 2))
                .ToDictionary(
                    parts => parts[0],
                    parts => parts[1].Trim(),
                    StringComparer.OrdinalIgnoreCase);
            var contentLength = headers.TryGetValue("Content-Length", out var rawLength)
                ? int.Parse(rawLength, System.Globalization.CultureInfo.InvariantCulture)
                : 0;
            var bodyBytes = new byte[contentLength];
            await stream.ReadExactlyAsync(bodyBytes).ConfigureAwait(false);

            var responseHeaders = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {_responseBody.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(responseHeaders).ConfigureAwait(false);
            await stream.WriteAsync(_responseBody).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
            return new ObservedRequest(
                requestLine[0],
                requestLine[1],
                headers,
                Encoding.UTF8.GetString(bodyBytes));
        }

        private static async Task<byte[]> ReadHeadersAsync(Stream stream)
        {
            using var buffer = new MemoryStream();
            var matched = 0;
            var delimiter = new byte[] { 13, 10, 13, 10 };
            while (buffer.Length < 32_768)
            {
                var value = stream.ReadByte();
                if (value < 0)
                {
                    throw new EndOfStreamException();
                }

                buffer.WriteByte((byte)value);
                matched = value == delimiter[matched] ? matched + 1 : 0;
                if (matched == delimiter.Length)
                {
                    return buffer.ToArray();
                }

                await Task.Yield();
            }

            throw new InvalidDataException("The fake HTTP request headers were too large.");
        }
    }

    private sealed record ObservedRequest(
        string Method,
        string Path,
        IReadOnlyDictionary<string, string> Headers,
        string Body);
}
