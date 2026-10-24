using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;

namespace TinyHarness.Core.Models.Configuration;

/// <summary>
///     用户配置中一个 provider profile 声明的模型及其显式声明的上下文窗口；窗口大小不从模型名称推断。
///     A model declared by a provider profile in the user config, with its explicitly declared context window;
///     the window size is never inferred from the model name.
/// </summary>
public sealed record UserProfileModel
{
    public string Id { get; init; } = string.Empty;

    /// <summary>
    ///     <para>可选显示名，仅用于界面展示；不参与请求标识、选择键或模型身份。</para>
    ///     <para>Optional display name for presentation only; never part of request identity, selection keys, or model identity.</para>
    /// </summary>
    public string? DisplayName { get; init; }

    public int ContextWindowTokens { get; init; }

    /// <summary>
    ///     <para>
    ///         该模型声明支持的推理强度档位集合；null 表示未声明能力，此时只能选择不发送强度参数，
    ///         不从模型名称或同 profile 的其他模型推断。
    ///     </para>
    ///     <para>
    ///         The reasoning effort levels this model declares; null means undeclared capability, in which case only
    ///         omitting the effort parameter is selectable — never inferred from model names or sibling models.
    ///     </para>
    /// </summary>
    public IReadOnlyList<ModelReasoningEffort>? ReasoningEfforts { get; init; }

    /// <summary>
    ///     <para>
    ///         模型默认强度档位；null 表示不发送强度参数（由服务决定）。声明时必须属于
    ///         <see cref="ReasoningEfforts" /> 集合，且不会在切换模型时自动带到其他模型。
    ///     </para>
    ///     <para>
    ///         The model's default effort; null sends no effort parameter and leaves the decision to the service.
    ///         When declared it must belong to <see cref="ReasoningEfforts" /> and is never carried over to another model.
    ///     </para>
    /// </summary>
    public ModelReasoningEffort? DefaultReasoningEffort { get; init; }
}

/// <summary>
///     命名 provider profile：endpoint、API key 引用（环境变量名或系统凭据存储条目）与模型列表。
///     普通 JSON 只保存引用，不保存密钥值。
///     A named provider profile: endpoint plus API-key references (an environment variable name or a system
///     credential-store entry). Plain JSON stores references only, never the secret value.
/// </summary>
public sealed record UserProfile
{
    public string Name { get; init; } = string.Empty;

    public string Endpoint { get; init; } = string.Empty;

    public string ApiKeyEnvironmentVariable { get; init; } = string.Empty;

    public string ApiKeyCredentialTarget { get; init; } = string.Empty;

    /// <summary>
    ///     该 profile 使用的 Chat API 协议；只能显式选择，不按模型名猜测。旧配置缺省该字段时沿用
    ///     Chat Completions。
    ///     The Chat API protocol this profile uses; an explicit choice that is never guessed from model names.
    ///     Legacy configurations without this field keep Chat Completions.
    /// </summary>
    public ChatApiKind ChatApi { get; init; } = ChatApiKind.ChatCompletions;

    public string DefaultModel { get; init; } = string.Empty;

    public IReadOnlyList<UserProfileModel> Models { get; init; } = [];
}

/// <summary>
///     用户配置中的全局默认设置；字段缺省时沿用 <see cref="TinyHarnessConfig" /> 的内置默认值。
///     不包含 workspaceRoot：工作区属于项目语义，由项目配置或当前目录决定。
///     Global defaults in the user config; absent fields fall back to the built-in defaults of
///     <see cref="TinyHarnessConfig" />. Deliberately has no workspaceRoot: the workspace is project
///     semantics, decided by the project config or the current directory.
/// </summary>
public sealed record UserConfigSettings
{
    /// <summary>
    ///     <para>可信运行预算；项目配置不能覆盖。</para>
    ///     <para>Trusted run budget; project configuration cannot override it.</para>
    /// </summary>
    public RunBudgetLimits? RunBudget { get; init; }

    public int? MaxAgentSteps { get; init; }

    public int? ReservedOutputTokens { get; init; }

    public int? CompactionThreshold { get; init; }

    public int? DefaultToolTimeoutSeconds { get; init; }

    public string? SessionDirectory { get; init; }

    /// <summary>
    ///     受保护会话存储目录（M13.1，PLAN §7.1）：可续聊会话快照、提交审计与锁文件的用户级位置。
    ///     只存放于用户配置（可信层）；必须是绝对路径，且经词法与链接检查后位于组合工作区之外，
    ///     否则拒绝使用。缺省时使用 %LOCALAPPDATA%\tinyharness\sessions。
    ///     The protected session-store directory (M13.1, PLAN §7.1): the user-level
    ///     location of resumable session snapshots, their commit audit and lock files.
    ///     It lives in the user config (the trusted layer) only, must be absolute,
    ///     and must resolve — lexically and through links — outside the composition
    ///     workspace or it is refused. When unset, %LOCALAPPDATA%\tinyharness\sessions
    ///     is used.
    /// </summary>
    public string? SessionStoreDirectory { get; init; }

    public IReadOnlyList<CommandRule>? CommandRules { get; init; }

    /// <summary>
    ///     一次性只读 MCP worker 的可信设置。仅存放在用户配置中；MCP 请求和目标项目配置不能修改它。
    ///     Trusted settings for the one-shot read-only MCP worker. Stored only in user config; MCP requests
    ///     and target-project config cannot change them.
    /// </summary>
    public UserWorkerSettings? Worker { get; init; }

    /// <summary>
    ///     Windows 沙箱的可信设置。仅存放在用户配置中；目标项目配置、命令规则和模型请求都不能
    ///     修改或覆盖它。缺省（null 或 enabled=false）保持宿主执行。
    ///     Trusted settings for the Windows sandbox. Stored only in user config; the
    ///     target project's config, command rules, and model requests cannot change
    ///     or override them. Absent (null or enabled=false) keeps host execution.
    /// </summary>
    public WindowsSandboxSettings? WindowsSandbox { get; init; }
}

/// <summary>
///     MCP worker 的宿主设置。空字段沿用保守默认值；显式字段由宿主再按稳定硬上限校验。
///     Host settings for the MCP worker. Missing fields use conservative defaults; explicit values are
///     checked by the host against stable hard ceilings.
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
///     TinyHarness 用户配置文档：默认 provider profile、profile 列表与全局默认设置。
///     The TinyHarness user config document: default provider profile, profile list, and global defaults.
/// </summary>
public sealed record UserConfig
{
    public string? DefaultProfile { get; init; }

    public IReadOnlyList<UserProfile> Profiles { get; init; } = [];

    public UserConfigSettings? Settings { get; init; }
}
