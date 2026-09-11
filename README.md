# TinyHarness.NET

English | [**简体中文**](docs/README.md)

A lightweight local coding-agent harness built on .NET 10. It uses the OpenAI-compatible Chat Completions protocol to let models inspect workspace files, propose patches, and run commands, while the application handles argument validation, permission approval, tool dispatch, and context management.

The project focuses on a small, complete, and explainable agent execution flow: the model proposes the next action, and the harness prepares, authorizes, and executes it. The CLI is the current entry point, and NativeAOT compatibility is continuously verified.

## Current status

M7 persistence and demo foundations are implemented: runs now write an inspectable JSONL audit and a JSON session snapshot under `artifacts/runs` (or the configured `sessionDirectory`). The offline smoke command is the repeatable tool-flow demo path; real-model diagnosis remains opt-in.

| Capability | Current implementation |
|---|---|
| Model protocol | Official OpenAI .NET SDK with custom endpoints; SSE text and fragmented tool-call arguments |
| Agent loop | Multiple tool calls per response, sequential execution, result feedback, step limits, failure and cancellation handling |
| File tools | `list_files`, `search_text`, `read_file`, `apply_patch` |
| Process tool | `shell` with direct process and explicit shell modes; stdout/stderr, exit codes, timeouts, process-tree termination, and output trimming |
| Permissions | `Allow` / `Ask` / `Deny`; one-time and session approvals, plus command allow rules |
| Context | Token estimation, tool-result trimming, recent complete turns, structured summaries, and successive compaction batches |
| Verification | Offline fake-client tests, local simulated SSE protocol tests, and a `win-x64` NativeAOT smoke test |
| Persistence | Per-run `<runId>.audit.jsonl` and `<runId>.session.json`; configured known secret values and explicitly supported sensitive fields are redacted before persistence |

Each CLI invocation runs one task and displays approvals, compaction notices, and the final result. Interactive multi-turn sessions, token-by-token terminal output, and session recovery are not yet available.

## Quick start: run the offline smoke test

These commands use PowerShell on Windows. The .NET 10 SDK is required. The initial NuGet restore requires network access; the tests and smoke path do not require a real model service or an API key.

```powershell
git clone https://github.com/banned2054/TinyHarness.git
cd TinyHarness
dotnet restore TinyHarness.slnx
dotnet test TinyHarness.Tests/TinyHarness.Tests.csproj --no-restore
dotnet run --project TinyHarness.Cli -- smoke --config tinyharness.json
```

The included [tinyharness.json](tinyharness.json) uses a placeholder endpoint and smoke model name. It works with the offline smoke test but must be changed before making real model requests.

Each live run writes its audit and completed session snapshot to `sessionDirectory`. The audit is append-only during the run and records prepared tool summaries, permission outcomes, tool-result metadata, and terminal status. Its `run.completed` entry means Agent execution reached a terminal result; because it is written before the snapshot, it is not proof that both files were saved successfully. The CLI reports only persistence paths that actually exist. The snapshot preserves the full in-memory message history and structured state for inspection; it is not a recovery or replay mechanism.

The smoke test creates a temporary package-free .NET 10 console fixture. Its fixed check program initially observes the broken `Calculator.Add` result `-1` and exits with `CHECK_FAIL`; the scripted model inspects and patches only `Calculator.cs`, rebuilds, then observes result `5` and `CHECK_PASS`. Fixture restore uses an empty package-source configuration and the installed .NET 10 SDK/targeting pack. Build and check commands use structured direct process mode (`dotnet build` and `dotnet <fixture.dll>`), while a separate step still covers explicit shell execution. Successful output includes:

```text
status       : Completed
steps        : 17
toolExecs    : 16
```

This verifies the harness execution flow. A real model's ability to fix code and the quality of its summaries require separate validation. Offline tests cover compaction invariants and successive compaction batches.

## Connect a real model

Create a JSON configuration file, such as `artifacts/live.json`. The `artifacts/` directory is ignored by Git and can hold local configuration. Create the directory first, then save the following content, replacing the endpoint, model, workspace, and context window settings.

```json
{
  "endpoint": "https://your-provider.example/v1",
  "model": "your-tool-capable-model",
  "apiKeyEnvironmentVariable": "TINYHARNESS_API_KEY",
  "contextWindowTokens": 128000,
  "reservedOutputTokens": 8000,
  "compactionThreshold": 100000,
  "maxAgentSteps": 40,
  "defaultToolTimeoutSeconds": 120,
  "workspaceRoot": "C:/Code/YourProject",
  "commandRules": []
}
```

`endpoint` is the SDK base URL, typically ending in `/v1`; do not use the full `/v1/chat/completions` URL. The model must support streaming Chat Completions and function/tool calling. The context window is explicitly configured, not inferred from the model name. The example values do not describe any particular model's capabilities.

