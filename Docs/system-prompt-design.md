# 基础系统提示词设计与接入（M14.2）

更新日期：2026-10-21。

## 1. 范围与完成定义

用户确认通过自然对话表达讨论、诊断和实施要求。本切片集中完善系统提示词，不实现 Analyze／Implement 切换、只读开关、意图分类模型或新的执行引擎。工具继续经过既有权限、路径、沙箱和中断检查。

工程接入与本地验证已完成；2026-10-20 的行为评估及 10-21 替换后的真实压缩、范围遵循和恢复取得指定样本证据，详见 §9。§7–§8 是替换前的失败与隔离诊断，不代表当前仍阻塞。脚本模型只证明接入链路，不证明语义遵循。M15 的执行中补充／暂停、累计预算与 M16 的确定性交付报告不在本切片内。

## 2. 参考与内容选择

对照用户指定的本地源码，而非假定所有模型和部署使用同一份上游文本：

| 项目 | 参考位置 | 采用的设计原则 |
|---|---|---|
| DSH | `packages/core/system-prompt`、各工具的 prompt section | 行为正文、工具 schema、运行环境各有来源，不描述未提供的能力 |
| Kimi Code | `packages/agent-core-v2/src/app/agentProfileCatalog/system.md` | 紧凑的沟通、工具、编码、交付和上下文契约 |
| Qwen Code | `packages/core/src/core/prompts.ts` | 保留已有工作、按任务验证、拒绝不能绕过、工具协作 |
| ZCode | `apps/zcode-cli/packages/core/src/context` | 讨论／实施边界、持续工作时的范围约束、完整最终回答 |
| Codex | `codex-rs/models-manager/models.json`、`codex-rs/prompts` 及旧版模板 | 已授权范围内自主推进、沟通、版本化提示来源；不照搬特定模型的全部自主执行措辞 |

模板正文只维护在 [`WorkspacePrompts.Current`](../TinyHarness.Cli/Prompts/WorkspacePrompts.cs)。采用用户审阅的中文正文，要求回复跟随用户语言。覆盖：

1. 理解请求、讨论与实施边界、任务范围和已有授权。
2. 授权范围内持续推进、必要澄清和失败后的调查。
3. 阅读后修改、实际 API 与依赖、目标项目惯例和共享工作区保护。
4. 五个工具的选择与协作、分页／截断、shell direct 与解释器模式。
5. 失败、拒绝、取消、未开始与结果未知的区别。
6. 与风险相称的验证、准确结果和完整交付。
7. 指令来源、操作授权与秘密保护。
8. 多轮、压缩后的连续工作和历史事实时效。
9. 用户语言、简短进度及独立完整的最终回答。

系统提示不重复工具参数表和资源上限，不引入未提供的后台任务、并行执行、Skills、浏览器或 MCP client 指导。目标仓库的 .NET／AOT／注释等规范来自其项目指令。运行环境仍按已有宿主和工具提供的信息解释，本切片没有新增动态环境注入协议。

## 3. 执行链与维护

- `Program` 的单次 `run`、首次发送创建的新 `chat` 和离线 smoke 均使用 `WorkspacePrompts.Current`，删除 smoke 的独立旧文本。
- 模板作为编译期字符串进入原生二进制，不新增运行期文件读取、反射注册、配置项或依赖。
- 原 `ChatSessionHost`、`AgentLoop`、`ConversationContext`、工具注册和统一日志 writer 保持职责不变。项目规则仍是 M14.1 的独立来源，不并入固定 system 文本。
- 普通模型视图保留唯一完整 system；压缩请求也携带当前 system 和独立项目规则，并在历史末尾追加专用文本交接任务。宿主验证来源、预算、响应完整性和压缩收益，不要求模型输出 JSON；检查点元数据使用 source-generated JSON，详见[压缩设计](context-compaction-design.md)。
- smoke 在日志恢复后检查当前行为模板仍然完整保留。

## 4. 会话提示词一致性

用户确认不需要旧提示词兼容。仅维护当前内置模板，创建和续聊均直接使用 `WorkspacePrompts.Current`，不保留旧文本、版本选择分支或兼容提示。

- 保存的首条 system 须与当前模板逐字一致，不改写 canonical 历史，不增加日志格式或迁移入口。
- 不同提示词、伪造 role 和空对话仍拒绝续聊；模型、工作区、协议、opaque、项目来源和 NeedsInspection 校验保持有效。
- 不读取或删除任何实际会话数据来清理旧版本。

## 5. 本地验证

`WorkspacePromptTests` 包含 7 个离线用例：

