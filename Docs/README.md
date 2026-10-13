<!-- markdownlint-disable -->

<div align="center">

# TinyHarness.NET

<br>

<div>
    <a href="https://dotnet.microsoft.com/"><img alt=".NET 10" src="https://img.shields.io/badge/.NET-10.0-512BD4"></a>
    <a href="https://learn.microsoft.com/dotnet/core/deploying/native-aot/"><img alt="NativeAOT win-x64" src="https://img.shields.io/badge/NativeAOT-win--x64-009688"></a>
    <a href="#-构建与运行"><img alt="默认测试离线" src="https://img.shields.io/badge/tests-offline-green"></a>
</div>

<br>

<!-- markdownlint-restore -->

[**English**](../README.md) | 简体中文

</div>

一个基于 .NET 10 的轻量本地 coding-agent harness。模型调用统一走 `Microsoft.Extensions.AI.IChatClient` 边界（底层为官方 OpenAI SDK），由配置显式选择 Chat Completions（缺省）或 Responses 协议。模型负责提出下一步行动——检查文件、提出补丁、运行命令；harness 负责参数校验、权限审批、工具调度与上下文管理。

目标是把 Agent 执行链路做小、做完整、可解释：CLI 是当前入口，`win-x64` NativeAOT 是持续验证的构建约束。

> **这里的审批是应用层策略，不是 OS 沙箱。** 获准的进程仍可能访问工作区外的文件和网络；可选的 Windows 沙箱只覆盖 `shell` 工具进程。

## 🚧 项目状态

早期开发中，命令、配置和存储格式可能随里程碑变化。

目前已实现：单任务 `run`、交互式多轮 `chat`、受保护会话存储与严格离线的 `session list` / `session show`、离线优先的配置管理、只读 MCP worker 和可选的 Windows 沙箱管理。续聊已保存的会话、项目指令（`AGENTS.md`）加载和宿主强制的只读/实施模式属于计划，尚未实现。默认测试与构建不调用付费模型；真实链路只验证过一组组合（`glm-5.3` + 单个 HTTPS endpoint）。

## ✨ 特性

- **Agent Loop** — 流式回复、一轮多个顺序工具调用、步数上限、失败与取消处理。
- **工具** — `list_files`、`search_text`、`read_file`、`apply_patch` 与 `shell`（直接进程/显式 shell 模式），带超时、进程树终止和输出裁剪。
- **权限** — 每次调用经过 `Prepare → Authorize → Execute`：工作区内读取默认放行，补丁与进程默认询问（`a` 单次 / `s` 本次运行 / `d` 拒绝），可配置命令允许规则。
- **上下文** — token 预算按真实 usage 校准、工具结果裁剪、旧回合压缩为经过校验的结构化摘要。
- **持久化** — 每次运行写入 JSONL 审计和脱敏 session 快照；`chat` 会话原子提交到受保护存储，可严格离线查看。
- **MCP worker** — `tinyharness mcp` 启动本机只读 stdio 服务，向 Codex 等客户端提供 `ask_glm`。
- **Windows 沙箱** — 可选的机器级 `shell` 进程隔离，通过 `sandbox status / provision / verify` 显式管理。

## 🗺️ 限制

- 会话可离线查看，但尚不能续聊；续聊是下一个里程碑。
- Windows 沙箱只在单台机器上验证过，与 Codex 发布版共存未验收。
- NativeAOT 只验证了 `win-x64`；协议验证基于本地模拟 SSE 服务加上上述唯一真实 endpoint。
- `chat` 尚未用真实模型服务验证。

## 🚀 构建与运行

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。首次 restore 需要网络；测试和 smoke 不调用真实模型、不需要 API key。

```powershell
git clone https://github.com/banned2054/TinyHarness.git
cd TinyHarness
dotnet restore TinyHarness.slnx
dotnet test TinyHarness.Tests/TinyHarness.Tests.csproj --no-restore
dotnet run --project TinyHarness.Cli -- smoke --config tinyharness.json
```

