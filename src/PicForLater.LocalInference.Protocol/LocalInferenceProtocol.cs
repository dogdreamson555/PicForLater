using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using PicForLater.Core.Analysis;

namespace PicForLater.LocalInference.Protocol;

public static class LocalInferenceProtocol
{
    public const int CurrentVersion = 1;
    public const int MinimumSupportedVersion = 1;
    public const int MaximumFrameBytes = 64 * 1024 * 1024;
    public const int MaximumIdleTimeoutSeconds = 15 * 60;

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 64,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false),
        },
    };

    public static int NegotiateVersion(int minimumVersion, int maximumVersion)
    {
        if (minimumVersion <= 0 || maximumVersion < minimumVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumVersion));
        }

        var selected = Math.Min(CurrentVersion, maximumVersion);
        if (selected < MinimumSupportedVersion || selected < minimumVersion)
        {
            throw new LocalInferenceProtocolException("local-worker.protocol-version-unsupported");
        }

        return selected;
    }

    public static JsonElement ToPayload<T>(T value) =>
        JsonSerializer.SerializeToElement(value, JsonOptions);

    public static T ReadPayload<T>(LocalInferenceEnvelope envelope)
    {
        try
        {
            return envelope.Payload.Deserialize<T>(JsonOptions)
                ?? throw new LocalInferenceProtocolException("local-worker.protocol-payload-empty");
        }
        catch (JsonException exception)
        {
            throw new LocalInferenceProtocolException(
                "local-worker.protocol-invalid-payload",
                exception);
        }
    }

    public static async ValueTask WriteAsync(
        Stream stream,
        LocalInferenceEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(envelope);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        if (bytes.Length is <= 0 or > MaximumFrameBytes)
        {
            throw new LocalInferenceProtocolException("local-worker.protocol-frame-too-large");
        }

        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<LocalInferenceEnvelope?> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[sizeof(int)];
        var headerRead = await ReadAtMostAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (headerRead == 0)
        {
            return null;
        }
        if (headerRead != header.Length)
        {
            throw new LocalInferenceProtocolException("local-worker.protocol-truncated-header");
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumFrameBytes)
        {
            throw new LocalInferenceProtocolException("local-worker.protocol-invalid-frame-length");
        }

        var payload = new byte[length];
        var payloadRead = await ReadAtMostAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        if (payloadRead != length)
        {
            throw new LocalInferenceProtocolException("local-worker.protocol-truncated-frame");
        }

        try
        {
            return JsonSerializer.Deserialize<LocalInferenceEnvelope>(payload, JsonOptions)
                ?? throw new LocalInferenceProtocolException("local-worker.protocol-envelope-empty");
        }
        catch (JsonException exception)
        {
            throw new LocalInferenceProtocolException(
                "local-worker.protocol-invalid-json",
                exception);
        }
    }

    private static async ValueTask<int> ReadAtMostAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}

public static class LocalInferenceMessageTypes
{
    public const string Hello = "hello";
    public const string HelloResult = "hello-result";
    public const string Request = "request";
    public const string Response = "response";
    public const string Cancel = "cancel";
    public const string Shutdown = "shutdown";
}

public static class LocalInferenceOperations
{
    public const string OcrAvailability = "ocr-availability";
    public const string Recognize = "recognize";
    public const string VisionAvailability = "vision-availability";
    public const string AnalyzeVision = "analyze-vision";
    public const string GenerateQwen = "generate-qwen";
    public const string RunPpOcrTensor = "run-ppocr-tensor";
}

public sealed record LocalInferenceEnvelope(
    int ProtocolVersion,
    string MessageType,
    Guid RequestId,
    string? Operation,
    JsonElement Payload)
{
    public LocalInferenceError? Error { get; init; }

    public InferenceExecutionStatus? ExecutionStatus { get; init; }
}

public sealed record LocalInferenceError(
    string ErrorCode,
    bool IsRetryable,
    string Category);

public sealed record LocalInferenceHelloRequest(
    int MinimumVersion,
    int MaximumVersion,
    int ParentProcessId,
    string AppDataRootPath,
    int IdleTimeoutSeconds);

public sealed record LocalInferenceHelloResponse(
    int SelectedVersion,
    int WorkerProcessId,
    IReadOnlyList<string> SupportedExecutionProviders);

public sealed record LocalInferenceImageReference(
    string RelativePath,
    string ContentHash);

public sealed record LocalInferenceOcrAvailabilityRequest(
    InferenceAccelerationMode AccelerationMode);

public sealed record LocalInferenceOcrAvailabilityResponse(bool IsAvailable);

public sealed record LocalInferenceRecognizeRequest(
    LocalInferenceImageReference Image,
    string OriginalFileName,
    int PixelWidth,
    int PixelHeight,
    IReadOnlyList<string> LanguageHints,
    InferenceAccelerationMode AccelerationMode);

public sealed record LocalInferenceRecognizeResponse(OcrDocument Document);

public sealed record LocalInferenceVisionAvailabilityRequest(
    ModelProfileSnapshot ProfileSnapshot,
    InferenceAccelerationMode AccelerationMode);

public sealed record LocalInferenceVisionAvailabilityResponse(bool IsAvailable);

public sealed record LocalInferenceAnalyzeVisionRequest(
    LocalInferenceImageReference Image,
    string OriginalFileName,
    OcrDocument OcrDocument,
    AnalysisCompositionContext CompositionContext,
    ModelProfileSnapshot ProfileSnapshot,
    DateTimeOffset ReferenceTimeUtc,
    string TimeZoneId,
    InferenceAccelerationMode AccelerationMode);

public sealed record LocalInferenceAnalyzeVisionResponse(VisionStructuredResult Result);

public sealed record LocalInferenceGenerateQwenRequest(
    string ModelDirectoryRelativePath,
    string ImageRelativePath,
    string Prompt,
    string JsonSchema,
    int MaximumOutputTokens,
    InferenceAccelerationMode AccelerationMode);

public sealed record LocalInferenceGenerateQwenResponse(string Output);

public sealed record LocalInferenceRunPpOcrTensorRequest(
    string ModelRelativePath,
    string InputName,
    string OutputName,
    float[] Input,
    IReadOnlyList<int> Dimensions,
    InferenceAccelerationMode AccelerationMode);

public sealed record LocalInferenceRunPpOcrTensorResponse(
    float[] Values,
    IReadOnlyList<int> Dimensions);

public sealed class LocalInferenceProtocolException : Exception
{
    public LocalInferenceProtocolException(string errorCode, Exception? innerException = null)
        : base("The local inference pipe protocol is invalid.", innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}