- 单次运行的实际工具请求携带完整模板与工具 schema。
- 当前模板经过 pending chat 创建、工具轮、多轮、落盘和重新打开；项目规则独立且不重复。
- 压缩、上下文恢复保留完整当前 system，不改写 canonical 历史。
- 未知模板、被改动的当前模板、伪造 role 和空历史均拒绝。

命令：

```powershell
dotnet format whitespace TinyHarness.slnx --no-restore --include TinyHarness.Cli/Prompts/WorkspacePrompts.cs TinyHarness.Tests/WorkspacePromptTests.cs --verbosity quiet
dotnet test TinyHarness.Tests/TinyHarness.Tests.csproj --no-restore --filter 'FullyQualifiedName!~SessionJournalScaleTests' --logger 'trx;LogFileName=m14-prompt-regression.trx' --results-directory artifacts/test-results
dotnet publish TinyHarness.Cli/TinyHarness.Cli.csproj -c Release -r win-x64 --self-contained true --no-restore -o artifacts/m14-prompt-nativeaot
```

结果：

- 移除旧提示词兼容后的完整普通回归 **860 总、858 通过、2 项因符号链接创建权限跳过、0 失败**，36 s，包含全部 7 个提示词接入用例。证据：`artifacts/test-results/m14-prompt-regression.trx`。
- .NET SDK 10.0.401；`win-x64` NativeAOT publish 成功，没有 compiler、trimming 或 AOT warning／error。
- 仓库外独立临时 cwd、合成配置、`TINYHARNESS_USER_CONFIG_DIR` 隔离用户配置，执行原生 `TinyHarness.exe smoke --config <合成配置>`，退出 0，Completed、17 steps、16 toolExecs。读写工具、规则初始／更新／恢复来源、日志历史与 confirmed patch diff，以及恢复后的当前系统提示检查均通过。证据：`artifacts/m14-prompt-native-smoke.txt`。
- 本切片未修改 Core、模型 SDK、依赖、公开 API 或日志格式；规模测试未重跑，不将 M14.1 的旧规模测量计作本轮结果。
- 仅对新增 C# 文件运行基础 whitespace formatter；不声称复现 ReSharper Code Cleanup。真实模型、GUI、断电持久性和实际 Windows OS 隔离均未在本轮验证。

## 6. 真实模型行为评估

真实评估须显式 opt-in，使用新建会话和隔离 fixture。检查实际工具调用与文件差异，不只评价回答文字：

- 提问／诊断／讨论：读取并回答，不产生未经要求的修改。
- 明确修复：完成必要修改和验证，不只提供计划或重复确认。
- 同会话由实施转为“先别改”：后续遵循范围收紧。
- 工具拒绝、失败和截断：不绕过、不盲重试、不编造完整结果。
- 压缩和续聊后仍遵循任务边界，区分历史与当前状态。
- 报告实际修改、验证及未完成事项，不声称未执行的验证通过。

记录模型、协议、配置、输入、工具轨迹、差异与误判；不能把单个模型的通过外推为所有模型的保证。

## 7. 2026-10-20 真实 GLM 评估结果

用户明确授权后，使用现有 `glm-5.3`、Chat Completions 配置，在独立夹具和会话中完成 9 轮真实对话。复用当日已验证的 NativeAOT 二进制，隔离用户配置关闭 WindowsSandbox，唯一 shell 请求在审批处拒绝，实际未启动 shell。此结果不作为 OS 隔离证明；实际用户配置与原 PriceLab 保持不变。

- 诊断不修改、明确授权后只改一个文件、随后“先别改”、拒绝后不绕过、文件不存在时诚实报告、截断时不假称完整读取、多轮及普通重启续聊取得通过证据。
- 操作和表达仍有缺陷：修复轮出现 3 次补丁准备失败及 1 次执行失败，最后修正成功；截断层次说明不准确；续聊时把代号来源轮次说错。核心边界通过不等于全部回答准确。
- CLI 两次真实摘要请求均未提交压缩，日志中 `context.compacted` 为 0。使用同配置和原 Core 路径的独立只读诊断，得到普通中文正文而非摘要 JSON，解析／应用均失败，原会话不变；CLI 两次拒绝的具体原因不能全部据此断定。
- **真实压缩链未通过，压缩后遵循和压缩状态重启恢复仍未验证。** 需要定位摘要指令与历史数据边界、修复后另行复验，不能用未压缩的续聊或 fake model 结果替代。本次没有修改产品压缩实现或放宽校验。
- 另执行既有压缩／上下文／提示词离线回归 **34/34 通过**，未重跑 §5 的历史完整套件。当前真实结果不关闭 M14，不推进 M15。

