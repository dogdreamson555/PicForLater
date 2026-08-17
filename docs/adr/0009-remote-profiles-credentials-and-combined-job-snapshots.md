# ADR 0009：远程 profile、凭据与组合任务快照

- 状态：Accepted
- 日期：2026-07-31

## 背景

ADR 0008 已确定本地默认、`RemoteOcrText`/`RemoteVision` 数据边界、版本化
同意和禁止跨模式回退。本阶段需要让现有本地模型 profile 与未来远程 API
profile 能够共同生成同一种持久化任务快照，同时保持旧任务 JSON 和升级用户
默认本地。

远程配置不能进入 `ModelPackages`：模型包表具有本地文件、清单、哈希和
安装目录语义。API key 也不能进入 SQLite、普通设置或任务快照。

## 决策

1. 在 Core 增加正交枚举：
   - `AnalysisExecutionBackend { Local = 0, RemoteApi = 1 }`；
   - `RemoteInputMode { LocalOcrText = 1, DirectImage = 2 }`。
   `Local = 0` 保证旧 JSON 缺少字段时使用 CLR 默认值仍为本地。
2. 保留 `ModelProfileSnapshot(AnalysisMode, Revision, Slots)` 的 positional
   构造不变，只增加带默认值的 init-only `ExecutionBackend`、可空
   `RemoteInputMode` 和可空 `RemoteApiProfileSnapshot`。本地快照的两个远程
   字段必须为空。
3. `RemoteApiProfileSnapshot` 固定任务创建时的非秘密字段：profile/provider/
   endpoint、base URI、model、prompt/schema、载荷与超时上限、
   credential reference 和同意版本。它不包含 API key、Authorization、请求
   正文或供应商完整响应。
4. 迁移 8 新建独立 `RemoteApiProfiles` 表，并只向唯一
   `AnalysisSettings` 行追加 `ExecutionBackend`、`RemoteInputMode` 和
   `RemoteApiProfileId`。三个字段默认 `Local/NULL/NULL`，不修改已发布迁移
   1–7，也不重写旧 `AnalysisJobs.ModelProfileSnapshotJson`。
5. 继续复用 `AnalysisSettings.ProfileRevision` 作为唯一配置 revision。本地
   模式、模型槽位、当前执行目标或已选远程 profile 改变时递增同一 revision。
   不建立第二套互相竞争的“远程 revision”。
6. `CombinedAnalysisProfileSnapshotProvider` 读取本地 capability snapshot 和
   远程执行状态；只有 revision 相等时才组合，否则有限重试。远程快照只有在
   profile 已启用、验证有效、支持所选输入模式，且同意版本和同意模式匹配时
   才能创建。
7. `RemoteApiProfiles` 只保存 HTTPS endpoint、能力/限制、政策链接和核验时间、
   验证状态、版本化披露/同意状态及 credential reference。扩大数据范围、
   切换 provider/endpoint、变更 prompt/schema/政策声明或提高载荷范围会清除
   旧同意。正在使用的 profile 若将变为不可用，必须先显式返回本地，不能在
   保存时静默切换。
8. `IRemoteApiCredentialService` 位于 Core 抽象层；Windows 实现继续使用当前
   Windows 用户的 Credential Locker (`PasswordVault`)，不依赖 package identity。
   保存、读取、存在性检查和删除均只按稳定 credential reference 操作，不缓存或
   记录明文。SQLite、`settings.json` 和旧 `ApplicationData.LocalSettings` 都不保存
   secret。unpackaged 桌面进程不具备 MSIX 容器隔离，因此该边界保护静态存储，不能
   防御同一用户权限下已经运行的恶意进程。
## 影响

- 新安装、迁移 1–7 的升级用户和旧任务 JSON 均解析为 `Local`。
- 远程选择只影响选择完成后创建的新任务；旧任务继续使用自己的快照。
- 新增 SQLite 表和少量设置列，不新增 SDK、网络请求、账号、遥测、模型文件
  或常驻服务。
- Credential Locker 的秘密生命周期独立于 profile 行；删除 profile 不代表
  供应商后台密钥已吊销，后续 UI 必须分别说明并协调删除。
