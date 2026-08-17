# ADR 0011：RemoteVision 清洗图片载荷与显式跳过阶段

- 状态：Accepted
- 日期：2026-07-31

## 背景

ADR 0008–0010 已固定本地默认、两种远程隐私边界、远程 profile/凭据/同意
快照和 `RemoteOcrText` 的实现。本阶段实现
`RemoteApi + DirectImage`（`RemoteVision`），必须跳过不需要的本地 OCR、
确定性 OCR 实体提取和 Qwen，同时继续使用现有任务租约、checkpoint、
结构化结果、候选确认、revision guard 和原子完成路径。

空字符串不能表示“成功完成 OCR”：它会混淆真正的空 OCR 结果与主动跨过事实层，
也可能让后续代码为远程模型文字伪造本地 OCR 可信度或定位证据。

## 决策

1. `AnalysisProvenance` 追加带默认值的
   `AnalysisStageOutcome`，当前值为 `Completed` 和
   `SkippedByRemoteDirectImage`。字段位于 positional record 末尾，旧构造器和
   旧 JSON 缺少它时继续解析为 `Completed`。
2. schema 10 仅向既有 `AnalysisStageResults` 追加非空、默认值为 0 的
   `StageOutcome` 列。旧行保持 `Completed`；不重建表、不迁移或删除已有 OCR
   历史。每个 `RemoteVision` 新任务的 OCR 与确定性实体 checkpoint 写入：
   - `ExecutionLocation=RemoteApi`；
   - `RemoteInputMode=DirectImage`；
   - `StageOutcome=SkippedByRemoteDirectImage`；
   - 空事实载荷和明确的 `analysis.skipped-by-remote-direct-image` 警告。
3. Worker 根据快照中的显式 `ExecutionBackend` 与 `RemoteInputMode` 选路。
   `DirectImage` 不调用 `IOcrProvider`、`IEntityExtractor`、
   `ConditionalAnalysisRouter` 或本地 `IVisionCaptionProvider`，而是无条件调用
   已选择的远程图片 Provider。不得根据 `ProviderId`、供应商名或
   `remote.xxx` 前缀推断行为。
4. `WindowsImageContentProcessor` 实现窄接口
   `IRemoteVisionImagePreprocessor`：从不可变原图解码像素、尊重方向、颜色
   管理到 sRGB，并重新编码为 PNG。远程副本优先保留原分辨率；超过 1600 万
   像素时才等比例缩小，重新编码后若仍超过载荷上限则按实际 PNG 大小继续等比
   收敛。编码只使用像素与固定 96 DPI，不复制 EXIF/XMP、文件名或路径；输出在
   内存中一次性持有，并同时受 profile `MaxImageBytes` 和 10 MiB Base64 data URI
   折算上限约束，释放 Provider 调用后立即清理。
5. `OpenAiCompatibleRemoteVisionProvider` 在任何图片读取前通过
   `RemoteApiRequestAuthorizer` 重新读取当前 profile，检查仍启用、已验证、
   支持本模式、同意仍有效且载荷范围与任务快照完全匹配，再检查凭据；发送前
   transport 再检查一次以覆盖清洗期间的撤销竞态。它拒绝没有显式 skipped
   OCR provenance 的请求，只把上述清洗副本编码为
   `data:image/...;base64`，附带已披露的参考 UTC 时间、时区、输出语言策略和
   固定 prompt/schema；不发送 OCR、bbox、类别、原文件名、路径、哈希、
   内部 ID、EXIF 或资料库上下文。
6. 两种远程适配器共用受控的 OpenAI-compatible HTTP transport：固定严格
   JSON schema、无 tool/function calling、禁用 redirect/cookie、有界响应、
   凭据临时读取和既有重试/错误分类规则。供应商 DTO 不进入 Core、Worker
   checkpoint 或 SQLite。
7. 返回值仍转换为现有 `VisionStructuredResult` 与
   `ExtractiveContentDraft`，远程类别建议强制为空。图片模型实体保持
   `Source=Model`、`BoundingBox=null`，并写入
   `RemoteVisionNoLocalOcrEvidence`；parser 不会拿主动跳过的空 OCR 去做
   数字事实背书，候选合并也不得覆盖这条披露。
8. 成功结果继续经过 `ReminderCandidateMerger` 和原有完成事务。它只产生
   待确认候选，不直接写 `Reminder` 或调度通知；`CompleteAsync` 的 revision
   条件更新继续保护分析期间发生的用户编辑。
9. 远程图片调用失败时保留原图和已写入的 skipped checkpoint，任务停在
   Vision/`NeedsAttention`，不生成抽取式空草稿，不调用本地 OCR/Qwen、
   不换供应商、不改成文字模式。用户之后只能显式重试 API 或新建本地重新分析。
## 影响

- `Local` 与 `RemoteOcrText` 的执行和默认值不变；升级用户和旧 stage 行仍是
  `Local`/`Completed`。
- 没有新增导入、图片、结果、候选、提醒或删除数据库；RemoteVision 是现有
  Provider seam 上的另一个适配器。
- 图片清洗会额外产生一次最多 1600 万像素的解码/PNG 编码；若 PNG 超过请求
  字节上限则进行少量有界重编码。副本只保存在内存中，不写临时文件，不把原图
  字节、base64 或完整请求写入持久化存储与日志。
- schema 从 9 增量升级到 10；初始化器继续在升级前创建并验证备份，迁移失败
  时保留原数据库。
