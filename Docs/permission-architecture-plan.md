# 权限架构重构任务记录

状态：现有工作区的可提交性修复与验证收口；完整计划及真实环境验收未完成。最新记录见第 11 节；
第 5–9 节保留原批次记录，不作为本轮验证结果。本文档是权限架构重构任务的主记录与契约源；
状态区分"已验证（真实环境）/ 离线验证 / 未验证 / 推断"。

## 1. 背景与目标

将现有七种权限模式（逐次确认、只读可申请、项目内读写、完全开放、逐项 AI 审核、只读 AI 审批、
项目内读写 AI 审批）重构为更简单的语义模型，任务要点：

- **双审批模式**：人工审批（HumanApproval）与 AI 自动审核（AIAutoReview）。
- **三级执行分类**：沙箱基线自动、沙箱内附加能力、Host 单次显式申请；Level 是展示/审计分类，非授权来源。
- **Grant 生命周期**：审批授权（Grant）具备显式生命周期与三种范围，取代"临时允许"与单次批准的隐式约定。
- **Reviewer 三决策**：审批人对单次请求给出允许、拒绝、升级人工三种决策；AI 技术失败不转人工。
- **search_text 增强**：检索工具补齐 regex/glob/上下文/输出模式与预算语义，与权限模型解耦。
- **旧模式迁移**：用户配置、会话记录中的旧模式值迁移到新模型；旧会话进入迁移待确认态，确认前禁止模型工具执行。

## 2. 基线

- 仓库：`C:\Code\.Net\TinyHarness`，git `master` @ `a71177c`（"docs: 补充沙箱网络权限与阶段交付记录"）。
- `master` 领先 `origin/master` 4 个提交；Phase 0 起点工作区干净（2026-11-07 确认）。
- .NET 10 + NativeAOT 是必须持续满足的产品约束；所有变更不得回归 trimming/AOT warning。
- 相关既有文档：[Docs/Guides/permission-modes.md](Guides/permission-modes.md)、
  [Docs/Development/phase2-codex-sandbox-report.md](Development/phase2-codex-sandbox-report.md)、
  [PAST_PLAN.md](../PAST_PLAN.md)。

## 3. 已确认架构决策（主 Agent 冻结契约，2026-11-07）

以下 D1–D9 为主 Agent 冻结决策的如实转录；D10（文件所有权）记录于第 4 节。

### D1 审批模式

删除 7 值 `SessionPermissionMode` 运行时。新增 `SessionApprovalMode { HumanApproval, AIAutoReview }`，
标签"人工审批"/"AI 审核"，默认 `HumanApproval`。引擎取消 `Mode=null` 传统路径，CLI 同引擎。
`composerModes` 概念取消。

### D2 执行后端与三级分类

`ExecutionBackend = WindowsSandbox | HostProcess`（复用 `ProcessExecutionBackendKind`）。
Level1 = 沙箱基线自动（工作区读写、读根读取、沙箱内 Shell）；
Level2 = 沙箱内附加能力（工作区外路径 Read/Write、受保护元数据写入、网络 enabled）；
Level3 = Host 单次显式申请（绑定完整调用，仅 SingleOperation）。
Level 是展示/审计分类，非授权来源。沙箱未配置/不可用 → `process.execute` 一律 Deny（fail closed），
Host 申请同样拒绝；文件工具仍可用。

### D3 Grant

`Grant { GrantId, Scope, Capability, Constraint, Targets, PolicyIdentity, CreatedUtc, ExpiresUtc, State }`；
`GrantScope { SingleOperation, Turn, Session }`；
`GrantState { Active, Consumed, Revoked, Expired, Superseded }`。
Turn = 一次已接受的用户任务及其工具循环（run 终态撤销）；
Session 仅当前运行实例、不跨重启、恢复不自动续；
单次授权原子消费（lock，并发不共用）；
参数/目录/环境/后端/策略/工作区修订任一变化 → 旧 Grant 失效；
撤销不回滚已发生修改（如实展示）。Host = 仅 SingleOperation。

### D4 审批协调

`IApprovalProvider` 改形 `PromptAsync(ApprovalRequest) → ApprovalResolution`
（`Granted{Scope}` / `Denied` / `Unavailable`）；
`AllowedScopes` 按能力类别（host 仅单次；元数据写/工作区外文件/网络 `[SingleOperation, Turn]` 默认单次；
Session 需显式选择并明示"仅当前运行实例"）。
AI Reviewer 进 Core：结构化输入（`UserUtterance`/`EffectiveAmendment`/`Revocation`/`TrustedPolicy`/
`SystemNotice`/`PendingOperation`/`ToolEvidence`/`GrantHistory`，带 `RecordId`）
+ 严格输出协议 `{"decision":"allow|deny|escalate","riskLevel","userAuthorization","reason"}`；
Allow → 放行 + Grant；Deny → 拒绝记录、保持未执行、绝不弹人工卡；
EscalateToUser → 同一冻结计划交人审，人批只覆盖原计划；
技术失败 `{ReviewUnavailable, InvalidResponse, Timeout, Cancelled}` → 不执行、不转人工、状态明确、
可手动重试审核。`RiskLevel { Low, Medium, High, Critical }` 与
`UserAuthorization { High, Medium, Low, Unknown }` 是审核维度非授权。
用户明确禁止必须拒绝；拒绝记录绑定操作指纹抑制同卡重弹（跨工具语义等价不声称）。

