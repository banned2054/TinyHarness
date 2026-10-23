<!-- markdownlint-disable -->

<div align="center">

# TinyHarness.NET

<br>

<div>
    <a href="#-项目状态"><img alt="版本 0.0.1 原型" src="https://img.shields.io/badge/version-0.0.1-blue"></a>
    <a href="https://dotnet.microsoft.com/"><img alt=".NET 10" src="https://img.shields.io/badge/.NET-10.0-512BD4"></a>
    <a href="#platform-status"><img alt="NativeAOT win-x64" src="https://img.shields.io/badge/NativeAOT-win--x64-009688"></a>
</div>

<br>

<!-- markdownlint-restore -->

[English](../README.md) | 简体中文

</div>

**TinyHarness.NET** 是基于 .NET 10 的轻量本地 coding-agent harness。

它把语言模型连接到本地工作区，通过 CLI 讨论代码、检查文件、应用补丁和运行命令。模型适配通过 `Microsoft.Extensions.AI.IChatClient` 使用官方 OpenAI SDK，由配置显式选择 Chat Completions 或 Responses 协议。

## 🚧 项目状态

**0.0.1 定位为第一个原型预览版。** 基础任务可以跑通，项目已在实际使用中发现和修复问题，目前仍很粗糙，适合尝试与反馈。

终端界面尚未完善：权限请求、AI 回复与用户输入缺乏清晰区分。命令、配置和会话格式仍可能变化。已有测试和有限的实际使用记录不代表生产可用，也不代表完成了全面安全审查。

## ✨ 特性

- **会话与工作区** — 单次任务、流式多轮对话、本地会话历史与跨进程续聊。
- **文件与进程工具** — 列举文件、搜索文本、读取文件、应用补丁和运行命令，带审批、超时与有界输出。
- **上下文与项目规则** — 加载根目录和局部 `AGENTS.md`，统计 token，自动压缩模型上下文并保留完整本地历史。
- **模型与配置** — Provider profile、显式模型上下文窗口、环境变量或 Windows Credential Manager 密钥引用，以及离线配置检查。
- **附加集成** — 本机只读 MCP worker，以及可选的 Windows `shell` 工具进程隔离。

## 🗺️ 限制

- 输入编辑、输出排版和运行中控制仍很简陋，没有完整 TUI、多行编辑器或供工具使用的交互式终端。
- 模型回答、摘要和工作项状态都可能出错；应核对文件变更与实际命令结果，再判断任务是否完成。
- **审批属于应用层策略。** 未启用可选 Windows 沙箱时，获准的进程仍可访问工作区外文件和网络。沙箱仅覆盖经它启动的 `shell` 进程，不覆盖整个 Harness 或模型请求，且需要单独配置组件和机器环境。
- 会话原样保存对话和工具内容且不加密，可能包含秘密。续聊有严格的身份与格式检查，升级可能导致旧会话不兼容；不提供旧格式迁移。
- NativeAOT 仅有 Windows x64 验证记录；其他平台、广泛供应商兼容性、断电持久性与全面安全性尚未验证。

## 🚀 构建与运行

### 从源码构建

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。首次 restore 需要联网；默认测试与 smoke 使用模拟模型，不需要 API key。

```powershell
git clone https://github.com/banned2054/TinyHarness.git
cd TinyHarness
dotnet restore TinyHarness.slnx
dotnet test TinyHarness.Tests/TinyHarness.Tests.csproj --no-restore
dotnet run --project TinyHarness.Cli -- smoke --config tinyharness.json
```

smoke 在临时工作区执行文件与进程工具链路，不测试真实供应商或 Windows 沙箱。

### NativeAOT 发布

Windows 上还需要 MSVC C++ 构建工具和 Windows SDK：

```powershell
dotnet publish TinyHarness.Cli/TinyHarness.Cli.csproj -c Release -r win-x64 --self-contained true
```

可执行文件输出到 `TinyHarness.Cli/bin/Release/net10.0/win-x64/publish/TinyHarness.exe`，原生产物运行时不需要安装 .NET runtime。可选沙箱组件另行配置。

如需从其他工作区使用该程序，在仓库根目录把发布目录加入当前 PowerShell 会话的 `PATH`：

```powershell
$publishDirectory = (Resolve-Path .\TinyHarness.Cli\bin\Release\net10.0\win-x64\publish).Path
$env:PATH = "$publishDirectory;$env:PATH"
```

