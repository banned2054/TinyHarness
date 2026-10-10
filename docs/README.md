# TinyHarness.NET

[**English**](../README.md) | 简体中文

一个基于 .NET 10 的轻量本地 coding-agent harness。通过统一的 `Microsoft.Extensions.AI.IChatClient` 边界（底层官方 OpenAI SDK）调用模型，由配置显式选择 Chat Completions（缺省）或 Responses 协议，让模型在工作区内检查文件、提出补丁和运行命令，并由应用执行参数校验、权限审批、工具调度与上下文管理。

项目重点是把 Agent 的执行链路做小、做完整、做得可解释：模型负责提出下一步行动，Harness 负责准备、授权和执行。CLI 是当前入口，NativeAOT 是持续验证的构建约束。

## 当前状态

M7 的持久化与演示基础已实现：每次运行会在 `artifacts/runs`（或配置的 `sessionDirectory`）写入可检查的 JSONL 审计和 JSON session 快照。M8 的配置命令可管理 provider profile 和模型、显示生效配置并执行离线 doctor 检查。M9 增加了供 Codex 使用的一次性只读 MCP worker。M10 把模型调用统一到 `Microsoft.Extensions.AI.IChatClient`，`chatApi` 显式选择 Chat Completions/Responses，已完成离线验证。离线 fake 验收已通过，配置的 HTTPS endpoint 与 `glm-5.3` 已通过直接 stdio 和 Codex MCP 客户端调用验收。

| 能力 | 当前实现 |
|---|---|
| 模型协议 | `Microsoft.Extensions.AI.IChatClient` + 官方 OpenAI SDK；`chatApi` 选择 Chat Completions（缺省）或 Responses；SSE 文本、tool-call 参数分片、usage 与 Responses reasoning 回传 |
| Agent Loop | 一轮多个工具调用、顺序执行、结果回传、最大步数、失败和取消处理 |
| 文件工具 | `list_files`、`search_text`、`read_file`、`apply_patch` |
| 进程工具 | `shell` 的直接进程与显式 shell 模式；stdout/stderr、退出码、超时、进程树终止和输出裁剪 |
| 权限 | `Allow` / `Ask` / `Deny`；单次授权、会话授权与命令允许规则 |
| 上下文 | token 估算、工具结果裁剪、近期完整回合、结构化摘要、连续多批压缩 |
| 验证 | fake-client 离线测试、本地模拟 SSE 协议测试、命令解析测试、`win-x64` NativeAOT smoke |
| 持久化 | 每次运行生成 `<runId>.audit.jsonl` 与 `<runId>.session.json`；落盘前脱敏已配置的已知密钥和明确支持的敏感字段 |
| MCP worker | 本机 stdio 服务只注册 `ask_glm`；每次请求使用独立历史、固定工作区、只读工具和可信预算 |

`run` CLI 每次调用执行一个任务，显示审批、压缩提示和最终结果；`mcp` 串行处理相互独立的 `ask_glm` 请求。管理命令不会把输入发送给模型。交互式多轮会话、逐 token 终端文本展示和会话恢复尚未提供。

## 快速开始：先运行离线 smoke

以下命令使用 PowerShell，在 Windows 上执行。需要 .NET 10 SDK；首次还原 NuGet 依赖需要网络，测试和 smoke 本身不需要真实模型服务或 API key。

```powershell
git clone https://github.com/banned2054/TinyHarness.git
cd TinyHarness
dotnet restore TinyHarness.slnx
dotnet test TinyHarness.Tests/TinyHarness.Tests.csproj --no-restore
dotnet run --project TinyHarness.Cli -- smoke --config tinyharness.json
```

仓库中的 [tinyharness.json](../tinyharness.json) 使用占位 endpoint 和 smoke 模型名，可以直接用于离线 smoke，不能直接用于真实模型调用。

Smoke 会创建无第三方包的临时 .NET 10 控制台 fixture。固定检查程序起初观察到错误的 `Calculator.Add` 结果 `-1` 并输出 `CHECK_FAIL`；脚本模型只检查和修改 `Calculator.cs`，重新构建后观察到结果 `5` 与 `CHECK_PASS`。fixture 使用空包源配置和本机 .NET 10 SDK/targeting pack 正常 restore；构建与检查统一走结构化 direct 模式（`dotnet build` 与 `dotnet <fixture.dll>`），另有独立步骤覆盖显式 shell。成功时输出包含：

