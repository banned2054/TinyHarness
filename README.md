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

Implemented today: one-shot `run`, interactive multi-turn `chat`, a protected session store with strictly offline `session list` / `session show`, offline-first configuration management, a read-only MCP worker, and opt-in Windows sandbox management. Resuming a saved session, project-instruction (`AGENTS.md`) loading, and host-enforced read-only/implement modes are planned but not implemented. Default tests and builds never call a paid model; only one live combination (`glm-5.3` over a single HTTPS endpoint) has been verified.

## ✨ Features

- **Agent loop** — streaming replies, multiple sequential tool calls per response, step limits, failure and cancellation handling.
- **Tools** — `list_files`, `search_text`, `read_file`, `apply_patch`, and `shell` (direct process or explicit shell mode) with timeouts, process-tree termination, and output trimming.
- **Permissions** — every call follows `Prepare → Authorize → Execute`: workspace reads are allowed by default, patches and processes ask for approval (`a` once / `s` this run / `d` deny), and command allow rules can be configured.
- **Context** — token budgets calibrated by real usage, tool-result trimming, and compaction of older turns into validated structured summaries.
- **Persistence** — every run writes a JSONL audit and a redacted session snapshot; `chat` sessions are committed atomically to a protected store and can be inspected strictly offline.
- **MCP worker** — `tinyharness mcp` runs a local read-only stdio server exposing `ask_glm` for clients such as Codex.
- **Windows sandbox** — opt-in machine-level isolation for `shell` processes with explicit `sandbox status / provision / verify` management.

## 🗺️ Limitations

- Sessions can be viewed offline but cannot be resumed yet; resume is the next milestone.
- The Windows sandbox is verified on a single machine, and coexistence with a Codex release install is unverified.
- Only `win-x64` NativeAOT is verified; protocol coverage comes from a local simulated SSE service plus the one live endpoint above.
- `chat` has not been verified against a real model service.

## 🚀 Build and Run

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). The initial restore needs network access; tests and the smoke path never call a real model or need an API key.

```powershell
git clone https://github.com/banned2054/TinyHarness.git
cd TinyHarness
dotnet restore TinyHarness.slnx
dotnet test TinyHarness.Tests/TinyHarness.Tests.csproj --no-restore
dotnet run --project TinyHarness.Cli -- smoke --config tinyharness.json
```

The bundled [tinyharness.json](tinyharness.json) uses a placeholder endpoint. In the smoke test a scripted model inspects and patches a deliberately broken fixture, rebuilds it, and the run finishes with `status : Completed`.

Native publish (also needs MSVC C++ build tools and the Windows SDK):

```powershell
dotnet publish TinyHarness.Cli/TinyHarness.Cli.csproj -c Release -r win-x64 --self-contained true -o artifacts/nativeaot
```

### Commands

```text
tinyharness run [--config <file>] "task"    one-shot task (a bare "task" also works)
tinyharness chat [--config <file>]          interactive multi-turn session
tinyharness session list | show <id>        strictly offline session inspection
tinyharness mcp                             read-only stdio MCP worker (ask_glm)
tinyharness smoke                           offline end-to-end self check
tinyharness init / config / provider / model / auth / doctor
tinyharness sandbox status | provision | verify    Windows sandbox management
```

`Ctrl+C` cancels the active run; in `chat` it cancels the current turn and exits when idle. Exit codes: `0` completed, `1` failed or step limit reached, `2` missing prompt, `130` cancelled. `doctor` is offline by default; `doctor --connect` is the explicit network check. Use `--help` or `help <command>` for full syntax.

### Configure

First-time setup does not require hand-editing JSON. `init` stores the provider profile, endpoint, key reference, model, and context window in the user config (`%APPDATA%\tinyharness\user-config.json` on Windows). Keys are kept as references — an environment variable or Windows Credential Manager — and are never written to JSON.

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
| Windows x64 | Primary development platform; offline tests and `win-x64` NativeAOT publish verified |
| Other RIDs | Not yet verified |

## 📚 Documentation

- [PLAN.md](PLAN.md) — current product scope and the M12+ roadmap
- [PAST_PLAN.md](PAST_PLAN.md) — archived MVP and M1–M11 plan
- [Windows sandbox management and acceptance records](Docs/windows-sandbox-management.md) (Chinese)
- [Windows sandbox integration design](Docs/windows-sandbox-dotnet-integration.md) (Chinese)
- [M9](Docs/m9-demo.md) and [M10](Docs/m10-model-protocol.md) validation records
- [AGENTS.md](AGENTS.md) — repository development rules

## 🤝 Contributing

Focused issues and pull requests are welcome. Keep default tests offline and free of API keys, and distinguish implemented behavior from unverified behavior. See [AGENTS.md](AGENTS.md) for the working rules.
