using PicForLater.Analysis;
using PicForLater.App.Models;
using PicForLater.Core.Analysis;

namespace PicForLater.App.Services;

public sealed record RemoteApiProviderPreset(
    string ProfileId,
    string ProviderId,
    string DisplayName,
    RemoteApiProviderCategory Category,
    bool IsCustom,
    string EndpointId,
    Uri BaseUri,
    string DefaultModelId,
    IReadOnlyList<RemoteInputMode> SupportedInputModes,
    RemoteApiProtocol Protocol,
    RemoteApiAuthenticationKind AuthenticationKind,
    RemoteStructuredOutputMode StructuredOutputMode,
    RemoteEndpointTrustMode EndpointTrustMode,
    string? ApiVersion,
    bool DisableProviderFallbacks,
    bool DisableExternalSearch,
    RemoteReasoningMode ReasoningMode,
    RemoteReasoningWireFormat ReasoningWireFormat,
    IReadOnlyList<RemoteReasoningMode> SupportedReasoningModes,
    Uri PrivacyUrl,
    Uri TermsUrl,
    Uri PricingUrl,
    string RetentionTrainingStatement,
    string RetentionResourceName,
    DateTimeOffset PolicyVerifiedAtUtc,
    string CredentialReference,
    string DisclosureVersion,
    string ModelSuggestion)
{
    public IReadOnlyList<string> RetiredDefaultModelIds { get; init; } = [];
}

public static class RemoteApiProviderCatalog
{
    private const string UserEndpointSuffix = ".user-endpoint";
    private const string PromptVersion = "picforlater.remote-analysis.v3";
    private const string GenericRetentionResource = "GenericApiRetentionStatement";
    private const string GenericRetention =
        "Retention and training treatment depends on the provider, plan, region, and current account controls. Review the linked provider policies before consenting.";
    private static readonly DateTimeOffset PolicyVerifiedAtUtc =
        new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly RemoteInputMode[] TextOnly = [RemoteInputMode.LocalOcrText];
    private static readonly RemoteInputMode[] TextAndImage =
        [RemoteInputMode.LocalOcrText, RemoteInputMode.DirectImage];
    private static readonly RemoteReasoningMode[] ProviderDefaultReasoning =
        [RemoteReasoningMode.ProviderDefault];
    private static readonly RemoteReasoningMode[] ToggleReasoning =
        [RemoteReasoningMode.Disabled, RemoteReasoningMode.ProviderDefault];
    private static readonly RemoteReasoningMode[] EffortReasoning =
        [RemoteReasoningMode.Low, RemoteReasoningMode.Medium, RemoteReasoningMode.High,
            RemoteReasoningMode.ProviderDefault];
    private static readonly RemoteReasoningMode[] AllReasoning =
        [RemoteReasoningMode.ProviderDefault, RemoteReasoningMode.Disabled,
            RemoteReasoningMode.Low, RemoteReasoningMode.Medium, RemoteReasoningMode.High];

