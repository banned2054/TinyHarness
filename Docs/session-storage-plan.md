# 会话存储治理与删除优化

更新日期：2026-11-17。S1、S2 的实现与本地定向验证记录见第 7 节（截至 2026-11-11）；本次只读对账未重跑验证。S3 已于 2026-11-15 完成 schema 冻结与版本化建库／升级基础（第 4 节），未切换生产读写权威、未导入真实数据；S4 已于 2026-11-15 实施分批导入、按迁移标记切换生产权威与旧版本兼容（第 5 节），并于 2026-11-17 修复静态审查发现的 5 项缺陷（第 5.8 节）；S5 已于 2026-11-17 实施业务库 schema 版本 2 的删除墓碑、可续作会话删除与迁移后项目删除事务（第 6 节），并同日修复静态审查发现的 4 项缺陷（第 7 节：v1 业务库的生产升级入口、清理未完成时保留待收尾墓碑、项目删除向宿主传递实际解绑会话、日志已消失的墓碑索引行由孤儿清理处理）；本地定向与全量验证见第 7 节。迁移与 schema 升级只可在显式入口触发且未对真实用户 store 执行。真实 GUI、真实用户库耗时、双进程并发恢复与断电现场验收仍待授权。入口见 [PLAN.md](../PLAN.md)。

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

2026-11-15 完成 schema 冻结与版本化建库／升级基础（第 4.11 节）。本阶段**不切换生产读写权威**：
项目目录册、置顶、归档、桌面偏好与会话日志仍是实际读写来源，业务库没有任何生产调用方；也未导入真实用户数据。

### 4.1 权威边界

| 数据 | 长期目标权威来源 | 约束 |
| --- | --- | --- |
| 项目定义、目录、修订、删除状态 | 每个 store 的 SQLite 业务表（`project`） | 保留目录结构和并发修订语义；删除仍需先解绑会话 |
| 会话当前归属、归档、置顶 | SQLite 业务表（`session_placement`／`archived_session`／`pinned_session`） | 当前归属不改变执行目录、模型或权限；分表保存，便于分批切换 |
| 项目／会话手动顺序、项目展开状态 | SQLite 组织／视图状态表（`project_order`／`session_order`／`pinned_project`／`collapsed_group`） | 保留置顶前普通位置和现有拖动分类边界 |
| 列表标题、活动时间、回合数、来源进度 | SQLite 可重建摘要表（`session-index.db` 的 `session_summary`，本阶段不迁入业务库） | 已保存标题来自 JSONL 的 `session-title` 事件；标题回退规则和活动时间按日志重建。Updater 不得覆盖业务表；重建只清理派生数据 |
| 对话、执行事实、检查点、模型／权限选择、固定执行目录、已保存标题状态 | JSONL | 严格恢复与安全判断继续依据日志，不用列表摘要替代；数据导入不得重新请求模型生成标题 |
| 主题、窗口、删除确认提示等应用偏好 | 现有用户／桌面偏好存储（`desktop-sidebar.json`／`user-config.json`） | 与每个 store 的组织状态分离；`ShowArchivedSessionDeletionPrompt` 保留在桌面偏好，不迁入 store 业务表 |
| 供应商配置、网络配置、凭据引用与真实凭据 | 现有配置／凭据机制 | 本计划不将秘密搬入会话数据库，不扩展为全面配置重构 |

### 4.2 数据库文件与两类数据的关系

同一 session store 目录下有两个**互不替代**的数据库文件：

| 文件 | 内容 | 丢失或损坏的处置 |
| --- | --- | --- |
| `session-index.db`（既有，schema 版本 2） | 可从 JSONL 重建的列表摘要与归档投影 | 允许整库清空重建，由对账重新派生 |
| `session-store.db`（S3 新增，schema 版本 2） | 项目、归属、归档、置顶、顺序、展开状态与会话删除墓碑 | **不可重建**：只能由业务备份恢复；缺失只表示"尚未迁移" |

分成两个文件是刻意的结构约束：索引的整库重建路径（`RebuildSchemaAsync` 直接 DROP 表）与"缺失即初始化"只允许作用于派生数据，业务表不能出现在任何可能被清空的语句里。

### 4.3 版本 1 schema（已冻结）

`SessionStoreDatabase.CurrentSchemaVersion = 1`，全部表 `WITHOUT ROWID`。

| 表 | 字段 | 约束与索引 | 现来源（迁移输入） |
| --- | --- | --- | --- |
| `store_meta` | `key`, `value` | 主键 `key`；键 `schema_version`／`store_identity`／`created_utc` | 新建 |
| `project` | `id`, `name`, `root_path`, `root_key`, `revision`, `is_deleting`, `created_utc`, `updated_utc` | CHECK：`id` 长 32、`name` 1–200、`root_path`／`root_key` 1–2048、`revision ≥ 1`、`is_deleting ∈ {0,1}`；`ux_project_root_key`（UNIQUE，平台比较键） | `projects.json` |
| `session_placement` | `session_id`, `project_id`（可空）, `updated_utc` | CHECK：`session_id` 长 32；外键 `project_id → project(id) ON DELETE RESTRICT`；`ix_session_placement_project` | JSONL `project-binding`／`project-assignment`／`project-unassignment` |
| `archived_session` | `session_id` | 主键；CHECK 长 32 | `archived-sessions.json`（集合） |
| `pinned_session` | `session_id`, `position` | 主键；CHECK 长 32、`position ≥ 0` | `pinned-items.json` `pinnedSessionIds`（有序） |
| `pinned_project` | `project_id`, `position` | 主键；CHECK 长 32、`position ≥ 0` | `pinned-items.json` `pinnedProjectIds`（有序） |
| `project_order` | `project_id`, `position` | 主键；CHECK 长 32、`position ≥ 0` | `desktop-sidebar.json` `stores[].manualProjectOrder` |
| `session_order` | `session_id`, `position` | 主键；CHECK 长 32、`position ≥ 0` | `desktop-sidebar.json` `stores[].manualSessionOrder` |
| `collapsed_group` | `group_key` | 主键 | `desktop-sidebar.json` `stores[].collapsedGroupKeys` |

已冻结的语义决定：

- **归属用行存在性表达三态**：`session_placement` 有行且 `project_id` 非空 = 已归属；有行且 `project_id` 为空 = 明确无项目；整行缺失 = 尚未迁移（对应 `PendingInitialization`）。缺行不能被读成"无项目"。
- **归属外键 RESTRICT**：项目行删除必须先清空其归属行，把"目录册删除标记 → 解绑 → 最终移除"的顺序变成数据库不变量，而不是调用方约定。
- **顺序表不建唯一约束**：`position` 允许并列，排序按 `(position, item_id)` 全序确定；SQLite 的唯一约束无法延迟到提交时检查，唯一化只会让整体重写在中间状态失败。
- **数量预算不进数据库**：项目 ≤1000、归档／置顶 ≤10000 等上限无法用 CHECK 表达，仍由调用方校验。
- **列类型由 `typeof()` 守卫**：SQLite 的 `INTEGER` 只是亲和性而非强类型，非数字文本会原样存成 TEXT，而 TEXT 在比较中恒大于数值，`revision >= 1` 之类的检查会被绕过。因此每个数值列同时约束 `typeof(col) = 'integer'`，每个文本列约束 `typeof(col) = 'text'`（可空列用 `IS NULL OR typeof(...) = 'text'`）。
- **时间与 ID 口径**：时间统一 Unix 毫秒；项目与会话 ID 统一 32 位小写十六进制（`CHECK` 只强制长度 32，十六进制与大小写仍由调用方保证）。
- **身份与来源分离**：`store_identity` 是随机 32 位十六进制，用于区分"已初始化的业务库"与被替换的同名文件；`created_utc` 记录建库时间。

### 4.4 schema 版本管理、升级与失败处理

版本号保存在 `store_meta.schema_version`（不依赖 `PRAGMA user_version`）。每个版本的迁移在**单个事务**内执行本版本语句，并在同一事务内写入 `store_identity`／`created_utc`（仅缺失时）与版本号，避免"表建好但版本没写"或相反的半迁移；迁移编号必须从 1 连续，编号断层在应用前抛错而不是半途而废。

每个版本都声明自己**必须具备的表与显式索引**，可用性与迁移都按它核对，而不是只看版本号或表名：

- 可用性：表、显式索引与元数据键（`schema_version`／`store_identity`／`created_utc`）必须齐全；缺索引意味着唯一性与查询约束已经失效，与缺表一样按损坏报告。
- 迁移前：该版本声明的表与索引必须齐全，缺少即拒绝——迁移脚本用 `CREATE TABLE IF NOT EXISTS`，一旦放行就会把缺失的表补成空表并写入新版本号，把数据丢失伪装成升级成功。版本 0 的已知结构是空库，出现任何表都表示来源不明而拒绝。多出的对象既不清空，也不阻止迁移与读取，由后续版本显式纳管。
- 迁移后：结构核对与版本号写入都在**同一事务内**完成；不符即回滚整个迁移（含 DDL 与版本号），不会留下"版本已推进但结构不完整"的库。

