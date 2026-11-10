# 会话存储治理与删除优化

更新日期：2026-11-11。

状态：S1、S2 已实现并通过本地定向验证，真实 GUI 与用户库耗时待验收；S3–S5 待启动，尚未迁移配置或真实用户数据。入口见 [PLAN.md](../PLAN.md)。

## 1. 目标与分期

先解决删除一个项目需要恢复整个会话库、重复读取目标日志、同步等待全库索引对账的问题。短期不改变持久化格式和权威来源，不以性能修复启动完整存储迁移。

长期将项目和会话组织状态收口到 SQLite，并统一 Core 操作入口，逐阶段替换散落在 Desktop 的多文件协调逻辑。各阶段独立验收，不要求一次完成；计划本身不授权迁移真实数据、清理旧文件、提交或发布。

既有边界见 [项目与会话](Archive/project-session-plan.md)、[列表索引](Archive/session-list-index-design.md)和[会话持久化](Archive/session-persistence-design.md)。短期继续遵循这些契约；长期的数据权威调整必须在对应阶段显式落地并更新契约。

## 2. S1：短期项目删除优化

### 范围

- 保持 `projects.json` 删除标记、JSONL 解除归属事实、原 writer 安全空闲边界与可重试语义。
- 一次读取 SQLite 索引行并枚举来源日志，不能仅查询目标项目而漏掉缺失、过期或失败索引。
- 健康索引行与来源长度、毫秒修改时间一致时，用摘要判断归属；非目标日志不读正文，目标日志直接进入 writer 锁内严格恢复。
- 索引缺失、不可用、条目失败或来源变化时，回退摘要扫描以发现目标。无法确定归属的坏日志继续阻止完成删除；不凭旧索引假装成功。
- 来源标记只是失效线索，沿用既有索引对同长度、同时间替换不可检测的边界；不能据此授予执行权限。目标写入仍严格校验。
- 临时 writer 接入 `SessionIndexUpdater`，解绑批次提交后增量更新索引；索引故障不回滚已提交事实，由既有后台对账补偿。
- Desktop 将日志筛选和解绑放到工作线程，不同步等待全库索引对账；界面状态变更仍回到 UI 上下文。

### 不包含

不迁移归档、置顶、排序或项目定义；不改公开 API、日志 schema 或依赖；不顺带重构整个 ViewModel。永久删除会话前的历史加载另列 S2，不夹带进项目删除修复。

### 验收

- 有效索引下，无关日志即使正文不可读仍可删除目标项目，证明未访问无关正文。
- 目标会话解绑后 SQLite 归属及时更新，活动时间和归档投影不变，原历史字节前缀不变；重复删除不追加重复解绑。
- 覆盖新日志无索引、来源变化、缺失／损坏索引、目标 writer 占用、坏日志和取消；占用或失败可继续处理，不能误报完成。
- 定向 Core／Desktop 回归及受影响构建；必要 NativeAOT publish 与离线产物 smoke 单列记录。隔离测试不替代真实 GUI、真实用户库耗时和跨进程现场验收。

## 3. S2：统一 Core 操作入口，保留现有存储

2026-11-11 已实现并通过本地定向验证；真实 GUI 验收与权威数据迁移不在本阶段。

- 已按 store 落地统一入口 `SessionStoreOperations`：持置顶／归档快照、不可用集合与懒构建 ID 集合缓存，置顶与归档共用一把串行门；受保护目录解析、锁、错误分类、提交后索引维护与 unavailable 状态管理收口在 Core，Core 抛类型化异常，文案与 Notice 仍由 ViewModel 产生。
- 置顶、归档、置顶项目排序、永久删除会话与项目删除的存储协调自 Desktop ViewModel 迁入；ViewModel 仅保留用户意图、运行中状态展示和结果应用。项目置顶与排序写入在 Core 串行门内复查项目删除运行状态并让位，命中时静默放弃。项目删除经 `ISessionProjectDeletionGateway` 宿主接缝编排，Core 不引用界面层。
- 排序边界：置顶项目顺序（`SessionStorePinStore.MovePinnedProjectAsync`）收口入 Core；普通会话／项目手动顺序仍是桌面偏好文件（应用偏好，S3 才迁 SQLite），`MoveSession`／`MoveProject`／`Prioritize`／`SeedManualSessionOrders` 留在 ViewModel。
- 单独核查完成：永久删除前的历史加载仅服务 UI 投影，资源释放、并发保护与失败恢复路径均不依赖它，已移除；严格校验属于恢复／续聊路径，损坏日志可被用户明确地永久删除。
- 复用现有日志 writer、摘要构建、Updater、Reconciler、受保护路径和 runtime 生命周期；CLI 未接新入口，入口为纯 Core、无 UI／Dispatcher 依赖。
- 验收见第 7 节本轮记录；本阶段未迁移权威数据。

