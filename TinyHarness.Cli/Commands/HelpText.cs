namespace TinyHarness.Cli.Commands;

/// <summary>
///     <para>TinyHarness 的帮助文本。CLI 与文档共用同一套命令含义，避免两处各自漂移。</para>
///     <para>
///         Help text for TinyHarness. The CLI and documentation share one set of command meanings
///         so they cannot drift apart.
///     </para>
/// </summary>
internal static class HelpText
{
    /// <summary>
    ///     <para>顶层用法总览。</para>
    ///     <para>The top-level usage overview.</para>
    /// </summary>
    public const string Overview = """
        TinyHarness - a local coding-agent harness (Chat Completions)

        Usage:
          tinyharness run [--config <path>] [--] "your prompt"
          tinyharness [--config <path>] "your prompt"          (verbless)
          tinyharness chat [--config <path>]                   (interactive multi-turn session)
          tinyharness smoke [--config <path>]                  (offline tool-flow smoke)
          tinyharness mcp                                      (local MCP stdio worker)
          tinyharness init                                     (create the user config)
          tinyharness config show | config set <key> <value>
          tinyharness provider list | provider add <name> | provider use <name>
          tinyharness auth set <name> [--env <VAR>] [--store]
          tinyharness model list | model add <model-id> --context-window <tokens> | model use <model-id>
          tinyharness doctor [--connect]
          tinyharness sandbox status | sandbox provision [--yes] | sandbox verify [--yes]
          tinyharness session list | session show <session-id>
          tinyharness help [<command>]

        Management commands are never sent to the model. To prompt with text that starts
        with a command name, put it after `run --`, e.g. tinyharness run -- "init notes".
        Everything after `--` is the prompt.

        Run 'tinyharness help <command>' for details about a command.
        """;

    public const string Verbless = """
        Usage:
          tinyharness [--config <path>] "your prompt"

        Sends the prompt to the configured model through the agent loop. Only --config is
        recognized before the prompt; use `run --` when the prompt itself starts with a
        command name or contains '--'.
        """;

    public const string Run = """
        Usage:
          tinyharness run [--config <path>] [--] "your prompt"

        Runs one agent task. Only --config is recognized before the prompt. Everything after
        `--` is the prompt, so `run -- "config your editor"` sends the whole text as a prompt.

        Without --config the lookup order is:
          1. .\tinyharness.json in the current directory (project config)
          2. the user config (tinyharness init creates it)
          3. built-in defaults

        A workspace without saved trust is read-only in noninteractive runs. Interactive
        startup asks whether to trust it; trust permits normal approval prompts, not automatic
        command execution. Directory authorization may require Windows UAC.
        """;

    public const string Chat = """
        Usage:
          tinyharness chat [--config <path>] [--session <id>]

        Starts an interactive multi-turn session against the configured model and workspace.
        Each line you type is one user turn; turns share one conversation context and the
        latest input is the current goal. Tool approvals are prompted inline and scoped to
        the current run only.

        Ctrl+C cancels the active turn (the session keeps running; start a new turn
        afterwards). Ctrl+C at the prompt or EOF (Ctrl+Z then Enter on Windows) ends the
        session. Every finished turn is committed as an atomic snapshot into the protected
        session store, where 'tinyharness session list' / 'session show <id>' can inspect
        it strictly offline. A failed save is reported as "not reliably saved" and never
        presented as success. Protocol messages, tool arguments and reasoning are stored
        verbatim, without redaction or encryption by the harness; pasted secrets can be saved.
        Inspection output and audit remain redacted. Keep the session store private.

        --session <id> resumes a stored session in a new process. The resume requires the
        same workspace, endpoint, model and chat api as the stored session, and the stored
        opaque reasoning payloads must pass their integrity check; any mismatch refuses the
        resume instead of replaying changed content. Resuming a session whose last run ended
        mid tool execution is also refused — inspect it with 'session show' first.
        Older v1/v2 snapshots are inspection-only because their content may be redacted.

        chat takes no prompt argument; use `run -- "text"` for a one-shot prompt.

        /trust is a host command at the input prompt. It changes workspace trust without
        sending a model request. Read-only workspaces permit list/search/read only; patch
        and shell are disabled. Trust is saved per exact directory identity outside sandbox
        write roots. Windows directory authorization is separate from sandbox provisioning.
        """;

