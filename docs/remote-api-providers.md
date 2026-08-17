# 远程 API preset、第三方政策与契约核验

核验日期：2026-08-01。本文记录工程事实，不代表供应商对隐私、可用性、价格或
模型寿命的保证。模型、套餐、地区、保留/训练控制及价格都可能变化；设置页始终
显示供应商链接，合成连接测试通过且用户完成当前版本同意之前不会发送用户内容。

除“自定义接口”外，preset 固定 endpoint、协议、鉴权方式和安全请求策略。普通
配置只需要用户提供自己的 API key（Ollama/vLLM 可无 key）和可编辑的 model ID；
高级区可调整该 preset 明确声明支持的思考档位、最大输出 token 和超时。固定字段
或高级参数变化会切回 `Local` 并使连接验证与旧同意失效。

## 契约矩阵

| 类别 | Preset | 固定 endpoint | 默认 model | 输入 | 协议 / 结构化策略 | 默认思考策略 |
|---|---|---|---|---|---|---|
| 国际官方 | OpenAI | `api.openai.com/v1/chat/completions` | `gpt-4.1-mini-2025-04-14` | 文字、图片 | OpenAI / JSON Schema | 供应商默认 |
| 国际官方 | Anthropic / Claude | `api.anthropic.com/v1/messages` | `claude-sonnet-4-5-20250929` | 文字、图片 | Messages / JSON Schema | 供应商默认 |
| 国际官方 | Google Gemini | `generativelanguage.googleapis.com/v1beta/openai/chat/completions` | `gemini-3.5-flash` | 文字、图片 | OpenAI / JSON Schema | 供应商默认 |
| 国际官方 | xAI / Grok | `api.x.ai/v1/chat/completions` | `grok-4.5` | 文字、图片 | OpenAI / JSON Schema | `reasoning_effort=low`；可选 low/medium/high/默认 |
| 国际官方 | Perplexity Sonar | `api.perplexity.ai/v1/sonar` | `sonar` | 文字 | Sonar OpenAI 兼容 / JSON Schema | 默认；显式 `disable_search=true` |
| 中国官方 | DeepSeek | `api.deepseek.com/chat/completions` | `deepseek-v4-flash` | 文字 | OpenAI / JSON Object + 完整提示契约 | `thinking.type=disabled`；可切回默认 |
| 中国官方 | 月之暗面 / Kimi | `api.moonshot.cn/v1/chat/completions` | `kimi-k2.5` | 文字、图片 | OpenAI / 仅提示契约 | 供应商默认 |
| 中国官方 | 腾讯混元 | `tokenhub.tencentmaas.com/v1/chat/completions` | `hy3-preview` | 文字 | TokenHub OpenAI / JSON Schema | `reasoning_effort=low`；可选 low/medium/high/默认 |
| 中国官方 | 火山引擎 / 豆包 | `ark.cn-beijing.volces.com/api/v3/chat/completions` | `doubao-seed-2-0-lite-260215` | 文字、图片 | OpenAI / JSON Object | `thinking.type=disabled`；可切回默认 |
| 中国官方 | 阿里云百炼 / Qwen | `dashscope.aliyuncs.com/compatible-mode/v1/chat/completions` | `qwen3.5-plus` | 文字、图片 | OpenAI / JSON Object | `enable_thinking=false`；可切回默认 |
| 中国官方 | 智谱 BigModel / GLM | `open.bigmodel.cn/api/paas/v4/chat/completions` | `glm-5.2` | 文字 | OpenAI / JSON Object | `thinking.type=disabled`；可切回默认 |
| 中国官方 | 百度千帆 / 文心 | `qianfan.baidubce.com/v2/chat/completions` | `ernie-4.5-turbo-128k` | 文字 | OpenAI / JSON Object | 供应商默认（该模型默认非深度思考） |
| 中国官方 | MiniMax | `api.minimaxi.com/anthropic/v1/messages` | `MiniMax-M2.7` | 文字 | Anthropic 兼容 / 仅提示契约；读取首个 text block | 供应商默认 |
| 聚合/推理 | SiliconFlow | `api.siliconflow.cn/v1/chat/completions` | `Pro/zai-org/GLM-4.7` | 文字 | OpenAI / JSON Object | `thinking.type=disabled`；可切回默认 |
| 聚合/推理 | OpenRouter | `openrouter.ai/api/v1/chat/completions` | `openai/gpt-4.1-mini` | 文字、图片 | OpenAI / JSON Schema | 默认；禁止上游 fallback 并要求参数支持 |
| 聚合/推理 | Groq | `api.groq.com/openai/v1/chat/completions` | `meta-llama/llama-4-scout-17b-16e-instruct` | 文字、图片 | OpenAI / JSON Object | 供应商默认 |
| 聚合/推理 | Together AI | `api.together.xyz/v1/chat/completions` | `Qwen/Qwen3.5-9B` | 文字、图片 | OpenAI / JSON Schema | `reasoning.enabled=false`；可切回默认 |
| 本机/私有 | Ollama | `127.0.0.1:11434/v1/chat/completions` | `qwen3-vl:4b` | 文字、图片 | OpenAI / JSON Schema | 供应商默认 |
| 本机/私有 | vLLM | `127.0.0.1:8000/v1/chat/completions` | `Qwen/Qwen3-VL-4B-Instruct` | 文字、图片 | OpenAI / JSON Schema | 供应商默认 |
| 自定义 | 自定义接口 | 用户输入；仅公共 HTTPS 或严格 loopback | 用户输入 | 文字、图片（需自行验证） | OpenAI 或 Messages；JSON Schema/JSON Object/仅提示契约 | 用户选择显式 wire format；连接测试把关 |

