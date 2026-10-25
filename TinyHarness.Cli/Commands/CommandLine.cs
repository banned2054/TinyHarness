using TinyHarness.Cli.Exceptions;
using TinyHarness.Cli.Models;
using TinyHarness.Core.Services.Persistence;

namespace TinyHarness.Cli.Commands;

/// <summary>
///     <para>
///         TinyHarness 命令行解析器。管理命令（init/config/provider/auth/model/doctor/sandbox）永远不作为
///         提示词发送给模型；与命令名冲突的普通文本使用 `run -- "文本"`，`--` 之后全部按提示词处理。
///         `chat` 启动交互会话，不接受提示词参数。
///     </para>
///     <para>
///         The TinyHarness command-line parser. Management commands
///         (init/config/provider/auth/model/doctor/sandbox) are never sent to the model as a prompt;
///         plain text that collides with a command name uses `run -- "text"`, and everything after `--`
///         is treated as the prompt. `chat` starts an interactive session and takes no prompt argument.
///     </para>
/// </summary>
internal static class CommandLine
{
    /// <summary>
    ///     <para>解析参数；失败抛出 <see cref="CliUsageException" />。</para>
    ///     <para>Parses the arguments; throws <see cref="CliUsageException" /> on failure.</para>
    /// </summary>
    public static CliOptions Parse(string[] args)
    {
        if (args.Length == 0) return new CliOptions { Kind = CliCommandKind.Chat };

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

        if (string.Equals(first, "--reasoning", StringComparison.Ordinal)) return ParsePromptArgs(args, false);

        if (first.StartsWith('-') && first.Length > 1)
            throw new CliUsageException($"Unknown option '{first}'.", HelpText.Overview);

        // 无动词调用也接受展示选项。
        return ParsePromptArgs(args, false);
    }

    /// <summary>
    ///     <para>解析 session list/show；会话 ID 必须为 32 位十六进制，路径逃逸输入按使用错误处理。</para>
    ///     <para>Parses session list/show; IDs must be 32 hex characters, and path escapes are usage errors.</para>
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
    ///     <para>解析 help 主题；未知主题按使用错误处理。</para>
    ///     <para>Parses the help topic; unknown topics are usage errors.</para>
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
    ///     <para>解析 run/无动词形式的配置和展示选项；`--` 之后全部是提示词。</para>
    ///     <para>
    ///         Parses configuration and display options for run/verbless forms; everything after `--` is the prompt.
    ///     </para>
    /// </summary>
    private static CliOptions ParsePromptArgs(string[] args, bool withRunVerb)
    {
        var                   usage            = withRunVerb ? HelpText.Run : HelpText.Verbless;
        string?               configPath       = null;
        ReasoningDisplayMode? reasoningDisplay = null;
        var                   promptParts      = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--", StringComparison.Ordinal))
            {
                // `--` 之后全部归入提示词，包括形似选项的 token。
                promptParts.AddRange(args[(i + 1)..]);
                return FinishPrompt(configPath, promptParts, usage, reasoningDisplay);
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

            if (string.Equals(args[i], "--reasoning", StringComparison.Ordinal))
            {
                reasoningDisplay = ReadReasoningDisplay(args, ref i, reasoningDisplay, usage);
                continue;
            }

            if (args[i].StartsWith('-') && args[i].Length > 1)
                throw new
                    CliUsageException($"Unknown option '{args[i]}'. To send text that starts with '-' as part of the prompt, put it after '--'.",
                                      usage);

            promptParts.Add(args[i]);
        }

        return FinishPrompt(configPath, promptParts, usage, reasoningDisplay);
    }

    /// <summary>
    ///     <para>合并提示词片段；缺失提示词按使用错误处理。</para>
    ///     <para>Joins prompt fragments; a missing prompt is a usage error.</para>
    /// </summary>
    private static CliOptions FinishPrompt(
        string? configPath, List<string> promptParts, string usage, ReasoningDisplayMode? reasoningDisplay)
    {
        var prompt = string.Join(' ', promptParts).Trim();
        return prompt.Length == 0
            ? throw new CliUsageException("A prompt is required.", usage)
            : new CliOptions
            {
                Kind             = CliCommandKind.Run, Prompt = prompt, ConfigPath = configPath,
                ReasoningDisplay = reasoningDisplay ?? ReasoningDisplayMode.Status
            };
    }

    /// <summary>
    ///     <para>严格解析一次思考展示选项，拒绝缺值、重复或未知模式。</para>
    ///     <para>Strictly parses one reasoning display option, rejecting missing, duplicate or unknown values.</para>
    /// </summary>
    private static ReasoningDisplayMode ReadReasoningDisplay(
        string[] args, ref int index, ReasoningDisplayMode? previous, string usage)
    {
        if (previous is not null)
            throw new CliUsageException("The --reasoning option may be given at most once.", usage);
        if (index + 1 >= args.Length)
            throw new CliUsageException("The --reasoning option requires status or content.", usage);

        return args[++index] switch
        {
            "status"  => ReasoningDisplayMode.Status,
            "content" => ReasoningDisplayMode.Content,
            _         => throw new CliUsageException("The --reasoning option requires status or content.", usage)
        };
    }

