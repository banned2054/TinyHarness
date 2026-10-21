# 项目指令加载与会话首次发送设计（M14.1）

更新日期：2026-10-18。

## 1. 状态、范围与阅读顺序

**状态：已实施，离线本地验收通过；链接创建受限场景明确未验证。** 首次发送创建、根／局部／递归规则、工具延期、上下文投影、统一日志及续聊刷新已落盘。实际 API、测试、产物和限制见 §14；本文不授权后续 Git、真实模型或外部操作。

- 源码核查基线：TinyHarness `10a697ab26967cdf2d4a1196e7f88de892f8738b`，核查时工作区干净。
- 本地参考：`C:\Code\Rust\codex`，HEAD `1343180d6c83a7d58e20dc4394678570aabde79a`；结论来自该 checkout 的源码阅读，不代表在线最新版或运行验证。
- [PLAN.md](../PLAN.md) 决定全局范围和顺序；本文细化其 §8.1。已有日志、协议和中断契约继续以[会话持久化设计](session-persistence-design.md)为准；[AGENTS.md](../AGENTS.md)规定工作授权。
- 本阶段只实现项目指令能力。用户后续确认 M14.2 聚焦[基础系统提示词](system-prompt-design.md)，不引入 Analyze / Implement 切换；任务中补充输入属于 M15，Git 差异与完整交付报告属于 M16，均不借本设计提前实现。

阅读顺序：§2–§3 保留实施前源码核查与设计依据，§4–§13 定义契约和验收要求，§14 记录实际实施与本地证据。拟议名称与实际 API 的差异以 §14 为准，不把历史缺口当作当前缺陷。

## 2. 现有代码核查：有哪些能力，缺什么

### 2.1 复用清单

| 当前位置 | 已有职责／接口 | 本设计的接入方式 |
|---|---|---|
| [Program.cs](../TinyHarness.Cli/Program.cs)，`RunChatAsync` | 组装配置、模型、工具、权限；新会话分支先构造 host，再进入输入循环 | 调整新会话的创建时点；继续使用同一个组装入口 |
| [ChatSessionConsole.cs](../TinyHarness.Cli/Services/ChatSessionConsole.cs)，`RunAsync` | 单一输入循环、非空输入提交、取消与中断确认；当前构造时必须提供 host | 扩展为可持有“未创建的新会话入口”或“已恢复 host”；不另建第二个聊天循环 |
| [ChatSessionHost.cs](../TinyHarness.Core/Services/Session/ChatSessionHost.cs) | 拥有固定 Session、共享上下文、每回合独立 Loop、writer 和单活动运行约束 | 保留会话所有权；增加规则加载的协调，不新建另一种 SessionHost |
| [ChatSession.cs](../TinyHarness.Core/Models/Session/ChatSession.cs) | Session 身份、工作区、模型和真实用户回合 | 继续负责真实用户回合；项目规则不伪装成 `SessionTurn` |
| [AgentLoop.cs](../TinyHarness.Core/Services/Agent/AgentLoop.cs)，`RunTurnAsync` / `RunTurnCoreAsync` | 校验固定 system，追加用户输入，压缩、请求模型、整轮准备／授权／顺序执行 | 在既有边界接入规则刷新与失效检查；不另建循环 |
| [ConversationContext.cs](../TinyHarness.Core/Services/Context/ConversationContext.cs) | canonical 消息、模型视图、预算、结构化压缩、恢复和尾部回退 | 增加宿主消息来源与当前规则投影，仍是唯一模型视图构造者 |
| [ChatMessage.cs](../TinyHarness.Core/Models/ChatCompletions/ChatMessage.cs) | SDK 无关协议消息；没有宿主规则来源标记 | 保留协议 DTO；来源由受控元数据标识，不能猜测正文 |
| [ITool.cs](../TinyHarness.Core/Services/Tools/ITool.cs) / [ToolPreparation.cs](../TinyHarness.Core/Models/Tools/ToolPreparation.cs) | 同步、无副作用 `Prepare`，不可变参数、能力、TargetPaths、私有执行计划和异步 Execute | 在原计划上增加指令作用域描述；不把读规则塞进同步 Prepare |
| [Workspace.cs](../TinyHarness.Core/Services/Runtime/Workspace.cs) | 词法解析、工作区边界、执行时最终链接检查、稳定显示路径 | 指令路径采用相同边界；必要的加强收敛在该路径边界内 |
| [DirectoryWalker.cs](../TinyHarness.Core/Services/Runtime/DirectoryWalker.cs) | list/search 共用遍历、生成目录排除、不下降目录链接、数量上限 | 复用排除和遍历规则；不能另写一个范围更宽的指令扫描器 |
| [PermissionEngine.cs](../TinyHarness.Core/Services/Permissions/PermissionEngine.cs) | Allow / Ask / Deny、grant、命令规则、worker 限制和 NeedsInspection | 原样保留授权责任；规则内容不直接生成 grant 或命令白名单 |
| [SessionStore.cs](../TinyHarness.Core/Services/Persistence/SessionStore.cs)，`ISessionStoreWriter` | Commit、执行意图提交、显式记录追加及 recorder factory | 在同一 writer 中增加显式规则事实；不新增规则日志或独立快照 |
| [SessionLogStore.cs](../TinyHarness.Core/Services/Persistence/SessionLogStore.cs) | JSONL writer、reader、ProjectionBuilder、source-generated JSON 和提交游标 | 扩展同一条写／读路径及恢复投影 |
| [SessionLogScanner.cs](../TinyHarness.Core/Services/Persistence/SessionLogScanner.cs) | 物理批次、身份、顺序与已知必需 kind 的共享校验 | 新 kind 必须在此校验，保留未知必需 kind 拒绝策略 |
| [RunPersistence.cs](../TinyHarness.Core/Services/Persistence/RunPersistence.cs)，`IRunRecorder` | 运行／工具／压缩记录；不是项目指令 provider | 复用必要的事实展示，不为读规则另建 recorder |
| [SessionLogRunRecorder.cs](../TinyHarness.Core/Services/Persistence/SessionLogRunRecorder.cs)，`StartAsync` | 首先提交初始上下文，再保存运行审计与真实用户历史条目 | 首次提交须已包含规则事实；保持真实用户条目只出现一次 |
| [SessionResume.cs](../TinyHarness.Core/Services/Session/SessionResume.cs) | 工作区／模型／system／opaque／工具组／压缩边界校验；纯内存重建 | 恢复规则元数据，但磁盘刷新放在恢复后的宿主边界 |
| [IHostEventSink.cs](../TinyHarness.Core/Services/Session/IHostEventSink.cs) / [HostEventKind.cs](../TinyHarness.Core/Models/Session/HostEventKind.cs) | 已有统一发布通道、身份关联和展示失败处理 | 扩展明确的规则变更通知，不再建一套通知总线 |
| [SessionHistoryReader.cs](../TinyHarness.Core/Services/Persistence/SessionHistoryReader.cs) | 共享 scanner 和有界展示分页 | 规则展示走同一投影与分页，原来的工具事实和游标不变 |

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

