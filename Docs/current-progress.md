# 当前进度与接力入口

更新日期：2026-10-21。工作目录：`C:\Code\.Net\TinyHarness`。

本次工作：用户明确要求进入 M15，随后要求继续。运行预算、安全边界控制和工作项已实施；本轮工程验收与产物见新增 §7。此前 M14 关机停点保留在历史章节，不再作为当前下一步。仍不重复真实模型调用、沙箱专项或旧代码清理，不自动进入 M16／M17，不替换用户正在使用的 exe。

这份文档用于跨 chat 接续工作，替代反复复制长交接 prompt。产品计划仍以 [PLAN.md](../PLAN.md) 为准，操作规则以 [AGENTS.md](../AGENTS.md) 和用户当前明确要求为准。开始工作先核对 HEAD、status、相关源码和差异；下面的路径、配置和结果是本次记录，不能替代下次的磁盘检查。后续取得新结果时更新这里的当前状态，详细证据留在对应报告。

## 1. 当前结论

**M14 的基础行为与剩余压缩尾项已取得此前限定的本机样本证据。当前按用户新授权完成 M15 本地实施与离线验收，见 §7；不自动进入 M16／M17。**

- 已真实验证读文件诊断、按授权最小修改、用户说“先别改”后停止修改、审批拒绝后不绕过、工具失败及截断时不假称成功、普通退出后重启续聊。
- 这些是本机、当前 GLM 配置和指定夹具的样本结果，不能概括为所有普通使用均无问题。补丁准备／执行失败后修正、截断描述不准确、历史轮次口误均保留在记录中。
- 替换前的格式与待办逐字校验失败保留为 §3–§3.1 的历史诊断。替换后使用文本交接、有界真实原话和当前宿主规则，不再维护七字段摘要状态或旧解析分支。
- 新实现真实 GLM 连续 4 轮自动压缩，加独立进程恢复后的第 5 次，全部完成、无工具请求或执行、无保存警告；未预置／重放摘要。恢复前后 canonical／视图／state 哈希与折叠边界一致，详见 §3.2。
- 沙箱环境、TCP 环回模式、工作区信任与目录授权、网络验收探针均已实现并本地提交；已有真实沙箱证据见下文，不能扩大为所有隔离契约完成。

## 2. 代码与 Git 基线

M15 开工时已重新核对磁盘：HEAD 为 `d620193`，工作区干净。当前按用户授权记录 M15 功能、配套测试和代码整理；通用排版规范与共享设置单独提交。实际提交与 HEAD 以 `git log` 为准；下列 `17f2944` 和六笔提交属于 M14 历史基线，不能作为当前 HEAD。

本次整理前 HEAD 为 `17f294417dbc2c2dbb31228e0e53137e2cc79828`，分支 `master`；六笔本地提交如下，未 push：

| 提交 | 内容 |
|---|---|
| `1bc6f7f` | 将配置服务迁入 `Services/ChatCompletions/Configuration` 并更新调用方 |
| `a570ea6` | 补齐沙箱 SDK/NuGet 用户目录环境及回归 |
| `1be1e47` | 可信 TCP 环回模式、独立账户/home、版本与模式校验 |
| `7fd81cf` | 工作区信任、显式目录授权、既有子项继承与启动前检查 |
| `6eb70e3` | 网络验收宿主正对照、IPv6、UDP/DNS 与异常判定 |
| `17f2944` | 沙箱专项和 M14 真实模型验收结果与限制 |

六笔提交完成时 Git 工作区干净；这是历史记录。之后的进度文档和本轮压缩替换不属于这六笔提交。本轮未操作 Git，以实施前文件副本与哈希核对范围。用户的注释整理和格式化需保留；旧提交时对原 69 个变动路径逐字节复核，变化 0，不要把这些历史改动误当成本轮新增。

配置服务现位于 `TinyHarness.Core/Services/ChatCompletions/Configuration/`，旧 `Services/Configuration/` 已迁走；不要按旧路径恢复一份。

## 3. 替换前的压缩诊断（历史）

真实模型使用现有 `glm-5.3`／Chat Completions 配置。会话 ID：`9abd2b3899ba40ceb9f9b70227d178e6`，9 轮，独立工作区及用户配置，Harness 实际 shell 执行 0；该夹具关闭 WindowsSandbox，因此这轮不是 OS 隔离验证。

