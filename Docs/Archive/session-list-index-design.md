# Session 列表加载与 SQLite 索引：设计与验收记录

归档日期：2026-11-04。本项已按计划完成 A–E 全部阶段；计划入口与最终状态见 [PLAN.md](../../PLAN.md)。归档不取消仍有效的技术约束；本文件保留设计契约、验收矩阵证据与测量记录。历史结论不自动更新为当前状态。

## 1. 最终状态

- 状态：**已完成（A–E 已实施并验收）。** 阶段 A–D 提交为 157aa02、8bd444b、80f67a1、9d06851；阶段 E 于 2026-11-04 完成综合验收并随文档轮提交。
- 核心约束全部满足：列表不恢复完整会话；索引不决定执行安全；后台维护不阻挡首屏；索引失步可发现、可修复。
- 首版范围保持：JSONL／既有元数据文件 + SQLite 可重建列表投影；不迁移项目定义，不引入 ORM、全文搜索、云同步或共享数据库能力。

## 2. 设计契约（仍然有效）

### 2.1 数据权威与可重建边界

| 数据 | 权威来源 | SQLite 职责 |
| --- | --- | --- |
| 会话身份、执行目录、对话与执行事实、模型／权限选择、项目归属 | 会话 JSONL | 保存列表必需摘要，不保存可执行授权或完整对话 |
| 项目名称、目录、修订和删除状态 | 项目目录册 | 不迁移项目定义；列表按既有项目读取路径关联展示 |
| 会话归档状态 | `SessionStoreArchiveStore`（归档状态文件） | 缓存归档投影，丢失数据库后可从原来源恢复 |
| 置顶、手动顺序、折叠及显示偏好 | 既有 Core sidecar 与 Desktop 偏好 | 首版不改变权威来源及手动排序语义 |

- SQLite 不是所有持久状态的唯一来源，也不只是 JSONL 的投影；不增加仅写数据库、无法从来源重建的标题或状态更新。
- 归档来源不可读时显式保留既有投影并报告，不以默认值覆盖；项目或元数据缺失继续按既有契约报告。
- 摘要覆盖列表／会话行实际消费的字段：id、workspace_root、project_id、归属三态（`PendingInitialization`／`Project`／`NoProject`）、标题、创建时间、活动时间、回合数、权限模式、`commit_sequence`／`indexed_bytes`、来源标记（长度＋毫秒修改时间）、条目状态（待建立／正常／过期／失败）、归档投影。
- 日志进度只对应同一来源快照中最后完整有效批次，半行不算完成；长度与修改时间只是失效线索，不是完整性证明。
- 归属维护不推进 `LastActivityUtc`；保留手动顺序与置顶规则。

### 2.2 写入顺序、多进程与故障语义

- 日志／归档权威来源先可靠提交，再更新 SQLite；不声称跨 JSONL、sidecar 与 SQLite 的原子事务。索引失败可观测、可重试，不将已提交日志误报为未提交，不 poison JSONL writer。
- 条件提交防旧覆盖新：健康条目只接受更大序号（或同序号不更少字节）；对账重建写入要求来源标记更新；序号仅在同一来源代次内比较。日志摘要与归档投影独立更新。
- 本地文件系统使用 WAL，事务短小，`busy_timeout` 与 provider 命令超时对齐为 3 秒（见验收发现）；`Pooling=false`，连接单操作归属明确。WAL 不代表多写者并行；扫描日志时不持有数据库写事务。
- 同进程直接回调（writer `batchCommitted` → `SessionIndexUpdater`）；跨进程在同一监测连接上比较 `PRAGMA data_version`，变化后重查。
- `data_version` 发现不了"已提交、索引未更新即崩溃"；由启动后台对账、周期刷新循环与手动刷新兜底。
- 变短、替换、边界不匹配或不支持格式不盲目续扫、不复用旧代次摘要；重建不修复、截断或改写原日志。
- 数据库缺失、损坏、不可写、锁等待耗尽或版本不受支持分别报告；必要时降级为不落库严格摘要扫描；重建不破坏权威数据。未识别的新版本不被旧程序清空或降级。
- 索引数据库、`-wal`／`-shm`、重建临时文件等索引产物留在受保护位置；复用 store 既有工作区／沙箱写根与链接校验边界，不新增 Agent 可篡改或指向任意文件的入口；索引含标题等用户内容，不是无敏感信息的遥测数据。

### 2.3 实现与接入边界

