# TinyHarness.NET 历史计划归档

归档日期：2026-10-23。

原 MVP 至 M17 阶段计划在 0.0.1 原型基线收口。用户已实际使用并持续修复细节；M17 不再作为尚未进入的未来阶段。这里的收口不表示完成了原文所有场景的全面验证，也不表示产品稳定或已完成安全审查。

以下分段保存各时期的计划原文。里程碑、测试数字、产物路径、“当前”“尚未进入”及未来方向均按原记录日期理解，不作为新的工作指令或授权。特别是后段快照中的“M17 尚未进入”已由上述实际使用状态取代。新功能与具体修复另行确定范围，不从旧路线自动推进。

- [MVP 与 M1–M11 原计划](#m1-m11)
- [M12–M17 阶段计划快照](#m12-m17)
- [当前文档入口](PLAN.md)、[使用说明](Docs/README.md)、[开发接力](PLAN.md)

<a id="m1-m11"></a>

# TinyHarness.NET 项目计划

本文档定义 TinyHarness.NET 的产品需求、架构边界、MVP 范围和实施顺序。编码 Agent 的操作规范位于 `AGENTS.md`。

路线更新（2026-09-23）：M8 已完成；2026-09-19 的职责目录整理及其构建、225 项默认测试、`win-x64` NativeAOT publish/smoke 验证记录继续有效。M9 的本机 MCP worker、离线闭环、`win-x64` NativeAOT smoke、`glm-5.3` HTTPS endpoint 和 Codex 客户端调用均已验证，记录见 [M9 验证记录](Docs/Archive/m9-demo.md)。

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
以上是已实现 MVP 的范围基线，不决定后续功能顺序。后续项目路线见第 21 节（M10 统一模型协议）和第 22 节（M11 Windows 工具进程沙箱）。

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

当前项目包含 `TinyHarness.Core`、`TinyHarness.Cli` 和 `TinyHarness.Tests` 三个项目；已交付的本机 MCP stdio 入口位于 CLI。M9 的实现与验收作为历史功能记录保留，后续产品路线不再规划外置 subagent MCP worker。

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

`TinyHarness.Cli` 是现有 NativeAOT publish root，覆盖 CLI 与本机 MCP 入口；Core 和运行时依赖须能被实际入口静态分析和裁剪。

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

## 21. 统一模型协议接入

**状态：先迁移 OpenAI Chat Completions 与 Responses；Anthropic Messages 暂缓。** 2026-10-08 用户确认以 `Microsoft.Extensions.AI.IChatClient` 作为统一调用接口，底层使用官方 SDK。此项扩展现有 Chat Completions-only MVP；前文记录的是原 MVP 的范围和验收历史，不回写成当时已支持多协议。

同日实施完成：两条 OpenAI 协议路径已实现并入主线——`ModelClientFactory`/`MicrosoftAiChatClient` 适配 `IChatClient`，`chatApi` 显式选择协议（缺省 `chat-completions`），Responses 的 reasoning/item id/encrypted content 回传与 `store:false` 无状态请求、真实 usage 的审计与预算校准均已落地。离线验收（默认测试 408/408、`win-x64` NativeAOT publish 零警告、原生产物仓库外 smoke）见 [M10 验证记录](Docs/Archive/m10-model-protocol.md)。真实供应商调用尚未执行，tested providers 清单不变。

### 兼容性结论

验证使用 .NET 10、`win-x64`、`PublishAot=true`，并保持 `JsonSerializerIsReflectionEnabledByDefault=false`。所有请求都由离线 mock HTTP/SSE handler 接收，没有调用真实模型服务或读取凭据。

| 协议与包 | Managed 离线闭环 | NativeAOT 原生产物 |
|---|---|---|
| OpenAI Chat Completions；`Microsoft.Extensions.AI.OpenAI` 10.10.1 / OpenAI 2.14.0 | 通过 | 通过 |
| OpenAI Responses；同上 | 通过 | 通过 |
| Anthropic Messages；Anthropic 12.54.0 | 失败 | 失败 |

两个 OpenAI 路径已验证文本和工具参数分片、多个工具调用、usage、结束原因及下一轮工具结果回传。Responses 还验证了 `store:false`、不依赖 `previous_response_id`，以及 reasoning item ID 和 `encrypted_content` 在后续请求中的保留。工具只提供声明式 schema，SDK 不自动执行工具。

Anthropic 12.54.0 在 NativeAOT 发布产物中产生 2,625 条 IL2026/IL3050 警告记录，运行时在发出 HTTP 请求前因禁用反射 JSON 而于 `TextBlockParam` 构造路径失败。Anthropic 12.54.1 的 managed 探针也复现同类失败。NativeAOT publish 成功不能证明该路径兼容；修复单个序列化调用也不足以证明整个 SDK 兼容。因此当前不把 Anthropic 包引入生产依赖，也不通过启用反射 JSON、关闭 AOT 或压制警告来绕过此门槛。

### 本轮实施范围

- 用 `IChatClient` 统一模型调用边界，保留现有 Agent Loop、上下文预算与压缩、工具注册和 `Prepare → Authorize → Execute` 权限流程；不启用自动函数执行。
- 通过配置显式选择 Chat Completions 或 Responses。旧配置缺省继续使用 Chat Completions；不按模型名猜协议，也不自动切换。
- CLI `run`、MCP host、`doctor --connect` 使用同一客户端创建逻辑。Responses 仅使用本地历史并设 `store:false`，不引入服务端会话恢复。
- 在流、历史、压缩和持久化中保留 reasoning 内容、item ID 与 encrypted content；正确传递 usage、结束原因、取消和错误。缺少真实 usage 时继续明确标为估算。
- Anthropic 只有在官方 SDK 的完整 NativeAOT 路径通过禁用反射 JSON 的离线探针和原生产物 smoke 后，才重新进入实现范围。

### 完成验收

- 离线契约测试覆盖分片、多工具调用与结果回传、拒绝后继续、取消、错误、Responses reasoning 回传及旧配置兼容。
- 受影响测试和回归通过；`win-x64` NativeAOT publish 后实际运行原生产物，并验证现有 CLI 功能未退化。
- 真实供应商调用另行授权；离线验证只作为 SDK 适配和本地执行路径的证据，不作为真实 endpoint 兼容结论。

## 22. M11：Windows 工具进程沙箱

**状态：M11.1（执行后端）、M11.2（Windows 编排）与 M11.3（策略闭环）已实现并提交；M11.4（管理/产物）已实现并提交；M11.5（隔离验收）的验收工具（`sandbox verify` 入口、Core 验收运行器与离线测试）已实现并入工作区（2026-10-10）。2026-10-10 起接入目标改为沙箱的 TinyHarness 独立命名空间 fork（上游同机安装会与 Codex 争用机器级命名）：setup/runner 二进制替换为 fork 构建（exe 文件名与 wire 协议不变），setup payload 账户名改为 `TinyHarnessOffline`/`TinyHarnessOnline`，父进程私有桌面前缀改为 `TinyHarnessDesktop-`，sandbox home 缺省使用独立固定默认（`%LOCALAPPDATA%\tinyharness\windows-sandbox-home`），仍为 Codex 用户名的旧 marker/凭据在状态检查、执行与 provisioning 一律拒绝复用；DPAPI 与 IPC 协议不变，不安装服务。同日经用户授权在本机完成真实 provisioning 与 `sandbox verify` 全矩阵：14/15 用例 PASS（身份、deny-read、读写与元数据隔离、退出码/超时/取消/断管道/大输出/遗弃回收），`network-denied` FAIL 如实记录——沙箱内对 127.0.0.1 与本机 IP 的 TCP 连接未被拦截（按用户作用域的防火墙 BLOCK 规则未对沙箱账户进程生效，或本机 IP 环回快速路径绕过远程地址匹配，用例无法区分；WFP 当时仅覆盖 ICMP/DNS/SMB）；真实验收同时修复了 3 个离线测试无法暴露的缺陷（ConvertStringSecurityDescriptorToSecurityDescriptorW 缺第 4 参数致原生崩溃、STARTUPINFOW 布局错误、cap SID 的 workspace_by_cwd 契约）。同晚 fork 补齐按账户 SID 的 WFP 通用 TCP/UDP BLOCK 过滤器（内核强制、覆盖环回，setup v5/IPC v6 与 C# 接入层不变）并重新 provisioning 后，`network-denied` 复验 PASS、全矩阵 **15/15 PASS（exit 0，报告 `tinyharness-verify-20261010-232828.json`）**——M11.5 隔离验收在本机闭环：身份、deny-read/deny-write、元数据保护、工作区写入、断网、退出码/超时/取消、断管道、大输出、宿主遗弃回收全部真实验证通过。边界：验证覆盖本机单环境；跨 home 重复 provisioning 会轮换共享账户密码（本次已实证并经默认 home 重新 provisioning 恢复一致）；与 Codex 发布版共存未验收。详见 [Docs/README.md](Docs/README.md) M11.5 验证记录。** 2026-10-09 确定 Windows 优先，TinyHarness 的 C# Runtime 直接编排已独立编译的 Codex Windows 沙箱组件；不通过 Codex CLI/Agent 执行，不新增 Rust shim，不把修改 Codex Rust 公共 API 作为首期前置条件。Linux/macOS 后续独立接入。

技术依据及 wire 协议见 [Windows 沙箱直接组件接入方案](Docs/Archive/windows-sandbox-dotnet-integration.md)。该文档基于 Codex revision `d650bd7c05`、setup version 5、IPC version 6；组件已编译，真实 Rust 序列化/帧协议和部分 C# 片段已有验证记录，但这些不证明完整 TinyHarness 接入或隔离生效。前文 MVP 的“无 OS sandbox”是历史基线，M11 完成前现有执行边界仍然有效。

### 22.1 产品范围

这是本机 Windows 账户、受限 token、ACL、私有桌面和防火墙/WFP 隔离，不是虚拟机或独立 Windows Sandbox 桌面环境。

首期支持：

- `win-x64`，仅 `shell` 启动的工具进程；保留文件工具的应用层路径边界。
- 结构化 direct 模式和经过 quoting 验证的显式 shell 模式；非 TTY、关闭 stdin、每命令独立 runner，不支持后台常驻进程。
- 固定断网策略，以及只读执行/工作区写入策略。只读指工作区不可写，必要临时写根需单独声明；工作区写入保护约定元数据目录。
- stdout/stderr、输出脱敏/裁剪/artifact、超时、取消、进程树回收、结构化结果和审计。
- 独立的状态检查与显式 provisioning 管理入口（命令名暂定 `sandbox status`、`sandbox provision`）。

首期不做：联网代理/域名或端口白名单、模型请求扩大隔离权限或宿主逃逸、交互式终端/GUI、自动提权修复、一键全局卸载、整个 Harness/worker 的 OS 隔离。MCP worker 保持现有只读工具及 hard deny；模型 HTTP 请求不受 shell 沙箱网络策略限制。

标准 elevated 路径要求有效 `root/read`，不提供严格的“仅仓库可读”保证；环境 key 清理也不能替代磁盘敏感文件访问控制。用户界面必须说明真实读写/网络范围，不宣传不存在的隔离。

### 22.2 组件和架构边界

日常生产执行使用 `codex-windows-sandbox-setup.exe`（普通权限 refresh）和 `codex-command-runner.exe`；`codex-windows-managed-deny-probe.exe` 用于验收。`codex-windows-sandbox-service.exe` 面向服务/MSIX 模式，首期不安装或依赖该服务。2026-10-10 起这组组件替换为 TinyHarness 独立命名空间 fork 构建：exe 文件名、payload 字段名、分块环境通道、DPAPI 与 IPC 协议不变，机器级命名（账户 `TinyHarnessOffline`/`TinyHarnessOnline`、组 `TinyHarnessUsers`、防火墙规则、WFP 对象、mutex、注册表记录、桌面前缀 `TinyHarnessDesktop-`）与 Codex 发布版完全分栈。

```text
ShellTool.Prepare：命令 + 清理后的环境 + 有效隔离策略
  → PermissionEngine：审批同一不可变计划
  → ShellTool.ExecuteAsync
      → HostProcessBackend：既有宿主执行
      → WindowsSandboxBackend：C# 直接编排 setup / runner
  → 输出捕获、执行终态、工具结果和审计
```

在 `Services/Runtime` 提取窄的进程执行后端，通过 CLI composition root 显式注入 `ShellTool`。prepared execution、effective policy、result 等类型按需归入 `Models/Runtime`；名称在实现时确定，不预建跨平台空类型或拆分程序集。

后端契约不强制返回 `.NET Process`，而是表达执行会话、双流输出、取消和结构化终态。Windows 后端负责有效权限解析、refresh、capability SID、凭据、私有桌面、管道身份和 runner 生命周期的一致性；直接用当前用户 `Process.Start(runner.exe)` 不构成正确接入。

### 22.3 配置信任和权限不变量

- 组件绝对路径、sandbox home、最低隔离要求、网络上限和额外写根来自独立可信用户/宿主设置，不进入可被目标项目配置覆盖的普通来源链。模型不接收任意 profile 或组件路径参数。
- 旧配置保留宿主执行并明确显示“无 OS 隔离”；显式启用 Windows sandbox 后，缺组件、未初始化、版本/策略不匹配或启动失败均拒绝执行，绝不自动 fallback。
- Prepare 无副作用，冻结命令、cwd、timeout、清理后的环境与有效策略。refresh、SID 写回、桌面/管道创建、进程启动均在授权后执行，不得在执行阶段扩大权限。
- fingerprint/session grant 绑定 backend、策略版本、规范化有效读写范围、deny、网络、临时根、元数据保护和环境策略身份。同一命令不能复用旧授权联网、扩大写根或切换宿主；拒绝不得因 backend 变化意外失效。
- commandRules 只表示命令许可，不授予 provisioning、联网或隔离逃逸。sandbox 不自动将 `Ask` 转为 `Allow`。
- 普通 run、默认 doctor/smoke 不自动 provisioning、账户修复或 UAC。管理操作展示机器级副作用，另行取得授权。

账户、密码、网络规则是机器级共享状态。fork 后 TinyHarness 与 Codex 发布版的机器级命名完全分栈，但分栈命名只降低共存时的互相破坏，不构成共存已验收的证明；同一 fork 命名空间内部不同 home 仍共享账户、密码和网络规则（重复 provisioning 重置 `TinyHarnessOffline`/`TinyHarnessOnline` 密码会使其他 home 凭据失效），仍不为实例级完整回滚提供保证，legacy cleanup 不进入自动失败回收。仍为 Codex 用户名的 marker/凭据一律拒绝复用。

### 22.4 执行、输出和生命周期

- setup、runner、目标命令环境分别清理；明确 PATH、TEMP/TMP、用户目录和缓存。凭据仅用于启动，不写入参数摘要、模型、审计或异常。
- 可信组件、审计和 artifact 目录不对目标命令可写；若位于工作区写根内，必须明确保护或迁移至受保护位置。
- 保留跨块脱敏、head/tail 与完整输出 artifact；字节流增量解码。有界内存和输出磁盘限额不得导致静默丢失，输出不完整要明确报告。
- 直接保留 runner 的 `exit_code`、`timed_out` 和错误 stage。缺少 exit 不用 OS 退出码补造命令结果，自然退出 192 不代表超时。
- 准备/refresh、连接、启动、命令、输出排空和回收期限分别有界；取消后使用独立清理期限。区分用户取消、命令超时、启动/协议失败和策略拒绝。
- 取消与回收状态进入审计；命令失败可以返回模型，回收失败或进程树状态未知必须终止后续副作用，不能当作普通工具错误继续。
- 现有 runner 的 breakaway/保留后代行为与首期“不遗留后台进程”要求需真实验证。若既有 EXE 无法满足，应报告阻碍并重新确定范围；不以杀死 runner/根进程代替完整杀树证明，不预先授权修改 Rust 模块。

### 22.5 纵向切片与验收

| 切片 | 实施内容 | 验收门槛 |
|---|---|---|
| M11.1 执行后端 | 提取宿主 backend；prepared execution、输出与结果契约；fake backend | 现有 shell 行为不退化，相关默认测试通过 |
| M11.2 Windows 编排 | C# 版本/状态检查、权限解析、refresh、SID/凭据、桌面、管道及 runner 协议 | 模拟管道覆盖错误/终态/取消；在获授权的受控环境完成最小启动与生命周期验证，阻碍未解决不继续宣称可用 |
| M11.3 策略闭环 | 可信配置、固定断网策略、指纹/授权、CLI 展示、输出和审计 | fake model 工具调用闭环；无权限扩大、无 fallback；实际执行与审批策略一致 |
| M11.4 管理和产物 | 显式 status/provision、组件路径/版本及打包约定 | 普通入口不自动修改机器；NativeAOT publish 和仓库外原生产物 smoke 通过 |
| M11.5 隔离验收 | Opt-in Windows 文件/网络/元数据及进程生命周期测试 | 验证真实隔离与完整回收，记录组件版本、环境、结果与限制后才声明支持 |

每个切片包含直接相关测试，不将测试集中到最后。默认测试使用 fake backend/管道，不修改机器账户、ACL、注册表或防火墙；覆盖策略不可变、授权隔离、无 fallback、帧版本/长度/截断、缺终态、断管道、跨帧解码/脱敏、大输出、超时/取消和回收失败。显式 shell 验证空参数、引号、尾反斜杠、管道及组合语法，保留现有 cmd quoting 语义。

真实系统测试另行授权，在可丢弃 Windows 环境执行：允许/拒绝读写对照、只读工作区、元数据保护、断网、自然退出 23/192、实际超时、启动取消、主动 terminate、宿主异常退出和后代存活状态。标准策略的读取范围与共存限制应明确记录，测试成功也不扩大宣称范围。

涉及配置、DTO、工具入口或 P/Invoke 的切片完成 `win-x64` NativeAOT publish，保持禁用反射 JSON，无未解释的 trimming/AOT warning；从仓库外运行原生产物的 CLI smoke 和 MCP 只读边界回归。默认 smoke 不隐式 provisioning，实际沙箱 smoke 单独 opt-in。

### 22.6 后续 Linux/macOS

共用的是有效策略语义、准备/授权和执行结果契约，不是 Windows SID、桌面或管道。后续分别调查平台能力并实现 Linux/macOS backend；不支持的请求明确拒绝，不静默忽略文件/网络限制。各平台独立完成 NativeAOT RID 发布、原生产物运行和真实隔离验收后，才进入 supported 清单。


---

<a id="m12-m17"></a>

# M12–M17 阶段计划快照

以下原文在 2026-10-23 从 `PLAN.md` 并入；原更新时间为 2026-10-22。原计划内容完整保留，不以本次归档补造测试或验收结果。历史文档中的章节引用可在本快照中查阅。

# TinyHarness.NET 后续产品计划

更新日期：2026-10-22。

当前接力入口：[当前进度与操作边界](PLAN.md)。涉及当前 M14／沙箱后续工作时，先读取该文档并核对磁盘状态；后续结果更新该入口，不再反复复制长交接 prompt。文档不产生操作授权。

2026-10-22：按用户要求默认关闭普通 step 上限，`maxAgentSteps` 缺省或为 0 时只统计，正数仍启用上限，负数拒绝；配置读取和 `config set` 同步支持零。保留独立累计请求／工具预算和只读 MCP worker 限制。针对性 78/78、普通回归 1099 通过／2 链接权限跳过／0 失败，NativeAOT 和仓库外离线 smoke 通过，见[当前进度 §15](Docs/Archive/current-progress-2026-10-22.md#15-默认关闭普通-step-上限2026-10-22)。

2026-10-22：修复沙箱组合仍检查旧 `sessionDirectory`、导致默认 `artifacts/runs` 阻止 chat 续聊工作区激活的问题。`run`／`chat` 仅校验实际使用的受保护会话目录，继续拒绝工作区及附加写根内的会话日志；不写持久日志的 sandbox verify 不再检查旧快照目录。普通离线回归 1091 通过／2 链接权限跳过／0 失败，NativeAOT 和仓库外原生 smoke 通过，见[当前进度 §14](Docs/Archive/current-progress-2026-10-22.md#14-沙箱启动的旧会话目录检查修复2026-10-22)。

2026-10-22：按用户要求精简终端请求输出，仅显示原位更新的等待／思考耗时，逐请求诊断和 usage 明细继续落在同一日志，回合末保留汇总。同时默认关闭总时长上限，`maxWallClockSeconds` 为 0 或缺省时仅统计，显式正数仍可启用；累计 token 默认不限的契约不变。针对性 105/105，普通离线回归 1086 通过／2 链接权限跳过／0 失败，NativeAOT 与仓库外原生 smoke 通过，见[当前进度 §13](Docs/Archive/current-progress-2026-10-22.md#13-简洁请求状态与默认关闭总时长限制2026-10-22)。

2026-10-22：按用户要求新增可选思考展示，`run`／`chat` 使用 `--reasoning status|content`。默认同一行循环一至三个点；内容模式流式展示可读推理，复用脱敏和终端转义，不发布内部压缩文本或 opaque 载荷，不改变模型请求与日志格式。普通离线回归 1076 通过／2 链接权限跳过／0 失败，NativeAOT 和仓库外原生 smoke 通过；详见[当前进度 §12](Docs/Archive/current-progress-2026-10-22.md#12-可选思考展示2026-10-22)。

2026-10-22：按用户确认仅关闭默认累计 token 上限；`maxTotalTokens` 缺省或为 0 时继续记录用量，显式正数仍可启用。其他运行限制、上下文窗口与默认 90% 压缩路径保留。针对性 98/98、普通回归 1062 通过／2 链接权限跳过／0 失败，NativeAOT 与仓库外原生 smoke 通过。输出预留仍用于本地容量检查，尚未作为最大输出参数传给模型；见[当前进度 §11](Docs/Archive/current-progress-2026-10-22.md#11-默认关闭累计-token-上限2026-10-22)。

2026-10-22：按用户授权补齐慢请求观测。沿原请求和日志路径记录首次 SDK 流事件、推理／正文阶段与计数、缓存和推理 token 子计数；取消也保存已观察到的阶段。CLI 区分活动并将同阶段等待提示节流到三十秒。普通离线回归 1056 通过／2 链接权限跳过／0 失败，NativeAOT 及仓库外原生 smoke 通过；没有真实模型调用，也未更改上下文、推理预算或缓存策略。首个 SDK 事件不等于 HTTP 首字节，真实服务端慢因仍待新观测判断，见[当前进度 §10](Docs/Archive/current-progress-2026-10-22.md#10-请求阶段观测与缓存元数据2026-10-22)。

2026-10-22：按用户授权修复真实使用中“无可用回答却 Completed”和模型等待无反馈的问题。模型响应在追加历史及准备工具前校验明确结束原因、正文／工具形态；reasoning 或 usage 不代替回答。新增串行模型请求开始、等待和结束事件，保留现有超时、重试、预算及会话续聊路径。普通离线回归 1039 通过／2 链接权限跳过／0 失败，独立 scale 1/1、NativeAOT 与仓库外原生 smoke 通过；未新增真实模型请求，第二轮上游原因仍未知。证据与边界见[当前进度 §9](Docs/Archive/current-progress-2026-10-22.md#9-模型响应完成校验与等待反馈2026-10-22)。

2026-10-22：按用户确认，M16 收窄为交付证据核查与补缺，取消新增专用 Git tool call，复用现有 `shell`、日志与补丁事实。随后按用户授权完成核查：针对性测试 158/158、既有 NativeAOT 产物的仓库外离线 smoke、四个历史会话检查及新增 GLM 合成报告样本通过，无需新增产品代码。收窄后的 M16 已验收，环境复跑和模型文字瑕疵如实保留，见 [M16 验收](Docs/Archive/m16-verification.md)；M17 尚未进入。

2026-10-21：用户明确要求进入 M15。任务控制、运行预算和可检查工作项已实施，沿用唯一执行与日志路径；工程验收见[任务控制设计](Docs/Archive/task-control-design.md)。该实施轮未调用真实模型、未操作沙箱机器配置。

2026-10-20 当前状态：积累的改动已拆为六笔本地 commit，HEAD 为 `17f2944`，未推送，用户的注释和格式化原样保留。提交前普通回归 955 通过／2 链接权限跳过／0 失败（未包含规模测试），五个代码提交的独立副本相关测试、最新 NativeAOT 与仓库外离线 smoke 均通过，详见进度文档。

M14：基础行为已有 9 轮真实评估；2026-10-21 按用户确认替换压缩实现，退出七字段 JSON 摘要与逐字事实校验，采用有界文本交接、有界真实用户原话和宿主当前规则，仍复用唯一 session-log。真实 GLM 4 轮连续自动压缩，加独立进程恢复后的 1 次自动压缩，均正常完成、无工具请求或执行；未预置或重放摘要。指定样本的压缩、遵循与恢复尾项通过，后续不自动推进 M15。普通回归、scale、NativeAOT 与仓库外 smoke 证据见[压缩设计与验收](Docs/Archive/context-compaction-design.md)。自然语言摘要仍有继承历史错误数字的质量限制，不是事实正确性保证。替换前的失败诊断保留在系统提示词设计 §8。

沙箱：保留历史 PriceLab 5 项通过、旧 verify 15/15，以及改进探针后的真实网络专项 3/3 用例／14/14 目标、28 个宿主前后 ACK 对照；这些结果分别计数。非环回专项只覆盖 Meta 接口 `198.18.0.1`，不扩大为物理 LAN 或完整 DNS 泄漏防护。弹窗产生的两条应用放行规则已另经用户授权精确删除，清理后 ActiveStore／PersistentStore 目标规则均为 0；不重复 provisioning、PriceLab 或规则清理。完整契约与历史证据见[管理文档](Docs/Archive/windows-sandbox-management.md)。

2026-10-18：M14.1 已实施项目规则和首次发送生命周期，证据见[项目指令设计](Docs/Archive/project-instructions-history.md#14-实施结果与本地验证)。用户随后确认 M14 剩余范围改为自然对话的基础行为提示词，不引入 Analyze／Implement 切换、只读开关或前置意图分类模型。M14.2 仅维护当前模板，接入及验证边界见[系统提示词设计](Docs/Archive/system-prompt-design.md)。

**目标：从单次任务执行器，推进为自己日常使用的本地 coding-agent harness。** 单 Agent、CLI 先行、可续聊、可控制、可检查；后续 GUI 可接入已有的 DSH Desktop，但不作为核心能力的前置条件。

统一 `tinyharness.session-log/1` 追加式会话日志已实施并完成本地统一验收，`run` 与新会话 `chat` 共用同一权威写入路径，旧持久化兼容读取与迁移已按用户确认移除。历史结果见 §10.1；M14.1 在相同 envelope 中增加显式版本化规则来源记录，M14.2 沿用该日志和执行栈，不推进 M15–M17，也不授予 Git、真实模型、远端或发布操作权限。

| 范围 | 当前状态 | 证据与限制 |
|---|---|---|
| M12 交互多轮与流式宿主事件 | 已实施，已有离线验证 | chat 共享上下文、审批与输入协调；不代表历史展示契约完整 |
| M13.1–M13.3 会话保存、续聊及中断保护 | 已实施，已有离线验证 | 这是旧 v3 全量快照＋审计路径的历史能力，不代表新日志最终验收 |
| M13.3 最近历史复验 | 2026-10-14 记录为 752/752 离线测试、NativeAOT 与仓库外 smoke 通过 | 属于旧实现的历史证据，本次未重跑；真实模型续聊与断电持久性未验证 |
| 统一 `tinyharness.session-log/1` 追加式会话内核 | 已实施，本地验收完成 | 精简后普通回归 775 总／774 通过／1 链接权限跳过／0 失败；scale 另 1/1 通过；AOT 与仓库外 native smoke 通过。旧三版 writer、兼容读取与显式迁移均已退出。证据见 §10.1 |
| M14.1 项目指令与首次发送 | 已实施，离线回归、scale、NativeAOT 与仓库外 smoke 通过 | 链接创建受限的测试跳过；真实模型与 OS 隔离未验证，详见项目指令设计 §14 |
| M14.2 基础系统提示词与压缩尾项 | 已实施；本机指定样本验收通过 | 原 9 轮行为记录，加新实现 5 次真实自动压缩、跨进程状态／视图一致及授权边界回顾；完整回归与产物验证见压缩设计。仍有摘要细节错误，不外推所有模型或任务 |
| M15 任务控制与运行预算 | 已实施，工程验收见任务控制设计 | 补充、暂停／继续／取消、有界工作项、累计预算及同日志恢复；真实模型语义遵循未验证 |
| M16 交付证据核查与补缺 | 收窄范围已验收，无需新增产品代码 | 158 项针对性测试、原生 smoke、离线历史检查及指定 GLM 样本；不保证所有任务下模型文字准确，见 M16 验收 |
| M17 日常使用验收 | 后续方向，尚未进入 | 当前不展开远期施工细节，进入时重新核实依赖 |

上述 M12–M13 记录的历史源码核查基线为 `45fae0b4a70e13c56846e4be1dab9bde5c9e0aa1`。详细记录和原路线已迁入[历史基线附录](Docs/Archive/2026-10-16-session-persistence-codex-discussion.md#legacy-plan-baseline)，不以历史通过证明新日志通过；当前统一日志的本地验收结果见 §10.1，旧实现的历史通过不计入本次数字。

## 1. 文档职责与历史归档

- 本文件定义产品目标、全局边界、当前状态及近期切片；只有当前切片细化到可执行程度，后续只保留目标和依赖。
- [当前进度与接力入口](PLAN.md) 保存当前停点、提交基线、本机证据位置、下一步与操作边界；不替代产品设计，也不授予操作权限。
- [会话持久化设计](Docs/Archive/session-persistence-design.md) 定义持久化的当前目标契约、关键选择、兼容与本地验收证据。本文件引用其规则，不维护第二份详细设计。
- [项目指令加载与会话首次发送设计](Docs/Archive/project-instructions-design.md) 定义 M14.1 契约并记录实施证据，细化 §8.1。
- [系统提示词设计](Docs/Archive/system-prompt-design.md) 定义 M14.2 的基础行为、集中维护和会话提示词一致性，区分工程接入与模型行为评估，细化 §8.2。
- [上下文压缩设计](Docs/Archive/context-compaction-design.md) 定义有界交接、原话来源、失败处理、检查点兼容与替换验收；不维护第二套事实或持久化路径。
- [任务控制与运行预算](Docs/Archive/task-control-design.md) 定义 M15 的控制边界、工作项陈述、可信累计预算、日志扩展与离线验收。
- [交付证据核查与验收](Docs/Archive/m16-verification.md) 记录 M16 对现有执行、补丁与日志路径的验收矩阵、本机结果及模型解释限制。
- [2026-10-16 讨论记录](Docs/Archive/2026-10-16-session-persistence-codex-discussion.md) 是历史材料，保留早期更正与迁入的旧计划验收记录，不作为当前实施指令。
- [AGENTS.md](AGENTS.md) 定义编码 Agent 在本仓库中可以怎样工作。
- [PAST_PLAN.md](PAST_PLAN.md) 原样保存截至 2026-10-11 的旧 `PLAN.md`，包含 MVP、M1–M11、协议选择与 Windows 沙箱接入的历史决策及验收记录。该归档不再承载新增路线。
- [README.md](README.md) 与 [中文 README](Docs/README.md) 描述实际可用的功能和使用方式；新功能验收前不得按本计划宣称已支持。
- [M9 验证记录](Docs/Archive/m9-demo.md)、[M10 验证记录](Docs/Archive/m10-model-protocol.md) 和 [Windows 沙箱接入方案](Docs/Archive/windows-sandbox-dotnet-integration.md) 保留为历史证据。M11.5 真实验收闭环见 [Windows 沙箱管理与验收记录](Docs/Archive/windows-sandbox-verification.md)。

历史记录中“当时未实现”的描述不否定后来完成的能力。当前顺序看本文件，持久化详细契约看设计文档；发现两者冲突先核实并对齐，不能自行选择较宽松规则。平台安全策略、当前用户明确指令和 AGENTS.md 的优先级不变。

## 2. 当前基线与尚缺能力

### 2.1 已有基础

- .NET 10、Core / CLI / Tests 三项目结构；`win-x64` NativeAOT 发布与原生产物 smoke 已有验证路径。
- 模型通过现有 `IChatCompletionClient` 窄接口进入 Agent Loop，适配层使用 `Microsoft.Extensions.AI.IChatClient` 与官方 OpenAI SDK；由 `chatApi` 显式选择 Chat Completions 或 Responses。
- 文本流及 tool-call 参数拼装、多工具调用顺序执行、结果回传、最大 step、错误和取消。
- 五个工具：`list_files`、`search_text`、`read_file`、`apply_patch`、`shell`。
- `Prepare → Authorize → Execute`，`Allow / Ask / Deny`、单次授权、运行期授权、命令规则、文件路径和链接边界。
- 工具输出脱敏、裁剪和 artifact；上下文预算、usage 校准、完整工具原子组和有界交接压缩。
- 统一 `tinyharness.session-log/1` 追加日志支撑多回合 chat、流式正文、重启续聊、过程／最终历史、工具与补丁事实、中断检查阻断；旧三版写入器退出，只支持 `tinyharness.session-log/1`；配置、凭据引用与严格离线 list / show / doctor。
- 本机只读 MCP server，公开独立的一次性 `ask_glm`；这不是供本 Agent 调用外部工具的 MCP client。
- Windows 工具进程沙箱：直接编排 TinyHarness 独立命名空间 fork 的 setup / runner，具有显式 status / provision / verify 管理入口。

M11.5 于 2026-10-10 在本机完成真实矩阵 15/15 PASS。该证据只覆盖本机单环境；与 Codex 发布版共存未验收。文件工具仍由应用层约束，沙箱只覆盖经该后端启动的 `shell` 工具进程，不隔离整个 Harness 或模型 HTTP 请求。

### 2.2 本阶段解决的实际缺口

`run` 与新会话 `chat` 已共用追加日志，退出旧全量快照与审计双写；历史条目确定性区分过程／最终展示，补丁按文件区分 confirmed 与 unknown，工具输出与日志批次有明确上限。整体切换本地验收已完成；真实模型续聊、GUI 消费、断电持久性及实际 OS 隔离未在本轮验证。

项目指令加载和首次发送创建已在 M14.1 实施，M14.2 接入自然对话的基础系统提示词。真实 GLM 的讨论／实施边界、替换后的自动压缩、压缩后遵循与恢复已有指定样本证据；M15 已实现运行中补充、控制和预算。M16 核查现有工具日志、补丁事实与模型报告是否足以交付，只补实际缺口，并如实说明变更归因的限制。

新阶段围绕一个使用场景设计：在真实仓库中提问，继续讨论，明确要求修改，审批工具，观察执行，取消或补充要求，退出后安全续聊，最终核实修改与测试结果。

## 3. 产品范围与完成定义

### 3.1 首期目标

1. 保留现有单次 `run`，新增连续的 CLI 会话。
2. 会话拥有稳定身份、多个用户回合和可检查的任务状态。
3. 正常退出后可以续聊；异常退出后可以恢复到已确认的安全边界，不自动重放副作用。
4. 读取目标仓库约定，并显示来源和适用范围；项目内容不获得安全授权地位。
5. 通过基础系统提示词区分提问、讨论、诊断和实施，用户以自然语言表达要求；工具仍受既有权限与隔离边界约束，模型不能自行授予权限。
6. 流式展示回答、工具生命周期、审批、预算与终态；能够取消并在安全边界处理补充指令。
7. 区分模型陈述和实际证据，交付可检查的变更与验证报告。
8. 默认测试保持离线，以少量固定任务验证长会话、拒绝、取消、恢复和报告质量。
9. 新会话的已完成历史保留用户输入、过程／最终文本、工具条目与已确认具体修改；实时完成条目与历史读取保持一致。先提供有界历史数据契约，GUI 的无缝加载与阅读锚点后续单独验收。

### 3.2 本阶段不纳入

- GUI / TUI 的具体界面、DSH Desktop 迁移及其技术栈改造。
- 多 Agent 编排、自动委派、并行写入或长驻后台任务。
- 任意后台进程、PTY / 交互式终端、自动继续运行的任务调度。
- 外部 MCP client、插件市场、动态加载任意程序集。
- 自动持久记忆、向量数据库、RAG 或全仓语义索引。
- 自动 commit / push / tag / PR / Release / publish / deploy。
- 沙箱联网代理、域名白名单、自动提权、自修复或 Linux/macOS 隔离接入。
- 新供应商 SDK、自动协议切换或跨协议历史无损迁移。

这些不是永久排除项，而是不阻塞日常单 Agent 闭环的后续扩展，见第 13 节。

## 4. 持续有效的工程与安全约束

### 4.1 核心架构

- 保持 .NET 10、nullable、显式注册、source-generated JSON 和 NativeAOT；不为新功能开启运行时反射或关闭分析器。
- 复用现有 Agent Loop、工具、权限、Runtime、Context 与模型适配层；按纵向切片演进，不引入重量级 Agent 框架或先建设空接口树。
- SDK 类型不泄露到会话、权限、工具或前端。模型适配层不得启用绕过本地授权的自动函数执行。
- Core 不依赖 Console、Avalonia 或终端格式；CLI 承担输入输出和宿主组装。现有 observer / 回调优先演进为窄的宿主契约，而非再建一套平行执行引擎。
- 公共类型、namespace、配置和持久化格式变更必须明确评估兼容性。新增 Session / Workspace 等职责按需进入现有 Models / Services，不因此拆分程序集。

### 4.2 执行与授权

- 每次工具调用仍须先完整准备、审批同一不可变计划，再执行；Prepare 无副作用。
- 模式、任务描述、项目指令、记忆或模型输出都不能自行授予 capability。
- 写文件、本地执行、Git 写操作和外部发布是不同授权层次。批准本地修改不授权提交、推送或发布；隔离成功也不代表用户已批准命令。
- `allow session` 是历史 UI 用语，现有实际范围为本次运行。新会话不能把旧 grant 自动扩大到所有未来回合；默认每个新运行清空临时 grant，持久规则由用户显式配置。
- 文件工具保持最终路径与链接检查。构建和测试会执行仓库代码，不按命令名自动视为安全。
- 工具失败可回传模型；进程回收失败或状态未知必须阻止后续副作用，不能按普通错误继续运行。

### 4.3 Windows 沙箱边界

- 可信组件路径、home、有效隔离上限和附加写根来自独立可信宿主设置，不被项目配置或模型参数覆盖。
- 显式启用沙箱后 fail closed，不回退宿主；普通执行不自动 provisioning、提权、密码修复或重置网络规则。工作区目录授权须由宿主另行请求明确确认，只给固定目录及原账户补授 ACL 管理权，不改变上述机器级设置。
- 授权指纹继续绑定后端、版本、规范化读写范围、deny、网络、临时根、元数据保护和环境策略身份。
- 严格模式继续阻断环回。可信 `allowTcpLoopback` 选项仅允许 TCP 环回（包括宿主服务），外部网络及 UDP 仍阻断；采用独立账户、默认 home 和规则标识，payload/marker v6，旧严格模式 v5，IPC v6 不变。该模式已有旧矩阵、PriceLab 和一次新网络专项的真实结果，范围及限制见 [管理文档](Docs/Archive/windows-sandbox-management.md)。非 TTY、每命令 runner、无后台后代契约保持不变。标准策略要求有效 `root/read`，不宣称“只能读取仓库”。
- 同一网络模式的不同 home 仍共享机器账户和密码；重复 provisioning 会影响同模式其他 home，不能并发轮换凭据。跨模式拒绝复用已有 marker/凭据目录。命名分栈不等于与 Codex 共存已验收。
- 会话快照、审计、恢复元数据及可信组件不得对沙箱目标命令可写；新存储需复用或扩展已有保护机制。

## 5. 会话、回合与运行模型

采用三个层次，避免把现有 `runId` 直接改成长期会话 ID：

- **Session**：固定工作区、会话 ID、模型配置引用、完整对话、结构化上下文与多个回合；不主动存储认证凭据或可执行授权，但对话内容可能包含用户粘贴、文件读取或模型返回的秘密（存储与脱敏契约见 §7.2）。
- **Turn**：一次用户输入及对此输入的完整 Agent 响应，可包含多次模型请求和工具调用。
- **Run**：处理某一回合的一次执行尝试，拥有独立 ID、预算、审批状态、工具记录和终态；取消后继续产生新运行，不偷偷重启旧运行。

首期一个会话同一时刻只允许一个活动运行；同一持久会话跨进程打开也需排他写入，不能仅在内存中防并发。

多轮需明确区分稳定背景与当前目标，不能永久把第一条用户输入当作后续目标。完整用户输入和工具事实留在 canonical 日志；模型视图使用有界原话、文本交接和独立重建的当前规则。摘要不充当任务数据库或授权记录，不要求不断逐字复制旧列表；最近完整工具组和 Responses reasoning/item ID/encrypted content 的协议语义保持有效。详见压缩设计。

首期固定工作区与模型协议身份。若 endpoint / profile / model / chatApi 改变，不静默回放旧 opaque 数据；明确拒绝不兼容续聊，或由用户显式创建新会话并仅转入可检查的文本交接内容。凭据在每次启动时按引用重新解析，不写入 Session；这不代表会话内容不含秘密，落盘契约见 §7.2。

已有 CLI 入口如下；当前只接受统一日志格式：

```text
tinyharness chat                         # 创建交互会话
tinyharness chat --session <id>          # 打开已保存会话
tinyharness session list|show <id>       # 严格离线查看
```

原有 `run "任务"`、管理命令及 MCP stdio 行为保持兼容；离线列举/查看不解析 API key、不启动工具、不调用模型。

## 6. 宿主事件、流式输出与控制

统一事件至少覆盖：会话/回合/运行身份、运行状态、assistant 文本增量、工具准备、审批请求/结果、工具开始/终态、压缩、usage/预算、错误和运行结束。事件具有顺序号和关联 ID，不要求逐 token 审计持久化。

- 状态与记录的事实来自 Harness，不以模型文字“测试通过”或“任务完成”决定执行终态。
- 文本流在显示前处理跨片段已知密钥脱敏；工具输出复用已有有界捕获与脱敏路径。reasoning 默认仅展示状态，宿主显式订阅后可显示可读增量；内部压缩文本和 opaque 载荷始终不展示。
- 显示事件与审批回复、取消等控制入口分离；事件订阅者不能绕过 Permission Engine。
- 消费者断开或缓慢不能导致无限缓存、丢失重要终态或遗留进程。实现需定义有界缓冲、背压/合并策略和宿主失联处理。
- CLI 不同时以多个读取循环争抢 stdin；审批、用户输入、控制命令由单一输入协调机制分发。
- 第一版可在安全边界接收补充指令；不要求中途打断正在写入的工具或重解释已审批的计划。
- “暂停”只在模型/工具边界生效，等待确认期间不执行下一工具；取消使用既有进程树清理并确认终态，不能用暂停代替清理。

该契约由 CLI 首先使用。以后 Avalonia 消费同一状态和事件，通过同一审批/控制入口操作，而不是解析终端文字。当前 HostEvent 是内存事件，不直接等同于持久化 DTO；实时完成条目、历史条目和模型协议记录的职责按[持久化设计](Docs/Archive/session-persistence-design.md) 明确。

## 7. 持久化与安全续聊

### 7.1 当前实现与目标存储

当前新会话权威路径是 `tinyharness.session-log/1`：`run` 与 `chat` 共用一个追加日志，结构化执行事实与可恢复压缩检查点写入同一权威记录链路；chat 的旧全量 run 快照和普通全量会话重写已退出。旧快照和独立 audit 文件不再读取，不提供迁移入口，也不自动清理已有文件。具体格式、兼容和验收见[持久化设计](Docs/Archive/session-persistence-design.md)。

持续有效的边界：

- 存储失败显示“本次未可靠保存”，明确可靠前缀与未知范围；不把执行终态当作落盘成功，不盲目追加到损坏尾部。
- 使用用户级受保护目录；显式目录经词法、最终路径／链接与沙箱写保护检查。同一会话跨进程排他写入，Session ID 不是任意路径。
- “受保护”不等于加密，不承诺所有宿主 shell 或本地进程均不可读写；内容与备份按敏感数据处理。
- 读取、记录、批次、artifact 与累计资源须有上限；未知格式、损坏、路径逃逸、并发或超限明确拒绝或报告，不能静默丢数据。已实施的单个 artifact 内联上限为 2 MiB，stdout/stderr 合计 4 MiB；日志批次上限 8 MiB，日志总量上限 256 MiB。
- 当前仅支持 `tinyharness.session-log/1`；旧格式未实际使用，不维护读取或迁移兼容层。未知日志格式仍拒绝读取或续聊。

### 7.2 协议保真与脱敏

canonical 会话内容为续聊保真服务：完整历史（含 system 消息）、文本交接、保留原话来源与折叠边界原样落盘，不经过脱敏器，因此可能包含用户粘贴、文件读取或模型返回的秘密；认证凭据不从认证配置主动注入会话。opaque reasoning/encrypted content 原样保留并逐条记录 SHA-256 完整性哈希；载荷被改变、截断或哈希不符时拒绝无损续聊，不伪造原值。检查投影、审计与终端输出继续按已知密钥规则脱敏，不输出 canonical 消息或 opaque 载荷原文。

Harness 不加密会话文件，“受保护目录”只是路径与排他写检查；用户应把会话目录和备份作为敏感数据管理。旧快照、旧提交审计及迁移专属模型和序列化注册已退出生产路径；新日志恢复仍使用必要的内存投影、协议 DTO 和完整性校验。

### 7.3 中断后的继续规则

- 正常完成的会话可以由新用户输入继续；启动时重新解析可信配置、工作区、项目指令和权限，历史内容只作为历史。项目指令重新加载自 M14 起生效，不作为 M13 的前置依赖。
- 记录工具执行意图与确认结果，区分“已知未开始”“有确认终态”“可能执行但结果未知”。重启不自动调用任何旧 prepared plan。
- 若最后一次副作用的结果或进程树状态未知，将运行标为 Interrupted / NeedsInspection；禁止继续写入或进程执行，直到完成明确检查并由用户确认继续。启动时不根据模型猜测消除阻断。
- 缺少工具结果的 assistant tool-call 组不能直接送回模型；模型续聊视图从通过恢复校验的 canonical 历史构建，回到最近完整协议边界，不拆分工具原子组，不以脱敏后的检查投影替代协议内容。用户可见的中断事实与 NeedsInspection 提示使用脱敏检查投影，不直接展示 canonical 消息或 opaque 载荷原文，不补造成功、失败或 exit code。
- 不序列化和恢复单次批准、运行期 grant、已取消 token 或旧 prepared plan；新调用重新 Prepare / Authorize。
- 续聊前报告工作区和指令变化。旧“测试通过”只说明当时执行结果，不是当前状态保证。

## 8. 项目上下文与基础行为

### 8.1 仓库指令加载

首期支持工作区根 `AGENTS.md` 与被访问目录祖先链上的局部 `AGENTS.md`；从工作区根向下应用，较深文件细化局部约定。不自动读取工作区之外的父目录或跟随越界链接。

- 记录文件路径、内容标识、适用范围和加载时间；显示哪些约定进入了当前上下文。
- 为文件数量、单文件大小和总 token 设置上限；排除敏感文件和生成目录，不把整个仓库塞入初始 prompt。
- 局部指令需在首次读取/修改该范围或执行该 cwd 的命令前加载。若发现于准备之后，在安全边界废弃尚未执行的计划并重新构建上下文与准备，不扩大旧授权；不重解释正在执行的计划。
- README、项目文件、构建脚本和测试入口按需读取；不通过“发现项目”自动执行脚本或联网。
- 项目指令、源码、测试输出和工具返回视为不可信输入，只能影响任务内容与项目约定，不能覆盖 hard safety、用户授权、模式或沙箱设置。

### 8.2 基础系统提示词

用户通过自然语言提问、讨论、诊断或要求实施；不引入 Analyze／Implement 切换、只读开关或前置意图分类模型。

- 基础提示词集中维护，覆盖任务理解与范围、自主推进、代码检查与修改、工具策略、结果解释、验证交付、指令来源、连续对话和沟通。
- 提问、讨论和诊断交付结论，不擅自实施；明确实施要求则完成必要本地修改和验证，不反复请求已有授权。新要求按上下文解释，不以关键词分类代替理解。
- 参数与限制由实际工具 schema 提供；项目规则继续沿用 M14.1 的独立来源和投影，不拼入固定 system 正文，也不复制目标仓库的语言／框架规范为所有任务的默认要求。
- `run`、`chat` 与离线 smoke 使用同一当前模板，不保留旧提示词兼容分支。系统提示在会话内固定；续聊严格校验与当前模板逐字一致，不自动升级或改写 canonical 历史。
- 既有 Prepare／Authorize／Execute、沙箱、拒绝和 NeedsInspection 边界保持有效。自然语言意图遵循依赖模型；提示词不是强制只读机制，也不新增权限。
- 离线测试验证请求、工具轮、多轮、项目规则分离、压缩和恢复；真实模型另行显式 opt-in 评估指令遵循，不能以 fake model 结果宣称语义行为已通过。

## 9. 任务状态、预算与最终报告

### 9.1 可检查的工作状态

M15 在会话中维护带来源的目标／约束工作项、完成／取消陈述和证据引用，复用既有回合与 canonical 工具事实，详细契约见[任务控制设计](Docs/Archive/task-control-design.md)。文本交接只用于有界模型上下文，不作为确定性工作项数据库；不重建已删除的七字段状态或维护两套相互冲突的事实。工作项陈述、执行终态与事实引用分别保存。

- 工作项使用稳定 ID；模型可以建议更新，用户要求和工具事实保留来源。
- 模型标记工作项完成是陈述，不代表测试或副作用成功。
- 新要求按回合记录；安全边界处理队列，不修改正在执行的 prepared plan。
- 普通步数 `maxAgentSteps`、总时长与累计 token 默认仅统计，缺省或为 0 时不限，正数可分别启用上限；模型请求和工具调用数保留可信累计上限。usage 缺失时明确区分估算；摘要请求也计入用量和已启用的累计预算。
- 达限得到可解释终态，不无限循环。网络请求重试有界且只处理确认可重试的情形；不完整 assistant 响应不触发工具，不自动重试状态不明的工具副作用。
- 费用仅在用户配置了价格且 usage 可用时估算，不猜测供应商价格，也不把累计估算宣传为精确扣费上限。

### 9.2 变更与验证证据

Git 检查复用现有 `shell`，本阶段不新增专用 Git tool call。单条 Git 查询优先使用 direct 模式逐项传参，必要时使用显式 shell 模式；均沿用既有 Prepare／Authorize／Execute、目录信任和执行后端，不自动放行名为 Git 的命令。只读查询须按命令适用范围禁用可选锁、交互提示、pager、fsmonitor、外部 diff/textconv 等可执行扩展；不自动 fetch、改配置、切分支、暂存或恢复文件。Git 不可用或工作区不是 Git 仓库时，如实报告，仍可使用文件工具和任务报告。

涉及修改的任务先检查已有修改，之后结合 Harness 补丁记录与实际工作区差异说明结果。起止 status／diff 只能证明观察时的差异，不能独自证明修改者；shell 修改、用户编辑或其他并发修改无法可靠区分时，明确标记归因不明，不能把所有最终 diff 宣称为 Agent 独占成果。不通过自动建 worktree 或回滚用户文件解决归因。以后若宿主展示确有需要，再单独评估内部 Git 读取，不作为本阶段的前置建设。

交付先复用唯一 session-log 中已有的工具结果、补丁事实、预算和历史检查输出，让模型解释与实际记录相互核对；仅在信息缺失或无法检查时补齐必要字段或展示，不预建独立报告系统、第二套事实存储或强制固定报告模板。按任务相关性提供以下信息：

- 用户目标与执行终态，并区分执行 Completed 与目标真的达成。
- 本轮已确认修改、原有修改和归因不明的差异。
- 实际执行的命令、cwd、退出码、超时/取消、输出 artifact 与结果完整性。
- 已验证、未验证、失败项和仍待处理事项。
- 预算消耗、压缩和可继续的会话 ID；无法可靠保存时明确说明。

退出码按命令语义解释：退出 0 不自动等于全部测试通过，`git diff --no-index` 退出 1 可以表示发现差异。shell 包装进程的退出码与其内部 Git 的退出码须区分，无法确认时说明限制。测试数仅在已验证的解析契约或明确证据可用时给出；否则保留命令、退出码和输出供检查。模型最终文字不能覆盖确定性失败记录。

报告属于检查输出而非恢复协议：终端及按需提供的结构化报告、可分享 artifact（含命令输出和模型解释中的引用）均遵守 §7.2 的已知密钥脱敏规则，不直接嵌入 canonical 会话快照或 opaque 载荷原文。展示脱敏不得改写用于续聊的 canonical 内容。

本机真实样本：2026-10-21 使用 TinyHarness NativeAOT 产物与 `glm-5.3`，完成状态／最近提交统计、预期失败引用查询、合成 no-index diff 三个场景；模型通过现有 `shell` 执行 Git，正确报告退出 128 的失败及 diff 退出 1 的含义。另一次初始运行因审批等待耗尽墙钟预算，不计为通过；真实项目完整补丁未获外发审批、未执行。该轮未覆盖的已有修改、后续变化、截断／未执行与历史结果区分，已在 2026-10-22 的源码核查、针对性测试及合成报告样本中补验，见 [M16 验收](Docs/Archive/m16-verification.md)；不外推完整并发作者归因或自动测试结果解析。原 Git [验证记录](artifacts/git-shell-glm53-20261021/verification.md)仅保留于本机被 Git 忽略的 artifacts 目录。

## 10. 当前切片与后续方向

### 10.1 当前切片：统一日志实施与本地验收（已完成）

**统一日志与旧兼容层精简均已完成本地验收。** `run` 与 `chat` 共用 `tinyharness.session-log/1` 追加日志，旧三版 writer、旧读取器和显式迁移均已移除。

- 范围：统一会话、用户与运行条目、协议记录、工具意图与结果、具体 patch 事实、压缩检查点、退出和恢复；fake 模型、隔离临时存储和合成秘密保持默认离线边界。
- 读取：分别检查完成历史条目与模型上下文；序号游标、共享 scanner 及每页最多返回 200 条、items 序列化 UTF-8 累计不超过 8 MiB 的分页必须保持稳定；该返回预算不限制 scanner 总 I/O。
- 故障与安全：检查意图保存失败不启动工具、执行后结果未保存保持阻断、半行 strict 拒绝、中间损坏／poison、重复／确认丢失、并发写入、NeedsInspection 与用户确认持久化。
- 格式边界：只读写 `tinyharness.session-log/1`；旧快照和独立 audit 不参与列举、查看或续聊，不清理已有文件。新格式的身份、opaque、状态、并发与资源限制保持有效。
- 验收：交付实际文件、命令、结果与资源证据，按设计的假设表和场景矩阵说明已验证／未验证；核查实时／历史内容一致、canonical 保真及历史工具启动次数不增加。真实模型、GUI 和断电持久性不在本次默认结论内。
- 首次日志验收（移除兼容层之前）的普通回归：794 总、793 通过、1 项链接权限跳过、0 失败，35 s；`artifacts/test-results/session-log-regression.trx`。History 9/9 验证 Prepare 失败完成条目即时进入原始 JSONL、live/page 同序和整 round 工具未执行；rawCallId 复用已由独立 InvocationId 修复。
- 首次日志验收的 scale：另 1/1 通过、23 s，原预算不变；`artifacts/test-results/session-log-scale.trx` 与 `artifacts/session-journal-scale-results.json`。100／1,000／10,000 规模 append p95 为 2.6615／2.5418／2.665 ms；10,000 规模恢复为 1,874.3712 ms、105,944,888 B；200 条首屏为 369.5788 ms、47,105,864 B，实际读取 24,661,090 B。8 MiB 是返回 items 的累计序列化 UTF-8 预算，不是总 I/O 上限。
- 首次日志验收的 NativeAOT 重 publish 成功，0 warning／error；仓库外 exe smoke 退出 0、Completed、17 steps、16 toolExecs，验证 read／restore／history／confirmed patch diff，且唯一日志、无旧 snapshot/audit 输出。产物为 `artifacts/session-log-nativeaot/TinyHarness.exe`，文本证据为 `artifacts/session-log-native-smoke.txt`；执行命令、环境和完整测量见[设计 §7.2](Docs/Archive/session-persistence-history.md#72-最终本地验证证据)。

- 移除旧兼容层后的复验：775 总、774 通过、1 链接权限跳过、0 失败；独立 scale 1/1、NativeAOT 与仓库外 smoke 均通过。当前证据与命令见[设计 §7.3](Docs/Archive/session-persistence-history.md#73-移除旧兼容层后的本地复验)。

**本轮收口结论：** 统一日志与旧兼容层精简的本地回归、故障与安全边界、规模测量和 NativeAOT 原生产物均已取得证据。真实模型、GUI、断电与实际 OS 隔离未验证；本轮到此停止，不自动进入后续方向。

### 10.2 当前阶段与后续方向

| 方向 | 目标与依赖 |
|---|---|
| 统一日志收口与后续演进 | 在最终验收基础上处理验收发现；后续扩展不改变已确认的安全、身份和追加日志事实语义 |
| M14：项目指令与基础系统提示词 | M14.1 项目规则与首次发送已完成；M14.2 接入唯一当前行为模板，见[系统提示词设计](Docs/Archive/system-prompt-design.md)；工程验证与模型遵循评估分别记录 |
| M15：任务控制与运行预算 | 已实施有界补充／暂停／继续／取消、工作项陈述和可信累计预算；工程验证与限制见任务控制设计，遵守 §6、§9 |
| M16：交付证据核查与补缺 | 已按 §9.2 完成收窄范围验收，无需新增产品代码；矩阵、证据与限制见 M16 验收，不新增专用 Git 工具或独立报告系统 |
| M17：日常使用验收 | 集成前述能力后细化；离线固定任务、异常组合、NativeAOT 与另行授权的少量真实验证 |
| GUI 接入 | 核心数据契约验收后单独规划；真实验证自动加载、阅读锚点、卡片及 diff，不能由 Core 测试代替 |

M12、M13 的原阶段编号和历史证据保持原含义，不重命名为新格式，也不把验证切片称为已经完成的 M13.4。远期展开之前再次检查前提，不要求每次从头重做全部架构。

M16 已按现有能力核查并收口：已有修改与归因不明的表达、命令失败／截断／未执行时的报告，以及历史证据与当前工作区状态的区分均有对应证据。本轮未发现需要新增产品代码的交付证据缺口；后续实际任务出现缺口时再单独处理，不为里程碑编号额外建设功能。

## 11. 阶段验收场景

跨阶段目标场景（**部分基础已实现，整体闭环尚未完成；当前状态见首页表格**）：

```text
创建会话：以自然语言表达要求
用户：为什么测试失败？
Agent：检查项目约定和相关文件，解释证据
用户：先说一下修改方案
Agent：继续讨论，不写文件
用户：按这个方案修复
Agent：准备补丁，审批，执行必要验证，返回报告
用户：补一个边界测试
Agent：在同一会话继续，保留之前的事实
退出并重新打开会话
用户：上次还有什么未完成？
Agent：重新检查工作区，说明当前状态与历史验证的区别
```

故障对照：补丁可能已写入，但进程在结果保存前退出。重新打开时报告中断和不确定项，不重跑旧补丁；恢复到完整协议边界，用户检查确认前禁止继续副作用。

完整闭环验收须同时满足：

- 连续多轮、正常续聊与中断阻断可复现，当前目标和关键历史事实不混淆。
- 项目约定与权限授权分离，讨论和实施边界通过实际模型场景评估；权限引擎与隔离限制不能由模型或项目文本绕过。
- 事件、审批和控制可由 CLI 以外的宿主消费，但无需实现 GUI 才能验收。
- 取消、暂停、输入冲突、预算达限与落盘失败得到准确终态。
- 报告的修改、命令和验证结论有事实证据；共享工作区不被自动恢复或清理。
- 已完成历史的过程／最终条目、工具结果与具体补丁可重新读取；历史工具不重执行，压缩不导致展示内容消失；相关场景按设计矩阵取得证据。
- 长历史数据有界、顺序稳定且无遗漏／重复；GUI 自动加载和阅读锚点留到 GUI 接入后实际验收。
- 原 `run`、配置管理、只读 MCP 和 Windows 沙箱边界不退化。
- NativeAOT 与离线默认测试持续满足，不扩大已验证服务或隔离环境的宣称范围。

## 12. 验证与交付规则

- 纯文档变更检查 diff、Markdown 和本地链接，不要求编译/测试。
- 实施先构建最窄受影响项目并运行直接相关测试；新测试必须实际执行。影响公共边界、模型历史、依赖或入口时扩大回归。
- 会话格式、JSON、事件 DTO、CLI、配置或工具注册等 AOT 敏感切片，执行 `win-x64` NativeAOT publish，保持禁用反射 JSON，无未解释的 trimming/AOT warning，并从仓库外运行原生产物。
- 默认使用 fake model/backend、模拟 SSE/管道、隔离用户配置和临时工作区；不读取真实凭据、不调用付费模型、不修改机器账户/ACL/注册表/防火墙。
- 存储恢复覆盖故障注入：截断、缺终态、写入失败、序号或身份冲突、未知格式、opaque 载荷篡改/哈希不符、拆分工具原子组的折叠或恢复边界、并发打开与目录链接；检查输出的脱敏变化不得修改 canonical 内容。
- 真实模型、系统隔离复验、provisioning、GUI 迁移及任何外部操作分别取得明确授权，不能从本计划推导。
- 交付区分已实现、已验证、未验证和已知限制；验收完成后再更新 README 与里程碑状态。

### 12.1 切片接力与纠偏

- 执行者提交实际改动、命令与证据，列明假设变化、重复职责、旧路径退出情况和下一切片前提；不把模型自述当作验收。
- 总体设计与验收者检查功能和整体结构。只有测试通过但重复职责未收口、假设未验证或兼容缺失时，不得宣布切片／替换完成。
- 当前任务范围内的实现偏差可自行修复并验证；架构假设失效时先暂停受影响工作，更新设计的选择与依据，再调整本文件的近期切片。产品、安全与兼容契约变化须交由用户决定。
- 与失效假设无关的工作可以继续；不修改验收口径掩盖失败，不在每次接力时无依据重做设计。
- 本文件维护顺序和状态，详细设计只维护在对应设计文档。当前切片验收后才展开下一切片，文档状态改变不自动授予实施或外部操作权限。

## 13. 核心闭环之后的扩展

以下按实际需求独立提案，不预建基础设施，也不计入 M12–M17 完成条件：

1. **Skills / 可复用任务说明**：把重复的 review、修复、文档任务组织成有版本的说明与受约束资源；显式触发，内容不授予权限，不自动执行安装脚本。
2. **外部 MCP client**：连接用户明确配置的工具服务；外部工具继续 Prepare / Authorize / Execute，声明式 schema 和工具描述视为不可信输入。不能把已有只读 MCP server 当作 client 已实现。
3. **可检查的长期记忆**：用户确认后记录跨会话项目事实，带来源、作用域、失效和删除机制；不是把所有聊天自动写成永不过期的 system instructions。
4. **Avalonia / DSH Desktop 接入**：先核对该项目当前代码和依赖，将其接入已验证的会话、事件、审批与控制契约；不提前规定迁移目录、复用程度或 GUI 的 AOT 发布方式。
5. **更丰富工具与平台**：浏览器、图像/文档输入、LSP、受控联网、交互终端、Linux/macOS 沙箱按真实任务选取，每项分别定义安全及发布验证边界。
6. **多 Agent**：只有单 Agent 质量与成本评估显示明确收益后，再设计独立权限、预算、取消传播、任务交接和共享工作区写入隔离；不直接并行复用同一授权状态。

**统一日志与 M14 已取得此前限定范围的验收证据；M15 本地实施和工程验收见任务控制设计，收窄后的 M16 已完成验收。** 工程与指定样本通过不代表真实模型已遵循全部行为契约；后续真实评估须另行 opt-in，不自动推进 M17。GUI、断电持久性及新的实际 OS 隔离场景仍未在本轮验证。
