# 项目指令加载的设计核查与实施记录

归档日期：2026-10-24。来源：[project-instructions-design.md](project-instructions-design.md)。

> 本页保存原设计阶段的核查、实施或验收记录，章节编号沿用原文；未收录的契约章节见来源文档。文中的「当前」「本轮」「尚未完成」等表述只指各条记录的历史时点，不是当前状态或操作授权。历史正文不持续更新；链接随目录整理调整。原始本机证据可能位于未纳入 Git 的 `artifacts/`，其他机器不一定具备。

## 2. 现有代码核查：有哪些能力，缺什么

### 2.1 复用清单

| 当前位置 | 已有职责／接口 | 本设计的接入方式 |
|---|---|---|
| [Program.cs](../../TinyHarness.Cli/Program.cs)，`RunChatAsync` | 组装配置、模型、工具、权限；新会话分支先构造 host，再进入输入循环 | 调整新会话的创建时点；继续使用同一个组装入口 |
| [ChatSessionConsole.cs](../../TinyHarness.Cli/Services/ChatSessionConsole.cs)，`RunAsync` | 单一输入循环、非空输入提交、取消与中断确认；当前构造时必须提供 host | 扩展为可持有“未创建的新会话入口”或“已恢复 host”；不另建第二个聊天循环 |
| [ChatSessionHost.cs](../../TinyHarness.Core/Services/Session/ChatSessionHost.cs) | 拥有固定 Session、共享上下文、每回合独立 Loop、writer 和单活动运行约束 | 保留会话所有权；增加规则加载的协调，不新建另一种 SessionHost |
| [ChatSession.cs](../../TinyHarness.Core/Models/Session/ChatSession.cs) | Session 身份、工作区、模型和真实用户回合 | 继续负责真实用户回合；项目规则不伪装成 `SessionTurn` |
| [AgentLoop.cs](../../TinyHarness.Core/Services/Agent/AgentLoop.cs)，`RunTurnAsync` / `RunTurnCoreAsync` | 校验固定 system，追加用户输入，压缩、请求模型、整轮准备／授权／顺序执行 | 在既有边界接入规则刷新与失效检查；不另建循环 |
| [ConversationContext.cs](../../TinyHarness.Core/Services/Context/ConversationContext.cs) | canonical 消息、模型视图、预算、结构化压缩、恢复和尾部回退 | 增加宿主消息来源与当前规则投影，仍是唯一模型视图构造者 |
| [ChatMessage.cs](../../TinyHarness.Core/Models/ChatCompletions/ChatMessage.cs) | SDK 无关协议消息；没有宿主规则来源标记 | 保留协议 DTO；来源由受控元数据标识，不能猜测正文 |
| [ITool.cs](../../TinyHarness.Core/Services/Tools/ITool.cs) / [ToolPreparation.cs](../../TinyHarness.Core/Models/Tools/ToolPreparation.cs) | 同步、无副作用 `Prepare`，不可变参数、能力、TargetPaths、私有执行计划和异步 Execute | 在原计划上增加指令作用域描述；不把读规则塞进同步 Prepare |
| [Workspace.cs](../../TinyHarness.Core/Services/Runtime/Workspace.cs) | 词法解析、工作区边界、执行时最终链接检查、稳定显示路径 | 指令路径采用相同边界；必要的加强收敛在该路径边界内 |
| [DirectoryWalker.cs](../../TinyHarness.Core/Services/Runtime/DirectoryWalker.cs) | list/search 共用遍历、生成目录排除、不下降目录链接、数量上限 | 复用排除和遍历规则；不能另写一个范围更宽的指令扫描器 |
| [PermissionEngine.cs](../../TinyHarness.Core/Services/Permissions/PermissionEngine.cs) | Allow / Ask / Deny、grant、命令规则、worker 限制和 NeedsInspection | 原样保留授权责任；规则内容不直接生成 grant 或命令白名单 |
| [SessionStore.cs](../../TinyHarness.Core/Services/Persistence/SessionStore.cs)，`ISessionStoreWriter` | Commit、执行意图提交、显式记录追加及 recorder factory | 在同一 writer 中增加显式规则事实；不新增规则日志或独立快照 |
| [SessionLogStore.cs](../../TinyHarness.Core/Services/Persistence/SessionLogStore.cs) | JSONL writer、reader、ProjectionBuilder、source-generated JSON 和提交游标 | 扩展同一条写／读路径及恢复投影 |
| [SessionLogScanner.cs](../../TinyHarness.Core/Services/Persistence/SessionLogScanner.cs) | 物理批次、身份、顺序与已知必需 kind 的共享校验 | 新 kind 必须在此校验，保留未知必需 kind 拒绝策略 |
| [RunPersistence.cs](../../TinyHarness.Core/Services/Persistence/RunPersistence.cs)，`IRunRecorder` | 运行／工具／压缩记录；不是项目指令 provider | 复用必要的事实展示，不为读规则另建 recorder |
| [SessionLogRunRecorder.cs](../../TinyHarness.Core/Services/Persistence/SessionLogRunRecorder.cs)，`StartAsync` | 首先提交初始上下文，再保存运行审计与真实用户历史条目 | 首次提交须已包含规则事实；保持真实用户条目只出现一次 |
| [SessionResume.cs](../../TinyHarness.Core/Services/Session/SessionResume.cs) | 工作区／模型／system／opaque／工具组／压缩边界校验；纯内存重建 | 恢复规则元数据，但磁盘刷新放在恢复后的宿主边界 |
| [IHostEventSink.cs](../../TinyHarness.Core/Services/Session/IHostEventSink.cs) / [HostEventKind.cs](../../TinyHarness.Core/Models/Session/HostEventKind.cs) | 已有统一发布通道、身份关联和展示失败处理 | 扩展明确的规则变更通知，不再建一套通知总线 |
| [SessionHistoryReader.cs](../../TinyHarness.Core/Services/Persistence/SessionHistoryReader.cs) | 共享 scanner 和有界展示分页 | 规则展示走同一投影与分页，原来的工具事实和游标不变 |

