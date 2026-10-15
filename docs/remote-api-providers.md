# Remote API Presets, Third-Party Policies, and Contract Verification

Full verification: 2026-08-01; incremental preset updates: 2026-10-15. This document records engineering facts; it does not guarantee provider privacy, pricing, availability, or model lifetime. Before user content is sent, the synthetic connection test must succeed and the user must give consent for the current version.

Except for custom interfaces, presets fix the endpoint, protocol, authentication, and secure request policies. Users provide an API key (Ollama/vLLM can work without one) and model ID, and can adjust explicitly supported reasoning levels, token limits, and timeouts. Changes to request fields or advanced parameters switch execution back to `Local` and invalidate connection verification and prior consent. Policy-only changes clear consent and return execution to `Local`, retaining connection verification.

## Contract Matrix

| Category | Preset | Fixed endpoint | Default model | Input | Protocol / structured-output policy | Default reasoning policy |
|---|---|---|---|---|---|---|
| Official international providers | OpenAI | `api.openai.com/v1/chat/completions` | `gpt-6-luna` | Text, image | OpenAI / JSON Schema | `reasoning_effort=none`; optional disabled/low/medium/high/default |
| Official international providers | Anthropic / Claude | `api.anthropic.com/v1/messages` | `claude-sonnet-4-6` | Text, image | Messages / JSON Schema | Thinking disabled (omitted); `max_tokens=1024` |
| Official international providers | Google Gemini | `generativelanguage.googleapis.com/v1beta/openai/chat/completions` | `gemini-3.6-flash` | Text, image | OpenAI / JSON Schema | Provider default |
| Official international providers | Meta AI | `api.meta.ai/v1/chat/completions` | `muse-spark-1.3` | Text, image | OpenAI / JSON Schema | `reasoning_effort=low`; optional low/medium/high/default |
| Official international providers | xAI / Grok | `api.x.ai/v1/chat/completions` | `grok-4.5` | Text, image | OpenAI / JSON Schema | `reasoning_effort=low`; optional low/medium/high/default |
| Official international providers | Perplexity Sonar (Legacy) | `api.perplexity.ai/v1/sonar` | `sonar` | Text, image (gated by connection test) | Sonar OpenAI-compatible / JSON Schema | Default; `disable_search=true` explicitly |
| Official Chinese providers | DeepSeek | `api.deepseek.com/chat/completions` | `deepseek-flash` | Text, image | OpenAI / JSON Object + full prompt contract | `thinking.type=disabled`; can return to default |
| Official Chinese providers | Moonshot AI / Kimi | `api.moonshot.cn/v1/chat/completions` | `kimi-k2.6` | Text, image | OpenAI / prompt contract only | `thinking.type=disabled`; can return to default |
| Official Chinese providers | Tencent Hunyuan | `tokenhub.tencentmaas.com/v1/chat/completions` | `hy3` | Text | TokenHub OpenAI / JSON Schema | `reasoning_effort=low`; optional low/medium/high/default |
| Official Chinese providers | Volcengine / Doubao | `ark.cn-beijing.volces.com/api/v3/chat/completions` | `doubao-seed-2-1-lite-260915` | Text, image | OpenAI / JSON Object | `thinking.type=disabled`; can return to default |
| Official Chinese providers | Alibaba Cloud Model Studio / Tongyi Qianwen Qwen | `dashscope.aliyuncs.com/compatible-mode/v1/chat/completions` | `qwen3.5-plus` | Text, image | OpenAI / JSON Object | `enable_thinking=false`; can return to default |
| Official Chinese providers | Zhipu BigModel / GLM | `open.bigmodel.cn/api/paas/v4/chat/completions` | `glm-5.2` | Text | OpenAI / JSON Object | `thinking.type=disabled`; can return to default |
| Official Chinese providers | Baidu AI Cloud Qianfan / ERNIE | `qianfan.baidubce.com/v2/chat/completions` | `ernie-5.0` | Text, image | OpenAI / JSON Object | Provider default |
| Official Chinese providers | MiniMax | `api.minimax.cn/anthropic/v1/messages` | `MiniMax-M3` | Text, image | Anthropic-compatible / prompt contract only; reads the first text block | Provider default |
| Official Chinese providers | Xiaomi MiMo | `api.xiaomimimo.com/v1/chat/completions` | `mimo-v2.6-flash` | Text, image | OpenAI / JSON Object | `thinking.type=disabled`; can return to default |
| Aggregators / inference | SiliconFlow | `api.siliconflow.cn/v1/chat/completions` | `Pro/zai-org/GLM-5.1` | Text | OpenAI / JSON Object | `enable_thinking=false`; can return to default |
| Aggregators / inference | OpenRouter | `openrouter.ai/api/v1/chat/completions` | `openai/gpt-4.1-mini` | Text, image | OpenAI / JSON Schema | Default; upstream fallback disabled and parameters required |
| Aggregators / inference | Groq | `api.groq.com/openai/v1/chat/completions` | `qwen/qwen3.8-27b` | Text, image | OpenAI / JSON Object | `reasoning_effort=none`; optional disabled/low/medium/high/default |
| Aggregators / inference | Together AI | `api.together.xyz/v1/chat/completions` | `Qwen/Qwen3.5-9B` | Text, image | OpenAI / JSON Schema | `reasoning.enabled=false`; can return to default |
| Local / private deployments | Ollama | `127.0.0.1:11434/v1/chat/completions` | `qwen3-vl:4b` | Text, image | OpenAI / JSON Schema | Provider default |
| Local / private deployments | vLLM | `127.0.0.1:8000/v1/chat/completions` | `Qwen/Qwen3-VL-4B-Instruct` | Text, image | OpenAI / JSON Schema | Provider default |
| Custom | Custom interface | User-provided; public HTTPS or strict loopback only | User-provided | Text, image (must be verified separately) | OpenAI or Messages; JSON Schema/JSON Object/prompt contract only | User selects explicit wire format; gated by connection test |

