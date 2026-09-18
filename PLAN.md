# TinyHarness.NET 项目计划

本文档定义 TinyHarness.NET 的产品需求、架构边界、MVP 范围和实施顺序。编码 Agent 的操作规范位于 `AGENTS.md`。

路线更新（2026-09-18）：M1–M7 的实现基础已完成，M8 的配置与命令入口已完成并记录于 `docs/m8-demo.md`；第 1–20 节仍作为 MVP 设计与验收基线。后续开发从第 22 节的 M9 开始，目标是把一次性任务 CLI 推进为可日常使用、可恢复对话、最终拥有 Avalonia 桌面入口的本地 coding agent。M9 及之后阶段均为计划；路线本身不代表代码修改或发布授权。

## 1. 项目定位

TinyHarness.NET 是一个用于求职展示的轻量本地 coding-agent harness。它通过 OpenAI-compatible Chat Completions 协议调用模型，围绕真实的本地代码任务提供工具调用、权限审批、受约束执行和上下文压缩。

核心展示点：

1. 可靠的 tool-call 主循环；
2. 可解释、可审计的权限控制；
3. 文件系统与进程执行的工程边界；
4. 基于 token budget 的上下文管理；
5. 可离线测试、可稳定复现的完整 Agent 链路。

项目优先追求“小而完整、稳定可演示、代码易解释”。TinyHarness 是开发主线；向其他项目提交 issue 或 PR 只是在遇到合适、可独立复现的问题时顺手进行，不能成为本项目完成的依赖。

## 2. 已确定的产品决策

- 目标框架为 .NET 10；
- NativeAOT 是 MVP 硬要求，不是发布阶段的可选优化；
- 第一使用形态为 CLI；
- 使用 `/v1/chat/completions`，不是 Legacy `/v1/completions`，也不以 Responses API 为基础；
- endpoint、API key、model 和 context window 可配置；
- 支持满足基线协议的第三方 endpoint，但不承诺兼容所有“OpenAI-compatible”服务；
- API 与 Agent Loop 逻辑分离，但 MVP 不拆成独立 API 程序集；
- 一轮可以返回多个 tool calls，但 MVP 顺序执行，不并行；
- 会话压缩由本地 Context Manager 实现，不依赖供应商专属 compaction endpoint；
- CLI、权限、Runtime 和 Context Manager 是 MVP；GUI、MCP 和多 Agent 不是 MVP；
- 开发优先采用可运行的纵向切片，不先搭建大量空接口。

只有经过契约测试或人工验证的服务和模型才能列入 README 的 tested providers/models。

## 3. MVP 范围

MVP 必须具备：

- Chat Completions 流式文本；
- function/tool calling；
- 流式 tool-call arguments 拼装；
- 一轮多个 tool calls 的校验、授权、顺序 dispatch 和结果回传；
- 最大 agent step；
- 异常、timeout 和用户取消；
- `Allow`、`Ask`、`Deny` 权限决策；
- `allow once`、`allow session`、`deny` 用户交互；
- 工作区文件边界；
- 结构化进程执行、进程树终止和输出裁剪；
- token budget、recent turns、structured state 和 compaction；
- 简单、可检查的本地会话与审计记录；
- 使用 fake model client 的离线测试；
- 可成功 NativeAOT publish，并能运行发布产物完成 smoke test；
- 一个可重复演示的“检查测试失败并修复”场景。

MVP 明确不做：

- Responses API；
- MCP；
- RAG；
- multi-agent；
- Web UI、TUI 或 Avalonia；
- Docker、VM 或 OS 级 sandbox；
- 插件系统；
- 远程服务和多用户账户；
- 复杂 eval 平台；
- 针对大量供应商的特殊兼容层；
- 专用 Git 工具；
- 自动 commit、push、tag、Release、发布或部署。

MVP 的 `shell` 理论上可以调用 Git，但必须服从命令权限规则，不能因为没有专用 Git 工具就绕过授权。

## 4. 解决方案结构

MVP 保持三个项目：

```text
TinyHarness.Core
├── Agent
├── ChatCompletions
├── Context
├── Tools
├── Permissions
└── Runtime

TinyHarness.Cli
TinyHarness.Tests
```

职责如下：

- `Agent`：主循环、step 状态、终止条件和错误传播；
- `ChatCompletions`：SDK/HTTP、认证、请求/响应 DTO、SSE 解码和流式 tool-call 拼装；
- `Context`：消息历史、token budget、tool output 裁剪、structured state 和 compaction；
- `Tools`：工具契约、schema、注册、准备和 dispatch；
- `Permissions`：capability、资源范围、规则匹配和审批；
- `Runtime`：文件系统与进程的受约束执行；
- `Cli`：输入输出、审批提示、配置加载和依赖组装；
- `Tests`：单元测试、协议契约测试和少量端到端测试。

暂不创建 `TinyHarness.OpenAI`、`TinyHarness.Runtime` 等额外程序集。只有出现第二种协议、API 依赖显著膨胀或需要独立发布 Core 时，才重新评估拆分。

`TinyHarness.Cli` 是 NativeAOT publish root。Core 和所有运行时依赖必须能被该入口静态分析和裁剪。

## 5. 核心边界

Agent Loop 不直接依赖某个 SDK 类型，只依赖一个窄的 Chat Completions client 接口，例如：

```csharp
public interface IChatCompletionClient
{
    IAsyncEnumerable<ChatStreamEvent> CompleteAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken);
}
```

