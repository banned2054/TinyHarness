<!-- markdownlint-disable -->

<div align="center">

# TinyHarness.NET

<br>

<div>
    <a href="https://github.com/banned2054/TinyHarness/releases/tag/v0.0.1"><img alt="Version 0.0.1" src="https://img.shields.io/badge/version-0.0.1-blue"></a>
    <a href="https://dotnet.microsoft.com/"><img alt=".NET 11" src="https://img.shields.io/badge/.NET-11.0-512BD4"></a>
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
- **Projects** — Shared CLI/Desktop project identities, a desktop project sidebar, per-project drafts and renaming. Existing unified logs are organized automatically by the desktop app on startup, store switches, and manual history refresh (up to 100 logs checked per batch); the CLI keeps the explicit `session initialize-projects [after-session-id]` command. Writing project ownership makes those logs unreadable by older versions. Sessions retain their original execution directory and permissions.
- **File and process tools** — List files, search text, read files, apply patches, and run commands with approval, timeouts, and bounded output.
- **Context and project rules** — Root and scoped `AGENTS.md` loading, token accounting, and automatic context compaction while retaining full local history.
- **Models and configuration** — Provider profiles, explicit model context windows, environment-variable or Windows Credential Manager key references, global and per-provider network routes, and offline configuration checks.
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

Configuration lookup is: explicit `--config <file>`, user configuration, then built-in defaults. Files are not implicitly merged, and a `tinyharness.json` in the working directory is never read automatically — opening a project directory cannot override the application configuration. **The repository's bundled `tinyharness.json` is a smoke example with a placeholder endpoint, used only through an explicit `--config`** (for example the offline `smoke` command). Real tasks use the profile created by `init`; supply an explicit configuration only when you want a different one.

The trusted user setting `settings.modelTimeouts` controls the model-request timeout policy; only user configuration can set it — an explicit `--config` file and project configuration never carry this section. All three fields are seconds: `streamIdleTimeoutSeconds` defaults to `300` and bounds idle gaps in the model stream; `responseTimeoutSeconds` defaults to `0` (disabled) and bounds one response end-to-end; `approvalTimeoutSeconds` defaults to `300`, must be positive, and bounds one AI review. The first two fields accept `0` to disable their deadline; negative values, a non-positive `approvalTimeoutSeconds`, or values beyond the timer range (at most 4,294,966 seconds) fail loading or saving with the `settings.modelTimeouts` field path. Stream idleness is observed at the M.E.AI SDK update level: tool-argument deltas are aggregated inside the SDK and may produce no visible update for a whole segment, so waiting for the first visible update also counts as idle time. `responseTimeoutSeconds` stays disabled until it is set explicitly. Setting `streamIdleTimeoutSeconds` to `0` only disables the app-layer update idle guard; the underlying SDK still keeps its 300-second per-network-operation protection, so the wait is never unlimited.

### Network settings

Desktop **Settings → Network** sets the global default route to the system proxy, direct, or a saved HTTP, HTTPS, or SOCKS5 proxy. It also manages reusable proxy entries. Usernames and passwords are kept in the system credential store on supported platforms; the ordinary user config contains only a credential target reference. CLI, Desktop, the MCP worker, `doctor --connect`, and online model discovery share the same route behavior.

Provider editing defaults to **Follow software default**. A provider can instead use the system proxy, direct, or a saved proxy; all models under that provider share the route. Older user configs without `settings.network` default to the system proxy, and older providers without `network` inherit the global route.

- **System proxy** follows the current .NET and operating-system proxy configuration, including applicable proxy environment variables. Requests can connect directly when no system proxy is configured.
- **Direct** disables the application-level proxy for TinyHarness HTTP requests. It does not bypass an operating-system VPN or TUN.
- **Selected proxy** uses the chosen proxy strictly. Connection, proxy-authentication, or DNS failures do not fall back to the system proxy or a direct connection.

Network routes affect only TinyHarness-managed model requests, model-list requests, and user-triggered proxy tests. They do not change the environment of Agent shell, git, npm, or other child processes, and they do not configure the operating system proxy. The proxy test requests an HTTPS target through the selected proxy and reports success with response-header latency when it receives a target response; it does not send provider credentials.

The network-specific portion of the trusted user config has this shape; the Desktop settings pages are the usual way to manage it:

```json
{
  "settings": {
    "network": {
      "defaultRoute": { "mode": "proxy", "proxyId": "local-mihomo" },
      "proxies": [
        {
          "id": "local-mihomo",
          "name": "Local Mihomo",
          "protocol": "socks5",
          "host": "127.0.0.1",
          "port": 7890
        }
      ]
    }
  },
  "profiles": [
    {
      "name": "openai",
      "network": { "mode": "proxy", "proxyId": "local-mihomo" }
    }
  ]
}
```