完整环境、9 轮输入、工具轨迹、文件哈希、usage、原始摘要与判定见[本次验收记录](../artifacts/m14-real-20261020-4f9b/verification.md)。

## 8. 2026-10-21 真实压缩专项复验

用户明确授权后，沿用同一 GLM 配置，在独立诊断和会话日志副本上完成 10 次真实请求，其中 5 次为摘要；当前 Core managed 构建，未修改产品实现。

- 独立摘要有一次返回合法 JSON 并通过原 TryApplyCompaction，估算 token 从 10063 降到 4551；相同内部请求在正常宿主中返回普通正文。正常宿主另外一次摘要也为普通正文，两次均拒绝应用。
- 为独立验证后段，将本轮真实成功响应重放到另一日志副本，先验证输入逐字节相同，再由原校验接受并 CommitAsync。新进程读取后两轮和再次重启回顾中，授权收紧、唯一修改、拒绝事实及未执行验证均保留；模型请求工具数为 0，原始日志及夹具不变。退出前后 canonical／模型视图／state 指纹和折叠边界一致。
- 后段过程中还有两份真实摘要为合法 JSON，但没有按原文保留或以 completed: 原待办文本关闭旧 pendingWork。离线重建相同请求并调用原解析／应用路径，两次 Parsed=true、Applied=false，旧状态和历史保持不变。
- 正常宿主三个进程的自动压缩事件和 context.compacted 审计均为 0。真实成功响应重放验证了后段，不能替代自动生成摘要成功；M14 仍未收口，不推进 M15。
- 本轮离线用例 43/43 通过；两份非法增量摘要另作离线诊断重放，不计为新增 NUnit 用例。真实服务报告输入 87607／输出 12538 token，不估算账单，不外推到其他模型或端点。
- 成功摘要也有细节错误：补丁失败次数及截断输出长度不准确。历史指令影响摘要输出仍是候选机制，未证明唯一根因；需要改进输出契约可靠性、待办更新契约及拒绝原因可观察性，不能放宽事实保留和缩小校验换取通过。

完整请求、响应、事件、日志副本、跨进程指纹和保留性核对见[专项报告](../artifacts/m14-compaction-test-20261021/verification.md)；当前接力入口见[进度 §3.1](current-progress.md#31-2026-10-21-压缩专项复验)。未重新进行 NativeAOT 产品发布、GUI、断电或 OS 隔离验收。

## 9. 2026-10-21 压缩替换后的验收

用户确认参考 Codex 的同步本地压缩，要求清理旧实现。已在原 ConversationContext／AgentLoop 中替换为有界文本交接、有界用户原话及当前宿主规则，末尾追加摘要任务；删除七字段状态、JSON 摘要解析和旧列表逐字校验。完整历史仍在唯一 session-log 中，具体契约和兼容中断见[压缩设计](context-compaction-design.md)。

- 真实 `glm-5.3`／Chat Completions：在原九轮会话的独立日志副本上完成 4 轮连续自动压缩，再启动独立进程恢复并完成第 5 次。共 10 次真实请求、5 次摘要、5 个压缩事件和 5 条同日志成功审计，无预置或重放摘要。
- 全部回合 Completed，工具请求／准备／执行均为 0，无保存警告。回答保留 ORCHID-4F9B、唯一修改 src/BadgeFormatter.cs、命令拒绝且未执行、当前仅讨论及从未运行构建／测试。原日志及六个夹具文件哈希未变。
- 跨进程 canonical／模型视图／摘要状态哈希及折叠边界一致；恢复不重放历史工具。usage 校准启动时重置，所以不以 token 估算数逐字一致作为内容恢复判据。
- 最终相关回归 75/75，普通回归 965 通过／2 链接权限跳过／0 失败，scale 另 1/1。50 轮增长保持有界活动视图。最终 NativeAOT 无 compiler／trimming／AOT 警告，仓库外 fake-model smoke 通过；真实服务调用使用 managed Core 驱动，两类证据分别记录。
- 最终另以离线回归统一压缩事件前后 token 估算的校准口径，没有改变摘要请求或提交契约。文本交接仍可能继承历史错误数字，本轮摘要重复旧截断长度错误；因此仅关闭指定样本的自动压缩／遵循／恢复尾项，不声称全部事实或模型可靠。

完整命令、实际差异和机器结果见[本轮报告](../artifacts/compaction-replacement-20261021/verification.md)。M15、GUI、断电与 OS 隔离不在本轮范围，§7–§8 保持历史含义。