## 4. S3：冻结 SQLite 业务数据与配置边界

待启动；完成 S2 后形成可评审 schema 和迁移方案，再实施。

| 数据 | 长期目标权威来源 | 约束 |
| --- | --- | --- |
| 项目定义、目录、修订、删除状态 | 每个 store 的 SQLite 业务表 | 保留目录结构和并发修订语义 |
| 会话当前归属、归档、置顶 | SQLite 业务表 | 当前归属不改变执行目录、模型或权限 |
| 项目／会话手动顺序、项目展开状态 | SQLite 的组织／视图状态表 | 保留置顶前普通位置和现有拖动分类边界 |
| 标题摘要、活动时间、回合数、来源进度 | SQLite 可重建摘要表 | Updater 不得覆盖业务表；重建只清理派生数据 |
| 对话、执行事实、检查点、模型／权限选择、固定执行目录 | JSONL | 严格恢复与安全判断继续依据日志，不用列表摘要替代 |
| 主题、窗口、确认提示等应用偏好 | 用户配置 | 与每个 store 的组织状态分离 |
| 供应商配置、网络配置、凭据引用与真实凭据 | 现有配置／凭据机制 | 本计划不将秘密搬入会话数据库，不扩展为全面配置重构 |

数据库从缓存升级为业务存储后，缺失／损坏不能再静默重建为空；需要业务备份恢复和派生摘要重建的独立入口。具体数据库文件命名、schema 升级与表约束在此阶段冻结，不直接复用当前“整库索引重建”的假设。

## 5. S4：分批导入、切换与兼容

待启动；依赖 S3。

1. 基于隔离副本实现幂等导入与迁移标记，先验证，再允许真实数据迁移。旧文件在新存储完整校验前保持原样。
2. 分批切换归档／置顶／排序、项目目录册、最后当前会话归属；每批只保留一个权威来源，过渡状态有版本标记，不长期双写。
3. 归属迁移需同时调整恢复投影、运行宿主与旧日志初始化：旧归属记录仅作导入来源，不能覆盖迁移后的当前关系；数据库缺失不得静默回退旧关系。
4. 明确中断续作、损坏报告、备份恢复及回退边界。旧版本仍会写旧文件，不能宣称新旧程序可同时写同一 store；在切换前制定实际可执行的升级／停止旧写者方案。
5. 所有调用方切换后再移除旧生产读写路径；用户数据备份清理必须另行明确授权，不在升级中自动删除。

## 6. S5：迁移后简化删除与验收

待启动；依赖 S4。

- 项目删除在 SQLite 事务内解除关系并清理项目组织状态，不再为组织变化逐日志追加记录；运行中会话限制与内存宿主更新仍需验证。
- 会话删除使用可恢复的删除状态协调数据库和日志文件，保留 writer 锁与宿主释放；不宣称跨文件和数据库原子提交，避免后台扫描复活删除中条目。
- 元数据操作不刷新对话活动时间；运行会话优先、空闲手动排序、取消置顶恢复原位置等语义不变。
- 覆盖双进程、在途写入、崩溃窗口、版本冲突、只读与不可写库、业务备份恢复、摘要重建不丢组织状态，并执行 NativeAOT／离线 smoke 与真实 GUI 验收。

## 7. 本轮实施与验证记录

2026-11-11，S2 review 修复：

