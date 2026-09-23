# TinyHarness.NET 项目计划

本文档定义 TinyHarness.NET 的产品需求、架构边界、MVP 范围和实施顺序。编码 Agent 的操作规范位于 `AGENTS.md`。

路线更新（2026-09-23）：M8 已完成；2026-09-19 的职责目录整理及其构建、225 项默认测试、`win-x64` NativeAOT publish/smoke 验证记录继续有效。M9 的本机 MCP worker、离线闭环、`win-x64` NativeAOT smoke、`glm-5.3` HTTPS endpoint 和 Codex 客户端调用均已验证，记录见 [M9 验证记录](docs/m9-demo.md)。

## 1. 项目定位

TinyHarness.NET 是一个用于求职展示的轻量本地 coding-agent harness。它通过 OpenAI-compatible Chat Completions 协议调用模型，围绕真实的本地代码任务提供工具调用、权限审批、受约束执行和上下文压缩。

核心展示点：

1. 可靠的 tool-call 主循环；
2. 可解释、可审计的权限控制；
3. 文件系统与进程执行的工程边界；
4. 基于 token budget 的上下文管理；
5. 可离线测试、可稳定复现的完整 Agent 链路。

项目优先追求“小而完整、稳定可演示、代码易解释”。已实现的 CLI harness 保持可用；后续优先让它成为自己真实使用的本机专项分析 worker，由 Codex 保持全局任务状态、决定是否实施修改。向其他项目提交 issue 或 PR 只是在遇到合适、可独立复现的问题时顺手进行，不能成为本项目完成的依赖。

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
以上是已实现 MVP 的范围基线，不决定后续功能顺序。后 MVP 的近期目标已调整为本机 MCP worker，见第 22 节。

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

当前 M8 基线保持 `TinyHarness.Core`、`TinyHarness.Cli` 和 `TinyHarness.Tests` 三个项目。已完成的目录整理采用职责分类优先、功能分类作为子目录的方式。MCP worker 优先复用 Core；新增入口放在 CLI 还是独立可执行项目，应以协议输入输出隔离和 NativeAOT 验证结果决定，不预建 GUI 目录或多 Agent 框架。

以下是目标结构，目录迁移已于 2026-09-19 完成（移动与拆分文件，并按职责同步调整 namespace；公开类型的 namespace 因此发生相应变化）；子目录按实际类型需要创建，不预建空目录：

```text
TinyHarness.Core
├── Models
│   ├── Agent
│   ├── ChatCompletions
│   ├── Configuration
│   ├── Context
│   ├── Permissions
│   ├── Persistence
│   ├── Runtime
│   └── Tools
├── Services
│   ├── Agent
│   ├── ChatCompletions
│   ├── Configuration
│   ├── Context
│   ├── Permissions
│   ├── Persistence
│   ├── Runtime
│   └── Tools
├── Exceptions
└── Utils

TinyHarness.Cli
├── Commands
├── Models
├── Services
├── Exceptions
└── Utils

TinyHarness.Tests
```

分类约定：

- `Models`：配置、消息、请求/响应、执行结果、状态和相关枚举，按功能建立子目录；不混入服务实现和异常类型。
- `Services`：主循环、协议客户端、配置加载与存储、上下文管理、权限判断、工具执行及 Runtime 等功能实现；接口随所属功能放置。目录分类不要求所有类型增加 `Service` 后缀。
- `Exceptions`：独立异常类型集中放置，数量增加时按功能建立子目录；名称统一使用 `Exceptions`。
- `Utils`：通用、无业务语义的小工具；有明确业务职责的逻辑归入 `Services`。
- `Commands`：CLI 命令解析后的入口调度与命令处理；可复用的核心能力放在 Core。
- 独立的顶层类、record、接口和枚举默认各自成文件，文件名与类型名一致；仅供当前实现使用的私有嵌套类型、测试 fixture 和 fake 可以保留在所属类型内。

以下功能边界继续有效；文中 `Agent`、`ChatCompletions` 等名称表示逻辑模块，其数据类型与实现分别归入 `Models/<功能>` 和 `Services/<功能>`：

- `Agent`：主循环、step 状态、终止条件和错误传播；
- `ChatCompletions`：SDK/HTTP、认证、请求/响应 DTO、SSE 解码和流式 tool-call 拼装；
- `Context`：消息历史、token budget、tool output 裁剪、structured state 和 compaction；
- `Tools`：工具契约、schema、注册、准备和 dispatch；
- `Permissions`：capability、资源范围、规则匹配和审批；
- `Runtime`：文件系统与进程的受约束执行；
- `Configuration`：配置模型、来源解析、profile 与模型选择、凭据引用和配置存储；
- `Cli`：输入输出、审批提示、配置加载和依赖组装；
- `Tests`：单元测试、协议契约测试和少量端到端测试。

