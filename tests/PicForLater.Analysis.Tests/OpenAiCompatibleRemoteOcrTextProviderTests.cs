using System.Net;
using System.Text;
using System.Text.Json;
using PicForLater.Core.Analysis;

namespace PicForLater.Analysis.Tests;

public sealed class OpenAiCompatibleRemoteOcrTextProviderTests
{
    private const string Credential = "test-only-secret";

    [Fact]
    public async Task AnalyzeAsync_NeverOpensImageAndRequestContainsNoImageOrLibraryContext()
    {
        string? requestBody = null;
        Uri? requestUri = null;
        string? authorization = null;
        string? idempotencyKey = null;
        using var httpClient = new HttpClient(new DelegateHandler(async request =>
        {
            requestUri = request.RequestUri;
            authorization = request.Headers.Authorization?.ToString();
            idempotencyKey = request.Headers.TryGetValues(
                "Idempotency-Key",
                out var values)
                ? Assert.Single(values)
                : null;
            requestBody = await request.Content!.ReadAsStringAsync();
            return JsonResponse(CreateStructuredOutput(
                "项目评审会议",
                "项目评审会议将于7月20日14:30在会议室A举行。",
                [
                    new
                    {
                        kind = "datetime",
                        rawText = "7月20日 14:30",
                        normalizedValue = (string?)null,
                        evidence = "7月20日 14:30 会议室A",
                    },
                ]));
        }));
        var credentials = new FakeCredentialService(Credential);
        var provider = new OpenAiCompatibleRemoteOcrTextProvider(
            httpClient,
            credentials,
            AllowAllRequestAuthorizer.Instance);
        var imageOpenCount = 0;
        var request = CreateRequest(() => imageOpenCount++);

        var result = await provider.AnalyzeAsync(request);

        Assert.Equal(0, imageOpenCount);
        Assert.Equal(new Uri("https://api.example.test/v1/chat/completions"), requestUri);
        Assert.Equal($"Bearer {Credential}", authorization);
        Assert.NotNull(idempotencyKey);
        Assert.Equal(32, idempotencyKey.Length);
        Assert.NotNull(requestBody);
        using var payload = JsonDocument.Parse(requestBody);
        var userPrompt = payload.RootElement
            .GetProperty("messages")[1]
            .GetProperty("content")
            .GetString();
        Assert.NotNull(userPrompt);
        Assert.Contains(request.OcrDocument.Text, userPrompt, StringComparison.Ordinal);
        Assert.Contains("zh-Hans", requestBody, StringComparison.Ordinal);
        Assert.Contains("China Standard Time", requestBody, StringComparison.Ordinal);
        Assert.DoesNotContain("private-original-name.png", requestBody, StringComparison.Ordinal);
        Assert.DoesNotContain("private category", requestBody, StringComparison.Ordinal);
        Assert.DoesNotContain(Credential, requestBody, StringComparison.Ordinal);
        Assert.DoesNotContain("\"image_url\"", requestBody, StringComparison.Ordinal);
        Assert.DoesNotContain("\"input_image\"", requestBody, StringComparison.Ordinal);
        Assert.DoesNotContain("\"file_name\"", requestBody, StringComparison.Ordinal);
        Assert.DoesNotContain("\"contentHash\"", requestBody, StringComparison.Ordinal);
        Assert.DoesNotContain("\"boundingBox\"", requestBody, StringComparison.Ordinal);
        Assert.DoesNotContain("iVBORw0KGgo", requestBody, StringComparison.Ordinal);
        Assert.False(payload.RootElement.TryGetProperty("tools", out _));
        Assert.False(payload.RootElement.TryGetProperty("functions", out _));
        Assert.Equal("remote-model", payload.RootElement.GetProperty("model").GetString());
        Assert.Empty(result.VisualFacts);
        Assert.Empty(result.Draft.SuggestedCategoryIds);
        Assert.Equal("项目评审会议", result.Draft.Title);
        Assert.Equal(AnalysisExecutionLocation.RemoteApi, result.Provenance.ExecutionLocation);
        Assert.Equal(RemoteInputMode.LocalOcrText, result.Provenance.RemoteInputMode);
        Assert.Equal(AnalysisOutputKind.ModelGeneratedDraft, result.Provenance.OutputKind);
    }

    [Fact]
    public async Task MissingCredential_DoesNotOpenImageOrSendRequest()
    {
        var sendCount = 0;
        using var httpClient = new HttpClient(new DelegateHandler(_ =>
        {
            sendCount++;
            throw new InvalidOperationException("No request should be sent.");
        }));
        var credentials = new FakeCredentialService(secret: null);
        var provider = new OpenAiCompatibleRemoteOcrTextProvider(
            httpClient,
            credentials,
            AllowAllRequestAuthorizer.Instance);
        var imageOpenCount = 0;
        var request = CreateRequest(() => imageOpenCount++);

        Assert.False(await provider.IsAvailableAsync(request.ProfileSnapshot));
        var exception = await Assert.ThrowsAsync<RemoteAnalysisProviderException>(
            () => provider.AnalyzeAsync(request));

        Assert.Equal("remote.credential-unavailable", exception.ErrorCode);
        Assert.False(exception.IsRetryable);
        Assert.Equal(0, imageOpenCount);
        Assert.Equal(0, sendCount);
    }