Core 可以明确依赖 Chat Completions 的消息和 tool-call 语义，不建立假想的 OpenAI、Anthropic、Gemini 等供应商接口树。

SDK 选择原则：

- 优先验证官方 `OpenAI` .NET SDK 是否同时满足自定义 endpoint、流式文本、tool calls 和 NativeAOT；
- 如果 SDK 对第三方返回过于严格或缺少必要能力，只在 `ChatCompletions` 模块内增加薄 `HttpClient` 实现；
- 不把 SDK 类型泄漏到 Agent、Context、Permissions 或 Tools；
- 不引入重量级 Agent 框架；
- 任何 SDK 在普通 JIT 模式下可用，都不能替代 NativeAOT publish 与运行验证。

## 6. NativeAOT 约束

NativeAOT 必须从第一个可执行 CLI 切片开始持续验证。不能先按 JIT 方式完成全部功能，再在最后集中处理裁剪和 AOT 问题。

设计约束：

- `TinyHarness.Cli` 的发布配置启用 NativeAOT；
- MVP 至少持续验证当前开发平台的一个明确 RID，支持的 RID 必须在 README 中准确列出；
- JSON 优先使用 `System.Text.Json` source-generated context；
- Chat Completions、配置、审计和 structured state 的序列化类型必须进入静态 JSON metadata；
- 工具、权限规则和服务使用显式注册，不通过 assembly scanning 自动发现；
- 避免 `Type.GetType`、运行时泛型构造、动态代理、表达式编译和其他动态代码生成；
- 依赖注入仅使用可静态分析的注册方式；如果容器带来不必要的 AOT 风险，使用直接 composition root；
- CLI 参数库、配置库、SDK 和日志库都必须经过 AOT publish 验证；
- 不通过无依据的 suppression 隐藏 ILLink、trimming 或 AOT warning；
- 必须运行发布后的原生产物，而不只检查 `dotnet publish` exit code。

NativeAOT 验证至少覆盖：

```text
启动 CLI
读取配置
构造依赖
运行不需要网络的基本命令或 fake-client smoke path
正常退出
```

真实 API 调用可以保持 opt-in，但模型协议实现的类型不能因为测试未联网而逃避 AOT 静态分析。

## 7. Agent 状态与主循环

建议的运行状态：

```text
Ready
  -> CallingModel
  -> PreparingTools
  -> AwaitingApproval
  -> ExecutingTools
  -> CallingModel
  -> Completed / Failed / Cancelled / StepLimitReached
```

主循环：

```text
构造模型上下文
  -> 请求模型
  -> 消费流式事件
  -> 得到最终 assistant message
  -> 无 tool call：结束当前 turn
  -> 有 tool calls：全部校验并准备
  -> 分别执行权限判断
  -> 顺序执行获准的调用
  -> 生成拒绝、失败或成功的 tool results
  -> 将全部 tool results 追加到消息历史
  -> 进入下一 step
```

必须显式处理：

- 未知工具名；
- arguments 不是合法 JSON；
- arguments 不符合 schema；
- tool call ID 缺失、重复或无法匹配；
- 工具异常；
- 权限拒绝；
- 模型流中断；
- timeout 或取消；
- 达到最大 step。

权限拒绝通常转换为 tool result 返回模型，使模型可以调整方案。用户明确取消整个任务时才终止循环。

流式 `tool_calls[index].function.arguments` 可能分布在多个 delta 中。协议层必须按 choice、tool index 和 call ID 拼装，完成后再解析 JSON，不能逐 delta 解析。

## 8. 用户意图与操作阶段

Harness 应在 system/developer instructions 中明确区分询问和实施：

- 询问、评审、解释和诊断默认只允许分析与读取；
- 明确的实现或修复请求才允许进入修改流程；
- 修改意图不会自动授权具体副作用，写文件和进程仍由 Permission Engine 决策；
- commit、push、tag、Release 和发布不能从代码修改授权中推导。

模型对用户意图的判断只影响建议的工作模式，不能成为安全授权来源。真正的副作用边界由 Permission Engine 和用户审批强制执行。

建议的授权关系：

```text
只读分析
  -> 明确实施请求
  -> 本地修改与必要验证
  -> 明确 commit 授权
  -> 明确 push 授权
  -> tag / Release / publish / deploy 分别授权
```

## 9. 工具集

MVP 仅实现五个工具：

```text
list_files
search_text
read_file
apply_patch
shell
```

暂不提供任意 `write_file`。源代码修改统一通过 `apply_patch`，方便展示 diff、审批和审计。

### Prepare、Authorize、Execute

每个工具必须经过：

```text
Prepare -> Authorize -> Execute
```

`Prepare` 只能执行无副作用工作：

- 校验和规范化参数；
- 解析路径与工作目录；
- 识别将访问的资源；
- 计算所需 capability；
- 生成人类可读摘要；
- 生成稳定调用指纹；
- 创建不可变的 prepared plan。

Permission Engine 审批 prepared plan，Runtime 执行同一个 plan。执行阶段不能重新解释原始参数，避免展示内容与实际副作用不一致。

建议 capability：

```text
filesystem.list
filesystem.search
filesystem.read
filesystem.write
process.execute
```

网络权限不是独立的 MVP 工具能力，但 shell 命令一旦包含下载、远端访问或外部写入，应提高风险等级并默认询问或拒绝。