### 2.2 已确认的缺口与风险

1. 搜索 Core、CLI 的 `AGENTS`、`instructions`、provider 和相关接口，未发现生产用的项目指令发现／加载／版本管理接口。Worker 提示中的 `AGENTS.md` 是“不可信任务材料”的说明，不是加载实现。不能把它当作已有 provider，也不能给 worker 自动加载整个项目。
2. `ChatSessionHost` 新建构造函数立即分配 Session；内部构造函数立即调用 writer factory。`SessionStoreWriter` 构造时会创建目录、锁文件和空 JSONL。因此现在的“进入 chat”不是所需的“首次发送才创建会话”。
3. system 一致性检查存在于 `ChatSessionHost`、`AgentLoop.RunTurnAsync` 和 `SessionResume.Restore`。这是有效的恢复约束，不能为动态 AGENTS 内容直接删除它。
4. `ChatMessage` 和 `PersistedMessage` 没有可信来源字段。仅检测 `# AGENTS.md` 或 `<INSTRUCTIONS>` 正文，会把用户粘贴内容错误认成宿主规则。
5. `ConversationContext` 当前只懂 system/user/assistant/tool，尚无“当前有效项目规则”的独立投影。首次规则若进入保留头部，旧版本可能一直留在模型视图；后续规则若进入折叠区，又可能只剩模型摘要。
6. `AssistantSpans`、`FoldSelection.FoldMessageCount` 和 `BuildOriginalTaskMessage` 基于现有消息布局。插入宿主 user 消息后，必须复核相邻 span 之间的消息、折叠区连续性、原始任务归属和预算，不能只让普通请求多出一条消息。这是接入风险，尚未通过新增场景复现为现有产品缺陷。
7. `ToolPreparation.TargetPaths` 是权限资源范围，不充分表达文件／目录／递归遍历语义。search 和递归 list 只声明根路径；shell 声明 cwd，补丁声明所有目标文件。不能据此声称已覆盖所有子目录规则。
8. `AppendRecordsAsync` 目前只接收 audit/history；scanner 和 ProjectionBuilder 的 kind 集合也都是封闭集合。新规则事实不能只在一处添加，或用任意 audit 文本冒充可恢复状态。
9. `PermissionEngine` 目前没有“仅清理临时 allow、保留 deny”的窄入口；host 也持有同一个引擎供后续运行使用。规则失效不能靠新建引擎偷偷丢弃拒绝，实施时须补足所需生命周期操作。现有跨回合 grant 生命周期是否完全符合 PLAN 另需测试核对，本轮不把它宣称为已复现或已修复的问题。

