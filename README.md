<!-- markdownlint-disable -->

<div align="center">

# TinyHarness.NET

<br>

<div>
    <a href="https://github.com/banned2054/TinyHarness/releases/tag/v0.0.1"><img alt="Version 0.0.1" src="https://img.shields.io/badge/version-0.0.1-blue"></a>
    <a href="https://dotnet.microsoft.com/"><img alt=".NET 10" src="https://img.shields.io/badge/.NET-10.0-512BD4"></a>
    <a href="#platform-status"><img alt="NativeAOT win-x64" src="https://img.shields.io/badge/NativeAOT-win--x64-009688"></a>
</div>

<br>

<!-- markdownlint-restore -->

English | [简体中文](Docs/README.md)

</div>

**TinyHarness.NET** is a lightweight local coding-agent harness built with .NET 10.

It connects a language model to a local workspace so you can discuss code, inspect files, apply patches, and run commands through a CLI. Model access uses the official OpenAI SDK through `Microsoft.Extensions.AI.IChatClient`, with Chat Completions or Responses selected explicitly in configuration.

## 🚧 Project Status

**0.0.1 is the first public release.** The project remains an early prototype: basic tasks can run, and practical issues are still being fixed through use and feedback.

The terminal interface is unfinished: permission requests, AI replies, and user input are not clearly separated. Commands, configuration, and session formats may change. Existing tests and limited real-use checks do not establish production readiness or a comprehensive security review.

## ✨ Features

- **Conversations and workspaces** — One-shot tasks, streaming multi-turn conversations, local session history, and cross-process resume.
- **File and process tools** — List files, search text, read files, apply patches, and run commands with approval, timeouts, and bounded output.
- **Context and project rules** — Root and scoped `AGENTS.md` loading, token accounting, and automatic context compaction while retaining full local history.
- **Models and configuration** — Provider profiles, explicit model context windows, environment-variable or Windows Credential Manager key references, and offline configuration checks.
- **Additional integrations** — A local read-only MCP worker and optional Windows isolation for `shell` tool processes.

## 🗺️ Limitations

- Input editing, output layout, and controls during execution are rudimentary. There is no full terminal UI, multiline editor, or interactive terminal for tools.
- Model answers, summaries, and work-item statuses can be wrong. Check file changes and actual command results before relying on a completion report.
- **Approval is application-level policy.** Without the optional Windows sandbox, approved processes can access files outside the workspace and use the network. The sandbox covers only its `shell` processes, not the whole harness or model requests; it requires separately configured components and machine setup.
- Sessions preserve conversation and tool content without encryption and may contain secrets. Resume has strict identity and format checks; upgrades can make old sessions incompatible, and no old-format migration is provided.
- Windows x64 is the only platform with recorded NativeAOT validation. Other platforms, broader provider compatibility, power-loss durability, and comprehensive security remain unverified.

## 🚀 Build and Run

### Download Windows x64