    [Fact]
    public async Task OversizedOcr_IsLocallyCompactedWithinProfileLimitAndReported()
    {
        const int maximumTextCharacters = 256;
        string? userPrompt = null;
        using var httpClient = new HttpClient(new DelegateHandler(async request =>
        {
            var body = await request.Content!.ReadAsStringAsync();
            using var payload = JsonDocument.Parse(body);
            userPrompt = payload.RootElement
                .GetProperty("messages")[1]
                .GetProperty("content")
                .GetString();
            return JsonResponse(CreateStructuredOutput(
                "Start",
                "Start summary.",
                []));
        }));
        var provider = new OpenAiCompatibleRemoteOcrTextProvider(
            httpClient,
            new FakeCredentialService(Credential),
            AllowAllRequestAuthorizer.Instance);
        var longText = string.Concat(
            "Start\n",
            new string('x', 600),
            "\nDeadline 2026-09-18 09:00 Room A\n",
            new string('y', 600),
            "\nEnd");
        var request = CreateRequest(
            onImageOpen: null,
            ocrText: longText,
            maximumTextCharacters);

        var result = await provider.AnalyzeAsync(request);

        Assert.NotNull(userPrompt);
        const string marker = "[OCR segments omitted by bounded local compaction]";
        Assert.Contains(marker, userPrompt, StringComparison.Ordinal);
        Assert.Contains("Deadline 2026-09-18 09:00 Room A", userPrompt, StringComparison.Ordinal);
        var ocrPayload = userPrompt[(userPrompt.IndexOf("OCR text:", StringComparison.Ordinal)
            + "OCR text:".Length)..].TrimStart();
        Assert.True(ocrPayload.Length <= maximumTextCharacters);
        Assert.Contains("remote.ocr-text-compacted", result.Warnings);
        Assert.Contains("remote.ocr-text-compacted", result.Draft.Warnings);
    }