```text
status       : Completed
steps        : 17
toolExecs    : 16
```

它验证 Harness 的执行流程；真实模型的修复能力和压缩效果需要单独验证。压缩不变量与连续多批压缩由离线测试覆盖。

## 通过 CLI 配置

首次使用不需要手工编辑 JSON：

```powershell
dotnet run --project TinyHarness.Cli -- init
dotnet run --project TinyHarness.Cli -- config show
dotnet run --project TinyHarness.Cli -- doctor
```

引导会把命名 provider profile、endpoint、API key 引用、模型和显式上下文窗口写入平台用户配置（Windows 默认是 `%APPDATA%\tinyharness\user-config.json`）。API key 永远不会写入 JSON。使用 `auth set <profile> --env <VAR>` 保存环境变量引用，或使用 `auth set <profile> --store` 保存到 Windows Credential Manager。`doctor` 默认离线；`doctor --connect` 才会执行联网检查，并可能产生费用。

其他管理命令：

```text
tinyharness provider list|add <name>|use <name>
tinyharness model list|add <id> --context-window <tokens>|use <id>
tinyharness auth set <name> [--env <VAR>|--store]
tinyharness config set <key> <value>
```

## 使用只读 MCP worker

先用上面的配置命令设置默认 provider profile、模型、上下文窗口和凭据，再启动 stdio 服务：

```powershell
dotnet run --project TinyHarness.Cli -- mcp
```

MCP 进程只读取 TinyHarness 用户配置（路径见 `config show`），使用其中的默认 provider profile 和显式选择的模型；不会读取目标工作区的 `tinyharness.json`，也不会采用其中的 `commandRules`。工作区在服务启动时固定：默认是进程当前目录，也可以在用户配置的 `settings.worker.workspaceRoot` 中设置（相对路径按进程当前目录解析）。

服务只公开 `ask_glm`，使用只读文件工具在固定工作区内列举、搜索和读取，并应用 focus 路径收窄与敏感文件排除。它不能修改文件或运行命令。允许读取的文件内容会发送到所选模型 endpoint。MCP 协议以 UTF-8、逐行 JSON 通过标准输入输出传输；启动诊断只写标准错误。收到 `notifications/cancelled`、客户端关闭输入或 Ctrl+C 时，会取消对应的模型与文件操作。请求串行处理，每次都建立新的历史、权限状态和预算。

worker 限额来自用户配置的 `settings.worker`，MCP 请求不能设置或提高这些值。默认值与硬上限如下：

| 设置项 | 默认值 | 硬上限 |
|---|---:|---:|
| `runTimeoutSeconds` | 120 秒 | 600 秒 |
| `maxAgentSteps`（模型请求数） | 6 | 24 |
| `defaultToolTimeoutSeconds` | 20 秒 | 120 秒 |
| `maxTaskPackageCharacters` | 12,000 | 32,000 |
| `maxToolCalls` | 12 | 40 |
| `maxToolOutputCharacters` | 24,000 | 128,000 |
| `maxContextTokensPerRequest` | `min(24,000, 上下文窗口 − 4,096)` | 256,000，且必须低于配置模型窗口减去 4,096 |
| `maxCumulativeContextTokens` | `min(32,000, 上下文窗口 − 4,096)` | 256,000 |
| `maxModelResponseCharacters` | 12,000 | 64,000 |

`WorkerResult` 文本另有 Core 强制的 16,000 字符上限。只在用户自己的配置中调整可信设置；不要把这些值放入项目的 `tinyharness.json`。

未指定 `--config` 时不做隐式合并，按顺序使用第一个存在的来源：当前目录 `tinyharness.json`、用户配置、内置默认值。`config show` 会显示生效来源、绝对查找路径和密钥可用性，但不会显示密钥值。使用 `--help` 或 `help <command>` 查看具体语法。测试和便携运行可通过 `TINYHARNESS_USER_CONFIG_DIR` 覆盖用户配置目录。

## 连接真实模型

准备一个 JSON 配置文件，例如 `artifacts/live.json`。`artifacts/` 已被 Git 忽略，可用于本地配置；先创建该目录，再保存下面的内容，并替换 endpoint、model、工作区及对应的窗口参数。