## 10. Permission Engine

权限结果：

```text
Allow
Ask
Deny
```

审批选项：

```text
allow once
allow session
deny
```

`allow session` 必须绑定 capability、规范化资源范围和必要调用约束，不能简单地永久放行某个工具。

建议决策优先级：

```text
hard deny
  > explicit/session deny
  > one-shot approval
  > session grant
  > configured allow rule
  > ask
```

默认策略：

| 操作 | 工作区内 | 工作区外 |
|---|---|---|
| 列举、搜索、读取 | allow | deny |
| apply_patch | ask | deny |
| shell | ask | deny |
| 精确配置的只读命令 | allow | deny |
| 精确配置的 build/test 命令 | allow | deny |
| Git 写操作 | ask | deny |
| 破坏性命令 | ask 或 hard deny | deny |

“测试命令”本质上仍然会执行仓库代码，不能仅根据名字自动视为安全。自动允许必须来自用户或项目配置中的精确规则，例如 executable、arguments pattern 和 cwd 范围。

权限判断必须基于 prepared plan 的规范化资源，不能只看工具名。

项目提供的是应用层 policy/approval，不是 OS sandbox。所有用户文档、CLI 文案和简历描述都必须准确表达这一边界。

### 外部仓库指令

TinyHarness 将来读取目标仓库中的 `AGENTS.md` 时，应把它作为项目约定输入，而不是安全授权：

```text
Harness hard safety policy
  > 当前用户的明确授权
  > 目标仓库 AGENTS.md
  > 推断出的约定
```

目标仓库文件可以规定构建、测试、风格和目录规则，但不能授权泄露凭据、读取范围外敏感文件、绕过权限、自动 commit/push、执行破坏性命令或发布部署。

## 11. 文件系统 Runtime

所有文件工具在执行前必须：

- 将输入路径解析为规范化绝对路径；
- 明确工作区根目录；
- 拒绝通过 `..`、绝对路径、大小写差异等方式逃逸；
- 处理 Windows junction/reparse point 和其他平台的 symlink；
- 对最终解析目标重新检查边界；
- 使用文件大小和输出长度限制；
- 支持 `CancellationToken`。

不能仅使用字符串前缀判断路径归属。

`apply_patch` 必须先解析所有目标文件并统一完成权限检查，之后才能产生写入，避免补丁预览与真实修改范围不同。

## 12. 进程 Runtime

工具名可以是 `shell`，但默认输入采用结构化进程参数：

```json
{
  "executable": "dotnet",
  "arguments": ["test"],
  "workingDirectory": ".",
  "timeoutSeconds": 120
}
```

默认直接启动 executable，不把整段字符串交给系统 shell。只有确实需要管道、重定向或 shell 内建语法时才进入显式 shell 模式，并提高风险等级。

执行要求：

- 检查 working directory 边界；
- 分离捕获 stdout/stderr；
- 支持 timeout 和 `CancellationToken`；
- 取消时终止整个进程树；
- 返回明确 exit code；
- 限制内存中的输出量；
- 截断时报告原始长度和裁剪方式；
- 防止 API key、环境变量全集和已知 secret 进入模型上下文或普通日志。

输出分为：

```text
完整输出：本地日志或临时 artifact
终端输出：供用户查看
模型输出：按预算保留 head + tail + 截断说明
```

## 13. Context Manager 与 Compaction

Chat Completions 不依赖服务端会话状态。应用显式维护并构造：

```text
System Instructions
Structured State
Recent Complete Turns
Current Turn
```

Structured State 至少包含：

```text
Goal
Constraints
Decisions
FilesInspected
FilesModified
CommandsAndResults
PendingWork
```

预算模型：

```text
ContextWindow
- ReservedOutput
- SystemInstructions
- ToolDefinitions
- CurrentTurn
= AvailableHistoryBudget
```

规则：

- 服务返回可靠 usage 时使用实际数据；否则采用保守估算；
- context window 由配置显式声明，不根据任意第三方 model 名称猜测；
- 先裁剪超长 tool output，再总结旧历史；
- 保留最近若干完整 turn；
- 旧历史压缩成结构化状态，而不是无约束自然语言摘要；
- assistant `tool_calls` 与对应 tool messages 是不可拆分的原子组；
- 等待审批或工具正在执行时不压缩；
- 摘要调用禁用 tools；
- 摘要结果需要结构校验；
- compaction 失败不得破坏原上下文；
- 完整历史保留在本地，压缩只影响发送给模型的视图；
- 测试允许设置极小预算以稳定触发压缩。

## 14. 配置

不得硬编码 endpoint、model、context window 或 API key。配置至少包含：

```text
Endpoint
ApiKeyEnvironmentVariable
Model
ContextWindowTokens
ReservedOutputTokens
CompactionThreshold
MaxAgentSteps
DefaultToolTimeout
WorkspaceRoot
CommandRules
```

API key 只从环境变量或明确的 secret provider 读取。

MVP 不实现复杂 capability negotiation。不满足基线协议的服务应返回清晰错误；只有出现真实、已验证需求时，才增加 `SupportsTools`、`SupportsStreamingUsage` 等能力配置。

## 15. 持久化与审计

MVP 不引入完整 event-sourcing 框架或数据库。优先使用简单、可检查的 JSONL 日志或 JSON session snapshot。

至少记录：

