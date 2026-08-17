# ADR 0012：设置子页、多 Provider 协议与受限自定义 endpoint

- 状态：Accepted
- 日期：2026-08-01

## 背景

ADR 0008–0011 已固定本地默认、`RemoteOcrText`/`RemoteVision` 两种载荷、凭据引用、
版本化同意、任务快照及失败不跨模式回退。本阶段需要让用户在设置界面中看见并
控制这些边界，同时保留已有本地模型控件和主导航。

远程选择不能等同于 OpenAI。目标供应商包括官方 API、多模型平台、本机/私有化
框架，并保留自定义兼容接口。它们至少存在 OpenAI Chat Completions、Anthropic
Messages、Bearer、`x-api-key`、无鉴权、JSON Schema、JSON Object 等真实差异；
这些差异不能通过 `ProviderId == ...` 或 `remote.xxx` 名称特判表达。

## 决策

1. 顶层 `NavigationView` 继续只有一个系统“设置”入口。`SettingsPage` 使用内部
   `NavigationView + Frame`，包含“概览”“本地分析”“API 分析”。原本地分析和
   主题控件迁移到对应子页，原行为、资源键与可行的 `AutomationId` 保持不变。
2. API 目录按用户可理解的四类展示，并提供独立“自定义接口”：
   - 国际官方：OpenAI、Anthropic/Claude、Google Gemini、xAI/Grok、Perplexity Sonar；
   - 中国官方：DeepSeek、Kimi、腾讯混元、火山/豆包、阿里百炼/Qwen、智谱/GLM、
     百度千帆/文心、MiniMax；
   - 聚合/高速：SiliconFlow、OpenRouter、Groq、Together AI；
   - 本机/私有化：Ollama、vLLM。
   preset 只声明其已核对的输入能力。未确认统一图片+结构化输出契约的默认只开放
   OCR 文本；用户不能仅凭品牌名越过图片上传边界。
3. 迁移 11 只向 `RemoteApiProfiles` 增加协议、鉴权、结构化输出、endpoint 信任、
   API 版本和请求约束字段。`RemoteApiProfile` 与任务 snapshot 使用带默认值的
   init-only 字段；旧数据库和旧 JSON 保持 OpenAI-compatible/Bearer/JSON Schema/
   固定 HTTPS 的既有含义，旧任务及升级用户仍为 `Local`。
   后续迁移在不改写既有迁移的前提下，追加结构化输出和带默认值的思考档位/
   wire format 字段。
4. transport 仅根据显式 `RemoteApiProtocol`、`RemoteApiAuthenticationKind`、
   `RemoteStructuredOutputMode`、`RemoteEndpointTrustMode` 和请求策略组装载荷；
   Worker、页面、transport 均不读取供应商 ID 来分派。Anthropic Messages 使用
   `x-api-key`、版本头、原生 base64 image source 和 `output_config.format`；其余
   preset 使用声明的 OpenAI-compatible 契约。
5. OpenRouter profile 显式发送 `allow_fallbacks=false` 和
   `require_parameters=true`，避免请求失败后路由至另一上游。Perplexity Sonar 显式
   `disable_search=true`，防止本产品的整理请求隐式触发外部检索。这两项是 snapshot
   中的请求策略，不是品牌字符串分支。
6. 自定义接口只支持本产品已实现的两种协议、三种鉴权与三种结构化输出；不宣传
   “任意 API 兼容”。公共模式必须是无 userinfo/query/fragment 的 HTTPS endpoint；
   DNS 每次连接重新解析并拒绝 loopback、RFC1918、CGNAT、link-local、ULA、multicast
   等非公共地址。loopback 模式只接受 `localhost`、`127.0.0.1`、`::1` 的 HTTP/HTTPS。
   不允许任意局域网 endpoint；重定向和 Cookie 始终关闭，避免 redirect/SSRF/鉴权
   host 漂移。凭据只加到最终已校验的请求。
7. API key 只通过 `PasswordBox` 进入凭据服务并立即清空；SQLite、profile、任务
   snapshot、checkpoint、日志和错误只接触凭据引用。无鉴权 loopback profile 不读取
   或发送凭据。替换/删除凭据、修改 endpoint/模型/协议/鉴权/schema/网络边界都会先
   切回 `Local`、使验证及同意失效。
8. 连接测试只发送固定合成文字或内置示例图片，不读取用户图片、OCR、文件名、
   路径、哈希、ID、EXIF 或资料库内容。测试使用当前 model、协议、输入模式和输出
   契约，并明确提示可能计费。
9. 首次启用、切换 provider/模式或同意范围变化必须依次满足：profile 已验证、必要
   凭据存在、当前模式的合成测试成功、用户在 `ContentDialog` 勾选明示同意。随后
   才写入版本化同意并选择远程。设置只影响新任务；失败不会改变 provider、模式，
   也不会自动切换本地/远程实现。
10. 两种远程模式继续复用既有 Worker、checkpoint、结构化 parser、候选合并、提醒
    确认、revision guard 和原子完成路径。API 子页只配置 profile，不创建 HTTP DTO，
    不建立第二套分析/提醒/结果数据库。
11. 模型级思考规模、输出 token 与超时放入折叠高级区；普通 preset 仍只需 key 和
    model。高级值由显式能力/wire format 生成请求，变化时使验证和同意失效。

## 资料核验与可变能力

供应商的 endpoint、鉴权、协议、价格、政策和模型能力可能变化。preset 的 model
保持可编辑，启用前必须以应用提供的官方链接与连接测试为准；政策或固定字段更新
会使既有同意失效。

## 影响

- 新安装和升级用户默认仍为本地；创建 20 个非秘密 profile 不会选中或发送它们。
- 原图、本地 OCR/Qwen、导入、搜索、分类、提醒、回收站和主完成事务不变。
- 自定义服务获得明确但有限的兼容面；私网非 loopback endpoint 暂不支持。若未来
  需要企业内网、自定义 CA、代理、其他协议或供应商特有鉴权，必须单独威胁建模与 ADR。