Enter the key in the current PowerShell session and run a task:

```powershell
$env:TINYHARNESS_API_KEY = Read-Host "API key" -MaskInput
dotnet run --project TinyHarness.Cli -- run --config artifacts/live.json "Find out why this project's tests fail and fix them"
```

`-MaskInput` requires PowerShell 7.1 or later. The key is read from the configured environment variable and is not stored in JSON. `run` calls the model service: both regular requests and compaction summary requests may incur charges. Authorized tools can modify files or start processes.

The CLI also accepts commands without the `run` verb:

```powershell
dotnet run --project TinyHarness.Cli -- --config artifacts/live.json "Explain this project's directory structure"
```

Without `--config`, the CLI reads `tinyharness.json` from the current directory. If `workspaceRoot` is omitted, it defaults to the process's current directory. Relative workspace paths are also resolved against the process's current directory, not the configuration file's directory. Use an absolute path when working on another project.

Press `Ctrl+C` to cancel. Exit codes are `0` for completion, `1` for failure or reaching the step limit, `2` for a missing prompt, and `130` for task cancellation during normal execution.

### Configuration

| Field | Default | Description |
|---|---|---|
| `endpoint` | Empty | HTTP(S) base URL for the model service |
| `model` | Empty | Model identifier; required for live runs |
| `apiKeyEnvironmentVariable` | Empty | Name of the environment variable holding the API key |
| `contextWindowTokens` | `128000` | Declared context window size |
| `reservedOutputTokens` | `8000` | Space reserved for output in the budget; currently not sent as an API output limit |
| `compactionThreshold` | `0` | Total estimated token threshold for compaction, including reserved output; `0` uses the context window size |
| `maxAgentSteps` | `40` | Maximum number of regular model requests; summary requests do not count toward this limit |
| `defaultToolTimeoutSeconds` | `120` | Default tool timeout in seconds |
| `workspaceRoot` | Current directory | Root for file-tool boundaries and command working directories |
| `sessionDirectory` | `artifacts/runs` | Directory for per-run audit JSONL and session JSON files |
| `commandRules` | `[]` | Explicit process allow rules |

## Permissions and execution boundaries

Every tool follows `Prepare → Authorize → Execute`: validate arguments, normalize targets, and create a prepared plan; evaluate permissions against capabilities and resource scopes; then execute that same plan.

Listing, searching, and reading within the workspace are allowed by default. Patches and processes require approval by default. File tools check normalized paths and resolve symlink/junction targets before execution to reject access outside the workspace.

The approval prompt shows the call summary, target paths, and the diff for `apply_patch`:

```text
[a]llow once / [s]ession / [d]eny:
```

- `a`: Allow this call once.
- `s`: Grant permission for this run using the displayed capability, path scope, and call constraints. Directory scopes include descendants; process grants also bind command constraints.
- `d`, empty input, or any unrecognized input: Deny this call. The denial is returned to the model so it can adjust its approach.

**This is application-level policy and approval, not an OS sandbox.** File-tool path checks do not constrain every action of an approved process: processes may access files outside the workspace or use the network. The `shell` tool checks the working directory and obtains approval for the call, but provides no OS-level file or network isolation. It also has no separate stage-detection mechanism for Git operations, publishing, or deployment.

### Command allow rules

Only an explicitly configured matching rule or an existing approval lets a command run without prompting. To allow exactly `dotnet test` at the workspace root, add this entry to `commandRules`:

```json
{
  "mode": "direct",
  "executable": "dotnet",
  "arguments": ["test"],
  "workingDirectory": "."
}
```

`direct` mode starts the executable without interpreting pipes, redirection, or other shell syntax. Rules match arguments individually, with `*` and `?` supported within each argument. The working directory must match exactly; subdirectories are not automatically allowed. The rule above does not match `dotnet test SomeProject.csproj` with its additional argument.

Rules for explicit `shell` mode use `shell`, `command`, and `workingDirectory`, matching the shell type, full command text, and directory exactly. Builds and tests execute repository code, so configure allow rules only for projects you trust.

Processes capture stdout/stderr separately and trim model output using head + tail retention. When output is truncated, the full output is kept in a local artifact and its path is returned. Live runs remove the configured API key environment variable from child processes. Persistence redacts configured known secret values by exact replacement (longer overlapping values first) and explicitly supported sensitive assignment/JSON fields such as API keys, access tokens, tokens, passwords, and secrets. This is not general-purpose secret detection and does not guarantee recognition of arbitrary sensitive data.

## How context compaction works

The Context Manager keeps the original message history during a run and builds a separate view for each model request:

```text
System instructions and original task
  + StructuredState (existing summary)
  + Recent complete turns
  + Current turn
```

It first trims oversized tool results in the model view, then selects older complete turns according to the budget and summarizes them into structured JSON through an additional Chat Completions request. Summary requests include no tool definitions. Tool calls and their results are folded together, while the current turn and latest complete turn are retained. Multiple batches of history can be compacted before a single regular request.

StructuredState contains `Goal`, `Constraints`, `Decisions`, `FilesInspected`, `FilesModified`, `CommandsAndResults`, and `PendingWork`. Before accepting a summary, the manager checks its format, budget savings, and rules for retaining existing state:

- A nonempty `Goal` cannot be cleared.
- Recorded files, command results, and decisions must be retained.
- Each existing pending item must be retained or explicitly closed with `completed: <item>`; constraints may be updated.
- If validation fails, that batch's summary is not applied and the original history remains intact. If the final estimate still exceeds the context window, the task stops without sending the regular request.

Later compaction follows “old summary + newly folded history → new summary.” The model rewrites the existing summary, and validation cannot guarantee complete initial fact extraction or lossless meaning across repeated summaries. Token counts use a character-based estimate; they are not currently calibrated against service-reported usage and are not guaranteed to be an upper bound on actual token counts.

Original messages remain in process memory and are not deleted by compaction, although tools may already have trimmed their own output before returning results. Completed runs persist that history and the committed structured state; session recovery and side-effect replay are deliberately not implemented.

## Tests and NativeAOT

Default tests use scripted/fake clients and a local simulated SSE service. They do not call paid models or require an API key. Local protocol tests listen on loopback ports.

```powershell
dotnet test TinyHarness.Tests/TinyHarness.Tests.csproj
dotnet test TinyHarness.Tests/TinyHarness.Tests.csproj --no-restore --filter FullyQualifiedName~ContextCompactionTests
```

Coverage includes the tool-call loop, protocol fragments, argument errors, permission denial, path boundaries, process timeouts and cancellation, output trimming, and compaction atomic groups, rollback, state retention, and successive batches within one step.

The currently verified NativeAOT RID is **`win-x64`**. Native publishing on Windows also requires MSVC C++ build tools and the Windows SDK, available through the Visual Studio/Build Tools “Desktop development with C++” workload. Other RIDs are not yet listed as verified targets.

Run from the repository root:

```powershell
dotnet publish TinyHarness.Cli/TinyHarness.Cli.csproj -c Release -r win-x64 --self-contained true -o artifacts/m7-nativeaot
./artifacts/m7-nativeaot/TinyHarness.exe smoke --config tinyharness.json
```

The published executable is named `TinyHarness.exe`. Use `run --config <path> "task"` to call a real service.

M7 verification on 2026-09-11: **178/178 default tests passed**; managed smoke passed both from the repository root and from an external directory; isolated `win-x64` NativeAOT publishing produced no trimming/AOT warnings; and that run's newly published native executable passed the smoke test from an external directory. See the [M7 demo record](docs/m7-demo.md) for details.

### Provider and model verification scope

Protocol verification currently uses a local simulated SSE service, covering custom endpoints, text streaming, fragmented tool calls, request tool definitions, HTTP errors, and cancellation. There is no verified list of real providers/models yet. Using an “OpenAI-compatible” interface does not mean every provider and model has been tested for compatibility.

## Code structure

| Directory | Responsibility |
|---|---|
| `TinyHarness.Core/Agent` | Main loop, tool registration, step counts, and terminal states |
| `TinyHarness.Core/ChatCompletions` | SDK adapter, messages and stream events, tool-argument assembly |
| `TinyHarness.Core/Tools` | Schemas, preparation, and execution for the five tools |
| `TinyHarness.Core/Permissions` | Permission decisions, session grants, and command matching |
| `TinyHarness.Core/Runtime` | Workspace path boundaries, processes, and output capture |
| `TinyHarness.Core/Context` | Budget estimation, model views, structured state, and compaction |
| `TinyHarness.Core/Configuration` | JSON configuration loading and path resolution |
| `TinyHarness.Cli` | CLI arguments, approval prompts, dependency composition, and offline smoke path |
| `TinyHarness.Tests` | Unit, protocol contract, and integration tests |

The agent loop accesses models through `IChatCompletionClient`; SDK types stay in the protocol adapter layer. Tools are registered explicitly. Structured state uses System.Text.Json source generation, and configuration uses manual binding without reflection to support ongoing trimming and NativeAOT verification.

See [PLAN.md](PLAN.md) for the complete product scope and milestones, and [AGENTS.md](AGENTS.md) for repository development rules. The MVP excludes GUI, MCP, RAG, multi-agent support, a plugin system, and an OS sandbox.
