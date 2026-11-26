<!-- markdownlint-disable -->

<div align="center">

# TinyHarness.NET

<br>

<div>
    <a href="https://github.com/banned2054/TinyHarness/releases/tag/v0.1.0-alpha.1"><img alt="Version 0.1.0-alpha.1" src="https://img.shields.io/badge/version-0.1.0--alpha.1-blue"></a>
    <a href="https://dotnet.microsoft.com/"><img alt=".NET 11" src="https://img.shields.io/badge/.NET-11.0-512BD4"></a>
    <a href="#platform-status"><img alt="NativeAOT win-x64" src="https://img.shields.io/badge/NativeAOT-win--x64-009688"></a>
</div>

<br>

<!-- markdownlint-restore -->

English | [简体中文](Docs/README.md)

</div>

**TinyHarness.NET** connects language models to a local workspace for coding tasks in CLI and Desktop. It runs on .NET 11 and supports Chat Completions and Responses.

## 🚧 Project Status

**v0.1.0-alpha.1** is a prerelease. Commands, settings, and session formats may change. Production readiness and comprehensive security remain unverified.

## ✨ Features

- **Sessions** — One-shot tasks, streaming chat, local history, and cross-process resume.
- **Projects** — Shared project identities across CLI and Desktop, with a desktop sidebar and per-project drafts.
- **Tools** — List, search, read, patch, and run commands with approvals and timeouts.
- **Context** — Scoped `AGENTS.md` rules, token tracking, and automatic context compaction.
- **Models** — Provider profiles, configurable context windows, key references, and per-provider proxies.
- **Integrations** — A read-only MCP worker and Windows Shell sandboxing.

## 🗺️ Limitations

- The CLI has basic input and run controls. Tools have no interactive terminal.
- Models can report incorrect results. Check file changes and command output.
- Shell commands are blocked when the Windows sandbox is unavailable. The sandbox covers sandboxed Shell processes only; approved host execution runs without OS isolation.
- Session files are unencrypted and may contain secrets. Back up your store before upgrading or migrating.

## 🚀 Build and Run

### Download Windows x64

Download the CLI and Desktop ZIPs and `SHA256SUMS.txt` from [Release v0.1.0-alpha.1](https://github.com/banned2054/TinyHarness/releases/tag/v0.1.0-alpha.1). Verify the SHA-256 checksum, extract each ZIP whole into separate directories outside your workspaces and sandbox write roots, add the CLI directory to `PATH`, and run `tinyharness init` in your project. Packages include sandbox components and run without the .NET runtime.

### Build from source

Install the [.NET 11 SDK](https://dotnet.microsoft.com/download/dotnet/11.0) (first restore needs network access):

```powershell
git clone https://github.com/banned2054/TinyHarness.git
cd TinyHarness
dotnet restore TinyHarness.slnx
dotnet test TinyHarness.Tests/TinyHarness.Tests.csproj --no-restore
dotnet run --project TinyHarness.Cli -- smoke --config tinyharness.json
```

Default tests and `smoke` use simulated models without a real provider or the Windows sandbox. For NativeAOT publishing and Desktop packaging, see the [release guide](Docs/Guides/releasing.md).

### Windows sandbox components

TinyHarness uses setup and runner executables from its [Codex fork](https://github.com/banned2054/codex) (bundled under `sandbox\` in each release ZIP or [built from source](Docs/Archive/windows-sandbox-dotnet-integration.md#构建产物已在本机构建验证)). Leave `settings.windowsSandbox.setupExecutablePath` and `runnerExecutablePath` blank to use each installation's bundled components (or set explicit absolute paths to override for both CLI and Desktop), then run `tinyharness sandbox provision` (requires elevation and modifies machine accounts, ACLs, and firewall rules; see the [sandbox guide](Docs/Archive/windows-sandbox-management.md)).

## 📖 Usage

The examples assume `tinyharness` is on `PATH` (or run `.\TinyHarness.exe` from the package directory). Use `tinyharness help <command>` for full CLI syntax.

### Configure a model

```powershell
Set-Location C:\Code\YourProject
tinyharness init
tinyharness doctor
```

In Desktop, add providers, models, and proxy routes under **Settings**. Use Windows Credential Manager or an environment-variable key reference (for example, `$env:TINYHARNESS_API_KEY = Read-Host "API key" -MaskInput`). CLI and Desktop share `%APPDATA%\tinyharness\user-config.json` by default (override with `TINYHARNESS_USER_CONFIG_DIR`). `doctor` checks offline; `doctor --connect` sends a real model request.

### Start a task or conversation

```powershell
tinyharness run -- "Explain how this project is organized"
tinyharness chat
```

Running `tinyharness` with no arguments also opens chat. Submitting a request sends conversation and tool context to your configured provider and may incur charges.

- **Workspace trust & ACLs** — New CLI workspaces ask for trust; untrusted directories permit read-only tools only. Workspace ACLs are prepared on demand on the first writable sandbox Shell (without UAC on normal user directories); if blocked, repair explicitly via `/trust` while idle in CLI or **Repair sandbox directory permissions** in Desktop.
- **Permissions & approvals** — Baseline allows ordinary reads, workspace edits, and sandboxed Shell; sensitive paths, out-of-workspace writes, network access, and extra write roots require approval; `executionScope=host` requires per-operation approval without OS isolation. See the [permission guide](Docs/Guides/permission-modes.md).
- **Controls & switching** — Pass `--reasoning status|content` to display reasoning. While in chat, use `/new`, `/sessions`, `/switch`, `/models`, `/approval`, `/trust`, `/pause`, `/resume`, `/cancel`, `/work`, and `Ctrl+C`.

### Sessions, budgets, and integrations

```powershell
tinyharness session list
tinyharness session show <session-id>
tinyharness chat --session <session-id>
```

Sessions default to `%LOCALAPPDATA%\tinyharness\sessions` (override with `settings.sessionStoreDirectory` or `TINYHARNESS_SESSION_STORE_DIR`). Older binaries may refuse logs from newer versions; session-store migration is explicit—close older clients and back up first (`tinyharness help session`, [backup & restore](Docs/Archive/session-storage-plan.md#54-备份恢复与回退)).

Additional CLI groups (`provider`, `model`, `auth`, `config`, `mcp`, `sandbox`) are documented under `tinyharness help <command>`. See [budgets](Docs/Archive/task-control-design.md) and [project `AGENTS.md` rules](Docs/Archive/project-instructions-design.md) for defaults and scoping.

<a id="platform-status"></a>

## 🖥️ Platform Status

| Platform | Status |
| --- | --- |
| Windows x64 | NativeAOT and limited real-use and isolation validation |
| Other RIDs | Distribution and daily use remain unverified |

## 📚 Documentation

- [Documentation index](Docs/README.md#documentation), [changelog](Docs/CHANGELOG.md), and [code style](Docs/code-style.md).
- [Active plans](PLAN.md), [archived designs and validation](Docs/Archive/README.md), and [historical plan](Docs/Archive/PAST_PLAN.md).
- [Permissions](Docs/Guides/permission-modes.md), [release packaging](Docs/Guides/releasing.md), and [third-party notices](Docs/Guides/third-party-notices.md).
- [Repository rules](AGENTS.md), [Apache-2.0 license](LICENSE), [NOTICE](NOTICE), and [third-party licenses](THIRD-PARTY-LICENSES.txt).

## 🤝 Contributing

For issues and fixes, include reproduction steps, the command, and redacted output. Keep default tests offline and distinguish verified results from assumptions. See [AGENTS.md](AGENTS.md).
