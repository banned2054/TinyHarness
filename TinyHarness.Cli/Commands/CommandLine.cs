using TinyHarness.Cli.Exceptions;
using TinyHarness.Cli.Models;
using TinyHarness.Core.Services.Persistence;

namespace TinyHarness.Cli.Commands;

/// <summary>
///     TinyHarness 命令行解析器。管理命令（init/config/provider/auth/model/doctor/sandbox）永远不作为
///     提示词发送给模型；与命令名冲突的普通文本使用 `run -- "文本"`，`--` 之后全部按提示词处理。
///     `chat` 启动交互会话，不接受提示词参数。
///     The TinyHarness command-line parser. Management commands
///     (init/config/provider/auth/model/doctor/sandbox) are never
///     sent to the model as a prompt; plain text that collides with a command name uses `run -- "text"`, and
///     everything after `--` is treated as the prompt. `chat` starts an interactive session and takes no prompt
///     argument.
/// </summary>
internal static class CommandLine
{
    /// <summary>
    ///     解析参数；失败抛出 <see cref="CliUsageException" />。
    ///     Parses the arguments; throws <see cref="CliUsageException" /> on failure.
    /// </summary>
    public static CliOptions Parse(string[] args)
    {
        if (args.Length == 0) throw new CliUsageException("No command or prompt was given.", HelpText.Overview);

        var first = args[0];
        if (string.Equals(first, "process-smoke-child", StringComparison.Ordinal))
            return new CliOptions { Kind = CliCommandKind.ProcessSmokeChild };

        if (string.Equals(first, "--", StringComparison.Ordinal)) return ParsePromptArgs(args, false);

        if (string.Equals(first, "help", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(first, "--help", StringComparison.Ordinal)         ||
            string.Equals(first, "-h", StringComparison.Ordinal))
            return ParseHelpArgs(args[1..]);

        if (string.Equals(first, "run", StringComparison.Ordinal))
        {
            if (args.Length >= 2 && IsHelpFlag(args[1]))
                return new CliOptions { Kind = CliCommandKind.Help, HelpTopic = "run" };

            return ParsePromptArgs(args[1..], true);
        }

        if (string.Equals(first, "chat", StringComparison.Ordinal)) return ParseChatArgs(args[1..]);

        if (string.Equals(first, "smoke", StringComparison.Ordinal)) return ParseSmokeArgs(args[1..]);

        if (string.Equals(first, "mcp", StringComparison.Ordinal)) return ParseMcpArgs(args[1..]);

        if (string.Equals(first, "init", StringComparison.Ordinal)) return ParseInitArgs(args[1..]);

        if (string.Equals(first, "config", StringComparison.Ordinal)) return ParseConfigArgs(args[1..]);

        if (string.Equals(first, "provider", StringComparison.Ordinal)) return ParseProviderArgs(args[1..]);

        if (string.Equals(first, "auth", StringComparison.Ordinal)) return ParseAuthArgs(args[1..]);

        if (string.Equals(first, "model", StringComparison.Ordinal)) return ParseModelArgs(args[1..]);

        if (string.Equals(first, "doctor", StringComparison.Ordinal)) return ParseDoctorArgs(args[1..]);

        if (string.Equals(first, "sandbox", StringComparison.Ordinal)) return ParseSandboxArgs(args[1..]);

        if (string.Equals(first, "session", StringComparison.Ordinal)) return ParseSessionArgs(args[1..]);

        if (first.StartsWith('-') && first.Length > 1)
            throw new CliUsageException($"Unknown option '{first}'.", HelpText.Overview);

        // Verbless invocation: the whole argument list is a prompt with optional --config.
        return ParsePromptArgs(args, false);
    }

    /// <summary>
    ///     解析 session 子命令：list 或 show &lt;sessionId&gt;；show 的会话 ID 在解析期即校验
    ///     （32 位十六进制），路径逃逸输入按使用错误处理。
    ///     Parses the session subcommands: list or show &lt;sessionId&gt;; the session id of show is
    ///     validated at parse time (32 hex characters), so path-escape input is a usage error.
    /// </summary>
    private static CliOptions ParseSessionArgs(string[] args)
    {
        var usage = HelpText.Session;
        if (args.Length == 0)
            throw new CliUsageException("session requires a subcommand: list or show.", usage);

        if (IsHelpFlag(args[0])) return new CliOptions { Kind = CliCommandKind.Help, HelpTopic = "session" };

        var subcommand = args[0].ToLowerInvariant();
        switch (subcommand)
        {
            case "list" :
                return args.Length > 1
                    ? throw new CliUsageException($"Unknown argument '{args[1]}' for session list.", usage)
                    : new CliOptions { Kind = CliCommandKind.Session, Subcommand = "list" };

            case "show" when args.Length != 2 :
                throw new CliUsageException("session show requires exactly one session id.", usage);

            case "show" :
                if (!SessionStorePaths.IsValidSessionId(args[1]))
                    throw new
                        CliUsageException($"'{args[1]}' is not a valid session id: exactly 32 hex characters are required.",
                                          usage);

                return new CliOptions { Kind = CliCommandKind.Session, Subcommand = "show", Primary = args[1] };

            default :
                throw new CliUsageException($"Unknown session subcommand '{args[0]}'.", usage);
        }
    }

    /// <summary>
    ///     解析 help 主题；未知主题按使用错误处理。
    ///     Parses the help topic; unknown topics are usage errors.
    /// </summary>
    private static CliOptions ParseHelpArgs(string[] args)
    {
        switch (args.Length)
        {
            case 0 :
                return new CliOptions { Kind = CliCommandKind.Help };
            case > 1 :
                throw new CliUsageException("help accepts at most one command name.", HelpText.Overview);
        }

        return !HelpText.IsKnownCommand(args[0])
            ? throw new CliUsageException($"Unknown help topic '{args[0]}'.", HelpText.Overview)
            : new CliOptions { Kind = CliCommandKind.Help, HelpTopic = args[0] };
    }

    /// <summary>
    ///     解析 run/无动词形式：`--` 之前只接受 --config 与 help 标志，`--` 之后全部是提示词。
    ///     Parses run/verbless forms: before `--` only --config and help flags are accepted; after `--` everything is the
    ///     prompt.
    /// </summary>
    private static CliOptions ParsePromptArgs(string[] args, bool withRunVerb)
    {
        var     usage       = withRunVerb ? HelpText.Run : HelpText.Verbless;
        string? configPath  = null;
        var     promptParts = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--", StringComparison.Ordinal))
            {
                // Everything after `--` is the prompt, including tokens that look like options.
                promptParts.AddRange(args[(i + 1)..]);
                return FinishPrompt(configPath, promptParts, usage);
            }

            if (string.Equals(args[i], "--config", StringComparison.Ordinal))
            {
                if (i + 1 >= args.Length)
                    throw new CliUsageException("The --config option requires a file path.", usage);

                configPath = args[++i];
                continue;
            }

            if (IsHelpFlag(args[i]))
                return new CliOptions { Kind = CliCommandKind.Help, HelpTopic = withRunVerb ? "run" : null };

            if (args[i].StartsWith('-') && args[i].Length > 1)
                throw new
                    CliUsageException($"Unknown option '{args[i]}'. To send text that starts with '-' as part of the prompt, put it after '--'.",
                                      usage);

            promptParts.Add(args[i]);
        }

        return FinishPrompt(configPath, promptParts, usage);
    }

