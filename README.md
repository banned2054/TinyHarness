<!-- markdownlint-disable -->

<div align="center">

# TinyHarness.NET

<br>

<div>
    <a href="https://dotnet.microsoft.com/"><img alt=".NET 10" src="https://img.shields.io/badge/.NET-10.0-512BD4"></a>
    <a href="https://learn.microsoft.com/dotnet/core/deploying/native-aot/"><img alt="NativeAOT win-x64" src="https://img.shields.io/badge/NativeAOT-win--x64-009688"></a>
    <a href="#-build-and-run"><img alt="Tests offline" src="https://img.shields.io/badge/tests-offline-green"></a>
</div>

<br>

<!-- markdownlint-restore -->

English | [简体中文](Docs/README.md)

</div>

A lightweight local coding-agent harness built on .NET 10. Models are called through a unified `Microsoft.Extensions.AI.IChatClient` boundary over the official OpenAI SDK, with Chat Completions (default) or Responses selected explicitly by configuration. The model proposes the next action — inspecting files, proposing patches, running commands — and the harness validates arguments, approves permissions, dispatches tools, and manages context.

The goal is a small, complete, and explainable execution loop. The CLI is the current entry point; `win-x64` NativeAOT is a continuously verified constraint.

> **Approval here is application-level policy, not an OS sandbox.** An approved process can still access files outside the workspace and the network. The optional Windows sandbox covers only `shell` tool processes.

## 🚧 Project Status

Early development; commands, configuration, and storage formats may change between milestones.

Implemented today: one-shot `run`, interactive multi-turn `chat`, a protected session store with strictly offline `session list` / `session show` and cross-process resume via `chat --session <id>`, offline-first configuration management, a read-only MCP worker, and opt-in Windows sandbox management. M14.1 adds bounded root/local `AGENTS.md` loading, rule refresh across tool boundaries and resume, and new-chat creation on the first nonblank submission. M14.2 adds one shared behavior prompt for natural-language task scope, tool use, verification, and delivery. Default tests and builds never call a paid model; milestone-specific evidence and limits are listed below.