| 观测 | 状态 | `InitializeAsync` | `UpgradeAsync` |
| --- | --- | --- | --- |
| 路径不存在 | `Missing` | 建库到当前版本 | 拒绝：业务数据只能备份恢复或后续阶段导入 |
| 路径被目录或链接占用 | `Damaged` | 拒绝 | 拒绝 |
| 路径无法判定（权限／IO） | `Failed` | 拒绝 | 拒绝 |
| 有文件、无任何用户表 | `NeedsSchema` | 建库到当前版本 | 拒绝，指引改用建库 |
| 有表但无 `store_meta` | `Damaged` | 拒绝 | 拒绝 |
| `store_meta` 无版本行或非整数 | `Damaged` | 拒绝 | 拒绝 |
| 版本 < 当前 | `NeedsUpgrade` | 拒绝，指引改用升级 | 结构包含该版本声明的全部表与索引时逐版本就地迁移；缺少或该版本无已知结构时**拒绝** |
| 版本 = 当前但缺冻结表／索引／元数据键 | `Damaged` | 拒绝 | 拒绝 |
| 版本 > 当前 | `Unsupported` | 拒绝，绝不降级 | 拒绝，绝不降级 |
| 非 SQLite 文件（`SQLITE_NOTADB`）或 `SQLITE_CORRUPT` | `Damaged` | 拒绝并保持文件原样 | 拒绝并保持文件原样 |
| 锁等待耗尽、只读、权限、IO 失败 | `Failed` | 拒绝 | 拒绝 |
| 可用 | `Available` | 无操作（不重写库身份） | 无操作 |

探测结论必须区分"确定不存在"、"暂时读不了"和"确实损坏"：`File.Exists` 在权限或路径错误时同样返回 false，不能用它判缺失，否则不可读的业务库会被报成尚未迁移并诱导首次导入；只有 SQLite 明确报告 `SQLITE_NOTADB`（26）或 `SQLITE_CORRUPT`（11）才算损坏，其余运行期失败按暂时不可用报告，避免把锁冲突误导成"去恢复备份"。

连接与一致性：每次操作独立连接、禁用连接池、私有缓存；`journal_mode=WAL`、`synchronous=FULL`、`foreign_keys=ON`、`busy_timeout` 与命令超时同为 5 秒。可用性探测只设置锁等待，不切换 journal 模式、不建表、不写元数据，因此只读检查不改动业务文件。`synchronous`、缓存模式与外键这三处**有意不同于索引库**（索引为 `NORMAL`／共享缓存／无外键）：索引丢数据可重建，业务数据只能靠备份，写失败必须可见。

### 4.5 备份／恢复边界

- 需要备份：`session-store.db`。WAL 模式下还需同一时刻的 `-wal` 内容，或先 checkpoint 再复制；S3 不实现备份入口。
- 可丢弃：`session-index.db` 及其 `-wal`／`-shm`。恢复业务库后删除索引文件，由既有对账从 JSONL 重新派生摘要与归档投影，不丢组织状态。
- 恢复顺序：停止写入方 → 恢复业务库 → 删除索引库 → 启动后对账。S3 不实现该流程，仅冻结边界。
- 仍未具备：导出／导入、`integrity_check`、损坏文件的自动修复、升级前自动备份。

### 4.6 为什么不能直接复用索引重建机制

索引库的下列既有假设对业务表都是数据丢失路径，因此 S3 既没有把业务表放进 `session-index.db`，也没有复用其生命周期：

- 版本变旧即 `DROP TABLE` 整库重建（`SessionIndexStore.RebuildSchemaAsync`）；
- 文件缺失或无 schema 时自动 `InitializeAsync`，读路径的 `ReadWriteCreate` 会先建出空库；
- 对账把"枚举不到的日志"对应的行无条件删除（孤儿清理），无宽限期与墓碑；
- 索引写失败只记 `Trace`／`LastFailure`，不阻断、不重试；
- 3 秒有界等待、无退避重试、`synchronous=NORMAL`。

### 4.7 JSONL 标题事件与会话事实边界

- `session-title` 记录载荷为 `tinyharness.session-title/1`，字段 `schema`／`name`／`pending`，全部必需；`Validate()` 强制 `pending == (name is null)`，名称经 `Normalize()`（压缩空白、去引号与末尾标点、最多 36 个 Unicode 标量）。
- 写入点唯一（`ChatSessionHost.Title`）：待重试标记可重复追加，命名结果只写一次且迟到结果不覆盖。日志因此**允许多条标题记录**，完整恢复取最后一条。
- 标题事件与归属维护同属元数据：**不推进对话活动时间、不增加回合数**；提交时间与提交序号仍随批次推进。纯标题批次会刷新 `updated_utc` 与索引来源标记，但不得改变排序所依赖的活动时间。
- 摘要投影对 `name = null` 的记录**不覆盖**已保存名称，与完整恢复的"最后一条胜"存在语义分叉（见 4.9 第 3 项）。
- 导入或重建必须原样保留已有标题事件：不调用模型、不丢弃标题、不补写重复事件。S3 未实现导入。

### 4.8 桌面应用偏好边界

继续留在 `desktop-sidebar.json`、**不进入** store 业务表：`ShowArchivedSessionDeletionPrompt`、`ShowProjectDeletionPrompt`、`IsProjectMode`、`SortProjectsByActivity`、`CollapsedCategoryKeys`（全局固定分类折叠）。它们与 store 无关，复制进每个 store 会在多 store 并存时漂移，并破坏"偏好文件损坏时禁止保存、回退默认值"的既有行为。

只有每 store 的组织状态（`manualProjectOrder`、`manualSessionOrder`、`collapsedGroupKeys`）与 legacy 迁移字段（`pinnedProjectOrder`、`pinnedSessionIds`，迁入 Core 后清空）属于后续迁移对象。主题与窗口目前没有持久化，不属于本阶段。

### 4.9 S4 输入事项的处理结果（2026-11-15）

1. `Missing` 的两种含义已由 store 级迁移标记区分：无标记＝首次迁移（旧文件仍是权威），标记完成而库缺失＝迁移后丢失、
   只能备份恢复（第 5.1 节）。
2. 备份／恢复入口已实现（SQLite 在线备份、完整性校验、身份匹配、恢复前保留原库副本、删除派生索引），见第 5.4 节；
   `integrity_check`、恢复与回退策略一并落地。升级前自动备份仍未实现。
3. 标题投影与完整恢复的 `pending` 语义已统一为"最后一条非空名称胜出"（第 5.6 节），两侧均有测试覆盖。
4. `session_placement` 的"无行＝尚未初始化"与"空项目＝明确无项目"已由读写路径与测试区分（第 5.3 节）。
5. S5 所需的删除墓碑／可恢复删除状态仍不在 v1 schema，须走版本 2 迁移；未在本阶段扩大 schema。（2026-11-17 已由 S5 的版本 2 实现，见第 4.12 节。）
6. 迁移互斥由 store 级租约实现，来源指纹复核覆盖旧写者在导入期间的写入，切换后以警告报告旧文件被改写（第 5.5 节）；
   业务库写失败的重试／排队策略与双进程写者仲裁仍是后续事项。
7. 数量预算、名称与路径规范化的调用方校验保持不变；`CHECK` 边界未变。
8. 摘要表仍留在 `session-index.db`，未并入业务库。
9. `UpgradeAsync` 的跨版本成功路径仍缺真实旧结构夹具；本阶段没有新增 schema 版本。（2026-11-17：S5 的版本 2 升级已补真实 v1→v2 夹具，见第 4.12 节。）
10. AOT 与离线 smoke：本阶段已接入真实调用方，CLI/Desktop 发布产物需重做业务库建库、导入、读写与重开的 smoke 验证（见第 7 节）。

### 4.10 2026-11-14 后续改动纳入边界

- 自动命名已把保存标题作为 `session-title` JSONL 事件持久化；SQLite 列表标题仍是可重建摘要。导入应保留已有事件和标题，不调用模型、不追加重复事件；纯标题更新不得推进对话活动时间或回合数。
- 归档会话删除确认选项属于桌面 UI 偏好，继续由现有 `SidebarPreferencesStore` 管理；不要把它并入每个 store 的业务 schema。
- 当前项目目标框架和 `AGENTS.md` 规则均为 `net11.0`／.NET 11；S4/S5 的 NativeAOT 与离线 smoke 仍须按该目标框架验证。

### 4.11 本轮代码落地

- 新增 `TinyHarness.Core/Services/Persistence/SessionStoreDatabase.cs`：冻结 schema 常量与表清单、`PathFor`／`Exists`、可用性状态机、`InitializeAsync`（建库）、`UpgradeAsync`（就地升级）、版本化迁移与连接配置。没有生产调用方。
- 新增 `TinyHarness.Core/Exceptions/SessionStoreDatabaseException.cs`。
- 新增测试 `TinyHarness.Tests/SessionStoreDatabaseTests.cs`（25 项）与 `TinyHarness.Tests/SessionStorageBoundaryTests.cs`（3 项）。
- 未修改任何既有生产类型、配置文件或依赖；未切换读写权威；未导入真实数据。