### D5 引擎决策链

硬限制（worker/未信任只读/NeedsInspection/权限系统资源）→ deny 记忆 →
资源分类（`ResourceClass`：`BaselineWorkspace`/`AdditionalReadRoot`/`AdditionalWriteRoot`/
`ProtectedMetadata`/`PermissionSystemProtected`/`OutsideWorkspace`；
Level2 通道 = grant 驱动沙箱 ACL 扩展，grant 根并入 PolicyIdentity，凭据目录重叠校验沿用）→
Level1 Allow → Grant 匹配 → Ask 按 ApprovalMode 路由。未知能力 Ask fail-closed。
执行入口 Revalidate 复核。

### D6 网络真实边界

仅 enabled = 沙箱内全网（经用户代理）唯一可执行档；窄域名授权未实现
（Codex proxy-enforced/restricting SID 未启用），不添加假接口；
审批与文档如实写明全网范围；现有 `networkPolicy`/`proxy`/附加读写根设置原样保留为独立维度。

### D7 文件工具 = 可信宿主代理

四工具（list_files/search_text/read_file/apply_patch）保持宿主进程内执行，
统一经 `ResourceAccessPolicy`（`PermissionSystemProtected` 硬拒）；
文档如实声明该策略属可信计算边界、非 OS 强制；
工作区内秘密文件不自动识别（集中分类器仅覆盖已知保护类）。

### D8 search_text 契约

`mode` `literal|regex`（缺省 literal 行为不变）；
regex = `System.Text.RegularExpressions` NonBacktracking 引擎
（文档列明支持语法子集：无环视/反向引用/条件等；不支持的表达式明确报错，不静默换引擎）；
`pattern` ≤ 512 字符；include/exclude glob（`*`/`**`/`?`/`[...]`），
只过滤已授权扫描集、不扩大访问范围；`caseSensitive` 缺省 false
（同时控制 regex IgnoreCase 与 glob 大小写）；
`contextBefore`/`contextAfter` 0–5 行，相邻重叠合并；
`outputMode` `content|files|count`（缺省 content 兼容现有格式）；
按行匹配，不做跨行正则与替换；
预算 = 现有常量（3000 文件/4MB/240 列显）+ 总输出预算（64k 字符截断标记）+ regex MatchTimeout；
超时/截断/部分计数必须显式标注，不得显示为"无匹配"；
files/count 仍受读取权限约束，策略排除文件不出现在文件名/计数/片段中。

### D9 迁移

旧 7 模式 → 映射（`Review*`/`AutoReview` → `AIAutoReview`，其余 → `HumanApproval`）仅供迁移/展示；
新 selection schema `tinyharness.permission-selection/2 { Revision, ApprovalMode }`；
新必需记录 kind `permission-migration/1 { Revision, CompletedUtc, PreviousMode?, ApprovalMode, PolicyVersion }`
（旧程序自动拒读）；
恢复判定：含 /1 selection 或无 selection 且有历史 run → `MigrationPending`，确认前禁止模型工具执行；
迁移确认展示：新基线允许工作区读写与沙箱 Shell、附加权限走两种审批、FullAccess 已删除、
旧临时授权与 commandRules 免审批规则不自动取得新权限；
旧配置 `defaultMode`/`composerModes` → 检测即置迁移待确认态（不拒载其余配置），
确认后改写 `settings.permissions` 为 `{defaultApprovalMode, useCurrentModel, reviewerProfile, reviewerModel}`；
commandRules 保留解析但运行时不再消费；
networkPolicy/附加读写根/代理设置平移；
`SessionIndexStore` 版本 bump 重建 + 容忍解析；
OfflineSmoke 同步改两模式。

### 裁决补充（2026-11-07 批 1–4 验收后）

- **摘要显示层**：`SessionSummary.PermissionMode` 保留字符串字段 + 容忍映射——新值显示
  "人工审批"/"AI 审核"，旧值经 `LegacyPermissionSelectionMapper` 显示旧标签，未知值显示
  "旧版模式"；仅展示，绝不作为运行时授权。模型类型归 S2，S3 接线。
- **迁移确认 UX**：Desktop = 会话视图通知条 + "确认迁移"按钮（对齐 NeedsInspection 确认交互）；
  CLI = chat 启动检测到待迁移会话时交互 y/n 提示，非交互为明确阻断状态。
- **CLI 冻结新增** `/approval human|ai`：空闲边界切换会话审批模式，经 selection 记录持久化，
  与 Desktop 同机制。
- **验收矩阵校准**：任务书第十五节实际共 51 场景（非 30），按五组对位第 9 节。
- **批 1–4 验收通过**；两处机械适配（索引版本常量化、旧形状往返补 ComposerModes）接受并记录在案。

## 4. 文件所有权（D10，唯一写入者）

