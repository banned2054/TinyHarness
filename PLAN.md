# TinyHarness.NET 项目计划

本文档定义 TinyHarness.NET 的产品需求、架构边界、MVP 范围和实施顺序。编码 Agent 的操作规范位于 `AGENTS.md`。

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

## 21. 暂待实现阶段验证的决策

以下问题保留到对应纵向切片用最小实验决定，不提前建立复杂抽象：

- 官方 `OpenAI` SDK 对自定义 endpoint 和第三方流式 tool calls 的实际兼容程度；
- 官方 `OpenAI` SDK 及候选替代实现的 NativeAOT 兼容程度；
- SDK 不满足要求时，薄 HTTP client 的最小范围；
- token 估算采用的库或保守算法；
- JSONL 与 JSON snapshot 的具体会话格式；
- command allow rule 的最小表达方式；
- compaction structured state 的最终 JSON schema。

任何选择都应优先满足：

```text
用户控制与安全边界
> 正确性和可测试性
> MVP 可演示性
> 简单性
> 扩展性
> 功能数量
```