以上是静态核查结论；本轮没有执行代码或新增测试。不能把这些接入风险写成已经修复的缺陷。

## 3. 从本地 Codex 借鉴哪些内容

以下路径相对于上述 Codex checkout：

| 源码 | 观察到的行为 | TinyHarness 的选择 |
|---|---|---|
| `codex-rs/core/src/agents_md.rs`，`agents_md_paths` | 通过项目标记确定根，根到 cwd 有序发现；有 override 和 fallback 文件名 | 使用用户明确配置的 workspace root；第一版只认 AGENTS.md，不自动向工作区外找根 |
| 同文件，`LoadedAgentsMd` | 有序保存正文和来源，提供模型片段与 sources | 保存路径、作用域、内容与标识；同一份数据生成模型和检查投影 |
| `context/user_instructions.rs` | AGENTS 片段以 user role 和专用标记渲染 | 规则是独立宿主上下文，实际用户输入紧随其后；不是第二次模型请求 |
| `context/world_state/agents_md.rs` | 比较当前与之前快照；明确 replacement/removal | 显式标识生效／替换／移除；不在旧正文上原地修改历史 |
| `agents_md_manager.rs`，`refresh` | 串行捕获，仓库缓存按环境选择和信任状态失效；用户／线程 provider 可独立刷新 | 固定单工作区，无需多环境 provider 体系；在明确边界检查已发现规则，避免引入庞大的缓存／插件管理层 |
| `session/mod.rs`，`capture_step_context_inner` | 请求准备阶段刷新规则，之后使用 coherent snapshot | 一次请求和一次审批使用明确规则版本；不能审批到一半偷偷切换 |

不照搬：多环境、子 Agent 继承、账号／线程 provider、fallback 配置平台。该本地 Codex 代码允许符号链接，读入预算溢出时可截断；TinyHarness 按自己的工作区边界处理链接，规则正文超限时明确拒绝该加载，不悄悄截断规范后继续。

## 10. 推荐实施切片与文件影响

按纵向切片交付，每片只保留一条有效生产路径。下表是未来实施范围，不是本轮代码修改清单。

| 切片 | 内容 | 主要现有接点 | 完成要求 |
|---|---|---|---|
| A：类型／加载／日志来源 | loader、预算、规则快照／来源索引、版本化记录和恢复校验 | Workspace、ConversationContext、SessionLogBatch、PersistedSession、SessionLogStore、scanner、SessionResume | fake／临时目录验证规则和日志闭环；无平行存储 |
| B：首次发送／请求与压缩 | 延迟新 host、根规则、单请求顺序、有效规则投影与准确预算 | Program、ChatSessionConsole、ChatSessionHost、AgentLoop、ConversationContext | 首次发送／取消／失败／多轮／压缩／续聊均满足契约；不保留旧 eager 分支 |
| C：局部规则与执行边界 | 指令作用域、递归 coverage、延期工具轮、审批后和 dispatch 检查 | ITool／ToolPreparation、五个工具、DirectoryWalker、AgentLoop、PermissionEngine 的临时 grant 生命周期 | 多目标／递归／AGENTS 自修改有证据；不破坏工具原子组和审批 |
| D：展示与统一验收 | 规则历史／事件、离线检查、回归与 NativeAOT smoke | SessionHistoryItem、recorder、reader、ConsoleHostEventSink、测试 fixture | 全部矩阵通过且旧路径退出，才将 M14.1 标为完成 |

A、B 可先取得局部证据，但 C、D 未完成前不能宣称完整支持局部 AGENTS；不能先在 README 宣布“自动遵守全部项目规则”。后续 M14.2 系统提示词的工程接入与模型遵循评估分别验收。

## 11. 过时设计、替换及清理清单

**没有发现两个现存 AGENTS loader，需要防止的是新增实现与已有职责重复。** 下表区分必须替换、必须保留与不在本切片清理的内容；本轮只记录，不删除代码。

