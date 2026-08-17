using System.Net;
using PicForLater.Core.Analysis;

namespace PicForLater.Analysis;

public sealed class OpenAiCompatibleRemoteApiConnectionTester : IRemoteApiConnectionTester
{
    private const int MaximumResponseBytes = 1_048_576;
    private const string CapabilityTestImageResourceName =
        "PicForLater.Analysis.TestAssets.cat.jpg";
    private const int MaximumCapabilityTestImageBytes = 128 * 1024;
    private static readonly Lazy<string> CapabilityTestImageBase64 = new(
        LoadCapabilityTestImageBase64,
        LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly HttpClient _httpClient;
    private readonly IRemoteApiCredentialService _credentialService;

    public OpenAiCompatibleRemoteApiConnectionTester(
        HttpClient httpClient,
        IRemoteApiCredentialService credentialService)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _credentialService = credentialService
            ?? throw new ArgumentNullException(nameof(credentialService));
    }

    public async Task TestAsync(
        RemoteApiProfile profile,
        RemoteInputMode inputMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!profile.SupportedInputModes.Contains(inputMode))
        {
            throw new RemoteAnalysisProviderException("remote.input-mode-not-supported", false);
        }

        var credential = profile.AuthenticationKind == RemoteApiAuthenticationKind.None
            ? null
            : await _credentialService.RetrieveAsync(
                profile.CredentialReference,
                cancellationToken).ConfigureAwait(false);
        if (profile.AuthenticationKind != RemoteApiAuthenticationKind.None
            && string.IsNullOrWhiteSpace(credential))
        {
            throw new RemoteAnalysisProviderException("remote.credential-unavailable", false);
        }

        var snapshot = CreateSnapshot(profile);
        var prompt = new RemoteChatPrompt(
            """
            This request contains only built-in, non-user capability-test data. Return the
            required PicForLater result; use empty categoryIds and entities.

            """ + RemoteStructuredOutputContract.PromptInstruction(
                profile.StructuredOutputMode),
            inputMode == RemoteInputMode.DirectImage
                ? "PicForLater capability test. Briefly describe the built-in licensed cat test image."
                : "Synthetic PicForLater capability test. Return a short title and summary for the word test.",
            inputMode == RemoteInputMode.DirectImage
                ? new RemoteChatImage("image/jpeg", CapabilityTestImageBase64.Value)
                : null);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(profile.TimeoutSeconds));
        try
        {
            using var request = RemoteChatProtocol.CreateRequest(
                snapshot,
                prompt,
                credential,
                Guid.NewGuid().ToString("N"));
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw CreateStatusException(response.StatusCode);
            }

            var responseBody = await RemoteChatProtocol.ReadBoundedResponseAsync(
                response,
                MaximumResponseBytes,
                timeout.Token).ConfigureAwait(false);
            var structuredContent = RemoteChatProtocol.ReadStructuredContent(
                profile.Protocol,
                responseBody);
            try
            {
                var normalized = QwenStructuredOutputParser.NormalizeGeneratedOutput(
                    structuredContent);
                var provenance = new AnalysisProvenance(
                    profile.ProviderId,
                    profile.ModelId,
                    ModelVersion: null,
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    profile.OutputSchemaVersion,
                    AnalysisExecutionLocation.RemoteApi,
                    AnalysisOutputKind.ModelGeneratedDraft,
                    inputMode);
                var ocrProvenance = new AnalysisProvenance(
                    "connection-test.synthetic-ocr",
                    "synthetic",
                    "1",
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    "connection-test.synthetic-ocr.v1",
                    AnalysisExecutionLocation.Local,
                    AnalysisOutputKind.OcrFacts);
                var ocr = new OcrDocument(
                    inputMode == RemoteInputMode.LocalOcrText ? "test" : string.Empty,
                    [],
                    ["en"],
                    [],
                    ocrProvenance,
                    1,
                    1);
                _ = new QwenStructuredOutputParser().Parse(
                    normalized,
                    ocr,
                    new AnalysisCompositionContext([]),
                    provenance,
                    DateTimeOffset.UnixEpoch,
                    "UTC");
            }
            catch (QwenStructuredOutputException exception)
            {
                throw new RemoteAnalysisProviderException(
                    exception.ErrorCode is "qwen.title-empty"
                        or "qwen.degenerate-text-output"
                        or "qwen.ungrounded-numeric-output"
                            ? "remote.invalid-content-draft"
                            : "remote.invalid-structured-output",
                    false,
                    exception);
            }
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

    private static RemoteApiProfileSnapshot CreateSnapshot(RemoteApiProfile profile) => new()
    {
        ProfileId = profile.ProfileId,
        ProviderId = profile.ProviderId,
        EndpointId = profile.EndpointId,
        BaseUri = profile.BaseUri,
        ModelId = profile.ModelId,
        PromptVersion = profile.PromptVersion,
        OutputSchemaVersion = profile.OutputSchemaVersion,
        MaxTextChars = profile.MaxTextChars,
        MaxImageBytes = profile.MaxImageBytes,
        MaxOutputTokens = Math.Min(512, profile.MaxOutputTokens),
        TimeoutSeconds = profile.TimeoutSeconds,
        CredentialReference = profile.CredentialReference,
        ConsentVersion = profile.DisclosureVersion,
        Protocol = profile.Protocol,
        AuthenticationKind = profile.AuthenticationKind,
        StructuredOutputMode = profile.StructuredOutputMode,
        EndpointTrustMode = profile.EndpointTrustMode,
        ApiVersion = profile.ApiVersion,
        DisableProviderFallbacks = profile.DisableProviderFallbacks,
        DisableExternalSearch = profile.DisableExternalSearch,
        ReasoningMode = profile.ReasoningMode,
        ReasoningWireFormat = profile.ReasoningWireFormat,
    };

    private static RemoteAnalysisProviderException CreateStatusException(
        HttpStatusCode statusCode) => (int)statusCode switch
        {
            (int)HttpStatusCode.Unauthorized or (int)HttpStatusCode.Forbidden =>
                new RemoteAnalysisProviderException("remote.credential-rejected", false),
            (int)HttpStatusCode.TooManyRequests =>
                new RemoteAnalysisProviderException("remote.rate-limited", true),
            >= 500 and <= 599 =>
                new RemoteAnalysisProviderException("remote.service-unavailable", true),
            _ => new RemoteAnalysisProviderException("remote.model-or-schema-rejected", false),
        };

    private static string LoadCapabilityTestImageBase64()
    {
        using var stream = typeof(OpenAiCompatibleRemoteApiConnectionTester).Assembly
            .GetManifestResourceStream(CapabilityTestImageResourceName);
        if (stream is null
            || !stream.CanRead
            || stream.Length is <= 0 or > MaximumCapabilityTestImageBytes)
        {
            throw new RemoteAnalysisProviderException(
                "remote.connection-test-asset-unavailable",
                false);
        }

        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return Convert.ToBase64String(bytes);
    }
}