```json
{
  "endpoint": "https://your-provider.example/v1",
  "model": "your-tool-capable-model",
  "chatApi": "chat-completions",
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

`endpoint` 是 SDK 基地址，例如以 `/v1` 结尾；不要填写完整的 `/v1/chat/completions` URL。模型需要支持所选协议——流式 Chat Completions（缺省）或 Responses——以及 function/tool calling；`chatApi` 显式选择协议，不根据模型名称推断。窗口大小由配置显式声明，不根据模型名称推断；示例数值不代表任何具体模型的能力。

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

每次真实 `run` 会在 `sessionDirectory` 中写入审计与 session 快照。审计在运行期间追加记录准备摘要、权限结论、工具结果元数据和终止状态；其中 `run.completed` 表示 Agent 执行已到达终态，因为它先于快照写入，所以不能证明两份文件都保存成功。CLI 只输出实际存在的持久化文件路径。快照保存完整消息历史、结构化状态和最终结果，仅用于检查，不提供恢复或副作用重放。

按 `Ctrl+C` 取消任务。正常完成返回 `0`，任务失败或达到步数上限返回 `1`，缺少提示词返回 `2`，正常运行路径中的任务取消返回 `130`。

### 配置项

| 字段 | 默认值 | 含义 |
|---|---|---|
| `endpoint` | 空 | 真实服务的 HTTP(S) 基地址 |
| `model` | 空 | 模型标识；真实运行必须填写 |
| `chatApi` | `chat-completions` | 模型协议传输：`chat-completions` 或 `responses`；显式选择，不根据模型名推断 |
| `apiKeyEnvironmentVariable` | 空 | 保存 API key 的环境变量名称 |
| `apiKeyCredentialTarget` | 空 | Windows Credential Manager 中保存 API key 的目标名；只保存引用，不保存密钥 |
| `contextWindowTokens` | `128000` | 声明的上下文窗口 |
| `reservedOutputTokens` | `8000` | 预算中为输出预留的空间；当前不作为 API 输出上限发送 |
| `compactionThreshold` | `0` | 触发压缩的总估算 token 阈值，含预留输出；`0` 使用窗口大小 |
| `maxAgentSteps` | `40` | 普通模型请求的最大步数，摘要请求不计入该步数 |
| `defaultToolTimeoutSeconds` | `120` | 工具默认超时秒数 |
| `workspaceRoot` | 当前目录 | 文件工具边界与命令工作目录的根 |
| `sessionDirectory` | `artifacts/runs` | 每次运行的 audit JSONL 与 session JSON 输出目录 |
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

进程分别捕获 stdout/stderr，以 head + tail 裁剪模型输出；发生截断时保留本地完整输出 artifact 并返回路径。真实运行会从子进程环境中移除已配置的 API key 环境变量。持久化会精确替换已配置的已知密钥值（重叠时优先替换较长值），并脱敏明确支持的赋值/JSON 敏感字段，例如 API key、access token、token、password 和 secret；这不是通用秘密检测，不保证识别任意敏感信息。

## Windows 沙箱管理命令

`tinyharness sandbox` 是 Windows 工具进程沙箱的显式管理入口，只读取用户配置中的可信设置 `settings.windowsSandbox`，不发送任何输入给模型。普通入口（`run`、`smoke`、`doctor`、`mcp`）不会触发 provisioning、账户修复或 UAC；机器级修改只能通过 `sandbox provision` 显式发生。

### sandbox status（严格只读）

```powershell
tinyharness sandbox status
```

打印 Enabled、policy、setup/runner/home 路径、本 build 期望的协议版本（setup v5 / IPC v6），并运行就绪检查：setup/runner 组件文件、`.sandbox\setup_marker.json`（version==5）、`cap_sid` 表与 `.sandbox-secrets\sandbox_users.json`。不创建任何目录或文件、不提权。退出码：未配置或就绪为 `0`；报告任何问题为 `1`。未配置 windowsSandbox 时打印启用指引。

### sandbox provision（显式机器级修改）

```powershell
tinyharness sandbox provision [--yes]
```

校验配置并构造 provisioning payload（路径缺失、非绝对或 payload base64 超过约 23000 字符都会在提权之前拒绝），打印机器级副作用清单并请求确认，然后以 UAC 运行 `codex-windows-sandbox-setup.exe`（TinyHarness 独立命名空间 fork 构建）一次（有界等待 10 分钟）。副作用包括：本地组 `TinyHarnessUsers`；本地账户 `TinyHarnessOffline`/`TinyHarnessOnline`（随机密码、永不过期）；注册表隐藏账户；防火墙规则与 offline 账户的 WFP BLOCK 过滤器；sandbox home 目录 ACL 与 write roots 授权。这套机器级命名与 Codex 发布版完全分栈（账户、组、防火墙、WFP、mutex、注册表互不共享），但共存行为尚未验收；同一 fork 命名空间内重复 provisioning 仍会重置账户密码、影响本机其他使用该命名空间的 home。没有卸载或回滚。

确认语义：默认交互确认（默认否）；`--yes` 跳过确认；非交互且无 `--yes` 直接报错退出、不提权。用户在 UAC 对话框取消、超时/取消（状态未知，请用 `sandbox status` 检查）、helper 非 0 退出（读取 `setup_error.json` 报告错误码与消息）均退出 `1`。退出 `0` 仅当 helper 成功且事后就绪检查通过。每次提权尝试（含拒绝、失败、超时/取消）向 `<sandboxHome>\.sandbox\tinyharness-provision.jsonl` 追加一行 JSON 审计（UTC 时间、outcome、退出码、mode、refreshOnly、payload SHA-256、截断的错误摘要；不含完整 payload 或凭据），写入尽力而为、失败仅告警。

### sandbox verify（opt-in 隔离验收）

```powershell
tinyharness sandbox verify [--yes] [--case <name>[,<name>...]] [--workspace <path>]
```

这是真实系统测试：在专用验收工作区（默认 `%TEMP%\TinyHarness\sandbox-verify-<时间戳>`，`--workspace` 可覆盖）内按固定矩阵执行真实沙箱命令，每条命令一次 ACL refresh（普通权限）与一个 runner 进程。前置条件：沙箱已配置、`enabled: true` 且就绪检查通过；否则拒绝执行并提示先 `sandbox provision`，绝不自动 provisioning、绝不回退宿主执行，本命令自身也不修改机器配置（账户/ACL/注册表/防火墙）。应在获得授权、可丢弃或受控的 Windows 环境执行。

固定矩阵 15 个用例（`--case` 按稳定名称选择子集，逗号分隔）：`identity-whoami`（沙箱内 whoami 为 offline 账户）、`deny-probe`（deny-read ACL 探针：退出 0=生效、20=隔离失效、21=探针配置错误）、`write-denied-outside-roots`（写根之外写入被拒）、`readonly-workspace-write-denied`（只读策略拒工作区写入）、`metadata-protected`（`.git` 等元数据目录只读）、`workspace-write-allowed`（写入正对照）、`network-denied`（非回环连接被阻为门控，回环仅作观测）、`exit-natural-23`、`exit-natural-192`（自然退出绝不判为超时）、`timeout-finite`（有限超时 192,true）、`terminate-active`（运行中取消）、`cancel-startup`（启动阶段取消）、`runner-broken-pipe`（runner 被外部击杀后不补造退出码）、`large-output`（大输出截断与完整 artifact）、`host-exit-reclaim`（宿主被 Kill 后无镜像幸存）。全矩阵有总期限；单用例失败记录后继续其余用例。

确认语义与 provision 一致：默认交互确认（默认否）；`--yes` 跳过；非交互且无 `--yes` 直接报错退出。退出码：`0` 仅当全部所选用例 PASS 且无 INDETERMINATE/SKIPPED；`4` 存在 FAIL、INDETERMINATE 或 SKIPPED；`1` 未配置/未启用/未就绪/组合失败；`2` 参数用法错误。控制台打印逐例结果表与工作区产物路径；完整报告（时间戳、机器与 OS、离线账户名、策略种类、组件路径/大小/mtime 与 marker/setup/IPC 版本、逐例判定、limitations 局限清单）写入 `<sandboxHome>\.sandbox\tinyharness-verify-<时间戳>.json`。验收报告仅作为“新二进制已通过最小测试”的依据，不构成完整隔离验收或与 Codex 共存已验证的证明。另有一个隐藏的 `sandbox verify-worker` 子命令供 host-exit 用例内部使用，不面向用户。

### 组件打包约定

两个日常组件 exe 文件名固定为 `codex-windows-sandbox-setup.exe` 与 `codex-command-runner.exe`，来自 Codex 仓库 Windows 沙箱的 **TinyHarness 独立命名空间 fork** 构建（文件名、payload 字段、DPAPI、IPC 协议与上游一致；机器级账户/组/防火墙/WFP/桌面前缀为 TinyHarness 专属；验收另用 `codex-windows-managed-deny-probe.exe`；`codex-windows-sandbox-service.exe` 首期不使用、不安装服务）。TinyHarness 不安装、不搜索它们：用户把 exe 放在任意自选的绝对路径，并在 user-config.json 的 `settings.windowsSandbox` 配置 `setupExecutablePath`、`runnerExecutablePath`；`sandboxHome` 可省略，缺省使用独立固定默认 `%LOCALAPPDATA%\tinyharness\windows-sandbox-home`（与 Codex 的 home 完全独立），也可显式指定绝对路径。配置后运行 `sandbox provision`；`enabled: true` 后 `run` 才会使用沙箱。marker 或凭据中仍为旧 Codex 用户名（`CodexSandboxOffline`/`CodexSandboxOnline`）的 home 会被状态检查、执行与 provisioning 一律拒绝复用（fail closed），需换独立 home 重新 provisioning。组件 exe 没有 PE 版本资源（ProductVersion/FileVersion 为空），因此版本权威校验是三处 JSON 数值：payload `version==5`（SETUP_VERSION）、runner 帧协议 `version==6`（IPC_PROTOCOL_VERSION）、setup marker `version==5`，加上 `cap_sid` 与 `sandbox_users.json` 的可解析检查；不匹配一律拒绝执行（fail closed，不回退宿主）。UAC 启动路径（`UseShellExecute=true`）无法使用环境分块通道，payload base64 超过约 23000 字符时 provision 直接拒绝并提示减少 `additionalWriteRoots`。技术细节见 [Windows 沙箱接入方案](windows-sandbox-dotnet-integration.md)。

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

后续压缩采用“旧摘要 + 新进入折叠范围的历史 → 新摘要”。已有摘要会再次被模型改写，校验不能保证首次事实提取完整或多次摘要语义无损。token 数基于字符规则估算，并按最近一次服务端真实 usage 的钳位比值校准（仍属估算，不保证是实际 token 数的上界）；缺少真实 usage 时保持纯估算。每步真实 usage 与结束原因会写入审计日志。

原始消息保存在进程内存中，压缩不删除这些消息；但工具在返回结果前可能已执行自己的输出裁剪。完成的运行会把这些消息和结构化状态写入 session；不支持恢复或重放。

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
dotnet publish TinyHarness.Cli/TinyHarness.Cli.csproj -c Release -r win-x64 --self-contained true -o artifacts/m7-nativeaot
./artifacts/m7-nativeaot/TinyHarness.exe smoke --config tinyharness.json
```