证据根目录：`artifacts/m14-real-20261020-4f9b/`。

| 证据 | 作用 |
|---|---|
| [verification.md](../artifacts/m14-real-20261020-4f9b/verification.md) | 完整逐项判定、边界与质量问题 |
| [prompts.json](../artifacts/m14-real-20261020-4f9b/evidence/prompts.json) | 九轮精确输入 |
| [per-turn-results.json](../artifacts/m14-real-20261020-4f9b/evidence/per-turn-results.json) | 工具意图、拒绝／失败、最终回答 |
| `sessions/9abd2b3899ba40ceb9f9b70227d178e6.session.jsonl` | 完整原始持久日志，勿改写 |
| [diagnostic-summary.txt](../artifacts/m14-real-20261020-4f9b/evidence/diagnostic-summary.txt) | 同配置独立诊断的真实摘要响应，实际为普通正文 |
| [diagnostic-result.json](../artifacts/m14-real-20261020-4f9b/evidence/diagnostic-result.json) | `Parsed=false`、`Applied=false`、原会话不变 |
| `diagnostic/Program.cs` | 复用原 Core 路径的诊断驱动；运行它会调用真实模型，不是纯离线读取命令 |

CLI 第 8/9 轮各发起过摘要请求，但日志中 `context.compacted` 为 0。CLI 两次的原始摘要正文未完整保存，不能将它们的具体拒绝原因全部归为独立诊断的非 JSON 响应。

当时停在诊断、拒绝原因和恢复验证；其后用户确认按 Codex 同步本地压缩思路替换，并明确清理旧实现。当前不再以恢复旧 JSON 契约为下一步；现行契约及已完成替换见 §3.2。此前明确授权的同配置 GLM 测试在本轮沿用，不扩大为新沙箱复验、机器配置或 Git／远端操作。

### 3.1 2026-10-21 压缩专项复验

用户明确授权整轮真实 GLM 压缩及恢复测试；授权审批问题经明确授权和原命令复核后解决，当前没有待确认的测试权限阻断。本轮只做测试与报告，未修改产品压缩实现。

- 证据根：[本轮报告](../artifacts/m14-compaction-test-20261021/verification.md)、[机器结果](../artifacts/m14-compaction-test-20261021/evidence/final-results.json)。
- 本轮离线 43/43，通过；另对两份真实非法增量摘要离线重放，原请求逐字节匹配、Parsed=true／Applied=false，历史、视图和已提交状态不变。
- 真实请求 10 次，其中摘要 5 次：1 次独立接受，2 次宿主非 JSON，2 次宿主丢失或改写旧待办而未按原文关闭。三个正常宿主进程的自动压缩事件和 context.compacted 审计均为 0。
- 独立成功摘要从 10063 降到 4551 估算 token；相同摘要请求也曾返回普通正文，不能把单次成功作为可靠性通过。
- 为隔离后段，另建 accepted-sessions 日志副本，在输入逐字节匹配后，经原 TryApplyCompaction 重放本轮真实成功响应并保存。其后新进程两轮对话及再次重启回顾均保持授权收紧、拒绝事实和未验证事项；没有请求工具。该路径不是宿主自动生成摘要成功，不补造成功事件。
- 原始日志、三个压缩源码文件与夹具六个文件，共 10 项哈希不变。只写本轮证据与日志副本；未重跑沙箱、未操作 Rust fork 或实际用户配置。使用当前 Core managed 构建，未做新的 NativeAOT 产品验收。

当前修复目标更明确：摘要输出格式可靠性、历史指令与摘要数据边界、旧 pendingWork 的保留／明确关闭契约，以及拒绝原因可观察性。仍保留摘要错误统计补丁失败次数、继承错误截断长度的质量问题。后续实施须由用户要求，本轮不自动修复、不推进 M15。上文 10-20 的证据及操作边界保留历史含义，后段“不曾验证”的旧描述以本节限定结果为准。

### 3.2 2026-10-21 压缩替换与验收