仓库自带的 [tinyharness.json](../tinyharness.json) 使用占位 endpoint：smoke 中脚本模型检查并修复一个刻意损坏的 fixture，重新构建后运行以 `status : Completed` 结束。

原生发布（另需 MSVC C++ 构建工具和 Windows SDK）：

```powershell
dotnet publish TinyHarness.Cli/TinyHarness.Cli.csproj -c Release -r win-x64 --self-contained true -o artifacts/nativeaot
```

### 命令

```text
tinyharness run [--config <file>] "任务"    单任务执行（也可省略 run 直接给任务）
tinyharness chat [--config <file>]           交互式多轮会话
tinyharness session list | show <id>         严格离线的会话查看
tinyharness mcp                              只读 stdio MCP worker（ask_glm）
tinyharness smoke                            离线端到端自检
tinyharness init / config / provider / model / auth / doctor
tinyharness sandbox status | provision | verify    Windows 沙箱管理
```

`Ctrl+C` 取消当前执行；在 `chat` 中先取消当前回合，空闲时再按结束会话。退出码：`0` 完成、`1` 失败或步数达限、`2` 缺少提示词、`130` 取消。`doctor` 默认离线，`doctor --connect` 才联网检查。完整语法见 `--help` 或 `help <命令>`。

### 配置

首次使用不需要手工编辑 JSON。`init` 会把 provider profile、endpoint、密钥引用、模型和上下文窗口写入用户配置（Windows 为 `%APPDATA%\tinyharness\user-config.json`）。密钥只保存引用——环境变量或 Windows Credential Manager——永远不会写入 JSON。

```powershell
dotnet run --project TinyHarness.Cli -- init
dotnet run --project TinyHarness.Cli -- config show
```

### 连接真实模型

在 Git 忽略的 `artifacts/` 目录下保存一个 JSON 配置，例如 `artifacts/live.json`：

```json
{
  "endpoint": "https://your-provider.example/v1",
  "model": "your-tool-capable-model",
  "chatApi": "chat-completions",
  "apiKeyEnvironmentVariable": "TINYHARNESS_API_KEY",
  "contextWindowTokens": 128000,
  "workspaceRoot": "C:/Code/YourProject",
  "commandRules": []
}
```

`endpoint` 是 SDK 基地址（通常以 `/v1` 结尾），不要填完整的 completions URL；模型必须支持所选协议和 tool calling，`chatApi` 不根据模型名推断。

```powershell
$env:TINYHARNESS_API_KEY = Read-Host "API key" -MaskInput    # PowerShell 7.1+
dotnet run --project TinyHarness.Cli -- run --config artifacts/live.json "检查这个项目为什么测试失败并修复"
```

`run` 与 `chat` 会调用真实模型服务并可能产生费用；获准的工具会实际修改文件、启动进程。未指定 `--config` 时读取当前目录的 `tinyharness.json`。

## 🖥️ 平台状态

| 平台 | 状态 |
| --- | --- |
| Windows x64 | 主要开发平台；离线测试与 `win-x64` NativeAOT 发布已验证 |
| 其他 RID | 尚未验证 |

## 📚 文档

- [PLAN.md](../PLAN.md) — 当前产品范围与 M12+ 路线
- [PAST_PLAN.md](../PAST_PLAN.md) — 归档的 MVP 与 M1–M11 计划
- [Windows 沙箱管理与验收记录](windows-sandbox-management.md)
- [Windows 沙箱接入方案](windows-sandbox-dotnet-integration.md)
- [M9 验证记录](m9-demo.md)、[M10 验证记录](m10-model-protocol.md)
- [AGENTS.md](../AGENTS.md) — 仓库开发规则

## 🤝 参与贡献

欢迎聚焦的 issue 和 PR。默认测试保持离线、不依赖 API key，并区分已实现与未验证的行为；工作规则见 [AGENTS.md](../AGENTS.md)。