## 4. 产品行为与首次发送生命周期

### 4.1 新会话

```text
进入 chat：等待输入，无 Session ID、无会话锁、无会话 JSONL
  → 收到首条非空用户输入
  → 校验工作区，加载根目录规则并检查预算
  → 创建原有 ChatSessionHost / Session / writer
  → 创建真实 Turn / Run，准备 system、规则上下文和用户输入
  → 在同一日志批次提交身份、初始上下文与规则来源事实
  → 请求模型一次
```

- 空行、仅打开界面、EOF、首次发送前取消均不创建 Session，不为避免空会话事后删除日志。
- “首次发送”指接受用户消息处理，不要求模型已成功回答。消息已经接收并开始运行后，模型失败／取消仍留下真实回合及终态。
- 规则预检失败发生在新 Session 创建前：不调用模型、不创建会话日志，报告具体路径和原因，输入交还宿主以便用户修改后重发；不自动丢弃后换成另一条输入，也不无限重试。
- writer 创建后或初始提交失败：明确报告会话未可靠保存；可能已经存在空文件或可靠前缀，保留供诊断，不擅自清理，更不发请求后假装已保存。
- CLI 仍只有 `ExclusiveInputReader` / 现有输入循环；审批和 NeedsInspection 不另读 stdin。新会话入口可以由一次性 factory 延迟生成 host，但不能复制出“新版 Console”。factory 不得提前捕获已创建的 host。
- 当前 `ChatSessionConsole.Session` 直接返回 `_host.Session`；pending 状态下不得为满足这个属性而创建空 Session。实施时明确未创建状态的访问方式（例如内部 TryGetSession），并同步所有调用点；已有会话和成功创建后的访问继续指向同一个 host。
- 初始输入只提交一次。`SessionTurn.UserInput`、recorder 的 user history 与发送模型的真实输入语义保持一致，不把 AGENTS 拼入 UserInput。

### 4.2 已有会话与其他入口

| 入口 | 行为 |
|---|---|
| 新 `chat` | 首次发送创建；后续回合复用同一个 host 和规则状态 |
| `chat --session <id>` | 打开时先持锁、严格恢复、显示必要中断事实；下次用户提交前重新检查项目规则。不重复创建 Session，不改变 ID |
| `run "任务"` | 命令参数已经是用户发送，直接采用与 chat 相同的初始化／规则路径，不另写 run loader |
| `session list/show` | 严格离线，只读已存的规则检查投影；不扫描当前工作区、不解析 key、不触发模型或工具 |
| `smoke` | 保持 fake 模型，增加规则加载 fixture，复用相同宿主路径 |
| 只读 MCP worker | 保留既有 focus、敏感排除和固定任务包契约；本切片不默认接入项目规则发现。今后接入须单独证明不会扩大读取或外发范围 |