| 范围 | 文件 |
|---|---|
| S1（执行侧） | `Services/Runtime/**` 与 `Models/Runtime/**` 全部（含 WindowsSandbox）、`ResourceAccessPolicy.cs`、`SensitivePathClassifier.cs`、`Services/Tools/{ShellTool, PermissionShellTool, ITool, ToolRegistry, ToolArgs, JsonArgs, ReadFileTool, ListFilesTool, ApplyPatchTool, PatchApplier, PatchParser}.cs`、`Models/Tools/**`、`Services/Agent/AgentLoop.cs`（执行派发侧）、CLI `SandboxVerifyCommand` 扩展、执行侧测试 |
| S2（授权侧） | `Models/Permissions/**`、`Services/Permissions/**`、`AgentLoop.Permissions.cs`（AuthorizeAsync）、`ChatSessionHost*.cs` 权限点、`Desktop.Runtime/{DesktopApprovalProvider, DesktopAutoApprovalProvider, DesktopSessionFactory.Permissions}.cs`、CLI `ConsoleApprovalProvider.cs`、权限持久化模型（selection/2、migration 记录类型）、授权侧测试 |
| S3（产品侧） | `SearchTextTool.cs` 全文件、ReadOnlyToolsTests/SearchText 新测试、`Models/Configuration/{PermissionSettings, UserConfigStore, UserConfigValidator}.cs`、`SessionLogStore/SessionLogScanner/SessionResume/SessionIndexStore/SessionSummary*`（selection/2 与 migration 接线、版本门）、`Models/Persistence` 接线与 JsonContext 注册、Desktop UI（PermissionSettingsViewModel/View、SessionViewModel*.cs、PermissionSelectorView、MainWindow 审批卡、WindowsSandbox 设置节联动）、`DesktopSessionFactory.cs`、`DesktopSession.cs`、`OfflineSmoke.cs`、CLI `Program.cs`/`HelpText.cs`/`ChatSessionConsole` 迁移交互、全部文档（本计划、permission-modes.md 重写、Docs/README、中英 README、CHANGELOG、PLAN.md 入口）、产品侧测试 |

共享规则：`AgentLoop.cs` = S1、`AgentLoop.Permissions.cs` = S2；
`Models/Persistence` 记录模型 = S2 写、S3 接线；冲突一律经主 Agent。
批 2 的 `WorkspacePrompts`（CLI 与 Desktop.Runtime 两份）按任务分派由 S3 更新 search_text 描述。

## 5. 进度

- **Phase 0（调查与记录）**：已完成。产品面、配置面、持久化面、search_text、
  SessionPermissionMode 引用清单、迁移矩阵草案调查报告已交付主 Agent。
- **Phase 1（契约冻结）**：已完成。主 Agent 冻结 D1–D10，S3 已转录至第 3、4 节与验收矩阵。
- **Phase 3（S3 产品侧切片）**：批 1–4 已完成并验证。
  - 批1：契约转录 + PLAN.md 入口行（已完成）。
  - 批2：`SearchTextTool` 全契约（mode/regex/glob/context/outputMode/预算与部分标注）+
    `ResourceAccessPolicy` 接线（CLI 与 Desktop 调用点）+ 两份 WorkspacePrompts 描述 +
    `SearchTextContractTests`（已完成）。
  - 批3：`PermissionSettings` 双形状（legacy `DefaultMode`/`ComposerModes` 可空 ⟺ 旧形状；
    `DefaultApprovalMode`/`PendingMigration`）+ `UserConfigStore` 双形状读取与形状随实例写出 +
    桌面消费点适配（已完成；S2 的 `SessionApprovalMode` 已落地，直接使用真实枚举，无需字符串过渡）。
  - 批4：`SessionIndexStore` `CurrentSchemaVersion` 1→2、`NeedsRebuild` 状态、`RebuildSchemaAsync`、
    `permission_mode` 列容忍解析；Updater/Reconciler/Desktop 侧栏联动（已完成）。
  - 批5（文档轮，验收裁决后）：permission-modes.md 重写草稿（文首标注 Phase 5 终稿）、
    第 9 节按 51 场景五组重排、Docs/README 导航与中英 README 权限段落草稿、
    3 个补充测试（全七模式映射、非法正则语法、中文/Unicode 边界）。
- **Phase 4（S3 存储接线批）**：已完成实现并验证（存储/判定/门禁全绿；详见第 6 节归因）。
  - `SessionLogStore.ValidateControlRecord` 按 schema 分发接受 selection/2，新增
    `permission-migration` kind（`PermissionPersistenceJsonContext`）；扫描器同步接受。
  - 投影构建器：/1 与 /2 共用一条连续修订链（迁移记录与伴随 /2 选择共享修订号，两种提交顺序都接受，
    重复迁移拒绝）；`PersistedSession` 新增 `PermissionSelectionV2`/`PermissionMigration`。
  - `SessionResume` 应用 /2 与迁移记录并按 `PermissionSelectionMigration.Evaluate` 计算判定
    （已迁移的 /1 不再触发待确认）；`ChatSession` 新增 `PermissionSelectionV2`/`PermissionMigration`/
    `PermissionMigrationPending`。
  - 宿主最小接线（S2 文件，协调者分派的恢复判定接线所需，请主 Agent 复核）：ComposeRunLoop 优先 /2；
    迁移待确认门禁按验收裁决的严格口径实现——所有模型工具能力一律 Deny（含只读，无例外），固定理由
    "permission migration pending; confirm to enable tool execution"，经工具门在工具边界生效
    （工具保持注册、模型视图不变、work items 持久化与历史恢复不受影响），确认成功解除；
    ChangeApprovalModeAsync/ConfirmPermissionMigrationAsync 修订链取 /1+/2 最大值并镜像会话事实。
  - 新测试 `PermissionSelectionPersistenceTests`（/2 往返、混合链、迁移修订共享与重复拒绝、
    恢复判定矩阵、确认解除门禁）；`ChatSessionPersistenceTests` 两处旧场景补迁移确认（D9 语义）。
