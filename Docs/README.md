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

目前已实现：单任务 `run`、交互式多轮 `chat`、受保护会话存储（严格离线的 `session list` / `session show`，并支持 `chat --session <id>` 跨进程续聊）、离线优先的配置管理、只读 MCP worker 和可选的 Windows 沙箱管理。M14.1 已加入有界的根／局部 `AGENTS.md` 加载、工具边界与续聊刷新，以及首次非空发送才创建新 chat 会话。M14.2 加入统一基础行为提示词，覆盖自然语言任务范围、工具策略、验证与交付。默认测试与构建不调用付费模型；各阶段的证据与限制分别见下文。

M14.1 离线回归通过：**853 总、851 通过、2 项因符号链接创建权限跳过、0 失败**；scale 独立 1/1 通过，原预算不变。NativeAOT 编译与仓库外 smoke 通过，验证规则初始加载、更新、续聊来源完整性、历史和已确认 patch diff。证据：`artifacts/test-results/m14-regression.trx`、`artifacts/test-results/m14-scale.trx`、`artifacts/m14-instructions-scale-results.json`、`artifacts/m14-instructions-native-smoke.txt`。兼容、命令及未验证边界见[实施与验收记录](project-instructions-design.md#14-实施结果与本地验证)。

M14.2 工程验证通过：**860 总、858 通过、2 跳过、0 失败**，包含 7 个提示词接入用例；NativeAOT 与仓库外原生 smoke 也已通过。只维护当前内置提示词，创建和续聊均使用它；提示词不一致时仍严格拒绝续聊。详见[系统提示词设计与证据](system-prompt-design.md)，模型遵循能力尚待单独评估。

M15 已加入安全边界补充、暂停／继续／取消、带来源的 `work_items` 与可信累计运行预算。最终离线回归 **1013 总／1011 通过／2 项链接权限跳过／0 失败**；scale 独立通过，`win-x64` NativeAOT 与仓库外原生 smoke 通过。详见[任务控制与验收](task-control-design.md)。M15 的真实模型遵循尚未评估；M14 后续指定样本的验收见[压缩设计](context-compaction-design.md)。

## ✨ 特性

- **Agent Loop** — 流式回复、一轮多个顺序工具调用、可选步数上限、失败与取消处理。`maxAgentSteps` 缺省或为 `0` 时不限，正数启用上限。
- **工具** — `list_files`、`search_text`、`read_file`、`apply_patch` 与 `shell`（直接进程/显式 shell 模式），带超时、进程树终止和输出裁剪。
- **权限** — 每次调用经过 `Prepare → Authorize → Execute`：工作区内读取默认放行，补丁与进程默认询问（`a` 单次 / `s` 本次运行 / `d` 拒绝），可配置命令允许规则。
- **上下文** — token 预算按真实 usage 校准、工具结果裁剪、有界文本交接和带来源的真实用户原话。
- **运行控制与工作项** — 运行中输入文本作为已保存的补充，`/pause`、`/resume`、`/cancel`、`/work` 提供可选宿主控制；工作项完成陈述与实际工具事实分开。
- **累计预算** — 可信用户 `settings.runBudget` 限制逻辑模型请求（含摘要）与工具执行；总时长和累计 token 默认只统计，`maxWallClockSeconds`／`maxTotalTokens` 为 0 或缺省时不限，正数可分别启用。usage 缺失时明确估算，`BudgetLimitReached` 保留原因和计数。
- **项目指令** — 按固定预算加载根／局部 `AGENTS.md`，压缩后保留当前规则，在请求、审批和派发边界重新检查；规则变化使未启动的旧计划与批准失效，项目文件不能授予权限。新 `chat` 打开、空输入或 EOF 不创建会话，首次真实发送通过规则预检后才创建。
- **持久化** — `run` 和新会话 `chat` 共用受保护会话存储中的 `tinyharness.session-log/1` 追加日志，默认不再输出旧的全量 run snapshot/audit。历史 DTO 确定性保存 process／final 分类、有界脱敏工具输出，以及逐文件 patch 事实；工作区后来变化后仍能读取当时 diff，并区分 `confirmed` 与 `unknown`。执行意图保存失败不启动副作用；副作用结果未知时设置 NeedsInspection，模型视图回退到最近完整工具边界，未知副作用阻断写入与进程执行直到用户检查确认。历史按序号游标经共享 scanner 分页（每页最多返回 200 条 items，序列化 UTF-8 累计预算 8 MiB；扫描可读取整个捕获范围）；artifact 内容内联保存，单个上限 2 MiB、stdout/stderr 合计 4 MiB；批次上限 8 MiB，日志总量上限 256 MiB。不确定写入使 writer poison，strict 续聊读取拒绝尾部半行，历史检查可返回可靠前缀并给出 warning。会话可严格离线查看，并可在新进程经工作区/模型/协议与 opaque 完整性检查后续聊（`chat --session <id>`）。会话可能包含秘密且不被 Harness 加密；`session list` / `session show` 只输出脱敏检查视图，不展示 canonical 消息或 opaque 载荷。
- **MCP worker** — `tinyharness mcp` 启动本机只读 stdio 服务，向 Codex 等客户端提供 `ask_glm`。
- **Windows 沙箱** — 可选的机器级 `shell` 进程隔离，通过 `sandbox status / provision / verify` 显式管理。

## 🗺️ 限制

- 中断后仍有未知副作用结果的会话，续聊时模型视图回退到最近完整工具边界，副作用工具保持阻断直到用户检查工作区并确认（恢复时的 `c` / `k` 提示）。当前仅支持 `tinyharness.session-log/1`；旧 `.session.json` 快照和独立 audit 文件不再读取，也不提供迁移命令。
- Windows 沙箱只在单台机器上验证过，与 Codex 发布版共存未验收。
- NativeAOT 只验证了 `win-x64`；M14.1 的 publish 和原生 smoke 已通过。本机规则 leaf／目录链接创建测试因权限跳过。协议验证基于本地模拟 SSE 服务，旧实现另有单个真实 endpoint 的历史验证。
- M15 控制和工作项的真实模型遵循未验证；GUI 历史消费、断电持久性及新的 OS 隔离场景也未在本轮验证。已保存但未生效的补充可检查，不会在重启时自动重放。

## 🚀 构建与运行

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。首次 restore 需要网络；测试和 smoke 不调用真实模型、不需要 API key。

```powershell
git clone https://github.com/banned2054/TinyHarness.git
cd TinyHarness
dotnet restore TinyHarness.slnx
dotnet test TinyHarness.Tests/TinyHarness.Tests.csproj --no-restore
dotnet run --project TinyHarness.Cli -- smoke --config tinyharness.json
```

仓库自带的 [tinyharness.json](../tinyharness.json) 使用占位 endpoint：smoke 的目标是由脚本模型检查并修复一个刻意损坏的 fixture，重新构建并以 `status : Completed` 结束；本次 native smoke 结果见项目状态。

原生发布（另需 MSVC C++ 构建工具和 Windows SDK）：

```powershell
dotnet publish TinyHarness.Cli/TinyHarness.Cli.csproj -c Release -r win-x64 --self-contained true -o artifacts/nativeaot
```

### 命令

```text
tinyharness run [--config <file>] "任务"    单任务执行（也可省略 run 直接给任务）
tinyharness chat [--config <file>] [--session <id>]    交互式多轮会话；--session 续聊已保存会话
tinyharness session list | show <id>         严格离线的会话查看
tinyharness mcp                              只读 stdio MCP worker（ask_glm）
tinyharness smoke                            离线端到端自检
tinyharness init / config / provider / model / auth / doctor
tinyharness sandbox status | provision | verify    Windows 沙箱管理
```

`Ctrl+C` 取消当前执行；在 `chat` 中先取消当前回合，空闲时再按结束会话。退出码：`0` 完成、`1` 失败或步数／累计预算达限、`2` 缺少提示词、`130` 取消。`doctor` 默认离线，`doctor --connect` 才联网检查。完整语法见 `--help` 或 `help <命令>`。

`run` 和 `chat` 默认在终端同一行循环点号并更新当前请求耗时：`等待中... 12s`，
收到推理后显示 `思考中... 23s`。逐请求诊断和 usage 明细仅保留在会话日志，回合末仍汇总用量。
重定向输出仅在状态变化时提示一次。`--reasoning content` 在独立的“思考”区域流式展示可读文本，
跨片段脱敏已知密钥并转义控制字符。
内部压缩文本和 opaque 载荷不展示。该选项仅影响显示，续聊时也可重新选择。

```powershell
tinyharness chat --reasoning content
tinyharness run --reasoning status -- "解释这个项目"
```

### 配置

首次使用不需要手工编辑 JSON。`init` 会把 provider profile、endpoint、密钥引用、模型和上下文窗口写入用户配置（Windows 为 `%APPDATA%\tinyharness\user-config.json`）。认证配置只保存密钥引用——环境变量或 Windows Credential Manager——不保存密钥本体，密钥也不会被主动注入 `chat` 会话。会话默认使用受保护存储（`%LOCALAPPDATA%\tinyharness\sessions`），可通过 `settings.sessionStoreDirectory` 或 `TINYHARNESS_SESSION_STORE_DIR` 覆盖；`config.sessionDirectory` 不是默认位置。会话为保真续聊原样保存 canonical 对话内容，粘贴进会话的文本、会话读取的文件或模型返回的内容都可能包含秘密；Harness 不加密会话文件——“受保护”的存储位置只是路径与排他写约定，不代表加密、也不承诺其他本地进程不可读写。当前仅支持 `tinyharness.session-log/1`，不读取或迁移旧持久化格式。

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
| Windows x64 | 主要开发平台；本地统一验收完成：离线回归、scale、`win-x64` NativeAOT 发布与原生 smoke |
| 其他 RID | 尚未验证 |

## 📚 文档

- [PLAN.md](../PLAN.md) — 当前产品范围与 M12+ 路线
- [PAST_PLAN.md](../PAST_PLAN.md) — 归档的 MVP 与 M1–M11 计划
- [会话持久化设计](session-persistence-design.md) — 当前契约与本地验收矩阵
- [任务控制与运行预算](task-control-design.md) — M15 契约、兼容与本地验收
- [Windows 沙箱管理与验收记录](windows-sandbox-management.md)
- [Windows 沙箱接入方案](windows-sandbox-dotnet-integration.md)
- [M9 验证记录](m9-demo.md)、[M10 验证记录](m10-model-protocol.md)
- [AGENTS.md](../AGENTS.md) — 仓库开发规则

## 🤝 参与贡献

欢迎聚焦的 issue 和 PR。默认测试保持离线、不依赖 API key，并区分已实现与未验证的行为；工作规则见 [AGENTS.md](../AGENTS.md)。