`PromptOnly` 不降低本地 parser 标准：它只是不向不声明 `response_format` 的接口发送
可能导致 HTTP 400 的字段；返回仍必须通过相同的八键 shape、非空且有依据的标题/
简介、实体、语言、长度和草稿质量检查。所有模式禁用 tools/function calling，且
不会因失败切换供应商、输入模式或本地/远程执行位置。

OpenAI-compatible 图片请求只发送标准的 `text` 与 `image_url.url`（data URL）部分。
不会发送可选的 `image_url.detail`，也不会显式发送默认值 `n=1`；这两个字段并非所有
兼容层都承诺接收。图片在此之前仍由 PicForLater 本地缩放、重编码并移除元数据，
输出数量仍由本地严格 parser 和 `max_tokens` 上限共同约束。

## 2026-08-01 官方契约复核结论

- 腾讯云已公告旧混元平台迁往 TokenHub，`hunyuan-turbos-latest` 已在旧模型下线
  清单中；preset 已改为广州 TokenHub 的 `hy3-preview`，并使用其文档声明的 Chat
  Completions、JSON Schema 和 `reasoning_effort`。旧 endpoint、旧默认 model、旧验证
  与同意不会沿用。新加坡地域账户需使用“自定义接口”填写官方国际 endpoint，固定
  preset 不会自动跨地域路由。
- 火山方舟将 `doubao-seed-2-0-lite-260215` 声明为支持文字、图片、视频等输入的
  多模态模型；preset 现允许 `API · 图片`，仍只发送一张去元数据的受限分析副本。
- Together 的 `Qwen/Qwen3.5-9B` 官方模型页和结构化视觉示例均声明图片输入；preset
  现允许 `API · 图片`，并保留其官方示例使用的 JSON Schema 与
  `reasoning.enabled=false`。
- OpenAI、Anthropic、Gemini、xAI、DeepSeek、Qwen、GLM、百度千帆、MiniMax、
  OpenRouter、Groq、Ollama 与 vLLM 的当前 preset endpoint、鉴权形状和声明能力与
  本轮查阅的官方文档一致。Kimi 的 `kimi-k2.5` 仍在官方模型列表且声明图片输入；
  官方当前示例多使用 `kimi-k2.6`，因此保留用户可编辑 model ID，不强制迁移仍可用
  的 2.5 配置。
- Perplexity Sonar 的当前 endpoint 为 `/v1/sonar`，支持 `disable_search=true` 与
  JSON Schema；PicForLater 显式关闭搜索，避免把当前图片分析扩展成未获同意的联网
  检索。

