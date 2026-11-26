<!-- markdownlint-disable -->

<div align="center">

# TinyHarness.NET

<br>

<div>
    <a href="https://github.com/banned2054/TinyHarness/releases/tag/v0.1.0-alpha.1"><img alt="版本 0.1.0-alpha.1" src="https://img.shields.io/badge/version-0.1.0--alpha.1-blue"></a>
    <a href="https://dotnet.microsoft.com/"><img alt=".NET 11" src="https://img.shields.io/badge/.NET-11.0-512BD4"></a>
    <a href="#platform-status"><img alt="NativeAOT win-x64" src="https://img.shields.io/badge/NativeAOT-win--x64-009688"></a>
</div>

<br>

<!-- markdownlint-restore -->

[English](../README.md) | 简体中文

</div>

**TinyHarness.NET** 是基于 .NET 11 的本地 Coding Agent，提供 CLI 和 Desktop。它将模型接入本地工作区，支持 Chat Completions 和 Responses。

## 🚧 项目状态

**v0.1.0-alpha.1** 是预发布版本。命令、配置和会话格式可能变化，生产可用性和全面安全性尚未验证。

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
- 会话文件未加密，可能包含秘密；升级或迁移前请先备份。

## 🚀 构建与运行

### 下载 Windows x64 版本

从 [Release v0.1.0-alpha.1](https://github.com/banned2054/TinyHarness/releases/tag/v0.1.0-alpha.1) 下载 CLI 与 Desktop ZIP 和 `SHA256SUMS.txt`。校验 SHA-256 后，将两个包分别完整解压到工作区和沙箱写根之外的不同目录，把 CLI 目录加入 `PATH`，在项目目录运行 `tinyharness init`。包内含沙箱组件，运行无需 .NET runtime。

### 从源码构建

安装 [.NET 11 SDK](https://dotnet.microsoft.com/download/dotnet/11.0)（首次还原需要联网）：

```powershell
git clone https://github.com/banned2054/TinyHarness.git
cd TinyHarness
dotnet restore TinyHarness.slnx
dotnet test TinyHarness.Tests/TinyHarness.Tests.csproj --no-restore
dotnet run --project TinyHarness.Cli -- smoke --config tinyharness.json
```

默认测试与 `smoke` 使用模拟模型，不涉及真实供应商或 Windows 沙箱。NativeAOT 发布与 Desktop 打包见[发布指南](Guides/releasing.md)。

### Windows 沙箱组件

沙箱使用本项目 [Codex fork](https://github.com/banned2054/codex) 的 setup 和 runner（随发布包放在各自安装目录的 `sandbox\` 下，也可[从源码构建](Archive/windows-sandbox-dotnet-integration.md#构建产物已在本机构建验证)）。`settings.windowsSandbox` 中的 `setupExecutablePath` 和 `runnerExecutablePath` 留空时默认使用当前安装包自带组件（源码运行或需覆盖时可填写绝对路径），然后运行 `tinyharness sandbox provision`（需提权，会修改机器账户、ACL 和防火墙规则；详见[沙箱管理说明](Archive/windows-sandbox-management.md)）。

## 📖 使用说明

以下示例假定 `tinyharness` 已加入 `PATH`（直接运行解压包请用 `.\TinyHarness.exe`）。完整 CLI 语法见 `tinyharness help <命令>`。

### 配置模型

```powershell
Set-Location C:\Code\YourProject
tinyharness init
tinyharness doctor
```

Desktop 可在「设置」中管理供应商、模型和网络代理。密钥支持 Windows Credential Manager 或环境变量引用（如 `$env:TINYHARNESS_API_KEY = Read-Host "API key" -MaskInput`）。CLI 与 Desktop 默认共用 `%APPDATA%\tinyharness\user-config.json`（可用 `TINYHARNESS_USER_CONFIG_DIR` 覆盖）。`doctor` 离线检查，`doctor --connect` 会实际请求模型。

### 开始任务或对话

```powershell
tinyharness run -- "解释这个项目的结构"
tinyharness chat
```

无参数运行 `tinyharness` 也会进入聊天。提交请求会将对话和相关工具内容发送到已配置的供应商，并可能产生费用。

- **工作区信任与目录 ACL** — CLI 进入新目录时会询问是否信任，未信任目录仅允许只读工具。工作区 ACL 在首次执行可写沙箱 Shell 时按需准备（普通用户目录无需 UAC）；权限受阻时可在 CLI 空闲提示符执行 `/trust` 或在 Desktop 使用「修复沙箱目录权限…」显式修复。
- **权限与审批** — 沙箱基线放行普通读取、工作区写入和沙箱内 Shell；敏感路径、越界写入、联网和额外写根需审批；宿主执行（`executionScope=host`）每次单独批准且无操作系统隔离。详见[权限与审批](Guides/permission-modes.md)。
- **思考展示与控制** — 用 `--reasoning status|content` 控制推理输出；聊天内支持 `/new`、`/sessions`、`/switch`、`/models`、`/approval`、`/trust`、`/pause`、`/resume`、`/cancel`、`/work` 与 `Ctrl+C`。

### 会话、预算与集成

```powershell
tinyharness session list
tinyharness session show <session-id>
tinyharness chat --session <session-id>
```

会话默认保存在 `%LOCALAPPDATA%\tinyharness\sessions`（可用 `settings.sessionStoreDirectory` 或 `TINYHARNESS_SESSION_STORE_DIR` 覆盖）。旧版程序可能无法打开新版日志；会话存储迁移需手动触发，迁移前请关闭旧版并备份（见 `tinyharness help session` 及[备份、恢复与回退](Archive/session-storage-plan.md#54-备份恢复与回退)）。

其余子命令组（`provider`、`model`、`auth`、`config`、`mcp`、`sandbox`）见 `tinyharness help <命令>`；默认运行预算与 `AGENTS.md` 作用域规则见[运行预算](Archive/task-control-design.md)和[项目规则](Archive/project-instructions-design.md)。

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
| 活动计划 | 当前无进行中的专项计划，详见 [PLAN.md](../PLAN.md) |
| [Archive](Archive/README.md) | [历史总计划](Archive/PAST_PLAN.md)、设计与验收记录 |
| Guides | [权限与审批](Guides/permission-modes.md)、[打包与发布](Guides/releasing.md)、[第三方声明](Guides/third-party-notices.md) |
| [Templates](Templates/README.md) | 跨项目代码风格模板 |

项目采用 [Apache-2.0](../LICENSE)，归属与第三方条款见 [NOTICE](../NOTICE) 和[第三方许可](../THIRD-PARTY-LICENSES.txt)。

## 🤝 参与贡献

反馈或修复请附复现步骤、相关命令和脱敏输出。默认测试保持离线，说明实际验证范围。工作规则见 [AGENTS.md](../AGENTS.md)。