- 用户消息；
- 最终 assistant message；
- prepared tool call 摘要；
- 权限决策；
- tool result、exit code、timeout 和取消；
- compaction 前后 token 数据或估算；
- 错误状态。

不需要逐 token 持久化流式输出。崩溃恢复和中断副作用重放不是 MVP；以后若实现，绝不能自动重跑状态不明的写入或进程调用。

## 16. CLI 体验

CLI 首先让用户看清 Agent 正在做什么：

- 流式显示 assistant 文本；
- 在工具执行前显示工具名、规范化目标和风险摘要；
- `Ask` 时明确显示 allow once、allow session、deny；
- 区分模型错误、工具错误、权限拒绝、timeout 和取消；
- 显示 step 使用情况；
- compaction 时显示压缩前后 token 数或估算值。

MVP 不追求复杂终端 UI。普通文本、稳定输入和可测试输出优先于动画或布局。

## 17. 测试策略

核心逻辑必须能在没有 API key、网络和付费模型的情况下测试。优先实现 scripted/fake `IChatCompletionClient`，使用预设流式事件驱动 Agent Loop。

必须覆盖：

- 普通文本完成；
- 单个 tool call；
- 一轮多个 tool calls；
- 分片 arguments 拼装；
- 非法 JSON、未知工具和 schema 错误；
- Allow、Ask、Deny；
- 用户拒绝后模型继续；
- 最大 step；
- timeout 和取消；
- 路径逃逸和链接逃逸；
- stdout/stderr、exit code 和输出裁剪；
- compaction 不拆分 tool-call 原子组；
- compaction 后 Goal、Decisions、FilesModified 和 PendingWork 不丢失。
- structured state 的保留边界必须可检查：Goal 非空时不得清空；FilesInspected、FilesModified、
  CommandsAndResults 和已有 Decisions 是历史事实，摘要不得删除；Constraints 可替换；PendingWork
  可用非空列表更新，但每个已有待办必须原样保留，或用对应的 `completed: <item>` 条目明确
  关闭；不得静默变为空或被其他待办替换。该规则只防止关键状态的无理由丢失，不承诺消除所有
  摘要语义失真。

真实 API 测试必须显式 opt-in，不能成为默认 `dotnet test` 的必要条件。

NativeAOT 是独立质量门槛：涉及可执行入口、依赖、序列化、配置绑定、工具注册或协议 DTO 的变更，必须执行目标 RID 的 publish，并运行生成的原生产物完成 smoke test。普通 `dotnet build` 成功不能替代这一验证。

## 18. 实施路线

按可运行的纵向切片推进：

### M1：离线 Agent Loop

- 建立 CLI、Core 和 Tests 项目；
- 定义最小消息、流事件和 client 接口；
- fake client 驱动纯文本响应；
- 实现 step 与取消；
- 建立 NativeAOT publish 配置和本地 smoke path；
- 从第一个可执行版本开始运行原生发布产物；
- 完成第一批离线测试。

### M2：真实 Chat Completions

- 验证 SDK 或薄 HTTP 方案；
- 将 NativeAOT publish 结果作为 SDK 选择条件；
- 支持自定义 endpoint、model 和 key 环境变量；
- 实现 SSE 文本流；
- 实现 tool-call delta 拼装；
- 增加协议契约测试。

### M3：只读工具闭环

- 实现 `list_files`、`search_text`、`read_file`；
- 建立 tool schema、registry 和 prepared plan；
- 完成模型调用、工具结果、下一轮模型调用的闭环。

### M4：权限与写入

- 实现 Permission Engine；
- 实现 CLI 审批；
- 实现 `apply_patch`；
- 覆盖路径边界、拒绝和 session grant 测试。

### M5：进程执行

- 实现结构化 `shell`；
- 实现 timeout、取消和进程树终止；
- 实现 stdout/stderr 与输出裁剪；
- 加入命令规则。

### M6：Context 与 Compaction

- 实现预算计算；
- 实现 tool output 裁剪；
- 实现 recent turns 和 structured state；
- 完成压缩不变量测试。

### M7：持久化与演示打磨

- 增加简单 session/audit 文件；
- 完成 README、示例配置和 tested providers；
- 固化可重复 Demo；
- 固化目标 RID 的 NativeAOT publish 与 smoke 命令；
- 完成默认离线测试与必要端到端验证。

每个里程碑必须同时包含相关测试，不把测试集中拖到最后。

## 19. MVP 验收场景

目标演示：

```text
> tinyharness "检查这个项目为什么测试失败并修复"

● shell
  dotnet test

● search_text
  FooService

● read_file
  src/FooService.cs

⚠ Agent wants to modify:
  src/FooService.cs
  [allow once / allow session / deny]

● apply_patch
  src/FooService.cs

● shell
  dotnet test

✓ 48 tests passed

Context:
  18,420 -> 7,930 tokens after compaction
```

验收重点不是模型每次都能独立修复任意项目，而是 Harness 能稳定、清楚、安全地完成：

```text
模型请求
-> 工具准备
-> 权限决策
-> 受约束执行
-> 结果回传
-> 上下文管理
-> 最终回答
```

## 20. 完成定义

MVP 完成需要同时满足：