Download `TinyHarness-0.0.1-win-x64.zip` and `SHA256SUMS.txt` from [Release v0.0.1](https://github.com/banned2054/TinyHarness/releases/tag/v0.0.1). Verify the ZIP's SHA-256, then extract the entire directory to a stable location outside your target workspace and sandbox write roots. Keep `sandbox/`, `LICENSE`, `NOTICE`, and `THIRD-PARTY-LICENSES.txt` beside `TinyHarness.exe`.

The package includes the three matching sandbox components and does not require the .NET runtime. Add the extracted directory to `PATH`, open a terminal in your target project, and run `tinyharness init`. See [Usage](#-usage) for model configuration and commands. Enabling the optional sandbox requires separately configured component paths and explicit provisioning; extraction alone does not change machine settings.

### Build from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). The first restore needs network access. Default tests and the smoke path use simulated models and do not need an API key.

```powershell
git clone https://github.com/banned2054/TinyHarness.git
cd TinyHarness
dotnet restore TinyHarness.slnx
dotnet test TinyHarness.Tests/TinyHarness.Tests.csproj --no-restore
dotnet run --project TinyHarness.Cli -- smoke --config tinyharness.json
```

The smoke command exercises file and process tools in a temporary workspace. It does not test a real provider or the Windows sandbox.

### NativeAOT publish

On Windows, also install MSVC C++ build tools and the Windows SDK:

```powershell
dotnet publish TinyHarness.Cli/TinyHarness.Cli.csproj -c Release -r win-x64 --self-contained true
```

The executable is written to `TinyHarness.Cli/bin/Release/net10.0/win-x64/publish/TinyHarness.exe`. The native executable does not need the .NET runtime installed. The optional sandbox components are configured separately.

To use it from another workspace, add the publish directory to the current PowerShell session's `PATH` while still at the repository root:

```powershell
$publishDirectory = (Resolve-Path .\TinyHarness.Cli\bin\Release\net10.0\win-x64\publish).Path
$env:PATH = "$publishDirectory;$env:PATH"
```

### Windows sandbox components and source

The optional Windows tool-process sandbox uses components built from [banned2054/codex](https://github.com/banned2054/codex), this project's fork of [OpenAI Codex](https://github.com/openai/codex). The fork gives TinyHarness its own machine-level identifiers and includes its network-isolation changes. TinyHarness's C# runtime directly orchestrates these components; running the Codex CLI or agent is not required.

The two runtime components are `codex-windows-sandbox-setup.exe` and `codex-command-runner.exe`. Use the matching components included in the release ZIP, or build them from the fork, and configure their absolute paths in user `settings.windowsSandbox.setupExecutablePath` and `runnerExecutablePath`. They are not produced or automatically downloaded by `dotnet publish`. See the [component build and integration instructions](Docs/Archive/windows-sandbox-dotnet-integration.md#构建产物已在本机构建验证) and [sandbox management guide](Docs/Archive/windows-sandbox-management.md) for setup and validation scope.

## 📖 Usage

The examples below use the published executable on `PATH`. Its file name is `TinyHarness.exe`; the command is written as `tinyharness` without a hyphen. Windows command lookup is case-insensitive, so `TinyHarness` works too. To launch directly from the publish directory in PowerShell, use `.\TinyHarness.exe`. There is no `tiny-harness` executable or alias supplied by this project.

### Configure a model

Change to the project you want to work on, then run the setup guide:

```powershell
Set-Location C:\Code\YourProject
tinyharness init
tinyharness config show
tinyharness doctor
```

`init` asks for a provider profile, endpoint, API key source, model ID, and context window. The endpoint is the SDK base URL, usually ending in `/v1`, not a full `/chat/completions` or `/responses` URL. The model must support streaming and tool calling. New profiles default to Chat Completions; Responses requires an explicit `chatApi` setting.

Choose either an environment-variable reference or Windows Credential Manager. When using an environment variable, set its value in the shell that will start TinyHarness, for example with PowerShell 7.1+:

```powershell
$env:TINYHARNESS_API_KEY = Read-Host "API key" -MaskInput
```

User configuration is stored at `%APPDATA%\tinyharness\user-config.json`; it stores key references rather than key values. `TINYHARNESS_USER_CONFIG_DIR` overrides that directory. `config show` identifies the effective configuration and its source; `doctor` checks it offline. `doctor --connect` makes a real model request and may incur charges.

Configuration lookup is: explicit `--config <file>`, current-directory `tinyharness.json`, user configuration, then built-in defaults. Files are not implicitly merged. **The repository's bundled `tinyharness.json` is a smoke example with a placeholder endpoint.** Run real tasks from your target project, or supply an explicit configuration, so it does not override the profile created by `init`.

### Start a task or conversation

```powershell
tinyharness run -- "Explain how this project is organized"
tinyharness
tinyharness chat
```

`tinyharness` with no arguments and `tinyharness chat` open the same interactive entry and list stored sessions. Use `/new` to create and select a saved conversation, `/sessions` to list all conversations (`*` marks the current one), and `/switch <session-id>` with the full 32-character ID to resume one. These commands make no model requests. A first ordinary request also creates a conversation automatically; exiting before selection creates none.

In a selected conversation, each submitted line starts a turn. You can discuss a problem first and explicitly request a change later. Model requests send conversation and relevant tool content to the configured provider and may incur charges.

Interactive startup asks about trust for a new workspace. Read-only workspaces allow list/search/read tools; patches and processes require workspace trust and then the normal permission flow. Noninteractive workspaces without saved trust stay read-only. Trust does not automatically approve commands; Windows directory authorization may separately require UAC.

At an approval prompt, `a` allows once, `s` allows matching requests for the current run, and `d` denies. Run-scoped approvals are not restored in later turns. Review the proposed action before approving it.

### Reasoning and basic controls

```powershell
tinyharness chat --reasoning content
tinyharness run --reasoning status -- "Explain this project"
```

The default `status` display shows waiting/reasoning elapsed time. `content` displays readable reasoning when provided by the service; it changes display only. Internal compaction text and opaque reasoning payloads are not displayed. Detailed request timings and usage are recorded in the session log.

During an active `chat` run, ordinary text is queued as an amendment for a safe boundary. The basic controls are:

| Input | Behavior |
| --- | --- |
| `/new` | Create and select a new saved conversation |
| `/sessions` | List all stored conversations |
| `/switch <session-id>` | Switch to a stored conversation |
| `/pause` | Request a pause at a model/tool boundary |
| `/resume` | Continue a paused run |
| `/cancel` | Cancel the active run and wait for cleanup |
| `/work` | Show work-item claims and unapplied amendment counts |
| `Ctrl+C` | Cancel the active turn; at the idle prompt, exit the session |

These are limited controls, not a general command system. Approval replies use their own prompt. `/trust` is available at the idle input prompt. Saved amendments that were not applied are not replayed automatically after cancellation or restart.

During a run, `/new` and `/switch` cancel it and wait for cleanup and saving before changing conversations. A failed switch keeps the current conversation; unreliable saves prevent leaving it. Switching requires the same workspace, endpoint, model and protocol as the stored conversation, so an incompatible conversation listed by `/sessions` must be reopened with its matching configuration.

### Inspect and resume sessions

```powershell
tinyharness session list
tinyharness session show <session-id>
tinyharness chat --session <session-id>
```

`session list` and `session show` are strictly offline: they do not call a model, run tools, or read an API key. Sessions default to `%LOCALAPPDATA%\tinyharness\sessions`; `settings.sessionStoreDirectory` or `TINYHARNESS_SESSION_STORE_DIR` can override the store. The legacy `sessionDirectory` field does not select this store.

Resume requires matching workspace, endpoint, model, protocol, current built-in prompt, and valid stored protocol content. If interrupted tool effects are uncertain, writes and processes stay blocked until you inspect and acknowledge the state. Local answers, readable reasoning, tool parameters, output, diffs, and `session show` preserve recorded content; terminal controls are escaped. Diagnostics, audits, and command summaries retain best-effort redaction. Earlier redacted history stays as recorded. Raw session files and local display output may contain secrets and are not safe-to-share exports; session files are not encrypted.

### Configuration, budgets, and integrations

Use `tinyharness help <command>` for full syntax:

| Command | Purpose |
| --- | --- |
| `provider list / add / use` | Manage provider profiles |
| `model list / add / use` | Manage explicitly configured models and context windows |
| `auth set <profile>` | Set a profile's API key reference |
| `config show / set` | Inspect sources or change supported user defaults |
| `mcp` | Serve the read-only `ask_glm` worker over stdio |
| `sandbox status / provision / verify` | Inspect, provision, or test the optional Windows sandbox |

Model selection is available through `/models` before the first request and between runs;
`list`, `use <id> --provider <name> [--effort <id>]`, and `effort <id|default>` also work directly.
Draft selection creates no session or writer. Current markers distinguish credential routes.
The menu includes catalog management and accepts credential references only. Enter new keys
through `auth set --store` or standalone provider forms. `provider edit <name>` and
`model edit <id> [--provider <name>]` preserve other models and settings.
`model add` also accepts `--display-name`, `--efforts low,high`, and `--default-effort high`.
Desktop supports idle session switching, declared reasoning levels, and provider/model forms.
Saving a catalog does not switch open sessions. Configuration drift requires explicit re-selection.
New keys use fresh credential targets; save failures retain old references and report possible orphan entries.

`maxAgentSteps` defaults to `0` (unlimited). Trusted user `settings.runBudget` defaults to 128 logical model requests, including compaction, and 256 tool executions per run. Total tokens and wall time are tracked without limits by default (`maxTotalTokens` / `maxWallClockSeconds` are `0`); positive values enable those limits. Individual tool timeouts and cancellation remain active. See the [budget contract](Docs/Archive/task-control-design.md) for details.

The MCP worker uses user configuration and sends permitted workspace content to the configured model; it cannot write files or run commands. It is an MCP server, not a client for external tools. `sandbox provision` changes machine accounts, ACLs, and network rules with elevation; `sandbox verify` runs real isolation checks. Follow the [sandbox setup and acceptance documentation](Docs/Archive/windows-sandbox-management.md) before using either.

<a id="platform-status"></a>

## 🖥️ Platform Status

| Platform | Status |
| --- | --- |
| Windows x64 | Primary development and prototype-use platform; NativeAOT and limited real-use/isolation evidence recorded |
| Other RIDs | Not yet validated for distribution or daily use |

## 📚 Documentation

- [Documentation navigation](Docs/README.md#documentation) — the Chinese README lists active plans and document locations.
- [Changelog](Docs/CHANGELOG.md) and [code style](Docs/code-style.md).
- Active plans: [provider/model selection](Docs/session-model-switching-design.md) and [desktop migration](Docs/desktop-migration-plan.md), mainly in Chinese.
- [Completed designs and historical records](Docs/Archive/README.md) — session persistence, project rules, prompts, compaction, budgets, sandbox integration, and validation evidence.
- Maintenance guides: [release packaging](Docs/Guides/releasing.md) and [third-party notices](Docs/Guides/third-party-notices.md).
- [PLAN.md](PLAN.md), [historical plans](PAST_PLAN.md), and [AGENTS.md](AGENTS.md) — development entry points and repository rules.
- [License](LICENSE) — Apache-2.0; [NOTICE](NOTICE) and [third-party licenses](THIRD-PARTY-LICENSES.txt) retain attribution and terms.

## 🤝 Contributing

Focused problem reports and fixes are welcome. Include reproduction steps, the relevant command, and redacted output. Keep default tests offline and distinguish verified results from assumptions. See [AGENTS.md](AGENTS.md) for repository rules.
