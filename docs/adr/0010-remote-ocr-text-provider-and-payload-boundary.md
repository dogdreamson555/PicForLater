# ADR 0010：RemoteOcrText Provider 与无图片载荷边界

- 状态：Accepted
- 日期：2026-07-31

## 背景

[ADR 0008](0008-local-default-remote-boundaries-and-explicit-provenance.md) 与 [ADR 0009](0009-remote-profiles-credentials-and-combined-job-snapshots.md) 已确定本地默认、远程载荷边界、同意与任务快照。本阶段只实现 `RemoteApi + LocalOcrText`，并复用现有 OCR、确定性实体、异步增强 Provider、结构化 parser、候选合并、checkpoint、revision/人工修改保护和原子完成路径。

## 决策

1. 首个适配器 `OpenAiCompatibleRemoteOcrTextProvider` 实现现有 `IVisionCaptionProvider`，接收 `VisionAnalysisRequest` 以复用 Worker 主干，但不得调用其中的 `OpenImageAsync`。
2. 请求仅含有界 OCR 纯文本、BCP-47 语言标签、`SameAsContent` 输出语言策略、已披露的参考 UTC 时间及时区，以及任务快照固定的 model、prompt version、schema 和输出 token 上限。不含图片、缩略图、base64、文件名、路径、哈希、内部 ID、bbox、类别或其他资料库内容。
3. 首个适配器采用 OpenAI-compatible Chat Completions JSON 契约；`RemoteApiProfileSnapshot.BaseUri` 是经审核的完整 POST endpoint。本阶段不开放自定义 endpoint UI，也不宣称兼容任意 API；请求契约不得按 `ProviderId` 或其前缀推断。
4. `RemoteOcrText` 始终先运行完整本地 OCR 和确定性实体阶段，再调用远程 Provider；本地 `OcrOnly/Balanced/AlwaysEnhance` 与条件路由都不能跳过远程调用，也不查询本地 Qwen Provider。
5. 响应通过 `QwenStructuredOutputParser` 的严格版本化 JSON schema 和证据校验，再转换为现有 `VisionStructuredResult`。适配器提供空类别上下文并清空 `categoryIds`、`visualFacts`；文字模式只生成标题、简介及有 OCR 证据的实体/提醒候选。
6. 远程候选与确定性 OCR 候选继续经过同一 `ReminderCandidateMerger` 和原完成事务。远程输出标记为 `ModelSuggested`，只产生待确认候选，不创建提醒或覆盖用户字段。
7. OCR 超出 profile 限制时，优先选取带日期、时间或地点迹象的原文行，再保留首尾片段；结果严格受 `MaxTextChars` 限制，并写入 `remote.ocr-text-compacted` 警告，不静默截断。
8. API 失败时保留本地 OCR、确定性候选和抽取式草稿；同一原子完成事务写入 composition checkpoint，将任务与图片标为 `NeedsAttention`，并保存脱敏错误码以供用户明确重试。失败不调用本地视觉模型、不切换供应商或输入模式。
9. `AnalysisProvenance` 增加可空 `RemoteInputMode`，迁移 9 只给 `AnalysisStageResults` 增加同名可空列；旧 JSON/行仍解析为 `NULL` 和本地语义。远程 Vision 与 TextComposition 阶段明确记录 `ExecutionLocation=RemoteApi`、`RemoteInputMode=LocalOcrText`。
10. HTTP transport 禁用 redirect 和 cookie、每 host 并发上限为 1、凭据从 Credential Locker 临时读取，并限制响应体。401/403 不重试；429 最多自动尝试两次，沿用不含内容的幂等键并遵守有界 `Retry-After`。当前契约未保证供应商支持幂等，故 5xx、超时及结果不确定的网络错误不盲目重发，交由用户明确重试。

## 影响

- `RemoteOcrText` 沿现有任务流水线完成分析；`RemoteVision` 由 [ADR 0011](0011-remote-vision-sanitized-image-and-skipped-stages.md) 增量补充，设置与协议扩展见 [ADR 0012](0012-settings-navigation-and-multi-provider-api-consent.md)，两者都不改变本文的文字载荷边界。
- 不新增数据库、导入、结果、提醒或删除流水线，也不新增第三方 SDK。schema 从 8 增量升级到 9，迁移前由现有初始化器创建可恢复备份。
