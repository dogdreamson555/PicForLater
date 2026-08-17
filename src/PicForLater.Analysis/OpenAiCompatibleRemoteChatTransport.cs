using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PicForLater.Core.Analysis;

namespace PicForLater.Analysis;

internal sealed record RemoteChatPrompt(
    string SystemText,
    string UserText,
    RemoteChatImage? Image = null);

internal sealed record RemoteChatImage(string MediaType, string Base64Data);

internal sealed class OpenAiCompatibleRemoteChatTransport
{
    private const int MaximumResponseBytes = 1_048_576;
    private const int MaximumAutomaticRetryDelaySeconds = 30;
    private readonly HttpClient _httpClient;
    private readonly IRemoteApiCredentialService _credentialService;
    private readonly IRemoteApiRequestAuthorizer _requestAuthorizer;

    public OpenAiCompatibleRemoteChatTransport(
        HttpClient httpClient,
        IRemoteApiCredentialService credentialService,
        IRemoteApiRequestAuthorizer requestAuthorizer)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _credentialService = credentialService
            ?? throw new ArgumentNullException(nameof(credentialService));
        _requestAuthorizer = requestAuthorizer
            ?? throw new ArgumentNullException(nameof(requestAuthorizer));
    }

    public Task EnsureAuthorizedAsync(
        RemoteApiProfileSnapshot profile,
        RemoteInputMode inputMode,
        CancellationToken cancellationToken) =>
        _requestAuthorizer.EnsureAuthorizedAsync(profile, inputMode, cancellationToken);

    public Task<bool> HasCredentialAsync(
        RemoteApiProfileSnapshot profile,
        CancellationToken cancellationToken) =>
        profile.AuthenticationKind == RemoteApiAuthenticationKind.None
            ? Task.FromResult(true)
            : _credentialService.ExistsAsync(profile.CredentialReference, cancellationToken);

    public async Task<bool> IsAvailableAsync(
        RemoteApiProfileSnapshot profile,
        RemoteInputMode inputMode,
        CancellationToken cancellationToken)
    {
        try
        {
            await EnsureAuthorizedAsync(profile, inputMode, cancellationToken).ConfigureAwait(false);
            return await HasCredentialAsync(profile, cancellationToken).ConfigureAwait(false);
        }
        catch (RemoteAnalysisProviderException)
        {
            return false;
        }
    }

    public async Task<string> CompleteAsync(
        RemoteApiProfileSnapshot profile,
        RemoteInputMode inputMode,
        RemoteChatPrompt prompt,
        CancellationToken cancellationToken)
    {
        await EnsureAuthorizedAsync(profile, inputMode, cancellationToken).ConfigureAwait(false);
        var credential = profile.AuthenticationKind == RemoteApiAuthenticationKind.None
            ? null
            : await _credentialService.RetrieveAsync(
                profile.CredentialReference,
                cancellationToken).ConfigureAwait(false);
        if (profile.AuthenticationKind != RemoteApiAuthenticationKind.None
            && string.IsNullOrWhiteSpace(credential))
        {
            throw new RemoteAnalysisProviderException(
                "remote.credential-unavailable",
                isRetryable: false);
        }

        try
        {
            return await SendAsync(
                profile,
                prompt,
                credential,
                Guid.NewGuid().ToString("N"),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new RemoteAnalysisProviderException("remote.timeout", true, exception);
        }
        catch (HttpRequestException exception)
        {
            throw new RemoteAnalysisProviderException("remote.network-failure", true, exception);
        }
    }

    private async Task<string> SendAsync(
        RemoteApiProfileSnapshot profile,
        RemoteChatPrompt prompt,
        string? credential,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(profile.TimeoutSeconds));
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            using var message = RemoteChatProtocol.CreateRequest(
                profile,
                prompt,
                credential,
                idempotencyKey);
            using var response = await _httpClient.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var responseBody = await RemoteChatProtocol.ReadBoundedResponseAsync(
                    response,
                    MaximumResponseBytes,
                    timeout.Token).ConfigureAwait(false);
                return RemoteChatProtocol.ReadStructuredContent(profile.Protocol, responseBody);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt == 1)
            {
                var retryDelay = GetRetryDelay(response);
                if (retryDelay <= TimeSpan.FromSeconds(MaximumAutomaticRetryDelaySeconds))
                {
                    await Task.Delay(retryDelay, timeout.Token).ConfigureAwait(false);
                    continue;
                }
            }

            throw CreateStatusException(response.StatusCode);
        }

        throw new RemoteAnalysisProviderException("remote.request-failed", true);
    }

    internal static RemoteAnalysisProviderException CreateStatusException(
        HttpStatusCode statusCode) => (int)statusCode switch
        {
            (int)HttpStatusCode.Unauthorized or (int)HttpStatusCode.Forbidden =>
                new RemoteAnalysisProviderException("remote.credential-rejected", false),
            (int)HttpStatusCode.TooManyRequests =>
                new RemoteAnalysisProviderException("remote.rate-limited", true),
            >= 500 and <= 599 =>
                new RemoteAnalysisProviderException("remote.server-failure", true),
            >= 300 and < 400 =>
                new RemoteAnalysisProviderException("remote.redirect-rejected", false),
            _ => new RemoteAnalysisProviderException("remote.request-rejected", false),
        };

    private static TimeSpan GetRetryDelay(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta >= TimeSpan.Zero)
        {
            return delta;
        }

        if (retryAfter?.Date is { } date)
        {
            return date <= DateTimeOffset.UtcNow ? TimeSpan.Zero : date - DateTimeOffset.UtcNow;
        }

        return TimeSpan.FromMilliseconds(250 + Random.Shared.Next(0, 251));
    }
}

