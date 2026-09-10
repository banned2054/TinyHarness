# TinyHarness.NET

一个基于 .NET 10 的轻量本地 coding-agent harness。通过 OpenAI-compatible Chat Completions 协议调用模型，让模型在工作区内检查文件、提出补丁和运行命令，并由应用执行参数校验、权限审批、工具调度与上下文管理。

项目重点是把 Agent 的执行链路做小、做完整、做得可解释：模型负责提出下一步行动，Harness 负责准备、授权和执行。CLI 是当前入口，NativeAOT 是持续验证的构建约束。

## 当前状态

M1–M6 已完成：Agent Loop、真实协议接入、只读工具、权限与补丁、进程执行、Context 与 Compaction。M7 的 session/audit 文件持久化和“检查测试失败并修复”的固定演示场景仍待完成。

| 能力 | 当前实现 |
|---|---|
| 模型协议 | 官方 OpenAI .NET SDK 接入自定义 endpoint；接收 SSE 文本与 tool-call 参数分片 |
| Agent Loop | 一轮多个工具调用、顺序执行、结果回传、最大步数、失败和取消处理 |
| 文件工具 | `list_files`、`search_text`、`read_file`、`apply_patch` |
| 进程工具 | `shell` 的直接进程与显式 shell 模式；stdout/stderr、退出码、超时、进程树终止和输出裁剪 |
| 权限 | `Allow` / `Ask` / `Deny`；单次授权、会话授权与命令允许规则 |
| 上下文 | token 估算、工具结果裁剪、近期完整回合、结构化摘要、连续多批压缩 |
| 验证 | fake-client 离线测试、本地模拟 SSE 协议测试、`win-x64` NativeAOT smoke |

当前 CLI 每次启动执行一个任务，显示审批、压缩提示和最终结果；尚未提供交互式多轮会话、逐 token 终端文本展示或会话恢复。

## 快速开始：先运行离线 smoke

以下命令使用 PowerShell，在 Windows 上执行。需要 .NET 10 SDK；首次还原 NuGet 依赖需要网络，测试和 smoke 本身不需要真实模型服务或 API key。

```powershell
git clone https://github.com/banned2054/TinyHarness.git
cd TinyHarness
dotnet restore TinyHarness.slnx
dotnet test TinyHarness.Tests/TinyHarness.Tests.csproj --no-restore
dotnet run --project TinyHarness.Cli -- smoke --config tinyharness.json
```

仓库中的 [tinyharness.json](tinyharness.json) 使用占位 endpoint 和 smoke 模型名，可以直接用于离线 smoke，不能直接用于真实模型调用。

Smoke 会创建临时工作区，使用脚本模型和自动审批 fixture 验证文件列举、搜索、读取、补丁、直接进程与显式 shell 的完整工具链路。成功时输出包含：

```text
status       : Completed
steps        : 10
toolExecs    : 9
```

它验证 Harness 的执行流程；真实模型的修复能力和压缩效果需要单独验证。压缩不变量与连续多批压缩由离线测试覆盖。

## 连接真实模型

准备一个 JSON 配置文件，例如 `artifacts/live.json`。`artifacts/` 已被 Git 忽略，可用于本地配置；先创建该目录，再保存下面的内容，并替换 endpoint、model、工作区及对应的窗口参数。

```json
{
  "endpoint": "https://your-provider.example/v1",
  "model": "your-tool-capable-model",
  "apiKeyEnvironmentVariable": "TINYHARNESS_API_KEY",
  "contextWindowTokens": 128000,
  "reservedOutputTokens": 8000,
  "compactionThreshold": 100000,
  "maxAgentSteps": 40,
  "defaultToolTimeoutSeconds": 120,
  "workspaceRoot": "C:/Code/YourProject",
  "commandRules": []
}
```

`endpoint` 是 SDK 基地址，例如以 `/v1` 结尾；不要填写完整的 `/v1/chat/completions` URL。模型需要支持流式 Chat Completions 和 function/tool calling。窗口大小由配置显式声明，不根据模型名称推断；示例数值不代表任何具体模型的能力。

在当前 PowerShell 会话中输入密钥并执行任务：

```powershell
$env:TINYHARNESS_API_KEY = Read-Host "API key" -MaskInput
dotnet run --project TinyHarness.Cli -- run --config artifacts/live.json "检查这个项目为什么测试失败并修复"
```

`-MaskInput` 需要 PowerShell 7.1 或更新版本。密钥由配置指定的环境变量读取，不写入 JSON。`run` 会实际调用模型服务，普通请求和压缩摘要请求都可能产生费用；获得授权的工具会实际修改文件或启动进程。