### 4.12 版本 2 schema（S5，2026-11-17 冻结）

`CurrentSchemaVersion = 2`。版本 2 只新增会话删除墓碑，不改动版本 1 的任何列、约束与索引：

| 表 | 字段 | 约束与索引 | 用途 |
| --- | --- | --- | --- |
| `session_tombstone` | `session_id`, `state`, `requested_utc`, `updated_utc` | 主键 `session_id`；CHECK：长 32、`state ∈ {pending, deleted}`、时间为整数；`ix_session_tombstone_state`（`state, session_id`） | 先于日志删除提交删除意图，跨文件崩溃窗口可续作；墓碑永久保留，阻止同一 ID 的日志复活 |

- **墓碑先于日志**：业务库事务与日志文件不能共享事务，因此删除协议不宣称跨文件原子。`pending` 表示删除意图已提交、日志或投影尚待收尾；`deleted` 表示已清理完成。两种状态都使该会话对所有读取路径不可见。
- **墓碑不删除**：日志可能因备份恢复、文件拷贝或旧版本程序重新出现；保留行是不让它复活成列表条目的唯一持久标记。行数上限由历史删除会话数决定，不随活跃数据增长。
- **升级**：迁移 2 用 `CREATE TABLE/INDEX IF NOT EXISTS`，形状为版本 1 全部对象加墓碑表与状态索引；迁移前要求版本 1 结构齐全，升级后同一事务内核对并写版本号。S3 缺失的"跨版本成功迁移"夹具已由"把版本 2 库删掉墓碑对象、版本号退回 1，再升级"的真实 v1 结构覆盖。
- **升级入口唯一**：`SessionStoreDatabase.UpgradeAsync` 不直接对外，只由显式 `session store migrate`（`SessionStoreMigration.MigrateAsync`）在迁移租约内调用，且两条路径都覆盖：迁移标记**已完整**的 store 就地升级已迁移业务库（本次不导入任何批次，结果以 `SchemaUpgradedFrom` 报告来源版本）；**未完成**的 store 在身份核对与批次导入之前先升级，避免按新结构导入旧库或停在半途。`InspectAsync` 对版本更低的库报告 `DatabaseNeedsUpgrade`，`SessionStoreBusinessConnection.OpenVerifiedAsync` 把 `NeedsUpgrade` 映射为 `Unsupported` 拒绝，因此升级前的生产读写一律失败而不是读半套结构。
- **备份边界不变**：版本 1 期间生成的备份在版本 2 程序中按 `NeedsUpgrade` 报告；S4 未对真实用户 store 迁移，因此不存在需要兼容的 v1 真实备份。恢复路径不接受需要升级的备份，仍然只能由备份恢复旧版本库存。

## 5. S4：分批导入、切换与兼容

已于 2026-11-15 实施。生产权威按持久化迁移标记切换；迁移只能由显式入口触发，**不自动迁移真实用户 store**。
本阶段只在隔离 fixture 与测试副本上执行迁移，未对真实用户数据运行，也未删除任何旧文件或用户备份。

### 5.1 迁移标记与状态

业务库之外新增迁移标记 `session-store-migration.json`（协议 `tinyharness.session-store-migration/1`），
与业务库内的 `store_meta.store_identity`／`migration_id` 双向绑定，用于区分六种情形：

| 观测 | 状态 | 生产读写 |
| --- | --- | --- |
| 无标记 | `NotStarted` | 旧文件仍是权威（未迁移 store 的兼容期） |
| 有标记、仍有批次未切换 | `InProgress` | 拒绝，必须继续迁移，不静默回退旧文件 |
| 全部批次已切换且业务库可用、身份一致 | `Completed` | 业务库是权威 |
| 标记完整但业务库缺失 | `DatabaseMissing` | 拒绝，只能由业务备份恢复；不得重新导入旧来源 |
| 业务库身份与标记记录不一致 | `DatabaseReplaced` | 拒绝，视为同名库被替换 |
| 标记无法严格解析 | `MarkerDamaged` | 拒绝并保留文件，交人工恢复 |

标记记录迁移标识、库身份、开始／更新时间、各批切换状态、各批来源指纹与最近一次可重试失败说明；
写入使用同目录临时文件加原子替换，读取严格校验协议与字段形态。首次迁移先写开始标记再建库，
使"标记已写、库未建"按可续作处理，而不是误判为首次迁移。

### 5.2 分批导入协议

固定顺序组织状态（归档、置顶、普通/置顶顺序、折叠分组）→ 项目目录册 → 会话当前归属：

- 每批先完整读取并校验旧来源快照、计算来源指纹，随后在**单个事务**内用该快照整体替换该批的业务表，
  核对导入行数后提交，最后把该批标记为已切换并写入来源指纹。任何批次失败只回滚本批事务，之前已切换批次不再重做。
- 幂等：重试重新导入仍待切换的批次；已切换批次不会因重复运行而重复写入或覆盖新权威下的修改。
- 崩溃窗口：提交成功但标记未写时该批仍待切换，重试重新导入（旧来源仍是该批权威）；
  标记写完但库未建、库建好但身份未记录等窗口分别按"继续建库"和"接管已建库并写入迁移标识"处理。
- 取消：提交前取消回滚事务、提交后取消保持该批待切换，均不破坏已切换批次；不宣称 SQLite、JSON 文件与标记
  具有跨文件单事务原子性。
- 来源变化：组织状态与项目目录册在提交后再次核对指纹，命中说明旧程序在导入期间写入，该批保持待切换并失败重试；
  会话归属按文件名、长度与修改时间摘要记录指纹，更细的重扫由检查入口提示。

### 5.3 生产权威切换与实际调用方

- 新增 `SessionStoreAuthority`：按标记解析 `Legacy`／`Migrated`／`Blocked`；`Blocked` 一律以类型化异常拒绝，
  绝不静默回退旧文件。旧文件读写实现只保留给尚未迁移 store 与导入器；已切换 store 的生产调用方不再触碰旧文件。
- 组织状态：`SessionStoreOrganizationStore` 承担归档、置顶、普通/置顶顺序与折叠分组的事务化读写；
  `SessionStorePinStore`／`SessionStoreArchiveStore` 在迁移后转调业务库并保持原有领域异常与模型，
  `SessionStoreOperations` 的快照、串行门与索引投影维护语义不变。
- 项目目录册：`SessionStoreProjectCatalogStore` 承担创建复用、改名修订号、删除标记与最终移除；
  `project.root_key` 唯一索引与 `session_placement.project_id` 的 `ON DELETE RESTRICT` 外键把
  "项目删除必须先解绑"变成数据库不变量。`ProjectStore` 按迁移状态在文件与业务库之间分派，
  `ProjectCatalogMonitor` 在业务库模式下每轮直接读取业务库。
- 会话当前归属：`SessionStorePlacementStore` 用行存在性表达三态（有行有项目＝已归属、有行空项目＝明确无项目、
  缺行＝尚未初始化）。`SessionStoreReader` 严格恢复后叠加业务库归属，历史 JSONL 归属不再覆盖切换后关系；
  `SessionSummaryBuilder` 与降级摘要扫描同样叠加，索引新增归属投影刷新（未变日志零正文扫描时也能纠正历史归属）。
- 初始化：`SessionProjectInitializer` 迁移后只写业务库（项目 + 归属行，插入时仅缺行才写，后到者不覆盖先到者），
  不再追加 JSONL 归属、不再获取 writer 锁；未绑定会话的日志正文不被改写。解绑：`SessionProjectUnbinder`
  迁移后按项目批量把归属行改为明确无项目，不读不写日志、不抢 writer 锁，仍保留可重试与删除标记语义。
- 新会话与恢复续聊：`SessionStoreWriter` 迁移后首次提交把归属写入业务库并**不再向 JSONL 追加 project-binding /
  project-assignment**；续聊的待初始化会话由 `SessionProjectBinding.BindRestoredAsync` 写业务库归属行，
  不再追加日志。等级/权限/执行目录语义不变。
- 索引：`session-index.db` 仍是可重建派生投影；项目删除/会话删除在业务库模式下同时清理归属行、置顶与顺序引用，
  失败以既有 issues 列表报告，不阻断已提交事实。

### 5.4 备份、恢复与回退

- 备份使用 SQLite 在线备份 API（读连接、`BackupDatabase`），WAL 内容随备份一致，不复制可能撕裂的 `-wal` 文件；
  备份目录内固定文件名 `session-store.db`，已存在则拒绝覆盖，绝不删除用户已有备份。
- 校验：完整性检查、schema 可用性、版本与库身份；恢复要求标记 `Completed` 且备份身份与标记记录一致，
  否则拒绝（备份属于别的 store / 同名库被替换需显式恢复匹配身份的备份）。