internal static class RemoteChatProtocol
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static HttpRequestMessage CreateRequest(
        RemoteApiProfileSnapshot profile,
        RemoteChatPrompt prompt,
        string? credential,
        string idempotencyKey)
    {
        if (!RemoteEndpointPolicy.IsAllowed(profile.BaseUri, profile.EndpointTrustMode))
        {
            throw new RemoteAnalysisProviderException("remote.endpoint-rejected", false);
        }

        object payload = profile.Protocol switch
        {
            RemoteApiProtocol.OpenAiChatCompletions => CreateOpenAiPayload(profile, prompt),
            RemoteApiProtocol.AnthropicMessages => CreateAnthropicPayload(profile, prompt),
            _ => throw new RemoteAnalysisProviderException("remote.protocol-unsupported", false),
        };
        var request = new HttpRequestMessage(HttpMethod.Post, profile.BaseUri)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonOptions),
                Encoding.UTF8,
                "application/json"),
        };
        ApplyAuthentication(request, profile.AuthenticationKind, credential);
        if (profile.Protocol == RemoteApiProtocol.AnthropicMessages)
        {
            request.Headers.TryAddWithoutValidation(
                "anthropic-version",
                string.IsNullOrWhiteSpace(profile.ApiVersion) ? "2023-06-01" : profile.ApiVersion);
        }

        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    public static async Task<string> ReadBoundedResponseAsync(
        HttpResponseMessage response,
        int maximumResponseBytes,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > maximumResponseBytes)
        {
            throw new RemoteAnalysisProviderException("remote.response-too-large", false);
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8_192];
        while (true)
        {
            var read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > maximumResponseBytes)
            {
                throw new RemoteAnalysisProviderException("remote.response-too-large", false);
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    public static string ReadStructuredContent(RemoteApiProtocol protocol, string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
            var content = protocol switch
            {
                RemoteApiProtocol.OpenAiChatCompletions => document.RootElement
                    .GetProperty("choices")[0].GetProperty("message").GetProperty("content"),
                RemoteApiProtocol.AnthropicMessages => ReadAnthropicText(document.RootElement),
                _ => throw new JsonException(),
            };
            if (content.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(content.GetString()))
            {
                throw new JsonException();
            }

            return content.GetString()!;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
                                          or IndexOutOfRangeException)
        {
            throw new RemoteAnalysisProviderException("remote.invalid-response", false, exception);
        }
    }

    private static JsonElement ReadAnthropicText(JsonElement root)
    {
        foreach (var block in root.GetProperty("content").EnumerateArray())
        {
            if (block.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(text.GetString()))
            {
                return text;
            }
        }

        throw new JsonException();
    }

    private static Dictionary<string, object?> CreateOpenAiPayload(
        RemoteApiProfileSnapshot profile,
        RemoteChatPrompt prompt)
    {
        object userContent = prompt.Image is null
            ? prompt.UserText
            : new object[]
            {
                new { type = "text", text = prompt.UserText },
                new
                {
                    type = "image_url",
                    image_url = new
                    {
                        url = $"data:{prompt.Image.MediaType};base64,{prompt.Image.Base64Data}",
                    },
                },
            };
        object? responseFormat = profile.StructuredOutputMode switch
        {
            RemoteStructuredOutputMode.JsonSchema => new
            {
                type = "json_schema",
                json_schema = new
                {
                    name = "picforlater_analysis",
                    strict = true,
                    schema = RemoteStructuredOutputContract.JsonSchema,
                },
            },
            RemoteStructuredOutputMode.JsonObject => new { type = "json_object" },
            RemoteStructuredOutputMode.PromptOnly => null,
            _ => throw new RemoteAnalysisProviderException("remote.protocol-unsupported", false),
        };
        var payload = new Dictionary<string, object?>
        {
            ["model"] = profile.ModelId,
            ["messages"] = new object[]
            {
                new { role = "system", content = prompt.SystemText },
                new { role = "user", content = userContent },
            },
            ["max_tokens"] = profile.MaxOutputTokens,
        };
        if (responseFormat is not null)
        {
            payload["response_format"] = responseFormat;
        }
        if (profile.DisableProviderFallbacks)
        {
            payload["provider"] = new { allow_fallbacks = false, require_parameters = true };
        }

        if (profile.DisableExternalSearch)
        {
            payload["disable_search"] = true;
        }

        if (profile.ReasoningMode == RemoteReasoningMode.ProviderDefault)
        {
            return payload;
        }

        switch (profile.ReasoningWireFormat)
        {
            case RemoteReasoningWireFormat.ThinkingObject
                when profile.ReasoningMode == RemoteReasoningMode.Disabled:
                payload["thinking"] = new { type = "disabled" };
                break;
            case RemoteReasoningWireFormat.ReasoningEffort:
                payload["reasoning_effort"] = profile.ReasoningMode switch
                {
                    RemoteReasoningMode.Disabled => "none",
                    RemoteReasoningMode.Low => "low",
                    RemoteReasoningMode.Medium => "medium",
                    RemoteReasoningMode.High => "high",
                    _ => throw new RemoteAnalysisProviderException(
                        "remote.protocol-unsupported", false),
                };
                break;
            case RemoteReasoningWireFormat.EnableThinkingBoolean
                when profile.ReasoningMode == RemoteReasoningMode.Disabled:
                payload["enable_thinking"] = false;
                break;
            case RemoteReasoningWireFormat.ReasoningEnabledObject
                when profile.ReasoningMode == RemoteReasoningMode.Disabled:
                payload["reasoning"] = new { enabled = false };
                break;
            default:
                throw new RemoteAnalysisProviderException("remote.protocol-unsupported", false);
        }

        return payload;
    }

    private static object CreateAnthropicPayload(
        RemoteApiProfileSnapshot profile,
        RemoteChatPrompt prompt)
    {
        object userContent = prompt.Image is null
            ? prompt.UserText
            : new object[]
            {
                new { type = "text", text = prompt.UserText },
                new
                {
                    type = "image",
                    source = new
                    {
                        type = "base64",
                        media_type = prompt.Image.MediaType,
                        data = prompt.Image.Base64Data,
                    },
                },
            };
        var payload = new Dictionary<string, object?>
        {
            ["model"] = profile.ModelId,
            ["max_tokens"] = profile.MaxOutputTokens,
            ["system"] = prompt.SystemText,
            ["messages"] = new[] { new { role = "user", content = userContent } },
        };
        if (profile.StructuredOutputMode == RemoteStructuredOutputMode.JsonSchema)
        {
            payload["output_config"] = new
            {
                format = new
                {
                    type = "json_schema",
                    schema = RemoteStructuredOutputContract.JsonSchema,
                },
            };
        }
        else if (profile.StructuredOutputMode != RemoteStructuredOutputMode.PromptOnly)
        {
            throw new RemoteAnalysisProviderException("remote.protocol-unsupported", false);
        }

        return payload;
    }

    private static void ApplyAuthentication(
        HttpRequestMessage request,
        RemoteApiAuthenticationKind kind,
        string? credential)
    {
        switch (kind)
        {
            case RemoteApiAuthenticationKind.Bearer:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
                break;
            case RemoteApiAuthenticationKind.XApiKey:
                request.Headers.TryAddWithoutValidation("x-api-key", credential);
                break;
            case RemoteApiAuthenticationKind.None:
                break;
            default:
                throw new RemoteAnalysisProviderException("remote.authentication-unsupported", false);
        }
    }
}
