namespace TinyHarness.Core.Models.Configuration;

/// <summary>
/// 用户配置中一个 provider profile 声明的模型及其显式声明的上下文窗口；窗口大小不从模型名称推断。
///
/// A model declared by a provider profile in the user config, with its explicitly declared context window;
/// the window size is never inferred from the model name.
/// </summary>
public sealed record UserProfileModel
{
    public string Id { get; init; } = string.Empty;

    public int ContextWindowTokens { get; init; }
}

/// <summary>
/// 命名 provider profile：endpoint、API key 引用（环境变量名或系统凭据存储条目）与模型列表。
/// 普通 JSON 只保存引用，不保存密钥值。
///
/// A named provider profile: endpoint plus API-key references (an environment variable name or a system
/// credential-store entry). Plain JSON stores references only, never the secret value.
/// </summary>
public sealed record UserProfile
{
    public string Name { get; init; } = string.Empty;

    public string Endpoint { get; init; } = string.Empty;

    public string ApiKeyEnvironmentVariable { get; init; } = string.Empty;

    public string ApiKeyCredentialTarget { get; init; } = string.Empty;

    public string DefaultModel { get; init; } = string.Empty;

    public IReadOnlyList<UserProfileModel> Models { get; init; } = [];
}

/// <summary>
/// 用户配置中的全局默认设置；字段缺省时沿用 <see cref="TinyHarnessConfig"/> 的内置默认值。
/// 不包含 workspaceRoot：工作区属于项目语义，由项目配置或当前目录决定。
///
/// Global defaults in the user config; absent fields fall back to the built-in defaults of
/// <see cref="TinyHarnessConfig"/>. Deliberately has no workspaceRoot: the workspace is project
/// semantics, decided by the project config or the current directory.
/// </summary>
public sealed record UserConfigSettings
{
    public int? MaxAgentSteps { get; init; }

    public int? ReservedOutputTokens { get; init; }

    public int? CompactionThreshold { get; init; }

    public int? DefaultToolTimeoutSeconds { get; init; }

    public string? SessionDirectory { get; init; }

    public IReadOnlyList<CommandRule>? CommandRules { get; init; }

    /// <summary>
    /// 一次性只读 MCP worker 的可信设置。仅存放在用户配置中；MCP 请求和目标项目配置不能修改它。
    /// Trusted settings for the one-shot read-only MCP worker. Stored only in user config; MCP requests
    /// and target-project config cannot change them.
    /// </summary>
    public UserWorkerSettings? Worker { get; init; }
}

/// <summary>
/// MCP worker 的宿主设置。空字段沿用保守默认值；显式字段由宿主再按稳定硬上限校验。
/// Host settings for the MCP worker. Missing fields use conservative defaults; explicit values are
/// checked by the host against stable hard ceilings.
/// </summary>
public sealed record UserWorkerSettings
{
    public string? WorkspaceRoot { get; init; }

    public int? RunTimeoutSeconds { get; init; }

    public int? MaxAgentSteps { get; init; }

    public int? DefaultToolTimeoutSeconds { get; init; }

    public int? MaxTaskPackageCharacters { get; init; }

    public int? MaxToolCalls { get; init; }

    public int? MaxToolOutputCharacters { get; init; }

    public int? MaxContextTokensPerRequest { get; init; }

    public int? MaxCumulativeContextTokens { get; init; }

    public int? MaxModelResponseCharacters { get; init; }
}

/// <summary>
/// TinyHarness 用户配置文档：默认 provider profile、profile 列表与全局默认设置。
///
/// The TinyHarness user config document: default provider profile, profile list, and global defaults.
/// </summary>
public sealed record UserConfig
{
    public string? DefaultProfile { get; init; }

    public IReadOnlyList<UserProfile> Profiles { get; init; } = [];

    public UserConfigSettings? Settings { get; init; }
}