发布后的可执行文件名为 `TinyHarness.exe`，也可使用 `run --config <path> "任务"` 调用真实服务。

M7 验证（2026-09-11）：默认测试 **178/178 通过**；托管 smoke 在仓库根目录和仓库外目录均成功；隔离目录中的 `win-x64` NativeAOT publish 没有 trimming/AOT warning；本次新生成的原生 EXE 也从仓库外目录通过 smoke。

M8 验证（2026-09-18）：默认测试 **225/225 通过**；CLI 无警告构建，`win-x64` NativeAOT publish 没有 trimming/AOT warning，发布后的原生程序从仓库外通过 `--help`、离线 `doctor` 和既有 smoke。使用隔离用户配置目录验证了 `init`、`config show/set`、provider/model 管理和环境变量凭据引用，并补充了这些命令路径的执行回归测试。尚未把任何真实 provider/model 列入 tested 清单。

M9 本机实现、离线/NativeAOT 验证及真实 endpoint 检查（2026-09-23）：初始全套测试 **367 项通过**；MCP `_meta` 兼容修复后的定向测试 **21/21 通过**。`win-x64` NativeAOT publish、发布产物 CLI smoke 与 MCP stdio initialize/tools-list smoke 均通过。真实 `glm-5.3` HTTPS endpoint 调用经由直接 stdio 与 Codex MCP 完成，并返回带行号证据的结论。见 [M9 验证记录](m9-demo.md)。