创建时点变化是 CLI 生命周期改变，不是新增另一种 Session 数据模型。具体 public constructor/property 的调整须在实施时列出兼容影响；保留原有低层测试入口不等于保留第二条生产持久化路径。

## 5. 规则发现、身份、范围与限制

### 5.1 规则链

- 根固定为 Session.WorkspaceRoot，不因发现 `.git`、源码或 AGENTS 文字自动扩大范围；不读取工作区外的父目录、用户 home 规则或任意引用文件。
- 第一版只认约定文件名 `AGENTS.md`，不解析 override、include、URL、安装脚本或可执行配置。文件名和路径比较沿用当前 OS 语义。
- 对文件 `src/Foo/A.cs`，发现链为根、src、src/Foo；对目录操作，链终点就是该目录。新增文件／目录从最近存在祖先开始，仍保留尚不存在目录的缺失探测记录。
- 根规则作用于整个工作区；局部规则只作用于该目录子树。根到叶排序，较深规则细化局部约定；两个兄弟目录的规则不能互相覆盖。
- 多目标补丁取全部目标链的并集，一份文件只加载一次；模型文本为每份规则写清适用目录，不能把合并文本宣传成无作用域的全局规范。
- 规则缺失、空文件是有效结果；缺失目录也须被跟踪，下一次边界才能发现新建的 AGENTS。文件删除后移除当前有效规则，但历史加载事实不删除。

### 5.2 路径、内容与错误

- 使用现有 `Workspace.ResolveInside` 和 `EnsureFinalTargetInside` 思路；每次实际读取前检查文件及祖先链接，链接越界、循环、无法确认最终目标时拒绝。
- 初版指令发现拒绝规则文件自身的符号链接／重解析点，目录遍历不下降目录链接；显式内部目录链接的身份与作用域易歧义，第一版也拒绝指令链穿过它。此限制仅作用于自动指令发现，不顺便重写普通文件工具已有链接语义。
- 不用 `File.Exists` 的 false 混淆“确实不存在”和“拒绝访问”。只有明确 NotFound 才记为缺失；权限、I/O、非法编码和路径检查失败有独立错误。
- UTF-8 严格解码，可接受 UTF-8 BOM；正文不 Trim、不截断、不做密钥替换再送模型。原始字节 SHA-256 标识磁盘变化，明确记录 BOM 解码行为；渲染文本另有 hash，二者不可混用。
- 读取使用 async API 和 CancellationToken；限长读取最多预算加一字节，不能先无限读入再检查 Length。读取前后观测不稳定时最多重试一次，仍变化则失败。
- 这些检查不提供文件系统原子快照，也不承诺阻止恶意宿主进程在检查后的瞬间改文件。已观测变化必须阻止旧计划，未观测竞态属于明确限制，不能以规则 loader 取代沙箱。

### 5.3 初版拟定预算

以下是实施和边界测试的初始值，尚无实测性能结论；作为可信宿主选项集中定义，不从仓库文件或模型参数放宽，不在第一版扩展用户配置平台。

| 项目 | 初始上限／行为 |
|---|---|
| 单个 AGENTS 原始字节 | 32 KiB |
| 当前有效规则累计原始字节 | 128 KiB |
| 当前有效非空规则文件 | 64 份 |
| 当前有效规则渲染消息估算 token | 8,000，且普通请求仍须通过既有窗口检查 |
| 会话跟踪的目录探测位置 | 2,048 个，包含当前缺失规则的位置 |
| 一次规则发现枚举条目 | 16,384 个；同时受工具已有更窄遍历／结果限制约束 |
| 单工具轮因规则更新被延期 | 最多一次；再次变化明确结束该次尝试，不无限重新采样 |
| 日志／展示 | 不放宽现有 8 MiB 批次、256 MiB 日志及 200 条／8 MiB 展示页边界 |

规则过多时报告涉及的操作范围，要求缩小目录或整理规则；不静默漏掉深层规则，也不驱逐旧规则后宣称整个会话仍具备全部规则。不因缩小加载预算自动扩大工具权限。

## 6. 最小职责划分与拟议接入契约

### 6.1 新增内容控制在一个加载器与必要数据类型