- `SessionIndexStore`：单 store 一个 `session-index.db`（schema v1），有界分页查询、条件提交、失败标记、归档投影同步、data_version 常驻监测。
- `SessionSummaryBuilder`：严格摘要扫描，不物化完整 `PersistedSession`；拒绝范围与严格恢复一致。
- `SessionIndexUpdater`：writer 提交通知驱动的增量快路径（前缀连续才增量，否则整份重扫）；失败记 `LastFailure` 不中断提交。
- `SessionIndexReconciler`：未变零正文扫描、缺失／变化整份重建、变短／替换先标记后重建、孤儿移除、归档来源同步；逐条提交、可取消、可续作；`SummaryCommitted` 回调支撑渐进上屏。
- `SessionListProjection`：CLI／Desktop 共用门面——可用性、分页取全、失败行、降级扫描（不落库）、对账与监测入口。
- CLI `session list`：Available 读索引摘要＋失败行，否则回退只读扫描（不建索引）。
- Desktop：首屏列表先行（Available 直接查询；Missing／NeedsSchema 降级扫描；Unsupported／Damaged／Failed 降级＋可见原因）；归属整理只处理索引标记的待初始化条目，索引不可用回退整轮扫描；15 秒周期刷新循环（data_version＋对账，变化才重载）；渐进泵按批应用并批尾重建项目分组。
- `SessionIndexReconciler` 的 "按会话修复" 与 "按新增字节续扫" 不同：现实现按缺失／变更整份重扫，不具备增量续扫；未变日志零正文扫描，已打开写者的正常提交走更新器增量快路径。
- 列表索引只验证其契约范围；实际续聊仍由权威日志的严格恢复路径把关。

## 3. 验收矩阵证据（阶段 E，2026-11-04）

离线测试证据以 2026-11-04 本轮源码为准；原生与 GUI 证据单列。测试名可在 `TinyHarness.Tests`／`TinyHarness.Desktop.Tests` 中检索。

| 场景 | 结论 | 证据 |
| --- | --- | --- |
| 有效索引、来源未变：不读正文、不构建全库 `PersistedSession` | 已验证（离线） | `Reconcile_SkipsUnchangedSourcesWithoutRescanning`（Unchanged 全量、哨兵标题存活）；基准 `Measure_IndexWarm_UnchangedSources`（Unchanged=3030、Rebuilt=0、对账 98ms）；Desktop `StartupWithValidIndexRendersRowsFromSummaries` |
| 增长长归档会话不拖默认列表 | 已验证（离线） | `ListSummaries_ValidIndexWithOccupiedArchivedLog_RendersWithoutBodyReads`（归档日志增长并被独占占用后默认列表仍完整呈现，证明零正文读取；释放后对账仅重建该条） |
| 首建／重建中取消或退出 | 已验证（离线） | `Reconcile_CancelledMidBuild_RetainsCommittedEntriesAndResumes`（取消后已完成条目保留、权威字节不变、续作收敛）；Desktop `CancelStopsTheRoundEvenWhenMoreLogsRemainAndRestartCompletes`、`ClosingDuringARoundAwaitsItAndLeavesNoLateWrites` |
| 新建／选择／归属／归档变更收敛 | 已验证（离线） | `Reconcile_CatchesUpSourceCommittedWithoutIndex`、`Updater_MissingRow_FallsBackToFullRebuild`、`SummaryBuilder_OwnershipOnlyBatchDoesNotAdvanceActivity`、`SendingDuringTheRoundPreservesNoProjectWhileLegacySessionsAreBound`、`Reconcile_BuildsMissingEntriesAndSyncsArchive`、`ArchiveProjection_IsolatesFromSummaries` |
| CLI／Desktop 双进程并发（真实子进程） | 已验证（离线＋原生） | `SessionIndexCrossProcessTests` 三测试（真实 `TinyHarness.IndexProbe` 子进程）：旧快照不覆盖子进程新提交＋data_version 跨进程可见；独占锁下索引写有界失败、业务提交不受阻且失败留痕、释放后对账追平；独占锁下读者保持 Available 且查询有界。原生 smoke 以 AOT 探针与 CLI 真实双进程复核 |
| 来源提交后、索引更新前故障 | 已验证（离线） | `BatchCommittedHook_FiresAfterCommitAndSurvivesFailures`、`Updater_DamagedIndex_StopsWithoutBreakingCommits`、`Reconcile_CatchesUpSourceCommittedWithoutIndex`（对账按来源标记追平，不依赖 data_version） |
| 归档来源更新后索引失败 | 已验证（离线） | `Reconcile_DamagedArchiveSource_KeepsProjectionAndReportsSkippedSync`（归档源损坏与日志刷新并存：`ArchiveSynced=false`、说明有界、既有投影保留、不当作未归档）；`ArchiveProjection_IsolatesFromSummaries` |
| 单项日志损坏、占用、超限或半行 | 已验证（离线） | `DamagedLogIsReportedAndNeverRepaired`、`ScanSummaries_RendersWithoutCreatingIndexAndReportsDamage`、`ListRows_ExposesFailedEntriesExcludedFromSummaries`、`SummaryBuilder_RejectsPartialTail`、`Reconcile_OccupiedSource_MarksFailedAndContinuesRound`、`Reader_RejectsUnknownFormatDeepJsonOversizedLogAndMidLogDamage` |
| 数据库缺失、损坏、不可写或版本冲突 | 已验证（离线） | `Availability_InitializeAndReopen_ReportsExpectedStates`、`Availability_UnsupportedVersionAndDamage_AreReportedSeparately`、`Operations_OnSchemalessDatabase_ThrowSessionIndexException`、`Reconcile_RefusesNewerSchemaWithoutClearing`；被锁场景由跨进程测试覆盖（有界、可见原因）；只读目录 ACL 变体未单独构造（Windows 只读目录属性不阻止建文件） |
| 来源截断、替换及同长度修改 | 已验证（离线） | `Reconcile_ShrunkSource_MarksFailedThenRebuilds`、`Reconcile_SameLengthReplacement_DetectsAndKeepsOldSummary`、`Updater_StaleRowPrefix_TriggersFullRebuild`；同长且同 mtime 替换不可检测（设计接受，记录在 Reconciler 备注） |
| UI 与 store 切换 | 已验证（离线） | `DraftTypingAndBlankPageSelectionSurviveTheRoundAndRefresh`、`StoreSwitchCancelsTheOldRoundOrganizesTheNewStoreAndRebindsTheBlankPage`、`SameRootAcrossTwoStoresKeepsPendingGroupsAndIssuesIsolated`、`ProjectGroupsKeepInstanceAndExpansionAcrossReload`、`SwitchingAndEffortUseRegisteredHostAndKeepHistoricalLabelsAfterResume` |
| 安全：路径／链接保护 | 已验证（离线） | `ResolveProtected_RejectsWorkspaceChildAndRelativePathAndAcceptsExternalPath`、`ResolveProtected_RejectsJunctionIntoWorkspaceWhenAvailable`、`LockAndLogLeafLinks_AreRejectedWhenLinkCreationIsAvailable`、`Store_RejectsInvalidAndEscapingIdentifiersButCanonicalizesHexCase`、`StartupRoundRefusesToWriteWhenStoreSitsInsideSandboxAdditionalWriteRoot` |

