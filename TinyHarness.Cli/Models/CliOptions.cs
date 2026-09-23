namespace TinyHarness.Cli.Models;

/// <summary>
/// 支持的顶层命令。
/// Supported top-level commands.
/// </summary>
internal enum CliCommandKind
{
    Run,
    Smoke,
    Mcp,
    Init,
    Config,
    Provider,
    Auth,
    Model,
    Doctor,
    Help,
    ProcessSmokeChild,
}

/// <summary>
/// 解析后的命令行意图。一个命令最多带一个主位置参数和一个次位置参数，
/// 例如 `config set &lt;key&gt; &lt;value&gt;` 或 `provider add &lt;name&gt;`。
///
/// The parsed command-line intent. A command carries at most one primary and one secondary positional
/// argument, e.g. `config set &lt;key&gt; &lt;value&gt;` or `provider add &lt;name&gt;`.
/// </summary>
internal sealed record CliOptions
{
    public required CliCommandKind Kind { get; init; }

    /// <summary>run/无动词形式的提示词。The prompt for run/verbless invocations.</summary>
    public string? Prompt { get; init; }

    public string? ConfigPath { get; init; }

    /// <summary>help 的主题命令；null 表示总览。Help topic command; null means the overview.</summary>
    public string? HelpTopic { get; init; }

    /// <summary>管理命令的子动词（show/set/list/add/use）。Sub-verb of a management command (show/set/list/add/use).</summary>
    public string? Subcommand { get; init; }

    /// <summary>主位置参数：profile 名、model id 或 config set 的键。Primary positional: profile name, model id, or config set key.</summary>
    public string? Primary { get; init; }

    /// <summary>次位置参数：config set 的值。Secondary positional: the config set value.</summary>
    public string? Secondary { get; init; }

    /// <summary>--provider 选项值。The --provider option value.</summary>
    public string? ProviderName { get; init; }

    /// <summary>model add 的 --context-window 值。The --context-window value of model add.</summary>
    public int? ContextWindowTokens { get; init; }

    /// <summary>auth set --env 选项值。The auth set --env option value.</summary>
    public string? EnvironmentVariable { get; init; }

    /// <summary>auth set --store 是否选择凭据存储。Whether auth set --store selected the credential store.</summary>
    public bool UseCredentialStore { get; init; }

    /// <summary>doctor --connect 是否联网测试。Whether doctor --connect was requested.</summary>
    public bool Connect { get; init; }
}