- 设计：[有界交接摘要与上下文压缩](context-compaction-design.md)；本机详细证据：[本轮报告](../artifacts/compaction-replacement-20261021/verification.md)、[最终机器结果](../artifacts/compaction-replacement-20261021/evidence/final-proof.json)。
- 沿用 ConversationContext／AgentLoop／唯一 session-log／SessionResume。删除 StructuredState、旧 JSON serializer、旧事实与 pending-work 逐字保留校验及其专属测试；终态 recorder 不再传完整摘要。保留完整 canonical、tool/opaque、来源与严格缩小校验；没有平行 writer、旧读取器或迁移入口。
- 最终相关回归 75/75；普通回归 967 总／965 通过／2 链接权限跳过／0 失败；scale 另 1/1。50 轮增长用例验证有界视图、150 份完整工具结果和 352 条 canonical 消息仍在。NativeAOT 无 compiler／trimming／AOT 警告，仓库外原生 smoke Completed／17 steps／16 toolExecs。
- 同一 GLM 配置，两个独立进程，真实请求共 10 次（摘要 5 次），自动事件和同日志 context.compacted 审计各 5 次。每轮 Completed，无工具请求／准备／执行和保存警告。原始九轮日志与夹具六文件哈希不变。真实链用当前 Core managed 驱动；原生 smoke 使用 fake model，不混为原生真实模型验收。
- 恢复后的 canonical／模型视图／state SHA-256 与折叠边界逐项一致。估算 token 数因启动时 usage 校准重新开始而不同，不把估算值当恢复内容指纹。最终只调整压缩事件前后估算到同一校准口径，并由专项离线回归验证；模型请求和提交契约未变。
- 不迁移旧七字段压缩检查点：含旧 `checkpoint` 的统一日志明确拒绝、原文件保留；没有旧检查点的统一日志继续可读。新必需 kind `compaction-checkpoint` 与 `tinyharness.compaction/1` 使旧程序也明确拒绝新格式，避免静默漏恢复。
- 语义限制：模型仍可能继承旧摘要／回答中的错误数字，本轮摘要重复了旧截断长度错误。授权收紧、唯一修改、拒绝未执行和未做测试取得样本证据，不能保证所有事实或模型均正确。GUI、断电和 OS 隔离不属于本轮。

## 4. 验证证据分层

| 类型 | 实际结果 | 限制与位置 |
|---|---|---|
| PriceLab 历史真实链路 | 一次 `dotnet test PriceLab.slnx --no-restore`，退出 0，5 通过 | Explicit 收据用例本次跳过；不重跑，不等于整个 M14 通过 |
| 旧 verify 真实矩阵 | 15 PASS，0 FAIL／INDETERMINATE／SKIPPED | 网络缺宿主非环回正对照；`artifacts/sandbox-vstest-ipc/real-verify-20261020-346ab5/` |
| 新网络真实专项 | 3/3 用例、14/14 目标通过，28 个前后宿主 ACK 匹配，无外层超时 | `artifacts/sandbox-network-acceptance-20261020/real-20261020-870c1a/` |
| M14 GLM 真实行为与压缩 | 原九轮基本边界，加新实现 5 次真实自动压缩、遵循与跨进程恢复通过 | 指定样本，仍有细节质量错误；见 §3.2 |
| 提交前普通离线回归 | 957 总／955 通过／2 链接权限跳过／0 失败 | 未包含 `SessionJournalScaleTests`，不是完整规模预算复验 |
| 五个代码提交的独立副本 | 分别 52/52、26/26、115/115、132/132、103/103 | 集合重叠，不能相加当唯一用例数 |
| 最新 NativeAOT 和离线 smoke | 发布无 compiler／trimming／AOT 警告；smoke 退出 0、Completed／17 steps／16 toolExecs | fake model，仓库外隔离配置，未调用真实模型／沙箱 |

提交与最新本地验证见 [commit-batch 验证记录](../artifacts/commit-batch-20261020-d17600/verification.md) 和 [final-proof.json](../artifacts/commit-batch-20261020-d17600/final-proof.json)。中间副本测试曾因工具环境拒绝只读访问既有 home／目录身份而失败，普通宿主权限复跑通过，失败证据仍保留；没有放宽断言。受保护的 1055 个文件及实际用户配置当时哈希不变。