- **Phase 2（S1 执行侧 / S2 授权侧）**：并行进行中；其 batch 1 已在同一工作区落地
  （SessionApprovalMode、Grant/GrantScope/GrantState、GrantRegistry、ApprovalRequest、
  PermissionEngine SetApprovalMode/Evaluate/Revalidate、ResourceAccessPolicy 接线），
  S2 仍在对齐 IApprovalProvider 及其测试；S3 未代写其文件。
- **Phase 4–7（迁移收口、验收、发布准备）**：待办，范围由主 Agent 分派后填写。

## 6. 测试命令与结果

| 批次 | 命令 | 结果 | 状态 |
|---|---|---|---|
| 批1 | 纯文档，diff 与 Markdown 结构检查 | 小节齐全、表格渲染正常 | 已验证 |
| 批2 | `dotnet test TinyHarness.Tests --filter SearchTextContractTests` | 22/22 通过 | 已验证（离线） |
| 批2 | `dotnet test TinyHarness.Tests --no-build --filter ReadOnlyToolsTests` | 35/35 通过（literal 兼容回归） | 已验证（离线） |
| 批3+4 | `dotnet test TinyHarness.Tests --filter "PermissionSettingsMigrationTests&#124;SessionIndexStoreVersionTests"` | 12/12 通过 | 已验证（离线） |
| 回归 | 上述 8 个套件合计（含既有 UserConfigStore/SessionIndexStore/Reconciler/SessionPermissionMode 套件） | 143/143 通过（含两处机械适配，见第 7 节） | 已验证（离线） |
| 构建 | `dotnet build TinyHarness.Core / TinyHarness.Tests / TinyHarness.Desktop / TinyHarness.Cli` | 全部 0 error | 已验证（离线） |
| 批5 | `dotnet test TinyHarness.Tests --filter "SearchTextContractTests&#124;PermissionSettingsMigrationTests"`（含 3 个新测试） | 39/39 通过 | 已验证（离线） |
| 批5 | 纯文档 diff 与 Markdown 结构检查（指南/计划/双语 README） | 小节齐全、表格渲染正常 | 已验证 |
| Phase4 | `dotnet test TinyHarness.Tests --no-build --filter "PermissionSelectionPersistenceTests&#124;ChatSessionPersistenceTests&#124;SessionJournalFaultTests&#124;SessionWorkItemTests&#124;SessionPermissionModeTests&#124;UserConfigStoreTests&#124;SearchTextContractTests&#124;SessionIndexStoreVersionTests&#124;ReadOnlyToolsTests"` | 151/151 通过 | 已验证（离线） |
| Phase4 | `dotnet test TinyHarness.Tests --no-build`（全量） | 1785/1791，2 失败＝S2 新增"当前审批模式"系统提示注入与压缩恢复/提示词测试未对齐（CompactionRecoveryTests、WorkspacePromptTests） | 部分验证（失败非 S3 改动面） |
| Phase4 | `dotnet test TinyHarness.Desktop.Tests --no-build`（门禁严格口径后） | 336/343，7 失败＝5 个 AiReview 用例 + ModeSwitchPersists/ProjectExternalWrite，全部位于 S2 进行中的 AI 审核进 Core 与 legacy 剥离面（其已将工厂开场写入切到 /2），非 S3 改动面 | 部分验证 |
| Phase4 | 门禁严格口径复验：`dotnet test TinyHarness.Tests --no-build --filter "PermissionSelectionPersistenceTests&#124;SessionWorkItemTests&#124;SessionJournalFaultTests&#124;ChatSessionPersistenceTests"` | 47/47 通过（含待确认期只读工具同样拒绝、理由固定、确认后恢复） | 已验证（离线） |
| Phase4 | `dotnet test TinyHarness.Tests --no-build`（门禁严格口径后全量） | 1785/1791，仅剩 2 失败＝S2 "当前审批模式"系统提示注入与压缩恢复/提示词测试未对齐（非 S3 改动面） | 部分验证 |
| Phase4 | `dotnet build TinyHarness.Core / Tests / Desktop / Desktop.Tests / Cli` | 全部 0 error | 已验证（离线） |

regex MatchTimeout 的实际触发路径无确定性单测（NonBacktracking 线性引擎难以稳定构造超时；
构造期携带 MatchTimeout 由 `RegexCompilationAndExecute_AgreeOnTimeoutBudget` 覆盖合法路径），
超时标注代码路径以审查为准，记为未验证。
NativeAOT publish 未在本批执行（后续综合验收切片覆盖）。

## 7. 阻塞与风险

- 任务书第十五节 51 场景已按五组校准进第 9 节（主 Agent 2026-11-07 裁决）。
- **[Verified，真实 OS，S1 原文转录]** 元数据写 Level 2 在新工作区可用性取决于 deny ACE 历史：
  工作区一旦被默认策略 refresh 过（含同运行内先行的负对照），`.git` 等元数据目录的 deny ACE
  持久存在且跨会话/跨运行不回收（Rust setup helper 不回收 deny ACE，与写根 ACE 收敛行为不对称，
  icacls 证据：capability SID 显式 DENY 对象级+可继承两条）；此后 metadata 写 grant 在该工作区
  不可用，直至 deny ACE 被外部清理或 helper 增加回收语义。该限制为可用性缺口，安全方向无缺口
  （失败方向=拒绝）。修复需修改外部 fork（codex-windows-sandbox-setup deny-ACE 回收），
  属单独立项，本任务不实施。
