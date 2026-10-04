# ADR 0008：本地默认、远程数据边界与显式分析 provenance

- 状态：Accepted
- 日期：2026-07-31

> 远程 profile、凭据与组合任务快照由 [ADR 0009](0009-remote-profiles-credentials-and-combined-job-snapshots.md) 增量补充；本 ADR 的隐私边界和失败语义继续有效。

## 背景

导入、受管图片存储、SQLite、持久化 `AnalysisJob`、阶段 checkpoint、`AnalysisWorker`、候选合并、revision/人工修改保护、提醒和回收站已组成主流水线。第三方 API 只能作为可选 Provider 接入，复用该流水线，不能建立第二套导入、结果、提醒或删除架构。`ProviderId` 是审计和适配器选择用的不透明标识，不能决定输出语义。

## 决策

### 1. 执行目标与默认值

新安装、升级用户和缺少未来可选字段的旧任务都以 `Local` 为默认执行目标。`AnalysisMode.OcrOnly/Balanced/AlwaysEnhance` 仅描述本地性能策略，不表达隐私边界。

任务快照正交保存 `AnalysisExecutionBackend { Local, RemoteApi }` 与远程时的 `RemoteInputMode { LocalOcrText, DirectImage }`。新增快照字段须有本地默认值且不改变现有 positional 参数；旧 JSON、数据库默认值和旧设置都解析为本地。设置变化只影响新任务及用户明确发起的重新分析。

### 2. 远程载荷

- `RemoteOcrText` 先完成本地 OCR 和确定性实体提取，只发送生成草稿和提醒候选所需的 OCR 纯文本、语言、输出语言策略及已披露的参考日期和时区；不读取或发送图片、缩略图、路径、原文件名、哈希、内部 ID、EXIF 或资料库上下文。
- `RemoteVision` 只发送从不可变原图解码、重新编码并移除 EXIF/XMP 的一次性分析副本，受像素和字节上限约束；默认不附带 OCR、路径、原文件名、哈希、内部 ID 或资料库上下文，调用结束后清理副本。跳过 OCR 必须记录 `SkippedByRemoteDirectImage`，不能伪装为空 OCR 成功或生成 bbox。

两种模式都只返回现有结构化草稿和候选；远程类别建议为空。模型不得直接创建提醒、安排通知、调用工具、打开 URL 或发起二次网络请求。

### 3. 凭据、同意与发送前检查

API key/token 只存于 Windows Credential Locker 或等价的用户级 OS 秘密存储。SQLite、普通设置、任务快照、checkpoint 和日志只保存 credential reference，不保存密钥、Authorization、完整请求/响应、图片或 base64。

首次启用远程分析前必须取得版本化同意，至少说明供应商、endpoint host、model、发送文字或图片、自动处理范围、第三方保留/训练声明及核验日期、可能费用和关闭方式。输入类型、供应商/endpoint、字段范围或政策声明变化会使旧同意失效。

每次发送前重新核验任务明确选择远程、profile 已验证且启用、能力匹配、凭据存在、同意仍有效、任务未撤销。网络可用或已有密钥本身不构成发送授权。

### 4. 失败不跨隐私边界回退

本地失败不得上传；远程失败不得静默换供应商、把 OCR 文字升级为图片、改用本地模型，或自动重发结果不确定且可能计费的请求。文字模式失败保留已提交 OCR、确定性候选和抽取式草稿；图片模式失败保留原图及已有结果，等待用户明确重试 API 或改用本地重新分析。取消只能阻止尚未发送的数据和后续提交，不能召回第三方已收到的数据或费用。

### 5. 主流水线与历史数据

远程 Provider 复用任务租约、stage checkpoint、结构化 parser/draft、`ReminderCandidateMerger`、revision/人工字段保护和原子完成路径。不得重写 `AnalysisWorker` 主干、增加平行数据库，或让供应商 DTO/错误码穿透到 Core、App 或 SQLite。profile 与 Provider 的具体契约由 ADR 0009–0012 补充。

迁移 7 只给 `AnalysisStageResults` 增加显式 stage provenance，不改写 `AnalysisJobs.ModelProfileSnapshotJson`；迁移前创建可验证备份，失败时回滚并保留原库。旧 stage 行保守回填为 `ExecutionLocation=Local`；OCR、确定性实体、无模型路由和抽取式组合分别标记为 `OcrFacts`、`DeterministicEntityCandidates`、`RoutingDecision`、`ExtractiveDraft`，带模型身份的旧结果标记为 `ModelGeneratedDraft`。

### 6. 显式输出语义

`AnalysisProvenance` 记录 `ExecutionLocation`（`Local`/`RemoteApi`）、`OutputKind`（`OcrFacts`、`DeterministicEntityCandidates`、`RoutingDecision`、`ModelGeneratedDraft`、`ExtractiveDraft`，以及仅供旧/未知数据使用的 `Unspecified`）和原有 Provider/model/hash/schema 字段。业务行为按这些显式字段决定；`ProviderId` 仅用于审计、显示、适配器选择和能力 profile 标识。禁止按 ID 前缀或具体供应商推断草稿来源、上传范围、候选资格或失败回退。

## 影响

- 本地行为和默认执行目标不变；本 ADR 本身不启用网络、不增加凭据存储，也不改变上传范围。
- provenance 不依赖供应商字符串来表达执行位置和输出性质。未声明 `OutputKind` 的新 Provider 按 `Unspecified` 处理，不获得模型建议语义；Provider 必须显式声明输出类型。
- 迁移 7 是向后兼容的增量迁移；远程快照字段、凭据服务和 API profile 由后续纵向切片实现。
