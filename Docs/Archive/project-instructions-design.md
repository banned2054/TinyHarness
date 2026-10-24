# 项目指令加载与会话首次发送设计（M14.1）

> 已完成实现的文档归档（2026-10-24）。保留设计、使用边界和相关证据供维护时查阅；归档不撤销仍有效的协议、安全、权限和 NativeAOT 约束，也不代表对后续修改的授权。当前开发范围以[活动计划](../../PLAN.md)和实际源码为准。

文档整理：2026-10-24；实施记录日期为 2026-10-18。

## 1. 状态、范围与阅读顺序

**状态：已实施，离线本地验收通过；链接创建受限场景明确未验证。** 首次发送创建、根／局部／递归规则、工具延期、上下文投影、统一日志及续聊刷新已落盘。实施时的 API、测试、产物和限制见[归档 §14](project-instructions-history.md#14-实施结果与本地验证)；本文不授权后续 Git、真实模型或外部操作。

- 源码核查基线：TinyHarness `10a697ab26967cdf2d4a1196e7f88de892f8738b`，核查时工作区干净。
- 本地参考：`C:\Code\Rust\codex`，HEAD `1343180d6c83a7d58e20dc4394678570aabde79a`；结论来自该 checkout 的源码阅读，不代表在线最新版或运行验证。
- 本文细化[归档阶段计划 §8.1](../../PAST_PLAN.md#81-仓库指令加载)，原阶段计划已收口。已有日志、协议和中断契约继续以[会话持久化设计](session-persistence-design.md)为准；[AGENTS.md](../../AGENTS.md)规定工作授权，新任务范围以用户当前要求及对应设计为准。
- 本阶段只实现项目指令能力。用户后续确认 M14.2 聚焦[基础系统提示词](system-prompt-design.md)，不引入 Analyze / Implement 切换；任务中补充输入属于 M15，Git 差异与完整交付报告属于 M16，均不借本设计提前实现。

本文保留行为、来源、工具边界、恢复和回归要求，沿用原章节编号以便引用。实施前调查、旧施工清单和实际接入结果已迁至[历史记录](project-instructions-history.md)；历史拟议名称不作为当前 API 清单，维护时仍须核对源码。

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

### 5.3 规则加载预算

以下预算作为可信宿主选项集中定义，历史测量见[实施记录](project-instructions-history.md#14-实施结果与本地验证)，不从仓库文件或模型参数放宽，不在第一版扩展用户配置平台。

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

## 6. 职责划分与接入契约

### 6.1 新增内容控制在一个加载器与必要数据类型

| 组件 | 唯一职责与所有者 |
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
- 保持显式接入，不新增通用插件钩子、事件路由平台或另一套 ITool。实施时的类型与构造函数调整见[历史记录](project-instructions-history.md#141-复用扩展与替换)；后续公开接口变更仍需明确评估兼容影响。

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

项目规则独立于固定 system 模板。当前模板与续聊一致性要求见[系统提示词设计](system-prompt-design.md)，不得为注入项目规则关闭一致性检查。

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

使用必需记录族 `project-instructions`，payload 显式携带 `schema: tinyharness.project-instructions/1`。外层沿用现有 JSONL batch、提交序号、record ID、排他锁、flush、poison 和原有资源限制，不另建 `.instructions.json`、独立审计或第二个 writer。

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

rules-applied 与对应 canonical message 在同一 CommitCore 批次提交；第一批中还包括 identity、真实 turn/run 和用户输入。scopes-tracked 随同一 writer 的下一次请求前提交或安全执行意图提交保存，不冒充规则生效通知。source 元数据与正文不能只写入 audit 或展示条目后就宣称可恢复。将同一份正文用于 canonical、来源验证和检查展示是有目的的投影，不是三份独立维护的权威状态。

### 8.2 必须一起修改的现有接点

1. `ConversationContext` 保存受管理规则索引及有效快照；提供给 writer 同一份数据，不让 recorder 根据消息正文重新识别来源。
2. `SessionStoreWriter.CommitCoreAsync` 在 gate 内收集新增规则记录和消息，只有 flush 确认后推进规则／消息游标；OpenExisting 从同一 projection 续种游标。
3. `SessionLogJsonContext` 显式注册 DTO；`SessionLogScanner.ValidatePayload` 校验 schema、边界和字段；ProjectionBuilder 校验 revision 链、turn/run、路径作用域、消息下标和 hash。
4. `PersistedConversation` 的内存恢复投影携带规则来源索引；`SessionResume` 继续只做内存校验，host 才重新读磁盘。若来源指向半个工具组、被回退的消息或与 canonical 不一致，拒绝／回退到可证明的有效状态，不能凭正文补造来源。
5. `ISessionStoreWriter.CommitAsync` 仍是权威提交入口，不添加另一个“新版保存全部会话”方法。`AppendRecordsAsync` 的 audit/history 校验不能被改成接受任意 kind 绕开结构化来源约束。
6. 初始规则与用户消息可靠保存后才发第一次模型请求；运行中规则变更亦须先保存再重新请求。没有生产 writer 的离线单元测试可保留纯内存路径，但不能冒充持久化验收。

### 8.3 与现有格式的兼容边界

这是已实施的必需记录扩展；外层仍为 `tinyharness.session-log/1`，新增语义由 kind 与 payload schema 表达：

- 新程序读取已有、尚无项目规则记录的统一日志时，恢复原有 canonical／opaque／检查状态，规则状态为“尚未管理”；下一回合注入新鲜完整规则。不能根据旧消息中的标记将其重新归类。
- 从第一次可靠提交 `project-instructions` 起，该日志需要支持此 schema 的程序。当前旧二进制会按已有未知必需 kind 策略拒绝；这是预期的明确降级边界，不能改为忽略新记录后继续执行。
- 外层格式没有改变批次／顺序语义，所以保留 envelope `/1`，以新 kind 和 payload schema 表示新增必需语义，格式表见[持久化设计](session-persistence-design.md)。后续若需改变 envelope，应先修订兼容策略，不暗改或同时维护两套 writer。
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
- 继续使用 IHostEventSink，`ProjectInstructionsChanged` 事件及明确 DTO 包含 revision、变更摘要和脱敏来源列表。通知不携带授权入口，不依赖消费者确认才能改变权限。
- 已可靠提交的规则展示条目以 `project-instructions` history kind 进入现有 `SessionHistoryItem`／HistoryItemCompleted／分页链路；`ProjectionBuilder.ApplyHistory` 的封闭分类同步校验。不能只添加 CLI 日志而让历史完全丢失规则变化。
- 规则原文用于模型／恢复，检查默认仅显示脱敏元数据与变更摘要；有需要的正文查看必须复用脱敏投影，不打印 canonical 原文。显示错误仍是 DisplayWarning，不能改写规则生效或工具确认事实。
- 规则正文可能含秘密，沿用现有“canonical 原样、检查与终端脱敏、受保护目录不等于加密”的契约，不主动读取凭据作为规则内容。

### 9.3 权限关系

加载是宿主为工作区执行的受限读取，不是模型工具调用，也不每次询问“是否读取 AGENTS”。这不授予 shell、写文件、Git 或联网权限。规则中的“自动提交／发布／读取密钥”等文字都不能改变 PermissionEngine、沙箱设置或用户授权。

M14.1 不提供讨论／实施的语义判断。后续 M14.2 通过基础系统提示词规范自然对话行为，不引入 Analyze / Implement 强制模式；既有权限与指令检查链路继续有效。

## 12. 验收矩阵与验证要求

默认使用 fake model、隔离临时工作区／存储和合成秘密。下表保留维护时的回归要求；历史结果及未验证项见[实施验收归档](project-instructions-history.md#14-实施结果与本地验证)，不表示本轮重新执行。

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
