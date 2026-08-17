# ADR 0008：本地默认、远程数据边界与显式分析 provenance

- 状态：Accepted
- 日期：2026-07-31

> 远程 profile、Credential Locker 与组合任务快照的实现细节由 ADR 0009
> 增量补充；本 ADR 的隐私边界与失败语义继续有效。

## 背景

现有导入、受管图片存储、SQLite、持久化 `AnalysisJob`、阶段 checkpoint、
`AnalysisWorker`、候选合并、revision/人工修改保护、提醒和回收站已经构成
产品主流水线。第三方 API 必须作为该流水线中的可选 Provider 增量接入，不能
建立第二套导入、结果、提醒或删除架构。

`ProviderId` 是用于审计和选择适配器的不透明标识，不能用来推断输出语义，
以免把供应商身份、执行位置和输出性质错误地耦合。

## 决策

### 1. 执行目标与默认值

新安装、升级用户和无法读取未来可选字段的旧任务都以 `Local` 为默认执行
目标。现有 `AnalysisMode.OcrOnly/Balanced/AlwaysEnhance` 只描述本地性能
策略，不承担隐私边界语义。

后续任务快照将正交保存
`AnalysisExecutionBackend { Local, RemoteApi }` 与远程时的
`RemoteInputMode { LocalOcrText, DirectImage }`。扩展现有
`ModelProfileSnapshot` 时使用带本地默认值的可选或 init-only 字段，不改变
现有 positional 参数；旧 JSON 缺少新字段、数据库默认值或旧设置都必须解析
为本地。设置变化只影响新任务及用户明确发起的重新分析。

### 2. 仅允许两种远程载荷

- `RemoteOcrText`：先运行现有完整本地 OCR 和确定性实体提取。请求只包含
  完成标题、简介和提醒候选所需的 OCR 纯文本、语言标签、期望输出语言，以及
  已披露的参考日期和时区。不得读取或发送图片、缩略图、路径、原文件名、
  哈希、内部 ID、EXIF 或资料库上下文。
- `RemoteVision`：只发送从不可变原图解码并重新编码、去除 EXIF/XMP、受像素
  和字节上限约束的一次性分析副本。默认不得附带本地 OCR、路径、原文件名、
  哈希、内部 ID 或资料库上下文；请求结束后清理临时副本。跳过本地 OCR 时
  写入明确的 `SkippedByRemoteDirectImage` stage outcome，不伪造空 OCR
  成功或 bbox。

两种模式都只能返回现有结构化草稿和候选类型；远程类别建议保持为空，模型
不得直接创建提醒、安排通知、调用工具、打开 URL 或发起二次网络请求。

### 3. 凭据、同意与发送前检查

API key/token 只存入 Windows Credential Locker 或等价的用户级 OS 秘密
存储。SQLite、普通设置、任务快照、checkpoint 和日志只保存 credential
reference，不保存密钥、Authorization、完整请求/响应、图片或 base64。

首次启用远程分析前必须展示并取得版本化同意，至少覆盖供应商、endpoint
host、model、发送 OCR 文字还是图片像素、自动处理范围、第三方保留/训练
声明及核验日期、可能费用和关闭方式。切换文字到图片、供应商/endpoint、
扩大字段或政策声明变化会使旧同意失效。

每次发送前都重新检查：任务快照明确为远程、Provider/profile 已验证且仍
启用、能力匹配、凭据存在、同意版本仍有效、任务未被撤销。仅存在网络或已
保存密钥不构成发送授权。

### 4. 失败不跨隐私边界回退

本地失败不得上传。远程失败不得静默换供应商、从 OCR 文字升级为图片、改用
本地模型或自动重发结果不确定且可能计费的请求。

`RemoteOcrText` 失败时保留已提交的本地 OCR、确定性候选和抽取式草稿；
`RemoteVision` 失败时保留原图和已有结果，进入可重试状态，并只提供用户
明确选择的“重试 API”或“改用本地重新分析”。取消只能阻止尚未发送的数据
和后续提交，不能承诺召回第三方已接收的数据或费用。

### 5. 主流水线与任务快照保持兼容

远程 Provider 复用现有任务租约、stage checkpoint、结构化 parser/draft、
`ReminderCandidateMerger`、revision/人工字段保护和原子完成路径。不得重写
`AnalysisWorker` 主干，不增加平行数据库，也不得让供应商 DTO 或错误码穿透
到 Core、App 或 SQLite。

当前迁移 7 只为既有 `AnalysisStageResults` 增加显式 stage provenance，
不改变 `AnalysisJobs.ModelProfileSnapshotJson`。迁移前创建可验证备份，
失败时回滚并保留原数据库。旧 stage 行按以下保守规则回填：

- `ExecutionLocation = Local`；
- OCR、确定性实体、无模型 Vision 路由和无模型文本组合分别回填为
  `OcrFacts`、`DeterministicEntityCandidates`、`RoutingDecision` 和
  `ExtractiveDraft`；
- 带模型身份的既有 Vision/文本组合结果回填为 `ModelGeneratedDraft`。

### 6. Provider ID 不再承载输出语义

`AnalysisProvenance` 显式记录：

- `ExecutionLocation`：`Local` 或 `RemoteApi`；
- `OutputKind`：`OcrFacts`、`DeterministicEntityCandidates`、
  `RoutingDecision`、`ModelGeneratedDraft`、`ExtractiveDraft`，以及仅供
  旧/未知数据使用的 `Unspecified`；
- 原有 Provider/model/hash/schema 字段。

业务行为按显式字段决定。`ProviderId` 只用于审计、显示、适配器选择和能力
profile 标识；禁止新增根据 `local.xxx`、`remote.xxx` 前缀或具体供应商 ID
推断草稿来源、上传范围、是否可用候选或失败回退的分支。

## 影响

- 本地行为和默认执行目标不变；此 ADR 本身不启用网络、不增加凭据存储，也
  不改变用户数据上传范围。
- stage provenance 可以在不识别供应商字符串的情况下区分本地/远程执行和
  事实、路由、抽取式草稿、模型草稿。
- 迁移 7 是向后兼容的增量迁移；任务快照的远程字段、凭据服务和 API profile
  仍按后续最小纵向切片实现。
- 未声明 `OutputKind` 的新 Provider 输出按 `Unspecified` 保守处理，不获得
  模型建议语义；Provider 实现必须显式声明其输出种类。