新网络专项非环回地址实际为 Meta 接口 `198.18.0.1`，不能宣称物理 LAN、远端公网、非环回 IPv6 已验证。53/853 只验证直接端口通信阻断，不覆盖 Windows 系统解析器／DNS 服务代发、DoH 或完整 DNS 泄漏防护。严格模式与 TCP 环回模式的规则不同；详细契约见 [Windows 沙箱管理文档](windows-sandbox-management.md)。

新探针执行后，用户确认系统网络提示，Windows 曾为执行文件创建 Public 入站 TCP/UDP Any 规则各一条。随后用户单独授权删除，已完成精确清理；清理后 ActiveStore 与 PersistentStore 的目标应用规则均为 0，见 [独立复核](../artifacts/sandbox-network-acceptance-20261020/real-20261020-870c1a/firewall-cleanup-independent-check.json)。不是尚待删除，也不授权以后继续修改规则。明确地址监听仍可能触发提示，不能承诺绝不弹窗。

## 5. 产物、配置与原数据

最新本轮本地产物（未自动替换聊天进程）：

```text
C:\Code\.Net\TinyHarness\artifacts\compaction-replacement-20261021\native\TinyHarness.exe
SHA-256: E5254543FACB25DF89A0F4AAB0668AD9E513601EE6690A6E1CF88194BE20159E
```

下面为此前已核对的历史构建：

```text
C:\Code\.Net\TinyHarness\artifacts\commit-batch-20261020-d17600\native\TinyHarness.exe
SHA-256: 82C567DA40B8EC4BD6627D1062A87255E70691F5EA19CDD4F799AC57376DB72C
```

真实网络专项和九轮 GLM 使用的是前一份产物，不能把那次真实结果说成由最新 exe 执行：

```text
C:\Code\.Net\TinyHarness\artifacts\sandbox-network-acceptance-20261020\native-complete\TinyHarness.exe
SHA-256: 7CA34BFF187ED486B34223C56718C5232F2049018D01387E31C74730A1859B88
```

原用户聊天产物 `artifacts/sandbox-tcp-loopback-rebuild-20261020-de4dfc/TinyHarness.exe` 仍保留，不自动终止或替换用户正在使用的进程。

实际可信用户配置：`C:\Users\banned\AppData\Roaming\tinyharness\user-config.json`。此前核实为 enabled=true、allowTcpLoopback=true、使用 TCP 环回默认 home；setup／runner 来自 `artifacts/sandbox-tcp-loopback-components/`。切换和 provisioning 已完成，不重复操作。本次只核对既有记录和产物哈希，不将此前配置快照当下一次的实时状态。

| Rust 组件（dev/unoptimized + debuginfo） | 本次重核 SHA-256 |
|---|---|
| codex-windows-sandbox-setup.exe | `820BBFBA00BD1CC121851F19B01E0C06109A89490151A5BF669B2FF72B27D09C` |
| codex-command-runner.exe | `A73EF13484162AC7F484CF5C4240AEB37D8D9C861A5EFE3BA59C7B454A00EC33` |
| codex-windows-managed-deny-probe.exe | `8F72576A7B15B9FFC4481A7B9FF65BE709466078BA0C69B8A231AB9E7AD34244` |

真实 Rust fork 为 `C:\Code\Rust\codex`；如需修改或重建，先告诉用户，由用户切换到 Codex 项目处理。不要在 TinyHarness chat 中操作该仓库。

原 PriceLab：`artifacts/m14-behavior-trials/ready-01/workspace`。保留既有 quantity==0 修复、零数量／负数量回归、五个默认计算用例、Explicit 收据标记与 `notes/user-draft.md`；不改源码、测试、配置、包、bin/obj，不 restore 或重跑。`ready-01/baseline.json` 是历史初始清单，不是恢复目标；`Samples/M14BehaviorLab` 已被用户删除，不恢复。

真实 PriceLab 会话：`C:\Users\banned\AppData\Local\tinyharness\sessions\a76b3243d1234a18bd937d6d2c728a05.session.jsonl`。实际持久会话由可信 `settings.sessionStoreDirectory` 或默认目录决定；临时项目配置里的 `sessionDirectory=session-audit` 不代表真实持久日志写入那里，不顺手重构两者语义。

