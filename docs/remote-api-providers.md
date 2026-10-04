# 远程 API preset、第三方政策与契约核验

完整核验：2026-08-01；预设增量更新：2026-09-21。本文记录工程事实，不保证供应商
隐私、价格、可用性或模型寿命。发送用户内容前，须通过合成连接测试并完成当前版本同意。

除自定义接口外，preset 固定 endpoint、协议、鉴权和安全请求策略；用户提供 API key
（Ollama/vLLM 可无 key）及 model ID，并可调整明确支持的思考档位、token 上限和超时。
固定字段或高级参数变化会切回 `Local`，使连接验证及旧同意失效。

## 契约矩阵

| 类别 | Preset | 固定 endpoint | 默认 model | 输入 | 协议 / 结构化策略 | 默认思考策略 |
|---|---|---|---|---|---|---|
| 国际官方 | OpenAI | `api.openai.com/v1/chat/completions` | `gpt-4.1-mini-2025-04-14` | 文字、图片 | OpenAI / JSON Schema | 供应商默认 |
| 国际官方 | Anthropic / Claude | `api.anthropic.com/v1/messages` | `claude-sonnet-4-5-20250929` | 文字、图片 | Messages / JSON Schema | 供应商默认 |
| 国际官方 | Google Gemini | `generativelanguage.googleapis.com/v1beta/openai/chat/completions` | `gemini-3.5-flash` | 文字、图片 | OpenAI / JSON Schema | 供应商默认 |
| 国际官方 | xAI / Grok | `api.x.ai/v1/chat/completions` | `grok-4.5` | 文字、图片 | OpenAI / JSON Schema | `reasoning_effort=low`；可选 low/medium/high/默认 |
| 国际官方 | Perplexity Sonar | `api.perplexity.ai/v1/sonar` | `sonar` | 文字、图片（连接测试把关） | Sonar OpenAI 兼容 / JSON Schema | 默认；显式 `disable_search=true` |
| 中国官方 | DeepSeek | `api.deepseek.com/chat/completions` | `deepseek-flash` | 文字、图片 | OpenAI / JSON Object + 完整提示契约 | `thinking.type=disabled`；可切回默认 |
| 中国官方 | 月之暗面 / Kimi | `api.moonshot.cn/v1/chat/completions` | `kimi-k2.5` | 文字、图片 | OpenAI / 仅提示契约 | 供应商默认 |
| 中国官方 | 腾讯混元 | `tokenhub.tencentmaas.com/v1/chat/completions` | `hy3` | 文字 | TokenHub OpenAI / JSON Schema | `reasoning_effort=low`；可选 low/medium/high/默认 |
| 中国官方 | 火山引擎 / 豆包 | `ark.cn-beijing.volces.com/api/v3/chat/completions` | `doubao-seed-2-0-lite-260215` | 文字、图片 | OpenAI / JSON Object | `thinking.type=disabled`；可切回默认 |
| 中国官方 | 阿里云百炼 / Qwen | `dashscope.aliyuncs.com/compatible-mode/v1/chat/completions` | `qwen3.5-plus` | 文字、图片 | OpenAI / JSON Object | `enable_thinking=false`；可切回默认 |
| 中国官方 | 智谱 BigModel / GLM | `open.bigmodel.cn/api/paas/v4/chat/completions` | `glm-5.2` | 文字 | OpenAI / JSON Object | `thinking.type=disabled`；可切回默认 |
| 中国官方 | 百度千帆 / 文心 | `qianfan.baidubce.com/v2/chat/completions` | `ernie-5.0` | 文字、图片 | OpenAI / JSON Object | 供应商默认 |
| 中国官方 | MiniMax | `api.minimax.cn/anthropic/v1/messages` | `MiniMax-M3` | 文字、图片 | Anthropic 兼容 / 仅提示契约；读取首个 text block | 供应商默认 |
| 聚合/推理 | SiliconFlow | `api.siliconflow.cn/v1/chat/completions` | `Pro/zai-org/GLM-5.1` | 文字 | OpenAI / JSON Object | `enable_thinking=false`；可切回默认 |
| 聚合/推理 | OpenRouter | `openrouter.ai/api/v1/chat/completions` | `openai/gpt-4.1-mini` | 文字、图片 | OpenAI / JSON Schema | 默认；禁止上游 fallback 并要求参数支持 |
| 聚合/推理 | Groq | `api.groq.com/openai/v1/chat/completions` | `qwen/qwen3.8-27b` | 文字、图片 | OpenAI / JSON Object | `reasoning_effort=none`；可选关闭/low/medium/high/默认 |
| 聚合/推理 | Together AI | `api.together.xyz/v1/chat/completions` | `Qwen/Qwen3.5-9B` | 文字、图片 | OpenAI / JSON Schema | `reasoning.enabled=false`；可切回默认 |
| 本机/私有 | Ollama | `127.0.0.1:11434/v1/chat/completions` | `qwen3-vl:4b` | 文字、图片 | OpenAI / JSON Schema | 供应商默认 |
| 本机/私有 | vLLM | `127.0.0.1:8000/v1/chat/completions` | `Qwen/Qwen3-VL-4B-Instruct` | 文字、图片 | OpenAI / JSON Schema | 供应商默认 |
| 自定义 | 自定义接口 | 用户输入；仅公共 HTTPS 或严格 loopback | 用户输入 | 文字、图片（需自行验证） | OpenAI 或 Messages；JSON Schema/JSON Object/仅提示契约 | 用户选择显式 wire format；连接测试把关 |

