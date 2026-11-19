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
- Shell commands are blocked when the Windows sandbox is unavailable. The sandbox covers sandboxed Shell processes only. Approved host execution runs without OS isolation.
- Session files are unencrypted and may contain secrets. Back up your store before upgrading. Compatibility details are under [Inspect and resume sessions](#inspect-and-resume-sessions).

## 🚀 Build and Run

### Download Windows x64

Download the CLI and Desktop ZIPs and `SHA256SUMS.txt` from [Release v0.1.0-alpha.1](https://github.com/banned2054/TinyHarness/releases/tag/v0.1.0-alpha.1). Verify the SHA-256 checksum and extract each ZIP whole into separate directories outside your workspaces and sandbox write roots. The packages include sandbox components and run without the .NET runtime.

Add the extracted directory to `PATH`, then run `tinyharness init` in your project.

### Build from source

Install the [.NET 11 SDK](https://dotnet.microsoft.com/download/dotnet/11.0). The first restore needs network access.

```powershell
git clone https://github.com/banned2054/TinyHarness.git
cd TinyHarness
dotnet restore TinyHarness.slnx
dotnet test TinyHarness.Tests/TinyHarness.Tests.csproj --no-restore
dotnet run --project TinyHarness.Cli -- smoke --config tinyharness.json
```

Tests and smoke use simulated models. Smoke runs file and process tools without a real provider or the Windows sandbox.

### NativeAOT publish

Install MSVC C++ build tools and the Windows SDK. From the repository root, publish the CLI and add the output directory to the current PowerShell session's `PATH`.

```powershell
dotnet publish TinyHarness.Cli/TinyHarness.Cli.csproj -c Release -r win-x64 --self-contained true
$publishDirectory = (Resolve-Path .\TinyHarness.Cli\bin\Release\net11.0\win-x64\publish).Path
$env:PATH = "$publishDirectory;$env:PATH"
```

For Desktop packaging, see the [release guide](Docs/Guides/releasing.md).

### Windows sandbox components and source

TinyHarness uses setup and runner executables from its [fork](https://github.com/banned2054/codex) of [OpenAI Codex](https://github.com/openai/codex). Use the matching components from the ZIP or [build them from source](Docs/Archive/windows-sandbox-dotnet-integration.md#构建产物已在本机构建验证).

Set the absolute paths in `settings.windowsSandbox.setupExecutablePath` and `runnerExecutablePath`, then run `tinyharness sandbox provision`. Provisioning requires elevation and changes machine accounts, ACLs, and network rules. See the [sandbox setup guide](Docs/Archive/windows-sandbox-management.md).

## 📖 Usage

The examples use `tinyharness` on `PATH`. To launch from the package directory in PowerShell, run `.\TinyHarness.exe`.

### Configure a model

```powershell
Set-Location C:\Code\YourProject
tinyharness init
tinyharness config show
tinyharness doctor
```

In Desktop, use **Settings** to add a provider and model. Choose a model that supports streaming and tool calls. Enter the provider base URL, usually ending in `/v1`. Chat Completions is the default. Set `chatApi` explicitly for Responses.

Use Windows Credential Manager or an environment-variable key reference. For the latter, set the key before launching TinyHarness, for example in PowerShell 7.1+.

```powershell
$env:TINYHARNESS_API_KEY = Read-Host "API key" -MaskInput
```

CLI and Desktop share `%APPDATA%\tinyharness\user-config.json` by default. It stores key references. Override its directory with `TINYHARNESS_USER_CONFIG_DIR`. Configuration lookup goes from explicit `--config` to user settings to built-in defaults, with no implicit merge. The repository's `tinyharness.json` is a smoke fixture and requires explicit `--config`. `doctor` checks offline. `doctor --connect` sends a real model request and may incur charges.

Set model timeouts in trusted user settings under `settings.modelTimeouts`. Defaults are 300 seconds for stream idle (`streamIdleTimeoutSeconds`), 0 for total response (`responseTimeoutSeconds`, disabled), and 300 seconds for AI review (`approvalTimeoutSeconds`). Set the stream value to 0 to disable the app-level idle timer. The SDK still enforces its network timeout.

### Automatic session titles

Desktop uses the first request to generate a title after saving a turn. The sidebar shows a preview until a title is available. Failed requests retry on a later turn.

In **Settings → General → Automatic session titles**, use the session model or choose a dedicated model. Title generation may incur extra usage. Old binaries that lack `session-title` support cannot open logs containing these records.

### Network settings

In **Settings → Network**, choose the system proxy, direct connection, or a saved HTTP, HTTPS, or SOCKS5 proxy. Providers inherit this route unless you select an override. A selected proxy fails without falling back to another route. Proxy credentials use the system credential store.

These settings apply to TinyHarness model and catalog requests and its proxy test. Shell tools use their own networking policy. For manual configuration, merge this fragment into your user configuration.

```json
{
  "settings": {
    "network": {
      "defaultRoute": { "mode": "proxy", "proxyId": "local-mihomo" },
      "proxies": [
        { "id": "local-mihomo", "name": "Local Mihomo", "protocol": "socks5", "host": "127.0.0.1", "port": 7890 }
      ]
    }
  },
  "profiles": [
    { "name": "openai", "network": { "mode": "proxy", "proxyId": "local-mihomo" } }
  ]
}
```

### Start a task or conversation

```powershell
tinyharness run -- "Explain how this project is organized"
tinyharness chat
```

Running `tinyharness` with no arguments also opens chat. Sending a request starts a turn and may incur model charges. The provider receives the conversation and relevant tool content.

The CLI asks for trust in a new workspace. Untrusted workspaces allow read-only file tools. Patch and process tools require trust and are subject to tool permissions. At approval prompts, `a` allows once, `t` allows for the current run, `s` allows for the current session, and `d` denies. Use the scopes shown in the prompt.

In Desktop, if directory permissions block a sandbox command, use **View details** or **Repair sandbox directory permissions** while idle. Review the affected directory before confirming. Retry the command after the repair succeeds.

### Desktop permissions and approvals

**Ask for approval** is the default. **Approve for me** routes eligible requests to an AI reviewer. Set the default mode, reviewer model, and standing authorizations under **Settings → Permissions and approvals**.

- **Baseline** allows ordinary reads, including outside the workspace, workspace edits, and sandboxed Shell.
- **Sandbox extensions** ask for approval for sensitive paths, out-of-workspace writes, protected metadata writes, network access, and extra Shell write roots. Standing network access and extra write roots require explicit confirmation.
- **Host execution** (`executionScope=host`) requires separate approval for each operation and runs without OS isolation.

Temporary grants expire at restart. Follow the separate prompts for legacy settings and sessions. See the [permission guide](Docs/Guides/permission-modes.md) and [implementation record](Docs/Archive/permission-architecture-plan.md).

### Reasoning and basic controls

```powershell
tinyharness chat --reasoning content
tinyharness run --reasoning status -- "Explain this project"
```

`status` shows elapsed time. `content` shows readable reasoning when the provider returns it. During a run, ordinary input is queued until a safe model or tool boundary.

| Input | Behavior |
| --- | --- |
| `/new` | Create and select a saved session |
| `/sessions` | List saved sessions |
| `/switch <session-id>` | Switch using the full session ID |
| `/pause` | Pause at a model or tool boundary |
| `/resume` | Resume a paused run |
| `/cancel` | Cancel and wait for cleanup |
| `/work` | Show work items and pending input |
| `/approval human\|ai` | Save the approval mode while idle |
| `Ctrl+C` | Cancel the current turn or exit while idle |

`/new` and `/switch` cancel an active run and wait for the session to save. Use `/trust` while idle to manage workspace trust. Pending input is not replayed after cancellation or restart.

### Inspect and resume sessions

```powershell
tinyharness session list
tinyharness session show <session-id>
tinyharness chat --session <session-id>
```

`list` and `show` work offline. The default store is `%LOCALAPPDATA%\tinyharness\sessions`. Override it with `settings.sessionStoreDirectory` or `TINYHARNESS_SESSION_STORE_DIR`.

Resume checks the workspace, endpoint, model, protocol, built-in prompt, and saved content. Inspect and acknowledge any uncertain effects from interrupted tools before resuming writes or commands. Keep raw history and local output private. Diagnostics and audit output redact secrets on a best-effort basis.

Older binaries may refuse logs written by newer versions. Session-store migration is explicit. Close older clients and back up the store before migrating. Use `tinyharness help session` for commands. See [backup, restore, and rollback](Docs/session-storage-plan.md#54-备份恢复与回退).

### Configuration, budgets, and integrations

Use `tinyharness help <command>` for full syntax.

| Command | Purpose |
| --- | --- |
| `provider list / add / use` | Manage providers |
| `model list / add / use` | Manage models and context windows |
| `auth set <profile>` | Set an API key reference |
| `config show / set` | Inspect or change user settings |
| `mcp` | Serve the read-only `ask_glm` worker over stdio |
| `sandbox status / provision / verify` | Inspect, set up, or test the Windows sandbox |

Use `/models` in the CLI or the Desktop model menu to switch models and reasoning effort while idle. Saving catalog edits does not change an open session's model. Reusing a key after an endpoint-origin change requires confirmation.

`settings.runBudget` defaults to 128 model requests and 256 tool executions per run. Total token and time limits are optional. See the [budget guide](Docs/Archive/task-control-design.md).

`AGENTS.md` rules follow the ancestors of explicit file targets. Recursive listing and search do not load descendant rules. See the [project rule design and diagnostics](Docs/Archive/project-instructions-design.md).

The MCP server sends permitted workspace content to the configured model and has no write or Shell tools. `sandbox verify` runs real isolation checks. See the [sandbox setup guide](Docs/Archive/windows-sandbox-management.md).

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