- 恢复：先校验备份，再持有迁移租约复制提交（同目录临时文件 + 替换），原业务库保留为
  `session-store.db.pre-restore-<unixms>` 副本；随后删除派生索引 `session-index.db`、`-wal`、`-shm`，
  由既有对账重建摘要与归档投影。旧 JSON 文件与用户备份保持原样，不自动删除。
- 回退策略：迁移完成即不可通过"重新导入旧文件"回退；业务库写入后也没有把新数据合并回旧 JSON/JSONL 的路径。
  唯一受支持的回退是按备份恢复业务库；若要回到旧版本程序，必须先恢复迁移前旧文件备份并显式移除业务库与迁移标记，
  该操作不在本阶段自动执行。
- 错误分级：缺失、损坏、版本不受支持、迁移进行中、库被替换、锁等待、权限、只读与 IO 失败分别报告，
  不再混成"缺失"或"损坏"。

### 5.5 旧写者停止与迁移互斥

- 迁移、备份与恢复共享 store 级迁移租约（独占句柄），并发尝试有界等待后以 `Locked` 失败；进程退出由操作系统释放。
- 旧程序不识别该租约，因此协议不假设旧写者会遵守新锁：组织状态与项目目录册在切换前复核来源指纹，
  切换后由检查入口比较旧文件指纹并在 `Warnings` 中报告"旧文件在切换后被改写"；生产读取只认业务库，
  不会被旧写入覆盖。仍明确要求升级迁移时关闭旧版本程序，不宣称新旧程序可同时写同一 store。

### 5.6 标题、日志与桌面偏好

- 导入只读取旧来源，不调用模型、不重写 JSONL、不重复追加标题事件；标题事件与归属维护不推进活动时间或回合数。
- 待生成标题语义在 S4 明确为"最后一条非空名称胜出"：完整恢复与列表摘要都忽略晚到的待生成标记，
  已保存名称不会被清除；不通过改写旧日志解决分叉。
- 桌面应用偏好（删除确认、全局分类折叠、项目模式、项目排序）继续留在 `desktop-sidebar.json`；
  只有每 store 的组织状态（手动项目/会话顺序、折叠分组）在迁移后由业务库承载。桌面保存时对已迁移 store
  投影为空组织字段并保留其他偏好，不静默清空无关设置。
- CLI 新增显式的 `session store status|migrate|backup|verify|restore` 命令；离线 smoke 覆盖
  建库、导入、读写、重开与备份恢复。

### 5.7 未完成边界

- 本阶段不实施 S5 的会话删除墓碑与删除流程简化；现有删除功能在切换后保持正确，但仍是"日志 + 业务库"两步操作，
  失败可重试、不宣称跨文件原子。
- 真实用户 store 迁移、真实 GUI 验收、双进程实际争用与旧版本程序并发实测均未执行，属于用户显式授权的后续操作。
- 已删除/移除旧文件存储的完整代码仍保留：未迁移 store 需要兼容期路径；完全移除需先确定强制迁移策略。

### 5.8 S4 静态审查修复（2026-11-17）

对 S4 的 42 个文件做静态审查发现 5 项缺陷，已全部修复；随后对备份恢复的 WAL 处理追加 1 项 P1 修复：

1. **运行中宿主解绑绕开业务库（P1）**：`ChatSessionHost.UnassignProjectAsync` 原先一律追加
   `project-unassignment` JSONL 记录，而迁移后的 writer 明确拒绝该记录，项目包含已打开会话时删除会停在删除标记。
   现在迁移后直接改写业务库归属行为明确无项目，并尽力刷新索引投影；未迁移 store 保持原追加语义。
   `ISessionStoreWriter` 增加默认实现的 `StoreDirectory`（由 `SnapshotPath` 父目录推导），使宿主无需额外构造参数即可
   解析迁移权威。
2. **阻塞状态回退历史归属（P1）**：`SessionPlacementOverlay.LoadAsync`／`ApplyAsync` 原用 `IsMigratedAsync`，
   把 `InProgress` 与 `MarkerDamaged` 一并当作"未迁移"并返回日志推导值。现在改用 `ResolveAsync` +
   `ThrowIfBlocked`，只有真正的 `Legacy` 才跳过叠加。迁移导入器改走新增的
   `SessionStoreReader.ReadLogProjectionAsync`（internal，只读日志自身严格投影），避免"导入中"的阻塞反过来拒绝导入器。
   降级摘要扫描与索引更新器同样改用该入口。
3. **外键损坏备份先覆盖再报错（P1）**：`VerifyAsync` 只做 `integrity_check`，而它不检查外键引用；恢复因此可能先用
   坏备份替换业务库再失败。现在 `VerifyAsync` 增加 `foreign_key_check`；恢复在替换前对**实际待提交的临时副本**
   再做完整性、外键与 schema 版本校验；替换后的复核仍失败时把本次恢复保存的 `pre-restore` 副本放回原文件名
   （替换本身不可回滚，提交前校验是主防线，回滚是兜底）。回滚只接受 `PreserveExistingDatabaseAsync` 返回的精确
   副本路径：目录里更早的同名前缀副本不会被自动选用，恢复前业务库缺失（本次未生成副本）时明确拒绝回滚，避免用
   历史副本覆盖刚恢复的数据库。
4. **索引更新未叠加业务库归属（P2）**：`SessionIndexUpdater` 的整份扫描分支调用三参数 `BuildAsync`（恒传 null），
   增量分支也没有叠加，迁移后新会话首次索引更新会被记成 `PendingInitialization`。现在两个分支都叠加：
   整份扫描经 `SessionPlacementOverlay.LoadAsync` 传入归属快照，增量分支用 `ApplyAsync` 覆盖推进后的摘要。
   `SessionIndexReconciler` 的重建分支同样提前叠加，避免同一轮内先写历史归属再由循环后同步纠正。
5. **续聊初始化覆盖并发归属（P2）**：`SessionProjectBinding.BindRestoredAsync` 原用 `SetProjectAsync`（无条件 UPSERT），
   恢复快照之后写入的归属（含显式 `NoProject`）会被覆盖。现在改用 `TryInitializeProjectAsync`（仅缺行时插入），
   随后读回最终归属同步内存会话与索引投影；新增 `ChatSession.MarkNoProject` 供读回 `NoProject` 时镜像内存状态。
6. **业务库 WAL／SHM 清理失败被当作成功（P1）**：提交与回滚此前都用 `TryDeleteSiblingFiles` 删除业务库同目录的
   `-wal`／`-shm`，该方法吞掉 `IOException`／`UnauthorizedAccessException` 后照常返回，外层因此在旧 WAL 仍在
   目录里时报告"恢复完成"或"原库已放回"。旧 WAL 属于数据库持久状态，后续打开会把它按新主文件重放并立即破坏
   恢复结果，与可重建的索引派生文件不是同一类清理。现在改为**先隔离后替换**：`QuarantineJournalFiles` 把旧
   `-wal`／`-shm` 改名到非日志文件名（SQLite 不会再应用它们），失败则以 `SessionStoreBackupException` 中止，
   已移开的日志先放回；替换或回滚成功后才由 `DeleteQuarantinedJournals` 删除隔离副本，替换/放回本身失败时由
   `RestoreQuarantinedJournals` 把日志放回原文件名，避免"主文件保留而日志被丢掉"的失配一代。
   选择隔离而非直接删除，是因为日志可能仍持有未 checkpoint 的已提交内容，替换若随后失败，先删除等于在保留旧主
   文件的同时丢掉它的数据。`TryDeleteSiblingFiles` 收窄为临时文件与派生索引的尽力清理，文档注释写明业务库日志
   不在此范围。
7. **WAL 隔离后进程退出缺少恢复机制（P1）**：上一项的隔离信息只保存在内存里。原 WAL 含未 checkpoint 的已提交
   内容时（`PreserveExistingDatabaseAsync` 允许 checkpoint 失败后继续），进程若在日志改名之后、主库替换之前被
   终止，旧主库就失去了原日志名下的 WAL：迁移标记仍是 `Completed`、租约随退出释放，而开库入口不识别
   `.stale-*.tmp`，后续读取会拿到缺少最新数据的旧主库。现在新增 `SessionStoreRestoreRecovery`：在隔离**之前**
   把待提交副本与计划隔离的全部日志文件名原子写入 `session-store-restore-recovery.json`（只存 store 目录内的
   文件名，逐个经 `EnsureStoreLeaf` 校验，已存在的记录拒绝覆盖）。`SessionStoreDatabase.GetAvailabilityAsync`
   在探测前先补完中断的恢复，恢复不了就以 `Failed` 拒绝打开；`MigrateAsync`／`CreateAsync`／`RestoreAsync`
   在持有租约后先调用 `RecoverLockedAsync`，并用 `HoldSettled` 登记，使编排内部的打开既不重入租约也不会把
   自己的窗口当成残留。判据是"待提交副本是否还在"：`File.Replace`／`File.Move` 成功会消耗源文件，因此副本仍在
   表示替换未发生（隔离日志放回原日志名），副本已不在表示替换已提交（隔离日志属于上一代，必须丢弃而不是重放）。
   备份校验通过 `SessionStoreDatabase(backupPath, false)` 构造，明确不把用户备份目录当作需要修复的业务库。
   恢复只在持有迁移租约时改文件，因此不与另一个进程正在执行的恢复交错；"隔离完成、替换未提交"的窗口用
   `QuarantineCommittedHookForTests` 接缝覆盖（生产路径始终为 null）。