`PromptOnly` 仅省略供应商不支持的 `response_format`，返回仍须通过相同的八键 shape、
非空且有依据的标题/简介、实体、语言、长度及草稿质量检查。所有模式禁用 tools/function calling，
失败不切换供应商、输入模式或执行位置。

OpenAI-compatible 图片请求只发送 `text` 与 `image_url.url`（data URL），省略可选
`image_url.detail` 和默认 `n=1`。只发送单张经本地缩放、重编码和去元数据的图片，输出受 parser
及 `max_tokens` 约束。

## 政策与价格链接

政策取决于供应商、套餐、地区和账户控制；链接不构成零保留、禁训练、固定数据地区或可删除保证。

| Preset | 隐私 | 条款 | 价格/模型 |
|---|---|---|---|
| OpenAI | [Privacy](https://openai.com/policies/privacy-policy/) | [Services agreement](https://openai.com/policies/services-agreement/) | [API pricing](https://openai.com/api/pricing/) |
| Anthropic | [Privacy](https://www.anthropic.com/legal/privacy) | [Commercial terms](https://www.anthropic.com/legal/commercial-terms) | [Pricing](https://platform.claude.com/docs/en/about-claude/pricing/overview) |
| Google Gemini | [Privacy](https://policies.google.com/privacy) | [Gemini API terms](https://ai.google.dev/gemini-api/terms) | [Pricing](https://ai.google.dev/gemini-api/docs/pricing) |
| xAI | [Privacy](https://x.ai/legal/privacy-policy) | [Terms](https://x.ai/legal/terms-of-service) | [Models](https://docs.x.ai/docs/models) |
| Perplexity | [Privacy](https://www.perplexity.ai/hub/legal/privacy-policy) | [Terms](https://www.perplexity.ai/hub/legal/terms-of-service) | [Pricing](https://docs.perplexity.ai/getting-started/pricing) |
| DeepSeek | [Privacy](https://cdn.deepseek.com/policies/en-US/deepseek-privacy-policy.html) | [Terms](https://cdn.deepseek.com/policies/en-US/deepseek-terms-of-use.html) | [Pricing](https://api-docs.deepseek.com/quick_start/pricing) |
| Kimi | [Privacy](https://www.moonshot.cn/privacy-policy) | [Terms](https://www.moonshot.cn/terms-of-service) | [Pricing](https://platform.kimi.com/docs/pricing/chat) |
| 腾讯混元 | [Privacy](https://www.tencentcloud.com/document/product/301/17345) | [Terms](https://www.tencentcloud.com/document/product/301/9247) | [TokenHub models](https://cloud.tencent.com/document/product/1823/130051) |
| 火山/豆包 | [Privacy](https://www.volcengine.com/docs/6256/64902) | [Terms](https://www.volcengine.com/docs/6256/64903) | [Pricing](https://www.volcengine.com/docs/82379/1099320) |
| 阿里百炼 | [Privacy](https://terms.alicdn.com/legal-agreement/terms/privacy_policy_full/20221129171420545/20221129171420545.html) | [Terms](https://terms.alicdn.com/legal-agreement/terms/suit_bu1_ali_cloud/suit_bu1_ali_cloud202112211045_86198.html) | [Pricing](https://help.aliyun.com/zh/model-studio/model-pricing) |
| 智谱 | [Privacy](https://www.zhipuai.cn/privacy) | [Terms](https://www.zhipuai.cn/terms) | [Pricing](https://open.bigmodel.cn/pricing) |
| 百度千帆 | [Privacy](https://cloud.baidu.com/doc/Agreements/s/Kjwvy245m) | [Terms](https://cloud.baidu.com/doc/Agreements/s/2jwvx9m0a) | [Pricing](https://cloud.baidu.com/doc/qianfan-docs/s/6m9l6p8iw) |
| MiniMax | [Privacy](https://www.minimaxi.com/privacy) | [Terms](https://www.minimaxi.com/terms) | [Pricing](https://platform.minimaxi.com/docs/guides/pricing) |
| SiliconFlow | [Privacy](https://siliconflow.cn/privacy-policy) | [Terms](https://siliconflow.cn/terms-of-service) | [Models](https://cloud.siliconflow.cn/me/models) |
| OpenRouter | [Privacy](https://openrouter.ai/privacy) | [Terms](https://openrouter.ai/terms) | [Models/pricing](https://openrouter.ai/models) |
| Groq | [Privacy](https://groq.com/privacy-policy/) | [Terms](https://groq.com/terms-of-use/) | [Pricing](https://groq.com/pricing/) |
| Together AI | [Privacy](https://www.together.ai/privacy) | [Terms](https://www.together.ai/terms-of-service) | [Pricing](https://www.together.ai/pricing) |
| Ollama | [Privacy](https://ollama.com/privacy) | [Terms](https://ollama.com/terms) | [Models](https://ollama.com/search) |
| vLLM | [Security](https://docs.vllm.ai/en/latest/security.html) | [Governance](https://docs.vllm.ai/en/latest/community/governance.html) | [OpenAI server](https://docs.vllm.ai/en/latest/serving/openai_compatible_server.html) |

loopback 只表示网络目标在本机，日志和保留行为仍由用户运行的服务控制。自定义接口的
所有者、政策、证书、兼容性与价格无法由本项目预先核验。

## 验证等级与已知边界

| 范围 | 验证记录 | 限制 |
| --- | --- | --- |
| 全部 preset | 生产 transport 的确定性 fake handler、真实 loopback HTTP 集成；覆盖 parser、响应体上限、鉴权脱敏、redirect/SSRF 和文字模式不发送图片 | 不代表云端真实 API 合格 |
| DeepSeek `RemoteOcrText` | 2026-08-01 三次真实合成 OCR 测量通过，后续复测得到 HTTP 200 但标题为空 | 提示及完整 parser 已修正，修正后的真实复测尚未完成 |
| 百炼 / Qwen `RemoteVision` | 2026-08-01，以 `qwen3-vl-flash-2026-01-22` 和内置 640×960 授权猫图验证；HTTP 200，约 3.16 秒返回八个根字段，最终生产载荷 contract 与回归通过 | 使用 `json_object`、`enable_thinking=false`，省略 `detail` / `n=1`；仅对恰好多一条低风险 `visualFacts` 保留前三条并警告，其他越界严格拒绝 |
| 其他云 preset | 官方契约核对及 fake HTTP | 未做付费实测；用户所填 model 仍须通过无用户内容的合成测试 |

该轮 Qwen 测试中的 1×1 PNG 曾返回 HTTP 400，后续图片连接测试改用上述已验证的授权猫图。

真实测试使用专用凭据和固定样例，执行方式见 [tests/README.md](../tests/README.md)。
价格估算仅在 usage 可靠且定价已核验时成立；聚合平台还受上游、地区、缓存和套餐影响。

## 预设迁移记录（2026-09-21）

仅迁移下表中的旧默认模型，保留其他用户填写的模型及自定义端点。模型、输入能力或
请求参数变化会清除验证/同意并切回本地；图片能力开放不自动选择上传，重新测试和同意
后才能启用。本轮仅核对模型/请求契约并做本地自动化测试，未更新隐私政策核验日期或调用付费 API。

| Preset | 旧默认 → 新默认 | 增量与依据 |
| --- | --- | --- |
| 百度千帆 | `ernie-4.5-turbo-128k` → `ernie-5.0` | 开放图片，保留 OpenAI / JSON Object；[模型与模态](https://cloud.baidu.com/doc/qianfan-api/s/Dmba8k71y) |
| MiniMax | `MiniMax-M2.7` → `MiniMax-M3` | 开放图片，使用矩阵中的 Anthropic 端点、Bearer 和 PromptOnly，默认关闭思考；[兼容契约](https://platform.minimax.cn/docs/api-reference/text-anthropic-api) |
| Perplexity | `sonar` 不变 | 开放图片，保持关闭搜索和 JSON Schema，以当前账号/model 的图片连接测试把关，不因缺少该型号文档示例禁用入口；[图片格式](https://docs.perplexity.ai/docs/sonar/media) |
| DeepSeek | `deepseek-v4-flash` / `deepseek-v4-flash-vision-exp` → `deepseek-flash` | 新 Flash 承接旧模型并开放图片；[模型与能力](https://api-docs.deepseek.com/quick_start/pricing/) |
| 腾讯混元 | `hy3-preview` → `hy3` | 旧预览已下线，保留文字、JSON Schema 和思考档位；[公告](https://cloud.tencent.com/announce/detail/2391) |
| SiliconFlow | `Pro/zai-org/GLM-4.7` → `Pro/zai-org/GLM-5.1` | 旧型号已下线，保留文字/JSON Object 和关闭思考；[公告](https://docs.siliconflow.cn/docs/release-notes/overview)、[模型](https://www.siliconflow.cn/models)、[参数](https://docs.siliconflow.cn/docs/api/chat-completions-post) |
| Groq | Llama 4 Scout → `qwen/qwen3.8-27b` | Scout 对普通账户停服；新版支持图片，标记 Preview，新配置关闭思考，已有配置保留受支持档位；[下线公告](https://console.groq.com/docs/deprecations)、[模型](https://console.groq.com/docs/model/qwen/qwen3.8-27b) |

百度与 MiniMax 的迁移用于提供视觉默认模型，不表示旧模型已下线。

<details>
<summary>2026-08-01 契约核验与历史来源</summary>

- 混元从旧平台的 `hunyuan-turbos-latest` 迁到广州 TokenHub 的 `hy3-preview`，
  不沿用旧 endpoint、验证或同意；9 月再迁至 `hy3`。固定 preset 不跨地域路由，
  新加坡账号需用自定义接口配置国际 endpoint。
- 豆包与 Together 按官方多模态/结构化视觉契约开放图片。Kimi `kimi-k2.5` 当时仍可用，
  因此未强制迁到官方示例的 2.6；model ID 保持可编辑。

其余 endpoint、鉴权与能力按当时官方文档核对，来源包括：

- OpenAI：[图片输入](https://developers.openai.com/api/docs/guides/images-vision)、[结构化输出](https://developers.openai.com/api/docs/guides/structured-outputs)。
- Anthropic：[Messages](https://platform.claude.com/docs/en/build-with-claude/working-with-messages)、[结构化输出](https://platform.claude.com/docs/en/build-with-claude/structured-outputs)。
- Gemini：[兼容层](https://ai.google.dev/gemini-api/docs/openai)、[3.5 Flash](https://ai.google.dev/gemini-api/docs/models/gemini-3.5-flash)。
- 混元：[旧平台迁移](https://cloud.tencent.com/document/product/1729/131925)、[TokenHub API](https://cloud.tencent.com/document/product/1823/130078)。
- 豆包：[Seed 2.0](https://www.volcengine.com/docs/82379/1795150)；百炼：[Chat API](https://help.aliyun.com/zh/model-studio/qwen-api-via-openai-chat-completions)；MiniMax：[文本生成](https://platform.minimaxi.com/docs/guides/text-generation)。
- OpenRouter：[图片输入](https://openrouter.ai/docs/guides/overview/multimodal/image-understanding)；Groq：[Vision](https://console.groq.com/docs/vision)；Together：[结构化视觉提取](https://docs.together.ai/docs/inference/vision/structured-extraction)。
- Ollama：[兼容层](https://docs.ollama.com/api/openai-compatibility)；vLLM：[多模态输入](https://docs.vllm.ai/en/stable/features/multimodal_inputs/)。

</details>