暂不创建 `TinyHarness.OpenAI`、`TinyHarness.Runtime` 等额外程序集。只有出现第二种协议、API 依赖显著膨胀或需要独立发布 Core 时，才重新评估拆分。

`TinyHarness.Cli` 是现有 NativeAOT publish root。若 MCP worker 使用独立可执行项目，该项目也必须作为 NativeAOT publish root 验证；Core 和所有运行时依赖须能被实际入口静态分析和裁剪。

目录整理作为 M8 完成后的独立重构事项记录，不改变里程碑编号，也不计入已完成状态。实施时保持行为、配置与持久化格式不变；公开类型的命名空间调整需明确评估兼容性，不能仅因移动文件而自动改变公开 API。整理后执行受影响项目的构建与回归测试，涉及 AOT 敏感边界时按既有要求执行 NativeAOT publish 和 smoke。后续入口按真实需求创建。

2026-09-19 实施记录：文件已按职责移动，并把 7 个跨类别文件按类型拆分（配置的模型/解析器、StructuredState 与其 JSON context、审计模型与记录器、进程输出结果与捕获器、CLI 解析器/选项/使用异常）；Core 类型 namespace 同步调整为 `TinyHarness.Core.Models.<feature>` 与 `TinyHarness.Core.Services.<feature>`，因此直接引用这些类型的消费者需要更新 using。行为、配置与持久化格式保持不变。`Models/Persistence` 与 `Services/Persistence` 按实际类型需要新增。验证：solution 构建 0 警告、225/225 默认测试通过、`win-x64` NativeAOT `--force` 完整发布 0 trimming/AOT 警告，发布产物从仓库外部目录通过 `--help`、离线 `doctor` 与完整 smoke 路径。

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

## 21. M9 开始前的实现基线

本节记录 M9 开始前的实现基础与缺口；当前 M9 状态见下一节和 [M9 验证记录](docs/m9-demo.md)。

| 领域 | 已有基础 | 面向 MCP worker 的缺口 |
|---|---|---|
| 协议 | 官方 OpenAI .NET SDK、自定义 endpoint、SSE 和 tool-call 拼装 | 真实服务的验证清单仍为空；不能把本地模拟测试当成供应商兼容证明 |
| 入口 | `run`、`smoke`、无动词 prompt、`--config` 和 M8 管理命令 | 没有供 Codex 调用的本机 MCP 服务入口；终端审批不可直接用于协议标准输入输出 |
| 配置 | 手工无反射 JSON 绑定；用户配置、profile、模型选择和环境变量/Windows 凭据引用 | worker 需要由可信启动配置固定模型、工作区、读取范围和预算；不能把目标仓库配置当作授权来源 |
| 工具 | 三个只读文件工具、`apply_patch` 与 `shell` 均已实现 | worker 需要独立的只读工具注册表、敏感文件排除和完整的执行层拒绝；`shell` 不能按名称当作只读工具 |
| 上下文 | 完整内存历史、结构化摘要、tool-call 原子组、连续分批压缩 | 一次性 worker 应限制输入、工具结果和请求次数；不需要把长期会话或恢复作为前置条件 |
| 权限 | prepared plan、单次与当前运行授权、命令匹配规则 | MCP 调用许可不能代替对内部副作用的用户审批；无权限引擎的运行路径不能用于 worker |
| 验证 | 离线测试、固定修复 Demo、M8 配置验收、`win-x64` NativeAOT | 缺少 Codex → MCP → fake/真实模型 → 有界结果的端到端验收及真实使用记录 |

这些缺口构成了 M9 的实施范围。已落地的设计选择包括官方 SDK、手工 JSON 绑定、JSONL + JSON snapshot、字符 token 估算、结构化命令参数匹配和复用现有 `StructuredState`。估算准确度、摘要语义保真，以及这次已验证组合以外的服务兼容性仍需后续验证。

连续会话、恢复和 GUI 属于原先拟定的使用方式，不作为当前主线的前置条件。

## 22. 后 MVP 主线：本机 GLM 专项分析 worker

下一轮产品目标：**让 Codex 把小而具体的本地代码调查任务交给 TinyHarness，使用一次性 GLM 上下文得到可核查的结论。** Codex 保持全局任务状态并决定是否实施；TinyHarness 管理本机工具、模型调用、预算和结果。GLM 输入、缓存和输出的成本特点及窄任务的效果是待实际测量的工作假设，不把它们写成已验证结论。