| 现有／可能出现的路径 | 处置 | 退出判据 |
|---|---|---|
| RunChatAsync 在用户输入前创建新 host／writer | 必须替换为首次发送创建；旧 eager 生产分支退出 | 空输入／EOF／取消不产生 session lock/jsonl；生产新会话只有一个创建路径 |
| ChatSessionConsole 构造即要求固定 host 的新会话用法 | 在同一 Console 上演进 pending／attached 状态；不复制类 | 输入、审批和检查确认继续共用单 reader；没有 LegacyChatConsole／V2Console |
| 通过拼接 WorkspaceSystemPrompt 注入 AGENTS 的捷径 | 禁止新增；若实施临时出现须移除 | system 常量与一致性检查保留，规则变化不靠关闭校验解决 |
| 原始 UserInput 混入规则／模拟第二个用户 Turn | 禁止新增；相关临时路径退出 | Turn、recorder user history、当前目标均只代表实际用户输入 |
| 依赖正文标记判断规则来源 | 由结构化日志来源与 canonical 索引替换 | 用户粘贴相同标记不影响受管理规则分类 |
| 原始历史直通导致旧规则重获效力、头部永久保留旧规则的假设 | 在原 ConversationContext 内替换相关投影和压缩假设 | enabled/disabled 预算、多个规则版本和多轮折叠均只保留当前规则有效性 |
| 用 TargetPaths 当作完整指令作用域的假设 | 权限字段保留；工具声明独立 InstructionScopes，统一门禁消费 | 递归 list/search、shell cwd 和多文件 patch 均有准确测试，不重复解析原始参数 |
| 独立规则 JSON／新 writer／新 compactor／第二权限引擎 | 不引入；临时实现须在切片收口时退出 | 规则事实仅进统一日志，模型视图仅由原 context 构造，执行仍走原 Loop |
| 规则 loader 重复实现路径／walker 排除逻辑 | 共用或提取原能力；不能拷贝后各自演进 | 同一目录排除、links、数量限制在预检和执行一致 |
| 三处固定 system 校验、NeedsInspection、执行意图／确认、worker 限制 | 必须保留，不能误判为过时代码 | 既有测试及故障场景不退化 |
| ISessionStoreWriter、IRunRecorder、SessionStoreWriter、现有 JSON source generation | 复用并必要扩展；不是过时模块 | 没有新增替代版本或接口默认 no-op 导致生产漏记规则 |
| SnapshotPath、LoadedSnapshot、MaxSnapshotBytes、CommitSnapshotAsync 等历史命名 | 当前仍在统一日志路径中使用，不是旧 writer 活着的证据；不做全库机械更名 | 本切片只在确有契约误导的局部说明中澄清，广泛 API 重命名另行评估 |
| 已移除的旧三版读写、显式迁移 | 不重新引入 | 新格式扩展没有恢复 snapshot/audit 双写或旧格式兼容工程 |

同步更新直接依赖旧创建时点、消息数量／排列、history kind 或构造函数的测试；不能靠保留一个“旧版生产入口”让测试继续通过。删除看似有意的测试／能力前仍按 AGENTS 的授权规则处理。

## 13. 完成门槛与实施前需确认的边界

M14.1 只有同时满足以下条件才完成：首次发送生命周期正确、根／局部／递归规则正确、工具边界无旧计划执行、规则不会在压缩后丢失或重新激活、来源可从同一日志恢复、历史与实时可检查、既有安全与 AOT 路径不退化，并按 §11 删除被替换的生产分支。

实施前必须明确列出本提案涉及的 API／构造函数调整，以及 §8.3 的记录版本兼容边界；文档本身不授权公开 API／格式变更。预算是初始设计值，若当前仓库 fixture 已不能容纳，应记录测量和原因再调整，不能静默放宽或删场景。递归 coverage 的准确范围与执行时守卫必须先取得离线证据；若做不到，应收窄该工具能力并报告未完成，不能用“模型会自己读”替代。

不要求为进入实施重新研究整套架构；只有具体证据推翻以上假设时修订对应章节。交付报告应逐条给出复用、扩展、替换和未验证项，不能只报测试数量。

