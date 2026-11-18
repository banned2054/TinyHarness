<!-- markdownlint-disable -->

<div align="center">

# TinyHarness.NET

<br>

<div>
    <a href="https://github.com/banned2054/TinyHarness/releases/tag/v0.0.1"><img alt="版本 0.0.1" src="https://img.shields.io/badge/version-0.0.1-blue"></a>
    <a href="https://dotnet.microsoft.com/"><img alt=".NET 11" src="https://img.shields.io/badge/.NET-11.0-512BD4"></a>
    <a href="#platform-status"><img alt="NativeAOT win-x64" src="https://img.shields.io/badge/NativeAOT-win--x64-009688"></a>
</div>

<br>

<!-- markdownlint-restore -->

[English](../README.md) | 简体中文

</div>

**TinyHarness.NET** 是基于 .NET 11 的本地 Coding Agent，提供 CLI 和 Desktop。它将模型接入本地工作区，支持 Chat Completions 和 Responses。

## 🚧 项目状态

**v0.0.1** 是首个公开版本。命令、配置和会话格式可能变化，生产可用性和全面安全性尚未验证。

## ✨ 特性

- **会话** — 单次任务、流式对话、本地历史和跨进程续聊。
- **项目** — CLI 和 Desktop 共用项目身份，桌面提供项目侧栏和项目内草稿。
- **工具** — 列举、搜索、读取、应用补丁和运行命令，支持审批与超时。
- **上下文** — 按范围加载 `AGENTS.md`，统计 token 并自动压缩上下文。
- **模型** — 管理供应商、上下文窗口、密钥引用和独立代理。
- **集成** — 只读 MCP worker 和 Windows Shell 沙箱。

## 🗺️ 限制

