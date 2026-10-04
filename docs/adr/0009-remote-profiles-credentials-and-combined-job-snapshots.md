# ADR 0009：远程 profile、凭据与组合任务快照

- 状态：Accepted
- 日期：2026-07-31

## 背景

[ADR 0008](0008-local-default-remote-boundaries-and-explicit-provenance.md) 已确定本地默认、两种载荷边界、版本化同意和禁止跨模式回退。本 ADR 为本地与远程配置定义共同任务快照，同时保持旧任务兼容。远程 API 配置不属于具有本地文件语义的 `ModelPackages`，API key 也不能进入 SQLite、普通设置或任务快照。

## 决策

1. Core 增加 `AnalysisExecutionBackend { Local = 0, RemoteApi = 1 }` 和 `RemoteInputMode { LocalOcrText = 1, DirectImage = 2 }`。`Local = 0` 使缺少该字段的旧 JSON 按 CLR 默认值解析为本地。
2. 保持 `ModelProfileSnapshot(AnalysisMode, Revision, Slots)` 的 positional 构造不变，仅增加带默认值的 init-only `ExecutionBackend`、可空 `RemoteInputMode` 和可空 `RemoteApiProfileSnapshot`。本地快照的远程字段必须为空。
3. `RemoteApiProfileSnapshot` 固定任务创建时的非秘密配置：profile/provider/endpoint、base URI、model、prompt/schema、载荷与超时上限、credential reference 和同意版本；不含 API key、Authorization、请求正文或完整响应。
4. 迁移 8 新建 `RemoteApiProfiles`，并只向唯一 `AnalysisSettings` 行追加 `ExecutionBackend`、`RemoteInputMode`、`RemoteApiProfileId`，默认分别为 `Local/NULL/NULL`。不修改已发布迁移 1–7 或重写旧任务快照 JSON。
5. `AnalysisSettings.ProfileRevision` 是唯一配置 revision；本地模式、模型槽位、执行目标或所选远程 profile 变更时递增，不另建远程 revision。
6. `CombinedAnalysisProfileSnapshotProvider` 合并本地 capability snapshot 与远程执行状态，仅在 revision 一致时返回，冲突时有限重试。远程快照仅能由已启用、已验证、支持所选输入模式且同意版本/模式匹配的 profile 创建。
7. 本阶段的 profile 只保存 HTTPS endpoint、能力/限制、政策链接及核验时间、验证状态、版本化披露/同意和 credential reference。扩大数据范围、切换供应商/endpoint、修改 prompt/schema/政策或提高载荷范围都会清除旧同意。当前选中的 profile 若将不可用，保存前必须显式切回本地，不能静默切换。
8. `IRemoteApiCredentialService` 定义在 Core，Windows 实现使用当前用户的 Credential Locker (`PasswordVault`)，不依赖 package identity。所有操作只通过稳定 reference 保存、读取、检查或删除密钥，不缓存或记录明文；SQLite、`settings.json` 和旧 `ApplicationData.LocalSettings` 均不保存 secret。Unpackaged 进程没有 MSIX 容器隔离，此边界保护静态存储，不能抵御同一用户权限下已运行的恶意进程。

## 影响

- 新安装、升级用户及旧任务 JSON 都解析为 `Local`；远程选择只影响其后创建的任务，旧任务继续使用自己的快照。
- 新增独立 profile 表和少量设置列，不引入 SDK、请求、账号、遥测、模型文件或常驻服务。
- Credential Locker 的密钥生命周期独立于 profile 行；删除 profile 不会吊销供应商侧密钥，后续 UI 必须分别说明并协调处理。