## 14. 实施结果与本地验证

2026-10-18 按用户明确指令实施。GLM-5.3 参与 host／loop／权限边界，GLM-5.3-Flash 参与不可变工具准备对象，GPT-6-Luna 参与持久化、恢复与测试；GLM 两种模型请求随后遭遇 429，主 Agent 接手未完成部分、检查实际文件并完成集成。模型自述不作为验收证据。

### 14.1 复用、扩展与替换

| 位置 | 实施结果与兼容影响 |
|---|---|
| `ProjectInstructionLoader`、`ProjectInstructionSnapshot` | 单一异步 loader；严格 UTF-8／BOM／原始 SHA-256，固定预算；revision 不含加载时间或探测集合。Renderer 同时供模型、日志校验和恢复使用 |
| `ToolPreparation`、五个工具、`DirectoryWalker` | 新增独立 `InstructionScopes`、内存 coverage 及保留私有 plan 的不可变派生；scope 附带 maxDepth、MaxEntries、FilesOnly。发现与执行共享原 walker，实际枚举有上限，未覆盖目录在进入或读取正文前拒绝 |
| `ChatSessionHost` | 两个现有 public 构造函数末尾追加可选 `instructionLoader`、`initialInstructions`、`knownSecrets`；生产 run/chat/smoke 显式注入，worker 保持原任务包。当前上下文拥有有效规则，host 协调暂存、可靠提交与生效 |
| `AgentLoop`、`PermissionEngine` | 内部窄边界刷新规则；模型请求及每个摘要请求前、整轮准备后、每次审批后与每次派发前检查。延期补齐原工具组，未启动调用保存 `NotStartedReason`，与 `PreparationError`、拒绝和未知副作用分别记录。规则变化清临时 allow，保留 deny 和 NeedsInspection |
| `ConversationContext` | 结构化记录绑定 canonical 下标；旧规则只从模型视图过滤，当前全文单独计费；普通请求、摘要与候选预算共用规则投影。折叠区覆盖 assistant 之间的真实连续区间，Restore/Reset/Truncate 同步来源 |
| `SessionStoreWriter`／scanner／projection／resume | 保留 `tinyharness.session-log/1`，新增必需 kind 与 `tinyharness.project-instructions/1` schema；规则与消息同批提交。`scopes-tracked` 写盘省略正文，读取时从当前 revision 补齐。旧统一日志无规则时恢复 unmanaged；未知版本和损坏来源拒绝 |
| `ChatSessionConsole`、`Program` | 在同一输入循环增加 pending factory；空行、EOF、首次发送前取消不创建 Session。规则预检先于 host/writer 创建，失败交还输入循环；已有 host 用法继续可用。内部 `TryGetSession` 查询不触发创建，pending 的 `Session` 访问明确拒绝 |
| 宿主事件与历史 | 同一 writer 提交后发布原 ID／sequence 的规则 history；`ProjectInstructionsChanged` 仅携带脱敏元数据和 source/scope。消费者失败进入 DisplayWarning，canonical 正文不受展示脱敏影响 |

旧 eager 新会话生产分支已替换；未新增另一种 Console、writer、Compactor、权限引擎或规则 sidecar。固定 `WorkspaceSystemPrompt` 和三处 system 一致性检查保持有效。公开 API 为上述增量数据／可选参数扩展；未改协议 SDK、依赖、配置平台或 NativeAOT 设置。

### 14.2 已执行验证

默认使用 fake model、临时工作区／存储及合成秘密；没有付费请求、provisioning、远端或发布操作。