- 共享工作区并行编辑：S2 中途重塑 `IApprovalProvider`（PromptAsync(ApprovalRequest)）期间
  Core/Tests 构建短暂中断，S3 轮询等待其落地后完成验证；期间未触碰 S1/S2 文件。
- 为保持既有套件与契约一致，S3 做了两处跨文件机械适配（均为既有断言与 D9 直接冲突），
  请主 Agent 复核：① `SessionIndexStoreTests.RemoveAndClear_KeepSchemaAndMeta` 的硬编码
  schema_version "1" 改为 `CurrentSchemaVersion` 常量；② `SessionPermissionModeTests.
  ConfigurationRoundTripPreservesAllModesAndReviewer` 的旧形状构造补显式
  `ComposerModes = SessionPermissionModes.All`（`ComposerModes` 现为形状判别字段，旧缺省值已移除）。
- 批 4 语义变化：索引旧版本（v1）从"停用等人工介入"改为"自动整库重建（仅索引，不改写日志）"，
  是 D9 的直接要求；更高版本仍保持 Unsupported 拒绝。
- `DesktopSessionFactory.cs` 与 CLI `Program.cs` 在本批期间被 S1/S2 接入 ResourceAccessPolicy；
  S3 的 search_text 接线基于其改动追加，未回退任何并行修改。
- 迁移门禁的契约后果（D9 严格口径）：任何"无审批模式选择且有历史运行"或"含未迁移 /1 选择"的会话，
  恢复后确认前所有模型工具能力一律拒绝（含只读）。既有测试中恢复旧会话后调用工具的场景需先确认迁移
  （已适配：ChatSessionPersistenceTests 两处、SessionJournalFaultTests 一处、SessionWorkItemTests 一处）；
  桌面新会话仍在 Phase 4 保持 /1 开场写入，其迁移待确认语义与 /2 工厂切换随 Phase 5 落地。
- S2 的临时诊断文件 `TempReproDiag.cs` 在共享工作区未完成期间阻塞 Tests 编译，S3 轮询等待其清理，
  不代写不删除。

## 8. 下一步动作

- S2 的 AllowOnce/审批管线与 legacy 剥离在验证窗口内持续演进：门禁测试的 post-confirm 执行断言
  曾因其中间态失败，S2 最新落地后已通过；Desktop 剩余 7 个失败（AiReview 与 /1 开场切换语义）
  属其进行中面，待其收口后复核。
- Phase 5（待主 Agent 在 Phase 4 垂直集成贯通后分派，勿提前动会话流）：
  Desktop 双模式菜单与迁移确认通知条、"确认迁移"按钮、selection/2 与 permission-migration/1
  接线、恢复 MigrationPending 门、OfflineSmoke 双模式适配、CLI `/approval human|ai` 与
  chat 迁移 y/n 交互。
- 摘要显示层映射接线待 S2 的 `SessionSummary` 模型改为字符串字段后由 S3 落地
  （新值"人工审批"/"AI 审核"，旧值旧标签，未知值"旧版模式"，仅展示）。

## 9. 验收矩阵

按主 Agent 校准后的任务书第十五节 51 场景排列（五组）；列"状态"区分
已验证（离线）／部分验证／待办／待复跑（重构前真实 OS 验收通过，重构后需重新执行）。

### 组一：权限与后端（13）

| # | 场景 | 测试名/验证方式 | 验证方式 | 负责 | 状态 |
|---|---|---|---|---|---|
| P1 | 基础工作区读写成功（Level 1 基线自动） | PermissionEngineTests（Level 1 放行） | 离线 | S2 | 待办 |
| P2 | 未授权区域写入失败 | PermissionEngineTests（OutsideWorkspace 路由） | 离线 | S2 | 待办 |
| P3 | 已知受保护资源读取或写入失败 | ResourceAccessPolicyTests + SearchTextContractTests.FileResolvingIntoProtectedPath | 离线 | S1/S3 | 部分验证（search_text 侧已验证；其余工具归 S1） |
| P4 | 子进程不能越过相同资源边界 | WindowsSandboxAcceptanceRunner（metadata-protected/write-denied 用例） | 真实 OS | S1 | 待复跑（既有真实 OS 验收通过） |
| P5 | Sandbox 不可用时不启动 Host（process.execute Deny fail closed） | SandboxFailClosedTests（新）+ sandbox verify 扩展 | 离线 + 真实 OS | S1 | 待办 |
| P6 | Level2 始终留在 Sandbox；metadata-write-grant-allowed | PermissionDecisionLevelTests（新）+ 真实 OS grant 用例（grant-write-positive / grant-revoked-write-denied / grant-identity-changes） | 离线 + 真实 OS | S2/S1 | 部分实现（新工作区可用性取决于 deny ACE 历史，真实 OS 验证 BlockedExternal）；grant-write-positive/grant-revoked-write-denied/grant-identity-changes 三用例真实 OS PASS |
| P7 | Host 必须有对应单次授权 | HostScopeGrantTests / GrantLifecycleTests | 离线 | S2 | 待办 |
| P8 | 参数/目录/环境/后端/策略变化使旧授权失效 | GrantLifecycleTests（失效矩阵）；grant-identity-changes 真实 OS PASS（S1） | 离线 + 真实 OS | S2/S1 | 待办（S2 离线用例；真实 OS 对照已 PASS） |
| P9 | 用户取消后不执行不重放 | SessionRunControlTests + 审批卡 UI 断言 | 离线 | S2/S3 | 待办 |
| P10 | 单次授权不能被两个并发请求消费 | GrantLifecycleTests（并发消费） | 离线 | S2 | 待办 |
| P11 | A Session 授权不能被 B Session 使用 | GrantRegistryTests（实例隔离） | 离线 | S2 | 待办 |
| P12 | Turn 到期后新执行不携带旧能力 | GrantLifecycleTests（Turn 终态） | 离线 | S2 | 待办 |
| P13 | 结果未知时不自动重试 | AgentLoop 执行派发测试 | 离线 | S1/S2 | 待办 |