- 核心功能符合第 3 节；
- NativeAOT publish 无未解释的 trimming/AOT warning，且发布产物通过 smoke test；
- 默认测试不需要网络或 API key；
- 权限拒绝确实阻止副作用；
- 文件和进程执行具备明确边界；
- compaction 满足原子组和 structured state 不变量；
- CLI 能稳定运行验收场景；
- README 能让面试官完成 clone、配置、运行和理解设计；
- 不把 policy/approval 宣称为 OS sandbox；
- 不把未经验证的服务宣称为兼容。

## 21. 当前实现基线与下一轮问题

本节依据当前源码、README、M7 验证记录和 M8 验证记录整理。

| 领域 | 已有基础 | 后续缺口 |
|---|---|---|
| 协议 | 官方 OpenAI .NET SDK、自定义 endpoint、SSE 和 tool-call 拼装 | 真实服务的验证清单仍为空；不能把本地模拟测试当成供应商兼容证明 |
| CLI | `run`、`smoke`、无动词 prompt、`--config`、审批与最终结果；M8 已增加 help、初始化、配置、provider/model/auth 和 doctor 命令 | 交互循环和终端实时文本仍待 M9 |
| 配置 | 手工无反射 JSON 绑定；M8 支持用户配置、命名 profile、模型选择和环境变量/Windows 凭据引用 | 需要在连续会话中复用配置；凭据存储目前只验证 Windows 方案 |
| 会话 | 每次运行写 audit JSONL 和脱敏 session JSON | 没有列举、读取、续聊；现有 snapshot 是检查记录，不是完整恢复协议 |
| 上下文 | 完整内存历史、结构化摘要、tool-call 原子组、连续分批压缩 | `RunAsync` 面向新任务；需要区分用户回合与内部工具 step，并保存恢复边界 |
| 权限 | prepared plan、单次与当前运行授权、命令匹配规则 | 长对话的授权生命周期、可见性和撤销需要明确 |
| 验证 | 离线测试、固定修复 Demo、M8 配置验收、`win-x64` NativeAOT | 日常多轮任务和会话恢复还没有验收场景 |

已落地的设计选择：使用官方 SDK；配置采用手工 JSON 绑定；会话采用 JSONL + JSON snapshot；token 使用字符估算；命令规则使用结构化参数匹配；摘要使用现有 `StructuredState`。不再把这些列作尚未选择的方案。估算准确度、摘要语义保真和真实服务兼容性仍需后续验证。

当前主要问题是连续使用闭环尚未建立：用户已经能在程序里完成配置并发送一次任务，但还不能在同一进程中追问、退出后找回并继续对话。

## 22. 后 MVP 产品目标与顺序

下一轮产品目标：**把 TinyHarness 作为自己实际处理本地代码任务的工具持续使用。** 求职展示材料来自真实功能、测试证据和使用记录；外部开源 PR 继续作为独立机会，不作为主线进度条件，也不为制造 PR 而增加依赖。

| 阶段 | 用户能获得什么 | 依赖与优先级 |
|---|---|---|
| M8：配置与命令入口 | 在 CLI 中完成 API 配置和模型选择，知道当前配置来自哪里 | 已完成（见 `docs/m8-demo.md`） |
| M9：交互式连续对话 | 连续追问、看到实时回复、取消当前回合 | M8 |
| M10：会话读取与恢复 | 列出、查看、选择并继续以前的对话 | M9；先只读，再续聊 |
| M11：日常代码工作流 | 项目约定、只读模式、权限查看与撤销、变更汇总 | M10 |
| M12：可靠性与使用验收 | 用多轮场景和个人使用记录验证工具确实好用 | M11；每阶段也要同步测试 |
| M13：Avalonia 最小桌面闭环 | 在窗口中配置、聊天、审批、恢复会话 | M12；先做 NativeAOT 技术验证 |
| M14：桌面体验与分发准备 | 完整会话导航、结果查看、安装与升级验证 | M13 |
| M15：按真实需要扩展 | 有条件地接入 MCP 等能力 | M12 后评估，默认排在桌面闭环之后 |

三个可独立交付的检查点：

1. **CLI 可用版（M8–M10）**：配置 → 聊天 → 退出 → 找回并继续。
2. **日常工作版（M11–M12）**：真实项目检查与修改 → 审批 → 验证 → 可解释的结果。
3. **桌面版（M13–M14）**：通过 GUI 完成同一套流程，复用相同执行与权限逻辑。

M8–M12 是近期明确主线；M13–M14 是后续方向，进入时复核范围；M15 是候选池。按验收结果推进，不提前承诺日期，不要求把所有候选能力做完才算项目完成。

以下命令中，M8 已实现的命令以当前 CLI 为准；M9 及之后仍是拟定交互契约。实施时集中维护帮助和文档，不让 CLI 与 GUI 各自定义一套含义。

### M8：配置引导、API profile 与模型选择

目标：第一次运行时，用户不需要先打开 JSON 文件。

**范围**：