Omitting a provider's `network` field means inherit. `mode` accepts only `system`, `direct`, or `proxy`; `proxy` must reference an existing proxy ID. SOCKS5 destination names are sent for proxy-side DNS resolution. Input `socks5h` is accepted and normalized to `socks5`.

### Start a task or conversation

```powershell
tinyharness run -- "Explain how this project is organized"
tinyharness
tinyharness chat
```

`tinyharness` with no arguments and `tinyharness chat` open the same interactive entry and list stored sessions. Use `/new` to create and select a saved conversation, `/sessions` to list all conversations (`*` marks the current one), and `/switch <session-id>` with the full 32-character ID to resume one. These commands make no model requests. A first ordinary request also creates a conversation automatically; exiting before selection creates none.

In a selected conversation, each submitted line starts a turn. You can discuss a problem first and explicitly request a change later. Model requests send conversation and relevant tool content to the configured provider and may incur charges.

Interactive startup asks about trust for a new workspace. Read-only workspaces allow list/search/read tools; patches and processes require workspace trust and then the normal permission flow. Noninteractive workspaces without saved trust stay read-only. Trust does not automatically approve commands; Windows directory authorization may separately require UAC.

At an approval prompt, `a` allows once, `t` allows matching requests for the current run, `s` explicitly allows them for the current session instance, and `d` denies. Only the scopes listed by the prompt are available; host execution accepts single-operation approval only. Grants never survive a restart. Review the proposed action before approving it.

In Desktop, registering a project, selecting a permission mode, and approving a tool do not establish Windows sandbox directory readiness. If directory preparation blocks a command before launch, the idle session offers details and an explicit sandbox-directory repair action. Confirm the exact workspace and inheritance-propagation scope first; a separate UAC confirmation is requested only when normal privilege is insufficient. There is no need to launch CLI `/trust`. Repair does not take ownership, reset the whole tree's ACLs, or reprovision the machine sandbox. It must pass a final check and never replays the failed command. An unknown authorization result blocks immediate retries and requires inspection and an application restart.

### Desktop permissions and approvals

There are two approval modes: human approval (the default) and AI review. Under the three-level execution model, the sandbox baseline runs ordinary reads (not limited to the workspace), workspace writes, and the sandboxed shell automatically; writes outside the workspace, known sensitive locations (reads and writes), protected-metadata writes, networking requests, and shell write-root requests ask at Level 2, and `executionScope=host` is explicit single-approval host execution with no OS isolation. Grants expire or can be revoked, and never survive a restart; grant lifecycles and trusted authorization changes are persisted as required `permission-grant-event/1` records in the session log. CLI and Desktop register the same shell routing shape, and approval prompts render fact lines from one shared source (backend and isolation declaration, file and network permissions, validity, active grant summary). Settings → Permissions and approvals configures new-session defaults, the independent AI reviewer, and a standing-authorization block (sandbox network and additional write roots) with confirm and revoke actions.

Standing trust migrates explicitly: with `networkPolicy=enabled` or additional write roots configured, networking and additional-write-root writes stay on per-call approval until you confirm (the OS baseline is the profile root plus the workspace); after interactive confirmation they run as explicit persistent authorization, and revoking returns the network policy to restricted and clears the write roots. Additional read roots ride the confirmed authorization into the sandbox OS read surface (outside-profile roots become readable) and are listed in the confirmation display.

Legacy settings and saved sessions require separate migration confirmations. Desktop provides an explicit configuration-migration button in permission settings and a notification in each affected session. Interactive CLI chat prompts for confirmation (including the standing-trust confirmation); redirected input cannot confirm, `run` refuses pending legacy settings, and `run` with unconfirmed standing trust stays usable on per-call approvals with a notice. Ordinary settings saves and approval-mode switches do not clear the migration gate. See the [permission guide](Docs/Guides/permission-modes.md) and [implementation and validation record](Docs/permission-architecture-plan.md).

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
| `/approval human\|ai` | Save the selected session's approval mode while idle |
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
`model edit <id> [--provider <name>]` preserve other models and settings. After an endpoint-origin
change, the default `keep` needs explicit confirmation before reusing an old reference; declining
never carries it to the new server. `model add` also accepts `--display-name`, `--efforts low,high`,
and `--default-effort high`.
Desktop supports idle session switching, declared reasoning levels, and provider/model forms.
Saving a catalog does not switch open sessions. Configuration drift requires explicit re-selection.
New keys use fresh credential targets; save failures retain old references and report possible orphan entries.

`maxAgentSteps` defaults to `0` (unlimited). Trusted user `settings.runBudget` defaults to 128 logical model requests, including compaction, and 256 tool executions per run. Total tokens and wall time are tracked without limits by default (`maxTotalTokens` / `maxWallClockSeconds` are `0`); positive values enable those limits. Individual tool timeouts and cancellation remain active. See the [budget contract](Docs/Archive/task-control-design.md) for details.

