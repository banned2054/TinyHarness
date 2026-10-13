using TinyHarness.Core.Models.Session;

namespace TinyHarness.Core.Models.Persistence;

/// <summary>
///     受保护会话存储中的版本化会话快照（M13.1，PLAN §7.1）：会话身份、固定工作区与模型身份、
///     单调提交序号和全部用户回合的检查视图。回合内容在写入前脱敏；本类型是检查产物，
///     不携带密钥、可执行授权或可回放的计划，无损续聊所需的协议数据属于后续切片的格式扩展。
///     The versioned session snapshot in the protected session store (M13.1,
///     PLAN §7.1): session identity, the fixed workspace and model identity, a
///     monotonic commit sequence, and an inspection view of every user turn.
///     Turn content is redacted before it is written; this type is an
///     inspection artifact and carries no secrets, no executable grants and no
///     replayable plans — the protocol data needed for lossless resume belongs
///     to a later format extension.
/// </summary>
public sealed record PersistedSession
{
    /// <summary>
    ///     当前格式版本；读取端只接受已知版本，未知版本按错误拒绝而不是猜测解释。
    ///     The current format version; readers accept known versions only and reject unknown ones instead of guessing.
    /// </summary>
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;

    public string Id { get; init; } = string.Empty;

    public string WorkspaceRoot { get; init; } = string.Empty;

    public SessionModelIdentity? Model { get; init; }

    public DateTimeOffset CreatedUtc { get; init; }

    /// <summary>本快照最后一次成功原子替换的时间。When this snapshot was last atomically replaced.</summary>
    public DateTimeOffset UpdatedUtc { get; init; }

    /// <summary>
    ///     单调递增的提交序号：每次原子替换加一，并在审计文件中记录同值提交；读取端用它检测
    ///     快照与审计是否一致（PLAN §7.1）。
    ///     The monotonically increasing commit sequence: every atomic replacement
    ///     increments it and appends an audit record with the same value; readers
    ///     use it to detect snapshot/audit divergence (PLAN §7.1).
    /// </summary>
    public long CommitSequence { get; init; }

    public IReadOnlyList<PersistedTurn> Turns { get; init; } = [];
}

/// <summary>
///     一个用户回合的持久化检查视图：输入原文（脱敏后）与该回合全部执行尝试的终态摘要。
///     The persisted inspection view of one user turn: the (redacted) input and
///     the terminal summaries of every run attempted for it.
/// </summary>
public sealed record PersistedTurn
{
    public string Id { get; init; } = string.Empty;

    public string UserInput { get; init; } = string.Empty;

    public DateTimeOffset CreatedUtc { get; init; }

    public IReadOnlyList<PersistedRun> Runs { get; init; } = [];
}

/// <summary>
///     一次运行的终态摘要（脱敏后的检查视图）：状态、步数、工具数、usage 与最终回答/错误文本。
///     不含消息级协议内容，也不能用于重放执行。
///     The terminal summary of one run (a redacted inspection view): status,
///     steps, tools, usage and the final/error text. It carries no
///     message-level protocol content and cannot replay execution.
/// </summary>
public sealed record PersistedRun
{
    public string Id { get; init; } = string.Empty;

    public DateTimeOffset StartedUtc { get; init; }

    /// <summary>终态写入时间；尚未结束的运行为 null。When the terminal result was recorded; null while the run is unfinished.</summary>
    public DateTimeOffset? FinishedUtc { get; init; }

    /// <summary>
    ///     <see cref="Models.Agent.AgentStatus" /> 的名称；运行未结束时为 null。The <see cref="Models.Agent.AgentStatus" /> name;
    ///     null while unfinished.
    /// </summary>
    public string? Status { get; init; }

    public string? FinalMessage { get; init; }

    public string? Error { get; init; }

    public int Steps { get; init; }

    public int ToolExecutions { get; init; }

    public long? InputTokens { get; init; }

    public long? OutputTokens { get; init; }
}