- 增加 `--help`、各子命令帮助、未知参数诊断；管理命令不得误当 prompt 发给模型。为与命令名冲突的普通文本提供 `run -- "文本"` 入口，明确 `--` 后均为 prompt。
- `tinyharness init` 引导创建用户配置：endpoint、API key 来源、模型和上下文预算；缺配置时显示该入口和实际查找路径。
- 支持命名 provider profile，每个保存 endpoint、凭据引用、模型列表及各模型的显式 context window；默认 profile 可选择。
- API key 通过隐藏输入设置到当前交互进程，或从既有环境变量读取；需要跨启动保存时，通过明确选择的系统凭据存储实现，先验证 Windows 方案。普通 JSON 只保存引用；不可用时给出环境变量用法，不退化为明文落盘。
- `config show` 显示生效值、来源和绝对文件路径，密钥只显示是否可用；`config set` 校验非敏感字段，默认只更新用户配置。
- 保留现有 `--config` 用法及旧文件语义：显式指定时选择该文件；未指定时保留当前目录 `tinyharness.json` 的优先级，再查用户配置，最后使用默认值。不在第一版隐式合并多份文件；用户配置、项目配置、会话目录分开显示。
- 用户配置默认放入平台用户配置目录，并固定解析规则；旧配置的相对路径继续按当前工作目录解释，避免悄悄改变现有行为。
- `doctor` 默认离线检查字段、路径和凭据是否存在；只有显式 `--connect` 才发送最小模型测试，并提前说明会联网且可能计费。

拟定命令：

```text
tinyharness init
tinyharness config show
tinyharness config set maxAgentSteps 60
tinyharness provider list
tinyharness provider add work
tinyharness provider use work
tinyharness auth set work
tinyharness model list
tinyharness model add <model-id> --context-window <tokens>
tinyharness model use <model-id>
tinyharness doctor
```

`provider add`、`auth set` 和 `model add` 提供必要的交互引导。独立运行的 `auth set` 用于环境变量引用或系统凭据存储；临时内存密钥仅适用于随后继续聊天的同一进程，不声称可以修改父 shell 的环境。`model list` 默认列出本地配置；远端模型发现作为可选后续能力，不假定每个 endpoint 都支持，也不从模型名称猜预算。

**验收**：在空的测试用户配置目录中，仅根据终端提示完成 profile 和模型配置；重新启动可发现配置和凭据来源；旧 `run --config`、无动词 prompt 和 smoke 仍可用。错误参数、无效预算、损坏配置和不存在的显式配置文件返回可操作提示，不发模型请求。新增序列化、凭据依赖与命令路径通过 `win-x64` NativeAOT publish 和离线 smoke；凭据值不进入日志、会话或命令参数。

### M9：交互循环、实时回复与回合控制

目标：一次启动中完成“提问 → 回答 → 追问”。

**范围**：

- `tinyharness chat` 进入交互模式；无参数且是交互终端时进入欢迎页或配置引导，重定向输入时不弹向导；保留一次性 `run`。
- 明确三个概念：session 是一段持久对话，turn 是一次用户输入及其执行结果，step 是一次普通模型请求。step 限额按 turn 计算。
- 新回合复用会话上下文，不再清空历史；已有单轮入口保持可用，不复制第二套 Agent Loop。
- 从 Core 输出窄的类型化事件：文本增量、工具准备/开始/完成、审批等待、压缩和回合终态；终端负责显示。摘要流与普通回复分开，不把内部压缩 JSON 当回复输出。
- 一次只运行一个回合；先不做运行中追加消息、并发聊天或复杂 TUI。
- `Ctrl+C` 在运行中取消当前回合并回到输入提示，空闲时退出；EOF 可正常退出。审批输入与聊天输入由同一调度器串行处理。
- 会话内命令由本地解析，不发送给模型；提供输入以 `/` 开头的普通文本的转义方式。
- provider/model 切换只能在空闲边界生效；显示新预算并重新检查上下文，超限时压缩或明确阻止请求。切换 provider 前说明后续请求会把已有上下文发送到新 endpoint。

拟定会话命令：

```text
/help
/config
/provider
/model
/new
/status
/exit
```

`/provider`、`/model` 无参数时提供选择；`/config` 显示配置并提供配置引导。交互内临时修改与保存默认值必须可区分。`/new` 新建对话并清空临时授权，不删除旧记录。当前对话获得的 session grant 只在当前进程激活期间有效，离开该对话即清除。

**验收**：fake client 连续完成三轮，后一轮能引用前文；文本在请求结束前可见且最终结果不重复输出；取消、拒绝、模型异常后仍能输入下一轮。未闭合的 tool-call 组不得污染下一次请求，须明确标记未执行/结果未知或回到有效检查点。离线交互测试覆盖命令解析、EOF、授权隔离和模型切换，原生可执行文件完成双轮 smoke。

### M10：会话浏览、持久化格式与安全续聊

目标：解决“有写没有读”，关闭程序后仍能找到并继续任务。

按三个切片实现，先交付读取，不等恢复全部完成：

1. **M10a 浏览**：`session list/show` 读取现有 M7 文件，显示时间、首条用户消息摘要、状态；旧记录没有模型、工作区等信息时明确显示未知。单个坏文件不阻断全部列举。
2. **M10b 版本化存储**：引入可恢复的 session 格式，每个完成的用户回合原子保存；保留分回合 run/audit 的关联和原始记录。
3. **M10c 续聊**：校验新格式后恢复上下文，允许追加新用户消息；提供会话选择器和最近会话入口。

拟定命令：

```text
tinyharness session list
tinyharness session show <id>
tinyharness chat --resume <id>
tinyharness chat --continue
/sessions
/resume <id>
```

**存储与恢复约束**：

