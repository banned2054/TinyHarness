# 会话持久化设计

更新日期：2026-10-21。§7 保留统一日志切换时的历史验收；本次压缩扩展见 §4.1、§6.1 及[压缩设计](context-compaction-design.md)。

## 1. 状态与适用范围

**状态：统一 `tinyharness.session-log/1` 已实施并完成本地统一验收，旧格式读取与迁移已按用户确认移除。** `run` 与新会话 `chat` 共用日志，旧三版 writer 已退出；仅保留统一日志格式，旧快照和独立审计文件不再读取或迁移。本轮证据见 §7，真实模型、GUI、断电和实际 OS 隔离未验证，不推进 M14–M17。

- 目标是可逐步扩展的会话内核：先满足内容完整、续聊正确和中断安全，再根据测量增加读取加速和存储规模能力。
- 设计形成时的源码参考基线：TinyHarness `45fae0b4a70e13c56846e4be1dab9bde5c9e0aa1`；本地参考 Codex `1343180d6c83a7d58e20dc4394678570aabde79a`。这些定位是历史依据，当前行为以磁盘实现和最终验收为准。
- 本文负责持久化契约、选择、假设与验收；原阶段范围与实施顺序见[历史计划](../PAST_PLAN.md#m12-m17)，当前停点见[开发接力](current-progress.md)。[AGENTS.md](../AGENTS.md) 负责工作规则，具体工作范围和授权以用户当前要求为准。
- [原讨论记录](2026-10-16-session-persistence-codex-discussion.md) 保留为历史材料，包含早期措辞与后续更正。当前持久化目标以本文及当前计划为准，不能把旧对话当作实施指令。

## 2. 用户体验与不可退化的契约

### 2.1 已完成历史的展示

- 保留 Session / Turn / Run 的身份及关系、用户输入、可见过程文本、最终回答、工具调用和结果的稳定顺序。一次回合可包含多次模型请求；运行尝试不与回合身份混用。
- 过程文本和最终回答有明确归属：已完成 assistant 消息带工具调用时记为 process，不带工具调用时记为 final，不让恢复端根据自然语言猜测；final 分类不等于运行成功。这里的过程文本指用户可见的 assistant 正文，不要求展示内部 reasoning 或 opaque 内容。
- 工具条目保存调用身份、展示所需的参数与结果、准备与权限事实、执行状态、退出码、超时和裁剪标记；未知字段保持未知，不从工具文本中猜出成功。
- Harness 补丁条目保留足以展示当时具体修改的路径、操作与 diff/hunk，并区分拟议修改、已确认执行和未知结果。模型提交的补丁参数只能证明意图，不能证明写入成功；多文件中途失败也不能显示全部成功。预校验失败不产生变化事实；逐文件 flush 与 dispose 均成功后才标 confirmed，已开始写入尝试但失败则标 unknown，后续未开始文件不报成功。
- 后来工作区文件被修改、删除，仍能读取当时已保存的改动。通用 shell 的任意文件改动不自动宣称已被完整捕获；实际差异与归因仍由 M16 的证据能力补充。
- 实时完成条目和历史读取条目遵守同一份展示契约；采用相同脱敏策略时，其内容、身份、顺序与状态一致。流式动画、滚动位置和未保存的瞬态增量不属于逐字重现承诺。
- 工具卡片默认折叠由界面决定；记住用户展开状态不是首版条件。模型原文不直接作为检查输出；同一条目的脱敏和裁剪边界在实时与历史路径中均明确显示。

### 2.2 续聊与中断

- 界面历史与模型上下文分别消费同一存储中的适用记录。模型上下文可以压缩，压缩不删除已保存的历史展示内容。
- 恢复只读取并组合数据，不调用模型重新生成旧回答，不重新执行旧工具，也不自动恢复 prepared plan 或授权。
- canonical 协议字段、工具原子组、交接文本／原话来源／折叠边界及 opaque 载荷保持当前保真契约；身份、协议、系统指令与完整性校验仍有效。
- 副作用前必须可靠确认执行意图记录已提交；确认失败不启动工具。结果未可靠记录时保守视为可能执行，保留 NeedsInspection，用户检查确认后才允许继续副作用。
- 用户确认、阻断建立与解除也是持久事实；新的未知执行使旧确认失效。普通失败、有确认终态与状态未知必须区分。

### 2.3 长历史与资源

- 历史读取提供有界批次、稳定身份、确定顺序与不遗漏、不重复的翻页语义；同时追加新记录不能使旧历史分页漂移。
- 首版允许顺序扫描定位，返回量和内存须有界；不把有界返回冒充恒定读取成本。模型恢复允许按需扫描，记录真实耗时、分配与文件读取量。
- 后续 GUI 应能自动加载旧内容、保持阅读锚点并展开完整工具和 diff；底层分页不要求界面使用“展示更多”按钮。GUI 平滑滚动属于后续真实界面验收，本次只验证支持它的数据契约。
- 记录、批次、检查点、artifact 与累计资源均有明确预算。已实施的单个 artifact 读取上限为 2 MiB，stdout/stderr 合计 4 MiB；日志批次上限 8 MiB，日志总量上限 256 MiB。超过预算或内容未完整保存时必须报告，不得静默省略后仍宣称完整恢复。

## 3. 当前问题与替换边界

实施前的静态定位如下，用于保留替换原因和检查范围，不表示这些旧路径仍是当前权威写入者：

| 位置 | 当前职责或缺口 |
|---|---|
| [Program.cs](../TinyHarness.Cli/Program.cs)，`RunChatAsync` | 实施前同时组装 FileRunRecorder 与 SessionStoreWriter |
| [RunPersistence.cs](../TinyHarness.Core/Services/Persistence/RunPersistence.cs)，`CompleteAsync` | 实施前在运行审计之外，按 run 保存完整脱敏消息与状态快照 |
| [SessionStore.cs](../TinyHarness.Core/Services/Persistence/SessionStore.cs)，`CommitAsync` | 实施前 v3 完整序列化、同目录临时写入与替换，提交审计对账；快照上限 16 MiB |
| [ChatSessionHost.cs](../TinyHarness.Core/Services/Session/ChatSessionHost.cs) | 执行前检查点、回合结算和 NeedsInspection；已接入统一存储，安全语义不得删除 |
| [SessionResume.cs](../TinyHarness.Core/Services/Session/SessionResume.cs) | 身份、opaque 与协议边界校验，可复用不变量，数据读取与重建需适配 |
| [HostEvent.cs](../TinyHarness.Core/Models/Session/HostEvent.cs) 与 [ChatMessage.cs](../TinyHarness.Core/Models/ChatCompletions/ChatMessage.cs) | 内存事件不是持久 DTO；实施前缺少显式的过程／最终展示分类 |
| [ApplyPatchTool.cs](../TinyHarness.Core/Services/Tools/ApplyPatchTool.cs) 与 [ToolPreparation.cs](../TinyHarness.Core/Models/Tools/ToolPreparation.cs) | 实施前结果主要是文件名摘要；现已补持久化的 PatchChanges 事实契约 |

保留 Agent Loop、Session / Turn / Run、权限、沙箱、模型适配、上下文预算和现有工具语义。替换范围为持久化写入、读取、恢复及必要的展示／执行事实承载，相关入口随切换调整；不重做整套执行引擎。

本次实施已退出两条全量快照并行写入。结构化执行事实不得丢失，也不要求所有审计都塞进一个 JSON 对象。由于旧格式从未实际使用，旧读取器与显式迁移也已移除。

首版不实现 GUI、SQLite、历史分叉、后台归档、分段压缩、外部 MCP 或新供应商支持；不引入完整 event-sourcing 框架或插件式存储平台。

## 4. 最小设计

以下数据职责和行为是当前实现的权威契约；后续兼容扩展必须显式版本化，不改变既有事实语义。

### 4.1 权威记录与投影

- 新会话和单次 `run` 使用 `tinyharness.session-log/1` 追加式 JSONL 主日志，显式 source-generated JSON DTO，不序列化现有 `HostEvent.Payload` 的任意 object，也不把每个流式 token 作为必存事件。
- 记录具有格式标识、种类、稳定 record ID、会话内单调序号、时间、Session / Turn / Run 关联及需要时的 message / tool-call / item ID。时间不是唯一排序依据，运行内事件序号也不能替代持久顺序。
- 主日志中的协议记录负责模型续聊，结构化执行事实负责执行状态与审计，脱敏展示条目负责用户历史。允许同一内容为协议和展示目的有受控重叠，但同一执行状态由同一个事实决定，不能各自维护互相竞争的状态。
- 最小记录族包括会话身份与上下文、用户回合与运行生命周期、协议消息、展示条目、工具准备／权限／执行意图／确认结果、usage、压缩检查点、检查阻断与用户确认。不是对未来所有 Codex 事件的预建映射。历史 DTO 确定性记录 process／final 分类；`apply_patch` 结果保留每个目标文件的路径、create／modify、完整 hunk diff 与 confirmed／unknown 状态，不把模型原始 patch 全体标为成功。
- 完成展示条目保存后可以独立读取；实时端消费同一条目契约。增量显示允许先于落盘，但必须显示保存失败；不能把用户已看到终态当作持久化成功。
- 当前 diff 与工具输出内联保存在历史条目中；从临时 artifact 读取内容的边界为单个 2 MiB、stdout/stderr 合计 4 MiB，并在保存前脱敏。artifact 缺失、不可读或超限时保留可用输出并明确 outputIncomplete／warning，不冒充完整归档，也不把临时路径作为长期可读引用。
- 可执行批准、凭据值和旧计划对象不落盘；审批与策略身份只作为历史事实。协议内容可能包含秘密，不加密的事实不能因换格式而消失。

M14.1 在相同 envelope 中扩展必需记录族，详细来源契约见[项目指令设计 §8](project-instructions-design.md#8-统一日志来源与版本演进)：

| 记录族 | payload schema | 兼容边界 |
|---|---|---|
| `project-instructions` | `tinyharness.project-instructions/1` | `rules-applied` 绑定同批 canonical 宿主消息和完整规则；`scopes-tracked` 仅更新完整探测集合，引用当前 revision，不重复正文 |
| `compaction-checkpoint` | `state.schema = tinyharness.compaction/1` | 保存有界交接、真实用户原话的 canonical 下标／内容及折叠边界；替换旧 `checkpoint`，不做隐式迁移 |

外层仍为 `tinyharness.session-log/1`，批次、顺序、锁与资源上限保持原契约。没有项目规则记录的统一日志仍可读取，规则状态为尚未管理；下一次真实发送才加载当前规则。首次写入此必需记录后，旧二进制按未知必需 kind 明确拒绝；新 reader 拒绝未知 payload schema、renderer 或损坏来源绑定，不忽略规则后继续执行。此扩展不重新引入旧快照读取、迁移或独立规则文件。

### 4.2 写入边界与故障

- 复用受保护用户级目录、词法与最终路径／链接检查、沙箱写保护及跨进程排他；一个会话只有一个串行写入者。
- 副作用启动前须确认 execution intent 已提交，恢复时也拒绝缺少对应 intent 的已执行副作用事实；调用以独立 InvocationId 关联，provider rawCallId 只保留协议身份，允许跨 assistant 轮复用，不能混淆不同执行。
- 新记录或一组相互依赖的记录只有在可验证提交边界内才视为提交。工具意图的提交先于启动，结果事实的提交先于把它视为可靠保存；当前以带格式标识、会话身份、单调序号、批次 ID 和记录列表的一行 JSONL 批次提交，写入换行并完成 flush 后确认；本轮故障验证证据见第 7 节。
- 写入异常、取消或确认丢失后，不盲目重试追加或继续写在可能损坏的尾部；写入链路进入 poison，明确已确认前缀与未知范围。重复 ID、序号冲突与重复确认均须检测，不能简单跳过。
- 尾部半条记录按 strict 边界拒绝，中间损坏单独处理。可检查地读取有效前缀，不代表可无条件续聊；缺失提交、断序、关键未知记录或副作用确认缺口不得静默忽略。
- 尾部恢复的条件、排他锁要求、是否保留损坏原件以及何时允许继续追加须先经故障验证，不默认自动截断或覆盖。未知主版本或必需记录种类拒绝续聊。
- 明确验证的是进程退出、写入异常还是设备故障。普通 flush、追加写或原子替换都不能单独证明断电持久性或恰好一次执行；不在无证据时升级保证。

### 4.3 两种读取

- 历史读取返回脱敏条目及序号游标；游标绑定会话和稳定记录序号，不暴露可被消费者依赖的物理 byte offset。scanner 打开时捕获文件长度，首屏确定 UpperSequence，后续页绑定该序号上界，使读取期间新增消息不影响旧页。每页最多返回 200 条 items，其序列化 UTF-8 累计预算为 8 MiB；scanner 实际可能扫描整个捕获范围，本次 scale 约读取 24 MB。8 MiB 不限制每页总 I/O 或分配量。
- 模型读取从有效压缩检查点与后续协议记录恢复。检查点保存交接文本、保留原话的下标／内容、适用位置和完整工具边界；结合 canonical 消息与规则来源重建同一模型视图，不在恢复时重新摘要或重新选择原话。
- 检查点之后的阻断、结果和确认事实仍须处理，不能因模型上下文只需近期内容而漏掉早期未解除的未知副作用。首版可顺序扫描执行事实，后续再增加可校验的恢复加速信息。
- 不完整工具组仅从模型视图回退，历史条目仍保留中断事实。历史查看不调用模型、不解析真实 API key、不启动工具；续聊则重新加载可信配置并重新 Prepare / Authorize 新调用。
- 压缩检查点可以重复保存必要上下文，这是有明确恢复用途的重复；不能每次普通追加都重新复制全历史。

## 5. 关键选择与验证边界

本轮已按第 7 节完成本地验收。下表保留设计选择、证据要求和后续变更的回归边界，不表示当前实现仍待实施；后续若出现偏差，先区分实现问题与设计问题，不自动换架构。

| 选择／假设 | 所需证据 | 推翻或暂停条件 | 处理 |
|---|---|---|---|
| 追加主日志适合日常长会话 | 固定新增负载下，不同历史规模的写入字节、耗时与分配 | 普通追加仍扫描或复制全历史 | 查写入链路；设计原因则先修改方案 |
| 结构化记录足以保留用户历史 | 实时完成条目与重启读取逐字段对照，含工具和具体 diff | 恢复丢内容、顺序或只能依赖当前文件 | 补齐事实来源；未通过前不切换 |
| 检查点可恢复有效模型上下文 | canonical 字段、opaque、状态及模型视图往返对照 | 压缩改变协议语义或丢事实 | 修复检查点语义与边界 |
| 串行追加能满足执行前门禁 | 写入前后与工具启动边界的故障注入 | 写入未确认仍启动，或未知结果恢复成成功 | 阻止切换，修复执行链路 |
| 顺序扫描足以支撑首版规模 | 分批读取和恢复的耗时、读取量与累计分配 | 超过明确的目标预算 | 先评估派生索引；不为加速更改事实语义 |
| 替换后只有一条新会话权威写入链路 | 调用链、文件产物及回归检查 | 仍逐轮生成旧全量快照，或丢失旧审计字段 | 后续出现重复权威写入时拒绝该变更验收 |

测量使用确定性合成会话，至少覆盖短、中、长三个规模；记录记录数、字节数、负载形态、硬件、命令与结果。实现约束为批次 8 MiB、日志 256 MiB、历史页 200 条且 8 MiB；不能为通过而调整口径。检查点开销与普通追加分别统计；最终本机测量见 §7.2；页返回预算不限制扫描的总 I/O。

## 6. 兼容与演进

### 6.1 当前格式边界

- 当前仅支持 `tinyharness.session-log/1`。旧持久化没有实际使用和数据保留需求，因此移除旧 M7 run 快照、v1/v2/v3 快照的读取器、独立提交审计对账、`session migrate` 命令及迁移专属类型与测试。
- 2026-10-21 压缩替换沿用同一 writer 和 envelope，使用必需 kind `compaction-checkpoint` 及 `tinyharness.compaction/1` 状态。旧七字段 `checkpoint` 明确拒绝，既有文件不删除或改写，也不保留旧读取器；尚无旧检查点的统一日志继续可读。旧程序会拒绝新的必需 kind，不能静默当作未压缩。新 schema／原话来源／预算／工具边界仍严格校验。
- `session list/show` 只消费受保护目录中的 `.session.jsonl` 日志。旧 `.session.json` 与独立 audit 文件被忽略，不扫描旧运行输出目录，不自动转换、删除或覆盖旧文件。
- 日志自身保留格式标识，未知格式、必需记录、重复、截断、poison、资源限额与身份校验继续有效。恢复投影不再带旧快照的 FormatVersion 和 OpaqueRedacted 标记。
- `run` 与 `chat` 共用统一持久化核心。协议 DTO、恢复投影、opaque 哈希、NeedsInspection 和用户确认属于当前功能，不因移除兼容层而删除；不复活旧 grant，也不恢复逐轮全量重复写入。

### 6.2 可按需增加的能力

| 首版 | 后续扩展 | 必须保持的边界 |
|---|---|---|
| 单会话单日志 | 分段与旧段压缩 | 稳定记录身份、顺序、引用和历史内容不变 |
| 顺序扫描恢复 | 索引定位检查点 | 去掉索引仍能恢复同一事实，仅性能变化 |
| 有界批次历史 | 读取索引、预取与缓存 | 游标语义与条目身份不依赖文件布局 |
| 扫描元数据列举 | 可重建的列表索引 | 持久用户元数据先有日志事实，不只写索引 |
| Core 数据契约与 CLI | GUI 自动加载、锚点与虚拟化 | 不从模型或工作区重新生成旧展示内容 |

这是演进边界，不是要求提前实现上述设施。新增可选字段可以兼容扩展；改变原有含义需要明确版本与读取策略，不能保证永不迁移。

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

### 8.2 后续变更与纠偏规则

- 执行者交付实际文件、命令与结果，列明已验证／未验证、假设变化、重复职责、旧路径退出情况及下一切片前提；未运行的验证不能写为通过。
- 总体设计与验收者检查实际 diff、测试与产物，同时检查功能和整体职责。执行者的自述或测试数量不能代替验收。
- 实现偏差在已授权范围内修复；架构假设失效时暂停依赖该假设的工作，更新本文的选择与理由，再调整 PLAN 的近期切片；产品、安全与兼容契约变更须明确交由用户决定。
- 不相关工作可以继续。不能因小问题重做全部架构，也不能在假设失败后仅改验收阈值继续推进。
- 本轮替换的本地验收条件已满足，证据见 §7；该结论不自动授权后续里程碑或真实环境验证。后续变更仍须重新核对受影响边界。

### 8.3 重要决策记录

| 日期 | 当前结论 | 依据与状态 |
|---|---|---|
| 2026-10-17 | 采用小而完整的追加式会话内核方向；保留现有执行安全，按需求增加读取与规模能力 | 方向确认时的历史记录；本轮本地证据见 §7 |
| 2026-10-17 | PLAN 只细化当前验证切片；本文保存契约、假设、推翻条件与验收 | 设计整理时的历史授权边界；本轮实施与本地验收见 §7 |
| 2026-10-17 | 旧格式未实际使用，不再维护旧读取器或显式迁移；仅保留统一日志 | 用户确认精简范围；复验见 §7.3 |
