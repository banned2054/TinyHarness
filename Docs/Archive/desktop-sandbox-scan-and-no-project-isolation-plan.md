# Windows 沙箱读取策略与会话隔离（已归档）

> **2026-11-25 归档说明**：当前活动计划已切换为 [Windows 沙箱可用性收口计划](../windows-sandbox-readiness-plan.md)。第 1 节顶层读根实施记录与未验证边界原样保留；§2.3-1 的两份 Rust fork 补丁（缺失旧路径视为已清理、`setup_error.json` 完整错误链）转入新版计划继续收口；§2.3-2（每会话 OS 级并发不同读策略）经实测证伪原轻量候选且定位收敛回与 Codex 对齐的"全机统一固定读底线 + 按工作区隔离写"，退出当前实施范围；第 3 节（无项目会话独立工具目录）不再绑定每会话 OS 读隔离，留作后续事项。

原说明：当前优先项是[按会话隔离 Windows 沙箱读取权限](#windows-sandbox-session-isolation)。第 1 节保留 Desktop 顶层读根与精确拒读的原轮记录，第 3 节安排无项目会话的独立工具目录。CLI 初次进入目录时的 ACL 授权体验另见[后续计划](../cli-workspace-acl-preparation-plan.md)。

## 1. 顶层读根与精确拒读（原轮记录）

### 1.1 背景与范围

当前 Desktop 首次 workspace Shell 在生成 OS 拒读集合时递归枚举用户 profile。取消原 500,000 条目上限并加入默认 10 分钟预算后，2026-11-21 的无项目生产入口验收在枚举 3,737,580 条时超时，Shell 未启动。该结果验证了准备失败时不会启动命令，有项目组未运行，生产入口的普通文件可读和敏感文件拒读均未完成正反对照。

本轮覆盖启用 Windows 沙箱的 Desktop 有项目和无项目会话。保留 [L1／L2／L3 执行层级、审批和网络契约](permission-architecture-plan.md#permission-product-contract)。本轮修改的是 L1 的 OS 读取策略。CLI 现有扫描及启动流程暂时保留，两端共享的后端拒读状态纳入兼容性验收。

### 1.2 新读取策略

- **普通文件继续广泛可读。** 参考 [Codex 的顶层读根实现](https://github.com/openai/codex/blob/main/codex-rs/windows-sandbox-rs/src/setup.rs)，只枚举用户 profile 的直接子项。筛掉固定敏感位置，再把普通子项作为读根交给 helper。工作区、显式信任的根和运行时所需路径沿用现有规则。普通项目外文件仍可由 L1 直接读取，无需逐次人工或 AI 审查。
- **已知位置由 OS 拒读。** 复用现有固定敏感目录名单和实际 C1 保护路径，包括上游 Codex 的 `.codex`。固定敏感目录既从默认读根排除，也以精确路径下发拒读，覆盖已存在的广泛读 ACL。检查规范化后的真实落点，避免链接或其他读根把排除项目重新作为默认授读根。沿用 L2 的精确 C2 读取授权及撤销流程，保持 C1 硬保护和组件运行所需的既有例外。
- **收敛 C2 的 OS 承诺。** 首版依据明确的路径保护位置，普通读根内任意深度的同名 `.env`、`.pem` 等文件不再由 Shell 启动时自动发现。内置文件工具继续应用自己的敏感路径分类和审批规则。首版采用内置固定路径及现有配置派生的精确 C1 路径，新增用户拒读配置与 glob 展开留待有明确需求时设计。
- **准备失败保持关闭。** 顶层枚举或必需的 OS 拒读准备失败时，以具体路径和阶段报告错误，当前命令保持未启动。普通首次 Shell 采用现行非提权 refresh 路径，不进入 UAC 修复流程。顶层枚举失败时保持已声明的读边界，禁止退回到授权整个 profile。

### 1.3 当前工作区改动的处理

以当前工作区为起点，有选择地替换全量扫描链路。保留现有工作区修改、未跟踪验收载体和历史证据作为实施对照。

| 现有改动 | 后续处理 |
| --- | --- |
| Desktop 传入无条目上限、递归扫描和记录全扫条目数与耗时 | 按新策略切换正常入口。接线生效后，移除仅服务于 Desktop 全扫的参数、计量和专用测试。 |
| 延迟准备、取消与失败时命令未启动、聊天可继续的行为 | 保留可复用的执行链路与行为测试，将测试触发点迁到新策略。 |
| Core 扫描器、预算和测试 | CLI 仍有调用点。首轮保留 CLI 依赖的行为，按引用清理仅为旧 Desktop 方案增加的部分，避免把 CLI 或共用指纹逻辑一起回退。 |
| 隔离夹具和旧验收记录 | 继续用作历史对照。将可复用的正读与 OS 拒读探针接到新策略的 Desktop 正常入口，重新核验。 |

### 1.4 实施顺序

1. **先定义顶层读根与拒读的配对。** 让 Desktop 的策略生成与冻结执行计划共享同一结果。调整组合校验，支持给已经排除出默认读根的路径显式下发拒读。策略身份记录实际读根和拒读集合，配置复核沿用现有失效规则，避免每次消息或审批都枚举文件系统。
2. **验证固定 Windows 组件能否落实该策略。** 当前 runner 使用 `WRITE_RESTRICTED`，helper 授予共享沙箱组的读 ACL 会持续存在。缩小 `read_roots` 不能撤销旧授权。拒读集合又按安全主体同步，Desktop 和 CLI 如果共享主体及拒读状态，交替 refresh 可能撤掉另一端正在依赖的拒读项。先用已配置沙箱的隔离环境验证旧 ACL、关闭继承的子目录、真实落点和两端并行行为。固定组件无法维持覆盖时，在切换正式入口前补齐主体隔离或拒读状态协调能力。机器 ACL 修复、组件部署和真实环境操作分别授权。
3. **切换 Desktop 正常入口，收尾旧扫描接线。** 有项目和无项目的首次 workspace Shell 都走新策略。在拒读已生效后启动 runner，保持取消、失败不运行命令及不回退宿主的行为。CLI 初次聊天时的 UAC 提示归[独立后续计划](../cli-workspace-acl-preparation-plan.md)。

### 1.5 验收

| 场景 | 通过条件 |
| --- | --- |
| Desktop 正常入口 | 有项目与无项目各从正常入口启动首次 Shell。隔离 profile 夹具含大量深层条目，策略生成只读顶层和精确路径。记录实际读根、顶层条目数和首次准备总耗时。 |
| 用户体验 | 已配置沙箱、权限正常时，首次 L1 Shell 读取项目外普通文件成功，UAC、人工审批和 AI Review 均为零。普通聊天不会初始化 Shell 沙箱。 |
| OS 读保护 | 用真实 runner 和子进程确认普通路径可读、固定 C1／C2 路径由 OS 拒读。正反对照核对真实返回和退出状态。C2 经授权仅开放批准范围，授权结束后恢复保护。 |
| 升级与并行 | 隔离环境预先存在旧广泛读 ACL，无需为绕过旧扫描临时授读。验证关闭继承、链接落点以及 Desktop 与 CLI 交替／并行时的拒读覆盖。 |
| 准备异常 | 顶层枚举、ACL 应用、组件匹配失败和取消均保持命令未开始。聊天仍可用，错误指向具体阶段。 |
| 回归与真机 | 跑定向离线测试及受影响入口的 .NET 11 NativeAOT 和产物 smoke。真实 Windows 权限效果另行授权并单独验收。 |

### 1.6 实施状态（2026-11-22，WP5 指定用例复验；WP6 并发收口复验；WP6 复核修复轮；2026-11-23，WP6 `cap_sid` 写侧持门与门外读收口轮）

**2026-11-24 收尾。** 第 1 节的原实施与验收轮已结束。顶层读根和精确拒读已实现，WP5／WP6 指定的隔离环境用例通过。Desktop 正常入口（有项目与无项目）使用 `WindowsSandboxComposer.PlanBaselineReadProtectionTopLevel`，只枚举 profile 顶层并下发精确拒读。CLI 仍使用原扫描入口。下文保留原轮的验收结果和未覆盖的范围，收尾后发现的问题进入第 2 节。

实测失败（用户复核确认）：

1. **双向拒读收口缺口**：拒读簿记并集此前只在 Desktop 首次准备（`PlanBaselineReadProtectionTopLevelCore`）时生效；每条命令的 refresh 仍发送会话冻结的语义集，`WindowsSandboxBackend.PreflightAsync` 每命令 `_policy.CreateSetupPayload(...)` 后 `RefreshAsync` 会撤销另一端写入簿记的条目。实测证据：`.tmp/2026-11-22-desktop-sandbox-codex-read/wp3b/logs/probe-console-alternation.log`（R5"CLI 式 payload 不含 desktop 专属条目"→ desktop-store 的 deny ACE 被撤销；反方向同理）；会话冻结期间另一端新写入簿记的条目同样会被本端 refresh 撤销。
2. **冷启动读授权竞态**：真实 helper 在全新夹具首跑时读根授予后台异步落地晚于首批读取（WP3b 报告记录的"helper 后台授予竞态"）；生产 refresh 返回后只做写 ACL 继承校验，读根就绪无校验；0.7s 计量轮的 ACL 应用为 stub 顶替，不能作冷启动证据。

本轮修复（已实施，WP5 复验通过）：

- **修复一（每次 refresh 前合并拒读簿记）**。backend 和授权读恢复路径在构造 payload 后读入并合并当时可配对的簿记条目。并集相对于单次 payload 的语义集合只追加。簿记读取与 helper 更新之间仍有跨进程窗口，未配对的条目也可能被过滤。
- **修复二（读根就绪门）**：新增 `SandboxReadRootReadiness` 与共享后刷新校验步骤 `SandboxPostRefreshValidation`（写 ACL 继承校验 + 读就绪，顺序固定，两处 refresh 点共用；注入委托形态对齐既有 `validateWriteAcls`）。refresh 成功返回后、命令启动前，按 payload read_roots 通道有界轮询（默认总预算 15s、间隔 250ms）校验每个组读根 DACL 就绪：存在 `TinyHarnessUsers` 组 Allow 读 ACE，或存在 BUILTIN\Users / Authenticated Users / Everyone 的 Allow 读 ACE（helper 对已有内置读跳过授予的形态），缺一即未就绪；超预算 fail-closed（`ProcessExecutionStartException`，错误列出未就绪读根与阶段 "read-root readiness"）；已就绪时首轮即过，稳态开销 = 每读根一次 DACL 查询。只校验组读根，不校验 C2 授权读专用根；deny 侧不设门（deny 下发即时生效；junction 词法路径自身无 deny ACE，门控会误报——残余风险已在代码 XML 文档如实记录）。

WP5 复验（2026-11-22，真实 helper、无 stub，全部 ACL 操作限隔离夹具 `C:\Code\Temp\tinyharness-wp5-fx-20261122`；证据树 `.tmp/2026-11-22-desktop-sandbox-codex-read/wp5/`）：

- **冷启动（真实 helper + 真实 runner，全新未预授读夹具）**：有项目与无项目经 TH_R5 组装路径端到端矩阵（10 条命令、审批探针）全过；新增冷启动载体实测首命令总耗时 11.2s（含首次准备、3 次 per-command refresh 与 4 条命令；helper 首次 read-ACL worker 冷启动约 7.1s 为主要构成；对照 stub 计量 0.7s 与旧方案 600s fail-closed）。就绪门在首命令 preflight 生效：`validate` 事件 15–24ms 首轮即过（授予已在首次准备 helper 的异步窗口内落地，未发生等待——非零等待→放行形态未实测，等待→超界 fail-closed 已由反证实测）。正反对照：读根 exit 0、固定敏感路径 OS 拒读 exit 1。
- **授予落地延迟（任务一 3）**。隔离夹具的 5 个成功样本在 refresh 返回后 41–48ms 观察到组 ACE。沿用 15s 就绪预算。
- **就绪门 fail-closed 反证（任务一 4）**：读根含不存在路径（实测 helper 对不存在读根 refresh exit=0 容忍、授予无从到达）→ 15s 超界 fail-closed（实测 15.7s），错误含阶段 "read-root readiness" 与未就绪根路径，命令未启动（无任何命令输出），聊天可继续。实测发现："ACL 拒绝 helper 写入"的构造（deny WDAC）被 helper 的特权授予路径绕过（~31–60s 后 ACE 仍落地；InheritOnly 内置读不触发跳过授予）——helper 授予路径对用户级 DACL 限制鲁棒（机制未验证，推断为服务/特权上下文；证据 `wp5/logs/probe-console-grant-latency.log`）。
- **双向拒读（任务二 1–2）**：CLI 式 refresh（真实 helper，不含 Desktop 专属条目）撤销 Desktop 条目后，Desktop 下一条命令的 preflight 并集恢复覆盖（诊断 `deny-read-union` bookkeepingMerged=3、denyReadCount 1→4；ACE + 沙箱读探针正反对照）；会话冻结期间簿记新增条目（cli-d）被并集吸收（denyReadCount 4→5，读探针确认不撤销）。修复前对照：`wp3b/logs/probe-console-alternation.log` R5（撤销行为）。
- **反方向（任务二 3）**：经真实 `SandboxDenyReadBookkeeping.Merge`（共享助手）构造 CLI 式端 payload，Desktop 专属条目被并入（bookkeepingMerged=1），真实 helper refresh 后该条目不撤销（ACE + 读探针）——共享实现覆盖两端。
- **并发保持既有拒读集（任务二 4）**。启动两个 backend 探针前，夹具已把两端条目写入同一簿记。两次 refresh 交错执行后两条拒读 ACE 在位，簿记 SHA-256 一致。该用例覆盖并发复用既有集合。
- **回归与产物**：TinyHarness.Tests 2372 通过 / 12 跳过 / 0 失败；Desktop.Tests 424 通过 / 4 跳过 / 0 失败（+2 跳为本轮新增默认关闭 TH_W5 真机载体）；CLI 与 Desktop NativeAOT publish 零 trim/AOT 警告（Avalonia 两条已知 ILC 提示为噪音）；CLI `--help` 与 smoke、Desktop `--offline-smoke` 通过（AOT 产物，仓库外运行）。

修复前隔离环境验收记录（WP3b，2026-11-22；其中交替方向实测对应上述失败 1、TH_R5 首跑对应失败 2）：

- 生产入口真实分支对真实 profile 的顶层规划：114 个顶层条目、单次规划 9.1–16.9ms（对照旧方案在同一体量深层条目上 600s 预算超时 fail-closed），junction 落点裁决（10 个真实 junction 均落点 profile 内并授予）与簿记并集（含真实 CLI 时代簿记 10 条全部并入）经 Debug 诊断事件核实。
- 组装路径端到端（夹具 profile + 真实 helper + 真实 runner）：沙箱命令以真实沙箱账户执行，项目外普通文件可读、固定敏感路径 OS 拒读（正反对照含真实退出码），L1 零人工审批、零 AI Review、零 UAC，普通聊天不初始化沙箱；有项目与无项目两组均通过。
- 真实分支端到端：真实 runner 与沙箱账户在真实 profile 读对照成立（读根目录可列、既有生产 deny 目录拒列），首次准备约 0.7s；helper 的 ACL 应用以替身进程顶替以避免触碰真实 profile ACL（见未验证边界）。
- 准备异常：组件缺失、簿记损坏、ACL 应用失败与取消均保持命令未开始、错误指向具体阶段、聊天可继续。
- 升级与并行（隔离夹具，真实 helper）：CLI 式大 deny 集之后经簿记并集 refresh 不撤销 CLI 条目；无协调收缩会撤销（ACE 层核实，撤销由后台异步落地）；反方向 CLI 式 payload 撤销 Desktop 专属条目（即实测失败 1）；deny 压制既有广泛读 ACL、关闭继承子目录不随授予打开、junction 授予与拒读均落到规范化目标。
- 回归与产物：Desktop.Tests 424 通过 / 0 失败 / 2 跳过；TinyHarness.Tests 定向 74 通过 / 0 失败 / 1 跳过；CLI 与 Desktop NativeAOT publish 零 trim/AOT 警告（Avalonia 两条已知 ILC 提示为噪音）；CLI `--help` 与 smoke、Desktop `--offline-smoke` 通过。

未验证边界（如实）：

- 真实用户 GUI 未验证；真实用户在 Desktop 应用内的交互路径未验收。
- 生产入口对真实 profile 的 helper refresh（读根授予与 deny 同步落到真实 profile 顶层）未执行：机器存在待人工收尾的临时授读残留，本轮禁止触碰真实 profile ACL；真实 helper 的授予与 deny 同步行为由隔离夹具路径验证。
- C2 获批读授权通道端到端未验证：沙箱组件为 pinned 构建（无协议 v7 能力），授权执行在能力门 fail-closed（见权限与审批指南已知限制）；"C2 经授权仅打开批准范围，授权结束后恢复保护"留待组件部署后验收。
- 指纹基线段与顶层计划不同源为已知边界（组合指纹守卫的基线段与顶层规划输出不共享同一生成路径）；旧 planner 的 5 参可取消重载暂无生产调用者。
- 夹具 profile 仅能经组装路径测试使用：生产分支的规划根恒为注册表解析的用户 profile（已实测 .NET 11 `GetFolderPath(UserProfile)` 不跟随 `USERPROFILE` 环境变量），无法在进程内重定向。
- Desktop 与 CLI 真机并行（同时各持会话 refresh）未测；交替两方向已测（WP5，修复一收口复验通过），backend 级两探针进程真并行已测（WP5）。就绪门"非零等待后放行"形态未实测（冷启动授予在首次准备 helper 的异步窗口内落地；等待→超界 fail-closed 已实测）。

- **并发更新拒读集合仍待收口。** host 先读簿记，再启动 helper。两端刷新不同的新拒读项时，后执行的 helper 仍可能收到旧快照生成的集合，撤销另一端刚应用的拒读。WP5 的并行用例预置了双方条目。后续验收须覆盖不同新条目并发提交，并核对双方拒读实际生效。
- 簿记默认读取先调用 `File.Exists`。该检查可能将访问错误视为不存在，空集分支因而可能跳过有界重试和失败关闭。应区分确认缺失与读取失败，再复验异常路径。

WP6 并发收口轮（2026-11-22）：上列两条待办由本轮两项修复关闭并经隔离环境真机验收（真实 helper、无 stub，全部 ACL 操作限隔离夹具 `C:\Code\Temp\tinyharness-wp6-fx-20261122`；证据树 `.tmp/2026-11-22-desktop-sandbox-codex-read/wp6/`）。§1.5 仍有未验收项，本记录不改变其状态。

- **修复三（拒读刷新跨进程串行门）**：新增 `SandboxDenyReadRefreshGate`（`ISandboxDenyReadRefreshGate`/`GlobalDenyReadRefreshGate`），按沙箱 home 键控 `Global\TinyHarness-DenyRead-Refresh-<sha256-16hex>` 命名互斥体，把每命令 preflight 与授权恢复/退出 refresh 的 [读簿记 → 合并 → helper refresh → 簿记持久化] 关键节跨进程串行化。实现复用授权窗口锁的 worker 线程模式（Mutex 线程亲和：获取线程驻留等待释放信号）；前持有者进程死亡表现为放弃互斥体，按获得处理；互斥体创建失败与门等待超时一律 fail-closed（backend 抛 `ProcessExecutionStartException`，消息含 "deny-read refresh gate"，命令未启动；授权协调器转入既有禁用语义）；`WindowsSandboxBackendLimits.DenyReadRefreshGateTimeout` 默认 90s（覆盖另一端最坏 refresh：调用器 60s 超时 + 余量）。
- **修复四（簿记默认读取区分确认缺失与读取失败）**：`SandboxDenyReadBookkeeping` 改为直接打开读取、不再前置 `File.Exists`：仅 FileNotFoundException（文件确认缺失）与 DirectoryNotFoundException（父目录 `.sandbox` 不存在）返回 null（空集贡献，保留全新 home 冷启动）；访问拒绝、共享冲突及其他 IO 失败经既有 3 次有界重试（50ms 间隔）后 fail-closed（错误含阶段与状态文件路径）；JSON 损坏立即 fail-closed 不重试。

WP6 验收（两端共享夹具 home，真实组件）：

- **并发新增拒读项（3 轮）**：每轮两个独立探针进程同时启动（backend 级），各自语义 deny = 共享旧条目 + 各自新条目（新条目均在共享读根覆盖内 = 可配对形态），单命令触发门内并集 refresh。后到进程的 `deny-read-union` 诊断均并入先到进程刚持久化的条目（R1：先到 denyReadCount=2 / 后到 bookkeepingEntries=2、denyReadCount=3；R2：4/5；R3：6/7）——门内重读成立；每轮簿记按内容核对含共享条目与全部新条目（R1 3/3、R2 5/5、R3 7/7，非哈希对比）；OS 读探针正反对照：两个新条目在沙箱账户下 exit 1（拒读生效）、读根内普通文件 exit 0。探针 31 断言全过、exit 0（`wp6/logs/probe-console-concurrent.log`、`wp6/logs/wp6-probe-concurrent-{a,b}-r{n}.log`、`wp6/logs/concurrent-*-sandbox-diagnostic-*.log`）。
- **撤销-恢复因果对照**：CLI 式 payload（不含新条目）经真实 helper 全量同步撤销末轮新条目并把簿记整包替换为 `[shared]`（如实断言），deny ACE 消失而组授予仍在；两端各以自己的语义集经 union 恢复（恢复 b 前重读 A 端已恢复的 a），最终簿记 `[shared, a3, b3]` 在位、OS 拒读重新生效。整包替换后仅语义集与后续簿记可恢复条目——并集不做历史深冻结的既有边界如实保持。
- **簿记读取异常映射（真机路径）**：确认缺失（全新 home 簿记文件缺失、setup marker 在位）首命令成功（空集冷启动形态，helper 随后持久化）；访问拒绝（对状态文件 deny 当前用户读 ACE）→ 有界重试后 fail-closed，错误含阶段 "per-command refresh" 与 `deny_read_acl_state.json` 路径，命令未启动、helper 未被调用（该 run 诊断无 helper 事件，失败耗时 126ms 含重试）；移除 deny 后下一条命令成功；JSON 损坏 → 立即 fail-closed（错误含阶段与路径，耗时 1ms 无重试）、命令未启动；清理后下一条命令成功。探针 22 断言全过、exit 0（`wp6/logs/probe-console-acc2.log`、`wp6/logs/acc2-sandbox-diagnostic-*.log`）。目录缺失形态（`.sandbox` 整目录不存在）在真机不可达：backend 先经 `WindowsSandboxStateInspector.InspectMarkerAsync` 对缺失 setup marker 未就绪 fail-closed，命令到不了簿记读取；该分支由单测 `Merge_FreshHomeWithoutSandboxDirectory_ContributesEmptySet` 覆盖。瞬态失败后成功形态由单测 `Merge_TransientReadFailuresThenSuccess_Merged`/`Merge_TransientUnauthorizedAccessThenSuccess_Merged` 覆盖（真机不强造毫秒级共享冲突竞态）。
- **回归与产物**：TinyHarness.Tests 2388 通过 / 12 跳过 / 0 失败；Desktop.Tests 423 通过 / 4 跳过 / 1 瞬态失败（`ExplicitHumanApprovalSelectionProtectsAnOtherwisePristineNextDraft`，List 枚举竞态，与本轮改动无关，单独重跑通过）；CLI 与 Desktop NativeAOT publish 零 trim/AOT 警告（Avalonia 两条已知 ILC 提示为噪音）；CLI `--help` 与 smoke、Desktop `--offline-smoke` 退出码 0（AOT 产物，仓库外运行；本轮 CLI smoke 曾向用户 PATH 注入临时工具路径一条，已精确移除并核对了持久化 PATH）。

WP6 复核修复轮（2026-11-22）：复核发现修复三的两项缺陷并收口（真实 helper、无 stub，全部 ACL 操作限隔离夹具 `C:\Code\Temp\tinyharness-wp6r-fx-20261122`；证据树 `.tmp/2026-11-22-wp6-review-fix/acceptance/`）。§1.5 未验收项与本节上文既有边界状态不变。

- **修复五（门身份）**：`SandboxDenyReadRefreshGate.MutexNameForHome` 原按词法路径键控——junction 别名（`home-j-alias` → `home-j`）与原路径取得不同互斥体，同一物理 home 的两端并发 refresh 不互斥，别名端旧 payload 可撤销原路径端刚同步的拒读条目，跨进程串行承诺被词法形态绕过。修复：键控前先经 `SandboxFinalPathResolver.TryGetFinalPath` 解析最终落点，别名与原路径收敛到同一实际目录同一互斥体；解析失败（不存在/无权限/非 Windows）抛 `InvalidOperationException` 拒绝本次 refresh（backend 包成 `ProcessExecutionStartException`、授权协调器转入禁用，绝不无门 refresh）。
- **修复六（退出确认）**：helper 终止或退出确认等待失败导致退出状态未知时，调用器原仍按"已退出"释放门——helper 可能仍在写簿记，另一端获得门后读到不完整簿记。修复：`ProcessSandboxSetupInvoker` 区分正常退出/终止后确认退出/退出未知三种状态；退出未知抛 `SandboxRefreshExitUncertainException`（TimeoutException 派生）或 `SandboxRefreshExitUncertainCancellationException`（OCE 派生）携带后台退出确认任务，backend `PreflightAsync` 与协调器 `RecoverInsideWindowAsync`/`RestoreAsync` 经异常过滤器把门交给 `ReleaseWhenConfirmed`（确认 true 归还；false/取消/失败保持持有 fail-closed），`finally` 不再提前 Dispose。

验收（真实组件；断言计数、退出码与日志见证据树 `logs/`，运行记录 `acceptance/RUN-NOTES.md`）：

- **junction 别名双进程串行（3 轮）**：worker a 全链路原路径 home-j、worker b 全链路别名 home-j-alias 同时启动（各自语义 deny=共享旧条目+各自新条目，单命令触发门内并集 refresh）；每轮后到 worker 的 `deny-read-union` 均并入先到端刚持久化条目（R1 a:entries=1→b:2、R2 a:3→b:4、R3 b:5→a:6，先到/后到按轮自然交替）；簿记按内容核对 3/3、5/5、7/7；OS 读探针两新条目 exit 1、读根普通文件 exit 0。探针 26 断言全过、exit 0（`probe-console-junction.log`、`junction-{a,b}-r{n}-sandbox-diagnostic-*.log`）。
- **双 home 独立**：home-j 与 home2b 两 worker 同时 refresh，两端成功、各自簿记互不含对方条目（leaked=0），两端 ExecuteAsync 窗口重叠 890ms——不同实际 home 不共享门、不跨 home 串行化（`probe-console-twohomes.log`；单测 `TryAcquireAsync_DifferentHomes_AreHeldConcurrentlyWithoutBlocking` 同向佐证）。
- **WP6 回归**：`concurrent 3` 31 断言、`acc2` 22 断言全过 exit 0，跑在本轮夹具独立 home 上（`probe-console-concurrent.log`、`probe-console-acc2.log`）；门顺序用例对照 WP6 记录无缺项（含本轮新增退出未知、junction 别名、不可解析 home 用例）。
- **回归与产物**：TinyHarness.Tests 2406 通过 / 12 跳过 / 0 失败；Desktop.Tests 423 通过 / 4 跳过 + 1 已知瞬态（同 WP6 记录，单独重跑通过）；CLI 与 Desktop NativeAOT publish 零 trim/AOT 警告（Avalonia 两条已知 ILC 提示为噪音）；CLI `--help`/smoke、Desktop `--offline-smoke` exit 0（AOT 产物，仓库外运行）；CLI smoke 注入用户 PATH 一条已精确移除并核对（89→90→89，`path-unpollute-run.log`）。

本轮新增未验证边界（如实）：

- 修复六"退出未知 → 门保持持有"的真机形态未实测（需强造真实 helper 终止/确认等待失败时序）；由单测 `ProcessSandboxSetupInvokerExitTests` 7 例与 backend/coordinator 退出未知用例离线覆盖。
- `SandboxCapabilitySidStore` 既有并发缺口（范围外发现，已由下文 WP6 `cap_sid` 写侧持门与门外读收口轮收口）：同一 home 双 backend 并发首次解析 cap_sid 时 get-or-create 竞态（单测定向单独跑 5/5 复现共享冲突；全量套件与 WP6/审查轮均未复现，调度决定）。机制复现实验 `C:\Code\Temp\tinyharness-wp6r-diag-sidshare`。
- 探针以 Debug 配置发布（`SandboxDiagnosticLog` 为 `[Conditional("DEBUG")]`，Release 发布的探针自身无诊断日志）；探针属验收载体，NativeAOT 约束不适用于它。

WP6 `cap_sid` 写侧持门与门外读收口轮（2026-11-22 至 2026-11-23；运行登记与日志 `.tmp/2026-11-22-wp6-capsid-gate-read/`）：`GlobalDenyReadRefreshGate` 不可重入（嵌套获取自旋满超时后 fail-closed）。该轮调查口径为 `cap_sid` 文件 I/O 共 5 个读点与 1 个写点，写侧原本全在门内，门外读点与门内整表写回存在真实并发窗口——门外读会撞上共享冲突或半 JSON 的中间态，把另一端正在进行的 refresh 误报成沙箱未就绪、目录修复失败或 "Sandbox SID mapping changed"。本轮把宿主侧读写入口收进同一物理 home 的同一把门，全部为离线回归，未做真机 ACL 操作；§1.5 未验收项与本节上文既有边界状态不变。

- **写侧持门（`ResolveCapabilitySids`）**：backend 的 capability SID 解析（`SandboxCapabilitySidStore.ResolveForPolicy` 的 get-or-create 与整表写回）在 `WindowsSandboxBackend.ResolveCapabilitySids` 内持门进行，与另一端宿主解析及门内 helper 的表写互斥；仅解析这一小段持门，runner 启动与桌面创建不持门，门等待超时按命令未启动 fail-closed。上列 `SandboxCapabilitySidStore` get-or-create 竞态由此收口。
- **门外读收口（宿主侧 4 类）**：`WindowsSandboxBackend.PreflightAsync` 的就绪检查、payload 构造、簿记并集与 helper refresh 全部门内，后刷新校验（写 ACL 继承校验与读根就绪）仍留在门外以不延长持门时间；`WorkspaceSandboxAclContext.CaptureAsync` 把就绪与表完整性检查连同 get-or-create 一起移入门内；宿主侧 `ValidateAsync` 改用带门重载，表存在时方法内自取门完成单表读；`HasPreparedRootAsync` 同样在表存在时门内读表。home 缺失（`PreflightAsync`/`CaptureAsync`）与表缺失（`ValidateAsync`/`HasPreparedRootAsync`）时不拿门——缺失时不存在同 home 的门内写者，且门键控对缺失目录必然抛出，保留既有 "not ready"/读缺失/判未准备语义。
- **CLI `WorkspaceAccessCoordinator`**：`ApplyAsync` 的异常过滤器补入 `JsonException`，损坏表在门内仍会读出，与既有四类同转为 "Workspace activation failed; remaining read-only" 授权失败语义，保持只读不逃逸。
- **离线回归**：新增 11 个用例全部通过（11/11），分布为 `WindowsSandboxBackendTests` 1 个、`WorkspaceAclInheritanceTests` 9 个、`WorkspaceAccessTests` 1 个。其中 4 个交错用例（preflight、`CaptureAsync`、`ValidateAsync`、`HasPreparedRootAsync` 各一）以真实 `GlobalDenyReadRefreshGate`（真实 Windows 命名互斥体）持门，配替身 setup invoker 与 runner launcher 控制撕裂表写入窗口，属离线回归而非真机 helper/runner 验收；其余覆盖门等待超时 fail-closed、表缺失不拿门、门等待期间取消，以及损坏表 `JsonException` 转授权失败。preflight 用例有修复前红灯对照（门外读撕裂表 → "The Windows sandbox is not ready"）与转绿记录。
- **回归与产物**：TinyHarness.Tests 定向 99 通过 / 0 失败，授权读对照 24 通过 / 0 失败，全量 2427 通过 / 12 跳过 / 0 失败；Desktop.Tests 424 通过 / 4 跳过 / 0 失败。CLI 与 Desktop NativeAOT publish 完成（仓库外产物目录）：CLI 发布日志无任何警告或错误行，Desktop 发布日志仅既有两条 Avalonia ILC 提示。CLI `--help` 输出完整，CLI 离线 smoke `status: Completed`（17 步、16 次工具执行），Desktop `--offline-smoke` 输出 `PASS`。本轮日志未留存 publish 与 smoke 的退出码数字及 trim/AOT 警告计数，以上以日志内容为准。CLI smoke 的 `OfflineSmoke.cs` 已向子进程传入 `DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=0`，防止临时 CLI home 下的 SDK 向用户 PATH 注入临时工具路径；该轮收尾记录为用户 PATH 无 tinyharness 条目（`.tmp/2026-11-22-wp6-capsid-gate-read/RUN-NOTES.md`），但 2026-11-23 提交前小收口轮的基线采集（早于该轮测试加固编辑与验证运行）与之不符（见下文），该记录不作为当前 PATH 状态的依据。

本轮（2026-11-23 提交前小收口）仅加固测试，不改生产代码：4 个交错测试以确定性的门进入信号取代无信号负等待，并如实上报 B 方真实终态；证据目录 `.tmp/2026-11-23-wp6-capsid-precommit-closure/`。加固机制为新增测试专用装饰器 `GateEntrySignalingDenyReadRefreshGate`：`TryAcquireAsync` 被调用的那一刻置位信号 `EnteredGateWait`，再把参数与取消令牌原样转发给内层真实门，不改内层门的时序与语义；装饰器只交给被测入口（B 方），A 方持门直接用真实门实例、不经过装饰器，信号因此只会由 B 置位。门等待观察 `ObserveGateWaitAsync` 在进入信号、B 结束与 5s 进入等待上限三者之间取先完成者：信号先到则做 200ms 负等待，确认 B 仍未完成（preflight 用例另核对 runner 未触达）；B 抢先结束、或三路都未在期限内完成，均判失败。失败分支不在观察点直接断言：调用方先写回完整表、释放 A 持有的门并收尾 B，再用返回的说明断言失败，断言消息在固定文案之后带上 B 方真实终态（任务状态；失败时逐层列出异常类型、消息与完整异常链），收尾时观察到的 B 方终态也一并追加、不覆盖原始失败。A 释放门后的收尾等待上限 10s。本轮验证如下（全部为离线回归，未做真机 ACL 操作；§1.5 未验收项与上文既有边界状态不变；下列日志路径未带目录前缀者均相对证据目录 `logs/`）：

- **忠实红绿对照（preflight 用例）**：临时改动只落在 `WindowsSandboxBackend.PreflightAsync`——把 `_stateInspector.InspectAsync` 调用及紧随的 `if (!status.Ready) throw …` 判断从取门之后移回取门之前，其余保持修复后状态，临时 diff 为 2 个 hunk、新增 11 行删除 10 行（`.tmp/2026-11-23-wp6-capsid-precommit-closure/logs/01a-red-temp-only.diff`；相对 HEAD 的完整 diff 见 `.tmp/2026-11-23-wp6-capsid-precommit-closure/01-red-temp.diff`，其中临时改动落在 hunk `@@ -339,49 +388,99 @@` 内）。红灯构建 `dotnet build TinyHarness.Tests/TinyHarness.Tests.csproj --no-incremental` 为 0 警告 0 错误（上一轮红灯日志出现过 CS0162 不可达代码警告，本轮 CS0162 为 0），退出码 0；目标测试 1 失败 / 0 通过 / 0 跳过，退出码 1，失败消息如实带出 B 方真实异常链 `TinyHarness.Core.Services.Runtime.ProcessExecutionStartException`（Message 含 `The Windows sandbox is not ready; refusing to execute: Capability SID table is not valid JSON: …`），失败原因归类为「B 在到达门前提前结束」（`logs/01-preflight-faithful-red.log`）。随后只用备份副本 `cp` 回写还原生产文件，还原后 sha256 与改前一致（`f12997482abade26b115791fa926f61a13f812ef10c22da1aa3189a0f342a957`）、`cmp` 逐字节相等，全仓清单相对基线只有本轮预期的 5 个文件差异（两个活动文档、A 加固的两个测试文件、新增的测试装饰器文件，生产代码无差异；`logs/01b-restore-verify.log`、`logs/01d-full-list-diff-after-restore.txt`）；同一构建命令与过滤表达式下同一测试转绿，1 通过 / 0 失败 / 0 跳过，退出码 0（`logs/02-preflight-green.log`）。「忠实」在此的准确含义是：修复前的精确文本从未提交、无法从 git 恢复，本轮的临时改动是在现行代码上只恢复该顺序缺陷（`InspectAsync` 先于取门），并非逐字还原修复前代码；红灯只跑目标测试 1 个、只做 1 次，失败分支的确定性未做重复验证。
- **离线回归**：11 个 `cap_sid` 用例按显式 FQN 命名复跑 11 通过 / 0 失败 / 0 跳过，退出码 0（`logs/03-capsid-11-named.log`）；六类定向回归（`WindowsSandboxBackendTests`、`WorkspaceAclInheritanceTests`、`WorkspaceAccessTests`、`SandboxDenyReadRefreshGateTests`、`AuthorizedReadCoordinatorTests`、`CliDesktopParityTests`）123 通过 / 0 失败 / 0 跳过，退出码 0（`logs/04-targeted-suite.log`）；加固后的 `dotnet build TinyHarness.Tests/TinyHarness.Tests.csproj --no-incremental` 0 警告 0 错误，退出码 0（`logs/a-04-build-after-edit.log`）。4 个交错测试按显式 FQN 过滤连续 5 轮各 4 通过 / 0 跳过，另有一组带逐测试输出的 5 轮同样各 4 通过（`logs/a-05-*`、`logs/a-06-*`）；加固后 `WindowsSandboxBackendTests` 40 通过、`WorkspaceAclInheritanceTests` 24 通过，均 0 跳过、退出码 0（`logs/a-08-*`、`logs/a-09-*`）。这些用例使用真实 Windows 命名互斥体（真实 `GlobalDenyReadRefreshGate`）加替身 setup invoker 与 runner launcher，属离线回归而非真机 helper/runner 验收。
- **用户 PATH 口径更正**：上一轮记录为收尾时用户 PATH 无 tinyharness 条目，与该记录不符的是——本轮基线采集时（2026-11-23 15:10，早于测试加固编辑与验证运行）实测用户 PATH 为 94 条，其中 65 条指向 tinyharness 临时工具路径（63 条在 `%TEMP%` 下、1 条在 `C:\Code\Temp\tinyharness-wp3b-tmp` 下、1 条在仓库 `.tmp\cli-smoke-20261115` 下；`.tmp/2026-11-23-wp6-capsid-precommit-closure/baseline/07b-user-path-values.txt`）。上一轮那次检查的判负方式是否可靠，本轮没有查证。本轮各次运行前后用户 PATH 各自逐字节一致、未新增：测试加固前后（`logs/a-00-user-path-before.txt` 与 `logs/a-11-user-path-after.txt`）、验证运行前后（`logs/00c-precheck.log` 与 `logs/05-closing-checks.log`）。
- **本轮未重跑的验证**：NativeAOT publish、产物 smoke test、`TinyHarness.Desktop.Tests`、全量 solution 测试、`jb` 格式化均未执行；上一轮上述各项的记录原样保留在上文，但本轮没有重新验证，不作为本轮改动后仍然成立的证据。

原轮收尾时保留的实现边界与未验收项：

- **提权 helper `ExecuteHelperAsync` → `ValidateAsync`（`R3b`）有意不持门**：helper 以提权完整性级别运行，跨完整性级别访问互斥体的权限没有真机证据，helper 内不等待刷新门；撕裂读的后果是授权判定 Failed（fail-closed，可重试），不会以半读表为依据改动 ACL。
- **`WindowsSandboxProvisioner`（`W2-provision` / `R1e`）是离线管理命令边界，无门**：provisioning 是显式的 `sandbox provision` 管理入口，不取拒读刷新门，代码中没有任何机制阻止它与同 home 的活动会话并行——"不并行"是使用约定而非已验证的保证。
- **诊断/展示路径无门**：CLI `sandbox status`/`verify` 经 `WindowsSandboxStateInspector.InspectAsync` 门外读 `cap_sid`（只影响展示的就绪状态）；`#if DEBUG` 诊断在写根继承校验失败时只读汇总 `cap_sid`（`WorkspaceAclInheritanceDiagnostics`），失败方向均为只读、不作为授权或执行依据。
- **abandoned mutex 边界**：前持有者进程死亡表现为放弃互斥体，按获得处理；但 `setup.exe` 由 `ProcessSandboxSetupInvoker` 以裸 `Process.Start` 启动、未绑定 kill-on-close 的 Job Object（仓库内唯一的此类 Job Object `WindowsJobProcess` 只用于 `HostProcessBackend`），宿主死亡或孤儿 helper 时 `ReleaseWhenConfirmed` 无法运行——孤儿 helper 可能继续写簿记而另一端已按放弃互斥体获得门。
- **原轮未覆盖的生产验收**包括真实 GUI、真实 profile refresh 成功路径、C2 v7 端到端、CLI 与 Desktop 各持真实会话并行，以及真实 helper 首次写 `cap_sid` 的跨进程窗口。

<a id="windows-sandbox-session-isolation"></a>

## 2. 按会话隔离 Windows 沙箱读取权限（当前优先）

### 2.1 发现的问题

- **真实首次 Shell 被旧路径阻塞。** 2026-11-24 19:16，无项目会话的 setup helper 以 Medium 完整性运行。它在撤销 `deny_read_acl_state.json` 中记录的旧拒读规则时，遇到已删除的测试用目录并收到 `ERROR_PATH_NOT_FOUND (3)`，导致刷新退出，PowerShell 未启动。后续宿主执行返回 0，沙箱内读取尚未通过。现场核对的 8 条旧记录均指向该已删除的目录。
- **共享沙箱组无法表达相互独立的读取策略。** 普通 Shell 的拒读 ACL 写给 `TinyHarnessUsers`，helper 按 SID 同步。宿主只合并符合当前读根条件的旧条目，刷新锁在命令启动前释放。若 B 刷新时没有合并 A 仍需要的规则，helper 可撤销这条共享组规则，即使 A 的进程还在运行。已有隔离环境结果覆盖符合合并条件的条目，这个风险来自源码分析，尚未在真实会话并行时验证。

### 2.2 目标与范围

- Windows 必须按各活动会话的策略执行读取权限。Desktop 有项目、无项目及使用同一后端的 CLI 都在范围内。不同会话可以同时对同一测试文件得到各自策略所要求的不同读取结果。
- C1 等通用保护对所有会话生效。保留普通文件的 L1 读取体验、现行写根和网络边界、L2 的授权范围与撤销规则，以及 [L3 显式宿主审批](permission-architecture-plan.md#permission-product-contract)。本节落实读取隔离，第 3 节接入无项目会话的独立工具目录。
- 会话宿主保存有效策略。实际命令使用与该策略绑定的执行身份或令牌。恢复会话时按当前归属和可信配置重新核验，已失效的授权不能恢复。结束、取消或宿主异常退出时，按已确认的进程退出状态回收该执行实例的保护和状态。

### 2.3 实施顺序

1. **修复旧拒读条目的撤销。** helper 撤销已记录的规则时，仅把确认目标已不存在的情况视为清理完成，包括目标文件缺失和父目录缺失。现行拒读条目无法应用，或撤销遇到访问拒绝、其他 I/O 错误时，仍须报告具体阶段和路径并保持命令未启动。成功同步后才更新记录。
2. **在 Rust fork 验证隔离机制。** 比较独立 Windows 执行身份与能约束读取的受限令牌。用真实 runner 验证候选机制在旧共享组已持有广泛读 ACL 的情况下仍可执行各会话的策略。选定方案后明确身份分配、进程寿命、回收条件、状态归属和旧组件兼容边界。
3. **接入 CLI 与 Desktop 正常入口。** 为选定机制定义 runner／helper 的协议和能力检查，冻结本次执行使用的身份和策略。公共保护与会话规则各有明确的管理范围。旧组件与新状态混用时，无法落实所需隔离的命令保持未启动。
4. **复验第 1 节的产品条件。** 在隔离环境验证后，再按授权进行真实 Windows 复验，覆盖首次 Shell、普通读、固定路径拒读、C2 授权及双端并行。记录运行身份、权限检查结果、退出码、准备耗时和审批次数。

### 2.4 验收条件

| 场景 | 通过条件 |
| --- | --- |
| 删除旧路径 | 保留已删除目录对应的历史记录开始验证。helper 成功清理过期记录，现行拒读仍生效。无法访问的现存旧目标仍报告失败，命令保持未启动。 |
| 首次 Shell | Desktop 有项目与无项目各在同一新会话中先普通聊天、再跑 L1 Shell。聊天不初始化沙箱。已配置且权限正常时，首次 Shell 由沙箱 runner 成功读取项目外普通文件，UAC、人工审批和 AI Review 均为零。 |
| 会话读取隔离 | 使用真实 runner 和普通测试文件，覆盖同一项目的两个 Desktop 会话、不同项目的两个 Desktop 会话、有项目与无项目会话，以及 Desktop 与 CLI 会话并行。A 的策略拒读、B 的策略允许时，两边同时得到各自的结果。B 刷新、结束或失败后，仍在运行的 A 保持原有保护。 |
| 恢复与回收 | A 停止后启动 B，再恢复 A。每次以当前策略重新核验权限。覆盖会话取消和未确认子进程退出的情况，回收范围只包含可确认归属且已结束的执行实例。 |
| 公共保护与升级 | 预置旧共享读 ACL，再验证 C1、有效写根及网络边界。C2 仅在批准的会话和范围内生效，结束后恢复保护。覆盖链接真实落点及旧组件能力不匹配时的失败行为。 |
| 回归与产物 | 跑受影响的 Rust 和 .NET 定向测试、CLI 与 Desktop 的 NativeAOT 发布及产物 smoke。单独记录真实 Windows 权限效果的验收结果。 |

### 2.5 归档前实测结论（2026-11-24 至 2026-11-25）

- **§2.3-1（已完成补丁与隔离夹具验证，转入新计划收口）**：Rust fork 新增缺失目标处理（`revoke_read_deny_aces` 对 raw error 2/3 返回成功）与 `setup_error.json` 完整错误链（`{err:#}`）；单测与隔离夹具真 helper 验证（A1/A2/B1/B2）通过（证据见 `.tmp/2026-11-24-session-read-isolation/`）。
- **§2.3-2（原轻量方案证伪，退出当前实施范围）**：
  1. `SetTokenInformation(TokenGroups)` 无法向进程令牌注入新组 SID：自定义授权机构、域形态、logon 形态及"将现有 13 个组原样回写"的对照组均返回 Win32 错误 87。
  2. `CreateProcessWithLogonW` 复用宿主 logon SID：宿主进程与两次 runner 子令牌的 logon SID 完全相同（`S-1-5-5-0-2812703922`），不能作为每会话独立身份（且本机 `whoami /groups` 不列出 logon SID）。
  3. 后续决定不再为当前 Codex fork 底座引入提权独立登录会话或全盘读白名单，OS 层收敛为与 Codex 一致的"全机统一固定读底线 + 按工作区写隔离"，转由 [Windows 沙箱可用性收口计划](../windows-sandbox-readiness-plan.md) 推进。

## 3. 无项目会话独立工具目录（后续）

- 每个运行实例拥有独立临时工具目录。Shell 的工作目录、OS 层用户数据读写边界及内置列举、搜索、读文件和补丁工具共用该目录。验证绝对路径、相对路径和链接最终落点。
- 无项目工具调用不能通过附加根、既有文件授权或宿主 Shell 扩大范围。需访问本机项目时使用现有项目入口。网络许可继续使用既有审批和代理规则。
- 新建、恢复和解绑按当前有效归属选择策略。旧会话历史继续可读，历史工作目录不自动成为当前授权。归属变化时先使旧策略和授权失效。临时目录以运行实例为生命周期，回收前确认子进程退出，异常残留按归属和进程状态处理。
- 以[第 2 节](#windows-sandbox-session-isolation)通过验收的读取隔离机制接入 Desktop。真实验收在项目会话已经建立广泛读 ACL 后启动无项目会话，并覆盖两者并行。组件能力失配时保持命令未启动。