M10 统一模型协议（2026-10-08）：模型调用迁移到 `Microsoft.Extensions.AI.IChatClient`（OpenAI SDK 2.14.0 + Microsoft.Extensions.AI.OpenAI 10.10.1），`chatApi` 显式选择协议，Responses 的 reasoning/item id/encrypted content 在历史中回传；默认测试 **408/408 通过**，无警告构建，`win-x64` NativeAOT publish 无 trimming/AOT warning，发布产物从仓库外通过 `--help`、离线 `doctor` 和 smoke。离线契约测试覆盖两种协议；不新增任何真实 provider/model 结论。见 [M10 验证记录](m10-model-protocol.md)。

M11 Windows 沙箱（2026-10-10，M11.1–M11.4）：M11.4 交付 `sandbox status`/`sandbox provision` 管理入口（见上文"Windows 沙箱管理命令"）。默认测试 **532/532 通过**（新增 28 个管理入口测试，全部离线，不启动真实 setup.exe、不触发 UAC）；`TinyHarness.Cli` 构建 0 警告；`win-x64` NativeAOT publish 无 trimming/AOT warning；发布产物从仓库外通过 `--help`、`sandbox status`（只读，本机未配置 windowsSandbox 如实报告并退出 0）、离线 `doctor` 与既有 smoke（Completed/17 steps/16 toolExecs），全程无 UAC、无机器修改；`sandbox provision --yes` 在未配置时于提权前拒绝（exit 1）。隔离验收（M11.5）未开始，完成前不宣称沙箱可用。