| 拟议内容 | 唯一职责与所有者 |
|---|---|
| `ProjectInstructionLoader` | 给定 Workspace、已跟踪位置和规范化作用域，有界发现与异步读取，返回候选快照／差异；无模型调用、授权、持久化和控制台输出 |
| `ProjectInstructionDocument` | source path、scope directory、原始内容标识、解码正文、加载时间；不是工具计划 |
| `ProjectInstructionSnapshot` | 有序文档、已探测位置、有效规则 revision、独立发现状态标识和渲染版本；描述某个完整有效集合，空集合也有明确版本 |
| `ProjectInstructionState` | 已应用快照、宿主规则消息的 canonical 下标绑定、未提交变更；由现有 ConversationContext 拥有，host／loop 使用同一份状态 |
| `InstructionScope` | 在 ToolPreparation 中声明 File、Directory、RecursiveDirectory（含 maxDepth／遍历限制）等访问范围，不包含模型授权 |
| `InstructionCoverage` | 一次工具轮预检完成的范围与规则版本凭据，仅内存使用；不是可恢复 grant，不序列化旧执行计划 |

加载器不再维护另一份权威“当前规则”。可缓存原始读取结果以减少重复分配，但当前有效集合只能以 `ConversationContext` 中已应用快照为准；不新增全局 singleton、反射注册或任意 provider 平台。

### 6.2 现有执行链上的边界

需要异步 I/O 的是宿主／loop 的边界，不改变 `ITool.Prepare` 的纯准备契约。允许增加一个窄的指令边界依赖，由 host 显式注入现有 Loop；逻辑契约如下，不要求照抄为公开 API：

```text
BeforeModelRequestAsync(token)
  → 检查根及已跟踪位置，暂存完整候选快照，可靠提交并应用后才能发请求

BeforeToolRoundAsync(preparedRound, token)
  → 汇总规范化指令作用域，发现本轮需要的规则
  → Unchanged(coverage) | Updated(snapshot) | Failed(reason)
```

- `ChatSessionHost` 负责初始加载、回合提交、持久化协调及同一时刻一个运行；边界组件不拥有第二个 host 或 writer。
- `AgentLoop` 仍负责工具轮、协议消息、重新采样和终态；边界组件只返回结果，不自行 Execute 或递归调用 Loop。
- 初始根快照可先在创建 Session 前计算，创建后交给同一状态；不为预检和正式运行各建一个 loader 实现。
- `ToolPreparation.InstructionScopes` 来自工具已经解析的参数／私有计划，不由 Loop 再次解析 patch、命令字符串或原始模型 JSON。TargetPaths 的权限意义不变。
- 若须把 coverage 绑定给工具，提供保持原参数／私有执行计划不变的内部不可变派生操作，在审批前得到最终准备对象。recorder、审批和 Execute 持有同一最终对象，避免引用关联和 fingerprint 漂移。
- 指令边界在生产 run/chat 是显式依赖；测试／worker 未启用时也必须保持其各自既有契约，不能把“可选注入”作为生产绕过开关。
- 本切片不要求新增通用插件钩子、事件路由平台或新版 ITool。最终可见性、构造函数和新增字段属于实施前需列明的 API 兼容影响，不能隐含地扩大公开接口。

## 7. 模型消息、压缩与工具轮状态机

### 7.1 一次请求的消息组织

首次请求的逻辑顺序：

```text
system：固定 WorkspaceSystemPrompt
user（宿主项目上下文）：带来源、作用域和规则版本的完整规则集合
user（真实输入）：用户本次发送的内容
```

后续模型视图保留固定 system、当前有效规则、有界原话与交接摘要及未折叠的真实对话／工具组。规则正文不拼入 system，不把加载规则作为模型工具调用，也不提前单独请求模型“确认收到”。空集合可以没有正文，但从非空变为空必须明确表示旧规则失效。

宿主规则上下文采用固定、版本化的渲染模板，包含路径／作用域、版本、内容块和以下边界：项目规则不授予 capability；不能覆盖宿主约束、当前用户明确要求、审批或隔离策略；局部规则只在其目录生效。正文分隔仅为提示结构，不作为可信身份判断或提示注入防护的充分保证。

当前 `WorkspaceSystemPrompt` 字符串保持不变。若未来另需改变 Harness 基础 system 契约，单独说明旧会话兼容，不能借本切片静默改常量后关闭一致性检查。

### 7.2 canonical 历史与当前模型视图

- 原始规则消息一旦提交，与其他 canonical 消息一样不可原地修改；规则来源和 canonical 消息下标由 §8 的结构化记录绑定。
- 模型视图过滤被这些受验证记录标识的旧宿主规则消息，生成且只生成一份当前有效规则块。过滤不改变 canonical 消息、下标、opaque 或历史工具组。
- 用户／工具／模型输出即使包含完全相同的标记，也不属于受管理规则消息；不得过滤或升级其来源。
- 初始规则不是交接摘要或原始任务。真实当前目标仍来自 `SessionTurn.UserInput`；规则附加不增加 Turn / Run 数。当前压缩契约见[上下文压缩设计](context-compaction-design.md)。
- 规则更新使用完整集合替换通知，删除规则使用明确失效通知。兄弟目录依旧保留各自作用域；不能仅追加一段局部规则而忘记其它生效内容。
- 当前规则块在每次模型视图中完整保留并计入 token；同一内容在 canonical 只记录一次版本变更，不能每个请求重复追加一份日志消息。
- 新进入的目录没有局部规则时，只更新“探测过哪些位置”的状态；有效规则 revision 不变，不清理 grant、不延期工具、不额外请求模型。探测集合的持久化与规则生效是同一状态里的两类变更，不能混为“规则内容更新”。