- session 保存 schema version、稳定 session ID、标题、创建/更新时间、规范化工作区、profile/model 引用、回合边界、终态与 revision；run ID 标识一次执行，不再充当整个对话的唯一身份。
- 保存脱敏历史、structured state、摘要覆盖的消息边界及构建模型视图所需的状态，验证边界与工具调用匹配。不把“完整历史 + 摘要”无条件全部重新发送，避免重复上下文。
- 审计记录和恢复快照职责分开；不从 audit 猜测工具是否成功，也不反演脱敏内容。脱敏导致的上下文缺失应可见，需要时请用户补充。
- 旧 M7 snapshot 默认只读。可选迁移必须生成新记录、明确补齐缺失元数据、验证历史；无法重建压缩边界时从完整有效历史重新构建视图，不把旧摘要盲目叠加。不覆盖原文件。
- `--continue` 只选择当前规范化工作区最近的可恢复会话；没有匹配时说明原因，不能静默跳到其他项目。工作区不存在或不同，先阻止恢复；目录迁移必须显式绑定。
- 从当前配置读取凭据；旧 system prompt、权限决定和 session grants 仅供历史查看，不能作为恢复后的安全授权。恢复时使用当前策略，显示配置或指令版本变化。
- 完整回合是第一版恢复边界。处理中断时保留诊断记录和上一有效检查点，明确可能已有文件或进程副作用；不自动重放工具、重试写入或假设回滚成功。
- 同一会话跨进程写入采用独占锁或 revision 冲突检测；写入失败不报告保存成功，不覆盖较新数据。恢复文件按不可信输入校验大小、版本、ID、路径和消息结构。
- 列表先扫描会话目录，不引入数据库；默认用户会话目录与工作区分离，同时保留旧 `sessionDirectory` 配置的读取入口。

**验收**：进程 A 完成多轮并触发压缩，退出后进程 B 列出、查看、恢复并续聊，恢复前后的有效模型视图符合相同边界；原授权不会复活。覆盖旧格式、未知版本、截断 JSON、脱敏记录、工作区不匹配、并发打开、磁盘写入失败和工具中断；保证没有历史副作用重放。新增读取与恢复路径执行 NativeAOT smoke。

### M11：项目上下文、权限控制与变更汇总

目标：让多轮聊天能稳定用于自己的真实代码任务。

**范围**：

- 加载目标工作区适用的 `AGENTS.md`，显示来源和范围，明确根目录与子目录规则的适用关系；文件变更后在回合边界更新。仓库指令不能授权扩大权限。
- 增加 `/mode inspect` 与 `/mode edit`：inspect 从工具暴露和权限层禁止写工具、默认禁止进程执行；edit 仍遵循逐项权限规则。不能只靠提示词约束只读模式。
- `/permissions` 查看当前对话的临时授权及资源范围，`/permissions clear` 撤销；模型无法调用这些用户控制命令。
- 为当前会话实际执行的补丁和命令生成变更汇总：涉及文件、diff、验证命令结果、失败和未验证项。基于工具事实区分 Agent 修改与已有用户修改。
- 可增加只读 Git 状态/diff 展示，但不得把全部工作树差异都归因于当前会话；暂不增加自动提交、推送或一键回滚。
- 审批展示前及获批后实际写入前检查文件是否仍与 prepared patch 基线一致；不一致时拒绝旧计划并重新准备审批，不能覆盖用户刚做的修改。该检查不宣称提供 OS 级原子隔离。

**验收**：同一个任务先检查、后获准修改、运行验证并展示准确结果；inspect 模式确实阻止副作用工具。指令文件不能放宽权限；撤销立即生效；已有用户改动保留。针对新增权限和补丁冲突边界执行回归测试。

### M12：可靠性、使用反馈与可复现验收

目标：验证“能长期使用”，给后续 GUI 一个稳定的底座。

**范围**：

- 固定少量离线场景：首次配置、连续追问、退出恢复、拒绝后调整、取消工具、压缩后续聊、补丁冲突。每个场景验证结果和禁止发生的副作用。
- 检查模型/摘要请求 timeout、取消传播、临时网络错误和持久化失败的用户提示；若增加重试，只限明确可重试的模型请求，设置上限，禁止把副作用执行纳入自动重试。
- `/status` 展示当前模型、工作区、回合 step、上下文估算、保存状态；能获得实际 usage 时与估算区分，普通请求和摘要请求分开累计。不写死第三方价格或伪造费用。
- 进行一轮个人日常试用：选至少三个自己确实需要的小任务，记录首次配置阻碍、恢复是否有用、审批是否清楚、失败原因和人工修正次数；据此调整下一阶段范围。
- 真实模型验收显式 opt-in，记录日期、模型、endpoint 类型和验证能力；新增 tested providers 条目必须有证据。
- 文档持续区分已实现、已验证和计划中功能；维护 CLI 命令与配置示例。测试、文档同步于各阶段，本阶段负责整体验收而非补做前面遗漏的测试。

**验收**：CLI 可用版全部场景在离线测试及原生产物中通过；有可复现的使用问题记录和修复结果；真实服务未测部分明确标注。到这里即可形成独立的项目展示版本，无须等待 GUI、MCP 或多 Agent。

### M13：Avalonia 技术验证与桌面最小闭环

目标：通过图形界面完成已经稳定的 CLI 工作流。

**先做 M13a 技术验证**：

- 实施时查证所选 Avalonia 版本及依赖的 .NET 10、trimming 和 NativeAOT 支持；当前计划不把这些兼容性视为已经验证。
- 新建最小桌面入口，在 `win-x64` 发布原生产物，验证窗口启动、绑定、异步事件、取消和一次 fake 对话。优先静态/编译绑定和显式注册，检查所有 warning。
- 若依赖阻断 AOT，先缩小或替换可选 UI 依赖并记录结果；不能自动关闭 AOT 门槛。CLI/Core 的 NativeAOT 约束持续有效，桌面目标的任何放宽需另行明确决策。