    public static IReadOnlyList<RemoteApiProviderPreset> Presets { get; } =
    [
        OpenAiPreset("openai-official", "openai.official", "OpenAI", RemoteApiProviderCategory.InternationalOfficial,
            "https://api.openai.com/v1/chat/completions", "gpt-4.1-mini-2025-04-14", TextAndImage,
            "https://openai.com/policies/privacy-policy/", "https://openai.com/policies/services-agreement/", "https://openai.com/api/pricing/"),
        AnthropicPreset(),
        OpenAiPreset("google-gemini-official", "google.gemini", "Google Gemini", RemoteApiProviderCategory.InternationalOfficial,
            "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions", "gemini-3.5-flash", TextAndImage,
            "https://policies.google.com/privacy", "https://ai.google.dev/gemini-api/terms", "https://ai.google.dev/gemini-api/docs/pricing"),
        OpenAiPreset("xai-official", "xai.grok", "xAI / Grok", RemoteApiProviderCategory.InternationalOfficial,
            "https://api.x.ai/v1/chat/completions", "grok-4.5", TextAndImage,
            "https://x.ai/legal/privacy-policy", "https://x.ai/legal/terms-of-service", "https://docs.x.ai/docs/models",
            reasoningMode: RemoteReasoningMode.Low,
            reasoningWireFormat: RemoteReasoningWireFormat.ReasoningEffort,
            supportedReasoningModes: EffortReasoning),
        OpenAiPreset("perplexity-sonar-official", "perplexity.sonar", "Perplexity Sonar", RemoteApiProviderCategory.InternationalOfficial,
            "https://api.perplexity.ai/v1/sonar", "sonar", TextOnly,
            "https://www.perplexity.ai/hub/legal/privacy-policy", "https://www.perplexity.ai/hub/legal/terms-of-service", "https://docs.perplexity.ai/getting-started/pricing", disableExternalSearch: true),

        OpenAiPreset("deepseek-official", "deepseek.official", "DeepSeek", RemoteApiProviderCategory.ChinaOfficial,
            "https://api.deepseek.com/chat/completions", "deepseek-v4-flash", TextOnly,
            "https://cdn.deepseek.com/policies/en-US/deepseek-privacy-policy.html", "https://cdn.deepseek.com/policies/en-US/deepseek-terms-of-use.html", "https://api-docs.deepseek.com/quick_start/pricing", RemoteStructuredOutputMode.JsonObject,
            reasoningMode: RemoteReasoningMode.Disabled,
            reasoningWireFormat: RemoteReasoningWireFormat.ThinkingObject,
            supportedReasoningModes: ToggleReasoning) with
        {
            RetiredDefaultModelIds = ["deepseek-chat", "deepseek-reasoner"],
        },
        OpenAiPreset("kimi-official", "moonshot.kimi", "月之暗面 / Kimi", RemoteApiProviderCategory.ChinaOfficial,
            "https://api.moonshot.cn/v1/chat/completions", "kimi-k2.5", TextAndImage,
            "https://www.moonshot.cn/privacy-policy", "https://www.moonshot.cn/terms-of-service", "https://platform.kimi.com/docs/pricing/chat", RemoteStructuredOutputMode.PromptOnly),
        OpenAiPreset("tencent-hunyuan-official", "tencent.hunyuan", "腾讯混元", RemoteApiProviderCategory.ChinaOfficial,
            "https://tokenhub.tencentmaas.com/v1/chat/completions", "hy3-preview", TextOnly,
            "https://www.tencentcloud.com/document/product/301/17345", "https://www.tencentcloud.com/document/product/301/9247", "https://cloud.tencent.com/document/product/1823/130051", RemoteStructuredOutputMode.JsonSchema,
            reasoningMode: RemoteReasoningMode.Low,
            reasoningWireFormat: RemoteReasoningWireFormat.ReasoningEffort,
            supportedReasoningModes: EffortReasoning,
            disclosureVersion: "tencent.hunyuan.disclosure.v2") with
        {
            RetiredDefaultModelIds = ["hunyuan-turbos-latest"],
        },
        OpenAiPreset("volcengine-doubao-official", "volcengine.doubao", "火山引擎 / 豆包", RemoteApiProviderCategory.ChinaOfficial,
            "https://ark.cn-beijing.volces.com/api/v3/chat/completions", "doubao-seed-2-0-lite-260215", TextAndImage,
            "https://www.volcengine.com/docs/6256/64902", "https://www.volcengine.com/docs/6256/64903", "https://www.volcengine.com/docs/82379/1099320", RemoteStructuredOutputMode.JsonObject,
            reasoningMode: RemoteReasoningMode.Disabled,
            reasoningWireFormat: RemoteReasoningWireFormat.ThinkingObject,
            supportedReasoningModes: ToggleReasoning,
            disclosureVersion: "volcengine.doubao.disclosure.v2"),
        OpenAiPreset("alibaba-bailian-qwen-official", "alibaba.bailian.qwen", "阿里云百炼 / 通义千问 Qwen", RemoteApiProviderCategory.ChinaOfficial,
            "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions", "qwen3.5-plus", TextAndImage,
            "https://terms.alicdn.com/legal-agreement/terms/privacy_policy_full/20221129171420545/20221129171420545.html", "https://terms.alicdn.com/legal-agreement/terms/suit_bu1_ali_cloud/suit_bu1_ali_cloud202112211045_86198.html", "https://help.aliyun.com/zh/model-studio/model-pricing", RemoteStructuredOutputMode.JsonObject,
            reasoningMode: RemoteReasoningMode.Disabled,
            reasoningWireFormat: RemoteReasoningWireFormat.EnableThinkingBoolean,
            supportedReasoningModes: ToggleReasoning),
        OpenAiPreset("zhipu-glm-official", "zhipu.glm", "智谱 BigModel / GLM", RemoteApiProviderCategory.ChinaOfficial,
            "https://open.bigmodel.cn/api/paas/v4/chat/completions", "glm-5.2", TextOnly,
            "https://www.zhipuai.cn/privacy", "https://www.zhipuai.cn/terms", "https://open.bigmodel.cn/pricing", RemoteStructuredOutputMode.JsonObject,
            reasoningMode: RemoteReasoningMode.Disabled,
            reasoningWireFormat: RemoteReasoningWireFormat.ThinkingObject,
            supportedReasoningModes: ToggleReasoning),
        OpenAiPreset("baidu-qianfan-official", "baidu.qianfan", "百度智能云千帆 / 文心", RemoteApiProviderCategory.ChinaOfficial,
            "https://qianfan.baidubce.com/v2/chat/completions", "ernie-4.5-turbo-128k", TextOnly,
            "https://cloud.baidu.com/doc/Agreements/s/Kjwvy245m", "https://cloud.baidu.com/doc/Agreements/s/2jwvx9m0a", "https://cloud.baidu.com/doc/qianfan-docs/s/6m9l6p8iw", RemoteStructuredOutputMode.JsonObject),
        AnthropicCompatiblePreset(
            "minimax-official", "minimax.official", "MiniMax", RemoteApiProviderCategory.ChinaOfficial,
            "https://api.minimaxi.com/anthropic/v1/messages", "MiniMax-M2.7", TextOnly,
            "https://www.minimaxi.com/privacy", "https://www.minimaxi.com/terms", "https://platform.minimaxi.com/docs/guides/pricing",
            RemoteApiAuthenticationKind.Bearer, RemoteStructuredOutputMode.PromptOnly),

        OpenAiPreset("siliconflow-official", "siliconflow.cloud", "硅基流动 SiliconFlow / SiliconCloud", RemoteApiProviderCategory.Aggregator,
            "https://api.siliconflow.cn/v1/chat/completions", "Pro/zai-org/GLM-4.7", TextOnly,
            "https://siliconflow.cn/privacy-policy", "https://siliconflow.cn/terms-of-service", "https://cloud.siliconflow.cn/me/models", RemoteStructuredOutputMode.JsonObject,
            reasoningMode: RemoteReasoningMode.Disabled,
            reasoningWireFormat: RemoteReasoningWireFormat.ThinkingObject,
            supportedReasoningModes: ToggleReasoning),
        OpenAiPreset("openrouter-official", "openrouter.official", "OpenRouter", RemoteApiProviderCategory.Aggregator,
            "https://openrouter.ai/api/v1/chat/completions", "openai/gpt-4.1-mini", TextAndImage,
            "https://openrouter.ai/privacy", "https://openrouter.ai/terms", "https://openrouter.ai/models", disableProviderFallbacks: true),
        OpenAiPreset("groq-official", "groq.official", "Groq", RemoteApiProviderCategory.Aggregator,
            "https://api.groq.com/openai/v1/chat/completions", "meta-llama/llama-4-scout-17b-16e-instruct", TextAndImage,
            "https://groq.com/privacy-policy/", "https://groq.com/terms-of-use/", "https://groq.com/pricing/", RemoteStructuredOutputMode.JsonObject),
        OpenAiPreset("together-ai-official", "together.official", "Together AI", RemoteApiProviderCategory.Aggregator,
            "https://api.together.xyz/v1/chat/completions", "Qwen/Qwen3.5-9B", TextAndImage,
            "https://www.together.ai/privacy", "https://www.together.ai/terms-of-service", "https://www.together.ai/pricing",
            reasoningMode: RemoteReasoningMode.Disabled,
            reasoningWireFormat: RemoteReasoningWireFormat.ReasoningEnabledObject,
            supportedReasoningModes: ToggleReasoning,
            disclosureVersion: "together.official.disclosure.v2"),

        LoopbackPreset("ollama-local", "ollama.local", "Ollama", "http://127.0.0.1:11434/v1/chat/completions", "qwen3-vl:4b",
            "https://ollama.com/privacy", "https://ollama.com/terms", "https://ollama.com/search"),
        LoopbackPreset("vllm-local", "vllm.local", "vLLM", "http://127.0.0.1:8000/v1/chat/completions", "Qwen/Qwen3-VL-4B-Instruct",
            "https://docs.vllm.ai/en/latest/security.html", "https://docs.vllm.ai/en/latest/community/governance.html",
            "https://docs.vllm.ai/en/latest/serving/openai_compatible_server.html"),
        CustomPreset(),
    ];