## 4. 测量记录（2026-11-04，本机隔离夹具）

环境：Windows 10.0.26300 x64，.NET 10（JIT 测试进程），夹具 `3000 短会话 + 20 长会话（60 回合）+ 10 工具密集会话（30 回合）`，共 3030 份日志、约 50.86 MB 正文，全部位于 %TEMP% 隔离目录；不触碰真实用户日志，不调用真实模型。

阶段 A 基线（同机同规模，改造前链路）：

| 指标 | 数值 |
| --- | --- |
| 来源枚举 | 217.2 ms |
| 逐日志严格读取（全部完成后才出首行） | 6164.8 ms（首行可见＝6164.8 ms），p50 1.724 ms / p95 2.987 ms / max 43.443 ms |
| `ListAsync`（入口总时长） | 6408.5 ms，分配 546.0 MB |
| 旧归属整理整轮（夹具全未绑定，31 批） | 57432.1 ms |

阶段 E：索引链路（同规模夹具，Explicit 基准 `SessionListBenchmarkTests.Measure_Index*`）：

| 场景 | 指标 | 数值 |
| --- | --- | --- |
| 首次建库 | 降级扫描（不落库，Desktop 首屏先行路径） | 2097.6 ms，分配 473.1 MB，3030 健康 / 0 失败 |
| 首次建库 | 建库对账（含首条渐进回调） | 32714.8 ms，分配 510.6 MB，Rebuilt=3030 / 失败 0 / 说明 0 |
| 首次建库 | **首建双扫描合计（计划内成本，单列）** | ≈ 34.8 s，分配 ≈ 983.7 MB |
| 首次建库 | 首条可见（首个提交回调） | **87.4 ms**（旧链路首行 6164.8 ms） |
| 有效索引 | 列表查询（全部 3030 行＋摘要） | 冷 68.6–81.2 ms，热 67.2–68.9 ms，分配 3.7 MB |
| 有效索引 | 后台对账（未变来源） | 98 ms，分配 4.1 MB，Unchanged=3030 / Rebuilt=0 |
| 少量变更 | 对账（新增 3 会话＋2 归档切换） | 156.4 ms，分配 4.6 MB，Rebuilt=3 / Unchanged=3030 |
| 少量变更 | 默认视图查询（3031 条健康） | 51.9 ms，分配 1.7 MB |
| 进程峰值 | 基准进程峰值工作集 | 106.9 MB |