**再做 M13b 产品闭环**：

- 增加 `TinyHarness.Desktop`，直接复用 Core 的配置、会话、执行、事件与审批契约，不另建 HTTP 服务。
- 仅在两个入口真实需要时提取共享应用服务，不预先拆出大量程序集；界面层只处理呈现与输入。
- 最小界面包括会话列表、对话区、输入框、模型选择、设置页、工具状态/详情和审批面板。
- 可在 GUI 中设置 API、选择工作区、新建/恢复会话、实时收取回复、取消当前回合；设置与 CLI 使用同一来源和凭据引用。
- 一次只激活一个执行任务。审批明确显示 prepared plan 和 diff，关窗或切换会话时正确取消/等待保存，不能遗留无人响应的审批和后台进程。

**验收**：从无配置启动到设置 API、完成聊天、工具审批、退出并恢复，整个路径可通过 GUI 操作；fake client 离线完成同一路径。CLI 与 GUI 的会话读取结果一致，写锁阻止同时覆盖。桌面原生发布完成启动和关键路径 smoke，CLI 的既有验证仍通过。

### M14：桌面体验、会话管理与分发准备

目标：让桌面版便于日常使用和演示，而不仅是一个可启动窗口。

**范围**：

- 会话重命名、按工作区筛选、搜索和归档；优先可恢复的归档，删除功能另行明确确认语义。
- Markdown/代码块显示、可复制命令、diff 与工具详情、长记录按需加载；避免渲染大输出阻塞界面。
- 补齐键盘导航、焦点管理、高 DPI、深浅主题和错误提示；真实外部链接只在用户主动操作时打开。
- 会话导出为脱敏 Markdown/JSON，导出前显示范围；日志、凭据和完整工具 artifact 不默认混入。
- 准备 Windows 分发说明、配置位置、升级与旧会话迁移验证、离线 Demo；下载、签名、安装形式等在实施时按实际需求决定。
- Linux/macOS 作为独立验证目标，只有对应构建、文件/进程边界和 smoke 均通过才列为支持平台。

**验收**：较长会话的滚动、搜索与恢复可用；升级保留既有配置与会话；交付说明能让新用户完成首次启动。这里只准备和本地验证产物，实际发布、上传、签名服务或远端自动化需要对应操作授权。

### M15：扩展候选与进入条件

以下不是必须完成的阶段清单。每次只选择一个有真实任务支撑的方向，重新给出小切片及验收标准。

| 候选 | 何时值得做 | 第一切片与边界 |
|---|---|---|
| MCP 工具接入 | 五个内置工具无法覆盖反复出现的服务/数据访问需求 | 先验证 SDK/AOT 和一个明确配置的本地服务；外部工具仍经过校验与审批，不信任服务提供的风险描述；服务启动也需授权 |
| 会话分支 | 经常希望保留原对话并尝试另一方案 | 从完整回合分叉消息与来源关系；不复制权限，不声称同时回滚工作区 |
| 更强代码检索 | `search_text` 在真实大仓库中出现可记录的查找瓶颈 | 先改善忽略规则、结果排序与预算；语法索引或 embeddings 必须有对比收益再引入 |
| OS 级隔离 | 确实需要运行不可信仓库命令 | 对一个平台做隔离实验，明确文件/网络/进程边界；应用层审批的文案不能提前改成 sandbox |
| 多 Agent / 并发工具 | 独立任务有明确可并行收益，且顺序执行成为瓶颈 | 先只读、限并发实验；写操作、审批、取消和上下文隔离有设计和测试后才扩大 |
| 供应商兼容增强 | 实际使用服务出现可复现协议差异 | 在 ChatCompletions 模块增加最小适配和契约测试，不先建立通用协议框架 |

GUI 已从 MVP 排除项转为后 MVP 的明确方向；MCP、多 Agent 等仍是有条件候选。远程服务、多用户账号、插件市场、复杂 RAG/eval 平台和自动发布均不进入当前主线。

## 23. 后续阶段的共同完成定义

每个阶段必须交付可运行的用户路径，而不仅是接口或存储结构：

- 本阶段命令/界面能独立演示，有清晰的错误和取消行为；
- 相关离线测试通过；涉及入口、依赖、序列化、配置或工具注册时，执行目标 RID NativeAOT publish 与产物 smoke；
- 现有单次运行、工具审批、上下文不变量和持久化脱敏没有退化；
- 保存与恢复不产生隐式工具重放，也不继承旧授权；
- README、帮助和示例与实际功能一致；新里程碑完成时更新本文件状态并链接验证记录；
- 不为了新入口重写已有 Agent Loop，不为了未来候选预先引入复杂依赖；
- 未通过的验证、真实服务兼容性和平台范围如实列出，不把计划作为完成证据。

M8 已交付命令骨架、`init`、`config show/set`、profile/凭据/模型选择和离线 `doctor`；下一项实施任务是 M9 的交互式连续对话，不同时铺开数据库和 GUI。

所有取舍继续遵循：

```text
用户控制与安全边界
> 正确性和可测试性
> 日常可用性与可演示性
> 简单性
> 扩展性
> 功能数量
```
