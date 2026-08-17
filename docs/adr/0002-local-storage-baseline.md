# ADR 0002: 本地存储与迁移基线

- 状态：Accepted（生产根路径由 ADR 0015 修订）
- 日期：2026-07-17

## 背景

图片必须先可靠保存，再进入可恢复的分析流程。SQLite、文件系统和以后接入的系统通知无法组成单一事务，因此第一阶段必须先确定稳定路径、迁移和失败语义，避免后续以删除或重建数据库修复一致性问题。

## 决策

应用组合根把 `%LocalAppData%\PicForLater` 作为 unpackaged 生产运行时根目录传入
Infrastructure；Core 与测试不读取用户真实目录。固定布局为：

```text
data/picforlater.db
data/backups/
assets/originals/
cache/thumbnails/
staging/
```

- SQLite 保存元数据、任务和稳定的规范化相对路径，不保存完整图片 BLOB。
- 原图最终路径由小写 SHA-256 和白名单扩展名生成，不使用用户文件名；原图 API 只暴露读流。
- 受管根目录到目标文件之间任何已存在路径段若带有 `FileAttributes.ReparsePoint`（包括符号链接和 junction），存储操作立即停止；创建目录后再次校验。清理 staging、数据库/备份访问和原图提升都不得跟随重解析点越出受管目录。
- staging 与 originals 位于同一根目录/卷。写 staging 时流式计算 SHA-256，关闭并校验后才允许使用不覆盖的原子移动提升。移动完成后重新计算最终文件的 SHA-256 与字节长度；任一不匹配都拒绝接受该原图。编码文件暂存硬上限为 512 MiB；超限或取消会删除部分文件。
- 存储层在提升前复核 PNG/JPEG/WebP 文件签名，防止调用者用不同扩展名为同一内容创建多份原图。签名检查不是“可解码”证明；第二阶段导入验证器仍必须用 Windows 解码 API 检查完整可解码性、像素尺寸和解压后像素预算，未经验证的 API 不对 UI 开放。
- v1 数据库包含 `SchemaMigrations`、`ImageAssets`、`ImageItems`、`ImportJobs` 和 `AnalysisJobs`。分类、提醒和模型结果在出现真实行为时通过后续迁移加入。
- 每条迁移有固定版本、名称和 SQL checksum。数据库版本高于应用或已应用 checksum 改变时立即停止，不降级、不重建。
- 数据库有 pending migration 时，先取得跨进程 `BEGIN IMMEDIATE` 写保留锁，再在锁内重新读取版本和迁移历史。是否备份不能依赖锁前缓存的“文件是否存在”：持锁后只要当前版本大于 0 或数据库已有用户 schema 对象，就必须先备份。备份使用另一条只读连接通过 SQLite Backup API 生成一致性快照，执行 `quick_check` 后原子命名，再由持锁连接应用全部 pending migrations。并发启动者取得锁后若发现版本已更新则不重复执行。备份或迁移失败时不继续启动存储功能，并保留主库与已验证备份。
- 校验迁移历史和 checksum 前不设置 `journal_mode` 等持久化 PRAGMA；不兼容或未来版本数据库按字节保持不变并 fail closed。
- 自动生成的迁移备份只用于内部失败恢复，不对外承诺用户备份/恢复能力，也不自动清理。

## 依赖

- `Microsoft.Data.Sqlite 10.0.10`：MIT；使用直接 ADO.NET API，不引入 ORM。
- `SQLitePCLRaw.lib.e_sqlite3 3.53.3`：Apache-2.0/SQLite public-domain components；显式覆盖 `Microsoft.Data.Sqlite` 当前传递引入的脆弱 2.1.11 原生库。

NuGet 安全审计覆盖直接和传递依赖，`NU1901`–`NU1904` 作为构建错误。依赖版本、许可证、替代方案和 Release 包体影响记录在 `docs/dependencies.md` 与 `THIRD-PARTY-NOTICES.md`。

## 结果

- 所有自动化测试必须注入独立临时根目录，并在连接释放后清理；禁止访问真实用户数据根。
- 以后导入事务可以依赖持久化 job、唯一 hash 和稳定相对路径实现幂等恢复。
- 集成测试必须覆盖等待迁移锁期间数据库由另一初始化器创建的竞态，并验证升级者仍基于锁内状态创建 v1 快照；路径测试必须验证目录和文件重解析点被拒绝且外部目标不被修改。
- SQLite 原生库会增加 Windows Release 包体；阶段 1 完成前必须测量实际增量，不能只引用 NuGet 下载大小。