    public const string Smoke = """
        Usage:
          tinyharness smoke [--config <path>]

        Runs the offline tool-flow smoke: a scripted model drives list/search/read/patch and
        process tools through the permission flow in a temporary workspace. No network, no
        API key. --config only supplies the model name and budget fields.
        """;

    public const string Mcp = """
        Usage:
          tinyharness mcp

        Starts the local MCP stdio server for editor/agent clients such as Codex. Protocol
        messages are newline-delimited JSON on stdin/stdout (UTF-8); all diagnostics go to
        stderr. It serves only the `ask_glm` tool through a fresh, read-only worker run. Permitted
        file contents are sent to the configured model endpoint; the worker cannot write files or
        run commands. Cancellation notifications, client disconnect and Ctrl+C cancel active work.

        The worker reads only the TinyHarness user config (shown by `tinyharness config show`),
        using its default provider profile and explicitly selected model. It never reads a target
        workspace's `tinyharness.json` or its `commandRules`. By default the workspace is the
        process working directory; trusted overrides and worker budgets live under
        `settings.worker` in the user config. Configure a provider, model and credential first.

        Register it in the MCP client with command `tinyharness` and argument `mcp`. Closing
        stdin shuts the server down; Ctrl+C exits without writing diagnostics to stdout.
        """;

    public const string Init = """
        Usage:
          tinyharness init

        Interactive guide that creates the user config: provider profile (endpoint), API key
        source, default model, and its context window. The user config is stored at:

          Windows : %APPDATA%\tinyharness\user-config.json
          Other   : $XDG_CONFIG_HOME/tinyharness/user-config.json (~/.config when unset)

        Override the directory with the TINYHARNESS_USER_CONFIG_DIR environment variable.
        Plain JSON stores references only; the API key never lands in the file.
        """;

    public const string Config = """
        Usage:
          tinyharness config show
          tinyharness config set <key> <value>

        config show prints the effective values, where each one comes from, and the absolute
        config file paths. API key material is never printed; only its availability.

        config set writes non-sensitive defaults to the user config. Supported keys:
          maxAgentSteps, reservedOutputTokens, compactionThreshold,
          defaultToolTimeoutSeconds, sessionDirectory, sessionStoreDirectory

        Endpoint, profile, model, and API key changes use the provider, model, and auth
        commands instead. Use --config to run tasks against a project config file.
        """;

    public const string Provider = """
        Usage:
          tinyharness provider list
          tinyharness provider add <name>
          tinyharness provider use <name>

        Named provider profiles live in the user config. Each profile holds an endpoint, an
        API key reference, and its model list. `provider add` guides through the fields
        interactively; `provider use` selects the default profile.
        """;

    public const string Auth = """
        Usage:
          tinyharness auth set <name> [--env <VAR>] [--store]

        Sets how the API key for a profile is found:
          --env <VAR>          read the key from environment variable <VAR> at run time
          --store              save the key in the Windows Credential Manager under
                               'TinyHarness:<name>' (hidden input); JSON stores the
                               reference only
        With no flag the command asks interactively. Authentication does not write the key
        value to the user config, logs, sessions, or command output. Secrets pasted into chat
        messages can still be stored verbatim as conversation content.
        """;

    public const string Model = """
        Usage:
          tinyharness model list [--provider <name>]
          tinyharness model add <model-id> --context-window <tokens> [--provider <name>]
          tinyharness model use <model-id> [--provider <name>]

        Manages the model list of a profile. The context window is always explicit; it is
        never inferred from the model name. `model list` shows the locally configured
        models; remote model discovery is not performed.
        """;

    public const string Doctor = """
        Usage:
          tinyharness doctor [--connect]

        Checks configuration, paths, and credential availability without network access:
        endpoint URL, model, context budgets, workspace, session directory, and API key.

        --connect additionally sends one minimal model request to the configured endpoint.
        This uses the network and may incur charges; it never runs tools.
        """;