- 恢复置顶写入的门内项目删除让位防护：`SessionStoreOperations.SetProjectPinnedAsync`／`MovePinnedProjectAsync` 增加写入前置谓词（VM 传 `() => IsProjectDeletionRunning`），Core 在取得串行门之后、任何 pin 文件写入与快照更新之前求值；命中时不写入、不更新快照并返回 false，VM 静默放弃（无 Notice、不重建列表），与原 `_pinStateMutationGate` 门内复查语义等价；门上排队期间项目删除启动的场景不再把正被删除的项目写回 pin 文件。会话置顶与归档写入门内无此检查，保持不变。
- 项目解绑完整性把关上移 Core（review P2）：`DeleteProjectAsync` 在调用 `gateway.UnassignmentAppliedAsync`（保持先应用行投影事实）之后、置顶清理／`PrepareCompletionAsync`／`CompleteDeletionAsync` 之前自行检查 `result.IsComplete`，不完整时抛新增类型 `SessionProjectUnassignmentIncompleteException`（Core Persistence 命名空间，携带 `Result` 属性；Core 既有异常均为 `sealed : Exception`，`SessionStoreException` 已 sealed，故按仓库惯例派生自 `Exception` 而非其子类），中止编排——目录册删除标记、pin 文件与日志归属保留，"继续删除"入口不变；异常消息为含占用／失败计数的技术性描述。Desktop `ProjectDeletionGateway.UnassignmentAppliedAsync` 移除 `!IsComplete → InvalidOperationException` 抛出（保留行投影应用与宿主取消 OCE 判定），`ConfirmProjectDeletionAsync` 新增对该异常的 catch 分支，用既有 `DescribeIncompleteProjectUnassignment(exception.Result)` 经 `_factory.Redact` 写入 `ProjectDeletionErrorText` 或 `Notice`，展示位置与原路径一致（原路径同样经 Redact）。gateway 接口与编排方法文档注释同步改为"取消判定属钩子实现、完整性由 Core 把关"。
- 补测 5 项：让位谓词在取得门后才求值、命中时不落盘不更新快照（Set／Move 各一，Set 附谓词放行对照）；置顶状态目录占位使清理失败并记入 issues，归档与索引清理照常；独立连接持写事务占用索引写锁，WAL 下可用性探测照常可用而删除写入等待超时失败，记入 issues 且归档／置顶清理照常、索引行保留；删除前取消异常原样传播且日志、归档、置顶与索引状态原样。
- 补测 2 项（完整性上移）：gateway 不主动抛异常且解绑含 Pending（writer 占用）时，Core 抛出新异常且 `Result` 可取，目录册项目带删除标记保留、项目置顶未清理、释放 writer 后日志归属未变、`PrepareCompletionAsync`／`CompletedAsync` 未调用；解绑含 Failed（非法日志文件名）时同样中止并保留目录册与置顶。原 `DeleteProject_WriterHeldIncompleteUnassignment_AbortsAndResumesOnRetry` 保留，改述为覆盖"gateway 钩子异常中止编排并原样传播"路径，假接缝注释同步；Desktop 无针对原 VM `InvalidOperationException` 路径的既有测试，无需调整。
- `MutationGateForTests` 更名 `MutationGate` 并如实描述为生产串行门（原注释"生产不得使用"与所有生产路径经此互斥的事实相反；Core 已有 `InternalsVisibleTo("TinyHarness.Tests")`，测试引用同步调整）。
- 验证：Core／Desktop Debug 构建 0 警告 0 错误；`SessionStoreOperationsTests` 23/23，Desktop 定向（归档会话删除 8、项目目录册同步 22、归档导航 5）合计 35/35；ReSharper CLI `jb cleanupcode`（profile `banned甄选`，仅 include 本轮修改的 3 个 C# 文件）执行并逐处复核。本轮改动为静态委托分发与测试，不触及 AOT 敏感新边界，未重跑 NativeAOT publish。
- 完整性上移验证：Core／Desktop Debug 构建 0 警告 0 错误；`SessionStoreOperationsTests` 25/25（新增 2 项），Desktop 定向（归档会话删除 8、项目目录册同步 22、归档导航 5）合计 35/35；`jb cleanupcode`（profile `banned甄选`，仅 include 本轮修改的 4 个 C# 文件）执行并逐处复核。本轮为 Core 编排内异常类型与检查新增 + VM catch 分支，无反射／动态代码／配置绑定／入口变化，未重跑 NativeAOT publish。
- 未验证：真实 GUI 交互、真实用户库、跨进程双写者并发实测（继承 S2 与 S1 记录的边界）。

2026-11-11，S2：