### 7.3 对现有压缩实现的必要适配

复用 `ConversationContext` 的入口和原子组不变量，替换其中不再适用的布局假设：

1. 建立受控来源索引后，`BuildModelView`、`EstimateViewTokens`、`EstimateCandidateViewTokens` 和预算规划使用同一套模型视图规则，不能普通请求注入了规则、预算却漏算。
2. 第一条 assistant 前的消息不因处于头部而永久有效；原始用户文本也使用独立保留预算。旧头部永久保留方法已退出，受管理的旧规则不能重新常驻。
3. 折叠区必须是 canonical 的真实连续区间，覆盖期间的真实用户消息与宿主上下文消息，边界不能落入 tool-call/result 组。必须验证 `AssistantSpans` 和 span 求和不会跨过夹在中间的 user 消息却计算错误终点。
4. 原话只从受验证的真实用户来源选择，排除宿主规则，旧目标包装方法已退出。当前规则另带来源进入摘要请求，计入摘要输入预算；过期规则不要求摘要永久保留其有效性，不把项目规范当用户目标。
5. 无预算的 passthrough 模式仍返回有效的规则投影；“关闭压缩”不应重新激活所有旧规则。未启用项目指令功能时，原直通契约保持。
6. `Restore` / `Reset` / `TruncateToMessageCount` 同步恢复／清理／裁剪来源索引，保留当前有效快照与完整协议边界的一致性；不得留下指向已回退消息的索引。
7. 不增加第二个 Compactor；不扩大到无关的摘要文案、估算算法或整个任务状态重构。

### 7.4 首次遇到局部规则：旧工具轮如何结束

```text
模型返回 assistant + 多个 tool_calls
  → 现有 PrepareRound：整轮纯校验
  → 检查整个工具轮需要的指令作用域
      未变化：绑定 coverage，记录准备、审批，顺序执行
      新增／变化：本轮全部不执行，不询问旧计划的审批
          → 为每个调用回传“因项目规则更新未开始”的真实 tool 结果
          → 记录 KnownNotStarted，保持 InvocationId 区分
          → 形成完整工具原子组
          → 追加新的规则上下文与结构化来源，同一 writer 可靠提交
          → 在原 Run 内重新请求模型；模型重新生成工具调用、准备和审批
      加载失败：整轮不执行，报告失败；不绕过规则继续写入
```

- 不能直接丢弃已经进入 canonical 的 assistant tool_calls，再插入 user 规则消息；每个 call 必须有事实相符的未开始结果，满足 `ChatHistoryInvariants`。
- `RecordNotStartedAsync` 可复用，但应在其既有事实里区分“准备失败”与“项目规则变化导致延期”；不能伪造 PermissionDenied、执行成功、exit code 或 ToolStarted。
- 规则变更重新采样计入现有 MaxAgentSteps；另受 §5.3 防抖上限约束，不能免费无限请求。
- 已经展示的 assistant 过程文本保留为当时输出，后续标注操作未执行；不能改历史文字来假装模型早就知道新规则。

### 7.5 审批等待与顺序执行中的变化

- 请求使用明确快照版本；准备、审批和 dispatch 绑定该版本。审批等待后、每次尚未启动工具前检查已跟踪规则及本轮范围。
- 审批期间发现变化：旧审批不执行、不扩大，尚未开始的调用按上一节结束工具组，再更新规则并重新生成计划。规则变化不清除 NeedsInspection，也不恢复旧 grant。
- 本轮前面的工具修改了 AGENTS：保留其已确认结果；后面的工具先标未开始，结束完整原子组后刷新规则、重新采样。正在执行的补丁不按新规则倒退解释或回滚。
- 若存在未知副作用、持久化失败或进程状态未知，仍按现有中断／poison 路径停止；不能用“规则更新”掩盖这些状态。
- 不将规则版本塞进 `SessionConstraint` 从而让已有拒绝失效。未启动的旧批准失效，授权重建在既有 PermissionEngine 内完成，不新增另一套 permission manager；同一运行的临时 allow grant 在规则变化后清理，显式 deny 保留。

### 7.6 递归工具与 shell 的精确边界