这是 TinyHarness **向 Codex 提供 MCP 服务**，与原先候选的“TinyHarness 接入外部 MCP 工具”方向不同。第一版只提供一个 `ask_glm` 工具，不建立通用 MCP 平台、多模型编排层、长期子智能体会话或远程服务。

| 阶段 | 用户能获得什么 | 优先级与进入条件 |
|---|---|---|
| M8：配置与命令入口 | 已有 CLI、profile、凭据引用和离线诊断 | 已完成（验证见 README） |
| M9：只读 MCP worker | Codex 调用 `ask_glm`，GLM 在固定工作区调查并返回有界结论 | 本机实现、离线闭环、NativeAOT smoke 和一次 `glm-5.3` HTTPS endpoint 调用已完成；Codex 客户端注册与调用待完成 |
| M10：真实使用与任务包优化 | 用其他项目的真实小任务评估质量、成本、延迟和重复读取 | M9 可用后；只修复实际暴露的问题 |
| M11：补丁建议模式 | GLM 返回可审查的补丁建议，由 Codex 决定是否应用 | 仅在 M10 记录了反复搬运补丁的需求后进入 |
| M12：受控执行能力 | 在明确审批链路下让 worker 修改或运行特定命令 | 仅在只读与建议模式不足以完成真实任务后进入 |

M9 与 M10 是近期主线；M11、M12 是有进入条件的候选，不要求全部完成。交互式连续对话、会话恢复、Avalonia、TinyHarness 作为 MCP 客户端、多 Agent 并发和复杂部署不再占用当前路线编号；如果后来有真实使用需求，重新定义范围和验收。已实现的单次 CLI 和 MVP 验收基线保持有效。

求职展示材料来自真实功能、测试证据和使用记录；外部开源 PR 继续作为独立机会，不作为主线进度条件。

### M8：配置引导、API profile 与模型选择

**状态：已完成。** 命令入口、profile、凭据引用、模型选择和离线 doctor 已交付。M9 的 worker 配置另设用户配置边界，不采用目标项目配置。

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

已实现命令：

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

### M9：本机只读 MCP worker

**本机实现、离线验收、真实 endpoint 与 Codex 客户端调用：已完成。** `glm-5.3` 经配置的 HTTPS endpoint 从 Codex MCP 工具完成了有界只读调查并返回行号证据。记录见 [M9 验证记录](docs/m9-demo.md)。当前兼容性结论仅适用于已测模型与 endpoint。

目标：Codex 调用一次 `ask_glm`，TinyHarness 在一个新的、有限的 GLM 上下文中调查本地代码，返回可核查的结论，不修改工作区。

**请求契约**：必填的具体 `task`；可选、大小受限的 `knownFacts`、`focusPaths`、`expectedOutput`。调用方提供的事实与文件内容只是任务资料，不是授权。`focusPaths` 只能缩小或提示搜索范围；模型、endpoint、工作区、权限模式、命令规则和预算均不能由 MCP 请求提高或改写。一次调用生成独立 run，不继承此前消息、会话授权或模型结论。

**入口与边界**：

- 提供本机 MCP 标准输入输出入口，第一版只注册 `ask_glm`。标准输出仅传协议消息，诊断写标准错误；不启动监听网络的服务。Codex 取消请求或断开连接时，取消对应模型调用和文件工具。第一版串行执行请求，避免共享状态与预算相互污染。
- 复用现有 Core 的 Chat Completions client 与 Agent Loop；具体 GLM 模型和 endpoint 从可信的本机配置显式选择。MCP 入口不自动采用目标仓库的 `tinyharness.json` 或其中的 `commandRules`。旧 CLI 的配置优先级保持原样，worker 另行建立清晰的可信配置边界。
- worker 只注册 `list_files`、`search_text`、`read_file`；权限层也必须明确拒绝 `filesystem.write`、`process.execute` 和未知副作用能力。不得用不注入 Permission Engine 的 Agent Loop 路径，也不得把 MCP 调用许可当作内部操作审批。`apply_patch` 和 `shell` 不向 worker 暴露。
- 工作区由服务启动时的可信配置固定；读取、搜索和列举都执行工作区及链接边界检查，并对直接路径和遍历结果应用同一套敏感文件排除。默认保护常见凭据与密钥文件，允许可信配置进一步收紧范围；仓库内容和 `AGENTS.md` 不能扩大读取权限。明确告知使用者，准许读取的内容会送往所选模型 endpoint。
- 以可配置的任务输入长度、模型请求次数、工具调用次数、工具输出量、总上下文量、返回长度和总耗时限制发散；达到限制时返回“未完成”及已有证据，不伪造结论。短任务不做自动 compaction；预算不够时结束或请求更小任务包，不能因关闭 Context Manager 而无限发送历史。
- 固定 specialist 指令要求只解决当前任务、证据足够时结束，并把任务包、仓库文本与工具结果视为待分析资料而非新的权限或系统指令；这些指令不能替代前述工具和权限硬边界。
- 输出包含状态、结论、证据（工作区相对路径及行号）、建议改法、测试建议、不确定项和执行统计。只把有界结果交还 Codex；不把完整工具历史、凭据、原始审计或大段代码默认塞进主会话。GLM 可以提出修改建议，但本阶段不返回已应用变更。