有效索引下启动列表成本相对旧链路：6408.5 ms → 67–81 ms（约 80–96×），分配 546 MB → 3.7 MB。归属整理在有效索引下只处理索引标记的待初始化条目（本夹具首建后第一次整理等于逐会话读正文，量级同建库对账；此后新增条目才产生维护成本）。冷／热口径为同进程建库后首次与重复查询；跨进程冷启动以 AOT 探针新进程读索引补充计时 83 ms（见第 5 节）。真实 GUI 流畅度（渐进上屏与刷新循环的视觉表现）未验证，不从离线基准推定。

执行命令（可复测）：

```text
dotnet test TinyHarness.Tests/TinyHarness.Tests.csproj --filter "FullyQualifiedName~Measure_IndexFirstBuild_ColdStore_Progressive"
dotnet test TinyHarness.Tests/TinyHarness.Tests.csproj --filter "FullyQualifiedName~Measure_IndexWarm_UnchangedSources"
dotnet test TinyHarness.Tests/TinyHarness.Tests.csproj --filter "FullyQualifiedName~Measure_IndexIncremental_NewSessionsAndArchive"
# 阶段 A 基线（改造前链路）：
dotnet test TinyHarness.Tests/TinyHarness.Tests.csproj --filter "FullyQualifiedName~Measure_CurrentListAndOrganize_Baseline"
# 夹具：
dotnet test TinyHarness.Tests/TinyHarness.Tests.csproj --filter "FullyQualifiedName~Fixture_BuildSyntheticStore_ReportsVolumes"
```

基准报告 JSON 随每次运行写在 `%TEMP%\tinyharness-session-list-bench\`（`index-first-build-report.json`、`index-warm-report.json`、`index-incremental-report.json`、`bench-report.json`）。

## 5. NativeAOT 与仓库外隔离 smoke（阶段 E，2026-11-04）

- 发布：CLI、Desktop、IndexProbe 三者 `win-x64` NativeAOT publish 全部 **0 warning**（`PublishTrimmed` 生效；产物含 `e_sqlite3.dll` 原生依赖；CLI 保持单文件约束下的既有形态）。
- 仓库外隔离 smoke（可执行文件与夹具均在 %TEMP%，不在仓库目录内运行）：
  1. 探针 AOT：`create-log 5` → `reconcile`（建库 Available，rebuilt=5）→ `list`（查，rows=5）→ `write-summary` 序号 3 条件推进（applied=1）→ `list`（更新可见）→ **新进程重开** `list`（Available，rows=5，冷进程读取 83 ms）。
  2. CLI AOT：`--help` 正常；空 store `session list` 输出 `(no stored sessions)` 且不创建任何文件；Available 索引上 `session list` 输出索引摘要行（读库路径）；`corrupt-index` 后 `session list` 降级为只读扫描并正常输出 5 行（exit 0），不修复损坏文件。
  3. Desktop AOT 仅发布验证 0 warning，未运行（GUI 交互未验证）。
- 真实多进程的跨端并发已由跨进程测试以真实子进程覆盖；真实用户数据未被触碰。

## 6. 验收发现与修复

- **锁等待并非宣称的有界 3 秒（验收发现，已修复）**：`Microsoft.Data.Sqlite` 在命令超时（默认 30 秒）内重试 `SQLITE_BUSY`，store 的 `busy_timeout=3000` 会被越过——跨进程实证写入在他人持锁时等待约 6.5 秒后成功。修复：`OpenAsync` 的连接串 `DefaultTimeout` 与 `BusyTimeoutMs` 对齐为 3 秒，锁等待真正有界；跨进程测试固化该语义（独占锁下索引写有界失败、业务提交健康、失败留痕）。
- 阶段 D 首建双扫描（降级扫描＋建库对账各读一遍正文）按计划保留为成本项并在第 4 节单列；不做合并优化。
- `SessionIndexStoreTests` 既有 6 处 CS4014 为历史遗留，本轮未触碰（不在授权范围）。

## 7. 未验证范围

- 真实 GUI：渐进上屏、15 秒刷新循环、侧栏交互的视觉表现与流畅度未验证。
- 真实多设备、断电、设备故障保证不在本轮范围，未验证。
- 真实用户日志测量未执行（需另行授权）；全部测量基于隔离合成夹具。
- 只读 ACL 目录下的数据库不可写变体未单独构造测试。
- 同长且同修改时间的来源替换按设计不可检测，已在契约中声明；实际续聊由严格恢复路径把关。