    [Fact]
    public async Task AuthenticationFailure_IsNotRetriedAndDoesNotExposeResponseBody()
    {
        var sendCount = 0;
        using var httpClient = new HttpClient(new DelegateHandler(_ =>
        {
            sendCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(
                    """{"error":{"message":"sensitive supplier detail"}}""",
                    Encoding.UTF8,
                    "application/json"),
            });
        }));
        var provider = new OpenAiCompatibleRemoteOcrTextProvider(
            httpClient,
            new FakeCredentialService(Credential),
            AllowAllRequestAuthorizer.Instance);

        var exception = await Assert.ThrowsAsync<RemoteAnalysisProviderException>(
            () => provider.AnalyzeAsync(CreateRequest()));

        Assert.Equal("remote.credential-rejected", exception.ErrorCode);
        Assert.False(exception.IsRetryable);
        Assert.DoesNotContain(
            "sensitive supplier detail",
            exception.ToString(),
            StringComparison.Ordinal);
        Assert.Equal(1, sendCount);
    }

    [Fact]
    public async Task ServerFailure_IsNotBlindlyRetriedWhenIdempotencySupportIsUnknown()
    {
        var idempotencyKeys = new List<string>();
        var imageOpenCount = 0;
        using var httpClient = new HttpClient(new DelegateHandler(request =>
        {
            idempotencyKeys.Add(Assert.Single(
                request.Headers.GetValues("Idempotency-Key")));
            return Task.FromResult(new HttpResponseMessage(
                HttpStatusCode.InternalServerError));
        }));
        var provider = new OpenAiCompatibleRemoteOcrTextProvider(
            httpClient,
            new FakeCredentialService(Credential),
            AllowAllRequestAuthorizer.Instance);

        var exception = await Assert.ThrowsAsync<RemoteAnalysisProviderException>(
            () => provider.AnalyzeAsync(CreateRequest(() => imageOpenCount++)));

        Assert.Equal("remote.server-failure", exception.ErrorCode);
        Assert.True(exception.IsRetryable);
        Assert.Single(idempotencyKeys);
        Assert.Equal(32, idempotencyKeys[0].Length);
        Assert.Equal(0, imageOpenCount);
    }

    [Fact]
    public async Task RateLimit_RetriesOnceWithSameNonContentIdempotencyKey()
    {
        var idempotencyKeys = new List<string>();
        using var httpClient = new HttpClient(new DelegateHandler(request =>
        {
            idempotencyKeys.Add(Assert.Single(
                request.Headers.GetValues("Idempotency-Key")));
            if (idempotencyKeys.Count == 1)
            {
                var rateLimited = new HttpResponseMessage(
                    HttpStatusCode.TooManyRequests);
                rateLimited.Headers.RetryAfter =
                    new System.Net.Http.Headers.RetryConditionHeaderValue(
                        TimeSpan.Zero);
                return Task.FromResult(rateLimited);
            }

            return Task.FromResult(JsonResponse(CreateStructuredOutput(
                "项目评审会议",
                "项目评审会议将在会议室A举行。",
                [])));
        }));
        var provider = new OpenAiCompatibleRemoteOcrTextProvider(
            httpClient,
            new FakeCredentialService(Credential),
            AllowAllRequestAuthorizer.Instance);

        var result = await provider.AnalyzeAsync(CreateRequest());

        Assert.Equal("项目评审会议", result.Draft.Title);
        Assert.Equal(2, idempotencyKeys.Count);
        Assert.Equal(idempotencyKeys[0], idempotencyKeys[1]);
        Assert.Equal(32, idempotencyKeys[0].Length);
    }

    private static VisionAnalysisRequest CreateRequest(
        Action? onImageOpen = null,
        string? ocrText = null,
        int maximumTextCharacters = 10_000)
    {
        var box = new OcrBoundingBox(1, 2, 30, 10);
        var document = new OcrDocument(
            ocrText ?? "项目评审会议\n7月20日 14:30 会议室A",
            [
                new OcrLine(
                    "7月20日 14:30 会议室A",
                    box,
                    [new OcrWord("7月20日", box, 0.99)],
                    0.99),
            ],
            ["zh-Hans"],
            [],
            new AnalysisProvenance(
                "test.local-ocr",
                "ocr-model",
                "1",
                new Dictionary<string, string>(),
                "ocr.v1",
                AnalysisExecutionLocation.Local,
                AnalysisOutputKind.OcrFacts),
            1280,
            720);
        var profile = ModelProfileSnapshot.Default with
        {
            AnalysisMode = AnalysisMode.OcrOnly,
            Revision = 2,
            ExecutionBackend = AnalysisExecutionBackend.RemoteApi,
            RemoteInputMode = RemoteInputMode.LocalOcrText,
            RemoteApiProfile = new RemoteApiProfileSnapshot
            {
                ProfileId = "remote-profile",
                ProviderId = "opaque-provider",
                EndpointId = "openai-compatible.chat-completions.v1",
                BaseUri = new Uri("https://api.example.test/v1/chat/completions"),
                ModelId = "remote-model",
                PromptVersion = "remote-ocr-text.prompt.v1",
                OutputSchemaVersion = QwenStructuredOutputParser.SchemaVersion,
                MaxTextChars = maximumTextCharacters,
                MaxImageBytes = 8 * 1024 * 1024,
                MaxOutputTokens = 1_024,
                TimeoutSeconds = 30,
                CredentialReference = "credential-ref",
                ConsentVersion = "disclosure.v1",
            },
        };
        return new VisionAnalysisRequest(
            _ =>
            {
                onImageOpen?.Invoke();
                return ValueTask.FromException<Stream>(
                    new InvalidOperationException("RemoteOcrText must not open the image."));
            },
            "private-original-name.png",
            document,
            new AnalysisCompositionContext(
                [new AnalysisCategoryOption(Guid.NewGuid(), "private category")]),
            profile)
        {
            ReferenceTimeUtc = new DateTimeOffset(
                2026,
                7,
                31,
                6,
                0,
                0,
                TimeSpan.Zero),
            TimeZoneId = "China Standard Time",
        };
    }

    private static HttpResponseMessage JsonResponse(string content)
    {
        var envelope = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        content,
                    },
                },
            },
        });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "application/json"),
        };
    }

    private static string CreateStructuredOutput(
        string title,
        string summary,
        IReadOnlyList<object> entities) =>
        JsonSerializer.Serialize(new
        {
            schemaVersion = QwenStructuredOutputParser.SchemaVersion,
            title,
            summary,
            visualFacts = Array.Empty<string>(),
            categoryIds = Array.Empty<string>(),
            entities,
            detectedLanguages = new[] { "zh-Hans" },
            warnings = Array.Empty<string>(),
        });

    private sealed class FakeCredentialService(string? secret)
        : IRemoteApiCredentialService
    {
        public Task StoreAsync(
            string credentialReference,
            string value,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string?> RetrieveAsync(
            string credentialReference,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(secret);

        public Task<bool> ExistsAsync(
            string credentialReference,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(secret is not null);

        public Task DeleteAsync(
            string credentialReference,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request);
    }

    private sealed class AllowAllRequestAuthorizer : IRemoteApiRequestAuthorizer
    {
        public static AllowAllRequestAuthorizer Instance { get; } = new();

        public Task EnsureAuthorizedAsync(
            RemoteApiProfileSnapshot profileSnapshot,
            RemoteInputMode inputMode,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
