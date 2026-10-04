# ADR 0012：设置子页、多 Provider 协议与受限自定义 endpoint

- 状态：Accepted
- 日期：2026-08-01

## 背景

[ADR 0008](0008-local-default-remote-boundaries-and-explicit-provenance.md)–[ADR 0011](0011-remote-vision-sanitized-image-and-skipped-stages.md) 已规定本地默认、远程载荷、凭据/同意快照和共享分析流水线。本阶段让用户能在设置中查看并控制这些边界，同时支持有明确契约的多 Provider 与受限自定义 endpoint。

## 决策

1. 顶层 `NavigationView` 保留单一“设置”入口。`SettingsPage` 内部使用 `NavigationView + Frame` 展示“概览”“本地分析”“API 分析”；迁移本地分析和主题控件时保留原行为、资源键及可行的 `AutomationId`。
2. API 目录分为国际官方、中国官方、聚合/高速、本机/私有化四类，并提供独立自定义接口。逐项 preset、endpoint、模型、协议、认证和核验信息维护在 [`remote-api-providers.md`](../remote-api-providers.md)。Preset 只开放已核验的输入能力；未确认统一的图片和结构化输出契约时默认只开放 OCR 文本，不能凭品牌名开放图片上传。
3. 迁移 11 只向 `RemoteApiProfiles` 增加协议、鉴权、结构化输出、endpoint 信任、API 版本和请求约束字段。profile 与任务快照新增字段带默认值；旧数据库/JSON 保持 OpenAI-compatible、Bearer、JSON Schema、固定 HTTPS 的既有含义，旧任务和升级用户仍为 `Local`。后续迁移只能追加带默认值的结构化输出、思考档位与 wire format 字段，不改写已发布迁移。
4. Transport 仅按显式 `RemoteApiProtocol`、`RemoteApiAuthenticationKind`、`RemoteStructuredOutputMode`、`RemoteEndpointTrustMode` 和请求策略组装请求；Worker、页面与 transport 都不得按供应商 ID 分派。协议、鉴权和输出格式差异必须由 profile 契约声明。

   首期 Anthropic profile 采用 Messages、`x-api-key`、版本头、原生 base64 图片块和 `output_config.format`；其余首期 preset 声明 OpenAI-compatible 契约。后续 preset 的协议和鉴权按其显式 profile 定义。
5. OpenRouter profile 显式设置 `allow_fallbacks=false`、`require_parameters=true`；Perplexity Sonar 显式设置 `disable_search=true`，防止改路由到其他上游或隐式外部检索。这些是快照请求策略，不是品牌字符串分支。
6. 自定义接口仅支持产品已实现的两种协议、三种鉴权和三种结构化输出，不宣传任意 API 兼容。公共 endpoint 必须为无 userinfo/query/fragment 的 HTTPS；每次连接重新解析 DNS 并拒绝 loopback、RFC1918、CGNAT、link-local、ULA、multicast 等非公共地址。Loopback 模式仅接受 `localhost`、`127.0.0.1`、`::1` 的 HTTP/HTTPS，不开放任意局域网目标。始终禁用 redirect 与 Cookie，并只向最终已验证 endpoint 添加凭据，以防 SSRF、重定向和鉴权 host 漂移。
7. API key 只经 `PasswordBox` 交给凭据服务并立即清空；SQLite、profile、任务快照、checkpoint、日志和错误只保存 credential reference。无鉴权 loopback profile 不读取或发送凭据。替换/删除凭据，或更改 endpoint、模型、协议、鉴权、schema、网络边界时，先切回 `Local`，并使验证与同意失效。
8. 连接测试只发送固定合成文字或内置示例图片，不读取用户图片、OCR、文件名、路径、哈希、ID、EXIF 或资料库内容。测试使用当前 model、协议、输入模式和输出契约，并提示可能计费。
9. 首次启用、切换 provider/模式或改变同意范围时，依次要求 profile 已验证、必要凭据存在、当前模式的合成测试成功、用户在 `ContentDialog` 明示同意；随后才保存版本化同意并选择远程。设置只影响新任务；失败不会自动切换 provider、模式或本地/远程执行。
10. 两种远程模式复用既有 Worker、checkpoint、结构化 parser、候选合并、提醒确认、revision guard 和原子完成路径。API 子页只配置 profile，不创建 HTTP DTO，也不建立第二套分析、提醒或结果数据库。
11. 模型思考规模、输出 token 和超时放入折叠高级区；普通 preset 仍只需 key 与 model。高级值依据显式能力和 wire format 生成请求，变化时使验证与同意失效。

## 资料核验与可变能力

供应商 endpoint、鉴权、协议、价格、政策和模型能力会变化。Preset 的 model 可编辑，启用前必须通过设置页提供的官方链接与连接测试；政策或固定字段更新会使既有同意失效。详细核验记录见 [`remote-api-providers.md`](../remote-api-providers.md)。

## 影响

- 新安装和升级用户默认仍为本地；创建 profile 不会选中或发送它们。
- 原图、本地 OCR/Qwen、导入、搜索、分类、提醒、回收站和主完成事务保持原有流水线。
- 自定义服务的兼容范围明确且有限，暂不支持 loopback 以外的私网 endpoint。未来若支持企业内网、自定义 CA、代理、其他协议或特殊鉴权，须单独威胁建模并记录 ADR。
