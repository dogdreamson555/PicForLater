namespace PicForLater.Core.Analysis;

public enum RemoteApiProfileValidationState
{
    Unverified = 0,
    Valid = 1,
    Invalid = 2,
}

public enum RemoteApiProtocol
{
    OpenAiChatCompletions = 0,
    AnthropicMessages = 1,
}

public enum RemoteApiAuthenticationKind
{
    Bearer = 0,
    XApiKey = 1,
    None = 2,
}

public enum RemoteStructuredOutputMode
{
    JsonSchema = 0,
    JsonObject = 1,
    PromptOnly = 2,
}

public enum RemoteEndpointTrustMode
{
    FixedHttps = 0,
    PublicHttps = 1,
    LoopbackHttp = 2,
}

public enum RemoteReasoningMode
{
    ProviderDefault = 0,
    Disabled = 1,
    Low = 2,
    Medium = 3,
    High = 4,
}

public enum RemoteReasoningWireFormat
{
    None = 0,
    ThinkingObject = 1,
    ReasoningEffort = 2,
    EnableThinkingBoolean = 3,
    ReasoningEnabledObject = 4,
}

public sealed record RemoteApiProfile
{
    public required string ProfileId { get; init; }

    public required string ProviderId { get; init; }

    public required string DisplayName { get; init; }

    public required string EndpointId { get; init; }

    public required Uri BaseUri { get; init; }

    public required string ModelId { get; init; }

    public required IReadOnlyList<RemoteInputMode> SupportedInputModes { get; init; }

    public required string PromptVersion { get; init; }

    public required string OutputSchemaVersion { get; init; }

    public required int MaxTextChars { get; init; }

    public required long MaxImageBytes { get; init; }

    public required int MaxOutputTokens { get; init; }

    public required int TimeoutSeconds { get; init; }

    public required Uri PrivacyUrl { get; init; }

    public required Uri TermsUrl { get; init; }

    public required string RetentionTrainingStatement { get; init; }

    public required DateTimeOffset RetentionTrainingVerifiedAtUtc { get; init; }

    public required string CredentialReference { get; init; }

    public required string DisclosureVersion { get; init; }

    // Optional init-only additions deliberately retain local/default wire values so
    // profiles serialized before multi-protocol support keep their old behavior.
    public RemoteApiProtocol Protocol { get; init; } = RemoteApiProtocol.OpenAiChatCompletions;

    public RemoteApiAuthenticationKind AuthenticationKind { get; init; } =
        RemoteApiAuthenticationKind.Bearer;

    public RemoteStructuredOutputMode StructuredOutputMode { get; init; } =
        RemoteStructuredOutputMode.JsonSchema;

    public RemoteEndpointTrustMode EndpointTrustMode { get; init; } =
        RemoteEndpointTrustMode.FixedHttps;

    public string? ApiVersion { get; init; }

    public bool DisableProviderFallbacks { get; init; }

    public bool DisableExternalSearch { get; init; }

    public RemoteReasoningMode ReasoningMode { get; init; } =
        RemoteReasoningMode.ProviderDefault;

    public RemoteReasoningWireFormat ReasoningWireFormat { get; init; } =
        RemoteReasoningWireFormat.None;

    public bool IsEnabled { get; init; }

    public RemoteApiProfileValidationState ValidationState { get; init; }

    public DateTimeOffset? LastVerifiedAtUtc { get; init; }

    public RemoteInputMode? ConsentedInputMode { get; init; }

    public string? ConsentedDisclosureVersion { get; init; }

    public DateTimeOffset? ConsentGrantedAtUtc { get; init; }

    public DateTimeOffset UpdatedAtUtc { get; init; }
}

public sealed record RemoteApiProfileSnapshot
{
    public required string ProfileId { get; init; }

    public required string ProviderId { get; init; }

    public required string EndpointId { get; init; }

    public required Uri BaseUri { get; init; }

    public required string ModelId { get; init; }

    public required string PromptVersion { get; init; }

    public required string OutputSchemaVersion { get; init; }

    public required int MaxTextChars { get; init; }

    public required long MaxImageBytes { get; init; }

    public required int MaxOutputTokens { get; init; }

    public required int TimeoutSeconds { get; init; }

    public required string CredentialReference { get; init; }

    public required string ConsentVersion { get; init; }

    public RemoteApiProtocol Protocol { get; init; } = RemoteApiProtocol.OpenAiChatCompletions;

    public RemoteApiAuthenticationKind AuthenticationKind { get; init; } =
        RemoteApiAuthenticationKind.Bearer;

    public RemoteStructuredOutputMode StructuredOutputMode { get; init; } =
        RemoteStructuredOutputMode.JsonSchema;

    public RemoteEndpointTrustMode EndpointTrustMode { get; init; } =
        RemoteEndpointTrustMode.FixedHttps;

    public string? ApiVersion { get; init; }

    public bool DisableProviderFallbacks { get; init; }

    public bool DisableExternalSearch { get; init; }

    public RemoteReasoningMode ReasoningMode { get; init; } =
        RemoteReasoningMode.ProviderDefault;

    public RemoteReasoningWireFormat ReasoningWireFormat { get; init; } =
        RemoteReasoningWireFormat.None;
}

public sealed record AnalysisExecutionSettings(
    AnalysisExecutionBackend Backend,
    RemoteInputMode? RemoteInputMode,
    string? RemoteApiProfileId,
    long Revision,
    DateTimeOffset UpdatedAtUtc);

public sealed record RemoteAnalysisExecutionState(
    AnalysisExecutionSettings Settings,
    RemoteApiProfile? Profile);

public sealed class RemoteApiProfileException : Exception, IModelOperationFailure
{
    public RemoteApiProfileException(string errorCode)
        : base("The remote API profile operation could not be completed.")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}

public sealed class AnalysisExecutionUnavailableException : Exception, IModelOperationFailure
{
    public AnalysisExecutionUnavailableException(string errorCode)
        : base("The selected analysis execution backend is unavailable.")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}

public sealed class RemoteAnalysisProviderException : Exception, IModelOperationFailure
{
    public RemoteAnalysisProviderException(
        string errorCode,
        bool isRetryable,
        Exception? innerException = null)
        : base("The remote analysis provider could not complete the request.", innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ErrorCode = errorCode;
        IsRetryable = isRetryable;
    }

    public string ErrorCode { get; }

    public bool IsRetryable { get; }
}