| 场景 | 证据 |
|---|---|
| 延迟创建、预检失败重发、首次模型失败 | `ChatSessionConsoleTests`：工厂次数、无目录／日志、真实 Turn 与请求数 |
| 根／BOM／空／缺失／兄弟／缺失位置新增和删除、固定预算 | `ProjectInstructionLoaderTests`、`ProjectInstructionRendererTests`：原字节 hash、完整规则顺序、revision／discovery 独立、编码／大小／文件数／总量／token／探测数拒绝 |
| 递归、深度与排除、新目录 coverage、shell 与多文件 patch 范围 | `InstructionScopeTests` 与 loader 测试；实际递归 search 首先发现深层规则，旧整轮零执行，重新采样后执行一次 |
| 审批变化、自修改、再次变化和临时授权 | `InstructionScopeTests`、`ProjectInstructionBoundaryTests`：停止后续旧审批与工具，保留已确认 patch，deny／NeedsInspection 不失效，KnownNotStarted 和原因可恢复 |
| 上下文／压缩／伪造标记／回退 | `ProjectInstructionContextTests` 及原 Context 测试：canonical 不改写、当前规则唯一、真实任务正确、连续工具边界、无预算模式与 Restore/Reset/Truncate |
| 日志、来源完整性、故障和展示 | `ProjectInstructionPersistenceTests` 及原日志故障／历史分页测试：旧统一日志兼容，schema／renderer／revision／hash／scope／下标篡改和 null 拒绝；初始与更新写失败不激活候选；实时历史一致、合成秘密脱敏、回调失败告警 |
| 原入口和 worker | 普通回归包含原 run/chat/session/配置/MCP/沙箱模拟测试；worker 未启用规则自动发现 |

命令与产物：

```powershell
dotnet test TinyHarness.Tests/TinyHarness.Tests.csproj --no-restore --filter 'FullyQualifiedName!~SessionJournalScaleTests' --logger 'trx;LogFileName=m14-regression.trx' --results-directory artifacts/test-results
dotnet test TinyHarness.Tests/TinyHarness.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~SessionJournalScaleTests' --logger 'trx;LogFileName=m14-scale.trx' --results-directory artifacts/test-results
dotnet publish TinyHarness.Cli/TinyHarness.Cli.csproj -c Release -r win-x64 --self-contained true --no-restore -o artifacts/m14-instructions-nativeaot
```

- 普通回归结果见 `artifacts/test-results/m14-regression.trx`；最终统计在 README 项目状态列出。独立 scale **1/1 通过**，原预算不变，固定指标副本为 `artifacts/m14-instructions-scale-results.json`。
- 100／1,000／10,000 规模 append p95：2.6168／2.5574／2.5908 ms；10,000 恢复 1,570.0449 ms、89,891,968 B；200 条首屏 319.4492 ms、38,734,864 B，读取 24,901,127 B。返回页 8 MiB 不等于 scanner 总 I/O 上限。
- 规则额外测量：10,011 条 canonical、10 次规则版本变更，模型视图 10,002 条、仅一份当前规则；当前规则 421 token，投影 0.134 ms、88,832 B 分配。该合成 workload 测量不是所有仓库的性能保证。
- NativeAOT **0 warning／error**。从仓库外的独立临时目录，使用合成配置和隔离用户配置执行 `TinyHarness.exe smoke --config <临时配置>`，退出 0，Completed、17 steps、16 toolExecs；验证规则初始加载／更新／续聊来源、日志恢复／历史／confirmed patch diff。文本为 `artifacts/m14-instructions-native-smoke.txt`；exe SHA-256 为 `C9DE97E219B68F8BAEB8133A1EE25578FB5E11C0BD48C2CF5149BEBBBF8926F3`。
- 本轮仅对修改的 C# 文件做基础 whitespace 格式化，随后保留未改代码的原有对齐，`git diff --check` 通过；不声称复现 ReSharper Code Cleanup。

### 14.3 未验证项与边界

- 两项符号链接创建测试因本机权限跳过：新 loader 的规则 leaf／目录 link 场景，以及既有 lock/log leaf link 场景；不能把跳过计作通过，实际 junction／ACL 权限矩阵仍需对应环境复验。
- 真实模型、GUI、断电持久性及实际 Windows OS 隔离没有在本轮验证；只读 MCP worker 仍使用既有 focus／敏感排除任务包。
- 文件读取前后观测和 dispatch 检查不能提供文件系统原子快照；恶意宿主进程仍可能在检查后制造竞态。shell 只覆盖 cwd 与明确识别的工作区内 executable 作用域，不声称推断脚本的全部文件访问。
- M14.1 本地验证不代表后续 M14.2 的模型遵循评估或 M15–M17 已完成；M14 的最新范围见 PLAN §8.2。本轮未执行 Git 写操作、远端、发包或部署。