    public static async Task<IReadOnlyList<RemoteApiProviderOption>> EnsureProfilesAsync(
        IRemoteApiProfileService profileService,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profileService);
        var existingProfiles = await profileService.GetProfilesAsync(cancellationToken)
            .ConfigureAwait(false);
        var profilesById = existingProfiles.ToDictionary(
            static profile => profile.ProfileId,
            StringComparer.Ordinal);
        var options = new List<RemoteApiProviderOption>(Presets.Count);
        foreach (var preset in Presets)
        {
            profilesById.TryGetValue(preset.ProfileId, out var existing);
            var desired = CreateProfile(preset, existing);
            if (existing is null || !ProfileEquals(existing, desired))
            {
                desired = await profileService.SaveProfileAsync(desired, cancellationToken)
                    .ConfigureAwait(false);
            }

            options.Add(new RemoteApiProviderOption(
                desired.ProfileId,
                desired.DisplayName,
                preset.Category,
                preset.IsCustom,
                preset.PricingUrl.AbsoluteUri,
                preset.RetentionResourceName,
                preset.ModelSuggestion,
                preset.SupportedReasoningModes));
        }

        return options;
    }

    public static RemoteApiProviderPreset GetPreset(string profileId) =>
        Presets.FirstOrDefault(preset => preset.ProfileId == profileId)
        ?? throw new InvalidOperationException("The remote API provider preset is unavailable.");

    private static RemoteApiProfile CreateProfile(RemoteApiProviderPreset preset, RemoteApiProfile? existing)
    {
        if (preset.IsCustom && existing is not null)
        {
            return existing with
            {
                PromptVersion = PromptVersion,
                OutputSchemaVersion = QwenStructuredOutputParser.SchemaVersion,
            };
        }

        var hasUserEndpointOverride = existing is not null
            && existing.EndpointId.EndsWith(UserEndpointSuffix, StringComparison.Ordinal)
            && existing.BaseUri != preset.BaseUri;
        return new RemoteApiProfile
        {
            ProfileId = preset.ProfileId,
            ProviderId = preset.ProviderId,
            DisplayName = preset.DisplayName,
            EndpointId = hasUserEndpointOverride ? existing!.EndpointId : preset.EndpointId,
            BaseUri = hasUserEndpointOverride ? existing!.BaseUri : preset.BaseUri,
            ModelId = existing is not null
                && !preset.RetiredDefaultModelIds.Contains(existing.ModelId, StringComparer.Ordinal)
                    ? existing.ModelId
                    : preset.DefaultModelId,
            SupportedInputModes = preset.SupportedInputModes,
            PromptVersion = PromptVersion,
            OutputSchemaVersion = QwenStructuredOutputParser.SchemaVersion,
            MaxTextChars = 64_000,
            MaxImageBytes = 8L * 1024 * 1024,
            MaxOutputTokens = existing?.MaxOutputTokens ?? 1_024,
            TimeoutSeconds = existing?.TimeoutSeconds
                ?? (preset.EndpointTrustMode == RemoteEndpointTrustMode.LoopbackHttp ? 120 : 60),
            PrivacyUrl = preset.PrivacyUrl,
            TermsUrl = preset.TermsUrl,
            RetentionTrainingStatement = preset.RetentionTrainingStatement,
            RetentionTrainingVerifiedAtUtc = preset.PolicyVerifiedAtUtc,
            CredentialReference = preset.CredentialReference,
            DisclosureVersion = preset.DisclosureVersion,
            Protocol = preset.Protocol,
            AuthenticationKind = preset.AuthenticationKind,
            StructuredOutputMode = preset.StructuredOutputMode,
            EndpointTrustMode = hasUserEndpointOverride
                ? existing!.EndpointTrustMode
                : preset.EndpointTrustMode,
            ApiVersion = preset.ApiVersion,
            DisableProviderFallbacks = preset.DisableProviderFallbacks,
            DisableExternalSearch = preset.DisableExternalSearch,
            ReasoningMode = existing is not null
                && preset.SupportedReasoningModes.Contains(existing.ReasoningMode)
                    ? existing.ReasoningMode
                    : preset.ReasoningMode,
            ReasoningWireFormat = preset.ReasoningWireFormat,
            IsEnabled = existing?.IsEnabled ?? true,
            ValidationState = existing?.ValidationState ?? RemoteApiProfileValidationState.Unverified,
            LastVerifiedAtUtc = existing?.LastVerifiedAtUtc,
            ConsentedInputMode = existing?.ConsentedInputMode,
            ConsentedDisclosureVersion = existing?.ConsentedDisclosureVersion,
            ConsentGrantedAtUtc = existing?.ConsentGrantedAtUtc,
            UpdatedAtUtc = existing?.UpdatedAtUtc ?? default,
        };
    }

    public static string GetEndpointId(RemoteApiProviderPreset preset, Uri endpoint) =>
        endpoint == preset.BaseUri
            ? preset.EndpointId
            : preset.EndpointId + UserEndpointSuffix;

    private static bool ProfileEquals(RemoteApiProfile left, RemoteApiProfile right) =>
        left == right || left with { SupportedInputModes = [] } == right with { SupportedInputModes = [] }
            && left.SupportedInputModes.SequenceEqual(right.SupportedInputModes);

    private static RemoteApiProviderPreset OpenAiPreset(
        string profileId,
        string providerId,
        string displayName,
        RemoteApiProviderCategory category,
        string endpoint,
        string model,
        IReadOnlyList<RemoteInputMode> modes,
        string privacy,
        string terms,
        string pricing,
        RemoteStructuredOutputMode outputMode = RemoteStructuredOutputMode.JsonSchema,
        bool disableProviderFallbacks = false,
        bool disableExternalSearch = false,
        RemoteReasoningMode reasoningMode = RemoteReasoningMode.ProviderDefault,
        RemoteReasoningWireFormat reasoningWireFormat = RemoteReasoningWireFormat.None,
        IReadOnlyList<RemoteReasoningMode>? supportedReasoningModes = null,
        string? disclosureVersion = null) =>
        new(
            profileId, providerId, displayName, category, false,
            providerId + ".chat-completions", new Uri(endpoint), model, modes,
            RemoteApiProtocol.OpenAiChatCompletions, RemoteApiAuthenticationKind.Bearer,
            outputMode, RemoteEndpointTrustMode.FixedHttps, null,
            disableProviderFallbacks, disableExternalSearch, reasoningMode,
            reasoningWireFormat, supportedReasoningModes ?? ProviderDefaultReasoning,
            new Uri(privacy), new Uri(terms), new Uri(pricing), GenericRetention,
            GenericRetentionResource, PolicyVerifiedAtUtc,
            "picforlater.remote." + providerId,
            disclosureVersion ?? providerId + ".disclosure.v1", model);

    private static RemoteApiProviderPreset AnthropicPreset() => new(
        "anthropic-official", "anthropic.claude", "Anthropic / Claude",
        RemoteApiProviderCategory.InternationalOfficial, false,
        "anthropic.messages.v1", new Uri("https://api.anthropic.com/v1/messages"),
        "claude-sonnet-4-5-20250929", TextAndImage,
        RemoteApiProtocol.AnthropicMessages, RemoteApiAuthenticationKind.XApiKey,
        RemoteStructuredOutputMode.JsonSchema, RemoteEndpointTrustMode.FixedHttps,
        "2023-06-01", false, false, RemoteReasoningMode.ProviderDefault,
        RemoteReasoningWireFormat.None, ProviderDefaultReasoning,
        new Uri("https://www.anthropic.com/legal/privacy"),
        new Uri("https://www.anthropic.com/legal/commercial-terms"),
        new Uri("https://platform.claude.com/docs/en/about-claude/pricing/overview"),
        GenericRetention, GenericRetentionResource, PolicyVerifiedAtUtc,
        "picforlater.remote.anthropic.claude", "anthropic.claude.disclosure.v1",
        "claude-sonnet-4-5-20250929");

    private static RemoteApiProviderPreset AnthropicCompatiblePreset(
        string profileId,
        string providerId,
        string displayName,
        RemoteApiProviderCategory category,
        string endpoint,
        string model,
        IReadOnlyList<RemoteInputMode> modes,
        string privacy,
        string terms,
        string pricing,
        RemoteApiAuthenticationKind authenticationKind,
        RemoteStructuredOutputMode outputMode) => new(
        profileId, providerId, displayName, category, false,
        providerId + ".messages", new Uri(endpoint), model, modes,
        RemoteApiProtocol.AnthropicMessages, authenticationKind,
        outputMode, RemoteEndpointTrustMode.FixedHttps,
        "2023-06-01", false, false, RemoteReasoningMode.ProviderDefault,
        RemoteReasoningWireFormat.None, ProviderDefaultReasoning,
        new Uri(privacy), new Uri(terms), new Uri(pricing), GenericRetention,
        GenericRetentionResource, PolicyVerifiedAtUtc,
        "picforlater.remote." + providerId, providerId + ".disclosure.v1", model);

    private static RemoteApiProviderPreset LoopbackPreset(
        string profileId,
        string providerId,
        string displayName,
        string endpoint,
        string model,
        string privacy,
        string terms,
        string pricing) => new(
        profileId, providerId, displayName, RemoteApiProviderCategory.LocalPrivate, false,
        providerId + ".openai-loopback", new Uri(endpoint), model, TextAndImage,
        RemoteApiProtocol.OpenAiChatCompletions, RemoteApiAuthenticationKind.None,
        RemoteStructuredOutputMode.JsonSchema, RemoteEndpointTrustMode.LoopbackHttp,
        null, false, false, RemoteReasoningMode.ProviderDefault,
        RemoteReasoningWireFormat.None, ProviderDefaultReasoning,
        new Uri(privacy), new Uri(terms), new Uri(pricing),
        "Requests stay on the selected loopback service. The server process and loaded model remain under the user's control.",
        "LoopbackApiRetentionStatement", PolicyVerifiedAtUtc,
        "picforlater.remote." + providerId, providerId + ".disclosure.v1", model);

    private static RemoteApiProviderPreset CustomPreset() => new(
        "custom-interface", "custom.openai-compatible", "自定义接口",
        RemoteApiProviderCategory.Custom, true,
        "custom.chat-completions", new Uri("https://api.example.invalid/v1/chat/completions"),
        "model-id", TextAndImage,
        RemoteApiProtocol.OpenAiChatCompletions, RemoteApiAuthenticationKind.Bearer,
        RemoteStructuredOutputMode.PromptOnly, RemoteEndpointTrustMode.PublicHttps,
        null, false, false, RemoteReasoningMode.ProviderDefault,
        RemoteReasoningWireFormat.None, AllReasoning,
        new Uri("https://example.invalid/privacy"), new Uri("https://example.invalid/terms"),
        new Uri("https://example.invalid/pricing"), GenericRetention,
        "CustomApiRetentionStatement", PolicyVerifiedAtUtc,
        "picforlater.remote.custom.interface", "custom.interface.disclosure.v1", "model-id");
}