8. **替换失败时忽略日志复原失败（P2）**：`RestoreQuarantinedJournals` 返回首个复原失败原因，但提交与回滚两处都
   直接丢弃并重新抛出替换异常；日志若也没移回，调用方只看到"替换失败"，不知道旧主库的 WAL 仍在隔离名下，也
   拿不到需要手工处理的路径。现在两处都合并报告原异常与复原失败、保留恢复记录使存储继续不可用，直到文件代际
   恢复一致；`SettleQuarantineFailureAsync` 负责"记录已落盘但隔离未完成"的收尾，避免把一次可以自愈的隔离失败
   呈现成永久不可用。复原或收尾也失败时，错误消息同时包含日志路径、隔离副本路径与记录路径。

验证（2026-11-17）：`TinyHarness.Core`／`TinyHarness.Desktop` Debug 构建 0 警告 0 错误；
新增 7 项回归测试（阻塞态公开读取拒绝 + 导入器原始读取可用、运行中宿主解绑只写业务库、外键损坏备份校验与恢复不覆盖、
迁移后索引更新两分支归属投影、续聊不覆盖并发归属、回滚只认本次副本、无本次副本时拒绝回滚）；定向回归 39/39 通过；
WAL 隔离另加 3 项（独占句柄持有旧 WAL 时恢复在替换前中止且不留临时副本、回滚报告失败且不覆盖刚恢复的库、
放回步骤失败时隔离日志回到原日志文件名），并用"把隔离失败改回吞掉"的临时改动验证前两项确实因此转红。
第 7、8 项另加 6 项：进程在隔离之后、替换之前被终止时下一次打开必须把隔离日志放回原日志名并清除记录、
进程在替换之后、记录清除之前被终止时必须丢弃上一代日志而不是重放、恢复记录损坏时业务库判为不可用且记录保留、
记录点名的隔离日志无法放回时报告两个失败并保留记录（解除占用后同一记录可自行收尾）、成功恢复后不残留记录与
隔离副本、待收尾记录必须先于文件叶子判断被处理（主文件缺失不得报成"尚未迁移"）；
并用"把记录写入移到隔离之后"与"把恢复移到叶子判断之后"的临时改动分别验证对应项确实因此转红。
`SessionStoreBackupTests` **25/25** 通过；持久化相关定向筛选 **306 通过／0 失败／3 跳过**；
`TinyHarness.Tests` 全量 **2287 通过／0 失败／10 跳过**（2297 总计；WAL 轮为 2281／0／10）。
本轮未改动 Desktop 代码，`TinyHarness.Desktop` Debug 构建 0 警告 0 错误，**未重跑** `TinyHarness.Desktop.Tests`
与 Desktop `--offline-smoke`（WAL 轮记录的全量结果为 420 通过／1 失败，唯一失败是既有偶发
`NetworkChangeDuringRunWaitsForTheNextRunBoundary`，单独重跑通过）。
CLI `win-x64` NativeAOT publish 成功且无 compiler／trimming／AOT 警告，仓库外隔离环境下的发布产物 `smoke`
PASS（exit 0，17 steps／16 tool executions，含 `session store: verified migration, read/write, reopen, backup and restore`，
即本轮改动的备份／恢复路径在 AOT 产物中真实执行）。
未对真实用户 store 执行迁移，未做真实 GUI、双进程并发恢复与真实断电验收。

#### 5.8.1 恢复记录文件角色校验（2026-11-17 复审追加）

第 7 项引入的记录只校验"名字是 store 目录内的普通文件名"，没有限制文件角色，因而结构合法的记录可以把任意目录内
文件交给 `Apply` 删除或移动：`pendingFileName` 写成 `session-store.db` 且日志列表为空时，替换判据落到"副本已不在"，
`Apply` 会把真实业务库当未提交副本删除；`quarantinedFileName` 同样可以指向会话日志。现在读取路径在改动任何文件之前
完成角色校验：操作必须是 `restore` 或 `rollback`；待提交副本必须形如 `session-store.db.<operation>-<32 位小写十六进制>.tmp`；
原日志只能是业务库的 `-wal`／`-shm`；隔离名必须是由对应原日志派生的 `<original>.stale-<32 位小写十六进制>.tmp`；
同一个名字不得出现两次（含与原日志重名），空值与缺失字段一律拒绝。写入路径调用同一校验并在落盘前中止，产名逻辑
合并为 `NewPendingCopyPath`／`NewQuarantinePath`，避免读写两侧命名漂移。违反任一条件按 `Damaged` 报告、保留记录、
业务库保持不可用。

验证（2026-11-17 复审）：新增 5 项测试——把业务库写成待提交副本、把会话日志写成隔离副本、五类语义非法形状
（未知操作／待提交副本为会话日志／原日志不是业务库日志／隔离名与前缀不符／同一名字重复）、`RecoverAsync` 在被拒绝时
不移动也不删除任何文件、写入端拒绝不符合替换窗口形状的待提交副本。`SessionStoreBackupTests` **30/30** 通过；
备份／迁移／权威／数据库／边界定向筛选 **95/95** 通过；`TinyHarness.Core` Debug 构建 0 警告 0 错误。把读取路径的
角色校验临时去掉后上述前 4 项均转红，恢复后重新转绿。
**验证边界**：既有"进程终止"用例在**同一测试进程内**用 `OperationCanceledException` 与测试接缝模拟，不是真实子进程
被终止；WAL 夹具是**文本文件**而非真实 SQLite WAL，因此未覆盖真实进程终止与 SQLite WAL 重放行为。本轮未重跑
全量 `TinyHarness.Tests`，未执行 NativeAOT publish 与产物 smoke。真实用户 store、双进程并发与断电现场验收仍待授权。

## 6. S5：迁移后简化删除与验收

已于 2026-11-17 实施，依赖 S4 与业务库版本 2（删除墓碑，见第 4.12 节）。本阶段只改迁移完成后的删除路径；
未迁移 store 保持原有"日志 + 旧文件"语义，真实用户 store 迁移仍只能由显式入口触发。

### 6.1 会话删除协议

- **墓碑先行**：`SessionStoreOperations.DeleteSessionPermanentlyAsync` 在迁移后先提交 `session_tombstone(pending)`，
  再在既有 writer 锁下删除日志（`SessionStoreDeletion.TryDeleteResolvedAsync`），随后清理归档、置顶、索引、
  归属行与手工会话顺序，最后把墓碑置为 `deleted`。重复删除对 `deleted` 墓碑按幂等完成返回。
- **只有清理确实完成才置为已删除**：`CleanupDeletedSessionProjectionsAsync` 把单项失败转为 `SessionStoreCleanupIssue`
  而不抛出，因此两条删除路径都以"本次是否新增清理问题"决定墓碑状态——有任何残留时墓碑保持 `pending`，返回
  清理问题，并由恢复入口在下次准备 store 时重试。否则一次暂时的置顶、归属、顺序或索引失败会让墓碑退出
  待收尾扫描，残留投影再无补做路径。
- **可续作**：日志被 writer 占用或进程在任一阶段退出时，墓碑停留在 `pending`。`RecoverPendingSessionDeletionsAsync`
  按请求时间遍历待收尾墓碑，尽力删除残留日志、清理投影并置为 `deleted`；日志仍被占用时保留待收尾并作为
  `SessionDeletion` 清理问题返回，不覆盖正在写入的会话。桌面在准备每个 store 时调用该入口补完中断删除。
- **不复活**：读取路径一律以墓碑优先于日志存在性——`SessionStoreReader.ReadAsync` 拒绝已删除会话、
  `ListAsync`／`SessionListProjection.ScanSummariesAsync`／`ListSummariesAsync`／`ListRowsAsync` 过滤墓碑 ID、
  `SessionIndexReconciler` 删除残留索引行且不再重建、`SessionProjectInitializer` 不为墓碑会话补建归属。
  墓碑查询失败时读取按原有降级路径继续并记诊断，不把整个列表拒绝。
- **日志已消失的墓碑索引行由孤儿清理处理**：对账的墓碑分支只在枚举到对应日志时执行，因此"日志已删除、索引
  清理尚未成功"的中断窗口里，残留行不在来源集合内，只能由孤儿清理移除。孤儿清理只排除本轮已按墓碑删掉的
  行，不排除全部墓碑 ID——否则这条残留行两条路径都不会处理，之后每轮对账都会保留它。
