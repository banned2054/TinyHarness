# 会话持久化实施与验收记录

归档日期：2026-10-24。来源：[session-persistence-design.md](session-persistence-design.md)。

> 本页保存原设计阶段的核查、实施或验收记录，章节编号沿用原文；未收录的契约章节见来源文档。文中的「当前」「本轮」「尚未完成」等表述只指各条记录的历史时点，不是当前状态或操作授权。历史正文不持续更新；链接随目录整理调整。原始本机证据可能位于未纳入 Git 的 `artifacts/`，其他机器不一定具备。

## 3. 当前问题与替换边界

实施前的静态定位如下，用于保留替换原因和检查范围，不表示这些旧路径仍是当前权威写入者：

| 位置 | 当前职责或缺口 |
|---|---|
| [Program.cs](../../TinyHarness.Cli/Program.cs)，`RunChatAsync` | 实施前同时组装 FileRunRecorder 与 SessionStoreWriter |
| [RunPersistence.cs](../../TinyHarness.Core/Services/Persistence/RunPersistence.cs)，`CompleteAsync` | 实施前在运行审计之外，按 run 保存完整脱敏消息与状态快照 |
| [SessionStore.cs](../../TinyHarness.Core/Services/Persistence/SessionStore.cs)，`CommitAsync` | 实施前 v3 完整序列化、同目录临时写入与替换，提交审计对账；快照上限 16 MiB |
| [ChatSessionHost.cs](../../TinyHarness.Core/Services/Session/ChatSessionHost.cs) | 执行前检查点、回合结算和 NeedsInspection；已接入统一存储，安全语义不得删除 |
| [SessionResume.cs](../../TinyHarness.Core/Services/Session/SessionResume.cs) | 身份、opaque 与协议边界校验，可复用不变量，数据读取与重建需适配 |
| [HostEvent.cs](../../TinyHarness.Core/Models/Session/HostEvent.cs) 与 [ChatMessage.cs](../../TinyHarness.Core/Models/ChatCompletions/ChatMessage.cs) | 内存事件不是持久 DTO；实施前缺少显式的过程／最终展示分类 |
| [ApplyPatchTool.cs](../../TinyHarness.Core/Services/Tools/ApplyPatchTool.cs) 与 [ToolPreparation.cs](../../TinyHarness.Core/Models/Tools/ToolPreparation.cs) | 实施前结果主要是文件名摘要；现已补持久化的 PatchChanges 事实契约 |

保留 Agent Loop、Session / Turn / Run、权限、沙箱、模型适配、上下文预算和现有工具语义。替换范围为持久化写入、读取、恢复及必要的展示／执行事实承载，相关入口随切换调整；不重做整套执行引擎。

本次实施已退出两条全量快照并行写入。结构化执行事实不得丢失，也不要求所有审计都塞进一个 JSON 对象。由于旧格式从未实际使用，旧读取器与显式迁移也已移除。

首版不实现 GUI、SQLite、历史分叉、后台归档、分段压缩、外部 MCP 或新供应商支持；不引入完整 event-sourcing 框架或插件式存储平台。

## 2026-10-24 本地原文与脱敏边界复验

2026-10-24 本地复验：普通离线回归 1164 总、1162 通过、2 项链接权限跳过、0 失败（不含规模测试），结果为 `artifacts/test-results/privacy-final-verified.trx`。覆盖本地正文、可读推理、参数、输出、artifact、diff、补充、工作项、离线 `session show`、诊断动态密钥及旧脱敏记录读取。`win-x64` NativeAOT 发布成功，无 compiler／trimming／AOT warning；`artifacts/privacy-nativeaot/TinyHarness.exe` 在仓库外隔离配置下离线 smoke Completed、17 steps、16 toolExecs，记录为 `artifacts/privacy-native-smoke.txt`。真实模型和 Desktop UI 未验证。

规模回归另行 1/1 通过，沿用原有预算，结果为 `artifacts/test-results/privacy-scale.trx`；未据此扩大真实环境保证。

## 7. 验收场景与证据

### 7.1 新设计的验收矩阵

下表本地场景已通过统一验收；唯一跳过项是环境无文件符号链接创建权限，未据此宣称该权限场景已验证。GUI 及其他明确列出的真实环境能力仍未验证。

