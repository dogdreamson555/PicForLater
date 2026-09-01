using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using PicForLater.Analysis;
using PicForLater.Core.Analysis;
using PicForLater.Infrastructure.Analysis;
using Xunit.Abstractions;

namespace PicForLater.IntegrationTests;

public sealed class RealRemoteApiContractTests(ITestOutputHelper output)
{
    private const string RunVariable = "PICFORLATER_RUN_REAL_REMOTE_CONTRACT";
    private const string QwenVisionRunVariable =
        "PICFORLATER_RUN_REAL_QWEN_VISION_CONTRACT";
    private const string CredentialFileVariable = "PICFORLATER_REMOTE_CONTRACT_CREDENTIAL_FILE";
    private const string MetricsPathVariable = "PICFORLATER_REMOTE_CONTRACT_METRICS_PATH";
    private const string SamplesVariable = "PICFORLATER_REMOTE_CONTRACT_SAMPLES";
    private const string ModelVariable = "PICFORLATER_REMOTE_CONTRACT_MODEL";
    private const string QwenVisionModelVariable =
        "PICFORLATER_QWEN_VISION_CONTRACT_MODEL";
    private const decimal CacheHitUsdPerMillion = 0.0028m;
    private const decimal CacheMissUsdPerMillion = 0.14m;
    private const decimal OutputUsdPerMillion = 0.28m;

    [ExplicitRealApiFact]
    [Trait("Category", "ExplicitRealApiContract")]
    public async Task DeepSeekRemoteOcrText_SatisfiesStrictContractAndWritesOnlySafeMetrics()
    {
        var credential = ReadCredential();
        var samples = ReadSampleCount();
        var modelId = Environment.GetEnvironmentVariable(ModelVariable)?.Trim();
        if (string.IsNullOrWhiteSpace(modelId))
        {
            modelId = "deepseek-v4-flash";
        }

        using var recordingHandler = new RecordingHandler(SafeRemoteHttpMessageHandler.Create());
        using var httpClient = new HttpClient(recordingHandler);
        var provider = new OpenAiCompatibleRemoteOcrTextProvider(
            httpClient,
            new MemoryCredentialService(credential),
            AllowRequestAuthorizer.Instance);
        var results = new List<ContractSample>(samples);
        for (var index = 0; index < samples; index++)
        {
            var imageOpenCount = 0;
            var request = CreateRequest(modelId, () => imageOpenCount++);
            var timer = Stopwatch.StartNew();
            VisionStructuredResult result;
            try
            {
                result = await provider.AnalyzeAsync(request);
            }
            catch
            {
                if (recordingHandler.Exchanges.LastOrDefault() is { } failedExchange)
                {
                    output.WriteLine(
                        "Safe response diagnostics: status={0}, model={1}, finishReason={2}, contentKind={3}, contentChars={4}, reasoningChars={5}, promptTokens={6}, completionTokens={7}, totalTokens={8}, structure={9}",
                        failedExchange.StatusCode,
                        failedExchange.Metadata.Model ?? "(missing)",
                        failedExchange.Metadata.FinishReason ?? "(missing)",
                        failedExchange.Metadata.ContentKind,
                        failedExchange.Metadata.ContentCharacters,
                        failedExchange.Metadata.ReasoningCharacters,
                        failedExchange.Usage.PromptTokens,
                        failedExchange.Usage.CompletionTokens,
                        failedExchange.Usage.TotalTokens,
                        failedExchange.Metadata.StructureShape);
                }

                throw;
            }
            timer.Stop();

            Assert.Equal(0, imageOpenCount);
            Assert.False(string.IsNullOrWhiteSpace(result.Draft.Title));
            Assert.False(string.IsNullOrWhiteSpace(result.Draft.Summary));
            Assert.Empty(result.Draft.SuggestedCategoryIds);
            Assert.Equal(RemoteInputMode.LocalOcrText, result.Provenance.RemoteInputMode);
            var exchange = recordingHandler.Exchanges[index];
            results.Add(new ContractSample(
                index + 1,
                Math.Round(timer.Elapsed.TotalMilliseconds, 2),
                Math.Round(exchange.TimeToHeaders.TotalMilliseconds, 2),
                exchange.RequestBytes,
                exchange.ResponseBytes,
                exchange.Usage.PromptTokens,
                exchange.Usage.PromptCacheHitTokens,
                exchange.Usage.PromptCacheMissTokens,
                exchange.Usage.CompletionTokens,
                exchange.Usage.TotalTokens,
                EstimateCostUsd(exchange.Usage)));
        }

        var report = new ContractMeasurementReport(
            "picforlater.remote-contract-measurement.v1",
            DateTimeOffset.UtcNow,
            "deepseek.official",
            "api.deepseek.com",
            modelId,
            RemoteInputMode.LocalOcrText.ToString(),
            results,
            Math.Round(Median(results.Select(item => item.TotalMilliseconds)), 2),
            Math.Round(Median(results.Select(item => item.TimeToHeadersMilliseconds)), 2),
            results.Sum(item => item.EstimatedCostUsd),
            "https://api-docs.deepseek.com/quick_start/pricing/",
            new DateOnly(2026, 8, 1));
        await WriteSafeMetricsAsync(report);

        output.WriteLine(
            "Real contract passed: provider={0}, model={1}, samples={2}, medianTotalMs={3}, medianHeadersMs={4}, estimatedUsd={5}",
            report.ProviderId,
            report.ModelId,
            report.Samples.Count,
            report.MedianTotalMilliseconds,
            report.MedianTimeToHeadersMilliseconds,
            report.EstimatedTotalCostUsd.ToString("0.00000000", CultureInfo.InvariantCulture));
    }

