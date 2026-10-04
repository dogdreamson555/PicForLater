# ADR 0011：RemoteVision 清洗图片载荷与显式跳过阶段

- 状态：Accepted
- 日期：2026-07-31

## 背景

[ADR 0008](0008-local-default-remote-boundaries-and-explicit-provenance.md)–[ADR 0010](0010-remote-ocr-text-provider-and-payload-boundary.md) 已定义远程边界、profile/凭据/同意快照和文字模式。本阶段实现 `RemoteApi + DirectImage`，跳过不需要的本地 OCR、确定性 OCR 实体提取与 Qwen，同时复用现有任务租约、checkpoint、parser、候选确认、revision guard 和原子完成路径。

空字符串无法区分真正的空 OCR 与主动跳过，也可能让远程文字被误认为本地事实或定位证据，因此跳过状态必须显式保存。

## 决策

1. `AnalysisProvenance` 末尾新增有默认值的 `AnalysisStageOutcome`，当前为 `Completed` 或 `SkippedByRemoteDirectImage`；旧构造器和缺少该字段的 JSON 仍解释为 `Completed`。
2. schema 10 只向 `AnalysisStageResults` 增加非空、默认 0 的 `StageOutcome`，旧行保持 `Completed`，不重建表或删除 OCR 历史。新 `RemoteVision` 任务的 OCR 与确定性实体 checkpoint 记录 `ExecutionLocation=RemoteApi`、`RemoteInputMode=DirectImage`、`SkippedByRemoteDirectImage`、空事实载荷及 `analysis.skipped-by-remote-direct-image` 警告。
3. Worker 只按快照中的 `ExecutionBackend` 与 `RemoteInputMode` 选路。`DirectImage` 不调用 `IOcrProvider`、`IEntityExtractor`、`ConditionalAnalysisRouter` 或本地 `IVisionCaptionProvider`，而直接调用选定的远程图片 Provider；禁止按 Provider ID 或供应商名称推断行为。
4. `WindowsImageContentProcessor` 通过窄接口 `IRemoteVisionImagePreprocessor` 从不可变原图解码，尊重方向、转换为 sRGB，并只从像素和固定 96 DPI 重编码 PNG。优先保留分辨率，超过 1600 万像素才等比缩小；若编码结果超过限制则继续按实际 PNG 大小有界缩小。副本不复制 EXIF/XMP、文件名或路径，只在内存短暂持有，并同时受 profile `MaxImageBytes` 与 10 MiB Base64 data URI 上限约束；Provider 调用结束即清理。
5. Provider 在读取任何图片前通过 `RemoteApiRequestAuthorizer` 重新检查当前 profile 已启用、已验证、能力匹配、同意有效且载荷范围与任务快照一致，并检查凭据。发送前 transport 再核验一次以覆盖清洗期间的撤销竞态。请求还必须带有显式 skipped OCR provenance；只发送清洗副本、已披露的参考时间及时区、输出语言策略和固定 prompt/schema，不发送 OCR、bbox、类别、原文件名、路径、哈希、内部 ID、EXIF 或其他资料库上下文。
6. 两种远程适配器共用 ADR 0010 定义的受控 HTTP transport 与严格 JSON 契约：无工具调用、禁用 redirect/cookie、有界响应、临时读取凭据并使用既有重试/错误分类。供应商 DTO 不进入 Core、Worker checkpoint 或 SQLite。
7. 响应转换为现有 `VisionStructuredResult` 与 `ExtractiveContentDraft`，远程类别为空。图片模型实体标记 `Source=Model`、`BoundingBox=null` 和 `RemoteVisionNoLocalOcrEvidence`；parser 不用主动跳过的空 OCR 背书数字事实，候选合并也不升级其证据。
8. 成功结果继续通过 `ReminderCandidateMerger` 和原完成事务，只生成待确认候选，不直接创建提醒或通知；`CompleteAsync` 的 revision 条件更新保护分析期间的用户编辑。
9. 图片调用失败时保留原图和 skipped checkpoint，任务停在 Vision/`NeedsAttention`。不生成空抽取草稿、不调用本地 OCR/Qwen、不换供应商或文字模式；后续只能明确重试 API 或新建本地重新分析。

## 影响

- `Local` 和 `RemoteOcrText` 的执行及默认值不变；升级用户和旧 stage 行仍是 `Local`/`Completed`。
- RemoteVision 复用现有 Provider seam，不新增导入、图片、结果、候选、提醒或删除数据库。schema 从 9 增量升级到 10，继续在升级前验证备份并在失败时保留原库。
- 图片清洗会产生最多 1600 万像素的解码/编码和少量受限重编码；副本只在内存中存在，不把原图字节、base64 或完整请求写入持久存储或日志。
