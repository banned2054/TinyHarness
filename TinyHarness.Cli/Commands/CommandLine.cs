using TinyHarness.Cli.Exceptions;
using TinyHarness.Cli.Models;

namespace TinyHarness.Cli.Commands;

/// <summary>
/// TinyHarness 命令行解析器。管理命令（init/config/provider/auth/model/doctor）永远不作为提示词发送给
/// 模型；与命令名冲突的普通文本使用 `run -- "文本"`，`--` 之后全部按提示词处理。
///
/// The TinyHarness command-line parser. Management commands (init/config/provider/auth/model/doctor) are never
/// sent to the model as a prompt; plain text that collides with a command name uses `run -- "text"`, and
/// everything after `--` is treated as the prompt.
/// </summary>
internal static class CommandLine
{
    /// <summary>
    /// 解析参数；失败抛出 <see cref="CliUsageException"/>。
    /// Parses the arguments; throws <see cref="CliUsageException"/> on failure.
    /// </summary>
    public static CliOptions Parse(string[] args)
    {
        if (args.Length == 0)
        {
            throw new CliUsageException("No command or prompt was given.", HelpText.Overview);
        }

        var first = args[0];
        if (string.Equals(first, "process-smoke-child", StringComparison.Ordinal))
        {
            return new CliOptions { Kind = CliCommandKind.ProcessSmokeChild };
        }

        if (string.Equals(first, "--", StringComparison.Ordinal))
        {
            return ParsePromptArgs(args, withRunVerb : false);
        }

        if (string.Equals(first, "help", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(first, "--help", StringComparison.Ordinal)         ||
            string.Equals(first, "-h", StringComparison.Ordinal))
        {
            return ParseHelpArgs(args[1..]);
        }

        if (string.Equals(first, "run", StringComparison.Ordinal))
        {
            if (args.Length >= 2 && IsHelpFlag(args[1]))
            {
                return new CliOptions { Kind = CliCommandKind.Help, HelpTopic = "run" };
            }

            return ParsePromptArgs(args[1..], withRunVerb : true);
        }

        if (string.Equals(first, "smoke", StringComparison.Ordinal))
        {
            return ParseSmokeArgs(args[1..]);
        }

        if (string.Equals(first, "init", StringComparison.Ordinal))
        {
            return ParseInitArgs(args[1..]);
        }

        if (string.Equals(first, "config", StringComparison.Ordinal))
        {
            return ParseConfigArgs(args[1..]);
        }

        if (string.Equals(first, "provider", StringComparison.Ordinal))
        {
            return ParseProviderArgs(args[1..]);
        }

        if (string.Equals(first, "auth", StringComparison.Ordinal))
        {
            return ParseAuthArgs(args[1..]);
        }

        if (string.Equals(first, "model", StringComparison.Ordinal))
        {
            return ParseModelArgs(args[1..]);
        }

        if (string.Equals(first, "doctor", StringComparison.Ordinal))
        {
            return ParseDoctorArgs(args[1..]);
        }

        if (first.StartsWith('-') && first.Length > 1)
        {
            throw new CliUsageException($"Unknown option '{first}'.", HelpText.Overview);
        }

        // Verbless invocation: the whole argument list is a prompt with optional --config.
        return ParsePromptArgs(args, withRunVerb : false);
    }

    /// <summary>
    /// 解析 help 主题；未知主题按使用错误处理。
    /// Parses the help topic; unknown topics are usage errors.
    /// </summary>
    private static CliOptions ParseHelpArgs(string[] args)
    {
        if (args.Length == 0)
        {
            return new CliOptions { Kind = CliCommandKind.Help };
        }

        if (args.Length > 1)
        {
            throw new CliUsageException("help accepts at most one command name.", HelpText.Overview);
        }

        if (!HelpText.IsKnownCommand(args[0]))
        {
            throw new CliUsageException($"Unknown help topic '{args[0]}'.", HelpText.Overview);
        }

        return new CliOptions { Kind = CliCommandKind.Help, HelpTopic = args[0] };
    }

    /// <summary>
    /// 解析 run/无动词形式：`--` 之前只接受 --config 与 help 标志，`--` 之后全部是提示词。
    ///
    /// Parses run/verbless forms: before `--` only --config and help flags are accepted; after `--` everything is the prompt.
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
                {
                    throw new CliUsageException("The --config option requires a file path.", usage);
                }

                configPath = args[++i];
                continue;
            }

            if (IsHelpFlag(args[i]))
            {
                return new CliOptions { Kind = CliCommandKind.Help, HelpTopic = withRunVerb ? "run" : null };
            }

