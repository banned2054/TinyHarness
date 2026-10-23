# M16 交付证据核查与验收

日期：2026-10-22。范围见[归档计划 §9.2](../PAST_PLAN.md#92-变更与验证证据)；该阶段计划已于 2026-10-23 归档，本次验收记录保留原日期与范围。

**收窄后的 M16 已完成本机验收，无需新增产品代码。** 现有 shell、补丁事实、唯一 session-log 和离线检查入口足以支撑本次交付场景；没有新增专用 Git tool call、归因数据库或独立报告系统。模型解释仍须与确定性记录核对，不承诺每句话正确或能识别所有并发修改者。

## 1. 验收对应关系

| 要求 | 当前路径与验证依据 | 结论 |
|---|---|---|
| 使用 Git 检查实际状态和差异 | 既有 `shell` direct／shell 模式；此前 GLM 三个完成样本；本轮实际 ShellTool direct 查询识别开工前已有的 `PLAN.md` 修改 | 满足，不需要专用 Git 工具 |
| 区分本轮补丁、已有文件和后续变化 | `ApplyPatchTool` 返回带当时 diff 的 confirmed／unknown 文件事实；`ApplyPatch_RecordsOriginalConfirmedDiffs` 在文件被后续改写／删除后仍保留原 diff；本轮 GLM 拒绝将当前 12 全部归因于确认的 10→11 补丁 | 满足可检查与说明限制的要求，不提供完整作者归因 |
| 部分写入不能冒充整次成功 | `ApplyPatch_MidWriteFailureKeepsConfirmedAndUnknownOnly`、`HostRetainsConfirmedFileFactsButRestoresIncompleteInvocationAsUnknown`、`CancelBetweenPatchFiles_PreservesConfirmedFileAndUnknownInvocation` | 已确认文件事实与整次 UnknownResult 分别保留，恢复后仍阻断未知副作用 |
| 命令结果和输出完整性可检查 | ShellTool 记录退出码、超时、stdout／stderr 与 artifact；SessionLogRunRecorder 内联有界输出，缺失／超限标记 incomplete；`Execute_TruncatesInMemoryOutputAndKeepsFullRedactedArtifact` 与 recorder 的 artifact 用例 | 满足，截断或缺失输出不推导测试通过 |
| 拒绝、准备失败与未启动不伪造结果 | `HostPersistsPrepareFailureHistoryAsKnownNotStartedAfterRestart`、`HostPersistsPermissionDenialImmediatelyWithoutStartingTool`、`HostPersistsEveryNotStartedCallInRoundOrderBeforeRetry` | KnownNotStarted 保留，执行次数为 0，成功与退出码保持 null |
| 历史事实不冒充当前状态 | CLI 显示会话／回合／运行时间和执行终态；`Show_AcknowledgedInspectionDoesNotClaimCurrentBlocking` 区分历史未知与当前确认；内置提示词要求重新检查可能变化的状态；当前文件与历史补丁不一致的真实 GLM 样本 | 满足本次范围；旧验证不能保证当前状态 |
| Completed 与目标／测试通过分开 | 确定性工具记录独立于模型最终文字；work_items 明示 claims, not verified outcomes，允许完成陈述但不赋予 verified 状态；真实 GLM 对 exit 0／截断／未执行记录未声称构建测试通过 | 满足，不建设自动测试语义解析器 |
| 证据可重新检查且不重放工具 | 分页历史与离线 `session show`；本轮重新显示此前四个真实会话，均退出 0、原始日志哈希不变；live/page 一致性与日志故障测试通过 | 满足，读取不解析凭据、不调用模型、不执行历史工具 |
| 预算、保存失败和秘密边界 | run 摘要保存 budget，历史保留结果；结果提交失败阻断后续工具；检查视图脱敏，不打印 canonical／opaque 原文；相关持久化与 work_items 测试、原生 smoke | 满足，模型文字不能覆盖失败记录 |

表中的源码与测试分别位于 `TinyHarness.Core/Services/Tools/ApplyPatchTool.cs`、`Services/Persistence/SessionLogRunRecorder.cs`、`TinyHarness.Cli/Commands/SessionCommand.cs`、`Prompts/WorkspacePrompts.cs` 及对应测试类。本轮没有修改这些文件。

## 2. 本轮工程验证

证据根目录：`artifacts/m16-acceptance-20261022/`（本机保留，被 Git 忽略）。[机器汇总](../artifacts/m16-acceptance-20261022/final-proof.json) 记录测试、原生 smoke、历史检查、模型请求和范围核对。

### 针对性测试

最终 **158 总／158 通过／0 失败／0 跳过**；这不是全 solution 回归或 scale 复验。结果为 [TRX](../artifacts/m16-acceptance-20261022/test-results/m16-evidence-bounded-path-host.trx)。

| 测试类 | 用例数 |
|---|---:|
| AgentLoopTests | 23 |
| ApplyPatchToolTests | 34 |
| ChatSessionPersistenceTests | 13 |
| SessionCommandTests | 11 |
| SessionHistoryPagingTests | 7 |
| SessionHistoryTests | 9 |
| SessionJournalFaultTests | 19 |
| SessionResumeTests | 19 |
| SessionWorkItemTests | 10 |
| ShellToolTests | 13 |

执行 `dotnet test TinyHarness.Tests/TinyHarness.Tests.csproj --no-restore`，以这十个类的 `FullyQualifiedName` OR 条件过滤，并输出上述 TRX。可复制的完整过滤器与命令保留在 [离线复跑脚本](../artifacts/m16-acceptance-20261022/run-offline-tests.ps1)；复跑使用新文件名，不覆盖原证据。仅为该测试进程设置精简 PATH，包含系统目录、Windows PowerShell、dotnet、pwsh 和 Git 的目录；没有修改系统或用户环境变量。

保留失败证据而不合并计数：

- 原 PATH 下受限及宿主运行均为 156 通过／2 失败；两个延时测试的 cmd 找不到 `ping`，提前退出，未进入超时／取消。
- 探针确认父进程 PATH 长 10,592 字符，子 cmd 按名称找不到 `ping`，`echo %PATH%` 也未返回路径值；补充 `if defined PATH` 探针为 PATH_DEFINED，因此不是环境变量缺失，不能将其描述为 PATH 为空。绝对路径 `ping.exe` 在约 1,034 ms 返回 `TimedOut=true`；精简进程级 PATH 后两项测试恢复。这里只确认该环境组合及调整效果，不宣称已经定位 Windows 内部原因，不修改产品进程环境策略。
- 精简 PATH 的受限运行是 157 通过／1 失败，CScript 明确报告读取设置被拒绝；同一组测试在宿主权限和精简 PATH 下 158/158 通过。未修改断言、跳过测试或关闭检查。
- 四份 TRX 与 `slow-command-result.json`、`slow-command-diagnosis.json`、`path-defined-proof.json`、`test-environment.json` 保留于证据目录；多轮复跑不相加为独立测试数。

### 原生产物与历史检查

- 使用既有 `artifacts/m15-manual-style/native/TinyHarness.exe`，SHA-256 `DAA578AC282EB9FA81100933332B72A84376E39B2562986C9612C6B99258BA9B`。
- 仓库外临时 cwd、隔离用户配置运行 `smoke --config <临时 project.json>`：退出 0、Completed／17 steps／16 tool executions；日志恢复、历史 confirmed diff、失败／成功夹具检查、两种命令模式及另行 M15 控制／预算 smoke 均通过。[原生输出](../artifacts/m16-acceptance-20261022/native-smoke.txt)。使用 fake model，未运行 Windows 沙箱。
- 本轮没有重新 publish；本轮只验证既有原生产物行为，不把历史 AOT 编译结果称作新的 publish。
- 对此前 Git 验证的四个会话运行原生 `session show <id>`，均退出 0、显示历史，源日志前后哈希相同。[检查汇总](../artifacts/m16-acceptance-20261022/offline-show-results.json)。

## 3. 真实 GLM 报告样本

沿用原 `glm-5.3`／Chat Completions 配置与凭据引用，通过上述原生 CLI 的 run 路径执行。新夹具只有合成内容，不发送产品源码；工作区保持未信任只读状态，shell 被禁用，没有新增目录信任或操作机器配置。

夹具准备先直接调用当前 Core 的 ApplyPatchTool，将 `owned.txt` 从 10 改到 11，取得实际 confirmed 事实；随后由验证脚本改到 12。另保留已有用户草稿。截断／历史 exit 0／拒绝未执行记录明确标注为合成输入，不能算作真的测试执行。该补丁由探针生成，不是模型自主实施的证据。

模型真实读取夹具后：

- 正确区分已确认的 10→11 与当前 12，后者来源不明，不能全部归于补丁。
- 未把补丁成功、合成 exit 0 或截断记录当作构建／测试通过；拒绝记录按未执行报告。
- 对用户草稿的字节一致性保留未验证说明；验收脚本独立比较三个夹具文件 SHA-256，实际变化 0。
- 运行 Completed，2 次模型请求、4 次工具执行（list_files 与三次 read_file）、7,516 reported accounted tokens；未执行 shell 或写入工具。会话 ID 为 `d2824992516a48b0972683724f169d54`。[检查投影](../artifacts/m16-acceptance-20261022/glm-report-inspection.json) 和 [CLI 输出](../artifacts/m16-acceptance-20261022/glm-report.txt) 保留证据。

已知文字瑕疵：开头说三个文件与 `currentFiles` 一致，但该列表实际只列出 owned.txt 和 user-draft.txt，未列 evidence.json。此句不准确，保留原文；不能把核心判定正确外推成整篇文字无误。确定性文件清单与工具记录优先于模型概括。

此前 Git 的三个完成样本、初次达限及完整源码补丁外发审批限制，仍按 [原验证记录](../artifacts/git-shell-glm53-20261021/verification.md) 分别计数，本轮不重跑完整源码补丁。

## 4. 收口与限制

本轮开工时 `PLAN.md` 已有此前获准的计划调整，不归为本轮新增实现。工程验证结束、记录文档之前，337 个已跟踪文件与本轮基线哈希一致；只新增本验收文档、更新计划与接力状态，以及本机验证产物。没有修改产品代码、测试、依赖或发布配置。

本阶段的完成含义是：现有事实可以被检查，模型解释可以与记录核对，所选工程与真实样本满足收窄后的交付要求。未知归因如实保留，不承诺解决所有并发竞态；不保证模型在任意任务下均准确，不提供自动测试通过判定或完整作者归因。GUI、断电持久性、新 OS 隔离环境、完整项目源码补丁外发和 M17 日常任务矩阵不在本轮验收范围。

无需为阶段编号补建功能；后续实际任务出现证据缺口时再单独处理。M17 不自动开始。