`artifacts/` 被 Git 忽略：上述详细证据在当前机器保留，没有强制纳入提交。不要批量提交用户配置、凭据、原始模型会话或二进制；换机器时需另行准备必要证据，不能假定链接中的本地产物自动存在。

## 6. 后续操作边界与暂缓事项

- M14 历史任务已完成压缩替换、旧代码清理和必要验证。M15 按当前新授权推进；新真实模型或沙箱专项仍须明确授权。
- 不自动 provisioning、UAC、账户／所有权／机器 ACL／Firewall/WFP 或系统 DNS 变更；新的真实网络专项须先给出具体命令、监听地址端口、系统提示可能性和影响范围，再取得对应授权。
- 已授权并完成的六笔 commit 不延伸为下一笔 commit、push、tag、PR、Release 或部署授权。
- 不关闭分析器、反射 JSON 限制或 NativeAOT 检查；publish 用新目录。默认测试不依赖真实模型、网络监听、机器账户或规则安装。
- M14 历史暂缓项：多行粘贴／`ReadLineAsync` 与审批共用输入、会话目录配置语义、模型策略查询接口及 M15。当前已按新授权进入 M15，单输入分发和运行中补充由 §7 接续；未增加多行编辑器，也未处理其余无关事项。

以后新 chat 可只给一句明确任务，例如：“先读 PLAN.md 和 Docs/current-progress.md，继续修复上下文压缩，保留已有边界。” 不需要再粘贴整个历史 prompt。

## 7. M15 本地实施与验收（2026-10-21）

- 设计和当前契约：[任务控制与运行预算](task-control-design.md)。本机证据目录 `artifacts/m15-development/`；最终命令、数字和限制见其中 `verification.md`。
- 同一 AgentLoop／ChatSessionHost 中加入步数之外的模型请求、工具次数、墙钟和 token 累计预算，摘要也计入；保留 reported usage 与估算的区别，达限终态为 BudgetLimitReached。
- 宿主控制支持补充、暂停／继续和取消；CLI 以唯一输入泵分发自然语言、审批与可选 `/pause`、`/resume`、`/cancel`、`/work`。补充使未启动旧计划失效并清除临时 allow，已执行结果、deny 与 NeedsInspection 保留。
- `work_items` 保存稳定 ID、来源、修订与完成／取消陈述，证据候选来自完整 canonical 工具消息；不从摘要建立事实，不把 completed 陈述视为测试通过。补充和工作项沿同一日志扩展记录并恢复，没有独立 writer 或恢复授权。
- 真实模型对新控制及工作项的遵循、GUI、多行输入编辑器、断电与新的真实 OS 隔离均未验证；默认验证全部 fake model／本机离线。原始 M14 会话、PriceLab、实际用户配置和机器沙箱均未修改。
- M15 实施和验证阶段未操作 Git；随后用户明确授权本地提交。推送、M16 实施、真实模型或沙箱验收分别按用户新指令执行。
- 最终普通回归 1013 总／1011 通过／2 项链接权限跳过／0 失败，41 s；独立 scale 1/1。win-x64 NativeAOT 无 compiler／trimming／AOT 警告，仓库外原生 smoke 通过：主链 17 steps／16 tools，另行验证 M15 控制／工作项／预算／恢复。
- 首次验收产物：`artifacts/m15-development/native-final/TinyHarness.exe`，SHA-256 `7A009E6D6258FA030501906F7FB77B305D848E6B43C65B2CF53097D9D766B62C`。完整记录：[M15 验收](../artifacts/m15-development/verification.md)。
- 后续按用户要求执行 ReSharper 和人工可读性整理，保留用户修改并补充通用排版规范。整理后普通回归 1013 总／1011 通过／2 项链接权限跳过／0 失败，43 s；最终 NativeAOT 和仓库外原生 smoke 均通过，详见[人工整理验收](../artifacts/m15-manual-style/verification.md)。
- 最新产物：`artifacts/m15-manual-style/native/TinyHarness.exe`，SHA-256 `DAA578AC282EB9FA81100933332B72A84376E39B2562986C9612C6B99258BA9B`。未替换用户使用中的 exe。
