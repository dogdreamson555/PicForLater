using System.Net;
using System.Text;
using System.Text.Json;
using PicForLater.Core.Analysis;

namespace PicForLater.Analysis.Tests;

public sealed class OpenAiCompatibleRemoteVisionProviderTests
{
    private const string Credential = "test-only-secret";

    [Fact]
    public async Task AnalyzeAsync_UploadsOnlySanitizedCopyAndReturnsUnlocatedCandidates()
    {
        string? requestBody = null;
        using var httpClient = new HttpClient(new DelegateHandler(async request =>
        {
            requestBody = await request.Content!.ReadAsStringAsync();
            return JsonResponse(CreateStructuredOutput());
        }));
        var preprocessor = new FakePreprocessor([9, 8, 7, 6]);
        var provider = new OpenAiCompatibleRemoteVisionProvider(
            httpClient,
            new FakeCredentialService(Credential),
            AllowAllRequestAuthorizer.Instance,
            preprocessor);
        var imageOpenCount = 0;

        var result = await provider.AnalyzeAsync(
            CreateRequest(() => imageOpenCount++));

        Assert.Equal(1, imageOpenCount);
        Assert.Equal(1, preprocessor.CallCount);
        Assert.Equal(4_096, preprocessor.MaximumBytes);
        Assert.True(preprocessor.CopyStreamDisposed);
        Assert.NotNull(requestBody);
        using var payload = JsonDocument.Parse(requestBody);
        var content = payload.RootElement
            .GetProperty("messages")[1]
            .GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Equal(
            "data:image/png;base64,CQgHBg==",
            content[1].GetProperty("image_url").GetProperty("url").GetString());
        Assert.False(content[1].GetProperty("image_url").TryGetProperty("detail", out _));
        Assert.False(payload.RootElement.TryGetProperty("n", out _));
        Assert.Contains("\"image_url\"", requestBody, StringComparison.Ordinal);
        Assert.DoesNotContain("AQID", requestBody, StringComparison.Ordinal);
        Assert.DoesNotContain("private-original-name.png", requestBody, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE_OCR_SHOULD_NOT_LEAK", requestBody, StringComparison.Ordinal);
        Assert.DoesNotContain("private category", requestBody, StringComparison.Ordinal);
        Assert.DoesNotContain("contentHash", requestBody, StringComparison.Ordinal);
        Assert.DoesNotContain("boundingBox", requestBody, StringComparison.Ordinal);
        Assert.DoesNotContain(Credential, requestBody, StringComparison.Ordinal);
        Assert.Empty(result.Draft.SuggestedCategoryIds);
        Assert.Equal("7月20日项目评审", result.Draft.Title);
        Assert.Equal(RemoteInputMode.DirectImage, result.Provenance.RemoteInputMode);
        var candidate = Assert.Single(result.Draft.EntityCandidates);
        Assert.Equal("Model", candidate.Source);
        Assert.Null(candidate.BoundingBox);
        Assert.Equal("RemoteVisionNoLocalOcrEvidence", candidate.AmbiguityReason);
        Assert.Contains(
            "remote.direct-image-no-local-ocr-evidence",
            result.Warnings);
    }

    [Fact]
    public async Task MissingCredential_DoesNotOpenOrProcessImageAndSendsNothing()
    {
        var sendCount = 0;
        using var httpClient = new HttpClient(new DelegateHandler(_ =>
        {
            sendCount++;
            throw new InvalidOperationException("No request should be sent.");
        }));
        var preprocessor = new FakePreprocessor([9, 8, 7, 6]);
        var provider = new OpenAiCompatibleRemoteVisionProvider(
            httpClient,
            new FakeCredentialService(secret: null),
            AllowAllRequestAuthorizer.Instance,
            preprocessor);
        var imageOpenCount = 0;
        var request = CreateRequest(() => imageOpenCount++);

        Assert.False(await provider.IsAvailableAsync(request.ProfileSnapshot));
        var exception = await Assert.ThrowsAsync<RemoteAnalysisProviderException>(
            () => provider.AnalyzeAsync(request));

        Assert.Equal("remote.credential-unavailable", exception.ErrorCode);
        Assert.False(exception.IsRetryable);
        Assert.Equal(0, imageOpenCount);
        Assert.Equal(0, preprocessor.CallCount);
        Assert.Equal(0, sendCount);
    }

    [Fact]
    public async Task OneExcessVisualFact_IsBoundedAndReportedWithoutASecondRequest()
    {
        var sendCount = 0;
        using var httpClient = new HttpClient(new DelegateHandler(_ =>
        {
            sendCount++;
            return Task.FromResult(JsonResponse(CreateStructuredOutput(
                ["事实一", "事实二", "事实三", "事实四"])));
        }));
        var provider = new OpenAiCompatibleRemoteVisionProvider(
            httpClient,
            new FakeCredentialService(Credential),
            AllowAllRequestAuthorizer.Instance,
            new FakePreprocessor([9, 8, 7, 6]));

        var result = await provider.AnalyzeAsync(CreateRequest(() => { }));

        Assert.Equal(1, sendCount);
        Assert.Equal(["事实一", "事实二", "事实三"], result.VisualFacts);
        Assert.Contains(
            "remote.output.visual-facts-truncated-to-schema",
            result.Warnings);
    }

    [Fact]
    public async Task RevokedAuthorization_DoesNotOpenOrProcessImageAndSendsNothing()
    {
        var sendCount = 0;
        using var httpClient = new HttpClient(new DelegateHandler(_ =>
        {
            sendCount++;
            throw new InvalidOperationException("No request should be sent.");
        }));
        var preprocessor = new FakePreprocessor([9, 8, 7, 6]);
        var provider = new OpenAiCompatibleRemoteVisionProvider(
            httpClient,
            new FakeCredentialService(Credential),
            new RejectingRequestAuthorizer(),
            preprocessor);
        var imageOpenCount = 0;
        var request = CreateRequest(() => imageOpenCount++);

        Assert.False(await provider.IsAvailableAsync(request.ProfileSnapshot));
        var exception = await Assert.ThrowsAsync<RemoteAnalysisProviderException>(
            () => provider.AnalyzeAsync(request));

        Assert.Equal("remote.consent-required", exception.ErrorCode);
        Assert.Equal(0, imageOpenCount);
        Assert.Equal(0, preprocessor.CallCount);
        Assert.Equal(0, sendCount);
    }

    [Fact]
    public async Task AuthorizationRevokedDuringSanitization_DoesNotSendPreparedCopy()
    {
        var sendCount = 0;
        using var httpClient = new HttpClient(new DelegateHandler(_ =>
        {
            sendCount++;
            throw new InvalidOperationException("No request should be sent.");
        }));
        var preprocessor = new FakePreprocessor([9, 8, 7, 6]);
        var authorizer = new RejectOnSecondRequestAuthorizer();
        var provider = new OpenAiCompatibleRemoteVisionProvider(
            httpClient,
            new FakeCredentialService(Credential),
            authorizer,
            preprocessor);

        var exception = await Assert.ThrowsAsync<RemoteAnalysisProviderException>(
            () => provider.AnalyzeAsync(CreateRequest()));

        Assert.Equal("remote.consent-required", exception.ErrorCode);
        Assert.Equal(2, authorizer.CallCount);
        Assert.Equal(1, preprocessor.CallCount);
        Assert.True(preprocessor.CopyStreamDisposed);
        Assert.Equal(0, sendCount);
    }

    [Fact]
    public async Task NonSkippedOcrBoundary_IsRejectedBeforeImageOrNetworkAccess()
    {
        var sendCount = 0;
        using var httpClient = new HttpClient(new DelegateHandler(_ =>
        {
            sendCount++;
            throw new InvalidOperationException("No request should be sent.");
        }));
        var preprocessor = new FakePreprocessor([9, 8, 7, 6]);
        var provider = new OpenAiCompatibleRemoteVisionProvider(
            httpClient,
            new FakeCredentialService(Credential),
            AllowAllRequestAuthorizer.Instance,
            preprocessor);
        var imageOpenCount = 0;
        var request = CreateRequest(() => imageOpenCount++);
        request = request with
        {
            OcrDocument = request.OcrDocument with
            {
                Provenance = request.OcrDocument.Provenance with
                {
                    StageOutcome = AnalysisStageOutcome.Completed,
                },
            },
        };

        var exception = await Assert.ThrowsAsync<RemoteAnalysisProviderException>(
            () => provider.AnalyzeAsync(request));

        Assert.Equal("remote.direct-image-ocr-boundary-invalid", exception.ErrorCode);
        Assert.Equal(0, imageOpenCount);
        Assert.Equal(0, preprocessor.CallCount);
        Assert.Equal(0, sendCount);
    }

    [Fact]
    public async Task OversizedSanitizedCopy_IsRejectedBeforeNetworkAccess()
    {
        var sendCount = 0;
        using var httpClient = new HttpClient(new DelegateHandler(_ =>
        {
            sendCount++;
            throw new InvalidOperationException("No request should be sent.");
        }));
        var preprocessor = new FakePreprocessor(
            new byte[4_097],
            declaredByteLength: 4_097);
        var provider = new OpenAiCompatibleRemoteVisionProvider(
            httpClient,
            new FakeCredentialService(Credential),
            AllowAllRequestAuthorizer.Instance,
            preprocessor);

        var exception = await Assert.ThrowsAsync<RemoteAnalysisProviderException>(
            () => provider.AnalyzeAsync(CreateRequest()));

        Assert.Equal("remote.image-copy-invalid", exception.ErrorCode);
        Assert.True(preprocessor.CopyStreamDisposed);
        Assert.Equal(0, sendCount);
    }

    [Fact]
    public async Task LargeProfileLimit_IsClampedSoBase64DataUriStaysWithinTenMiB()
    {
        using var httpClient = new HttpClient(new DelegateHandler(_ =>
            Task.FromResult(JsonResponse(CreateStructuredOutput()))));
        var preprocessor = new FakePreprocessor([9, 8, 7, 6]);
        var provider = new OpenAiCompatibleRemoteVisionProvider(
            httpClient,
            new FakeCredentialService(Credential),
            AllowAllRequestAuthorizer.Instance,
            preprocessor);
        var request = CreateRequest();
        request = request with
        {
            ProfileSnapshot = request.ProfileSnapshot with
            {
                RemoteApiProfile = request.ProfileSnapshot.RemoteApiProfile! with
                {
                    MaxImageBytes = 8L * 1024 * 1024,
                },
            },
        };

        await provider.AnalyzeAsync(request);

        Assert.Equal(7_864_302, preprocessor.MaximumBytes);
    }

    private static VisionAnalysisRequest CreateRequest(Action? onImageOpen = null)
    {
        var skippedProvenance = new AnalysisProvenance(
            "analysis.execution-router",
            ModelId: null,
            ModelVersion: null,
            new Dictionary<string, string>(),
            "remote-direct-image-skip.v1",
            AnalysisExecutionLocation.RemoteApi,
            AnalysisOutputKind.OcrFacts,
            RemoteInputMode.DirectImage,
            AnalysisStageOutcome.SkippedByRemoteDirectImage);
        var document = new OcrDocument(
            Text: string.Empty,
            Lines: [],
            LanguageTags: [],
            Warnings: ["analysis.skipped-by-remote-direct-image"],
            skippedProvenance,
            ImageWidth: 1280,
            ImageHeight: 720);
        var profile = ModelProfileSnapshot.Default with
        {
            Revision = 3,
            ExecutionBackend = AnalysisExecutionBackend.RemoteApi,
            RemoteInputMode = RemoteInputMode.DirectImage,
            RemoteApiProfile = new RemoteApiProfileSnapshot
            {
                ProfileId = "remote-profile",
                ProviderId = "opaque-provider",
                EndpointId = "openai-compatible.chat-completions.v1",
                BaseUri = new Uri("https://api.example.test/v1/chat/completions"),
                ModelId = "remote-model",
                PromptVersion = "remote-vision.prompt.v1",
                OutputSchemaVersion = QwenStructuredOutputParser.SchemaVersion,
                MaxTextChars = 10_000,
                MaxImageBytes = 4_096,
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
                return ValueTask.FromResult<Stream>(
                    new MemoryStream([1, 2, 3], writable: false));
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

    private static string CreateStructuredOutput(string[]? visualFacts = null) =>
        JsonSerializer.Serialize(new
        {
            schemaVersion = QwenStructuredOutputParser.SchemaVersion,
            title = "7月20日项目评审",
            summary = "图片显示项目评审会议将在会议室A举行。",
            visualFacts = visualFacts ?? ["图片中可见会议通知。"],
            categoryIds = Array.Empty<string>(),
            entities = new[]
            {
                new
                {
                    kind = "datetime",
                    rawText = "7月20日 14:30",
                    normalizedValue = (string?)null,
                    evidence = "7月20日 14:30 会议室A",
                },
            },
            detectedLanguages = new[] { "zh-Hans" },
            warnings = Array.Empty<string>(),
        });

    private sealed class FakePreprocessor(
        byte[] sanitizedBytes,
        long? declaredByteLength = null)
        : IRemoteVisionImagePreprocessor
    {
        private TrackingMemoryStream? _copyStream;

        public int CallCount { get; private set; }

        public long MaximumBytes { get; private set; }

        public bool CopyStreamDisposed => _copyStream?.Disposed == true;

        public Task<RemoteVisionImageCopy> CreateRemoteAnalysisCopyAsync(
            Stream source,
            long maximumBytes,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            MaximumBytes = maximumBytes;
            _copyStream = new TrackingMemoryStream(sanitizedBytes);
            return Task.FromResult(new RemoteVisionImageCopy(
                _copyStream,
                "image/png",
                640,
                360,
                declaredByteLength ?? sanitizedBytes.LongLength));
        }
    }

    private sealed class TrackingMemoryStream(byte[] bytes)
        : MemoryStream(bytes, writable: false)
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

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

    private sealed class RejectingRequestAuthorizer : IRemoteApiRequestAuthorizer
    {
        public Task EnsureAuthorizedAsync(
            RemoteApiProfileSnapshot profileSnapshot,
            RemoteInputMode inputMode,
            CancellationToken cancellationToken = default) =>
            Task.FromException(
                new RemoteAnalysisProviderException(
                    "remote.consent-required",
                    isRetryable: false));
    }

    private sealed class RejectOnSecondRequestAuthorizer
        : IRemoteApiRequestAuthorizer
    {
        public int CallCount { get; private set; }

        public Task EnsureAuthorizedAsync(
            RemoteApiProfileSnapshot profileSnapshot,
            RemoteInputMode inputMode,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return CallCount == 1
                ? Task.CompletedTask
                : Task.FromException(
                    new RemoteAnalysisProviderException(
                        "remote.consent-required",
                        isRetryable: false));
        }
    }
}
