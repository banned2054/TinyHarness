namespace TinyHarness.Cli;

/// <summary>
/// TinyHarness 的帮助文本。CLI 与文档共用同一套命令含义，避免两处各自漂移。
///
/// Help text for TinyHarness. The CLI and documentation share one set of command meanings so they cannot drift apart.
/// </summary>
internal static class HelpText
{
    /// <summary>
    /// 顶层用法总览。
    /// The top-level usage overview.
    /// </summary>
    public const string Overview = """
        TinyHarness - a local coding-agent harness (Chat Completions)

        Usage:
          tinyharness run [--config <path>] [--] "your prompt"
          tinyharness [--config <path>] "your prompt"          (verbless)
          tinyharness smoke [--config <path>]                  (offline tool-flow smoke)
          tinyharness init                                     (create the user config)
          tinyharness config show | config set <key> <value>
          tinyharness provider list | provider add <name> | provider use <name>
          tinyharness auth set <name> [--env <VAR>] [--store]
          tinyharness model list | model add <model-id> --context-window <tokens> | model use <model-id>
          tinyharness doctor [--connect]
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
        """;

    public const string Smoke = """
        Usage:
          tinyharness smoke [--config <path>]

        Runs the offline tool-flow smoke: a scripted model drives list/search/read/patch and
        process tools through the permission flow in a temporary workspace. No network, no
        API key. --config only supplies the model name and budget fields.
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
          defaultToolTimeoutSeconds, sessionDirectory

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
        With no flag the command asks interactively. The key value is never written to the
        user config, logs, sessions, or command output.
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

    /// <summary>
    /// 判断是否为已知 help 主题。
    /// Whether a name is a known help topic.
    /// </summary>
    public static bool IsKnownCommand(string name) => name.ToLowerInvariant() switch
    {
        "run"      => true,
        "smoke"    => true,
        "init"     => true,
        "config"   => true,
        "provider" => true,
        "auth"     => true,
        "model"    => true,
        "doctor"   => true,
        "help"     => true,
        _          => false,
    };

    /// <summary>
    /// 返回某个主题的帮助文本；未知主题返回总览。
    /// Returns the help text for a topic; the overview for unknown topics.
    /// </summary>
    public static string For(string? topic) => topic?.ToLowerInvariant() switch
    {
        "run"      => Run,
        "smoke"    => Smoke,
        "init"     => Init,
        "config"   => Config,
        "provider" => Provider,
        "auth"     => Auth,
        "model"    => Model,
        "doctor"   => Doctor,
        _          => Overview,
    };
}