- CLI 的编辑与运行控制较基础，工具没有交互式终端。
- 模型结果可能有误，请核对文件变更与命令输出。
- Windows 沙箱不可用时拒绝执行 Shell。沙箱只覆盖沙箱内的 Shell 进程，获批的宿主执行没有操作系统隔离。
- 会话文件未加密，可能包含秘密。升级前请备份，兼容性说明见[查看历史与续聊](#查看历史与续聊)。

## 🚀 构建与运行

### 下载 Windows x64 版本

从 [Release v0.0.1](https://github.com/banned2054/TinyHarness/releases/tag/v0.0.1) 下载 CLI ZIP 和 `SHA256SUMS.txt`。校验 SHA-256 后，将整包解压到工作区和沙箱写根之外。包内含沙箱组件，运行无需 .NET runtime。

将解压目录加入 `PATH`，在项目目录运行 `tinyharness init`。

### 从源码构建

安装 [.NET 11 SDK](https://dotnet.microsoft.com/download/dotnet/11.0)。首次还原依赖需要联网。

```powershell
git clone https://github.com/banned2054/TinyHarness.git
cd TinyHarness
dotnet restore TinyHarness.slnx
dotnet test TinyHarness.Tests/TinyHarness.Tests.csproj --no-restore
dotnet run --project TinyHarness.Cli -- smoke --config tinyharness.json
```

默认测试与 smoke 使用模拟模型。Smoke 覆盖文件和进程工具，不涉及真实供应商或 Windows 沙箱。

### NativeAOT 发布

安装 MSVC C++ 构建工具和 Windows SDK。在仓库根目录发布 CLI，并将输出目录加入当前 PowerShell 会话的 `PATH`。

```powershell
dotnet publish TinyHarness.Cli/TinyHarness.Cli.csproj -c Release -r win-x64 --self-contained true
$publishDirectory = (Resolve-Path .\TinyHarness.Cli\bin\Release\net11.0\win-x64\publish).Path
$env:PATH = "$publishDirectory;$env:PATH"
```

Desktop 打包见[发布指南](Guides/releasing.md)。

### Windows 沙箱组件与来源

沙箱使用来自本项目的 [Codex fork](https://github.com/banned2054/codex) 的 setup 和 runner。可使用 ZIP 随附的匹配组件，或[从源码构建](Archive/windows-sandbox-dotnet-integration.md#构建产物已在本机构建验证)。

在 `settings.windowsSandbox` 下填写 `setupExecutablePath` 和 `runnerExecutablePath` 的绝对路径，再运行 `tinyharness sandbox provision`。初始化需提权，会修改机器账户、ACL 和网络规则。具体步骤见[沙箱管理说明](Archive/windows-sandbox-management.md)。

## 📖 使用说明

以下示例使用 `PATH` 上的 `tinyharness`。从解压目录直接启动时，在 PowerShell 运行 `.\TinyHarness.exe`。

### 配置模型

```powershell
Set-Location C:\Code\YourProject
tinyharness init
tinyharness config show
tinyharness doctor
```

Desktop 可在「设置」中添加供应商与模型。选择支持流式输出和工具调用的模型，填写供应商基地址（通常以 `/v1` 结尾）。默认使用 Chat Completions，使用 Responses 时显式设置 `chatApi`。

密钥可用 Windows Credential Manager 或环境变量引用。选择环境变量时，在启动 TinyHarness 前设值，例如在 PowerShell 7.1+ 中运行以下命令。

```powershell
$env:TINYHARNESS_API_KEY = Read-Host "API key" -MaskInput
```

CLI 和 Desktop 默认共用 `%APPDATA%\tinyharness\user-config.json`，其中保存密钥引用。可用 `TINYHARNESS_USER_CONFIG_DIR` 更改目录。配置依次查找显式 `--config`、用户配置和内置默认值，各来源独立使用。仓库内的 `tinyharness.json` 是 smoke 示例，仅通过显式 `--config` 加载。`doctor` 离线检查，`doctor --connect` 会实际请求模型并可能产生费用。

可信用户设置 `settings.modelTimeouts` 的默认值以秒计。`streamIdleTimeoutSeconds` 为 300，`responseTimeoutSeconds` 为 0（关闭），`approvalTimeoutSeconds` 为 300。将流空闲超时设为 0 只关闭应用层计时，SDK 的网络超时仍生效。

### 会话自动命名

Desktop 保存回合后用首条输入生成标题。生成前显示输入摘要，失败则在之后的回合重试。

在「设置 → 通用 → 会话自动命名」中使用当前模型或指定模型。命名可能产生额外用量。包含 `session-title` 记录的日志需要支持该记录的版本。

### 网络设置

Desktop「设置 → 网络」可选系统代理、直连或已保存的 HTTP、HTTPS、SOCKS5 代理。供应商默认继承全局设置，也可单独指定。指定代理失败时不会切换其他路由。代理凭据保存在系统凭据存储中。

这些设置影响模型请求、模型目录查询和代理测试。Shell 工具使用独立的网络策略。手工配置时，将以下片段合并到用户配置。

```json
{
  "settings": {
    "network": {
      "defaultRoute": { "mode": "proxy", "proxyId": "local-mihomo" },
      "proxies": [
        { "id": "local-mihomo", "name": "本地 Mihomo", "protocol": "socks5", "host": "127.0.0.1", "port": 7890 }
      ]
    }
  },
  "profiles": [
    { "name": "openai", "network": { "mode": "proxy", "proxyId": "local-mihomo" } }
  ]
}
```

### 开始任务或对话

```powershell
tinyharness run -- "解释这个项目的结构"
tinyharness chat
```

无参数运行 `tinyharness` 也会进入聊天。提交请求即开始一个回合，供应商会收到对话和相关工具内容，可能产生费用。

CLI 会询问是否信任新工作区。未信任时仅可使用只读文件工具，打补丁和运行命令须先信任工作区，再按权限策略执行。审批时，`a` 单次允许，`t` 本次运行内允许，`s` 当前会话内允许，`d` 拒绝。可用范围以提示为准。

如果沙箱命令因目录权限未启动，可在 Desktop 空闲时使用「查看详情」或「修复沙箱目录权限…」。确认影响范围，修复成功后再重试命令。

### Desktop 权限与审批

默认使用「请求批准」。「帮我批准」将符合条件的请求交给 AI 审核。在「设置 → 权限与审批」配置默认模式、审核模型及常设授权。

- **沙箱基线**允许普通读取（含工作区外）、工作区写入和沙箱内 Shell。
- **沙箱扩展**需审批敏感路径、越界写入、受保护元数据写入、联网和额外 Shell 写根。常设联网和附加写根须显式确认。
- **宿主执行**（`executionScope=host`）每次都须单独批准，执行时没有操作系统隔离。

临时授权在重启后失效。旧配置与旧会话按各自提示确认迁移。详见[权限与审批](Guides/permission-modes.md)和[实施记录](permission-architecture-plan.md)。

### 思考展示与基础控制

```powershell
tinyharness chat --reasoning content
tinyharness run --reasoning status -- "解释这个项目"
```

`status` 显示等待与思考耗时。供应商返回可读推理时，`content` 展示其内容。运行中输入的普通文本会排队，等到安全的模型或工具边界再处理。

| 输入 | 行为 |
| --- | --- |
| `/new` | 创建并选中新会话 |
| `/sessions` | 列出已保存会话 |
| `/switch <会话ID>` | 用完整 ID 切换会话 |
| `/pause` | 请求在模型或工具边界暂停 |
| `/resume` | 继续已暂停的运行 |
| `/cancel` | 取消运行并等待清理 |
| `/work` | 查看工作项和待处理输入数量 |
| `/approval human\|ai` | 空闲时保存审批模式 |
| `Ctrl+C` | 取消当前回合，空闲时退出 |

运行中使用 `/new` 或 `/switch` 会先取消运行并等待保存。空闲时可用 `/trust` 管理工作区信任。取消或重启后，待处理输入不会自动重放。

### 查看历史与续聊

```powershell
tinyharness session list
tinyharness session show <session-id>
tinyharness chat --session <session-id>
```

`list` 和 `show` 离线运行。会话默认位于 `%LOCALAPPDATA%\tinyharness\sessions`，可用 `settings.sessionStoreDirectory` 或 `TINYHARNESS_SESSION_STORE_DIR` 更改。

续聊校验工作区、endpoint、模型、协议、内置提示词及存储内容。如果上次中断的工具可能已有副作用，先检查并确认，再继续写入或执行命令。原始历史和本地输出应妥善保管。诊断和审计输出会尽力脱敏。

旧版程序可能无法打开新版写入的日志。会话存储迁移需手动启动，迁移前关闭旧版程序并备份会话目录。命令见 `tinyharness help session`，细节见[备份、恢复与回退](session-storage-plan.md#54-备份恢复与回退)。

### 配置、预算与集成

完整语法见 `tinyharness help <命令>`。

| 命令 | 用途 |
| --- | --- |
| `provider list / add / use` | 管理供应商 |
| `model list / add / use` | 管理模型与上下文窗口 |
| `auth set <profile>` | 设置密钥引用 |
| `config show / set` | 查看或修改用户配置 |
| `mcp` | 通过 stdio 提供只读 `ask_glm` worker |
| `sandbox status / provision / verify` | 检查、初始化或验证 Windows 沙箱 |

CLI 用 `/models`，Desktop 用模型菜单，可在空闲时切换模型与推理强度。保存模型目录不会自动切换现有会话。更换 endpoint 来源后，复用旧密钥前需要确认。

`settings.runBudget` 默认每次运行最多 128 次模型请求和 256 次工具执行。累计 token 与总时长可另设上限。详见[运行预算](Archive/task-control-design.md)。

`AGENTS.md` 按明确文件目标的祖先目录加载。递归列举或搜索不会加载后代规则。详见[项目规则与诊断](Archive/project-instructions-design.md)。

MCP server 将获准读取的工作区内容发送给配置的模型，仅提供只读能力。`sandbox verify` 执行真实隔离检查，使用前请阅读[沙箱管理说明](Archive/windows-sandbox-management.md)。

<a id="platform-status"></a>

## 🖥️ 平台状态

| 平台 | 状态 |
| --- | --- |
| Windows x64 | 已验证 NativeAOT，有有限的实际使用与隔离记录 |
| 其他 RID | 分发和日常使用待验证 |

<a id="documentation"></a>

## 📚 文档

[PLAN.md](../PLAN.md) 是当前计划入口。另见[更新日志](CHANGELOG.md)、[代码风格](code-style.md)和[仓库规则](../AGENTS.md)。

| 位置 | 内容 |
| --- | --- |
| 活动计划 | [权限架构](permission-architecture-plan.md)、[会话存储](session-storage-plan.md) |
| [Archive](Archive/README.md) | [历史总计划](Archive/PAST_PLAN.md)、设计与验收记录 |
| Guides | [权限与审批](Guides/permission-modes.md)、[打包与发布](Guides/releasing.md)、[第三方声明](Guides/third-party-notices.md) |
| [Templates](Templates/README.md) | 跨项目代码风格模板 |

项目采用 [Apache-2.0](../LICENSE)，归属与第三方条款见 [NOTICE](../NOTICE) 和[第三方许可](../THIRD-PARTY-LICENSES.txt)。

## 🤝 参与贡献

反馈或修复请附复现步骤、相关命令和脱敏输出。默认测试保持离线，说明实际验证范围。工作规则见 [AGENTS.md](../AGENTS.md)。