M11.5 验收工具（2026-10-10）：交付 `sandbox verify` 隔离验收入口（见上文"Windows 沙箱管理命令"）、Core 验收运行器（15 用例固定矩阵、总期限、报告落盘与进程存活扫描）与 **34 项新增离线测试**（fake backend/脚本化判定，不启动真实 setup/runner、不修改机器）。验证：`dotnet build TinyHarness.slnx --no-incremental` 0 警告 0 错误；`dotnet test TinyHarness.Tests/TinyHarness.Tests.csproj` **568 通过 / 0 失败 / 0 跳过**；`dotnet publish TinyHarness.Cli/TinyHarness.Cli.csproj -c Release -r win-x64 --self-contained true`（仓库外 `-o`）NativeAOT 0 trimming/AOT 警告；仓库外原生产物 `TinyHarness.exe` smoke：`--help` 与 `sandbox --help` 含 verify 用法、`sandbox verify`（无 `--yes`、无配置）exit 1 且输出「用户配置未设置 settings.windowsSandbox」、`sandbox verify-worker` 缺参 exit 2。边界声明：以上离线验证只覆盖验收工具自身逻辑（fake backend/脚本化判定）；真实隔离验收（provision 后执行 `sandbox verify`）尚未执行，M11.5 完成前不宣称沙箱可用。