- 新增 Core 统一入口 `SessionStoreOperations`（`TinyHarness.Core/Services/Persistence/SessionStoreOperations.cs`）：按 storeKey（去尾分隔符完整路径，Windows 不区分大小写）持置顶／归档快照、不可用集合与懒构建 ID 集合缓存；置顶与归档共用一把串行门（原 `_pinStateMutationGate` 语义整体迁移，未拆分）；提供 `ResolveProtectedStoreDirectoryAsync` 静态解析，VM 的 `ResolveDirectoryOverride` 测试接缝保留等价语义。
- 迁移并收口的操作：置顶／归档读取迁移（legacy 列表由 VM 传入，迁移事实返回 VM 清理旧偏好并保存桌面偏好文件）；`SetSessionPinned`／`SetProjectPinned`／`MovePinnedProject`；`SetSessionArchived`（门内受保护目录校验 + `SyncArchiveProjectionAsync` 尽力同步：索引缺失／不可用不创建库文件，失败只记 Trace 不回滚归档权威）；会话永久删除编排（writer 锁删除 + 归档／置顶／索引投影清理，失败以 `SessionStoreCleanupIssue` 列表返回不阻断；旧侧栏偏好清理留在 VM 并把失败并入 issues）；项目删除编排（`ISessionProjectDeletionGateway` 六阶段宿主接缝：标记 → 解绑草稿页 → 经打开 runtime 解绑运行会话 → `SessionProjectUnbinder.UnassignAsync`（`Task.Run` 工作线程语义保持）→ 置顶清理 → 最终移除，取消与完整性判定属 gateway 实现）。
- Desktop `MainWindowViewModel` 删除原快照字段、不可用集合与 `_pinStateMutationGate`，改持单一入口实例；`IsSessionPinned`／`IsSessionArchived`／`CanUsePinState`／`CanUseArchiveState` 查询语义经入口保留，unavailable 文案与拒绝行为逐字保留。项目删除运行中检查（`IsProjectDeletionRunning`）与 draft `CanChooseWorkspace` 检查按设计留在 VM 调用前判断；`SameSidebarStore` 数组维护、`RebuildProjects`、dialogVersion 迟到防护留 VM。
- 排序边界陈述：置顶项目顺序收口入 Core；普通会话／项目手动顺序持久化在桌面偏好文件（`SidebarPreferencesStore`，属应用偏好，S3 才迁 SQLite），`MoveSession`／`MoveProject`／`Prioritize`／`SeedManualSessionOrders` 留在 ViewModel。
- 单独核查永久删除前的历史加载（证据链）：`SessionStoreReader.ReadAsync`（`SessionLogStore.cs`）与 `SessionHistoryReader.ReadPageAsync` 对损坏日志严格抛 `SessionStoreException`，其职责属于恢复路径而非删除（删除只删文件，不读内容）；`LoadHistoryAsync` 自身无锁、`CancellationToken.None`，其调用方（SendAsync／换模型／换权限）在打开 runtime 前自行加载，删除的并发保护由 mutation 占用 + refresh gate + writer 锁承担，与历史加载无关；`ReleaseRuntimeForDeletionAsync` 不依赖历史；writer 占用失败后行保留，重新选择走 `SelectedSession` 赋值触发的 `LoadSelectedHistoryAsync`，续聊走 SendAsync 自行加载（`DeleteFailureAfterRuntimeRelease` 语义保持）。据此移除删除前的 `EnsureHistoryLoadedAsync`：其唯一效果是 UI 投影预读，且损坏日志会把缓存任务永久置于 faulted（`_historyLoad ??=` 不复位），使损坏会话无法再经 UI 删除。新增 `ConfirmDeletion_SucceedsWithoutPreloadedHistory`、`ConfirmDeletion_DamagedLog_IsDeleted` 两项证明；损坏日志从"阻断删除"变为"可删除"是有据行为变更，恢复／续聊路径严格校验不变。
- 新增 Core `SessionStoreOperationsTests` 18 项（legacy 迁移一次性、目录不一致拒绝、损坏标记不可用并恢复、快照与 ID 缓存失效、置顶插入／拖放语义、串行门互斥、归档索引尽力同步含缺失不建库与损坏不回滚、清理 issues 不阻断、项目删除编排顺序／writer 占用中止后重试／取消保留删除标记、受保护目录解析拒绝），与 Unbinder／Updater／Binding／Reconciler／Deletion 定向合计 **67/67** 通过。Desktop 既有与新增回归定向 **152/152** 通过（新增置顶主路径、无预加载删除、损坏日志可删三项；全量 Desktop 套件在格式化前 **379/379** 通过）。全量 Core **2182/2184**：2 项失败为 `SandboxTargetEnvironmentTests.SdkNuGetPathLookup_UsesRedirectedEnvironment`（子进程 NuGet 环境重定向断言，本机 SDK 11.0.100 环境下预存，与 S2 改动无交集）。
- CLI 与 Desktop Release `win-x64` NativeAOT publish 均成功，0 compiler／trimming／AOT warning；产物、smoke 与 publish 日志在 `artifacts/s2-core-entry-20261110/`。CLI `smoke`（steps 17／toolExecs 16，含 SQLite 索引读回）与 Desktop `--offline-smoke`（parallel=2、switched=A/B/A/B、refreshed=true、resumedTurns=4、disposedClients=6）通过，均使用工作区内隔离 TEMP／APPDATA／LOCALAPPDATA 并补系统 PowerShell 路径；publish 保持进程级精简 PATH、显式 MSVC／Windows SDK `LIB`、`IlcUseEnvironmentalTools=true`，Desktop 进程级 `UsedAvaloniaProducts=`。
- 本机环境变化记录：默认 SDK 升至 11.0.100 后，smoke fixture 的 `<clear/>` 离线还原因本机 packs 目录缺 `Microsoft.NETCore.App.Ref` 10.0.15 而失败；未改机器配置，以进程级 `RestoreFallbackFolders` 指向用户包缓存完成离线还原。
- 格式化：ReSharper CLI `jb cleanupcode`（profile `banned甄选`，仅 include 本轮修改的 5 个 C# 文件）执行三轮并按 Docs/code-style.md 逐处复核；外层调用已收编为首参数紧跟左括号的形状，`MainWindowViewModel.cs` 中两处内层 lambda 调用（`PreparePinStateAsync`／`PrepareArchiveStateAsync` 的 `token => ResolveProtectedDirectoryAsync(...)`）与 `SessionDeletion.cs` 中一处 `DeleteSessionPermanentlyAsync` 续行缩进为工具三轮后的稳定输出，不再与工具反复拉扯。
- 未验证：真实 GUI 交互、真实用户库、跨进程双写者并发实测；S3（SQLite 权威迁移）未开始。