| 工具 | 规则发现范围 |
|---|---|
| read_file | 目标文件所在目录的祖先链；读取 AGENTS 本身仍是普通工具事实，不自动把用户要求读的任意文档升级为规则 |
| apply_patch | 所有目标文件的祖先链，包括新文件和新建 AGENTS 的父链；同一补丁按执行前规则审批，写入后下一边界重新发现 |
| 非递归 list_files | 所列目录的祖先链；只显示子项名字不等于已经进入每个子目录读取正文 |
| 递归 list_files | 按 maxDepth、排除和遍历上限将要进入的目录链；深层规则也必须在操作前发现 |
| search_text | 单文件的祖先链，或有界搜索候选文件所在目录链；根目录一次规则检查不能替代这些局部检查 |
| shell | 规范化 cwd 的祖先链；另覆盖已明确识别且位于工作区内的 executable 所在目录。不能声称推导出了任意脚本内访问的全部目录 |

递归预检与实际遍历必须共享候选范围和排除规则：可以提取 DirectoryWalker 的有界候选枚举供同一次调用复用，但不新建另一个 walker。候选清单作为运行期 coverage 保存，不作为可恢复执行计划。执行不得自行扩大到未覆盖的新目录；发现新目录时停止／报告未覆盖，进入下个规则边界，不先读完正文再补规则。

对递归只读操作，scope guard 拒绝尚未覆盖的范围可作为真实未完成结果返回；不能已有部分只读工作却声称整个调用完全未开始。对写操作，只要存在这种不确定性，启动前拒绝；已开始后仍按实际 patch facts／UnknownResult 处理。本切片不声称任何 shell 具有“所有文件访问前都加载局部规则”的保证。

## 8. 统一日志、来源与版本演进

### 8.1 只扩展现有日志链

拟增加必需记录族 `project-instructions`，payload 显式携带 `schema: tinyharness.project-instructions/1`。外层沿用现有 JSONL batch、提交序号、record ID、排他锁、flush、poison 和原有资源限制，不另建 `.instructions.json`、独立审计或第二个 writer。

每次生效版本至少保存：

| 字段 | 契约 |
|---|---|
| schema / action / revision / previousRevision | 记录版本、rules-applied 或 scopes-tracked、确定性内容身份及前一有效版本；首个版本明确无前项 |
| turnId / runId / reason | 本次生效的来源；initial、scope-discovered、changed、removed、resume-refresh 等显式原因 |
| documents | rules-applied 的完整有序规则集合：规范化 source/scope、原字节 hash、字节数、hasUtf8Bom、解码正文、loadedUtc；空集合显式保存 |
| probedDirectories / discoveryFingerprint | 已跟踪的有效／缺失规则位置及其独立标识，恢复后重新探测；不能把历史路径直接作为当前可读授权 |
| rendererVersion / renderedHash | 固定渲染版本及其结果完整性校验；时间戳不参与内容 revision |
| canonicalMessageIndex | rules-applied 对应宿主 user 上下文的 canonical 零基下标，必须与渲染结果匹配；scopes-tracked 引用已有 revision，不创建消息绑定 |

revision 由稳定序列化的有效文档内容标识、作用域和 rendererVersion 生成，不含探测集合、loadedUtc 或检查时间；发现位置独立计算 discoveryFingerprint。hasUtf8Bom 与严格解码正文使恢复端可以重建 UTF-8 字节并核对原字节 hash，而不是把正文 hash 冒充原文件 hash。一次加载拿到的是 coherent candidate；全部成功、预算通过才整体替换，不半更新。

同一 schema 的 rules-applied 保存完整规则和对应消息绑定；scopes-tracked 仅保存新的完整探测集合并引用当前有效 revision，不重复保存正文、不创建 user 消息。后者要求 revision 等于前一有效 revision；前者必须是初始版本或真实规则内容／作用域变化。规则为空的首次 rules-applied 仍写入明确的空规则上下文，建立可验证的初始来源。两类 action 都由同一状态、同一 writer、同一 scanner 校验，不新增独立存储路径。

rules-applied 与对应 canonical message 在同一 CommitCore 批次提交；第一批中还包括 identity、真实 turn/run 和用户输入。scopes-tracked 随同一 writer 的下一次请求前提交或安全执行意图提交保存，不冒充规则生效通知。source 元数据与正文不能只写入 audit 或展示条目后就宣称可恢复。将同一份正文用于 canonical、来源验证和脱敏展示是有目的的投影，不是三份独立维护的权威状态。

### 8.2 必须一起修改的现有接点