M11.5 真实隔离验收（2026-10-10，用户授权，本机 Windows 11 + TinyHarness 独立命名空间 fork 组件执行）：user-config 配置 `settings.windowsSandbox`（组件指向 fork 构建的 `codex-windows-sandbox-setup.exe`/`codex-command-runner.exe`，home 使用固定默认 `%LOCALAPPDATA%\tinyharness\windows-sandbox-home`）→ `sandbox provision --yes` 一次通过（UAC 建组 `TinyHarnessUsers`、账户 `TinyHarnessOffline`/`TinyHarnessOnline`、防火墙/WFP/ACL，事后检查就绪）→ `sandbox verify --yes` 全矩阵 **14 PASS / 1 FAIL**（报告：`<home>\.sandbox\tinyharness-verify-20261010-133539.json`）。**通过**：身份（whoami=TinyHarnessOffline）、deny-read 探针（deny-probe exit 0）、写根外写入拒绝、只读策略拒工作区写入、`.git` 元数据保护、工作区写入正对照、自然退出 23/192、有限超时（192,true）、运行中取消（OCE）、启动取消、runner 外部击杀不补造退出码、约 4MB 大输出截断与 artifact、宿主 Kill 后遗弃回收。**FAIL（如实记录）**：`network-denied`——沙箱内 curl 对 `127.0.0.1` 与本机 LAN IP 的 TCP 连接均建立成功并收到 HTTP 应答（exit 0，按契约判隔离失效）；防火墙 BLOCK 规则（`tinyharness_sandbox_offline_*`，按用户作用域 LocalUserAuthorizedList）已安装且启用但未对沙箱账户进程生效，或本机 IP 连接走 Windows 环回快速路径绕过按远程地址匹配的规则——两者该用例无法区分，已记入报告 limitations；WFP 过滤器仅覆盖 ICMP/DNS/SMB，通用 TCP/UDP 断网完全依赖上述防火墙规则。**结论边界**：文件读写/元数据隔离与进程生命周期语义已在本机真实验证；断网未验证生效，网络隔离修复并复验通过前不宣称沙箱完整可用；与 Codex 发布版的共存未验收。真实验收还修复了三个此前离线测试无法暴露的缺陷：`ConvertStringSecurityDescriptorToSecurityDescriptorW` P/Invoke 缺第 4 个可选参数（首跑即原生 0xC0000005 崩溃）、`STARTUPINFOW` 结构布局错误（7 字段截断且偏移不符）、capability SID 的 cwd 契约（写根==命令 cwd 时必须用 `workspace_by_cwd` 表的 SID——setup 刷到目录 ACL 的正是它，C# 误用 `writable_root_by_path` 导致 workspace-write 写拒绝）；另修正三处验收 fixture 自身问题（cmd /c 载荷嵌套 CRT 转义引号在宿主与沙箱同样不可解析、waitfor 依赖 SMB mailslot 被断网过滤秒退改用 powershell sleeper、对照监听改回合法 HTTP/1.0 使 curl exit 0 语义无歧义）。回归门禁：默认测试 **576/576**（新增 cap SID 双表契约等测试）、`win-x64` NativeAOT publish 0 警告、仓库外原生产物 smoke 通过。

M11.5 断网修复与全矩阵闭环（2026-10-10 晚）：fork 侧新增按账户 SID 的 WFP 通用 BLOCK 过滤器（`tinyharness_wfp_tcp_connect_v4/v6`、`tinyharness_wfp_udp_connect_v4/v6`，ALE_AUTH_CONNECT 层，条件=账户 SID + 协议；内核强制，不依赖防火墙归因与远程地址匹配，覆盖环回），setup/runner 重建（SETUP_VERSION=5 / IPC v6 不变，C# 接入层零修改）并经显式 UAC 重新 provisioning 安装规则。期间实证了已记录的跨 home 危害：Rust 侧临时验收 home 先行 provisioning 轮换了共享账户密码，导致 TinyHarness 默认 home 的 DPAPI 凭据失效（`sandbox verify` 启动 runner 即报凭据错误）；随后用项目正式 `sandbox provision --yes` 在默认 home 重建一致性（`TinyHarnessOffline`/`TinyHarnessOnline` 密码再次轮换并同步）。修复后先复验 `network-denied` 单用例 PASS（沙箱内对 `192.168.8.248` 与 `127.0.0.1` 的 TCP 连接均立即失败，curl exit 7；报告 `tinyharness-verify-20261010-225744.json`），再重跑全矩阵 **15/15 PASS（exit 0，报告 `tinyharness-verify-20261010-232828.json`）**——M11.5 隔离验收在本机闭环：身份、deny-read/deny-write、元数据保护、工作区写入、断网、退出码/超时/取消语义、断管道、大输出、宿主遗弃回收全部真实验证通过。边界：验证覆盖本机单环境；与 Codex 发布版共存未验收；Rust 临时验收 home 仍留过期凭据（不可复用，需先重新 provisioning）。

### 服务与模型验证范围

当前仓库的协议验证基于本地模拟 SSE 服务，覆盖自定义 endpoint、Chat Completions 与 Responses 两种传输、文本流、工具调用分片、usage 与 reasoning 回传、请求工具定义、HTTP 错误和取消。另有 `glm-5.3` / HTTPS endpoint 的真实 MCP worker 调用记录，包含直接 stdio 和 Codex 客户端路径；这只验证该配置组合，其他服务和模型仍未验证兼容。使用“OpenAI-compatible”接口不代表所有服务和模型都已验证兼容。

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

完整产品边界和里程碑见 [PLAN.md](../PLAN.md)，仓库开发规范见 [AGENTS.md](../AGENTS.md)。MVP 基线不包含 GUI、MCP、RAG、多 Agent、插件系统或 OS 级 sandbox；后续 M9 已交付本机只读 MCP worker。