2026-11-10，S1：

- `SessionProjectUnbinder` 读取索引行并比对来源标记，未变日志不再预先完整恢复；缺失或变化条目回退摘要扫描，目标日志仍在排他 writer 锁内严格恢复。临时 writer 接入既有 `SessionIndexUpdater`，无需新增公开 API 或更改数据格式。
- Desktop 将筛选／解绑调度到工作线程，移除解绑后的同步全库对账等待；原目录册删除标记、占用／损坏中断、侧栏清理与最终移除顺序保留。
- 新增 `SessionProjectUnbinderTests` 9 项；与 `SessionIndexUpdaterTests`、`SessionProjectBindingTests`、`SessionIndexReconcilerTests` 合计 **47/47** 通过。覆盖独占持有无关正文仍可成功（证明未读正文）、解绑后 SQLite 更新／归档投影／活动时间／原字节前缀保持、重复处理幂等、来源变化、新日志漏索引、数据库缺失／损坏、writer 占用、坏日志与取消。
- `DesktopProjectCatalogSyncTests` **22/22** 通过，包含项目删除与目录册检查交错；Desktop Debug 构建 **0 warning / 0 error**。
- CLI 与 Desktop Release `win-x64` NativeAOT publish 成功，无 compiler／trimming／AOT warning；原生产物 CLI `smoke`（17 steps／16 tool executions，含 SQLite 索引读回）与 Desktop `--offline-smoke`（parallel=2、switched=A/B/A/B、refreshed=true、resumedTurns=4、disposedClients=6）通过。产物与 smoke 输出在 `artifacts/project-deletion-opt-20261110/`。
- 本机继承 PATH 过长，自动 VS 工具探测失败；publish 使用进程级精简 PATH、显式 MSVC／Windows SDK `LIB` 和 `IlcUseEnvironmentalTools=true`，Desktop 使用进程级 `UsedAvaloniaProducts=`。首次 smoke 受用户临时目录访问和精简 PATH 缺少 PowerShell 影响；改用工作区内隔离 TEMP／APPDATA／LOCALAPPDATA，并补齐系统 PowerShell 路径后通过，未修改机器环境或权限。
- 按 `.editorconfig` 和邻近风格人工检查修改范围，未执行完整 ReSharper CleanupCode；文档相对链接、行尾空白与文件结尾检查单列执行。
- 未验证：真实 GUI 删除体验、真实用户会话库分段耗时、用户所述那次删除现场、Linux／macOS 与新增双进程并发实测。离线 smoke 不直接覆盖项目删除界面；长期存储迁移、会话永久删除历史加载优化及其他功能协调入口尚未实施。