1. `ConversationContext` 保存受管理规则索引及有效快照；提供给 writer 同一份数据，不让 recorder 根据消息正文重新识别来源。
2. `SessionStoreWriter.CommitCoreAsync` 在 gate 内收集新增规则记录和消息，只有 flush 确认后推进规则／消息游标；OpenExisting 从同一 projection 续种游标。
3. `SessionLogJsonContext` 显式注册 DTO；`SessionLogScanner.ValidatePayload` 校验 schema、边界和字段；ProjectionBuilder 校验 revision 链、turn/run、路径作用域、消息下标和 hash。
4. `PersistedConversation` 的内存恢复投影携带规则来源索引；`SessionResume` 继续只做内存校验，host 才重新读磁盘。若来源指向半个工具组、被回退的消息或与 canonical 不一致，拒绝／回退到可证明的有效状态，不能凭正文补造来源。
5. `ISessionStoreWriter.CommitAsync` 仍是权威提交入口，不添加另一个“新版保存全部会话”方法。`AppendRecordsAsync` 的 audit/history 校验不能被改成接受任意 kind 绕开结构化来源约束。
6. 初始规则与用户消息可靠保存后才发第一次模型请求；运行中规则变更亦须先保存再重新请求。没有生产 writer 的离线单元测试可保留纯内存路径，但不能冒充持久化验收。

### 8.3 与现有格式的兼容边界

这是**显式版本化的必需记录扩展提案**，不是声称现有 `session-log/1` reader 已经支持：

- 新程序读取已有、尚无项目规则记录的统一日志时，恢复原有 canonical／opaque／检查状态，规则状态为“尚未管理”；下一回合注入新鲜完整规则。不能根据旧消息中的标记将其重新归类。
- 从第一次可靠提交 `project-instructions` 起，该日志需要支持此 schema 的程序。当前旧二进制会按已有未知必需 kind 策略拒绝；这是预期的明确降级边界，不能改为忽略新记录后继续执行。
- 外层格式没有改变批次／顺序语义，所以提议保留 envelope `/1`，以新 kind 和 payload schema 表示新增必需语义；此选择须在实施前确认并同步持久化设计的格式表。若发现 envelope 也必须改变，先修订此选择，不暗改或同时维护两套 writer。
- 不恢复已移除的三版快照读取器、迁移子命令或旧 snapshot/audit 双写。这里的“已有日志”仅指当前统一 JSONL。
- renderer/schema 未来未知版本拒绝无损续聊，不能用最新模板重算旧消息并宣称原样一致。原始 canonical 内容和 opaque 校验保持原契约。

### 8.4 失败及恢复

加载失败不发布“已生效”。候选快照和待提交消息先暂存；writer 必须能在同一批次读取这份候选，但只有确认提交后才发布新有效 revision 和展示事件。提交失败时不发送使用新规则的请求、不启动工具，保留原有效状态和待提交诊断；不确定写入沿用 writer poison，不自动回滚磁盘或重试。不得为了暂存／提交再引入一套完整 ConversationContext 或另一种 writer。

进程在规则批次完整提交后退出，恢复可以重建该版本；重启后仍重新检查磁盘。进程在规则批次半行时退出，strict 恢复继续拒绝损坏尾部，不为抢救规则而自动截断。没有副作用时不凭规则更新制造 NeedsInspection；已有未知副作用也不能因规则加载成功而解除。

## 9. 续聊刷新、用户展示与权限

### 9.1 恢复顺序

```text
现有 OpenExisting：排他打开并严格读取统一日志
  → 现有 SessionResume：身份／system／opaque／协议组／折叠边界校验
  → 恢复受验证的规则状态，不运行旧计划
  → 保留／显示已有 NeedsInspection，确认仍走原入口
  → 用户提交新回合时，验证历史规则路径仍在当前工作区
  → 重新探测根及已跟踪位置，区分新增／更新／删除
  → 在新回合使用当前规则，可靠记录变化后才发模型请求
```

刚打开会话但用户尚未发送时，不制造虚假用户回合。可以显示“上次加载的规则”；不能在未刷新时显示“当前磁盘规则已验证”。规则刷新失败可以继续离线查看历史，但不以旧规则静默执行新任务。

### 9.2 展示契约

- 初次显示已加载文件数、来源／作用域和有效版本；后续只提示变化或失败，不每个 token／每次工具刷屏。
- 继续使用 IHostEventSink，拟增加 `ProjectInstructionsChanged` 事件及明确 DTO，包含 revision、变更摘要和脱敏来源列表。通知不携带授权入口，不依赖消费者确认才能改变权限。
- 已可靠提交的规则展示条目进入现有 `SessionHistoryItem`／HistoryItemCompleted／分页链路，拟新增 `project-instructions` history kind；需同步 `ProjectionBuilder.ApplyHistory` 的封闭分类。不能只添加 CLI 日志而让历史完全丢失规则变化。
- 规则原文用于模型／恢复，检查默认仅显示脱敏元数据与变更摘要；有需要的正文查看必须复用脱敏投影，不打印 canonical 原文。显示错误仍是 DisplayWarning，不能改写规则生效或工具确认事实。
- 规则正文可能含秘密，沿用现有“canonical 原样、检查与终端脱敏、受保护目录不等于加密”的契约，不主动读取凭据作为规则内容。

### 9.3 权限关系

