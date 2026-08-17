# ADR 0007: 本地提醒候选、人工确认与通知 outbox

- 状态：Accepted
- 日期：2026-07-28
- 修订：2026-07-30、2026-08-13（unpackaged 通知路径见 ADR 0015）

## 背景

图片里的日期和地点可能来自 OCR、可信元数据或本地模型。它们既可能存在
`03/04`、缺少年份、缺少时间、夏令时等歧义，也不能因为模型给出一个看似
完整的结果就直接创建系统提醒。Windows 定时通知也不是事实源：电脑关闭或
休眠超过投递窗口后，通知可能不会补发。

## 决策

1. 所有分析模式都先运行 OCR；`Balanced` 在路由命中时、`AlwaysEnhance`
   在模型可用时再运行本地视觉模型。提醒发现独立合并确定性 OCR 候选与视觉
   模型候选，不受标题/简介最终采用哪个 `ITextComposer` 的影响。OCR 候选
   先进入去重结果；视觉模型补回但 OCR 无法逐字复核的候选保留为低信任
   `ModelOnlyInterpretation`，在 UI 明示“仅模型识别”并要求重点对照原图，
   不把它升级成 OCR 事实，也不自动创建提醒。
2. 在 OCR 之后运行确定性 `IEntityExtractor`，日期与地点分别生成
   `EntityCandidate`。日期时间由本地 `Microsoft.Recognizers.Text.DateTime`
   解析，不再扩张应用自有的场景正则；当前只声明上游正式支持并通过测试的
   中、英、西、法、葡、德、意、土八种语言。保存原始文本、规范化值、来源、
   OCR 证据、边界框、参考时间、时区和歧义原因。模型候选可以补充其他语言
   与语义场景，但不能覆盖 OCR 事实，也不能在未安装时伪装成可用能力。
   多语言 OCR 返回 `und-Hani`、`und-Latn` 等“语言未定、文字体系已知”的
   BCP-47 标签时，实体解析器按文字体系选择本地解析能力；同一 OCR 行内被
   星期附注或轻量标点拆开的日期与时间可在解析结果层合并。同一视觉文本块
   中相邻、左侧对齐且垂直间距足够小的 OCR 行，也只在上行恰有一个日期、
   下行恰有一个时间时合并；不得跨逗号、分号、句号、远距离行或多个事件
   边界拼接。OCR 与模型给出的相同规范化时刻按时区合并为一个候选，完整
   日期时间会吸收同一证据中重复的日期片段或时间片段，不同的时刻继续作为
   不同提醒候选。若原始 OCR 和模型证据都没有年份，统一使用参考时区中的
   分析当年并标记 `MissingYear`，即使月日已过也不得擅自滚到下一年。
3. 只把候选展示给用户。确认 UI 必须让用户核对日期、时间、Windows 时区
   和可编辑地点。为减少机械输入，识别到完整日期但缺少时间时暂填 `10:00`，
   只到年月时暂填该月 `1 日 10:00`，只到年份时暂填 `1 月 1 日 10:00`；
   `InfoBar` 必须明确说明哪些字段来自默认值，且用户确认前不得写入提醒或安排
   通知。无效日期、数字日期顺序歧义、夏令时缺口或重叠时间仍不自动选择。
4. 提醒页使用 WinUI `ListView` 主从布局：
   - `>=1008` epx 同时显示候选/已确认列表与右侧编辑器；
   - `641–1007` 和 `<640` epx 使用列表与编辑器单页切换；
   - 使用 `CalendarDatePicker`、`TimePicker`、`ComboBox`、`TextBox` 和
     `InfoBar`，并为自动化与键盘操作提供稳定标识。
5. SQLite 的 `Reminders` 是事实源。确认或编辑先提交数据库，同时写入
   `ReminderNotificationOutbox`；系统调度成功与数据库提交不伪装成同一事务。
   每个提醒拥有稳定 scheduler ID，重试、编辑和取消均保持幂等。
6. Windows 投影使用
   `AppNotificationBuilder` 构造本地化 payload，并通过 unpackaged desktop
   compatibility notifier 的 `ScheduledToastNotification`/`AddToSchedule` 安排通知。
   `ToastNotificationManagerCompat.OnActivated` 在进程初始启动代码中注册激活入口；
   点击通知以稳定 reminder/image ID 打开资料库项。
7. 启动和进入提醒页时对账：
   - 恢复被进程中断的 outbox 项；
   - 为仍在未来但系统调度中缺失的活动提醒重新排队；
   - 取消没有活动数据库记录的孤立系统通知；
   - 到期超过五分钟仍未激活的提醒标记为“已错过”，在应用内可见，不承诺补发。
8. 软删除图片时写入取消 outbox 并暂停其提醒；恢复时，未来提醒进入
   “需要重新确认”，过去提醒标记“已错过”，均不自动重新安排。
9. 资料库详情和项目右键菜单提供“添加提醒”。即使没有任何识别候选，用户也
   能以图片标题、缩略图和本地时区为起点打开同一确认编辑器；保存时允许
   `SourceDateCandidateId`/`SourceLocationCandidateId` 为空，但仍走相同
   SQLite 事实记录、outbox、未来时间和夏令时校验。
10. 分析完成后通过进程内变更事件刷新当前提醒页。事件只负责唤醒 UI 查询，
     SQLite 仍是候选事实源；连续事件会合并处理，不引入后台轮询。
11. “待确认候选”是 SQLite 事实的可操作投影，不是删除后的事实集合。能够
    证明按当前暂填规则已过期的绝对日期时间不再进入提醒队列；缺少日期的
    单独时间和无法安全解释的值继续保留证据，但不猜测成未来提醒。重新分析
    同一图片时，只由新结果原子替换仍为 `Pending` 的旧候选，用户已经确认或
    忽略的决定不会被重置。资料库标题与简介仍沿用原分析逻辑；同一图片有
    多个不同提醒时，提醒列表和编辑器以各候选的证据行作为可编辑默认标题，
    避免第二个事件误用第一个事件的时间标题。

## 影响

- 全流程保持本地，不增加在线地理编码、云推理、遥测或账号依赖。
- 通知被系统或用户禁用时，图片、候选和提醒记录仍可查看编辑；界面显示投递
  能力限制。
- 模式迁移升级到版本 6，并沿用迁移前备份与失败回滚；提醒标题独立保存在
  `Reminders`，旧提醒的空标题回退显示资料库标题，编辑提醒不再改写资料库。
- 实体解析使用本地运行时，不增加模型、网络能力或常驻服务。
- 当前版本的确定性地点抽取只保留地址片段，不尝试在线补全或反向地理编码。

## 平台依据

- [Scheduled app notifications](https://learn.microsoft.com/windows/apps/develop/notifications/app-notifications/app-notifications-scheduled)
- [App notifications quickstart](https://learn.microsoft.com/windows/apps/develop/notifications/app-notifications/app-notifications-quickstart)