**验收**：fake client 离线完成一次 Codex 风格 MCP 请求，经文件检索返回结构化结果；越界路径、敏感文件、提示注入诱导写入或运行命令均失败且无副作用；两个请求的状态与授权隔离；取消、超时、模型错误和达到预算都有明确结果；协议标准输出无诊断污染。新入口及协议序列化完成 `win-x64` NativeAOT publish 和原生产物 smoke。另须在 Codex 中显式启用真实 GLM，完成至少一个本地小任务并记录模型、endpoint 类型和日期；不能把离线 fake 视为兼容验证。

### M10：真实使用与任务包优化

目标：让 `ask_glm` 成为自己在 Codex 中实际会调用的工具，用使用记录决定下一步，而不是补齐原先设想的通用 Agent 产品功能。

**范围**：

- 至少在三个非 TinyHarness 项目的真实、小范围代码调查任务中使用。记录 Codex 给出的任务包、GLM 检查的文件、证据是否充分、结论是否正确、人工修正次数及是否真正节省主任务时间。敏感项目内容不进入公开 fixture 或文档。
- 根据失败样本调整任务包：明确目标、已知事实、当前唯一关键未知量、关注文件、禁止扩大范围的约束和预期输出。`knownFacts` 不覆盖工具查到的相反证据；worker 须标出矛盾与不确定性。
- 对反复搜索、无新证据的重复读取和过长最终输出设置可解释的停止条件；必要时改善搜索排序或关注路径输入，不先建设索引、记忆系统或复杂规划器。
- 能取得可靠 usage 时分别记录输入、缓存输入和输出 token；否则标注为估算或不可用。记录调用耗时、模型请求数、工具调用数及 Codex 收到的结果长度。按同类任务比较直接让 Codex 调查与调用 worker 的结果；如有可行对照，再比较长期 GLM 主 Agent 与一次性 specialist 的成本和质量。不写死供应商价格。
- 修复真实使用暴露的协议、取消、路径和结果质量问题；默认回归测试保持离线。真实服务验收显式 opt-in，并准确更新 tested providers/models 记录。

**验收**：至少三个任务有可复核的匿名化结果记录，能指出成功案例、失败案例和下一项最值得做的改进；Codex 收到的结果足够做下一步决定，同时不会被完整 worker 历史淹没。每项改动有对应离线回归测试，真实调用的计量数据与估算值明确区分。

### M11：候选——补丁建议模式

进入条件：M10 的真实使用记录表明，GLM 的建议经常足够准确，却需要用户反复手动把修改搬给 Codex。

目标：让 worker 返回可审查、可定位到文件基线的最小补丁建议，**仍由 Codex 根据当前用户任务和自身权限决定是否应用**。这一阶段不把 `apply_patch` 注册给 worker，不因为输出了 diff 就声称文件已经修改。

**范围**：

- 在 `ask_glm` 的结果中增加可选的统一 diff 或逐文件修改建议，并附根因、证据、适用的文件路径、生成时的文件基线标识和测试建议。证据不足时明确返回无法给出可靠补丁。
- 补丁建议受文件数、字符数和返回预算约束；不允许在建议中夹带对工作区外路径、Git 操作或外部平台操作的执行指令。
- Codex 应以当前文件内容重新检查和应用建议；建议可能过期，不把 worker 的文本输出解释成审批或已执行结果。

**验收**：针对真实样本，Codex 能看清建议涉及哪些文件以及依据；文件在调查后变化时，旧建议不会被当成可无条件应用的事实。worker 仍保持零写入、零进程执行，离线测试覆盖无效 diff、路径越界和输出截断。

### M12：候选——受控执行能力

进入条件：M10/M11 的使用记录表明，只读分析和 Codex 应用建议仍有反复出现、可量化的阻碍；先选一个具体操作，不一次开放所有内置工具。