    /// <summary>
    ///     <para>
    ///         解析 chat 参数；接受 --config、--session、--reasoning 与 help 标志。chat 不接受提示词：
    ///         每个回合都在交互中逐行输入，把文本作为参数传入是重复的启动请求，按使用错误处理。
    ///         会话 ID 在解析期即校验（32 位十六进制），路径逃逸输入按使用错误处理。
    ///     </para>
    ///     <para>
    ///         Parses chat arguments; --config, --session, --reasoning and help flags are
    ///         accepted. Chat takes no prompt — every turn is typed interactively, so
    ///         passing text as an argument is a duplicate start request and a usage
    ///         error. The session id is validated at parse time (32 hex characters),
    ///         so path-escape input is a usage error.
    ///     </para>
    /// </summary>
    private static CliOptions ParseChatArgs(string[] args)
    {
        const string usage = HelpText.Chat;

        string?               configPath       = null;
        string?               resumeSessionId  = null;
        ReasoningDisplayMode? reasoningDisplay = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (IsHelpFlag(args[i])) return new CliOptions { Kind = CliCommandKind.Help, HelpTopic = "chat" };

            if (string.Equals(args[i], "--reasoning", StringComparison.Ordinal))
            {
                reasoningDisplay = ReadReasoningDisplay(args, ref i, reasoningDisplay, usage);
                continue;
            }

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
        {
            Kind             = CliCommandKind.Chat, ConfigPath = configPath, ResumeSessionId = resumeSessionId,
            ReasoningDisplay = reasoningDisplay ?? ReasoningDisplayMode.Status
        };
    }

    /// <summary>
    ///     <para>解析 smoke 参数；只接受 --config 与 help 标志。</para>
    ///     <para>Parses smoke arguments; only --config and help flags are accepted.</para>
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
    ///     <para>解析 mcp 参数；不接受任何额外参数。</para>
    ///     <para>Parses mcp arguments; no extra arguments are accepted.</para>
    /// </summary>
    private static CliOptions ParseMcpArgs(string[] args)
    {
        if (args.Length == 0) return new CliOptions { Kind = CliCommandKind.Mcp };

        return IsHelpFlag(args[0])
            ? new CliOptions { Kind = CliCommandKind.Help, HelpTopic = "mcp" }
            : throw new CliUsageException($"Unknown argument '{args[0]}' for mcp.", HelpText.Mcp);
    }

    /// <summary>
    ///     <para>解析 init 参数；不接受任何额外参数。</para>
    ///     <para>Parses init arguments; no extra arguments are accepted.</para>
    /// </summary>
    private static CliOptions ParseInitArgs(string[] args)
    {
        if (args.Length == 0) return new CliOptions { Kind = CliCommandKind.Init };

        return IsHelpFlag(args[0])
            ? new CliOptions { Kind = CliCommandKind.Help, HelpTopic = "init" }
            : throw new CliUsageException($"Unknown argument '{args[0]}' for init.", HelpText.Init);
    }