本轮协议结论以供应商官方文档为依据，包括：

- [OpenAI 图片输入](https://developers.openai.com/api/docs/guides/images-vision)、
  [结构化输出](https://developers.openai.com/api/docs/guides/structured-outputs)；
- [Anthropic Messages](https://platform.claude.com/docs/en/build-with-claude/working-with-messages)、
  [结构化输出](https://platform.claude.com/docs/en/build-with-claude/structured-outputs)；
- [Gemini OpenAI 兼容层](https://ai.google.dev/gemini-api/docs/openai)、
  [Gemini 3.5 Flash](https://ai.google.dev/gemini-api/docs/models/gemini-3.5-flash)；
- [腾讯云旧平台迁移公告](https://cloud.tencent.com/document/product/1729/131925)、
  [TokenHub Chat API](https://cloud.tencent.com/document/product/1823/130078)；
- [豆包 Seed 2.0](https://www.volcengine.com/docs/82379/1795150)、
  [百炼 Chat Completions](https://help.aliyun.com/zh/model-studio/qwen-api-via-openai-chat-completions)、
  [MiniMax 文本生成](https://platform.minimaxi.com/docs/guides/text-generation)；
- [OpenRouter 图片输入](https://openrouter.ai/docs/guides/overview/multimodal/image-understanding)、
  [Groq Vision](https://console.groq.com/docs/vision)、
  [Together 视觉结构化提取](https://docs.together.ai/docs/inference/vision/structured-extraction)；
- [Ollama OpenAI 兼容层](https://docs.ollama.com/api/openai-compatibility)、
  [vLLM 多模态输入](https://docs.vllm.ai/en/stable/features/multimodal_inputs/)。

## 政策与价格链接

设置页使用以下供应商资源。PicForLater 只显示“政策处理取决于供应商、套餐、地区
和账户控制”的保守声明，不把链接存在等同于 zero retention、禁训练、数据地区或
可删除保证。

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

loopback 只表示网络目标在本机；服务进程、模型、日志和保留行为仍由用户运行的
Ollama/vLLM 实例控制。自定义接口的所有者、政策、证书、兼容性和价格无法由本
项目预先核验。

## 验证等级与已知边界

- 全部 preset：生产 transport 的确定性 fake handler、真实 loopback HTTP 集成、
  严格 parser、最大响应体、鉴权脱敏、redirect/SSRF 与无图片文字模式测试。
- DeepSeek `RemoteOcrText`：使用专用测试 key 和合成 OCR 的显式真实 contract。
  2026-08-01 的三次测量通过；随后一次复测得到 HTTP 200 但空标题，暴露了提示中
  “空骨架”会被模型照抄的问题。该骨架已删除，连接测试现改为运行完整 parser；
  受审批通道中断影响，修正后的真实复测尚未完成，不能宣称所有真实调用必过。
- 阿里云百炼 / Qwen `RemoteVision`：2026-08-01 使用专用测试 key、
  `qwen3-vl-flash-2026-01-22` 和内置 640×960 授权猫图复测。保留
  `response_format=json_object` 和 `enable_thinking=false`，同时省略可选的
  `detail` 与默认的 `n=1`，得到 HTTP 200，约 3.16 秒返回全部八个结构化根字段。
  原内置 1×1 PNG 对同一
  model 返回 HTTP 400，因此故障不是 key、model ID、`detail` 或 JSON Object。
  生产 parser 复测又暴露 JSON Object 提示未写明数组数量/长度上限；补齐后
  真实响应仍返回 4 条 `visualFacts`（上限 3，最长单项 47 字符）。现仅对
  “恰好多一条低风险视觉事实”保留前三条并写警告；更大溢出和其他字段仍
  严格拒绝。最终生产载荷形状的真实 contract 与回归测试均已通过。
- 其他云 preset：本轮只做官方契约核对和 fake HTTP，不使用未经提供的真实 key，
  因而不标记为真实 API 合格。用户输入 model 后仍须通过无用户内容的合成测试。
- 价格估算只在供应商返回可靠 usage 且定价已核验时成立；聚合平台还可能因上游、
  地区、缓存和套餐不同而变化。