CLI 也接受省略 `run` 的形式：

```powershell
dotnet run --project TinyHarness.Cli -- --config artifacts/live.json "说明这个项目的目录结构"
```

未指定 `--config` 时读取当前目录的 `tinyharness.json`。`workspaceRoot` 缺省时使用进程当前目录；相对路径也相对于进程当前目录解析，而非配置文件所在目录。操作其他项目时建议填写绝对路径。

按 `Ctrl+C` 取消任务。正常完成返回 `0`，任务失败或达到步数上限返回 `1`，缺少提示词返回 `2`，正常运行路径中的任务取消返回 `130`。

### 配置项

| 字段 | 默认值 | 含义 |
|---|---|---|
| `endpoint` | 空 | 真实服务的 HTTP(S) 基地址 |
| `model` | 空 | 模型标识；真实运行必须填写 |
| `apiKeyEnvironmentVariable` | 空 | 保存 API key 的环境变量名称 |
| `contextWindowTokens` | `128000` | 声明的上下文窗口 |
| `reservedOutputTokens` | `8000` | 预算中为输出预留的空间；当前不作为 API 输出上限发送 |
| `compactionThreshold` | `0` | 触发压缩的总估算 token 阈值，含预留输出；`0` 使用窗口大小 |
| `maxAgentSteps` | `40` | 普通模型请求的最大步数，摘要请求不计入该步数 |
| `defaultToolTimeoutSeconds` | `120` | 工具默认超时秒数 |
| `workspaceRoot` | 当前目录 | 文件工具边界与命令工作目录的根 |
| `commandRules` | `[]` | 显式配置的进程允许规则 |

## 权限与执行边界

每个工具经过 `Prepare → Authorize → Execute`：先校验参数、规范化目标并生成准备计划，再根据能力和资源范围判断权限，最后执行同一个计划。

工作区内的列举、搜索和读取默认允许；补丁与进程默认询问。文件工具检查规范化路径，并在执行前检查 symlink/junction 的最终目标，拒绝越界访问。

审批界面显示调用摘要、目标路径，以及 `apply_patch` 的 diff：

```text
[a]llow once / [s]ession / [d]eny:
```

- `a`：允许当前调用一次。
- `s`：按提示中的能力、路径范围及调用约束授权本次运行；目录范围包括后代路径，进程还绑定命令约束。
- `d`、空输入或其他未识别输入：拒绝本次调用。拒绝结果会返回模型，模型可以调整方案。

**这是应用层 policy/approval，不是 OS sandbox。** 文件工具的路径检查不会限制已获准进程的全部行为：进程可能访问工作区外的文件或网络。`shell` 检查工作目录并审批调用，但不提供操作系统级文件、网络隔离；也没有针对 Git、发布或部署的独立阶段识别机制。

### 命令允许规则

只有明确配置的匹配规则或已有授权才让命令免于询问。若希望允许在工作区根目录执行精确的 `dotnet test`，可以把以下项放入 `commandRules`：

```json
{
  "mode": "direct",
  "executable": "dotnet",
  "arguments": ["test"],
  "workingDirectory": "."
}
```

`direct` 模式直接启动 executable，不解释管道、重定向等 shell 语法。规则逐项匹配参数，单个参数内支持 `*` 和 `?`；工作目录精确匹配，不自动放行子目录。上述规则不匹配额外带参数的 `dotnet test SomeProject.csproj`。

显式 `shell` 模式的规则使用 `shell`、`command` 和 `workingDirectory`，精确匹配 shell 类型、完整命令文本和目录。构建与测试会执行仓库代码，允许规则应针对你信任的项目设置。

进程分别捕获 stdout/stderr，以 head + tail 裁剪模型输出；发生截断时保留本地完整输出 artifact 并返回路径。真实运行会从子进程环境中移除已配置的 API key 环境变量，并对已知密钥值脱敏；这不是通用敏感信息检测。

## 上下文压缩如何工作

Context Manager 保留运行期间的原始消息历史，并为每次模型请求构建单独的视图：

```text
系统指令与原始任务
  + StructuredState（已有摘要）
  + 近期完整回合
  + 当前回合
```

先裁剪模型视图中的超长工具结果，再按预算选择旧完整回合，通过额外的 Chat Completions 请求生成结构化 JSON。摘要请求不携带工具定义；工具调用及对应结果整体折叠，当前回合和最新完整回合保留。一次普通请求前可以连续压缩多批历史。