    /// <summary>
    ///     合并提示词片段；缺失提示词按使用错误处理。
    ///     Joins prompt fragments; a missing prompt is a usage error.
    /// </summary>
    private static CliOptions FinishPrompt(string? configPath, List<string> promptParts, string usage)
    {
        var prompt = string.Join(' ', promptParts).Trim();
        return prompt.Length == 0
            ? throw new CliUsageException("A prompt is required.", usage)
            : new CliOptions { Kind = CliCommandKind.Run, Prompt = prompt, ConfigPath = configPath };
    }

    /// <summary>
    ///     解析 chat 参数；接受 --config、--session &lt;id&gt; 与 help 标志。chat 不接受提示词：
    ///     每个回合都在交互中逐行输入，把文本作为参数传入是重复的启动请求，按使用错误处理。
    ///     会话 ID 在解析期即校验（32 位十六进制），路径逃逸输入按使用错误处理。
    ///     Parses chat arguments; --config, --session &lt;id&gt; and help flags are
    ///     accepted. Chat takes no prompt — every turn is typed interactively, so
    ///     passing text as an argument is a duplicate start request and a usage
    ///     error. The session id is validated at parse time (32 hex characters),
    ///     so path-escape input is a usage error.
    /// </summary>
    private static CliOptions ParseChatArgs(string[] args)
    {
        const string usage = HelpText.Chat;

        string? configPath      = null;
        string? resumeSessionId = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (IsHelpFlag(args[i])) return new CliOptions { Kind = CliCommandKind.Help, HelpTopic = "chat" };

            if (string.Equals(args[i], "--session", StringComparison.Ordinal))
            {
                if (resumeSessionId is not null)
                    throw new CliUsageException("The --session option may be given at most once.", usage);

                if (i + 1 >= args.Length)
                    throw new CliUsageException("The --session option requires a session id.", usage);

                if (!SessionStorePaths.IsValidSessionId(args[i + 1]))
                    throw new
                        CliUsageException($"'{args[i + 1]}' is not a valid session id: exactly 32 hex characters are required.",
                                          usage);

                resumeSessionId = args[++i];
                continue;
            }

            if (!string.Equals(args[i], "--config", StringComparison.Ordinal))
                throw new
                    CliUsageException($"Unknown argument '{args[i]}' for chat. Chat takes no prompt; type each turn after it starts " +
                                      "(use `run -- \"text\"` for a one-shot prompt).", usage);
            if (i + 1 >= args.Length)
                throw new CliUsageException("The --config option requires a file path.", usage);

            configPath = args[++i];
        }