- **边界**：墓碑与日志不在同一事务，不宣称跨文件原子；墓碑行永久保留，用于阻止备份恢复、文件拷贝或旧程序
  写回的日志复活。writer 锁、宿主释放与"运行中会话不可删除"语义不变。

### 6.2 迁移后的项目删除

- `SessionStoreProjectCatalogStore.BeginDeletionAndUnassignAsync` 在单个事务内：标记 `is_deleting`、读出仍归属
  该项目的会话 ID、把这些 `session_placement` 改为显式无项目、删除 `pinned_project` 与 `project_order` 引用。
  迁移后不再扫描会话日志、不再追加 `project-unassignment` 记录。
- `SessionStoreOperations.DeleteProjectAsync` 的迁移分支复用既有 `ISessionProjectDeletionGateway` 六阶段契约
  （标记 → 草稿页 → 运行宿主 → 行投影 → 完成前准备 → 完成），并在 `UnassignmentAppliedAsync` 中把事务内实际
  解绑的会话 ID 以 `Unassigned` 结果交给宿主——只传空结果会让没有打开 runtime 的现存侧栏行继续显示已删除的
  项目，直到后续刷新才被纠正。事务后重新核对归属；并发写回的项目归属按解绑不完整中止并保留删除标记，最终
  移除仍由 `ON DELETE RESTRICT` 外键兜底。折叠分组键属于界面层，继续由宿主钩子按项目清理。
- 未迁移 store 的目录册、日志解绑与旧侧栏清理路径完全不变。

### 6.3 未完成／未验证边界

- 双进程真实争用、在途写入与断电现场未实测：墓碑续作只由同一测试进程内的状态构造覆盖（writer 占用、
  墓碑已提交而日志仍在），不含真实子进程终止与 SQLite WAL 重放。
- 真实 GUI 的删除交互与真实用户 store 迁移后删除未验收；本轮 NativeAOT publish 与两端产物 smoke 已执行
  （见第 7 节），但 smoke 本身仍不含会话删除步骤。
- 已删除会话的墓碑不提供用户可见的恢复／撤销：本阶段"可恢复"指删除操作本身可续作，不是恢复已删会话。
- 备份恢复不做墓碑合并：恢复的是整库快照，墓碑随快照回到备份时刻的状态。
- 真实用户库从版本 1 升到版本 2 的升级现场未实测：升级夹具是把版本 2 库退回版本 1 结构构造的，覆盖了
  结构与数据保留，但不含真实用户库的历史数据分布与耗时。

## 7. 本轮实施与验证记录

> 本节为截至 2026-11-11 的历史实施与验证快照。2026-11-14 的计划对账没有重跑其中的构建、测试、NativeAOT、smoke 或真实环境验收。

2026-11-17，S5 静态审查 4 项修复：

对 S5 的 15 个变更文件做静态审查发现 4 项缺陷，已全部修复：

1. **v1 业务库没有生产升级入口（P1）**：`CurrentSchemaVersion` 升到 2 后，已完成 S4 的 v1 库会被
   `OpenVerifiedAsync` 以 `NeedsUpgrade` 拒绝，而全仓生产代码没有调用 `UpgradeAsync`；再次执行显式
   `MigrateAsync` 也会在"已完成迁移"分支按当前版本核对身份后直接返回，无法升级。现在两条迁移路径都在
   持有租约的前提下就地升级：已完成的 store 在 `BuildAlreadyCompletedResultAsync` 升级已迁移业务库
   （不导入任何批次，`SessionStoreMigrationResult.SchemaUpgradedFrom` 报告来源版本），未完成的 store 在
   `EnsureDatabaseBindingAsync` 升级后继续导入，因此身份核对、导入与校验都按当前结构进行。`InspectAsync`
   新增 `SessionStoreMigrationStatus.DatabaseNeedsUpgrade`，不再把版本更低报成 `DatabaseDamaged`；升级前的
   生产读写仍由连接入口以 `Unsupported` 拒绝。
2. **清理失败仍标记完成，导致无法续作（P2）**：`CleanupDeletedSessionProjectionsAsync` 把单项失败转为 issues
   返回，而 `DeleteSessionPermanentlyAsync` 与 `RecoverPendingSessionDeletionsAsync` 都无条件
   `MarkDeletedAsync`。一旦归档、置顶、归属、顺序或索引清理暂时失败，墓碑便退出待收尾扫描，重复删除又按
   幂等直接返回，残留永久不再重试。现在两条路径都以"本次是否新增清理问题"决定墓碑状态：有残留时保持
   `pending` 并把问题返回给调用方，恢复入口下次准备 store 时继续重试。
3. **空解绑结果没有更新 Desktop 会话行（P2）**：迁移分支向 gateway 传 `SessionProjectUnassignmentResult([])`，
   而 Desktop 的 `ApplyProjectUnassignmentResult` 只更新结果中标为 `Unassigned` 的会话，因此没有打开 runtime
   的现存侧栏行仍保留旧 `ProjectId`，项目移除后在 `RebuildProjects` 中被归入"项目信息缺失"直到后续刷新。
   `SessionProjectDeletionStart` 增加 `UnassignedSessionIds`（事务内先读后改，保证名单与解绑一致），
   `DeleteMigratedProjectAsync` 把它们按 `Unassigned` 交给宿主。原 `RecordingDeletionGateway` 只检查回调顺序，
   新测试同时断言宿主收到的会话 ID 与"结果完整"。
4. **日志已消失的墓碑索引行被永久跳过（P2）**：`SessionIndexReconciler` 的墓碑删除分支只在枚举到对应日志时
   执行，孤儿清理又排除全部墓碑 ID，因此"日志已删除、索引清理尚未成功"的中断窗口里残留索引行两条路径都不
   处理，之后每轮对账都保留它。现在孤儿清理只排除本轮已按墓碑移除的行（`clearedByTombstone`），该残留行由
   孤儿清理删除；原有测试保留了日志，未覆盖这一窗口。

逐项反向验证（把修复改回旧行为后对应测试必须转红，恢复后重新转绿）：

| 修复 | 反向改动 | 结果 |
| --- | --- | --- |
| 第 1 项（已完成分支） | 删掉 `BuildAlreadyCompletedResultAsync` 内的升级块 | `Migrate_CompletedStoreWithOlderSchema_UpgradesInPlaceAndKeepsRows` 转红 |
| 第 2 项 | 去掉 `issues.Count == 0` 与 `issues.Count != before` 两处判断 | 两项墓碑清理测试同时转红 |
| 第 3 项 | 把 gateway 载荷改回空结果 | `ProjectDeletion_MigratedStore_ReportsUnassignedSessionIdsToHost` 转红 |
| 第 4 项 | 孤儿清理改回排除全部墓碑 ID | `Reconciliation_RemovesTombstonedRowWhoseLogIsAlreadyGone` 转红 |

验证（2026-11-17）：`TinyHarness.slnx` Debug 构建 **0 警告 0 错误**；`TinyHarness.Tests` 全量
**2306 通过／0 失败／10 跳过（2316 总计）**；`TinyHarness.Desktop.Tests` 全量 **421 通过／0 失败**。
新增 6 项测试：`Migrate_CompletedStoreWithOlderSchema_UpgradesInPlaceAndKeepsRows`（就地升级、保留项目／归属／
归档行、升级后可写墓碑、重复运行不再升级）、`Migrate_UnfinishedStoreWithOlderSchema_UpgradesBeforeResumingImport`
（旧结构续作先升级再导入）、`Delete_WhenProjectionCleanupFails_LeavesTombstonePendingForRetry`
（索引清理失败保留待收尾，恢复入口补齐后转为已删除）、`Recovery_WhenProjectionCleanupStillFails_KeepsTombstonePending`、
`Reconciliation_RemovesTombstonedRowWhoseLogIsAlreadyGone`、`ProjectDeletion_MigratedStore_ReportsUnassignedSessionIdsToHost`。
CLI 与 Desktop `win-x64` NativeAOT publish 均成功，**0 条 compiler／trimming／AOT 警告**（Desktop 仍有既有的两条
Avalonia Win32 COM `will always throw` 信息性诊断，来自第三方库）；从仓库外工作目录、隔离
TEMP／APPDATA／LOCALAPPDATA 运行**发布产物**：CLI `smoke --config <repo>/tinyharness.json` PASS（exit 0，
17 steps／16 tool executions，含 `session store: verified migration, read/write, reopen, backup and restore`），
Desktop `--offline-smoke` PASS（`session-store=migrated+readwrite+restore`）。日志保留在 `artifacts/s5-20261117/`
（`cli-publish.log`、`desktop-publish.log`、`cli-aot-smoke.log`、`desktop-aot-smoke.log`），发布输出临时放在
`.tmp/aot-s5-20261117/` 并在验证后删除。
未验证／未执行：未对真实用户 store 执行迁移或 v1→v2 升级（升级夹具是把版本 2 库退回版本 1 结构构造的）；
未做真实 GUI 删除交互、双进程并发、断电与真实进程终止验收；两端 smoke 不含会话删除步骤。