M14.1 offline regression passed: **853 total / 851 passed / 2 skipped because symbolic links cannot be created / 0 failed**. Scale passed separately (1/1) within the original budgets. NativeAOT publish and smoke outside the repository passed, verifying initial rules, updates, resumed source integrity, history, and confirmed patch diffs. Evidence: `artifacts/test-results/m14-regression.trx`, `artifacts/test-results/m14-scale.trx`, `artifacts/m14-instructions-scale-results.json`, and `artifacts/m14-instructions-native-smoke.txt`. See the [implementation and validation record](Docs/project-instructions-design.md#14-实施结果与本地验证) for compatibility, commands, and unverified boundaries.

M14.2 engineering validation passed: **860 total / 858 passed / 2 skipped / 0 failed**, including 7 prompt integration cases; NativeAOT and native smoke outside the repository also passed. Only the current built-in prompt is maintained; new and resumed sessions use it, and different prompts fail strict resume. See the [system prompt design and evidence](Docs/system-prompt-design.md); model adherence remains to be evaluated separately.

M15 adds safe-boundary amendments, pause/resume/cancel, a sourced `work_items` list, and trusted cumulative run budgets. Final offline regression: **1013 total / 1011 passed / 2 link-permission skips / 0 failed**; scale passed separately, and `win-x64` NativeAOT plus repository-external smoke passed. See the [M15 contract and evidence](Docs/task-control-design.md). M15 model adherence has not been evaluated with a real service; M14's later sample acceptance is recorded in the [compaction design](Docs/context-compaction-design.md).

## ✨ Features

- **Agent loop** — streaming replies, multiple sequential tool calls per response, step limits, failure and cancellation handling.
- **Tools** — `list_files`, `search_text`, `read_file`, `apply_patch`, and `shell` (direct process or explicit shell mode) with timeouts, process-tree termination, and output trimming.
- **Permissions** — every call follows `Prepare → Authorize → Execute`: workspace reads are allowed by default, patches and processes ask for approval (`a` once / `s` this run / `d` deny), and command allow rules can be configured.
- **Context** — token budgets calibrated by real usage, tool-result trimming, bounded text handoffs and sourced user wording after compaction.
- **Run control and work** — text entered during a run becomes a saved amendment; `/pause`, `/resume`, `/cancel`, and `/work` provide optional host controls. Work-item completion stays a claim, separate from actual tool facts.
- **Cumulative budgets** — trusted `settings.runBudget` limits logical model requests (including summaries), tool executions and wall time. Total tokens are tracked without a limit by default: omit `maxTotalTokens` or set it to 0; a positive value enables the token limit. Missing usage is estimated; `BudgetLimitReached` retains its reason and counters.
- **Project instructions** — root and scoped `AGENTS.md` files are loaded within fixed limits, retained through compaction, and refreshed before requests and tool dispatch. A changed rule set invalidates old unstarted plans and approvals; project files grant no permissions. Opening a new `chat`, blank input, or EOF creates no session until a real submission passes rule preflight.
- **Persistence** — `run` and new `chat` sessions share the append-only `tinyharness.session-log/1` format in the protected session store; the old full run snapshot/audit pair is no longer the default output. History DTOs keep deterministic process/final classification, bounded redacted tool output, and per-file patch facts whose diffs distinguish `confirmed` from `unknown` after a later workspace change. An execution-intent failure prevents a side effect; an unknown side-effect result sets NeedsInspection, the model view rewinds to the last complete tool boundary, and an unknown side effect blocks writes and process execution until the user inspects and confirms. Session histories page by sequence cursor through a shared scanner (up to 200 returned items with an 8 MiB cumulative serialized UTF-8 budget; scanning may read the captured log range), artifact content is inlined up to 2 MiB per artifact and 4 MiB across stdout/stderr, batches are capped at 8 MiB, and a log at 256 MiB. An uncertain write poisons the writer and strict resume rejects a truncated final line; history inspection may return a reliable prefix with a warning. Sessions can be inspected strictly offline and resumed (`chat --session <id>`) after workspace/model/protocol and opaque-data integrity checks. Session files may contain secrets and are not encrypted; `session list` / `session show` print only redacted inspection views, never canonical messages or opaque payloads.
- **MCP worker** — `tinyharness mcp` runs a local read-only stdio server exposing `ask_glm` for clients such as Codex.
- **Windows sandbox** — opt-in machine-level isolation for `shell` processes with explicit `sandbox status / provision / verify` management.

## 🗺️ Limitations

- A session with an unknown side-effect result after interruption resumes with the model view rewound to the last complete tool boundary and side-effect tools blocked until the user inspects the workspace and confirms (`c` / `k` prompt at resume). Only `tinyharness.session-log/1` is supported. Old `.session.json` snapshots and separate audit files are ignored; no migration command is provided.
- The Windows sandbox is verified on a single machine, and coexistence with a Codex release install is unverified.
- Only `win-x64` NativeAOT has been verified; M14.1 publish and native smoke passed. Rule-file and directory-link creation tests were skipped on this host. Protocol coverage comes from a local simulated SSE service, with historical validation against one live endpoint for the earlier implementation.
- M15 control/work-item adherence has not been evaluated with a real model service; GUI history consumption, power-loss durability and new OS-isolation scenarios are also unverified in this acceptance run. Saved unapplied amendments are inspectable and never replayed automatically.

## 🚀 Build and Run

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). The initial restore needs network access; tests and the smoke path never call a real model or need an API key.

```powershell
git clone https://github.com/banned2054/TinyHarness.git
cd TinyHarness
dotnet restore TinyHarness.slnx
dotnet test TinyHarness.Tests/TinyHarness.Tests.csproj --no-restore
dotnet run --project TinyHarness.Cli -- smoke --config tinyharness.json
```

The bundled [tinyharness.json](tinyharness.json) uses a placeholder endpoint. The smoke test is intended to have a scripted model inspect and patch a deliberately broken fixture, rebuild it, and finish with `status : Completed`; the current native smoke result is recorded under Project Status.