    public const string Sandbox = """
        Usage:
          tinyharness sandbox status
          tinyharness sandbox provision [--yes]
          tinyharness sandbox verify [--yes] [--case <name>[,<name>...]] [--workspace <path>]

        Management entry for the Windows tool-process sandbox (trusted user settings,
        settings.windowsSandbox in the user config). None of the subcommands sends
        input to a model.

        sandbox status is strictly read-only: it prints the trusted settings, the
        protocol versions this build expects, and the readiness inspection
        (component files, setup marker, capability SIDs, account credentials). Exit 0
        when not configured or ready; exit 1 when any problem is reported.

        sandbox provision runs the elevated setup helper once (UAC). It creates or
        resets machine-level state: local group TinyHarnessUsers, the local
        accounts TinyHarnessOffline/TinyHarnessOnline (fresh random passwords
        that never expire), registry entries hiding those accounts, firewall
        rules plus WFP block filters for the offline account, and directory ACLs
        for the sandbox home and the declared write roots. These names belong to
        the TinyHarness independent-namespace fork of the sandbox components
        (the setup/runner binary file names, payload fields, DPAPI, and the IPC
        protocol are unchanged; no service is installed). sandboxHome may stay
        blank in the trusted settings and defaults to a fixed per-user home
        (%LOCALAPPDATA%\tinyharness\windows-sandbox-home), independent of the
        Codex release's home; markers or credentials still naming the legacy
        Codex accounts are refused and must be re-provisioned. Re-provisioning
        resets this namespace's account passwords and can disturb other homes
        sharing this fork's namespace on the same machine; there is no uninstall
        or rollback. The command asks for confirmation first; --yes skips it
        (non-interactive use requires --yes). Every elevation attempt is
        appended to <sandboxHome>\.sandbox\tinyharness-provision.jsonl. Exit 0
        only when the helper succeeded and the re-inspection is ready.

        sandbox verify is the opt-in isolation acceptance: a REAL system test that
        runs a fixed matrix of sandboxed commands (one ACL refresh + runner per
        command) in a disposable workspace under %TEMP%\TinyHarness by default,
        covering identity, deny-read/deny-write probes, network blocking, exit-code
        and timeout semantics, cancellation, runner protocol failure, large output,
        and host-abandonment reclamation. It requires the sandbox to be configured,
        enabled, and provisioned; it never provisions, never changes machine
        configuration, and never falls back to host execution. Run it only in an
        environment authorized for such testing. It prints a per-case table and
        writes a JSON report to <sandboxHome>\.sandbox\tinyharness-verify-*.json.
        --case selects a subset of cases by stable name; --workspace overrides the
        acceptance workspace. The command asks for confirmation first; --yes skips
        it (non-interactive use requires --yes). Exit 0 only when every selected
        gated case passed with no INDETERMINATE result; exit 4 otherwise.
        (An internal verify-worker subcommand exists for the host-abandonment case;
        it is not part of the public interface.)
        """;

    public const string Session = """
        Usage:
          tinyharness session list
          tinyharness session show <session-id>

        Strictly offline inspection of tinyharness.session-log/1 sessions: no API key
        is read, no model is called, no tool runs, and no workspace file is opened for writing.

        session list prints sessions in the protected session store (default
        %LOCALAPPDATA%\tinyharness\sessions, overridden by settings.sessionStoreDirectory
        in the user config or the TINYHARNESS_SESSION_STORE_DIR environment variable).
        Damaged entries are reported without stopping the list.

        session show prints session history and the turn/run inspection summary.
        Damaged or unknown log formats are rejected. Raw protocol messages and opaque
        reasoning are not printed.
        """;

    /// <summary>
    ///     <para>判断是否为已知 help 主题。</para>
    ///     <para>Whether a name is a known help topic.</para>
    /// </summary>
    public static bool IsKnownCommand(string name)
    {
        return name.ToLowerInvariant() switch
        {
            "run"      => true,
            "chat"     => true,
            "smoke"    => true,
            "mcp"      => true,
            "init"     => true,
            "config"   => true,
            "provider" => true,
            "auth"     => true,
            "model"    => true,
            "doctor"   => true,
            "sandbox"  => true,
            "session"  => true,
            "help"     => true,
            _          => false
        };
    }

    /// <summary>
    ///     <para>返回某个主题的帮助文本；未知主题返回总览。</para>
    ///     <para>Returns the help text for a topic; the overview for unknown topics.</para>
    /// </summary>
    public static string For(string? topic)
    {
        return topic?.ToLowerInvariant() switch
        {
            "run"      => Run,
            "chat"     => Chat,
            "smoke"    => Smoke,
            "mcp"      => Mcp,
            "init"     => Init,
            "config"   => Config,
            "provider" => Provider,
            "auth"     => Auth,
            "model"    => Model,
            "doctor"   => Doctor,
            "sandbox"  => Sandbox,
            "session"  => Session,
            _          => Overview
        };
    }
}