2026-11-17，S5 删除墓碑与项目删除事务：

- 新增 `SessionStoreTombstoneStore`（墓碑读写、待收尾扫描、读取路径过滤入口）与 `SessionStoreDeletionState`／`SessionDeletionTombstone`；`SessionStoreDatabase` 的 `CurrentSchemaVersion` 升到 2，新增 `session_tombstone` 表与 `ix_session_tombstone_state` 索引，迁移 2 声明版本 1 全部对象加墓碑对象的完整形状。
- `SessionStoreDeletion` 拆出 `TryDeleteResolvedAsync`：允许在已解析的受保护 store 上按幂等语义删除日志（缺失返回 false），供墓碑续作使用；公开 `DeleteAsync` 的 writer 锁与叶子校验语义不变。
- `SessionStoreOperations.DeleteSessionPermanentlyAsync` 迁移后走"墓碑 → 日志 → 投影 → 置为已删除"，新增 `RecoverPendingSessionDeletionsAsync`；`SessionStoreCleanupArea` 增加 `SessionDeletion`，桌面把新区域映射为"删除收尾"并在准备 store 时调用恢复入口。
- 读取路径排除墓碑：`SessionStoreReader.ReadAsync`／`ListAsync`、`SessionListProjection` 的降级扫描与索引查询、`SessionIndexReconciler`、`SessionProjectInitializer`。对账遇到墓碑会话会删除残留索引行且不重建。
- 项目删除：`SessionStoreProjectCatalogStore.BeginDeletionAndUnassignAsync` 在单事务内标记删除、归属改为显式无项目、清理置顶与手动顺序；`SessionStoreOperations.DeleteProjectAsync` 的迁移分支复用 gateway 六阶段并在事务后复核归属。
- 测试：`SessionStoreTombstoneTests` 5 项（迁移后删除清理日志/墓碑/索引/归属并幂等、writer 占用保留待收尾且恢复续作、读取与两条列表路径过滤待收尾会话、对账删除且不重建墓碑行、项目删除单事务解绑与组织清理）；`SessionStoreDatabaseTests` 更新冻结表/索引清单，新增真实 v1 结构升级到 v2 的成功夹具（覆盖 S3 缺失的跨版本成功路径）。
- 验证：`TinyHarness.slnx` Debug 构建 0 警告 0 错误；`TinyHarness.Tests` 全量 **2300 通过／0 失败／10 跳过（2310 总计）**，`TinyHarness.Desktop.Tests` 全量 **421 通过／0 失败**；持久化定向筛选 **301 通过／0 失败／3 跳过**（含本轮新增 5 项墓碑测试、数据库 27 项与真实 v1→v2 升级夹具）。Desktop `--offline-smoke` 在隔离 TEMP／APPDATA／LOCALAPPDATA 下 PASS（`session-store=migrated+readwrite+restore`），覆盖业务库迁移后的读写与恢复，但 smoke 本身尚未加入删除步骤。
- 未验证／未执行：未执行 CLI／Desktop NativeAOT publish 与发布产物 smoke（本轮未改入口、依赖、JSON 序列化或配置绑定，也没有新增反射／动态代码；按仓库规则未触发该项）；未做真实 GUI 删除交互、双进程并发、断电与真实用户 store 迁移后删除验收；墓碑续作用例在同一测试进程内构造状态（writer 占用、墓碑已提交而日志仍在），不是真实进程终止与 SQLite WAL 重放。

2026-11-15，S4 分批导入、切换与兼容：

- 新增 Core 迁移与权威层：`SessionStoreMigrationContract`（标记/状态/批次/备份结果类型）、`SessionStoreMigration`（标记路径与读取、租约、显式 `MigrateAsync`／`InspectAsync`）、`SessionStoreMigrationStateStore`（标记严格读写与原子替换、store 级租约）、`SessionStoreMigration.Import`（三批导入、来源指纹、续作与崩溃窗口处理）、`SessionStoreBackup`（在线备份、校验、恢复）、`SessionStoreAuthority`（`Legacy`／`Migrated`／`Blocked` 解析与类型化拒绝）、`SessionPlacementOverlay`（三处投影共用同一归属叠加）、`SessionStoreOrganizationStore`／`SessionStoreProjectCatalogStore`／`SessionStorePlacementStore`（组织状态、项目目录册、会话归属的业务库读写）、`SessionStoreSidebarPreferencesReader`（导入用桌面偏好读取）与三个类型化异常。迁移标记 `session-store-migration.json` 记录迁移标识、库身份、各批状态与来源指纹，并与业务库 `store_meta.migration_id`／`store_identity` 双向绑定（第 5.1 节）。
- 生产权威切换：`ProjectStore`、`SessionStorePinStore`、`SessionStoreArchiveStore` 按标记在旧文件与业务库之间分派；`SessionStoreReader`、`SessionSummaryBuilder`、`SessionIndexUpdater` 叠加业务库归属，历史 JSONL 归属不再覆盖切换后关系；`SessionIndexStore` 新增归属投影刷新（未变日志零正文扫描也能纠正历史归属）；`SessionIndexReconciler` 在迁移后同步归属投影；`SessionProjectInitializer`／`SessionProjectUnbinder`／`SessionProjectBinding` 在迁移后只写业务库、不追加 JSONL 归属、不抢 writer 锁；`SessionStoreWriter` 首次提交把归属写入业务库并停止写 ownership 记录；`SessionStoreOperations` 补齐业务库模式下的会话/项目删除清理。`ProjectCatalogMonitor` 在业务库模式下每轮直接读取业务库。未迁移 store 保持原文件语义（兼容期），迁移进行中或标记损坏时生产读写拒绝而不回退旧文件。旧文件在全部批次切换前保持原样，迁移后也不会被自动删除。
- 备份与恢复：SQLite 在线备份（WAL 一致）、完整性检查、schema/身份校验、恢复前保留原库 `session-store.db.pre-restore-<unixms>`、恢复后删除派生索引 `session-index.db*`、拒绝异库备份与未迁移目标、不删除用户备份；迁移完成后的回退策略是"只能按业务备份恢复"，不提供把业务库数据合并回旧 JSON/JSONL 的路径（第 5.4 节）。
- 标题与桌面偏好：完整恢复改为"最后一条非空名称胜出"，与列表摘要一致，晚到的待生成标记不会清除已保存名称；导入不调用模型、不重写日志、不重复追加标题事件。桌面侧栏仅在迁移后把每 store 组织状态（手动项目/会话顺序、折叠分组）切到业务库，应用级偏好（删除确认等）继续留在 `desktop-sidebar.json`，保存时为已迁移 store 投影为空组织字段且保留其他偏好；阻塞状态提示迁移/恢复并不写入。
- CLI 新增 `session store status|migrate|backup|verify|restore`（迁移与恢复要求 `--yes`，路径须为绝对路径），离线 smoke 增加业务库建库、导入、读写、重开、备份与恢复步骤。
- 测试（Core `TinyHarness.Tests`）：新增 `SessionStoreMigrationTests` 18 项（三批导入、重复导入无操作、重复置顶合并、中断续作保留已切换批次、崩溃窗口幂等、身份接管、标记损坏、完成后库缺失/替换/损坏、切换后旧来源改写警告、非法旧 ID 拒绝且库保持待切换、损坏 JSONL 留待初始化、悬挂项目引用丢弃、租约占用、取消重试、无效侧栏路径、缺目录册）、`SessionStoreBackupTests` 12 项（备份校验、拒绝覆盖、恢复原库副本与索引删除、异库/缺失/损坏备份拒绝、未迁移目标拒绝）、`SessionStoreBusinessAuthorityTests` 15 项（权威模式与阻塞拒绝、三个领域存储读写、归属三态、历史归属不覆盖、项目目录册修订与删除外键、初始化/并发插入/解绑/删除业务库路径、writer 首次提交与恢复绑定、旧来源读取绕过阻塞、标题一致语义）。Desktop 新增 `DesktopStoreOrganizationTests` 3 项（迁移后读业务库折叠状态、切换折叠只写业务库并清空文件组织字段、未迁移 store 保持文件权威）。
- 构建与定向回归：`TinyHarness.Core`／`TinyHarness.Desktop` Debug 构建 0 警告 0 错误；新增迁移/备份 30/30、权威 15/15、Desktop 组织 3/3 通过；既有持久化定向回归（数据库/边界/Project/Pin/Archive/Initializer/Unbinder/Binding）133 通过 0 失败 2 跳过；既有索引与恢复定向回归 104/104 通过；Desktop 既有定向回归 97/97 通过；`TinyHarness.Tests` 全量 **2270 通过／0 失败／10 跳过（2280 总计，跳过项为链接权限、网络隔离与基准测量等环境相关用例）**；`TinyHarness.Desktop.Tests` 全量 **421 通过／0 失败**（一次全量运行中 `DesktopBlankConversationTests.ExplicitHumanApprovalSelectionProtectsAnOtherwisePristineNextDraft` 出现过一次与本阶段无关的集合枚举竞态，该测试单独重跑与全量重跑均通过，按既有偶发问题记录，未改动该测试或放宽断言）。
- 离线 smoke（Debug 构建产物）：CLI `smoke --config <repo>/tinyharness.json` 在隔离 TEMP/APPDATA/LOCALAPPDATA 下 PASS（17 steps／16 tool executions），输出含 `session store: verified migration, read/write, reopen, backup and restore`；Desktop `--offline-smoke` 同条件 PASS，输出含 `session-store=migrated+readwrite+restore`。
- NativeAOT 与产物 smoke（本阶段新增业务 schema 的真实执行证明）：`dotnet publish TinyHarness.Cli/TinyHarness.Cli.csproj -c Release -r win-x64 --self-contained true` 与 Desktop 同名发布命令均成功，退出码 0，无编译／trimming／AOT 警告行（Desktop 发布时 ILC 打印两条 Avalonia Win32 COM 内部方法的 "will always throw" 信息性诊断，属既有第三方库内容，不来自本阶段代码）。从仓库外工作目录、隔离 TEMP／APPDATA／LOCALAPPDATA 运行**发布产物**：CLI `smoke --config <repo>/tinyharness.json` PASS（17 steps／16 tool executions），并输出 `session store: verified migration, read/write, reopen, backup and restore`；Desktop `TinyHarness.Desktop.exe --offline-smoke` PASS，并输出 `session-store=migrated+readwrite+restore`。发布产物中实际执行了业务库建库、旧来源导入、组织状态读写、重开、备份校验与恢复，不是只跑旧索引 smoke。日志保留在 `artifacts/s4-20261115/`（`cli-aot-smoke.log`、`desktop-aot-smoke.log`、全量测试日志）；发布输出临时放在 `.tmp/aot-s4/` 并在验证后删除。
- 未验证／未执行：未对真实用户 store 运行迁移（本阶段禁止），未删除任何旧文件或备份；未在真实 GUI 中做迁移/恢复交互验收；未做双进程真实旧写者争用、断电与磁盘只读的现场实测（仅有租约占用、来源指纹、取消与损坏夹具覆盖）；`UpgradeAsync` 的跨版本真实旧结构夹具仍缺失（本阶段无新 schema 版本）；S5 的删除墓碑／可恢复删除未实施。