    [ExplicitQwenVisionRealApiFact]
    [Trait("Category", "ExplicitRealApiContract")]
    [Trait("ContractProvider", "QwenVision")]
    public async Task QwenRemoteVisionConnectionTest_SatisfiesStrictContractWithBuiltInImage()
    {
        var credential = ReadCredentialAfterMarker("this is qwen api key");
        var modelId = Environment.GetEnvironmentVariable(QwenVisionModelVariable)?.Trim();
        if (string.IsNullOrWhiteSpace(modelId))
        {
            modelId = "qwen3-vl-flash-2026-01-22";
        }

        using var recordingHandler = new RecordingHandler(SafeRemoteHttpMessageHandler.Create());
        using var httpClient = new HttpClient(recordingHandler);
        var tester = new OpenAiCompatibleRemoteApiConnectionTester(
            httpClient,
            new MemoryCredentialService(credential));
        var profile = new RemoteApiProfile
        {
            ProfileId = "contract-qwen-vision",
            ProviderId = "alibaba.bailian.qwen",
            DisplayName = "Alibaba Model Studio / Qwen",
            EndpointId = "alibaba.bailian.qwen.chat-completions",
            BaseUri = new Uri(
                "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions"),
            ModelId = modelId,
            SupportedInputModes = [RemoteInputMode.DirectImage],
            PromptVersion = "picforlater.remote-analysis.v3",
            OutputSchemaVersion = QwenStructuredOutputParser.SchemaVersion,
            MaxTextChars = 64_000,
            MaxImageBytes = 8 * 1024 * 1024,
            MaxOutputTokens = 1_024,
            TimeoutSeconds = 90,
            PrivacyUrl = new Uri(
                "https://terms.alicdn.com/legal-agreement/terms/privacy_policy_full/20221129171420545/20221129171420545.html"),
            TermsUrl = new Uri(
                "https://terms.alicdn.com/legal-agreement/terms/suit_bu1_ali_cloud/suit_bu1_ali_cloud202112211045_86198.html"),
            RetentionTrainingStatement = "Explicit contract test only.",
            RetentionTrainingVerifiedAtUtc = DateTimeOffset.Parse(
                "2026-08-01T00:00:00Z",
                CultureInfo.InvariantCulture),
            CredentialReference = "contract-memory-only",
            DisclosureVersion = "contract-qwen-vision.v1",
            Protocol = RemoteApiProtocol.OpenAiChatCompletions,
            AuthenticationKind = RemoteApiAuthenticationKind.Bearer,
            StructuredOutputMode = RemoteStructuredOutputMode.JsonObject,
            EndpointTrustMode = RemoteEndpointTrustMode.FixedHttps,
            ReasoningMode = RemoteReasoningMode.Disabled,
            ReasoningWireFormat = RemoteReasoningWireFormat.EnableThinkingBoolean,
            IsEnabled = true,
            ValidationState = RemoteApiProfileValidationState.Unverified,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

        var timer = Stopwatch.StartNew();
        try
        {
            await tester.TestAsync(profile, RemoteInputMode.DirectImage);
        }
        catch
        {
            if (recordingHandler.Exchanges.LastOrDefault() is { } failedExchange)
            {
                output.WriteLine(
                    "Safe Qwen diagnostics: status={0}, model={1}, finishReason={2}, contentKind={3}, contentChars={4}, reasoningChars={5}, totalTokens={6}, structure={7}",
                    failedExchange.StatusCode,
                    failedExchange.Metadata.Model ?? "(missing)",
                    failedExchange.Metadata.FinishReason ?? "(missing)",
                    failedExchange.Metadata.ContentKind,
                    failedExchange.Metadata.ContentCharacters,
                    failedExchange.Metadata.ReasoningCharacters,
                    failedExchange.Usage.TotalTokens,
                    failedExchange.Metadata.StructureShape);
            }

            throw;
        }
        timer.Stop();

        var exchange = Assert.Single(recordingHandler.Exchanges);
        Assert.Equal((int)System.Net.HttpStatusCode.OK, exchange.StatusCode);
        Assert.Equal(JsonValueKind.String, exchange.Metadata.ContentKind);
        Assert.Contains("missing:[]", exchange.Metadata.StructureShape, StringComparison.Ordinal);
        Assert.Contains("unexpected:0", exchange.Metadata.StructureShape, StringComparison.Ordinal);
        output.WriteLine(
            "Real Qwen vision contract passed: provider={0}, model={1}, totalMs={2}, headersMs={3}, requestBytes={4}, responseBytes={5}, totalTokens={6}, structure={7}",
            profile.ProviderId,
            profile.ModelId,
            Math.Round(timer.Elapsed.TotalMilliseconds, 2),
            Math.Round(exchange.TimeToHeaders.TotalMilliseconds, 2),
            exchange.RequestBytes,
            exchange.ResponseBytes,
            exchange.Usage.TotalTokens,
            exchange.Metadata.StructureShape);
    }

    private static VisionAnalysisRequest CreateRequest(string modelId, Action onImageOpen)
    {
        var provenance = new AnalysisProvenance(
            "contract.synthetic-ocr",
            "synthetic",
            "1",
            new Dictionary<string, string>(),
            "contract.synthetic-ocr.v1",
            AnalysisExecutionLocation.Local,
            AnalysisOutputKind.OcrFacts);
        var ocr = new OcrDocument(
            "PicForLater synthetic API contract test.\nProject review: 2026-08-18 14:30, Room A.",
            [],
            ["en"],
            [],
            provenance,
            1,
            1);
        var profile = ModelProfileSnapshot.Default with
        {
            ExecutionBackend = AnalysisExecutionBackend.RemoteApi,
            RemoteInputMode = RemoteInputMode.LocalOcrText,
            RemoteApiProfile = new RemoteApiProfileSnapshot
            {
                ProfileId = "contract-deepseek",
                ProviderId = "deepseek.official",
                EndpointId = "deepseek.official.chat-completions",
                BaseUri = new Uri("https://api.deepseek.com/chat/completions"),
                ModelId = modelId,
                PromptVersion = "picforlater.remote-analysis.v3",
                OutputSchemaVersion = QwenStructuredOutputParser.SchemaVersion,
                MaxTextChars = 4_096,
                MaxImageBytes = 1_024,
                MaxOutputTokens = 512,
                TimeoutSeconds = 90,
                CredentialReference = "contract-memory-only",
                ConsentVersion = "contract-explicit.v1",
                Protocol = RemoteApiProtocol.OpenAiChatCompletions,
                AuthenticationKind = RemoteApiAuthenticationKind.Bearer,
                StructuredOutputMode = RemoteStructuredOutputMode.JsonObject,
                EndpointTrustMode = RemoteEndpointTrustMode.FixedHttps,
                ReasoningMode = RemoteReasoningMode.Disabled,
                ReasoningWireFormat = RemoteReasoningWireFormat.ThinkingObject,
            },
        };
        return new VisionAnalysisRequest(
            _ =>
            {
                onImageOpen();
                return ValueTask.FromException<Stream>(
                    new InvalidOperationException("RemoteOcrText must not open an image."));
            },
            "not-read.png",
            ocr,
            new AnalysisCompositionContext([]),
            profile)
        {
            ReferenceTimeUtc = DateTimeOffset.Parse(
                "2026-08-01T00:00:00Z",
                CultureInfo.InvariantCulture),
            TimeZoneId = "China Standard Time",
        };
    }

    private static string ReadCredential()
    {
        var path = Environment.GetEnvironmentVariable(CredentialFileVariable);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new InvalidOperationException(
                $"{CredentialFileVariable} must name an existing credential file.");
        }

        var secret = File.ReadLines(path)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith("sk-", StringComparison.Ordinal));
        return !string.IsNullOrWhiteSpace(secret)
            ? secret
            : throw new InvalidOperationException("No supported credential entry was found.");
    }

    private static string ReadCredentialAfterMarker(string marker)
    {
        var path = Environment.GetEnvironmentVariable(CredentialFileVariable);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new InvalidOperationException(
                $"{CredentialFileVariable} must name an existing credential file.");
        }

        var lines = File.ReadAllLines(path);
        var markerIndex = Array.FindIndex(
            lines,
            line => line.Contains(marker, StringComparison.OrdinalIgnoreCase));
        var secret = markerIndex < 0
            ? null
            : lines.Skip(markerIndex + 1)
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.StartsWith("sk-", StringComparison.Ordinal));
        return !string.IsNullOrWhiteSpace(secret)
            ? secret
            : throw new InvalidOperationException(
                $"No credential was found after the '{marker}' marker.");
    }

    private static int ReadSampleCount() =>
        int.TryParse(
            Environment.GetEnvironmentVariable(SamplesVariable),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var value)
            ? Math.Clamp(value, 1, 5)
            : 3;

    private static decimal EstimateCostUsd(RemoteUsage usage)
    {
        var knownInput = usage.PromptCacheHitTokens + usage.PromptCacheMissTokens;
        var unclassifiedInput = Math.Max(0, usage.PromptTokens - knownInput);
        return decimal.Round(
            ((usage.PromptCacheHitTokens * CacheHitUsdPerMillion)
             + ((usage.PromptCacheMissTokens + unclassifiedInput) * CacheMissUsdPerMillion)
             + (usage.CompletionTokens * OutputUsdPerMillion)) / 1_000_000m,
            10,
            MidpointRounding.AwayFromZero);
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        return ordered.Length % 2 == 1
            ? ordered[ordered.Length / 2]
            : (ordered[(ordered.Length / 2) - 1] + ordered[ordered.Length / 2]) / 2;
    }

    private static async Task WriteSafeMetricsAsync(ContractMeasurementReport report)
    {
        var path = Environment.GetEnvironmentVariable(MetricsPathVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(
            fullPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
            })).ConfigureAwait(false);
    }

    private sealed class RecordingHandler(HttpMessageHandler innerHandler)
        : DelegatingHandler(innerHandler)
    {
        public List<RemoteExchange> Exchanges { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var requestBytes = request.Content?.Headers.ContentLength ?? 0;
            var timer = Stopwatch.StartNew();
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var timeToHeaders = timer.Elapsed;
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            var usage = ParseUsage(bytes);
            var metadata = ParseMetadata(bytes);
            var replacement = new ByteArrayContent(bytes);
            foreach (var header in response.Content.Headers)
            {
                replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            response.Content.Dispose();
            response.Content = replacement;
            Exchanges.Add(new RemoteExchange(
                (int)response.StatusCode,
                requestBytes,
                bytes.LongLength,
                timeToHeaders,
                usage,
                metadata));
            return response;
        }

        private static RemoteUsage ParseUsage(byte[] responseBody)
        {
            try
            {
                using var document = JsonDocument.Parse(responseBody);
                if (!document.RootElement.TryGetProperty("usage", out var usage))
                {
                    return default;
                }

                return new RemoteUsage(
                    ReadInt32(usage, "prompt_tokens"),
                    ReadInt32(usage, "prompt_cache_hit_tokens"),
                    ReadInt32(usage, "prompt_cache_miss_tokens"),
                    ReadInt32(usage, "completion_tokens"),
                    ReadInt32(usage, "total_tokens"));
            }
            catch (JsonException)
            {
                return default;
            }
        }

        private static int ReadInt32(JsonElement value, string propertyName) =>
            value.TryGetProperty(propertyName, out var property)
            && property.TryGetInt32(out var result)
                ? result
                : 0;

        private static SafeResponseMetadata ParseMetadata(byte[] responseBody)
        {
            try
            {
                using var document = JsonDocument.Parse(responseBody);
                var root = document.RootElement;
                var model = root.TryGetProperty("model", out var modelValue)
                    && modelValue.ValueKind == JsonValueKind.String
                        ? modelValue.GetString()
                        : null;
                if (!root.TryGetProperty("choices", out var choices)
                    || choices.ValueKind != JsonValueKind.Array
                    || choices.GetArrayLength() == 0)
                {
                    return new SafeResponseMetadata(
                        model, null, JsonValueKind.Undefined, 0, 0, "content-unavailable");
                }

                var choice = choices[0];
                var finishReason = choice.TryGetProperty("finish_reason", out var finish)
                    && finish.ValueKind == JsonValueKind.String
                        ? finish.GetString()
                        : null;
                if (!choice.TryGetProperty("message", out var message))
                {
                    return new SafeResponseMetadata(
                        model, finishReason, JsonValueKind.Undefined, 0, 0, "content-unavailable");
                }

                var contentKind = message.TryGetProperty("content", out var content)
                    ? content.ValueKind
                    : JsonValueKind.Undefined;
                var contentCharacters = contentKind == JsonValueKind.String
                    ? content.GetString()?.Length ?? 0
                    : 0;
                var reasoningCharacters = message.TryGetProperty("reasoning_content", out var reasoning)
                    && reasoning.ValueKind == JsonValueKind.String
                        ? reasoning.GetString()?.Length ?? 0
                        : 0;
                var structureShape = contentKind == JsonValueKind.String
                    ? DescribeStructure(content.GetString())
                    : "content-not-string";
                return new SafeResponseMetadata(
                    model,
                    finishReason,
                    contentKind,
                    contentCharacters,
                    reasoningCharacters,
                    structureShape);
            }
            catch (JsonException)
            {
                return new SafeResponseMetadata(
                    null, null, JsonValueKind.Undefined, 0, 0, "response-not-json");
            }
        }

        private static string DescribeStructure(string? content)
        {
            try
            {
                using var document = JsonDocument.Parse(content ?? string.Empty);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return $"root:{root.ValueKind}";
                }

                string[] requiredKeys =
                [
                    "schemaVersion", "title", "summary", "visualFacts", "categoryIds",
                    "entities", "detectedLanguages", "warnings",
                ];
                var missing = requiredKeys.Where(key => !root.TryGetProperty(key, out _)).ToArray();
                var unexpectedCount = root.EnumerateObject()
                    .Count(property => !requiredKeys.Contains(property.Name, StringComparer.Ordinal));
                var kinds = string.Join(",", requiredKeys
                    .Where(key => root.TryGetProperty(key, out _))
                    .Select(key => $"{key}:{root.GetProperty(key).ValueKind}"));
                var entityCount = root.TryGetProperty("entities", out var entities)
                    && entities.ValueKind == JsonValueKind.Array
                        ? entities.GetArrayLength()
                        : -1;
                string[] stringArrayKeys =
                [
                    "visualFacts", "categoryIds", "detectedLanguages", "warnings",
                ];
                var arraySizes = string.Join(",", stringArrayKeys.Select(key =>
                {
                    if (!root.TryGetProperty(key, out var array)
                        || array.ValueKind != JsonValueKind.Array)
                    {
                        return $"{key}:not-array";
                    }

                    var lengths = array.EnumerateArray()
                        .Select(item => item.ValueKind == JsonValueKind.String
                            ? item.GetString()?.Length ?? 0
                            : -1)
                        .ToArray();
                    return $"{key}:{lengths.Length}/{(lengths.Length == 0 ? 0 : lengths.Max())}";
                }));
                return $"missing:[{string.Join(',', missing)}];unexpected:{unexpectedCount};entities:{entityCount};arrays:[{arraySizes}];kinds:[{kinds}]";
            }
            catch (JsonException)
            {
                return "content-not-json";
            }
        }
    }

    private sealed class MemoryCredentialService(string credential) : IRemoteApiCredentialService
    {
        public Task StoreAsync(string credentialReference, string secret, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> ExistsAsync(string credentialReference, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<string?> RetrieveAsync(string credentialReference, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(credential);

        public Task DeleteAsync(string credentialReference, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class AllowRequestAuthorizer : IRemoteApiRequestAuthorizer
    {
        public static AllowRequestAuthorizer Instance { get; } = new();

        public Task EnsureAuthorizedAsync(
            RemoteApiProfileSnapshot profile,
            RemoteInputMode inputMode,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private readonly record struct RemoteUsage(
        int PromptTokens,
        int PromptCacheHitTokens,
        int PromptCacheMissTokens,
        int CompletionTokens,
        int TotalTokens);

    private sealed record RemoteExchange(
        int StatusCode,
        long RequestBytes,
        long ResponseBytes,
        TimeSpan TimeToHeaders,
        RemoteUsage Usage,
        SafeResponseMetadata Metadata);

    private readonly record struct SafeResponseMetadata(
        string? Model,
        string? FinishReason,
        JsonValueKind ContentKind,
        int ContentCharacters,
        int ReasoningCharacters,
        string StructureShape);

    private sealed record ContractSample(
        int Sample,
        double TotalMilliseconds,
        double TimeToHeadersMilliseconds,
        long RequestBytes,
        long ResponseBytes,
        int PromptTokens,
        int PromptCacheHitTokens,
        int PromptCacheMissTokens,
        int CompletionTokens,
        int TotalTokens,
        decimal EstimatedCostUsd);

    private sealed record ContractMeasurementReport(
        string SchemaVersion,
        DateTimeOffset MeasuredAtUtc,
        string ProviderId,
        string EndpointHost,
        string ModelId,
        string InputMode,
        IReadOnlyList<ContractSample> Samples,
        double MedianTotalMilliseconds,
        double MedianTimeToHeadersMilliseconds,
        decimal EstimatedTotalCostUsd,
        string PricingSource,
        DateOnly PricingVerifiedAt);

    public sealed class ExplicitRealApiFactAttribute : FactAttribute
    {
        public ExplicitRealApiFactAttribute()
        {
            if (!string.Equals(
                    Environment.GetEnvironmentVariable(RunVariable),
                    "1",
                    StringComparison.Ordinal))
            {
                Skip = $"Set {RunVariable}=1 and invoke tests/run-real-remote-contract.ps1 explicitly.";
            }
        }
    }

    public sealed class ExplicitQwenVisionRealApiFactAttribute : FactAttribute
    {
        public ExplicitQwenVisionRealApiFactAttribute()
        {
            if (!string.Equals(
                    Environment.GetEnvironmentVariable(QwenVisionRunVariable),
                    "1",
                    StringComparison.Ordinal))
            {
                Skip = $"Set {QwenVisionRunVariable}=1 and invoke tests/run-real-qwen-vision-contract.ps1 explicitly.";
            }
        }
    }
}