            if (args[i].StartsWith('-') && args[i].Length > 1)
            {
                throw new CliUsageException(
                                            $"Unknown option '{args[i]}'. To send text that starts with '-' as part of the prompt, put it after '--'.",
                                            usage);
            }

            promptParts.Add(args[i]);
        }

        return FinishPrompt(configPath, promptParts, usage);
    }

    /// <summary>
    /// 合并提示词片段；缺失提示词按使用错误处理。
    /// Joins prompt fragments; a missing prompt is a usage error.
    /// </summary>
    private static CliOptions FinishPrompt(string? configPath, List<string> promptParts, string usage)
    {
        var prompt = string.Join(' ', promptParts).Trim();
        if (prompt.Length == 0)
        {
            throw new CliUsageException("A prompt is required.", usage);
        }

        return new CliOptions { Kind = CliCommandKind.Run, Prompt = prompt, ConfigPath = configPath };
    }

    /// <summary>
    /// 解析 smoke 参数；只接受 --config 与 help 标志。
    /// Parses smoke arguments; only --config and help flags are accepted.
    /// </summary>
    private static CliOptions ParseSmokeArgs(string[] args)
    {
        string? configPath = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (IsHelpFlag(args[i]))
            {
                return new CliOptions { Kind = CliCommandKind.Help, HelpTopic = "smoke" };
            }

            if (string.Equals(args[i], "--config", StringComparison.Ordinal))
            {
                if (i + 1 >= args.Length)
                {
                    throw new CliUsageException("The --config option requires a file path.", HelpText.Smoke);
                }

                configPath = args[++i];
                continue;
            }

            throw new CliUsageException($"Unknown argument '{args[i]}' for smoke.", HelpText.Smoke);
        }

        return new CliOptions { Kind = CliCommandKind.Smoke, ConfigPath = configPath };
    }

    /// <summary>
    /// 解析 init 参数；不接受任何额外参数。
    /// Parses init arguments; no extra arguments are accepted.
    /// </summary>
    private static CliOptions ParseInitArgs(string[] args)
    {
        if (args.Length == 0)
        {
            return new CliOptions { Kind = CliCommandKind.Init };
        }

        if (IsHelpFlag(args[0]))
        {
            return new CliOptions { Kind = CliCommandKind.Help, HelpTopic = "init" };
        }

        throw new CliUsageException($"Unknown argument '{args[0]}' for init.", HelpText.Init);
    }

    /// <summary>
    /// 解析 config 子命令：show 或 set &lt;key&gt; &lt;value&gt;。
    /// Parses the config subcommands: show or set &lt;key&gt; &lt;value&gt;.
    /// </summary>
    private static CliOptions ParseConfigArgs(string[] args)
    {
        var usage = HelpText.Config;
        if (args.Length == 0)
        {
            throw new CliUsageException("config requires a subcommand: show or set.", usage);
        }

        if (IsHelpFlag(args[0]))
        {
            return new CliOptions { Kind = CliCommandKind.Help, HelpTopic = "config" };
        }

        var subcommand = args[0].ToLowerInvariant();
        switch (subcommand)
        {
            case "show" when args.Length == 1 :
                return new CliOptions { Kind = CliCommandKind.Config, Subcommand = "show" };
            case "show" :
                throw new CliUsageException($"Unknown argument '{args[1]}' for config show.", usage);
            case "set" when args.Length == 3 :
                return new CliOptions
                {
                    Kind       = CliCommandKind.Config,
                    Subcommand = "set",
                    Primary    = args[1],
                    Secondary  = args[2],
                };
            case "set" :
                throw new CliUsageException("config set requires exactly: <key> <value>.", usage);
            default :
                throw new CliUsageException($"Unknown config subcommand '{args[0]}'.", usage);
        }
    }

    /// <summary>
    /// 解析 provider 子命令：list / add &lt;name&gt; / use &lt;name&gt;。
    /// Parses the provider subcommands: list / add &lt;name&gt; / use &lt;name&gt;.
    /// </summary>
    private static CliOptions ParseProviderArgs(string[] args)
    {
        var usage = HelpText.Provider;
        if (args.Length == 0)
        {
            throw new CliUsageException("provider requires a subcommand: list, add, or use.", usage);
        }

        if (IsHelpFlag(args[0]))
        {
            return new CliOptions { Kind = CliCommandKind.Help, HelpTopic = "provider" };
        }

        var subcommand = args[0].ToLowerInvariant();
        if (subcommand == "list")
        {
            if (args.Length > 1)
            {
                throw new CliUsageException($"Unknown argument '{args[1]}' for provider list.", usage);
            }

            return new CliOptions { Kind = CliCommandKind.Provider, Subcommand = "list" };
        }

        if (subcommand is "add" or "use")
        {
            if (args.Length != 2)
            {
                throw new CliUsageException($"provider {subcommand} requires exactly one profile name.", usage);
            }

            ValidateProfileName(args[1], usage);
            return new CliOptions { Kind = CliCommandKind.Provider, Subcommand = subcommand, Primary = args[1] };
        }

        throw new CliUsageException($"Unknown provider subcommand '{args[0]}'.", usage);
    }

    /// <summary>
    /// 解析 auth set：profile 名加上 --env 或 --store 之一。
    /// Parses auth set: a profile name plus either --env or --store.
    /// </summary>
    private static CliOptions ParseAuthArgs(string[] args)
    {
        var usage = HelpText.Auth;
        if (args.Length == 0)
        {
            throw new CliUsageException("auth requires a subcommand: set.", usage);
        }

        if (IsHelpFlag(args[0]))
        {
            return new CliOptions { Kind = CliCommandKind.Help, HelpTopic = "auth" };
        }

        if (!string.Equals(args[0], "set", StringComparison.OrdinalIgnoreCase))
        {
            throw new CliUsageException($"Unknown auth subcommand '{args[0]}'.", usage);
        }

        string? profile  = null;
        string? envVar   = null;
        var     useStore = false;
        for (var i = 1; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--env", StringComparison.Ordinal))
            {
                if (i + 1 >= args.Length)
                {
                    throw new CliUsageException("The --env option requires an environment variable name.", usage);
                }

                envVar = args[++i];
                continue;
            }

            if (string.Equals(args[i], "--store", StringComparison.Ordinal))
            {
                useStore = true;
                continue;
            }

            if (profile is null && !args[i].StartsWith('-'))
            {
                profile = args[i];
                continue;
            }

            throw new CliUsageException($"Unknown argument '{args[i]}' for auth set.", usage);
        }

        if (profile is null)
        {
            throw new CliUsageException("auth set requires a profile name.", usage);
        }

        ValidateProfileName(profile, usage);
        if (envVar is not null && useStore)
        {
            throw new CliUsageException("auth set accepts either --env or --store, not both.", usage);
        }

        return new CliOptions
        {
            Kind                = CliCommandKind.Auth,
            Subcommand          = "set",
            Primary             = profile,
            EnvironmentVariable = envVar,
            UseCredentialStore  = useStore,
        };
    }

    /// <summary>
    /// 解析 model 子命令：list / add &lt;id&gt; --context-window &lt;n&gt; / use &lt;id&gt;，均可选 --provider。
    ///
    /// Parses the model subcommands: list / add &lt;id&gt; --context-window &lt;n&gt; / use &lt;id&gt;, all with optional --provider.
    /// </summary>
    private static CliOptions ParseModelArgs(string[] args)
    {
        var usage = HelpText.Model;
        if (args.Length == 0)
        {
            throw new CliUsageException("model requires a subcommand: list, add, or use.", usage);
        }

        if (IsHelpFlag(args[0]))
        {
            return new CliOptions { Kind = CliCommandKind.Help, HelpTopic = "model" };
        }

        var subcommand = args[0].ToLowerInvariant();
        if (subcommand == "list")
        {
            var provider = ReadProviderOption(args, 1, usage);
            return new CliOptions { Kind = CliCommandKind.Model, Subcommand = "list", ProviderName = provider };
        }

        if (subcommand == "add")
        {
            if (args.Length < 2 || args[1].StartsWith('-'))
            {
                throw new CliUsageException("model add requires a model id.", usage);
            }

            var modelId = args[1];
            ValidateModelId(modelId, usage);
            int? contextWindow = null;
            var  provider      = (string?)null;
            for (var i = 2; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--context-window", StringComparison.Ordinal))
                {
                    if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out var parsed) || parsed <= 0)
                    {
                        throw new CliUsageException(
                                                    "The --context-window option requires a positive integer (tokens).",
                                                    usage);
                    }

                    contextWindow = parsed;
                    i++;
                    continue;
                }

                if (string.Equals(args[i], "--provider", StringComparison.Ordinal))
                {
                    if (i + 1 >= args.Length)
                    {
                        throw new CliUsageException("The --provider option requires a profile name.", usage);
                    }

                    provider = args[++i];
                    continue;
                }

                throw new CliUsageException($"Unknown argument '{args[i]}' for model add.", usage);
            }

            if (contextWindow is null)
            {
                throw new CliUsageException(
                                            "model add requires --context-window <tokens>; the window is never inferred from the model name.",
                                            usage);
            }

            return new CliOptions
            {
                Kind                = CliCommandKind.Model,
                Subcommand          = "add",
                Primary             = modelId,
                ContextWindowTokens = contextWindow,
                ProviderName        = provider,
            };
        }

        if (subcommand == "use")
        {
            if (args.Length < 2 || args[1].StartsWith('-'))
            {
                throw new CliUsageException("model use requires a model id.", usage);
            }

            ValidateModelId(args[1], usage);
            var provider = ReadProviderOption(args, 2, usage);
            return new CliOptions
            {
                Kind         = CliCommandKind.Model,
                Subcommand   = "use",
                Primary      = args[1],
                ProviderName = provider,
            };
        }

        throw new CliUsageException($"Unknown model subcommand '{args[0]}'.", usage);
    }

    /// <summary>
    /// 解析 doctor 参数；只接受可选的 --connect。
    /// Parses doctor arguments; only the optional --connect flag is accepted.
    /// </summary>
    private static CliOptions ParseDoctorArgs(string[] args)
    {
        var connect = false;
        foreach (var arg in args)
        {
            if (IsHelpFlag(arg))
            {
                return new CliOptions { Kind = CliCommandKind.Help, HelpTopic = "doctor" };
            }

            if (string.Equals(arg, "--connect", StringComparison.Ordinal))
            {
                connect = true;
                continue;
            }

            throw new CliUsageException($"Unknown argument '{arg}' for doctor.", HelpText.Doctor);
        }

        return new CliOptions { Kind = CliCommandKind.Doctor, Connect = connect };
    }

    /// <summary>
    /// 从位置开始读取可选的 --provider 选项。
    /// Reads the optional --provider option starting at a position.
    /// </summary>
    private static string? ReadProviderOption(string[] args, int start, string usage)
    {
        string? provider = null;
        for (var i = start; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--provider", StringComparison.Ordinal))
            {
                if (i + 1 >= args.Length)
                {
                    throw new CliUsageException("The --provider option requires a profile name.", usage);
                }

                provider = args[++i];
                continue;
            }

            throw new CliUsageException($"Unknown argument '{args[i]}' for model list.", usage);
        }

        return provider;
    }

    /// <summary>
    /// 判断 profile 名称是否合法：字母、数字、点、下划线、连字符，不以 - 或 . 开头，长度 1-64。
    ///
    /// Whether a profile name is valid: letters, digits, dot, underscore, hyphen; must not start with '-' or '.', length 1-64.
    /// </summary>
    public static bool IsValidProfileName(string name) =>
        name.Length is >= 1 and <= 64 && name[0] is not '-' and not '.' &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    /// <summary>
    /// 判断模型 id 是否合法：非空、无空白、长度不超过 200。
    /// Whether a model id is valid: non-empty, no whitespace, at most 200 characters.
    /// </summary>
    public static bool IsValidModelId(string id) => id.Length is >= 1 and <= 200 && !id.Any(char.IsWhiteSpace);

    /// <summary>
    /// 校验 profile 名称：字母、数字、点、下划线、连字符，不以 - 或 . 开头，长度 1-64。
    ///
    /// Validates a profile name: letters, digits, dot, underscore, hyphen; must not start with '-' or '.', length 1-64.
    /// </summary>
    private static void ValidateProfileName(string name, string usage)
    {
        if (!IsValidProfileName(name))
        {
            throw new CliUsageException(
                                        $"'{name}' is not a valid profile name. Use 1-64 characters: letters, digits, '.', '_', '-', not starting with '-' or '.'.",
                                        usage);
        }
    }

    /// <summary>
    /// 校验模型 id：非空、无空白、长度不超过 200。
    /// Validates a model id: non-empty, no whitespace, at most 200 characters.
    /// </summary>
    private static void ValidateModelId(string id, string usage)
    {
        if (!IsValidModelId(id))
        {
            throw new CliUsageException($"'{id}' is not a valid model id: no whitespace, 1-200 characters.", usage);
        }
    }

    /// <summary>
    /// 判断参数是否为 help 标志。
    /// Whether an argument is a help flag.
    /// </summary>
    private static bool IsHelpFlag(string argument) =>
        string.Equals(argument, "--help", StringComparison.Ordinal) ||
        string.Equals(argument, "-h", StringComparison.Ordinal);
}