2026-11-15，S3 schema 冻结与建库／升级基础（含评审修复）：

- 新增 Core `SessionStoreDatabase`（`TinyHarness.Core/Services/Persistence/SessionStoreDatabase.cs`）与 `SessionStoreDatabaseException`：按 store 解析 `session-store.db`（`StoreDirectory` 必须绝对路径），导出 `CurrentSchemaVersion = 1`、`DatabaseFileName`、`PathFor`／`Exists`、可用性状态机（`Available`／`Missing`／`NeedsSchema`／`NeedsUpgrade`／`Unsupported`／`Damaged`／`Failed`）与两个写入入口 `InitializeAsync`（建库）／`UpgradeAsync`（就地升级）。**没有生产调用方**，未切换任何权威来源。
- schema 版本 1 冻结 9 张 `WITHOUT ROWID` 表：`store_meta`、`project`（含 `ux_project_root_key` 唯一索引）、`session_placement`（外键 `project_id → project(id) ON DELETE RESTRICT` + `ix_session_placement_project`）、`archived_session`、`pinned_session`、`pinned_project`、`project_order`、`session_order`、`collapsed_group`。约束覆盖列类型（`typeof` 守卫）、ID 形态、名称/路径长度、修订号下界、删除标记取值与顺序表非负位置；数量预算仍由调用方校验。语义与理由见第 4.3 节。
- 版本号存 `store_meta.schema_version`；迁移逐版本在单事务内执行并同事务写入版本号（身份与建库时间用 `INSERT OR IGNORE`，版本用 `INSERT OR REPLACE`），迁移编号必须从 1 连续。每个版本声明应具备的表与显式索引：可用性要求表、索引与元数据键齐全；迁移前后都按声明核对，缺失一律拒绝迁移，版本 0 必须是空库——避免 `CREATE TABLE IF NOT EXISTS` 把缺失表补成空表后宣称升级成功。升级探测只设置 `busy_timeout`，不切 journal、不建表、不写元数据。
- 探测结论区分"确定缺失／暂时读不了／确实损坏"：路径不存在才是 `Missing`；目录或链接占位判 `Damaged`；权限或路径无法判定、锁等待耗尽、只读与 IO 失败判 `Failed`；只有 SQLite 明确报告 `SQLITE_NOTADB`（26）或 `SQLITE_CORRUPT`（11）才判 `Damaged`，不把运行期冲突误导成"去恢复备份"。`SessionStorePaths.EnsureStoreLeaf` 的拒绝也收敛为本类型的异常，不漏出其他异常类型。
- 连接策略与索引库有意不同：禁用连接池、私有缓存、`journal_mode=WAL`、`synchronous=FULL`、`foreign_keys=ON`，`busy_timeout` 与命令超时同为 5 秒。理由见第 4.4 节，与"为何不能复用整库索引重建"一并见第 4.6 节。
- 补齐标题边界测试：纯标题批次只改标题与提交时间，不推进活动时间、不增加回合数；待重试标记不清除已保存名称；无标题事件按首回合首行、无回合时按会话 ID 前 8 位回退。
- 评审修复（4 项 + 复核追加 2 项，均在 S4 接入前处理）：①旧版本升级不再把任何低于当前的版本当成可升级——迁移前核对声明结构，未知或缺失即拒绝，版本 0 有表即拒绝；②可用性不再只看表名——缺显式索引或元数据键同样判损坏；③数值约束加 `typeof(...) = 'integer'`，堵住"非数字文本因 TEXT 恒大于数值而通过 `CHECK`"；④探测错误分级如上条，不再把权限/锁/只读错误报成缺失或损坏；⑤路径叶子校验只保留 `try` 内的一次——此前 `OpenAsync` 在 `try` 外还调用了一次，属性探测后文件若变成链接，`SessionStoreException` 会绕过本类型的异常契约与可用性分类；⑥迁移后的结构核对移入**同一事务**并在提交前完成——此前是先提交 DDL 与版本号再核对，迁移脚本遗漏必需对象时库已永久记录新版本。
- ⑤⑥ 的覆盖状态：⑥ 已用故意漏建 `project` 的迁移夹具确定性覆盖（`Initialize_WhenMigrationProducesIncompleteStructure_RollsBackEverything` 断言异常、回滚后无任何表、库重新被判为 `NeedsSchema`，即未留下版本号）；为此给 `SessionStoreDatabase` 增加一个仅测试可见的迁移注入重载与 `internal` 迁移/结构类型，生产路径不变。⑤ 只在"属性探测之后、打开之前文件被换成链接"的竞态窗口内可达，目前**尚未覆盖**，只由单一调用点与 `try` 内校验的结构约束。
- 验证：`TinyHarness.Core` 与 `TinyHarness.Tests` Debug 构建 **0 警告 0 错误**；新增定向测试 **28/28** 通过（`SessionStoreDatabaseTests` 25、`SessionStorageBoundaryTests` 3）；含既有相邻套件的定向回归 **74/74** 通过（另含 `SessionTitleTests`、`SessionIndexStoreTests`、`SessionIndexStoreVersionTests`、`SessionIndexReconcilerTests`）；`TinyHarness.Tests` 全量 **2224 通过／0 失败／10 跳过**（2234 总，跳过项为链接权限、网络隔离与基准测量等环境相关用例）。
- NativeAOT 与 smoke：`dotnet publish TinyHarness.Cli/TinyHarness.Cli.csproj -c Release -r win-x64 --self-contained true` 成功，**0 条 compiler／trimming／AOT 警告**，产物含 `e_sqlite3.dll`；从仓库外工作目录运行原生产物 `smoke --config <repo>/tinyharness.json`（隔离 TEMP／APPDATA／LOCALAPPDATA、补系统 PowerShell 与 `dotnet` 路径）**PASS，退出码 0**：17 steps／16 tool executions，含 `session index: verified SQLite summary row for the smoke session`。检查产物字符串（UTF-16）确认 `session-index.db` 存在而 `session-store.db` 不存在，即本阶段新增类型被裁剪——发布与 smoke 只覆盖"编译无警告、既有行为未退化"，不覆盖业务 schema 在 AOT 下的实际执行。日志留在 `artifacts/s3-20261115/`，发布输出按测试资源规则放在 `.tmp` 并在验证后删除。
- 未验证／未执行：未运行 Desktop 全量套件与 Desktop `--offline-smoke`（本轮未触碰 Desktop 代码）；未运行 ReSharper `jb cleanupcode`，新增文件按 `.editorconfig`（LF、文件末尾换行、无行尾空白）与邻近风格人工整理并逐行复核；`UpgradeAsync` 的"逐版本成功迁移"无真实旧版本可测（见第 4.9 节第 9 项）；真实 GUI、真实用户库、跨进程双写与备份恢复均未验收。

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
