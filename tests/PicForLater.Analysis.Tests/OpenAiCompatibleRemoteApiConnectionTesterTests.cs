using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PicForLater.Core.Analysis;

namespace PicForLater.Analysis.Tests;

public sealed class OpenAiCompatibleRemoteApiConnectionTesterTests
{
    private const string Credential = "synthetic-test-secret";

    [Fact]
    public async Task TextMode_SendsOnlySyntheticContentAndPortableStrictSchema()
    {
        string? requestBody = null;
        string? authorization = null;
        using var client = new HttpClient(new DelegateHandler(async request =>
        {
            authorization = request.Headers.Authorization?.ToString();
            requestBody = await request.Content!.ReadAsStringAsync();
            return JsonResponse();
        }));
        var tester = new OpenAiCompatibleRemoteApiConnectionTester(
            client,
            new FakeCredentialService(Credential));

        await tester.TestAsync(CreateProfile(), RemoteInputMode.LocalOcrText);

        Assert.Equal($"Bearer {Credential}", authorization);
        Assert.NotNull(requestBody);
        using var payload = JsonDocument.Parse(requestBody);
        Assert.Equal(
            "Synthetic PicForLater capability test. Return a short title and summary for the word test.",
            payload.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
        Assert.False(payload.RootElement.TryGetProperty("temperature", out _));
        Assert.False(payload.RootElement.TryGetProperty("n", out _));
        var schema = payload.RootElement
            .GetProperty("response_format")
            .GetProperty("json_schema")
            .GetProperty("schema");
        Assert.False(schema.TryGetProperty("x-guidance", out _));
        var schemaVersion = schema.GetProperty("properties").GetProperty("schemaVersion");
        Assert.False(schemaVersion.TryGetProperty("const", out _));
        Assert.Equal(
            QwenStructuredOutputParser.SchemaVersion,
            schemaVersion.GetProperty("enum")[0].GetString());
        Assert.DoesNotContain("image_url", requestBody, StringComparison.Ordinal);
        Assert.DoesNotContain("base64", requestBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fileName", requestBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("contentHash", requestBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Credential, requestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImageMode_SendsOnlyPinnedBuiltInLicensedImage()
    {
        string? requestBody = null;
        using var client = new HttpClient(new DelegateHandler(async request =>
        {
            requestBody = await request.Content!.ReadAsStringAsync();
            return JsonResponse();
        }));
        var tester = new OpenAiCompatibleRemoteApiConnectionTester(
            client,
            new FakeCredentialService(Credential));

        await tester.TestAsync(CreateProfile(), RemoteInputMode.DirectImage);

        Assert.NotNull(requestBody);
        using var payload = JsonDocument.Parse(requestBody);
        var content = payload.RootElement.GetProperty("messages")[1].GetProperty("content");
        var dataUrl = content[1].GetProperty("image_url").GetProperty("url").GetString();
        Assert.False(content[1].GetProperty("image_url").TryGetProperty("detail", out _));
        Assert.NotNull(dataUrl);
        Assert.StartsWith("data:image/jpeg;base64,", dataUrl, StringComparison.Ordinal);
        var imageBytes = Convert.FromBase64String(dataUrl["data:image/jpeg;base64,".Length..]);
        Assert.Equal(61_868, imageBytes.Length);
        Assert.Equal(
            "9afff550a763f949ecc3b39dd5a7d17c9225e40e0405da93330fb0a2487aa641",
            Convert.ToHexString(SHA256.HashData(imageBytes)).ToLowerInvariant());
        Assert.Contains("built-in licensed cat test image", requestBody, StringComparison.Ordinal);
        Assert.DoesNotContain("private-original", requestBody, StringComparison.Ordinal);
        Assert.DoesNotContain("OCR secret", requestBody, StringComparison.Ordinal);
        Assert.DoesNotContain(Credential, requestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectedCredential_IsClassifiedWithoutRetry()
    {
        var requests = 0;
        using var client = new HttpClient(new DelegateHandler(_ =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }));
        var tester = new OpenAiCompatibleRemoteApiConnectionTester(
            client,
            new FakeCredentialService(Credential));

        var exception = await Assert.ThrowsAsync<RemoteAnalysisProviderException>(
            () => tester.TestAsync(CreateProfile(), RemoteInputMode.LocalOcrText));

        Assert.Equal("remote.credential-rejected", exception.ErrorCode);
        Assert.False(exception.IsRetryable);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task AnthropicProtocol_UsesMessagesHeadersSchemaAndImageSource()
    {
        string? requestBody = null;
        string? apiKey = null;
        string? apiVersion = null;
        using var client = new HttpClient(new DelegateHandler(async request =>
        {
            apiKey = request.Headers.GetValues("x-api-key").Single();
            apiVersion = request.Headers.GetValues("anthropic-version").Single();
            requestBody = await request.Content!.ReadAsStringAsync();
            return AnthropicJsonResponse();
        }));
        var tester = new OpenAiCompatibleRemoteApiConnectionTester(
            client,
            new FakeCredentialService(Credential));
        var profile = CreateProfile() with
        {
            Protocol = RemoteApiProtocol.AnthropicMessages,
            AuthenticationKind = RemoteApiAuthenticationKind.XApiKey,
            ApiVersion = "2023-06-01",
        };

        await tester.TestAsync(profile, RemoteInputMode.DirectImage);

        Assert.Equal(Credential, apiKey);
        Assert.Equal("2023-06-01", apiVersion);
        Assert.NotNull(requestBody);
        using var payload = JsonDocument.Parse(requestBody);
        Assert.Equal("json_schema", payload.RootElement
            .GetProperty("output_config").GetProperty("format").GetProperty("type").GetString());
        var content = payload.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal("image", content[1].GetProperty("type").GetString());
        Assert.Equal("base64", content[1].GetProperty("source").GetProperty("type").GetString());
        Assert.False(payload.RootElement.TryGetProperty("response_format", out _));
        Assert.DoesNotContain(Credential, requestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplicitRequestPolicies_DisableFallbacksAndExternalSearch()
    {
        string? requestBody = null;
        using var client = new HttpClient(new DelegateHandler(async request =>
        {
            requestBody = await request.Content!.ReadAsStringAsync();
            return JsonResponse();
        }));
        var tester = new OpenAiCompatibleRemoteApiConnectionTester(
            client,
            new FakeCredentialService(Credential));

        await tester.TestAsync(CreateProfile() with
        {
            DisableProviderFallbacks = true,
            DisableExternalSearch = true,
            ReasoningMode = RemoteReasoningMode.Disabled,
            ReasoningWireFormat = RemoteReasoningWireFormat.ThinkingObject,
        }, RemoteInputMode.LocalOcrText);

        using var payload = JsonDocument.Parse(requestBody!);
        Assert.False(payload.RootElement.GetProperty("provider").GetProperty("allow_fallbacks").GetBoolean());
        Assert.True(payload.RootElement.GetProperty("provider").GetProperty("require_parameters").GetBoolean());
        Assert.True(payload.RootElement.GetProperty("disable_search").GetBoolean());
        Assert.Equal("disabled", payload.RootElement
            .GetProperty("thinking").GetProperty("type").GetString());
    }

    [Theory]
    [InlineData(RemoteReasoningMode.Low, RemoteReasoningWireFormat.ReasoningEffort, "reasoning_effort", "low")]
    [InlineData(RemoteReasoningMode.Disabled, RemoteReasoningWireFormat.EnableThinkingBoolean, "enable_thinking", "false")]
    [InlineData(RemoteReasoningMode.Disabled, RemoteReasoningWireFormat.ReasoningEnabledObject, "reasoning", "false")]
    public async Task ExplicitReasoningPolicy_UsesProfileSelectedWireShape(
        RemoteReasoningMode mode,
        RemoteReasoningWireFormat wireFormat,
        string propertyName,
        string expectedValue)
    {
        string? requestBody = null;
        using var client = new HttpClient(new DelegateHandler(async request =>
        {
            requestBody = await request.Content!.ReadAsStringAsync();
            return JsonResponse();
        }));
        var tester = new OpenAiCompatibleRemoteApiConnectionTester(
            client,
            new FakeCredentialService(Credential));

        await tester.TestAsync(CreateProfile() with
        {
            ReasoningMode = mode,
            ReasoningWireFormat = wireFormat,
        }, RemoteInputMode.LocalOcrText);

        using var payload = JsonDocument.Parse(requestBody!);
        var value = payload.RootElement.GetProperty(propertyName);
        var actual = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.False or JsonValueKind.True =>
                value.GetBoolean().ToString().ToLowerInvariant(),
            JsonValueKind.Object => value.GetProperty("enabled").GetBoolean()
                .ToString().ToLowerInvariant(),
            _ => null,
        };
        Assert.Equal(expectedValue, actual);
    }

    [Fact]
    public async Task AnthropicPromptOnly_OmitsOutputConfigAndReadsTextAfterThinkingBlock()
    {
        string? requestBody = null;
        using var client = new HttpClient(new DelegateHandler(async request =>
        {
            requestBody = await request.Content!.ReadAsStringAsync();
            return AnthropicJsonResponse(includeThinkingBlock: true);
        }));
        var tester = new OpenAiCompatibleRemoteApiConnectionTester(
            client,
            new FakeCredentialService(Credential));

        await tester.TestAsync(CreateProfile() with
        {
            Protocol = RemoteApiProtocol.AnthropicMessages,
            AuthenticationKind = RemoteApiAuthenticationKind.Bearer,
            StructuredOutputMode = RemoteStructuredOutputMode.PromptOnly,
        }, RemoteInputMode.LocalOcrText);

        using var payload = JsonDocument.Parse(requestBody!);
        Assert.False(payload.RootElement.TryGetProperty("output_config", out _));
        Assert.Contains("exactly these eight root keys", requestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JsonObjectMode_EmbedsTheExactParserContractInThePrompt()
    {
        string? requestBody = null;
        using var client = new HttpClient(new DelegateHandler(async request =>
        {
            requestBody = await request.Content!.ReadAsStringAsync();
            return JsonResponse();
        }));
        var tester = new OpenAiCompatibleRemoteApiConnectionTester(
            client,
            new FakeCredentialService(Credential));

        await tester.TestAsync(CreateProfile() with
        {
            StructuredOutputMode = RemoteStructuredOutputMode.JsonObject,
        }, RemoteInputMode.LocalOcrText);

        using var payload = JsonDocument.Parse(requestBody!);
        Assert.Equal("json_object", payload.RootElement
            .GetProperty("response_format").GetProperty("type").GetString());
        var systemPrompt = payload.RootElement
            .GetProperty("messages")[0].GetProperty("content").GetString();
        Assert.NotNull(systemPrompt);
        Assert.Contains("exactly these eight root keys", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("picforlater.analysis.v1", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("normalizedValue", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("visualFacts is an array of at most 3", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("320 characters", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("raw JSON only", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("title and summary", systemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("\"title\":\"\"", systemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyRequiredDraftContent_IsRejectedBeforeProfileVerification()
    {
        using var client = new HttpClient(new DelegateHandler(_ =>
            Task.FromResult(JsonResponse(title: string.Empty))));
        var tester = new OpenAiCompatibleRemoteApiConnectionTester(
            client,
            new FakeCredentialService(Credential));

        var exception = await Assert.ThrowsAsync<RemoteAnalysisProviderException>(() =>
            tester.TestAsync(CreateProfile(), RemoteInputMode.LocalOcrText));

        Assert.Equal("remote.invalid-content-draft", exception.ErrorCode);
        Assert.False(exception.IsRetryable);
    }

    [Fact]
    public async Task LoopbackNoneAuthentication_DoesNotRequireOrSendCredential()
    {
        using var client = new HttpClient(new DelegateHandler(request =>
        {
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("x-api-key"));
            return Task.FromResult(JsonResponse());
        }));
        var tester = new OpenAiCompatibleRemoteApiConnectionTester(
            client,
            new FakeCredentialService(secret: null));

        await tester.TestAsync(CreateProfile() with
        {
            BaseUri = new Uri("http://127.0.0.1:11434/v1/chat/completions"),
            EndpointTrustMode = RemoteEndpointTrustMode.LoopbackHttp,
            AuthenticationKind = RemoteApiAuthenticationKind.None,
        }, RemoteInputMode.LocalOcrText);
    }

    private static RemoteApiProfile CreateProfile() => new()
    {
        ProfileId = "profile",
        ProviderId = "provider",
        DisplayName = "Provider",
        EndpointId = "chat.v1",
        BaseUri = new Uri("https://api.example.test/v1/chat/completions"),
        ModelId = "remote-model",
        SupportedInputModes =
            [RemoteInputMode.LocalOcrText, RemoteInputMode.DirectImage],
        PromptVersion = "prompt.v1",
        OutputSchemaVersion = QwenStructuredOutputParser.SchemaVersion,
        MaxTextChars = 64_000,
        MaxImageBytes = 8 * 1024 * 1024,
        MaxOutputTokens = 1_024,
        TimeoutSeconds = 30,
        PrivacyUrl = new Uri("https://api.example.test/privacy"),
        TermsUrl = new Uri("https://api.example.test/terms"),
        RetentionTrainingStatement = "Test only.",
        RetentionTrainingVerifiedAtUtc = DateTimeOffset.UtcNow,
        CredentialReference = "credential",
        DisclosureVersion = "disclosure.v1",
        IsEnabled = true,
        ValidationState = RemoteApiProfileValidationState.Unverified,
        UpdatedAtUtc = DateTimeOffset.UtcNow,
    };

    private static HttpResponseMessage JsonResponse(string title = "Test")
    {
        var structured = JsonSerializer.Serialize(new
        {
            schemaVersion = QwenStructuredOutputParser.SchemaVersion,
            title,
            summary = "Synthetic capability test.",
            visualFacts = Array.Empty<string>(),
            categoryIds = Array.Empty<string>(),
            entities = Array.Empty<object>(),
            detectedLanguages = new[] { "en" },
            warnings = Array.Empty<string>(),
        });
        var response = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new { content = structured },
                },
            },
        });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(response, Encoding.UTF8, "application/json"),
        };
    }

    private static HttpResponseMessage AnthropicJsonResponse(bool includeThinkingBlock = false)
    {
        var openAiResponse = JsonResponse();
        using var openAiDocument = JsonDocument.Parse(
            openAiResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        var structured = openAiDocument.RootElement.GetProperty("choices")[0]
            .GetProperty("message").GetProperty("content").GetString();
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    content = includeThinkingBlock
                        ? new[]
                        {
                            new { type = "thinking", text = (string?)null },
                            new { type = "text", text = structured },
                        }
                        : new[] { new { type = "text", text = structured } },
                }),
                Encoding.UTF8,
                "application/json"),
        };
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request);
    }

    private sealed class FakeCredentialService(string? secret) : IRemoteApiCredentialService
    {
        public Task StoreAsync(
            string credentialReference,
            string value,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<string?> RetrieveAsync(
            string credentialReference,
            CancellationToken cancellationToken = default) => Task.FromResult(secret);

        public Task<bool> ExistsAsync(
            string credentialReference,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(!string.IsNullOrWhiteSpace(secret));

        public Task DeleteAsync(
            string credentialReference,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