`settings.modelTimeouts` provides the timeout policy for every model request, applied uniformly to chat, run, the MCP worker, `doctor --connect`, and approval calls made by AI review: `streamIdleTimeoutSeconds` defaults to `300` seconds (`0` disables the app-layer guard; the underlying 300-second per-network-operation protection is kept) watching stream idle gaps, `responseTimeoutSeconds` defaults to `0` (disabled) bounding one response end-to-end, and `approvalTimeoutSeconds` defaults to `300` seconds bounding one AI review; a deadline expiry is handled as a timeout failure — the operation stays unexecuted, never reaches a human, and records no denial memory. The section comes only from trusted user configuration; field semantics and error behavior are described in [Configure a model](#configure-a-model). After changing this section, same-model refreshes and reselections rebuild and replace the client under the new policy without resetting reasoning continuity.

Project instructions follow the ancestor chains of explicit tool targets. Directory listing and recursive searches do not activate descendant `AGENTS.md` files; reads, edits, and searches targeting a specific file still check its ancestor rules before execution. New tool scopes replace the active scopes; refreshes without new scopes retain the current set. Source reads are bounded to 1 MiB per file and 1 MiB per candidate, preferring more specific sources. Oversized sources are omitted with recorded reasons. The model projection, including framing and notices, is limited to 32 KiB and an estimated 8,000 tokens: broader bodies are omitted before the remaining specific body is truncated. Full successfully read sources retain their original hashes and text in the session log; notices also appear in history. Budget trimming does not fail the run. File-count or tracked-directory limits can still require narrower paths or split calls; invalid encoding, access, link, and consistency failures still stop the run. Existing renderer `/1` snapshots restore unchanged and refresh to `/2` on the next turn; older binaries cannot resume `/2` snapshots.

The MCP worker uses user configuration and sends permitted workspace content to the configured model; it cannot write files or run commands. It is an MCP server, not a client for external tools. `sandbox provision` changes machine accounts, ACLs, and network rules with elevation; `sandbox verify` runs real isolation checks. Follow the [sandbox setup and acceptance documentation](Docs/Archive/windows-sandbox-management.md) before using either.

Project-rule logs are written to `%LOCALAPPDATA%\tinyharness\logs\project-instructions-TIMESTAMP-pPID.log`, correlated by `session`/`run`. Debug retains individual checks, scopes, revisions, and full source-hash comparisons. Release keeps only important Information/Warning/Error events: applied rules, budget truncation/omission, actual deferrals, excessive scopes, check failures, and terminal outcomes; routine checks, tool-start counter resets, and full source inventories are excluded. `event=decision` retains deferral counts and `retry`/`stop-deferral-limit` decisions. Rule bodies, chat content, search terms, and command text are excluded; known secrets and common secret assignments are redacted. Each process writes at most 8 MiB, adding `event=log-limit` before stopping. Write failures disable diagnostics without changing run outcomes. These files are diagnostic evidence, not recovery state; detailed sandbox and desktop lifecycle logs remain Debug-only.

<a id="platform-status"></a>

## 🖥️ Platform Status

| Platform | Status |
| --- | --- |
| Windows x64 | Primary development and prototype-use platform; NativeAOT and limited real-use/isolation evidence recorded |
| Other RIDs | Not yet validated for distribution or daily use |

## 📚 Documentation

- [Documentation navigation](Docs/README.md#documentation) — the Chinese README lists active plans and document locations.
- [Changelog](Docs/CHANGELOG.md) and [code style](Docs/code-style.md).
- Active plan: [permission architecture](Docs/permission-architecture-plan.md), mainly in Chinese.
- [Archived plans, designs, and historical records](Docs/Archive/README.md) — historical milestones, desktop migration, model selection, projects and sessions, core designs, and validation evidence. Archiving preserves outstanding work and does not imply full acceptance.
- Maintenance guides: [release packaging](Docs/Guides/releasing.md) and [third-party notices](Docs/Guides/third-party-notices.md).
- [PLAN.md](PLAN.md), [historical plans](Docs/Archive/PAST_PLAN.md), and [AGENTS.md](AGENTS.md) — development entry points and repository rules.
- [License](LICENSE) — Apache-2.0; [NOTICE](NOTICE) and [third-party licenses](THIRD-PARTY-LICENSES.txt) retain attribution and terms.

## 🤝 Contributing

Focused problem reports and fixes are welcome. Include reproduction steps, the relevant command, and redacted output. Keep default tests offline and distinguish verified results from assumptions. See [AGENTS.md](AGENTS.md) for repository rules.