StructuredState 包含 `Goal`、`Constraints`、`Decisions`、`FilesInspected`、`FilesModified`、`CommandsAndResults`、`PendingWork`。提交摘要时检查格式、压缩收益和已有状态保留规则：

- 已有 `Goal` 非空时不可清空。
- 已记录的文件、命令结果和决策必须保留。
- 每个已有待办必须保留，或以 `completed: <item>` 明确关闭；约束允许更新。
- 校验失败不应用该批摘要，不破坏原始历史；最终估算仍超出窗口时终止任务，不发送该普通请求。

后续压缩采用“旧摘要 + 新进入折叠范围的历史 → 新摘要”。已有摘要会再次被模型改写，校验不能保证首次事实提取完整或多次摘要语义无损。token 数使用字符规则估算，当前未用服务端 usage 校准，也不保证是实际 token 数的上界。

原始消息保存在进程内存中，压缩不删除这些消息；但工具在返回结果前可能已执行自己的输出裁剪。session/audit 落盘及恢复尚未实现。

## 测试与 NativeAOT

默认测试使用 scripted/fake client 与本地模拟 SSE 服务，不调用付费模型，也不需要 API key。本地协议测试会监听 loopback 端口。

```powershell
dotnet test TinyHarness.Tests/TinyHarness.Tests.csproj
dotnet test TinyHarness.Tests/TinyHarness.Tests.csproj --no-restore --filter FullyQualifiedName~ContextCompactionTests
```

覆盖工具调用闭环、协议分片、参数错误、权限拒绝、路径边界、进程超时与取消、输出裁剪，以及压缩原子组、回滚、状态保留和同一步连续多批压缩。

当前已验证的 NativeAOT RID 为 **`win-x64`**。Windows 原生发布还需要 MSVC C++ 构建工具和 Windows SDK，例如 Visual Studio/Build Tools 中的“使用 C++ 的桌面开发”工作负载。其他 RID 尚未列为已验证目标。

在仓库根目录执行：

```powershell
dotnet publish TinyHarness.Cli/TinyHarness.Cli.csproj -c Release -r win-x64 --self-contained true -o artifacts/m6-nativeaot
./artifacts/m6-nativeaot/TinyHarness.exe smoke --config tinyharness.json
```

发布后的可执行文件名为 `TinyHarness.exe`，也可使用 `run --config <path> "任务"` 调用真实服务。

M6 收尾验证：2026-09-10 默认测试 **171/171 通过**，NativeAOT publish 未出现 trimming/AOT warning，原生产物 smoke 成功。具体发布命令及结果见 [M6 NativeAOT 验证记录](docs/m6-nativeaot-smoke.md)。这是一份阶段记录，当前工作区的验证结果以实际运行输出为准。

### 服务与模型验证范围

当前仓库的协议验证基于本地模拟 SSE 服务，覆盖自定义 endpoint、文本流、工具调用分片、请求工具定义、HTTP 错误和取消。尚无可据此列出的真实 tested providers/models 清单；使用“OpenAI-compatible”接口不代表所有服务和模型都已验证兼容。

## 代码结构

| 目录 | 职责 |
|---|---|
| `TinyHarness.Core/Agent` | 主循环、工具注册、步数与终止状态 |
| `TinyHarness.Core/ChatCompletions` | SDK 适配、消息与流事件、工具参数拼装 |
| `TinyHarness.Core/Tools` | 五个工具的 schema、准备与执行 |
| `TinyHarness.Core/Permissions` | 权限决策、会话授权与命令匹配 |
| `TinyHarness.Core/Runtime` | 工作区路径边界、进程和输出捕获 |
| `TinyHarness.Core/Context` | 预算估算、模型视图、结构化状态与压缩 |
| `TinyHarness.Core/Configuration` | JSON 配置加载与路径解析 |
| `TinyHarness.Cli` | 参数入口、审批界面、依赖组装、离线 smoke |
| `TinyHarness.Tests` | 单元、协议契约与集成测试 |

Agent Loop 通过 `IChatCompletionClient` 使用模型，SDK 类型留在协议适配层。工具显式注册；结构化状态使用 System.Text.Json source generation，配置使用无反射的手工绑定，以便持续验证 trimming 与 NativeAOT。

完整产品边界和里程碑见 [PLAN.md](PLAN.md)，仓库开发规范见 [AGENTS.md](AGENTS.md)。MVP 不包含 GUI、MCP、RAG、多 Agent、插件系统或 OS 级 sandbox。