### 组二：网络（8）

| # | 场景 | 测试名/验证方式 | 验证方式 | 负责 | 状态 |
|---|---|---|---|---|---|
| N1 | 离线负对照 | sandbox verify --network restricted | 真实 OS | S1 | 待复跑（既有 17/17 真实 OS 验收通过） |
| N2 | 获准网络目标正对照 | sandbox verify --network enabled | 真实 OS | S1 | 待复跑（既有 8/8） |
| N3 | 未获准目标负对照 | verify restricted 拒绝用例 | 真实 OS | S1 | 待复跑（既有） |
| N4 | 直接 IP/绕过代理/环回等协议路径验证 | verify 网络用例（AcceptanceLimits 端点） | 真实 OS | S1 | 待复跑（既有部分覆盖） |
| N5 | 代理失败不退回直连 | verify 坏代理快速失败用例（非挂起） | 真实 OS | S1 | 待复跑（既有 exit 7 对照） |
| N6 | 窄网络授权不会变成全网（窄请求→明确错误，绝不静默放宽） | 审批协议无窄授权字段断言 + 组合期 fail-closed | 离线 | S2/S1 | 待办 |
| N7 | TLS 与真实工具链问题单独记录 | phase2 交付报告 §7 + 指南保留 | 文档 | S1/S3 | 已记录（文档） |
| N8 | HTTP 可达不代表 HTTPS/NuGet restore 已通过 | 指南网络节如实声明 | 文档检查 | S3 | 已验证（本批文档草稿） |

### 组三：文件与搜索（12）

| # | 场景 | 测试名/验证方式 | 验证方式 | 负责 | 状态 |
|---|---|---|---|---|---|
| F1 | read/list/search/patch 权限一致 | ResourceAccessPolicyTests（四工具）+ S3 search 侧 | 离线 | S1/S3 | 部分验证（search_text 侧已验证） |
| F2 | glob 不扩大目录范围 | SearchTextContractTests.IncludeGlob_NarrowsScanSet / ExcludeGlob_Removes | 离线 | S3 | 已验证（本批） |
| F3 | regex 不改变资源访问范围 | RegexMode 用例 + 既有 SearchText_Prepare_RejectsEscape 回归 | 离线 | S3 | 已验证（本批 + 回归） |
| F4 | literal 默认行为兼容 | LiteralExplicitMode_MatchesDefaultBehavior + ReadOnlyToolsTests 35/35 | 离线 | S3 | 已验证（本批） |
| F5 | 合法/非法/不支持正则 | RegexMode_MatchesPerLine / InvalidSyntax / UnsupportedSyntax / Over512Characters | 离线 | S3 | 已验证（本批） |
| F6 | 大小写/中文/Unicode 边界 | CaseSensitiveToggle_IgnoreCaseFollows + UnicodeAndChinesePatterns_MatchPerLine | 离线 | S3 | 已验证（本批） |
| F7 | 上下文合并与输出截断 | ContextLines_MergeAdjacentWindowsAndMarkMatches + OutputBudgetExceeded_TruncatesWithExplicitMarker | 离线 | S3 | 已验证（本批） |
| F8 | content/files/count 语义 | OutputModeFiles / OutputModeCount 用例 | 离线 | S3 | 已验证（本批） |
| F9 | 超时/取消/超长行/增长文件/扫描预算 | 预算截断、240 列显、部分计数已验；MatchTimeout/取消/增长文件无确定性单测 | 离线 | S3 | 部分验证（余项代码审查 + 综合验收） |
| F10 | 敏感结果不经文件名/计数/上下文泄露 | FileResolvingIntoProtectedPath_IsSkippedCountedAndNeverNamed + 既有 worker 排除用例 | 离线 | S3/S1 | 已验证（search 侧） |
| F11 | 链接/重解析点/目标变化 | 既有 symlink 用例（随 35/35 运行）+ ResourceAccessPolicy 最终路径比对 | 离线 + 真实 OS | S3/S1 | 已验证（离线） |
| F12 | 计数截断不显示为完整统计 | OutputModeCount_PartialCountIsExplicitlyMarked | 离线 | S3 | 已验证（本批） |

### 组四：Reviewer（10）