加载是宿主为工作区执行的受限读取，不是模型工具调用，也不每次询问“是否读取 AGENTS”。这不授予 shell、写文件、Git 或联网权限。规则中的“自动提交／发布／读取密钥”等文字都不能改变 PermissionEngine、沙箱设置或用户授权。

M14.1 不提供讨论／实施的语义判断。后续 M14.2 通过基础系统提示词规范自然对话行为，不引入 Analyze / Implement 强制模式；既有权限与指令检查链路继续有效。

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

## 12. 验收矩阵与验证要求

默认 fake model、隔离临时工作区／存储和合成秘密。以下均为待实施的验收要求，本轮未执行。

| 类别 | 必须覆盖的场景 | 核查证据 |
|---|---|---|
| 会话创建 | 打开、空行、EOF、首次发送前取消；首次发送成功；首次模型失败 | factory 次数、Session ID、临时目录文件、真实 Turn/Run 数 |
| 初始规则 | 根文件存在／缺失／空白／UTF-8 BOM；根加载失败 | 模型请求次数及精确消息顺序；失败时无 HTTP／工具／新会话日志 |
| 重复与目标 | 多轮不变规则、进入无局部规则的新目录、用户输入包含规则标记、真实用户改变目标 | revision 不因探测集合变化而改变；无多余模型请求／规则消息；UserInput 和 user history 不被污染 |
| 作用域 | 根／多级／兄弟目录；多文件 patch；新建目标目录 | 规则顺序、scope、无重复正文、无兄弟规则覆盖 |
| 递归 | 根 search 命中有局部规则的深目录；recursive list 与 maxDepth；排除目录 | 调用前加载正确规则，预检与执行候选一致；越界／超限拒绝可解释 |
| 目录变化 | 新增 AGENTS、删除文件、删除目录、新目录在预检后出现 | 缺失探测刷新、removal、coverage 拒绝；不在未覆盖范围先读正文 |
| 链接与权限 | 规则 leaf link、祖先 junction、工作区外链、不可读文件、大小写差异 | fail closed；不读工作区外正文；失败原因与 NotFound 区分 |
| 工具延期 | 同一 assistant 轮多个调用，其中一个首次发现规则 | 全轮零执行、无旧审批、每 call KnownNotStarted、工具组完整、InvocationId 独立 |
| 审批变化 | 等待审批时改规则，原允许规则／单次批准存在 | dispatch 前阻止旧计划；重新采样／准备／审批，deny 和 NeedsInspection 不失效 |
| 自修改 | patch 修改／创建 AGENTS，轮内仍有后续 patch 或 shell | 保留已确认 patch；后续未开始；新规则在下一次执行前生效 |
| 并发与取消 | 两次同时提交；加载／枚举／提交／审批时取消 | 单活动运行、无第二输入循环、可解释终态、没有无限重试 |
| 压缩 | 多版本规则位于头部／中间／折叠区；规则消息夹在用户与工具轮之间 | canonical 不变、折叠真实连续、工具组完整、原始目标正确、当前规则原文保留 |
| token | 规则渲染、工具 schema、摘要规则输入、usage 校准、无预算模式 | 发送视图与所有预算口径一致，超窗口拒绝请求，不静默裁剪规则 |
| 日志与兼容 | 无规则记录的当前日志、新 schema、未知 schema/kind、错 revision／scope／hash／消息下标 | 当前 JSONL 唯一 writer；旧程序明确拒绝新必需 kind；新程序严格校验来源 |
| 故障恢复 | 初始写失败、规则更新写失败、半行、flush 后确认丢失、未知副作用 | 不调用／不继续、poison、不自动截断、不重放工具、不错误解除阻断 |
| 历史与脱敏 | 规则正文含合成秘密、通知消费者失败、规则变更跨分页 | live/history 绑定同一提交事实，脱敏一致、游标稳定、DisplayWarning 不覆盖事实 |
| 入口回归 | run/chat/smoke 一致，session list/show 离线，worker focus 与任务包不变 | 请求捕获、工具次数、文件／环境访问证据 |
| NativeAOT | 新 DTO、JSON、入口及事件路径 | win-x64 publish 无未解释 warning；仓库外原生产物离线 smoke 验证加载／更新／恢复 |

建议扩展现有 ChatSessionConsoleTests、ChatSessionTests、ChatSessionPersistenceTests、AgentLoopTests、ConversationContextTests、ContextCompactionTests、SessionStoreTests、SessionResumeTests、SessionHistoryTests、SessionJournalFaultTests 和相关工具测试；新增文件仅承载独立 loader／作用域场景，不复制旧测试套件。

实施后运行修改过的测试，再按公共上下文／恢复边界影响扩大到普通离线回归；scale 单独运行，保持原预算并额外记录规则开销。执行仓库既有 NativeAOT publish 与仓库外 native smoke，不以 JIT 单测替代。真实模型、真实 OS 隔离和 GUI 验收需另行授权，本设计不借默认测试触发。

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