### Windows 沙箱组件与来源

可选 Windows 工具进程沙箱使用 [banned2054/codex](https://github.com/banned2054/codex) 构建的组件，这是本项目对 [OpenAI Codex](https://github.com/openai/codex) 的 fork。fork 为 TinyHarness 隔离了机器级命名，并包含对应的网络隔离改动。TinyHarness 的 C# Runtime 直接编排这些组件，不需要运行 Codex CLI 或 Codex Agent。

日常运行使用 `codex-windows-sandbox-setup.exe` 与 `codex-command-runner.exe`。请从该 fork 构建匹配组件，并在用户配置 `settings.windowsSandbox.setupExecutablePath` 和 `runnerExecutablePath` 中设置绝对路径；`dotnet publish` 不会生成或自动下载它们。构建与接入细节见[组件构建说明](windows-sandbox-dotnet-integration.md#构建产物已在本机构建验证)，配置和验收范围见[沙箱管理说明](windows-sandbox-management.md)。

## 📖 使用说明

以下示例假设发布后的程序已加入 `PATH`。产物文件名是 `TinyHarness.exe`，文档命令统一写成不带连字符的 `tinyharness`；Windows 查找命令时不区分大小写，写成 `TinyHarness` 也可用。在发布目录直接用 PowerShell 启动时写 `.\TinyHarness.exe`。项目没有提供 `tiny-harness` 可执行文件或别名。

### 配置模型

进入要处理的项目目录，然后运行配置向导：

```powershell
Set-Location C:\Code\YourProject
tinyharness init
tinyharness config show
tinyharness doctor
```

`init` 会询问 provider profile、endpoint、API key 来源、模型 ID 和上下文窗口。endpoint 是 SDK 基地址，通常以 `/v1` 结尾，不要填写完整 `/chat/completions` 或 `/responses` URL。模型需要支持流式输出和 tool calling。新建 profile 默认使用 Chat Completions；Responses 需要显式设置 `chatApi`。

密钥来源可以选择环境变量引用或 Windows Credential Manager。若选择环境变量，需要在启动 TinyHarness 的终端设置其值，例如 PowerShell 7.1+：

```powershell
$env:TINYHARNESS_API_KEY = Read-Host "API key" -MaskInput
```

用户配置位于 `%APPDATA%\tinyharness\user-config.json`，保存密钥引用而非密钥值。`TINYHARNESS_USER_CONFIG_DIR` 可覆盖该目录。`config show` 显示生效配置与来源，`doctor` 离线检查配置；`doctor --connect` 会实际请求模型，可能产生费用。

配置查找顺序为：显式 `--config <文件>`、当前目录的 `tinyharness.json`、用户配置、内置默认值；这些文件不会隐式合并。**仓库自带的 `tinyharness.json` 是使用占位 endpoint 的 smoke 示例。** 实际任务请从目标项目目录启动，或显式指定配置，避免它覆盖 `init` 创建的 profile。

### 开始任务或对话

```powershell
tinyharness run -- "解释这个项目的结构"
tinyharness chat
```

`chat` 中每次提交一行作为一个用户回合，可以先讨论问题，再明确要求修改。模型请求会向配置的供应商发送对话及相关工具内容，并可能产生费用。

首次交互启动时会询问工作区信任。只读工作区允许列举、搜索和读取；补丁与进程需要先信任工作区，再经过正常审批。非交互启动且没有已保存信任时保持只读。信任不等于自动批准命令，Windows 目录授权还可能单独触发 UAC。

工具审批时，`a` 单次允许，`s` 允许本次运行内匹配的请求，`d` 拒绝。运行期批准不会恢复到后续回合，批准前请核对具体动作。

### 思考展示与基础控制

```powershell
tinyharness chat --reasoning content
tinyharness run --reasoning status -- "解释这个项目"
```

默认 `status` 显示等待／思考耗时；`content` 在服务提供可读推理时显示内容，仅改变展示方式。内部压缩文本和 opaque 推理载荷不会展示。逐请求计时与用量明细保留在会话日志中。

`chat` 运行期间输入普通文本，会将它保存为待安全边界处理的补充。现有基础控制如下：

| 输入 | 行为 |
| --- | --- |
| `/pause` | 请求在模型／工具边界暂停 |
| `/resume` | 继续已暂停的运行 |
| `/cancel` | 取消当前运行并等待清理 |
| `/work` | 查看工作项陈述与未生效补充数量 |
| `Ctrl+C` | 取消活动回合；空闲输入时退出会话 |

这些只是有限控制，还没有通用命令系统；审批回复使用独立提示。空闲输入时可用 `/trust` 管理工作区信任。取消或重启后，已保存但未生效的补充不会自动重放。

### 查看历史与续聊

```powershell
tinyharness session list
tinyharness session show <session-id>
tinyharness chat --session <session-id>
```

`session list` 和 `session show` 严格离线，不调用模型、不执行工具，也不读取 API key。会话默认位于 `%LOCALAPPDATA%\tinyharness\sessions`，可由 `settings.sessionStoreDirectory` 或 `TINYHARNESS_SESSION_STORE_DIR` 覆盖；旧 `sessionDirectory` 字段不决定该存储位置。

续聊要求工作区、endpoint、模型、协议、当前内置提示词匹配，且已存协议内容通过校验。中断工具的副作用不确定时，写入和进程执行保持阻断，直到用户检查并确认。不要把原始会话文件当普通日志分享：检查输出会脱敏，但存储的对话内容不加密，也不做通用脱敏。

### 配置、预算与集成

完整语法见 `tinyharness help <命令>`：

| 命令 | 用途 |
| --- | --- |
| `provider list / add / use` | 管理 provider profile |
| `model list / add / use` | 管理显式配置的模型及上下文窗口 |
| `auth set <profile>` | 设置 profile 的密钥引用 |
| `config show / set` | 查看配置来源或调整支持的用户默认值 |
| `mcp` | 通过 stdio 提供只读 `ask_glm` worker |
| `sandbox status / provision / verify` | 检查、配置或验证可选 Windows 沙箱 |

`maxAgentSteps` 默认 `0`，表示不限普通步数。可信用户 `settings.runBudget` 默认每次运行最多 128 次逻辑模型请求（含压缩）和 256 次工具执行。累计 token 和总时长默认只统计、不设限（`maxTotalTokens`／`maxWallClockSeconds` 为 `0`），正数可启用相应上限；单工具超时与取消仍有效。详细契约见[运行预算](task-control-design.md)。

MCP worker 使用用户配置，将获准读取的工作区内容发送给模型，但不能写文件或执行命令；它是 MCP server，不是连接外部工具的 client。`sandbox provision` 会提权修改机器账户、ACL 与网络规则，`sandbox verify` 会执行真实隔离检查；使用前请阅读[沙箱配置与验收说明](windows-sandbox-management.md)。

<a id="platform-status"></a>

## 🖥️ 平台状态

| 平台 | 状态 |
| --- | --- |
| Windows x64 | 主要开发与原型使用平台，已有 NativeAOT 和限定范围的真实使用／隔离记录 |
| 其他 RID | 尚未完成分发或日常使用验证 |

## 📚 文档

- [许可证](../LICENSE)：项目采用 Apache-2.0；[NOTICE](../NOTICE) 与[第三方许可](../THIRD-PARTY-LICENSES.txt)保留组件归属和条款。更新方式见[维护说明](third-party-notices.md)。
- [更新日志](CHANGELOG.md)：中英文版本说明与已知限制。
- [会话持久化](session-persistence-design.md)、[项目规则](project-instructions-design.md)、[行为提示词](system-prompt-design.md)与[上下文压缩](context-compaction-design.md)：契约和验证记录。
- [任务控制与预算](task-control-design.md)、[交付证据](m16-verification.md)。
- [Windows 沙箱管理](windows-sandbox-management.md)：配置前提、范围和限制。
- [打包与发布](releasing.md)：GitHub Actions 构建、固定沙箱来源、ZIP 结构和 Release 草稿。
- [当前接力入口](current-progress.md)：当前工作与历史本机证据。
- [历史计划](../PAST_PLAN.md)：MVP 至 M17 的归档记录；[PLAN.md](../PLAN.md) 暂作归档入口，当前没有制定新的总路线。
- [AGENTS.md](../AGENTS.md)：仓库开发规则。

## 🤝 参与贡献

欢迎聚焦的问题反馈与修复，请附复现步骤、相关命令和脱敏输出。默认测试保持离线，并区分已验证结果与推测；仓库工作规则见 [AGENTS.md](../AGENTS.md)。