目标：在保持 Codex 主任务控制权的前提下，为 worker 增加确有收益的本地副作用能力。

**约束**：

- 优先评估明确配置的验证命令；测试和构建会执行仓库代码，不能把它们称为只读 shell。通用 `shell`、显式解释器模式和网络访问不因单个验证需求自动开放。
- 若允许 worker 写文件，必须有把 prepared plan 和 diff 展示给**实际用户**的审批通道；Codex 对 `ask_glm` 的一次调用许可、模型建议或仓库指令都不能代替内部审批。MCP 标准输入输出不得与终端提示混用；没有可用审批通道时拒绝执行。
- 授权绑定能力、规范化路径、命令约束和单次 worker run；不跨请求继承 session grant。检查审批时与实际写入前的文件基线；发生变化就废弃旧计划。失败或取消后如实报告已发生与状态未知的副作用，不自动重放。
- Git 暂存、提交、推送、标签、Release、发布和部署保持各自独立的用户授权阶段；本阶段不默认实现这些操作。进程仍非 OS 沙箱，若真实任务需要运行不可信代码，须另行评估隔离。

**验收**：先针对选定的一个副作用场景完成离线授权、拒绝、取消、冲突及结果归因测试；无审批或越权请求确实没有副作用。相关入口、工具和权限变更通过 NativeAOT publish 与原生产物 smoke 后，才在真实任务中启用。

### 其他候选与进入条件

以下方向没有预定里程碑。只有在 `ask_glm` 的真实使用记录提供明确需求时，才重新提出范围与验收：

| 候选 | 进入信号 | 第一切片 |
|---|---|---|
| 更强代码检索 | 现有搜索在多个真实仓库中成为主要瓶颈 | 先改善忽略规则、排序和预算，再比较索引收益 |
| TinyHarness 接入外部 MCP 工具 | 内置文件工具无法取得反复需要的本地服务或数据 | 从一个明确配置的服务验证 SDK/AOT、权限和启动边界 |
| 交互式 CLI 与会话恢复 | 用户确实需要让 TinyHarness 而非 Codex 长期持有对话 | 另定回合、恢复、授权失效与中断副作用契约 |
| GUI | 本机 MCP + Codex 界面不能满足反复出现的交互或审批需求 | 先验证 UI 依赖、NativeAOT 和一个最小闭环 |
| 并发 worker 或多模型编排 | 串行、单一 specialist 在真实任务中形成可量化瓶颈 | 先做只读隔离和资源上限实验 |
| OS 级隔离 | 必须运行不可信仓库命令 | 对一个平台验证文件、网络和进程边界，不能把现有应用层策略称为沙箱 |
| 供应商兼容增强 | 真实服务出现可复现协议差异 | 在 ChatCompletions 模块增加最小适配与契约测试 |

远程服务、多用户账户、插件市场和自动发布不进入当前主线。

## 23. 后续阶段的共同完成定义

每个进入实施的阶段必须交付可运行的用户路径，而不仅是接口或存储结构：

- Codex 能发起边界清晰的任务并收到状态、证据和限制说明；模型错误、取消与预算用尽不会被呈现为成功结论；
- 默认离线测试无需网络或 API key，覆盖禁止发生的副作用；真实模型调用显式 opt-in；
- 涉及入口、依赖、序列化、配置或工具注册时，执行目标 RID NativeAOT publish 与原生产物 smoke；
- 现有 CLI 单次运行、工具审批、上下文不变量和持久化脱敏没有退化；MCP 协议输出与诊断隔离；
- 每次 worker 请求隔离历史、权限与预算；任何恢复或重试均不隐式重放副作用或继承旧授权；
- README、帮助和示例区分旧 CLI 与新 MCP worker 的配置和权限边界；里程碑完成时更新状态并链接验证记录；
- 不为了新入口重写已有 Agent Loop，不为未来候选预先引入 GUI、数据库、通用 MCP 平台或复杂编排；
- 未通过的验证、真实服务兼容性、模型用量和平台范围如实列出，不把计划或估算当成完成证据。

M8 已交付命令骨架、`init`、`config show/set`、profile/凭据/模型选择和离线 `doctor`。M9 的只读 MCP worker 已通过离线、NativeAOT、真实 `glm-5.3` endpoint 和 Codex 客户端调用验收；下一步按 M10 范围记录真实使用，再决定后续改进。

所有取舍继续遵循：

```text
用户控制与安全边界
> 正确性和可测试性
> 日常可用性与可演示性
> 简单性
> 扩展性
> 功能数量
```