| # | 场景 | 测试名/验证方式 | 验证方式 | 负责 | 状态 |
|---|---|---|---|---|---|
| R1 | 合理小范围请求可 Allow | AiReviewerRoutingTests（新） | 离线 | S2 | 待办 |
| R2 | 用户明确禁止时 Deny | AiReviewerRoutingTests（新） | 离线 | S2 | 待办 |
| R3 | 相关但超委托范围时 EscalateToUser | AiReviewerRoutingTests（新） | 离线 | S2 | 待办 |
| R4 | Deny 不弹人工批准 | AiReviewerRoutingTests（新） | 离线 | S2 | 待办 |
| R5 | Escalate 只执行原计划 | AiReviewerRoutingTests（新） | 离线 | S2 | 待办 |
| R6 | 超时/非法 JSON/模型不可用保持未执行 | AiReviewerFailureTests（新） | 离线 | S2 | 待办 |
| R7 | 不可信内容不能伪造用户授权 | AiReviewerProtocolTests（来源标记 + RecordId 注入断言） | 离线 | S2 | 待办 |
| R8 | 用户撤销优先于旧授权 | AiReviewerRoutingTests（撤销顺序） | 离线 | S2 | 待办 |
| R9 | 文件读取与外发授权分开判断 | AiReviewerProtocolTests（分开判断断言） | 离线 | S2 | 待办 |
| R10 | 不把 mock 输出当作真实模型抗注入证明 | 验证诚实性条目：指南声明 + 真实模型 opt-in 综合验收 | 文档 + 真实模型 | S3/主 Agent | 部分验证（指南已声明；真实模型验收待综合阶段） |

### 组五：迁移（8）

| # | 场景 | 测试名/验证方式 | 验证方式 | 负责 | 状态 |
|---|---|---|---|---|---|
| M1 | 七旧模式全部有明确处理 | PermissionSettingsMigrationTests.LegacyDefault_AllSevenModesMapExplicitly | 离线 | S3 | 已验证（本批） |
| M2 | ReadOnly 不静默获得写权限；迁移确认展示新基线允许工作区写入 | PermissionSettingsMigrationTests + 迁移确认 UI（Phase 5） | 离线 | S3 | 部分验证（配置层已验；UI 待 Phase 5） |
| M3 | FullAccess 不恢复自动 Host | 引擎 Host 意图测试（S2）+ 指南声明 | 离线 | S2/S3 | 待办 |
| M4 | 旧常设网络与命令规则不成为隐蔽授权（networkPolicy 平移须展示、commandRules 不再消费） | 引擎不消费 commandRules 断言（S2）+ networkPolicy 平移测试（S3） | 离线 | S2/S3 | 部分验证（配置平移随 UserConfigStoreTests；引擎断言待 S2 切换后） |
| M5 | 历史日志仍可展示 | SessionIndexStoreVersionTests（容忍解析）+ 旧日志展示映射接线（Phase 5） | 离线 | S3 | 部分验证（索引侧已验） |
| M6 | 迁移失败保持阻断 | MigrationGateTests（新，Phase 5） | 离线 | S3/S2 | 待办 |
| M7 | 运行期 Grant 不跨重启自动恢复 | GrantRegistryTests（不持久化断言） | 离线 | S2 | 待办 |
| M8 | Legacy 解析代码不能成为旧模式运行入口 | 引擎 SetApprovalMode 全量切换后编译 + 引用断言（S2）；配置层旧形状仅映射不进运行时（S3 已验） | 离线 | S2/S3 | 部分验证（配置侧已验） |

## 10. 提交拆分与本轮修复（2026-11-07）

本节为当前复核结果；以上批次状态保留为历史实施记录，不代表整项功能已经验收。

### 已提交的独立部分

- `e0d4eb5`：记录权限架构契约与实施进度。
- `bcf6265`：独立搜索增强，包含搜索契约测试与 CLI/Desktop 提示词；不含新资源策略接线。
- 其余权限、执行、迁移及产品适配继续留在工作区，本轮没有提交这一部分。

### 本轮修复

- 执行前复核必须具有对应的有效授权；不再因找不到活动 Grant 而直接放行。
  已获批调用绑定一次性内部派发凭据及授权修订，撤销、模式变化、授权到期或并发消费失去授权时不执行。
- 冻结能力与当前快照必须一致；新增或撤销能力均使旧计划失效，不能继续携带已撤销的路径权限。
- 相同审批模式的重复应用保持幂等，不撤销 Session Grant；显式切换仍撤销授权。
  相同已保存选择不重复追加修订，迁移重复确认在宿主空闲锁内拒绝。
- Host 不接受 Turn/Session Grant；网络申请不会随普通文件基线自动放行，也不覆盖系统保护路径的硬拒绝。
- 迁移测试先构造并关闭旧日志写入端，再恢复宿主接管唯一 writer；不放宽生产存储锁。
- Desktop 测试改为双审批模式契约；旧权限断言区分基线允许与 Grant 匹配，不恢复旧七模式运行行为。
- CLI 离线 smoke 使用基线文件策略及六次显式 Host 单次审批；保留工具数和审批数断言，失败时报告实际计数。
  该脚本测试组合不是机器沙箱可用性或 OS 隔离证明。
- 仅对本轮修改的 C# 文件执行 `banned甄选` cleanup 并复核；没有修改共享格式、依赖或 AOT 设置。

### 验证结果

