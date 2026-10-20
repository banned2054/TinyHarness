# 基础系统提示词设计与接入（M14.2）

更新日期：2026-10-20。

## 1. 范围与完成定义

用户确认通过自然对话表达讨论、诊断和实施要求。本切片集中完善系统提示词，不实现 Analyze／Implement 切换、只读开关、意图分类模型或新的执行引擎。工具继续经过既有权限、路径、沙箱和中断检查。

工程接入与本地验证已完成；2026-10-20 经用户明确授权，已执行真实 GLM 行为评估，讨论／实施边界等场景取得证据，但真实压缩未成功提交，M14 尚不能收口，详见 §7。脚本模型只证明接入链路，不证明语义遵循。M15 的执行中补充／暂停、累计预算与 M16 的确定性交付报告不在本切片内。

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
- 普通模型视图保留唯一完整 system；压缩请求仍使用专用摘要契约，普通请求在折叠后继续携带原 system。不能把行为模板替代摘要 JSON 契约。
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