| 场景 | 必须观察的结果 | 验证层次 |
|---|---|---|
| 多回合保存、退出、再打开 | 用户输入、过程／最终条目、调用关联和稳定顺序一致 | 本地已通过；Core 往返与 CLI 原生产物 |
| 补丁历史后文件再次变化 | 原条目的具体 diff 仍可读；confirmed、unknown、部分失败和未开始分开 | 本地已通过；工具事实捕获与历史读取 |
| 重启后继续、压缩后继续 | 同一合法模型上下文；opaque 与结构化状态保真，无历史工具再执行 | 本地已通过；fake 模型请求断言 |
| 意图保存失败与执行后丢结果 | 前者工具启动次数为 0；后者保持未知并阻断，检查确认可持久化 | 本地已通过；故障注入与启动计数 |
| 半行、断序、中间损坏与重复追加 | poison／strict 边界明确，有效前缀、拒绝或受控恢复不悄悄跳过关键事实 | 本地已通过；存储故障与重试测试 |
| 审批拒绝、取消、并发打开 | 无授权复活；终态准确；同会话拒绝第二写入者 | 本地已通过；会话与进程级回归 |
| 长历史分批读取并同时追加 | 序号游标无遗漏、重复和顺序漂移，每页返回最多 200 条，items 序列化 UTF-8 累计不超过 8 MiB | 本地已通过；数据契约与资源测量 |
| 实时、重开、报告中的合成秘密 | 同策略展示一致，检查输出不泄漏已知秘密与 opaque，canonical 不被改写 | 本地已通过；脱敏与保真对照 |
| 移除旧格式兼容 | 列举与查看忽略旧快照和独立 audit，不改动原文件；迁移子命令被拒绝，当前日志安全边界不退化 | 兼容层移除后的复验见 §7.3 |
| 新会话完成切换 | 无旧逐轮全量 snapshot/audit 默认输出，执行事实仍可读 | 本地已通过；实际文件产物与调用链 |
| GUI 历史浏览 | 自动加载、阅读锚点稳定、工具和 diff 可展开 | GUI 接入后真实验收；本次不验证 |

### 7.2 最终本地验证证据

以下是移除兼容层之前的首次统一日志验收历史证据；本次精简后的复验单列于 §7.3。以下为主验收执行、并经本地证据文件核对的结果，不包含真实模型、GUI、断电或实际 OS 隔离验证。

- 普通回归：794 总、793 通过、1 项链接权限跳过、0 失败，35 s；`artifacts/test-results/session-log-regression.trx`。唯一跳过项为 `LockAndLogLeafLinks_AreRejectedWhenLinkCreationIsAvailable`，原因是环境无法创建文件符号链接。
- History 9/9 覆盖 Prepare 失败完成条目即时进入原始 JSONL、live/page 同序，以及整 round 工具未执行。provider rawCallId 跨轮复用由独立 InvocationId 正确关联。
- scale 单独 1/1 通过，23 s；`artifacts/test-results/session-log-scale.trx`，原始指标为 `artifacts/session-journal-scale-results.json`。原锁定预算不变：append p95 ≤100 ms；10,000 规模恢复 ≤5 s、累计分配 ≤256 MiB；200 条首屏 ≤2 s、累计分配 ≤64 MiB。
- 最终 `win-x64` NativeAOT 重 publish 成功，0 warning、0 error；仓库外隔离临时 workspace/config/store 的 exe smoke 退出 0，Completed、17 steps、16 toolExecs，验证 read、restore、history 和 confirmed patch diff，并确认唯一会话日志、无旧 snapshot/audit 输出。文本证据为 `artifacts/session-log-native-smoke.txt`，可执行产物为 `artifacts/session-log-nativeaot/TinyHarness.exe`。

普通回归和 NativeAOT 的执行命令（从仓库根目录执行）：

```powershell
dotnet test TinyHarness.Tests/TinyHarness.Tests.csproj --no-restore --filter "FullyQualifiedName!~SessionJournalScaleTests" --logger "trx;LogFileName=session-log-regression.trx" --results-directory artifacts/test-results
dotnet publish TinyHarness.Cli/TinyHarness.Cli.csproj -c Release -r win-x64 --self-contained true --no-restore -o artifacts/session-log-nativeaot
```

仓库外 smoke 使用上述产物，在隔离的临时 workspace/config/store 下执行：

```powershell
& C:/Code/.Net/TinyHarness/artifacts/session-log-nativeaot/TinyHarness.exe smoke --config C:/Code/.Net/TinyHarness/tinyharness.json
```

最终规模测量：

| 历史规模 | 普通 append p95（ms） | append 分配中位数（B） |
|---|---:|---:|
| 100 | 2.6615 | 11,064 |
| 1,000 | 2.5418 | 10,832 |
| 10,000 | 2.665 | 10,832 |

- 10,000 规模 canonical 恢复：1,874.3712 ms、105,944,888 B 累计分配。
- 10,000 条 history 批量写入：3 batches、138.9558 ms、50,067,544 B 累计分配、9,290,690 B 文件增量。
- 200 条首屏：369.5788 ms、47,105,864 B 累计分配；实际读取 24,661,090 B，UpperSequence=10104、NextCursor 存在、warning=null。返回 items 的 8 MiB UTF-8 预算不是总 I/O 上限。

环境：SDK 10.0.401、.NET 10.0.12、Windows 10.0.26300、X64、32 个逻辑 CPU；CPU 型号和 RAM 未记录。分配量是进程级累计分配，不代表峰值内存。这些数字只说明本机固定合成负载的结果，不承诺其他环境的绝对性能。