    /// <summary>
    ///     <para>解析 config 子命令：show 或 set &lt;key&gt; &lt;value&gt;。</para>
    ///     <para>Parses the config subcommands: show or set &lt;key&gt; &lt;value&gt;.</para>
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
    ///     <para>解析 provider 子命令：list / add &lt;name&gt; / use &lt;name&gt;。</para>
    ///     <para>Parses the provider subcommands: list / add &lt;name&gt; / use &lt;name&gt;.</para>
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
            case "add" or "edit" or "use" when args.Length != 2 :
                throw new CliUsageException($"provider {subcommand} requires exactly one profile name.", usage);
            case "add" or "edit" or "use" :
                ValidateProfileName(args[1], usage);
                return new CliOptions { Kind = CliCommandKind.Provider, Subcommand = subcommand, Primary = args[1] };
            default :
                throw new CliUsageException($"Unknown provider subcommand '{args[0]}'.", usage);
        }
    }

    /// <summary>
    ///     <para>解析 auth set：profile 名加上 --env 或 --store 之一。</para>
    ///     <para>Parses auth set: a profile name plus either --env or --store.</para>
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
    ///     <para>解析 model 子命令：list / add &lt;id&gt; --context-window &lt;n&gt; / use &lt;id&gt;，均可选 --provider。</para>
    ///     <para>
    ///         Parses the model subcommands: list / add &lt;id&gt; --context-window &lt;n&gt; / use &lt;id&gt;,
    ///         all with optional --provider.
    ///     </para>
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
                int?    contextWindow = null;
                string? displayName   = null, efforts = null, defaultEffort = null;
                var     provider      = (string?)null;
                for (var i = 2; i < args.Length; i++)
                {
                    if (args[i] is "--display-name" or "--efforts" or "--default-effort")
                    {
                        if (i + 1 >= args.Length) throw new CliUsageException($"{args[i]} requires a value.", usage);
                        switch (args[i++])
                        {
                            case "--display-name" :   displayName   = args[i]; break;
                            case "--efforts" :        efforts       = args[i]; break;
                            case "--default-effort" : defaultEffort = args[i]; break;
                        }

                        continue;
                    }

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
                    ModelDisplayName    = displayName, ModelEfforts = efforts, ModelDefaultEffort = defaultEffort,
                    Primary             = modelId,
                    ContextWindowTokens = contextWindow,
                    ProviderName        = provider
                };
            }
            case "edit" when args.Length < 2 || args[1].StartsWith('-') :
                throw new CliUsageException("model edit requires a model id.", usage);
            case "edit" :
                ValidateModelId(args[1], usage);
                return new CliOptions
                {
                    Kind         = CliCommandKind.Model, Subcommand = "edit", Primary = args[1],
                    ProviderName = ReadProviderOption(args, 2, usage)
                };
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
    ///     <para>解析 doctor 参数；只接受可选的 --connect。</para>
    ///     <para>Parses doctor arguments; only the optional --connect flag is accepted.</para>
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
    ///     <para>
    ///         解析 sandbox 子命令：status、provision [--yes]、verify [--yes] [--case &lt;name&gt;[,&lt;name&gt;...]]
    ///         [--workspace &lt;path&gt;]，以及隐藏的 verify-worker（内部使用，不进公开帮助）。
    ///     </para>
    ///     <para>
    ///         Parses the sandbox subcommands: status, provision [--yes], verify
    ///         [--yes] [--case &lt;name&gt;[,&lt;name&gt;...]] [--workspace &lt;path&gt;], plus the
    ///         hidden verify-worker (internal use, absent from the public help).
    ///     </para>
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
    ///     <para>解析 sandbox verify：[--yes] [--case &lt;name&gt;[,&lt;name&gt;...]] [--workspace &lt;path&gt;]。</para>
    ///     <para>Parses sandbox verify: [--yes] [--case &lt;name&gt;[,&lt;name&gt;...]] [--workspace &lt;path&gt;].</para>
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
    ///     <para>
    ///         解析隐藏的 sandbox verify-worker：--marker &lt;path&gt; --hold-seconds &lt;n&gt; --workspace &lt;path&gt;
    ///         全部必填（由父 verify 进程构造，不面向用户）。
    ///     </para>
    ///     <para>
    ///         Parses the hidden sandbox verify-worker: --marker &lt;path&gt;
    ///         --hold-seconds &lt;n&gt; --workspace &lt;path&gt;, all required (constructed by
    ///         the parent verify process, not user facing).
    ///     </para>
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
    ///     <para>从位置开始读取可选的 --provider 选项。</para>
    ///     <para>Reads the optional --provider option starting at a position.</para>
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
    ///     <para>判断 profile 名称是否合法：字母、数字、点、下划线、连字符，不以 - 或 . 开头，长度 1-64。</para>
    ///     <para>
    ///         Whether a profile name is valid: letters, digits, dot, underscore, hyphen; must not start with
    ///         '-' or '.', length 1-64.
    ///     </para>
    /// </summary>
    public static bool IsValidProfileName(string name) =>
        name.Length is >= 1 and <= 64 && name[0] is not '-' and not '.' &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');


    /// <summary>
    ///     <para>判断模型 id 是否合法：非空、无空白、长度不超过 200。</para>
    ///     <para>Whether a model id is valid: non-empty, no whitespace, at most 200 characters.</para>
    /// </summary>
    public static bool IsValidModelId(string id) => id.Length is >= 1 and <= 200 && !id.Any(char.IsWhiteSpace);


    /// <summary>
    ///     <para>校验 profile 名称：字母、数字、点、下划线、连字符，不以 - 或 . 开头，长度 1-64。</para>
    ///     <para>
    ///         Validates a profile name: letters, digits, dot, underscore, hyphen; must not start with '-' or '.',
    ///         length 1-64.
    ///     </para>
    /// </summary>
    private static void ValidateProfileName(string name, string usage)
    {
        if (!IsValidProfileName(name))
            throw new
                CliUsageException($"'{name}' is not a valid profile name. Use 1-64 characters: letters, digits, '.', '_', '-', not starting with '-' or '.'.",
                                  usage);
    }

    /// <summary>
    ///     <para>校验模型 id：非空、无空白、长度不超过 200。</para>
    ///     <para>Validates a model id: non-empty, no whitespace, at most 200 characters.</para>
    /// </summary>
    private static void ValidateModelId(string id, string usage)
    {
        if (!IsValidModelId(id))
            throw new CliUsageException($"'{id}' is not a valid model id: no whitespace, 1-200 characters.", usage);
    }

    /// <summary>
    ///     <para>判断参数是否为 help 标志。</para>
    ///     <para>Whether an argument is a help flag.</para>
    /// </summary>
    private static bool IsHelpFlag(string argument) => string.Equals(argument, "--help", StringComparison.Ordinal) ||
                                                       string.Equals(argument, "-h", StringComparison.Ordinal);
}