| 验证 | 本轮结果 |
|---|---|
| Solution Debug 构建 | 0 error；8 条 CS4014 来自基线已有测试，不属于本轮新增警告 |
| 权限与迁移针对性回归 | 135/135 通过，包含 7 个新增授权回归用例 |
| Core/CLI 全量测试 | 1796 项：1784 通过、3 失败、9 跳过 |
| Desktop 全量测试 | 342 项：341 通过、0 失败、1 跳过 |
| CLI 最后增量回归 | 命令行、审批与提示词 40/40 通过 |
| CLI/Desktop win-x64 NativeAOT | 两端本地 publish 成功，未见 AOT/trimming warning |
| 原生产物离线 smoke | 源码目录外 CLI `--help`、完整 `smoke` 与 Desktop `--offline-smoke` 均 exit 0 |
| Diff | `git diff --check` 通过，第三组暂存区为空 |

标准 `dotnet test` 的 testhost 在本环境因监控父进程时拒绝访问而中止；上述测试通过安装包内
`NUnit.Framework.Api.NUnitTestAssemblyRunner` 直接执行原程序集、原夹具与断言，不等同于标准 testhost 已通过。
Core 剩余三项为 `ShellToolTests` 的超时/取消和 `WindowsSandboxAcceptanceTests.SurvivorsAfterCancellation_FailTheCase`；
在未含权限重构的对照副本中同样失败，当前环境的 `cmd` 无法解析其依赖的 `ping` 命令。
本轮没有为得到全绿而修改这些既有进程夹具或机器配置。

CLI 原生 smoke 通过了工具、补丁、进程、日志恢复、项目指令、SQLite 索引、任务控制、预算与响应恢复检查；
Desktop smoke 不初始化窗口，不能替代真实 GUI 验收。具体日志与测试 XML 位于本地
`.tmp/permission-repair-20261107/`，不纳入提交。

### 仍未完成的范围

截至本节记录时，迁移确认通知条与按钮、CLI `/approval` 和迁移交互、历史模式展示仍待产品收口；
后续工作区变化以第 11 节为准。
真实 GUI、真实模型 Reviewer 效果及新权限链路的机器沙箱综合验收尚未完成。
第 7 节记录的外部 setup helper 元数据 deny ACE 回收限制仍存在，本轮未修改外部 fork 或机器安全配置。
因此，不能以本轮修复和离线结果宣称整项权限架构任务完整完成。

## 11. 当前工作区可提交性收口（2026-11-07）

本轮最终范围为修复现有工作区并完成必要本地验证，不继续按长计划补齐功能或综合验收。
先前已写入工作区的迁移通知、CLI `/approval`、历史标签和审批范围接线保留；
其存在不代表 Phase 5 或整项权限架构已经验收完成。第 5–10 节保留当时记录。

### 修复与核对

- 修正新增接线中的类型、命名空间、跨程序集可访问性及旧菜单引用错误；保留显式 null 超时语义。
- CLI 的当前宿主使用独立 GrantRegistry，执行工具与引擎接入同源授权及沙箱冻结上下文；
  候选宿主失败时保留旧引擎与 Shell 配对。`t/turn` 正确进入审批回复通道。
- 配置和会话各自确认迁移；普通保存不解除配置门禁，失败保持阻断。
  旧配置字段显式为 null 时仍按旧形状处理，混合形状拒绝。
- 待迁移会话不能通过直接保存 /2 模式选择绕过重启后的门禁，必须先原子保存迁移确认记录。
- 修正取消异常断言及恢复夹具的 writer 归属，恢复读回断言使用实际持久化数据。
- 本轮涉及的 C# 文件使用 `banned甄选` cleanup，并检查实际输出和 diff；未修改共享格式设置。

### 验证边界

| 验证 | 最终结果 |
|---|---|
| Solution Debug 构建 | 0 error；8 条既有测试 CS4014 warning |
| Core/CLI 标准全量测试 | 发现 1844 项，执行的 1835 项全部通过；其余 9 项为跳过或显式用例，0 失败 |
| Desktop 标准全量测试 | 发现 342 项，执行的 341 项全部通过；1 项显式测量未运行，0 失败 |
| 迁移门禁与 CLI 审核针对性回归 | 17/17 通过，包含待迁移模式切换拒绝、持久化读回和取消传播 |
| CLI/Desktop win-x64 NativeAOT | 最终源码两端 publish 成功，未见 AOT/trimming warning |
| 最终原生产物离线 smoke | 源码目录外执行 CLI `--help`、完整 `smoke` 与 Desktop `--offline-smoke`，均 exit 0 |
| Diff 与暂存区 | `git diff --check` 通过，未执行 Git 写操作，暂存区为空 |

测试计数按 TRX 的发现与执行数记录。CLI smoke 验证 16 次工具执行与明确审批的脚本闭环；
Desktop smoke 验证并行会话、切换、恢复和释放，不初始化窗口。

标准 `dotnet test` 在本轮环境可用。验证进程继承的 PATH 过长，会使 `cmd` 无法解析
`ping` / `where`；仅精简本轮验证子进程的 PATH 后，原进程夹具和 NativeAOT 工具链可正常运行。
没有修改机器环境变量、沙箱账户、ACL、网络策略或现有进程夹具。

真实 GUI 操作、真实模型 Reviewer 效果、机器沙箱综合验收和外部 setup helper 的 deny ACE
回收仍未完成。沙箱可用性是组合时快照；初始组合失败后在同一入口通过 `/trust` 修复，
不保证旧引擎同步启用进程执行，失败方向为拒绝。这些边界不能被离线 smoke 替代。
验证日志与 TRX 保存在本地 `.tmp/permission-finish-20261107/`，不纳入提交。