        return new CliOptions
            { Kind = CliCommandKind.Chat, ConfigPath = configPath, ResumeSessionId = resumeSessionId };
    }

    /// <summary>
    ///     解析 smoke 参数；只接受 --config 与 help 标志。
    ///     Parses smoke arguments; only --config and help flags are accepted.
    /// </summary>
    private static CliOptions ParseSmokeArgs(string[] args)
    {
        string? configPath = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (IsHelpFlag(args[i])) return new CliOptions { Kind = CliCommandKind.Help, HelpTopic = "smoke" };

            if (!string.Equals(args[i], "--config", StringComparison.Ordinal))
                throw new CliUsageException($"Unknown argument '{args[i]}' for smoke.", HelpText.Smoke);
            if (i + 1 >= args.Length)
                throw new CliUsageException("The --config option requires a file path.", HelpText.Smoke);

            configPath = args[++i];
        }

        return new CliOptions { Kind = CliCommandKind.Smoke, ConfigPath = configPath };
    }

    /// <summary>
    ///     解析 mcp 参数；不接受任何额外参数。
    ///     Parses mcp arguments; no extra arguments are accepted.
    /// </summary>
    private static CliOptions ParseMcpArgs(string[] args)
    {
        if (args.Length == 0) return new CliOptions { Kind = CliCommandKind.Mcp };

        return IsHelpFlag(args[0])
            ? new CliOptions { Kind = CliCommandKind.Help, HelpTopic = "mcp" }
            : throw new CliUsageException($"Unknown argument '{args[0]}' for mcp.", HelpText.Mcp);
    }

    /// <summary>
    ///     解析 init 参数；不接受任何额外参数。
    ///     Parses init arguments; no extra arguments are accepted.
    /// </summary>
    private static CliOptions ParseInitArgs(string[] args)
    {
        if (args.Length == 0) return new CliOptions { Kind = CliCommandKind.Init };

        return IsHelpFlag(args[0])
            ? new CliOptions { Kind = CliCommandKind.Help, HelpTopic = "init" }
            : throw new CliUsageException($"Unknown argument '{args[0]}' for init.", HelpText.Init);
    }

    /// <summary>
    ///     解析 config 子命令：show 或 set &lt;key&gt; &lt;value&gt;。
    ///     Parses the config subcommands: show or set &lt;key&gt; &lt;value&gt;.
    /// </summary>
    private static CliOptions ParseConfigArgs(string[] args)
    {
        var usage = HelpText.Config;
        if (args.Length == 0) throw new CliUsageException("config requires a subcommand: show or set.", usage);

        if (IsHelpFlag(args[0])) return new CliOptions { Kind = CliCommandKind.Help, HelpTopic = "config" };

        var subcommand = args[0].ToLowerInvariant();
        return subcommand switch
        {
            "show" when args.Length == 1 => new CliOptions { Kind = CliCommandKind.Config, Subcommand = "show" },
            "show" => throw new CliUsageException($"Unknown argument '{args[1]}' for config show.", usage),
            "set" when args.Length == 3 => new CliOptions
            {
                Kind = CliCommandKind.Config, Subcommand = "set", Primary = args[1], Secondary = args[2]
            },
            "set" => throw new CliUsageException("config set requires exactly: <key> <value>.", usage),
            _     => throw new CliUsageException($"Unknown config subcommand '{args[0]}'.", usage)
        };
    }

    /// <summary>
    ///     解析 provider 子命令：list / add &lt;name&gt; / use &lt;name&gt;。
    ///     Parses the provider subcommands: list / add &lt;name&gt; / use &lt;name&gt;.
    /// </summary>
    private static CliOptions ParseProviderArgs(string[] args)
    {
        var usage = HelpText.Provider;
        if (args.Length == 0) throw new CliUsageException("provider requires a subcommand: list, add, or use.", usage);

        if (IsHelpFlag(args[0])) return new CliOptions { Kind = CliCommandKind.Help, HelpTopic = "provider" };

        var subcommand = args[0].ToLowerInvariant();
        switch (subcommand)
        {
            case "list" :
                return args.Length > 1
                    ? throw new CliUsageException($"Unknown argument '{args[1]}' for provider list.", usage)
                    : new CliOptions { Kind = CliCommandKind.Provider, Subcommand = "list" };
            case "add" or "use" when args.Length != 2 :
                throw new CliUsageException($"provider {subcommand} requires exactly one profile name.", usage);
            case "add" or "use" :
                ValidateProfileName(args[1], usage);
                return new CliOptions { Kind = CliCommandKind.Provider, Subcommand = subcommand, Primary = args[1] };
            default :
                throw new CliUsageException($"Unknown provider subcommand '{args[0]}'.", usage);
        }
    }

    /// <summary>
    ///     解析 auth set：profile 名加上 --env 或 --store 之一。
    ///     Parses auth set: a profile name plus either --env or --store.
    /// </summary>
    private static CliOptions ParseAuthArgs(string[] args)
    {
        var usage = HelpText.Auth;
        if (args.Length == 0) throw new CliUsageException("auth requires a subcommand: set.", usage);

        if (IsHelpFlag(args[0])) return new CliOptions { Kind = CliCommandKind.Help, HelpTopic = "auth" };

        if (!string.Equals(args[0], "set", StringComparison.OrdinalIgnoreCase))
            throw new CliUsageException($"Unknown auth subcommand '{args[0]}'.", usage);

        string? profile  = null;
        string? envVar   = null;
        var     useStore = false;
        for (var i = 1; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--env", StringComparison.Ordinal))
            {
                if (i + 1 >= args.Length)
                    throw new CliUsageException("The --env option requires an environment variable name.", usage);

                envVar = args[++i];
                continue;
            }

            if (string.Equals(args[i], "--store", StringComparison.Ordinal))
            {
                useStore = true;
                continue;
            }

            if (profile is not null || args[i].StartsWith('-'))
                throw new CliUsageException($"Unknown argument '{args[i]}' for auth set.", usage);
            profile = args[i];
        }

        if (profile is null) throw new CliUsageException("auth set requires a profile name.", usage);

        ValidateProfileName(profile, usage);
        if (envVar is not null && useStore)
            throw new CliUsageException("auth set accepts either --env or --store, not both.", usage);

        return new CliOptions
        {
            Kind                = CliCommandKind.Auth,
            Subcommand          = "set",
            Primary             = profile,
            EnvironmentVariable = envVar,
            UseCredentialStore  = useStore
        };
    }

    /// <summary>
    ///     解析 model 子命令：list / add &lt;id&gt; --context-window &lt;n&gt; / use &lt;id&gt;，均可选 --provider。
    ///     Parses the model subcommands: list / add &lt;id&gt; --context-window &lt;n&gt; / use &lt;id&gt;, all with optional
    ///     --provider.
    /// </summary>
    private static CliOptions ParseModelArgs(string[] args)
    {
        var usage = HelpText.Model;
        if (args.Length == 0) throw new CliUsageException("model requires a subcommand: list, add, or use.", usage);

        if (IsHelpFlag(args[0])) return new CliOptions { Kind = CliCommandKind.Help, HelpTopic = "model" };

        var subcommand = args[0].ToLowerInvariant();
        switch (subcommand)
        {
            case "list" :
            {
                var provider = ReadProviderOption(args, 1, usage);
                return new CliOptions { Kind = CliCommandKind.Model, Subcommand = "list", ProviderName = provider };
            }
            case "add" when args.Length < 2 || args[1].StartsWith('-') :
                throw new CliUsageException("model add requires a model id.", usage);
            case "add" :
            {
                var modelId = args[1];
                ValidateModelId(modelId, usage);
                int? contextWindow = null;
                var  provider      = (string?)null;
                for (var i = 2; i < args.Length; i++)
                {
                    if (string.Equals(args[i], "--context-window", StringComparison.Ordinal))
                    {
                        if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out var parsed) || parsed <= 0)
                            throw new
                                CliUsageException("The --context-window option requires a positive integer (tokens).",
                                                  usage);

                        contextWindow = parsed;
                        i++;
                        continue;
                    }

                    if (!string.Equals(args[i], "--provider", StringComparison.Ordinal))
                        throw new CliUsageException($"Unknown argument '{args[i]}' for model add.", usage);
                    if (i + 1 >= args.Length)
                        throw new CliUsageException("The --provider option requires a profile name.", usage);

                    provider = args[++i];
                }

                if (contextWindow is null)
                    throw new
                        CliUsageException("model add requires --context-window <tokens>; the window is never inferred from the model name.",
                                          usage);

                return new CliOptions
                {
                    Kind                = CliCommandKind.Model,
                    Subcommand          = "add",
                    Primary             = modelId,
                    ContextWindowTokens = contextWindow,
                    ProviderName        = provider
                };
            }
            case "use" when args.Length < 2 || args[1].StartsWith('-') :
                throw new CliUsageException("model use requires a model id.", usage);
            case "use" :
            {
                ValidateModelId(args[1], usage);
                var provider = ReadProviderOption(args, 2, usage);
                return new CliOptions
                {
                    Kind         = CliCommandKind.Model,
                    Subcommand   = "use",
                    Primary      = args[1],
                    ProviderName = provider
                };
            }
            default :
                throw new CliUsageException($"Unknown model subcommand '{args[0]}'.", usage);
        }
    }

    /// <summary>
    ///     解析 doctor 参数；只接受可选的 --connect。
    ///     Parses doctor arguments; only the optional --connect flag is accepted.
    /// </summary>
    private static CliOptions ParseDoctorArgs(string[] args)
    {
        var connect = false;
        foreach (var arg in args)
        {
            if (IsHelpFlag(arg)) return new CliOptions { Kind = CliCommandKind.Help, HelpTopic = "doctor" };

            if (!string.Equals(arg, "--connect", StringComparison.Ordinal))
                throw new CliUsageException($"Unknown argument '{arg}' for doctor.", HelpText.Doctor);
            connect = true;
        }

        return new CliOptions { Kind = CliCommandKind.Doctor, Connect = connect };
    }

    /// <summary>
    ///     解析 sandbox 子命令：status、provision [--yes]、verify [--yes] [--case &lt;name&gt;[,&lt;name&gt;...]]
    ///     [--workspace &lt;path&gt;]，以及隐藏的 verify-worker（内部使用，不进公开帮助）。
    ///     Parses the sandbox subcommands: status, provision [--yes], verify
    ///     [--yes] [--case &lt;name&gt;[,&lt;name&gt;...]] [--workspace &lt;path&gt;], plus the
    ///     hidden verify-worker (internal use, absent from the public help).
    /// </summary>
    private static CliOptions ParseSandboxArgs(string[] args)
    {
        var usage = HelpText.Sandbox;
        if (args.Length == 0)
            throw new CliUsageException("sandbox requires a subcommand: status, provision, or verify.", usage);

        if (IsHelpFlag(args[0])) return new CliOptions { Kind = CliCommandKind.Help, HelpTopic = "sandbox" };

        var subcommand = args[0].ToLowerInvariant();
        switch (subcommand)
        {
            case "status" :
                return args.Length > 1
                    ? throw new CliUsageException($"Unknown argument '{args[1]}' for sandbox status.", usage)
                    : new CliOptions { Kind = CliCommandKind.Sandbox, Subcommand = "status" };

            case "provision" :
                var assumeYes = false;
                for (var i = 1; i < args.Length; i++)
                {
                    if (!string.Equals(args[i], "--yes", StringComparison.Ordinal))
                        throw new CliUsageException($"Unknown argument '{args[i]}' for sandbox provision.", usage);
                    assumeYes = true;
                }

                return new CliOptions
                    { Kind = CliCommandKind.Sandbox, Subcommand = "provision", AssumeYes = assumeYes };

            case "verify" :
                return ParseSandboxVerifyArgs(args, usage);

            case "verify-worker" :
                return ParseSandboxVerifyWorkerArgs(args, usage);

            default :
                throw new CliUsageException($"Unknown sandbox subcommand '{args[0]}'.", usage);
        }
    }

    /// <summary>
    ///     解析 sandbox verify：[--yes] [--case &lt;name&gt;[,&lt;name&gt;...]] [--workspace &lt;path&gt;]。
    ///     Parses sandbox verify: [--yes] [--case &lt;name&gt;[,&lt;name&gt;...]] [--workspace &lt;path&gt;].
    /// </summary>
    private static CliOptions ParseSandboxVerifyArgs(string[] args, string usage)
    {
        var           assumeYes  = false;
        string?       workspace  = null;
        List<string>? caseFilter = null;
        for (var i = 1; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--yes", StringComparison.Ordinal))
            {
                assumeYes = true;
                continue;
            }

            if (string.Equals(args[i], "--case", StringComparison.Ordinal))
            {
                if (i + 1 >= args.Length)
                    throw new CliUsageException("The --case option requires a comma-separated case name list.", usage);

                var names = args[++i]
                   .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (names.Length == 0)
                    throw new CliUsageException("The --case option requires at least one case name.", usage);

                caseFilter ??= [];
                caseFilter.AddRange(names);
                continue;
            }

            if (!string.Equals(args[i], "--workspace", StringComparison.Ordinal))
                throw new CliUsageException($"Unknown argument '{args[i]}' for sandbox verify.", usage);
            if (i + 1 >= args.Length)
                throw new CliUsageException("The --workspace option requires a directory path.", usage);

            workspace = args[++i];
            if (string.IsNullOrWhiteSpace(workspace))
                throw new CliUsageException("The --workspace option requires a non-empty directory path.", usage);
        }

        return new CliOptions
        {
            Kind            = CliCommandKind.Sandbox,
            Subcommand      = "verify",
            AssumeYes       = assumeYes,
            CaseFilter      = caseFilter,
            VerifyWorkspace = workspace
        };
    }

    /// <summary>
    ///     解析隐藏的 sandbox verify-worker：--marker &lt;path&gt; --hold-seconds &lt;n&gt; --workspace &lt;path&gt;
    ///     全部必填（由父 verify 进程构造，不面向用户）。
    ///     Parses the hidden sandbox verify-worker: --marker &lt;path&gt;
    ///     --hold-seconds &lt;n&gt; --workspace &lt;path&gt;, all required (constructed by
    ///     the parent verify process, not user facing).
    /// </summary>
    private static CliOptions ParseSandboxVerifyWorkerArgs(string[] args, string usage)
    {
        string? marker    = null;
        string? workspace = null;
        int?    hold      = null;
        for (var i = 1; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--marker", StringComparison.Ordinal))
            {
                if (i + 1 >= args.Length)
                    throw new CliUsageException("The --marker option requires a file path.", usage);

                marker = args[++i];
                if (string.IsNullOrWhiteSpace(marker))
                    throw new CliUsageException("The --marker option requires a non-empty file path.", usage);

                continue;
            }

            if (string.Equals(args[i], "--hold-seconds", StringComparison.Ordinal))
            {
                if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out var parsed) || parsed <= 0)
                    throw new CliUsageException("The --hold-seconds option requires a positive integer.", usage);

                hold = parsed;
                i++;
                continue;
            }

            if (!string.Equals(args[i], "--workspace", StringComparison.Ordinal))
                throw new CliUsageException($"Unknown argument '{args[i]}' for sandbox verify-worker.", usage);
            if (i + 1 >= args.Length)
                throw new CliUsageException("The --workspace option requires a directory path.", usage);

            workspace = args[++i];
            if (string.IsNullOrWhiteSpace(workspace))
                throw new CliUsageException("The --workspace option requires a non-empty directory path.", usage);
        }

        if (marker is null || hold is null || workspace is null)
            throw new
                CliUsageException("sandbox verify-worker requires --marker <path>, --hold-seconds <n>, and --workspace <path>.",
                                  usage);

        return new CliOptions
        {
            Kind              = CliCommandKind.Sandbox,
            Subcommand        = "verify-worker",
            VerifyMarkerPath  = marker,
            VerifyHoldSeconds = hold,
            VerifyWorkspace   = workspace
        };
    }

    /// <summary>
    ///     从位置开始读取可选的 --provider 选项。
    ///     Reads the optional --provider option starting at a position.
    /// </summary>
    private static string? ReadProviderOption(string[] args, int start, string usage)
    {
        string? provider = null;
        for (var i = start; i < args.Length; i++)
        {
            if (!string.Equals(args[i], "--provider", StringComparison.Ordinal))
                throw new CliUsageException($"Unknown argument '{args[i]}' for model list.", usage);
            if (i + 1 >= args.Length)
                throw new CliUsageException("The --provider option requires a profile name.", usage);

            provider = args[++i];
        }

        return provider;
    }

    /// <summary>
    ///     判断 profile 名称是否合法：字母、数字、点、下划线、连字符，不以 - 或 . 开头，长度 1-64。
    ///     Whether a profile name is valid: letters, digits, dot, underscore, hyphen; must not start with '-' or '.', length
    ///     1-64.
    /// </summary>
    public static bool IsValidProfileName(string name) =>
        name.Length is >= 1 and <= 64 && name[0] is not '-' and not '.' &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');


    /// <summary>
    ///     判断模型 id 是否合法：非空、无空白、长度不超过 200。
    ///     Whether a model id is valid: non-empty, no whitespace, at most 200 characters.
    /// </summary>
    public static bool IsValidModelId(string id) => id.Length is >= 1 and <= 200 && !id.Any(char.IsWhiteSpace);


    /// <summary>
    ///     校验 profile 名称：字母、数字、点、下划线、连字符，不以 - 或 . 开头，长度 1-64。
    ///     Validates a profile name: letters, digits, dot, underscore, hyphen; must not start with '-' or '.', length 1-64.
    /// </summary>
    private static void ValidateProfileName(string name, string usage)
    {
        if (!IsValidProfileName(name))
            throw new
                CliUsageException($"'{name}' is not a valid profile name. Use 1-64 characters: letters, digits, '.', '_', '-', not starting with '-' or '.'.",
                                  usage);
    }

    /// <summary>
    ///     校验模型 id：非空、无空白、长度不超过 200。
    ///     Validates a model id: non-empty, no whitespace, at most 200 characters.
    /// </summary>
    private static void ValidateModelId(string id, string usage)
    {
        if (!IsValidModelId(id))
            throw new CliUsageException($"'{id}' is not a valid model id: no whitespace, 1-200 characters.", usage);
    }

    /// <summary>
    ///     判断参数是否为 help 标志。
    ///     Whether an argument is a help flag.
    /// </summary>
    private static bool IsHelpFlag(string argument) => string.Equals(argument, "--help", StringComparison.Ordinal) ||
                                                       string.Equals(argument, "-h", StringComparison.Ordinal);
}
