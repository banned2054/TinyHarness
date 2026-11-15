# 会话存储治理与删除优化

更新日期：2026-11-15。

状态：S1、S2 的实现与本地定向验证记录见第 7 节（截至 2026-11-11）；本次只读对账未重跑验证。S3 已于 2026-11-15 完成 schema 冻结与版本化建库／升级基础（第 4 节），未切换生产读写权威、未导入真实数据；真实 GUI 与用户库耗时待验收，S4–S5 待启动。入口见 [PLAN.md](../PLAN.md)。

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
| `session-store.db`（本阶段新增，schema 版本 1） | 项目、归属、归档、置顶、顺序、展开状态 | **不可重建**：只能由业务备份恢复；缺失只表示"尚未迁移" |

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

### 4.9 未解决事项（S4／S5 输入）

1. `Missing` 的两种含义尚未区分：首次迁移（应导入）与迁移后丢失（应拒绝并引导恢复）。需要在导入完成时写一个业务库之外的标记，或由导入阶段自带证明。
2. 备份／恢复入口（含 WAL 一致性、checkpoint、`integrity_check`、升级前自动备份）未实现。
3. 标题投影与完整恢复的 `pending` 语义分叉：需在 S4 明确"最后一条胜"还是"最后非空胜"，否则 SQLite 行与 JSONL 重放会给出不同标题。
4. `session_placement` 以"无行"表达 `PendingInitialization`，导入标记必须能区分"未导入"与"明确无项目"。
5. S5 需要的会话删除墓碑／可恢复删除状态不在 v1 schema，须走版本 2 迁移；项目删除的可恢复性同样未定义。
6. 业务库写失败的重试／排队策略、双进程写者仲裁未确定（当前只有有界等待后失败）。
7. 数量预算、名称与路径的规范化规则仍需调用方校验；`CHECK` 只覆盖可表达的形态与类型。
8. 是否最终把摘要表并入业务库仍未决定；当前选择是两个文件。
9. 迁移引擎的成功路径缺少真实跨版本验证：当前只有版本 1，`UpgradeAsync` 的"逐版本迁移"尚无已发布旧结构可测，现有测试只覆盖"结构不符即拒绝"与"已是当前版本为无操作"。加入版本 2 时必须同时提供一份旧结构夹具库，按真实数据验证迁移与备份恢复，不能只把版本号改小。
10. AOT 与离线 smoke：本阶段新增类型没有任何调用方，`win-x64` NativeAOT 发布产物里确实不含 `session-store.db` 等字符串（即被裁剪），因此发布与 smoke 只证明编译与既有行为未退化，**不能**证明业务 schema 在 AOT 下可用；接入调用方的阶段必须重做该验证。

### 4.10 2026-11-14 后续改动纳入边界

- 自动命名已把保存标题作为 `session-title` JSONL 事件持久化；SQLite 列表标题仍是可重建摘要。导入应保留已有事件和标题，不调用模型、不追加重复事件；纯标题更新不得推进对话活动时间或回合数。
- 归档会话删除确认选项属于桌面 UI 偏好，继续由现有 `SidebarPreferencesStore` 管理；不要把它并入每个 store 的业务 schema。
- 当前项目目标框架和 `AGENTS.md` 规则均为 `net11.0`／.NET 11；S4/S5 的 NativeAOT 与离线 smoke 仍须按该目标框架验证。

### 4.11 本轮代码落地

- 新增 `TinyHarness.Core/Services/Persistence/SessionStoreDatabase.cs`：冻结 schema 常量与表清单、`PathFor`／`Exists`、可用性状态机、`InitializeAsync`（建库）、`UpgradeAsync`（就地升级）、版本化迁移与连接配置。没有生产调用方。
- 新增 `TinyHarness.Core/Exceptions/SessionStoreDatabaseException.cs`。
- 新增测试 `TinyHarness.Tests/SessionStoreDatabaseTests.cs`（25 项）与 `TinyHarness.Tests/SessionStorageBoundaryTests.cs`（3 项）。
- 未修改任何既有生产类型、配置文件或依赖；未切换读写权威；未导入真实数据。

## 5. S4：分批导入、切换与兼容

待启动；依赖 S3 的冻结 schema（第 4 节）与第 4.9 节未解决事项。

1. 基于隔离副本实现幂等导入与迁移标记，先验证，再允许真实数据迁移。旧文件在新存储完整校验前保持原样。
2. 分批切换归档／置顶／排序、项目目录册、最后当前会话归属；每批只保留一个权威来源，过渡状态有版本标记，不长期双写。
3. 归属迁移需同时调整恢复投影、运行宿主与旧日志初始化：旧归属记录仅作导入来源，不能覆盖迁移后的当前关系；数据库缺失不得静默回退旧关系。
4. 明确中断续作、损坏报告、备份恢复及回退边界。旧版本仍会写旧文件，不能宣称新旧程序可同时写同一 store；在切换前制定实际可执行的升级／停止旧写者方案。
5. 所有调用方切换后再移除旧生产读写路径；用户数据备份清理必须另行明确授权，不在升级中自动删除。
6. 导入验收覆盖已保存标题、标题回退和标题事件重放；迁移不得触发模型调用、丢失标题或重复追加标题事件。桌面删除提示偏好保持在应用层存储。

## 6. S5：迁移后简化删除与验收

待启动；依赖 S4，且需要业务库版本 2 迁移（墓碑／可恢复删除状态，见第 4.9 节）。

- 项目删除在 SQLite 事务内解除关系并清理项目组织状态，不再为组织变化逐日志追加记录；运行中会话限制与内存宿主更新仍需验证。
- 会话删除使用可恢复的删除状态协调数据库和日志文件，保留 writer 锁与宿主释放；不宣称跨文件和数据库原子提交，避免后台扫描复活删除中条目。
- 元数据操作不刷新对话活动时间；运行会话优先、空闲手动排序、取消置顶恢复原位置等语义不变。
- 归档会话删除继续尊重桌面偏好中的确认提示；迁移或删除 store 组织数据不得改写该 UI 偏好。
- 覆盖双进程、在途写入、崩溃窗口、版本冲突、只读与不可写库、业务备份恢复、摘要重建不丢组织状态，并执行 NativeAOT／离线 smoke 与真实 GUI 验收。

## 7. 本轮实施与验证记录

> 本节为截至 2026-11-11 的历史实施与验证快照。2026-11-14 的计划对账没有重跑其中的构建、测试、NativeAOT、smoke 或真实环境验收。

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
