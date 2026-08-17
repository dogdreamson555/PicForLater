# ADR 0010：RemoteOcrText Provider 与无图片载荷边界

- 状态：Accepted
- 日期：2026-07-31

## 背景

ADR 0008、0009 已固定本地默认、两种远程隐私边界、版本化同意、非秘密
任务快照和失败不跨模式回退。本阶段只实现 `RemoteApi + LocalOcrText`，必须
复用已有 OCR、确定性实体、异步增强 Provider seam、结构化 parser、
候选合并、checkpoint、revision/人工修改保护和原子完成路径。

## 决策

1. `OpenAiCompatibleRemoteOcrTextProvider` 实现现有
   `IVisionCaptionProvider`。它接受现有 `VisionAnalysisRequest` 以避免复制
   Worker 主干，但不得调用其中的 `OpenImageAsync`。
2. 远程请求只包含：
   - 有界的 OCR 纯文本；
   - OCR 的 BCP-47 语言标签；
   - `SameAsContent` 输出语言策略；
   - 本次任务披露的参考 UTC 时间和时区；
   - 快照固定的 model、prompt version、schema 和输出 token 上限。
   请求不包含图片、缩略图、base64、文件名、路径、内容哈希、内部 ID、
   bbox、类别名称/ID或其他资料库内容。
3. 首个适配器采用 OpenAI-compatible chat-completions JSON 契约，并把
   `RemoteApiProfileSnapshot.BaseUri` 视为经过审核的完整 POST endpoint，
   不按 `ProviderId` 或 `remote.xxx` 字符串分派。当前不开放自定义 endpoint
   UI，也不宣称兼容任意 API。
4. Worker 对 `RemoteOcrText` 始终先执行完整本地 OCR 和确定性实体阶段，
   随后调用远程 Provider；本地 `OcrOnly/Balanced/AlwaysEnhance` 与条件路由
   不得跳过这次调用，本地 Qwen Provider 也不会被查询。
5. 远程响应继续使用 `QwenStructuredOutputParser` 的严格版本化 JSON
   schema 和证据校验，再转换为现有 `VisionStructuredResult`。适配器向
   parser 提供空类别上下文并强制清空 `categoryIds` 与 `visualFacts`；
   远程文字模式只生成标题、简介和有 OCR 证据的实体/提醒候选。
6. 远程候选继续与确定性 OCR 候选经过同一
   `ReminderCandidateMerger`，并通过原有完成事务写入。远程输出仍是
   `ModelSuggested`，不会直接创建提醒或覆盖用户字段。
7. OCR 超过 profile 限制时，先在本地选择有日期/时间/地点迹象的原文行，
   再保留头尾原文片段；最终 OCR 文本严格受 `MaxTextChars` 约束，并在结果
   写入 `remote.ocr-text-compacted` 警告。禁止静默截断。
8. API 失败时不调用本地视觉模型、不切换供应商或输入模式。Worker 用本地
   OCR 生成抽取式标题/简介并保留确定性候选，在同一原子完成事务中写入
   composition checkpoint，同时把任务和图片标记为 `NeedsAttention` 并保存
   脱敏错误码，供用户明确重试。
9. `AnalysisProvenance` 增加可空 `RemoteInputMode`；迁移 9 只给
   `AnalysisStageResults` 增加同名可空列。旧 JSON 和旧行继续解析为
   `NULL`/本地语义；远程 Vision 与 TextComposition 阶段明确记录
   `ExecutionLocation=RemoteApi`、`RemoteInputMode=LocalOcrText`。
10. HTTP transport 禁用 redirect 和 cookie、每 host 最大并发为 1、使用
    Credential Locker 临时取得 Bearer secret，并限制响应体。401/403 不重试；
    429 至多使用同一非内容幂等键自动尝试两次并遵守有界 `Retry-After`。
    当前兼容契约没有声明供应商一定支持幂等，因此 5xx、超时或结果不确定的
    网络错误不盲目重发，而是进入可由用户明确重试的失败状态。
## 影响

- `RemoteOcrText` 已可沿现有任务流水线完成分析；后续 `RemoteVision`
  的实现由 ADR 0011 增量补充，不改变本文的文字载荷边界。
- 不新增数据库、导入、结果、提醒或删除流水线，不新增第三方 SDK。
- schema 从 8 增量升级到 9；迁移前仍由现有初始化器创建可恢复备份。