Native publish (also needs MSVC C++ build tools and the Windows SDK):

```powershell
dotnet publish TinyHarness.Cli/TinyHarness.Cli.csproj -c Release -r win-x64 --self-contained true -o artifacts/nativeaot
```

### Commands

```text
tinyharness run [--config <file>] "task"    one-shot task (a bare "task" also works)
tinyharness chat [--config <file>] [--session <id>]    interactive multi-turn session; --session resumes a stored one
tinyharness session list | show <id>        strictly offline session inspection
tinyharness mcp                             read-only stdio MCP worker (ask_glm)
tinyharness smoke                           offline end-to-end self check
tinyharness init / config / provider / model / auth / doctor
tinyharness sandbox status | provision | verify    Windows sandbox management
```

`Ctrl+C` cancels the active run; in `chat` it cancels the current turn and exits when idle. Exit codes: `0` completed, `1` failed or a step/budget limit reached, `2` missing prompt, `130` cancelled. `doctor` is offline by default; `doctor --connect` is the explicit network check. Use `--help` or `help <command>` for full syntax.

### Configure

First-time setup does not require hand-editing JSON. `init` stores the provider profile, endpoint, key reference, model, and context window in the user config (`%APPDATA%\tinyharness\user-config.json` on Windows). The authentication configuration stores only key references — an environment variable or a Windows Credential Manager entry — never the key material itself, and keys are not proactively injected into `chat` sessions. Sessions use the protected store by default (`%LOCALAPPDATA%\tinyharness\sessions`), overridden by `settings.sessionStoreDirectory` or `TINYHARNESS_SESSION_STORE_DIR`; `config.sessionDirectory` is not the default. Session files preserve canonical conversation content verbatim so resume stays faithful, which means anything pasted into the chat, read from files, or returned by the model may contain secrets. The harness does not encrypt session files — the protected store location is a path and exclusive-write convention, not encryption and not a promise that other local processes cannot read it. Only `tinyharness.session-log/1` is supported; older persistence formats are neither read nor migrated.

```powershell
dotnet run --project TinyHarness.Cli -- init
dotnet run --project TinyHarness.Cli -- config show
```

### Connect a real model

Save a JSON file under the Git-ignored `artifacts/` directory, for example `artifacts/live.json`:

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

`endpoint` is the SDK base URL (typically ending in `/v1`), not the full completions URL. The model must support the selected transport plus tool calling; `chatApi` is never inferred from the model name.

```powershell
$env:TINYHARNESS_API_KEY = Read-Host "API key" -MaskInput    # PowerShell 7.1+
dotnet run --project TinyHarness.Cli -- run --config artifacts/live.json "Find out why the tests fail and fix it"
```

`run` and `chat` call the model service and may incur charges; authorized tools can modify files and start processes. Without `--config`, the CLI reads `tinyharness.json` from the current directory.

## 🖥️ Platform Status

| Platform | Status |
| --- | --- |
| Windows x64 | Primary development platform; local unified acceptance completed: offline regression, scale, `win-x64` NativeAOT publish and native smoke |
| Other RIDs | Not yet verified |

## 📚 Documentation

- [PLAN.md](PLAN.md) — current product scope and the M12+ roadmap
- [PAST_PLAN.md](PAST_PLAN.md) — archived MVP and M1–M11 plan
- [Session persistence design](Docs/session-persistence-design.md) (Chinese)
- [Task control and run budgets](Docs/task-control-design.md) (Chinese)
- [Windows sandbox management and acceptance records](Docs/windows-sandbox-management.md) (Chinese)
- [Windows sandbox integration design](Docs/windows-sandbox-dotnet-integration.md) (Chinese)
- [M9](Docs/m9-demo.md) and [M10](Docs/m10-model-protocol.md) validation records
- [AGENTS.md](AGENTS.md) — repository development rules

## 🤝 Contributing

Focused issues and pull requests are welcome. Keep default tests offline and free of API keys, and distinguish implemented behavior from unverified behavior. See [AGENTS.md](AGENTS.md) for the working rules.