原 M12–M13 的 2026-10-14 752 项离线测试、NativeAOT 与原生产物验证仍是旧实现历史证据，不计入本次数字；详见[历史基线附录](2026-10-16-session-persistence-codex-discussion.md#legacy-plan-baseline)。真实模型、GUI、断电持久性、实际 OS 隔离和其他外部操作未在本轮验证；M14–M17 未实施，本轮不推进。

### 7.3 移除旧兼容层后的本地复验

2026-10-17，用户确认旧持久化未实际使用，授权移除其读取与迁移。生产路径只保留统一日志；`PersistedSession` 继续作为日志恢复的内存投影，旧 FormatVersion／OpaqueRedacted 字段和旧快照专属序列化注册已删除。删除旧格式专属测试，保留并迁移当前有效的秘密保护、未知副作用及确认持久化覆盖。

- 普通回归：775 总、774 通过、1 项文件符号链接权限跳过、0 失败，32 s；`artifacts/test-results/journal-only-regression.trx`。沙箱内初跑受 HttpListener、CScript 和临时测试凭据权限限制；同一测试集在沙箱外复跑通过，未修改这些测试。
- 新增检查覆盖旧文件被忽略且原样保留、旧文件不占用新日志身份，以及移除的 `session migrate` 按未知子命令拒绝。损坏日志仍拒绝读取，list 继续报告损坏条目。
- scale：独立 1/1 通过，21 s，原预算未变；`artifacts/test-results/journal-only-scale.trx` 和 `artifacts/journal-only-scale-results.json`。100／1,000／10,000 规模 append p95 为 2.5906／2.4631／2.4654 ms；10,000 规模恢复 1,379.596 ms、91,243,704 B 累计分配；200 条首屏 290.2675 ms、54,436,424 B 累计分配，实际读取 24,661,153 B。
- `win-x64` NativeAOT publish 成功，无编译警告或错误；新产物 `artifacts/journal-only-nativeaot/TinyHarness.exe` 在仓库外、独立临时 workspace/config/store 下离线 smoke 退出 0，Completed、17 steps、16 toolExecs，验证 read／restore／history／confirmed patch diff。文本证据：`artifacts/journal-only-native-smoke.txt`。

从仓库根执行的复验命令：

```powershell
dotnet test TinyHarness.Tests/TinyHarness.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName!~SessionJournalScaleTests" --logger "trx;LogFileName=journal-only-regression.trx" --results-directory artifacts/test-results
dotnet test TinyHarness.Tests/TinyHarness.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~SessionJournalScaleTests" --logger "trx;LogFileName=journal-only-scale.trx" --results-directory artifacts/test-results
dotnet publish TinyHarness.Cli/TinyHarness.Cli.csproj -c Release -r win-x64 --self-contained true --no-restore -o artifacts/journal-only-nativeaot
```

原生 smoke 在仓库外调用上述新 exe 的 `smoke --config <临时配置路径>`；配置为合成模型和 `https://example.invalid/v1`，并将 `TINYHARNESS_USER_CONFIG_DIR`／`TINYHARNESS_SESSION_STORE_DIR` 指向独立临时目录。未调用真实模型或实际 OS 隔离。§7.2 的数字属于上轮历史记录；scale 测试会覆盖默认指标文件，本次固定副本另存于上述 `journal-only-scale-results.json`。

## 8. 验收边界与纠偏

### 8.1 本轮结论与未验证范围

- 展示分类、工具结果、具体 diff 及 Prepare 失败完成条目的顺序，已由离线回归与 History 9/9 验证。
- intent 门禁、缺失 intent 的已执行副作用事实拒绝、unknown、poison、strict 尾部边界与确认持久化，已在本地故障回归中验证。
- 旧 writer、旧读取器与迁移已退出；统一 run/chat 的新日志路径保留。兼容层移除后的证据见 §7.3。
- 顺序扫描在本机固定规模及原预算内通过；其 I/O 可覆盖整个捕获日志，不承诺恒定成本。
- 真实模型、GUI、断电和实际 OS 隔离未验证；符号链接创建权限场景跳过。M14–M17 未实施，本轮不推进。

### 8.3 重要决策记录

| 日期 | 当前结论 | 依据与状态 |
|---|---|---|
| 2026-10-17 | 采用小而完整的追加式会话内核方向；保留现有执行安全，按需求增加读取与规模能力 | 方向确认时的历史记录；本轮本地证据见 §7 |
| 2026-10-17 | PLAN 只细化当前验证切片；本文保存契约、假设、推翻条件与验收 | 设计整理时的历史授权边界；本轮实施与本地验收见 §7 |
| 2026-10-17 | 旧格式未实际使用，不再维护旧读取器或显式迁移；仅保留统一日志 | 用户确认精简范围；复验见 §7.3 |