`PromptOnly` only omits `response_format` when unsupported by the provider; the response must still pass the same checks for the eight-key shape, a non-empty and evidence-based title and summary, entities, language, length, and draft quality. All modes disable tools/function calling. Failures do not switch providers, input modes, or execution locations.

OpenAI-compatible image requests send only `text` and `image_url.url` (data URL), omitting optional `image_url.detail` and the default `n=1`. Only one image is sent, locally resized, re-encoded, and stripped of metadata. Output is bounded by the parser and `max_tokens`.

## Policy and Pricing Links

Policies depend on the provider, plan, region, and account controls. Links do not guarantee zero retention, no training, a fixed data region, or deletion.

| Preset | Privacy/security | Terms/license | Pricing/models/docs |
|---|---|---|---|
| OpenAI | [Privacy](https://openai.com/policies/privacy-policy/) | [Services agreement](https://openai.com/policies/services-agreement/) | [API pricing](https://openai.com/api/pricing/) |
| Anthropic | [Privacy](https://www.anthropic.com/legal/privacy) | [Commercial terms](https://www.anthropic.com/legal/commercial-terms) | [Pricing](https://platform.claude.com/docs/en/about-claude/pricing) |
| Google Gemini | [Privacy](https://policies.google.com/privacy) | [Gemini API terms](https://ai.google.dev/gemini-api/terms) | [Pricing](https://ai.google.dev/gemini-api/docs/pricing) |
| Meta AI | [Privacy](https://www.facebook.com/privacy/policy/) | [Model API terms](https://dev.meta.ai/legal/terms-of-service) | [Pricing](https://dev.meta.ai/docs/pricing-rate-limits) |
| xAI | [Data processing addendum](https://x.ai/legal/data-processing-addendum) | [Enterprise terms](https://x.ai/legal/terms-of-service-enterprise) | [Models](https://docs.x.ai/developers/models) |
| Perplexity | [Privacy](https://www.perplexity.ai/hub/legal/privacy-policy) | [Terms](https://www.perplexity.ai/hub/legal/terms-of-service) | [Pricing](https://docs.perplexity.ai/getting-started/pricing) |
| DeepSeek | [Privacy](https://cdn.deepseek.com/policies/en-US/deepseek-privacy-policy.html) | [Platform terms](https://cdn.deepseek.com/policies/en-US/deepseek-open-platform-terms-of-service.html) | [Pricing](https://api-docs.deepseek.com/quick_start/pricing) |
| Kimi | [Privacy](https://platform.kimi.com/docs/agreement/userprivacy) | [Open Platform Agreement](https://platform.kimi.com/docs/agreement/modeluse) | [Pricing](https://platform.kimi.com/docs/pricing/chat) |
| Tencent Hunyuan | [Privacy](https://cloud.tencent.com/document/product/1823/130108) | [Terms](https://cloud.tencent.com/document/product/301/129852) | [Pricing](https://cloud.tencent.com/document/product/1823/130055) |
| Volcengine / Doubao | [Privacy](https://www.volcengine.com/docs/6256/64902) | [Terms](https://www.volcengine.com/docs/6256/64903) | [Pricing](https://docs.volcengine.com/docs/ark/model-pricing?lang=zh) |
| Alibaba Cloud Model Studio | [Privacy](https://terms.alicdn.com/legal-agreement/terms/privacy_policy_full/20230801173543354/20230801173543354.html) | [Terms](https://terms.alicdn.com/legal-agreement/terms/common_platform_service/20230728213935489/20230728213935489.html) | [Pricing](https://help.aliyun.com/zh/model-studio/model-pricing) |
| Zhipu BigModel | [Privacy](https://docs.bigmodel.cn/cn/terms/privacy-policy) | [Service agreement](https://docs.bigmodel.cn/cn/terms/service-agreement) | [Pricing](https://open.bigmodel.cn/pricing) |
| Baidu AI Cloud Qianfan | [Privacy](https://cloud.baidu.com/doc/Agreements/s/Plr0fi68q) | [Terms](https://cloud.baidu.com/doc/qianfan/s/Cmh4stmpy) | [Pricing](https://cloud.baidu.com/doc/qianfan/s/wmh4sv6ya) |
| MiniMax | [Privacy](https://design.minimax.cn/protocol/privacy-policy) | [Terms](https://design.minimax.cn/protocol/user-agreement) | [Pricing](https://platform.minimax.cn/docs/pricing/overview) |
| Xiaomi MiMo | [Privacy](https://privacy.mi.com/XiaomiMiMoPlatform/zh_CN) | [Terms](https://mimo.mi.com/docs/terms/user-agreement) | [API pricing](https://mimo.mi.com/docs/zh-CN/price/pay-as-you-go) |
| SiliconFlow | [Privacy](https://api-docs.siliconflow.cn/docs/legals/privacy-policy) | [Terms](https://api-docs.siliconflow.cn/docs/legals/terms-of-service) | [Models](https://cloud.siliconflow.cn/me/models) |
| OpenRouter | [Privacy](https://openrouter.ai/privacy) | [Terms](https://openrouter.ai/terms) | [Models/pricing](https://openrouter.ai/models) |
| Groq | [Privacy](https://console.groq.com/docs/legal/customer-data-processing-addendum) | [Terms](https://console.groq.com/docs/legal/services-agreement) | [Pricing](https://groq.com/pricing/) |
| Together AI | [Privacy](https://www.together.ai/privacy) | [Terms](https://www.together.ai/terms-of-service) | [Pricing](https://www.together.ai/pricing) |
| Ollama | [Privacy](https://ollama.com/privacy) | [Terms](https://ollama.com/terms) | [Qwen3 VL model](https://ollama.com/library/qwen3-vl) |
| vLLM | [Security](https://docs.vllm.ai/en/latest/usage/security/) | [Apache License](https://github.com/vllm-project/vllm/blob/main/LICENSE) | [OpenAI server](https://docs.vllm.ai/en/latest/serving/online_serving/openai_compatible_server/) |

Loopback only means the network target is on the local machine; logging and retention behavior remain under the control of the service the user runs. The owner, policies, certificates, compatibility, and pricing of custom interfaces cannot be verified in advance by this project.

## Verification Levels and Known Limitations

| Scope | Verification record | Limitations |
|---|---|---|
| All presets | Deterministic fake handler for the production transport and real loopback HTTP integration; covers parser, response-body limits, authentication redaction, redirect/SSRF, and no image sending in text mode | Does not establish that real cloud APIs are suitable |
| DeepSeek `RemoteOcrText` | Three live tests using synthetic OCR text passed on 2026-08-01; a later retest returned HTTP 200 but an empty title | The prompt and full parser have been fixed; a live retest with the fix has not yet been completed |
| Alibaba Cloud Model Studio / Qwen `RemoteVision` | Verified on 2026-08-01 with `qwen3-vl-flash-2026-01-22` and a built-in licensed 640×960 cat image; HTTP 200, eight root fields returned in about 3.16 seconds, and the final production payload contract and regression checks passed | Uses `json_object` and `enable_thinking=false`, omitting `detail` / `n=1`; only when there is exactly one additional low-risk `visualFacts` item are the first three retained with a warning; other excess items are strictly rejected |
| Other cloud presets | Official contract review and fake HTTP | No paid tests; the model entered by the user must still pass a synthetic test with no user content |

A 1×1 PNG used in that Qwen test once returned HTTP 400. Subsequent image connection tests use the verified licensed cat image above.

Real tests use dedicated credentials and fixed samples; see [tests/README.md](../tests/README.md) for how to run them. Price estimates are valid only when usage data is reliable and pricing has been verified; aggregators are also affected by upstream providers, region, caching, and plans.

## Preset Updates (2026-10-15)

Only the listed old default model IDs migrate, and only when the preset endpoint has not been edited. Other user-entered models and custom endpoints are preserved. Migrated settings keep API credentials, user token limits, timeouts, and explicitly selected reasoning; an old `ProviderDefault` reasoning value becomes the new preset's safe default. Migration clears connection verification and consent and returns execution to `Local`; run a new connection test and confirm consent before remote use.

| Preset | Old default → new default | Change and official basis |
|---|---|---|
| OpenAI | `gpt-4.1-mini-2025-04-14` → `gpt-6-luna` | Sets `reasoning_effort=none`; the replacement supports text, images, and JSON Schema. [Deprecations](https://developers.openai.com/api/docs/deprecations), [GPT-6 Luna](https://developers.openai.com/api/docs/models/gpt-6-luna) |
| Anthropic / Claude | `claude-sonnet-4-5-20250929` → `claude-sonnet-4-6` | Retains the Messages contract, omitted thinking, and a 1,024-token output limit. Sonnet 4.6 remains active; Sonnet 5.5 defaults to adaptive thinking and rejects disabled thinking, so adopting it would require a separate request-contract change. [Model lifecycle](https://platform.claude.com/docs/en/about-claude/model-deprecations), [Sonnet 4.6](https://platform.claude.com/docs/en/models/sonnet-4-6/overview), [Sonnet 5.5 migration guide](https://platform.claude.com/docs/en/models/sonnet-5-5/migration-guide) |
| Google Gemini | `gemini-3.5-flash` → `gemini-3.6-flash` | Gemini automatically routes the old 3.5 name to 3.6; updating the preset name does not mean 3.5 support ended. [Deprecations and automatic routing](https://ai.google.dev/gemini-api/docs/deprecations) |
| Moonshot AI / Kimi | `kimi-k2.5` → `kimi-k2.6` | K2.5 retired on 2026-08-31. The preset keeps text-and-image input and PromptOnly output, and disables thinking by default. [Model availability](https://platform.kimi.com/docs/models), [K2.6 thinking control](https://platform.kimi.com/docs/guide/kimi-k2-6-quickstart) |
| Volcengine / Doubao | `doubao-seed-2-0-lite-260215` → `doubao-seed-2-1-lite-260915` | Moves to the current recommended Lite model while retaining text-and-image input, JSON Object output, and disabled thinking. [Model deprecation notice](https://docs.volcengine.com/docs/ark/model-deprecation-notice?lang=zh) |

OpenRouter remains on `openai/gpt-4.1-mini`; it is a separately listed model on the OpenRouter endpoint and is not covered by OpenAI's model deprecation notice. [OpenRouter model](https://openrouter.ai/openai/gpt-4.1-mini)

- Meta AI uses the current Model API and the standard `muse-spark-1.3` model, Bearer authentication, [Chat Completions](https://dev.meta.ai/docs/protocols/chat-completions), base64 [image input](https://dev.meta.ai/docs/image-understanding), and [JSON Schema output](https://dev.meta.ai/docs/structured-output). Reasoning defaults to low; disabling reasoning is not supported by Muse Spark. Contributor model IDs have different data policies and are not the default.
- Xiaomi MiMo uses the pay-as-you-go OpenAI-compatible endpoint and `mimo-v2.6-flash`, with Bearer authentication via the [documented OpenAI SDK configuration](https://mimo.mi.com/models/zh-CN/mimo-v2.6-flash), base64 [image input](https://mimo.mi.com/docs/zh-CN/quick-start/usage-guide/multimodal-understanding/image-understanding), [JSON Object output](https://mimo.mi.com/docs/zh-CN/quick-start/usage-guide/text-generation/structured-output), and [thinking disabled](https://mimo.mi.com/docs/zh-CN/quick-start/usage-guide/text-generation/deep-thinking). Token Plan keys require their own endpoint and cannot be used with this default endpoint.

Perplexity remains **Perplexity Sonar (Legacy)** at `/v1/sonar`, using `sonar` with `disable_search=true`. Perplexity ended support for the legacy API on 2026-09-27, while synchronous compatibility conversion remains available. Its Agent API needs a separate request/response integration; changing only the endpoint would not preserve the current contract. The Agent API fast preset includes search and fallback behavior, so it does not meet this preset's request policy. [Migration overview](https://docs.perplexity.ai/docs/agent-api/migrate-from-sonar/overview), [migration details](https://docs.perplexity.ai/docs/agent-api/migrate-from-sonar/how-to)

Other models remain unchanged. Three evidence limits remain:

- ERNIE 5.0 keeps JSON Object because the available official documentation does not establish that it is unsupported; this is not a claim that the format is unavailable. [ERNIE 5.0 documentation](https://intl.cloud.baidu.com/en/doc/qianfan/s/6m8r1x5hz-intl-en), [special agreement](https://cloud.baidu.com/doc/qianfan/s/emh4stmvj)
- SiliconFlow's `Pro/zai-org/GLM-5.1` `enable_thinking=false` compatibility is not fully established by its documentation, so it remains an unconfirmed contract. [Chat Completions parameters](https://docs.siliconflow.cn/docs/api/chat-completions-post)
- Together's official Qwen3.5-9B page still uses the `.xyz` API host, so the endpoint is not treated as unavailable. [Qwen3.5-9B](https://www.together.ai/models/qwen3-5-9b)

The Kimi Open Platform Agreement was updated on 2026-08-24 and took effect on 2026-08-31.

Policy links were reviewed on 2026-10-15 for Meta AI, Xiaomi MiMo, xAI, DeepSeek, Kimi, Tencent Hunyuan, Qwen, Zhipu BigModel, Baidu Qianfan, MiniMax, SiliconFlow, OpenRouter, Groq, and vLLM. Other providers retain their 2026-08-01 policy review date; model and pricing changes do not imply a broader policy review. No paid API calls were made. New presets and migrated presets still require a successful connection test and current consent.

TokenHub and BigModel privacy links were confirmed through official product documentation; their complete policy text could not be retrieved during this review.

## Preset Migration Record (2026-09-21)

Only the old default models in the table below were migrated; other user-entered models and custom endpoints were retained. Changes to models, input capabilities, or request parameters clear verification/consent and switch execution back to local. Enabling image capability does not automatically select image upload; a new test and consent are required before it can be enabled. This update reviewed only models/request contracts and ran local automated tests; it did not update the privacy-policy verification date or call paid APIs.

| Preset | Old default → new default | Change and basis |
|---|---|---|
| Baidu AI Cloud Qianfan | `ernie-4.5-turbo-128k` → `ernie-5.0` | Enabled images; retained OpenAI / JSON Object; [models and modalities](https://cloud.baidu.com/doc/qianfan-api/s/Dmba8k71y) |
| MiniMax | `MiniMax-M2.7` → `MiniMax-M3` | Enabled images, using the Anthropic endpoint, Bearer, and PromptOnly in the matrix; reasoning disabled by default; [compatibility contract](https://platform.minimax.cn/docs/api-reference/text-anthropic-api) |
| Perplexity | `sonar` unchanged | Enabled images, kept search disabled and JSON Schema, and gated use with an image connection test for the current account/model; did not disable the entry point for lack of a documented example for that model; [image format](https://docs.perplexity.ai/docs/sonar/media) |
| DeepSeek | `deepseek-v4-flash` / `deepseek-v4-flash-vision-exp` → `deepseek-flash` | The new Flash model takes over from the old model and adds image input; [models and capabilities](https://api-docs.deepseek.com/quick_start/pricing/) |
| Tencent Hunyuan | `hy3-preview` → `hy3` | The old preview was discontinued; text, JSON Schema, and reasoning levels were retained; [announcement](https://cloud.tencent.com/announce/detail/2391) |
| SiliconFlow | `Pro/zai-org/GLM-4.7` → `Pro/zai-org/GLM-5.1` | The old model was discontinued; text/JSON Object and disabled reasoning were retained; [announcement](https://docs.siliconflow.cn/docs/release-notes/overview), [models](https://www.siliconflow.cn/models), [parameters](https://docs.siliconflow.cn/docs/api/chat-completions-post) |
| Groq | Llama 4 Scout → `qwen/qwen3.8-27b` | Scout was discontinued for standard accounts; the new model supports images and is marked Preview. New configurations disable reasoning, while existing configurations retain supported levels; [deprecation notice](https://console.groq.com/docs/deprecations), [model](https://console.groq.com/docs/model/qwen/qwen3.8-27b) |

The Baidu and MiniMax migrations provide visual default models; they do not mean the old models were discontinued.

<details>
<summary>Contract Verification and Historical Sources (2026-08-01)</summary>

- Hunyuan moved from the old platform to Guangzhou TokenHub, changing `hunyuan-turbos-latest` to `hy3-preview`; the old endpoint, verification, and consent were not carried over. The Hunyuan preset then moved to `hy3` in September. Fixed presets do not route across regions; Singapore accounts must use a custom interface to configure an international endpoint.
- Doubao and Together enabled images according to official multimodal/structured-vision contracts. `kimi-k2.5` was still available at the time, so migration to the official example 2.6 was not forced; the model ID remains editable.

The remaining endpoints, authentication methods, and capabilities were checked against official documentation at the time. Sources include:

- OpenAI: [image inputs](https://developers.openai.com/api/docs/guides/images-vision), [structured outputs](https://developers.openai.com/api/docs/guides/structured-outputs).
- Anthropic: [Messages](https://platform.claude.com/docs/en/build-with-claude/working-with-messages), [structured outputs](https://platform.claude.com/docs/en/build-with-claude/structured-outputs).
- Gemini: [compatibility layer](https://ai.google.dev/gemini-api/docs/openai), [3.5 Flash](https://ai.google.dev/gemini-api/docs/models/gemini-3.5-flash).
- Hunyuan: [migration from the old platform](https://cloud.tencent.com/document/product/1729/131925), [TokenHub API](https://cloud.tencent.com/document/product/1823/130078).
- Doubao: [Seed 2.0](https://www.volcengine.com/docs/82379/1795150); Alibaba Cloud Model Studio: [Chat API](https://help.aliyun.com/zh/model-studio/qwen-api-via-openai-chat-completions); MiniMax: [text generation](https://platform.minimaxi.com/docs/guides/text-generation).
- OpenRouter: [image input](https://openrouter.ai/docs/guides/overview/multimodal/image-understanding); Groq: [Vision](https://console.groq.com/docs/vision); Together: [structured vision extraction](https://docs.together.ai/docs/inference/vision/structured-extraction).
- Ollama: [compatibility layer](https://docs.ollama.com/api/openai-compatibility); vLLM: [multimodal inputs](https://docs.vllm.ai/en/stable/features/multimodal_inputs/).

</details>
