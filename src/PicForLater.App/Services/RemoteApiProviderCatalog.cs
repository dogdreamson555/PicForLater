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
    private static readonly DateTimeOffset PolicyReviewedAtUtc =
        new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
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
            "https://api.openai.com/v1/chat/completions", "gpt-6-luna", TextAndImage,
            "https://openai.com/policies/privacy-policy/", "https://openai.com/policies/services-agreement/", "https://openai.com/api/pricing/",
            reasoningMode: RemoteReasoningMode.Disabled,
            reasoningWireFormat: RemoteReasoningWireFormat.ReasoningEffort,
            supportedReasoningModes: AllReasoning) with
        {
            RetiredDefaultModelIds = ["gpt-4.1-mini-2025-04-14"],
        },
        AnthropicPreset(),
        OpenAiPreset("google-gemini-official", "google.gemini", "Google Gemini", RemoteApiProviderCategory.InternationalOfficial,
            "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions", "gemini-3.6-flash", TextAndImage,
            "https://policies.google.com/privacy", "https://ai.google.dev/gemini-api/terms", "https://ai.google.dev/gemini-api/docs/pricing") with
        {
            RetiredDefaultModelIds = ["gemini-3.5-flash"],
        },
        OpenAiPreset("meta-ai-official", "meta.ai", "Meta AI", RemoteApiProviderCategory.InternationalOfficial,
            "https://api.meta.ai/v1/chat/completions", "muse-spark-1.3", TextAndImage,
            "https://www.facebook.com/privacy/policy/", "https://dev.meta.ai/legal/terms-of-service", "https://dev.meta.ai/docs/pricing-rate-limits",
            reasoningMode: RemoteReasoningMode.Low,
            reasoningWireFormat: RemoteReasoningWireFormat.ReasoningEffort,
            supportedReasoningModes: EffortReasoning) with
        {
            PolicyVerifiedAtUtc = PolicyReviewedAtUtc,
        },
        OpenAiPreset("xai-official", "xai.grok", "xAI / Grok", RemoteApiProviderCategory.InternationalOfficial,
            "https://api.x.ai/v1/chat/completions", "grok-4.5", TextAndImage,
            "https://x.ai/legal/data-processing-addendum", "https://x.ai/legal/terms-of-service-enterprise", "https://docs.x.ai/developers/models",
            reasoningMode: RemoteReasoningMode.Low,
            reasoningWireFormat: RemoteReasoningWireFormat.ReasoningEffort,
            supportedReasoningModes: EffortReasoning) with
        {
            PolicyVerifiedAtUtc = PolicyReviewedAtUtc,
        },
        OpenAiPreset("perplexity-sonar-official", "perplexity.sonar", "Perplexity Sonar (Legacy)", RemoteApiProviderCategory.InternationalOfficial,
            "https://api.perplexity.ai/v1/sonar", "sonar", TextAndImage,
            "https://www.perplexity.ai/hub/legal/privacy-policy", "https://www.perplexity.ai/hub/legal/terms-of-service", "https://docs.perplexity.ai/getting-started/pricing", disableExternalSearch: true),

        OpenAiPreset("deepseek-official", "deepseek.official", "DeepSeek", RemoteApiProviderCategory.ChinaOfficial,
            "https://api.deepseek.com/chat/completions", "deepseek-flash", TextAndImage,
            "https://cdn.deepseek.com/policies/en-US/deepseek-privacy-policy.html", "https://cdn.deepseek.com/policies/en-US/deepseek-open-platform-terms-of-service.html", "https://api-docs.deepseek.com/quick_start/pricing", RemoteStructuredOutputMode.JsonObject,
            reasoningMode: RemoteReasoningMode.Disabled,
            reasoningWireFormat: RemoteReasoningWireFormat.ThinkingObject,
            supportedReasoningModes: ToggleReasoning) with
        {
            RetiredDefaultModelIds = ["deepseek-chat", "deepseek-reasoner", "deepseek-v4-flash", "deepseek-v4-flash-vision-exp"],
            PolicyVerifiedAtUtc = PolicyReviewedAtUtc,
        },
        OpenAiPreset("kimi-official", "moonshot.kimi", "月之暗面 / Kimi", RemoteApiProviderCategory.ChinaOfficial,
            "https://api.moonshot.cn/v1/chat/completions", "kimi-k2.6", TextAndImage,
            "https://platform.kimi.com/docs/agreement/userprivacy", "https://platform.kimi.com/docs/agreement/modeluse", "https://platform.kimi.com/docs/pricing/chat", RemoteStructuredOutputMode.PromptOnly,
            reasoningMode: RemoteReasoningMode.Disabled,
            reasoningWireFormat: RemoteReasoningWireFormat.ThinkingObject,
            supportedReasoningModes: ToggleReasoning) with
        {
            RetiredDefaultModelIds = ["kimi-k2.5"],
            PolicyVerifiedAtUtc = PolicyReviewedAtUtc,
        },
        OpenAiPreset("tencent-hunyuan-official", "tencent.hunyuan", "腾讯混元", RemoteApiProviderCategory.ChinaOfficial,
            "https://tokenhub.tencentmaas.com/v1/chat/completions", "hy3", TextOnly,
            "https://cloud.tencent.com/document/product/1823/130108", "https://cloud.tencent.com/document/product/301/129852", "https://cloud.tencent.com/document/product/1823/130055", RemoteStructuredOutputMode.JsonSchema,
            reasoningMode: RemoteReasoningMode.Low,
            reasoningWireFormat: RemoteReasoningWireFormat.ReasoningEffort,
            supportedReasoningModes: EffortReasoning,
            disclosureVersion: "tencent.hunyuan.disclosure.v2") with
        {
            RetiredDefaultModelIds = ["hunyuan-turbos-latest", "hy3-preview"],
            PolicyVerifiedAtUtc = PolicyReviewedAtUtc,
        },
        OpenAiPreset("volcengine-doubao-official", "volcengine.doubao", "火山引擎 / 豆包", RemoteApiProviderCategory.ChinaOfficial,
            "https://ark.cn-beijing.volces.com/api/v3/chat/completions", "doubao-seed-2-1-lite-260915", TextAndImage,
            "https://www.volcengine.com/docs/6256/64902", "https://www.volcengine.com/docs/6256/64903", "https://docs.volcengine.com/docs/ark/model-pricing?lang=zh", RemoteStructuredOutputMode.JsonObject,
            reasoningMode: RemoteReasoningMode.Disabled,
            reasoningWireFormat: RemoteReasoningWireFormat.ThinkingObject,
            supportedReasoningModes: ToggleReasoning,
            disclosureVersion: "volcengine.doubao.disclosure.v2") with
        {
            RetiredDefaultModelIds = ["doubao-seed-2-0-lite-260215"],
        },
        OpenAiPreset("alibaba-bailian-qwen-official", "alibaba.bailian.qwen", "阿里云百炼 / 通义千问 Qwen", RemoteApiProviderCategory.ChinaOfficial,
            "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions", "qwen3.5-plus", TextAndImage,
            "https://terms.alicdn.com/legal-agreement/terms/privacy_policy_full/20230801173543354/20230801173543354.html", "https://terms.alicdn.com/legal-agreement/terms/common_platform_service/20230728213935489/20230728213935489.html", "https://help.aliyun.com/zh/model-studio/model-pricing", RemoteStructuredOutputMode.JsonObject,
            reasoningMode: RemoteReasoningMode.Disabled,
            reasoningWireFormat: RemoteReasoningWireFormat.EnableThinkingBoolean,
            supportedReasoningModes: ToggleReasoning) with
        {
            PolicyVerifiedAtUtc = PolicyReviewedAtUtc,
        },
        OpenAiPreset("zhipu-glm-official", "zhipu.glm", "智谱 BigModel / GLM", RemoteApiProviderCategory.ChinaOfficial,
            "https://open.bigmodel.cn/api/paas/v4/chat/completions", "glm-5.2", TextOnly,
            "https://docs.bigmodel.cn/cn/terms/privacy-policy", "https://docs.bigmodel.cn/cn/terms/service-agreement", "https://open.bigmodel.cn/pricing", RemoteStructuredOutputMode.JsonObject,
            reasoningMode: RemoteReasoningMode.Disabled,
            reasoningWireFormat: RemoteReasoningWireFormat.ThinkingObject,
            supportedReasoningModes: ToggleReasoning) with
        {
            PolicyVerifiedAtUtc = PolicyReviewedAtUtc,
        },
        OpenAiPreset("baidu-qianfan-official", "baidu.qianfan", "百度智能云千帆 / 文心", RemoteApiProviderCategory.ChinaOfficial,
            "https://qianfan.baidubce.com/v2/chat/completions", "ernie-5.0", TextAndImage,
            "https://cloud.baidu.com/doc/Agreements/s/Plr0fi68q", "https://cloud.baidu.com/doc/qianfan/s/Cmh4stmpy", "https://cloud.baidu.com/doc/qianfan/s/wmh4sv6ya", RemoteStructuredOutputMode.JsonObject) with
        {
            RetiredDefaultModelIds = ["ernie-4.5-turbo-128k"],
            PolicyVerifiedAtUtc = PolicyReviewedAtUtc,
        },
        AnthropicCompatiblePreset(
            "minimax-official", "minimax.official", "MiniMax", RemoteApiProviderCategory.ChinaOfficial,
            "https://api.minimax.cn/anthropic/v1/messages", "MiniMax-M3", TextAndImage,
            "https://design.minimax.cn/protocol/privacy-policy", "https://design.minimax.cn/protocol/user-agreement", "https://platform.minimax.cn/docs/pricing/overview",
            RemoteApiAuthenticationKind.Bearer, RemoteStructuredOutputMode.PromptOnly) with
        {
            RetiredDefaultModelIds = ["MiniMax-M2.7"],
            PolicyVerifiedAtUtc = PolicyReviewedAtUtc,
        },
        OpenAiPreset("xiaomi-mimo-official", "xiaomi.mimo", "小米 / Xiaomi MiMo", RemoteApiProviderCategory.ChinaOfficial,
            "https://api.xiaomimimo.com/v1/chat/completions", "mimo-v2.6-flash", TextAndImage,
            "https://privacy.mi.com/XiaomiMiMoPlatform/zh_CN", "https://mimo.mi.com/docs/terms/user-agreement", "https://mimo.mi.com/docs/zh-CN/price/pay-as-you-go", RemoteStructuredOutputMode.JsonObject,
            reasoningMode: RemoteReasoningMode.Disabled,
            reasoningWireFormat: RemoteReasoningWireFormat.ThinkingObject,
            supportedReasoningModes: ToggleReasoning) with
        {
            PolicyVerifiedAtUtc = PolicyReviewedAtUtc,
        },

        OpenAiPreset("siliconflow-official", "siliconflow.cloud", "硅基流动 SiliconFlow / SiliconCloud", RemoteApiProviderCategory.Aggregator,
            "https://api.siliconflow.cn/v1/chat/completions", "Pro/zai-org/GLM-5.1", TextOnly,
            "https://api-docs.siliconflow.cn/docs/legals/privacy-policy", "https://api-docs.siliconflow.cn/docs/legals/terms-of-service", "https://cloud.siliconflow.cn/me/models", RemoteStructuredOutputMode.JsonObject,
            reasoningMode: RemoteReasoningMode.Disabled,
            reasoningWireFormat: RemoteReasoningWireFormat.EnableThinkingBoolean,
            supportedReasoningModes: ToggleReasoning) with
        {
            RetiredDefaultModelIds = ["Pro/zai-org/GLM-4.7"],
            PolicyVerifiedAtUtc = PolicyReviewedAtUtc,
        },
        OpenAiPreset("openrouter-official", "openrouter.official", "OpenRouter", RemoteApiProviderCategory.Aggregator,
            "https://openrouter.ai/api/v1/chat/completions", "openai/gpt-4.1-mini", TextAndImage,
            "https://openrouter.ai/privacy", "https://openrouter.ai/terms", "https://openrouter.ai/models", disableProviderFallbacks: true) with
        {
            PolicyVerifiedAtUtc = PolicyReviewedAtUtc,
        },
        OpenAiPreset("groq-official", "groq.official", "Groq", RemoteApiProviderCategory.Aggregator,
            "https://api.groq.com/openai/v1/chat/completions", "qwen/qwen3.8-27b", TextAndImage,
            "https://console.groq.com/docs/legal/customer-data-processing-addendum", "https://console.groq.com/docs/legal/services-agreement", "https://groq.com/pricing/", RemoteStructuredOutputMode.JsonObject,
            reasoningMode: RemoteReasoningMode.Disabled,
            reasoningWireFormat: RemoteReasoningWireFormat.ReasoningEffort,
            supportedReasoningModes: AllReasoning) with
        {
            RetiredDefaultModelIds = ["meta-llama/llama-4-scout-17b-16e-instruct"],
            PolicyVerifiedAtUtc = PolicyReviewedAtUtc,
        },
        OpenAiPreset("together-ai-official", "together.official", "Together AI", RemoteApiProviderCategory.Aggregator,
            "https://api.together.xyz/v1/chat/completions", "Qwen/Qwen3.5-9B", TextAndImage,
            "https://www.together.ai/privacy", "https://www.together.ai/terms-of-service", "https://www.together.ai/pricing",
            reasoningMode: RemoteReasoningMode.Disabled,
            reasoningWireFormat: RemoteReasoningWireFormat.ReasoningEnabledObject,
            supportedReasoningModes: ToggleReasoning,
            disclosureVersion: "together.official.disclosure.v2"),

        LoopbackPreset("ollama-local", "ollama.local", "Ollama", "http://127.0.0.1:11434/v1/chat/completions", "qwen3-vl:4b",
            "https://ollama.com/privacy", "https://ollama.com/terms", "https://ollama.com/library/qwen3-vl"),
        LoopbackPreset("vllm-local", "vllm.local", "vLLM", "http://127.0.0.1:8000/v1/chat/completions", "Qwen/Qwen3-VL-4B-Instruct",
            "https://docs.vllm.ai/en/latest/usage/security/", "https://github.com/vllm-project/vllm/blob/main/LICENSE",
            "https://docs.vllm.ai/en/latest/serving/online_serving/openai_compatible_server/") with
        {
            PolicyVerifiedAtUtc = PolicyReviewedAtUtc,
        },
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
        var migrateDefaultModel = existing is not null
            && !hasUserEndpointOverride
            && preset.RetiredDefaultModelIds.Contains(existing.ModelId, StringComparer.Ordinal);
        return new RemoteApiProfile
        {
            ProfileId = preset.ProfileId,
            ProviderId = preset.ProviderId,
            DisplayName = preset.DisplayName,
            EndpointId = hasUserEndpointOverride ? existing!.EndpointId : preset.EndpointId,
            BaseUri = hasUserEndpointOverride ? existing!.BaseUri : preset.BaseUri,
            ModelId = existing is null || migrateDefaultModel ? preset.DefaultModelId : existing.ModelId,
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
                && (!migrateDefaultModel || existing.ReasoningMode != RemoteReasoningMode.ProviderDefault)
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
        "claude-sonnet-4-6", TextAndImage,
        RemoteApiProtocol.AnthropicMessages, RemoteApiAuthenticationKind.XApiKey,
        RemoteStructuredOutputMode.JsonSchema, RemoteEndpointTrustMode.FixedHttps,
        "2023-06-01", false, false, RemoteReasoningMode.ProviderDefault,
        RemoteReasoningWireFormat.None, ProviderDefaultReasoning,
        new Uri("https://www.anthropic.com/legal/privacy"),
        new Uri("https://www.anthropic.com/legal/commercial-terms"),
        new Uri("https://platform.claude.com/docs/en/about-claude/pricing"),
        GenericRetention, GenericRetentionResource, PolicyVerifiedAtUtc,
        "picforlater.remote.anthropic.claude", "anthropic.claude.disclosure.v1",
        "claude-sonnet-4-6")
    {
        RetiredDefaultModelIds = ["claude-sonnet-4-5-20250929"],
    };

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
