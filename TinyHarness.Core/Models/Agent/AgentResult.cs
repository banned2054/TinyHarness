namespace TinyHarness.Core.Models.Agent;

/// <summary>
///     一次完整 Agent 运行的最终结果。
///     The outcome of an entire Agent run.
/// </summary>
public sealed record AgentResult
{
    public required AgentStatus Status { get; init; }

    /// <summary>
    ///     运行成功产出时的最终 assistant 文本。
    ///     The final assistant text, when the run produced one.
    /// </summary>
    public string FinalMessage { get; init; } = string.Empty;

    /// <summary>
    ///     已经执行的模型请求步数。
    ///     Total number of model request steps executed.
    /// </summary>
    public int Steps { get; init; }

    /// <summary>
    ///     已经实际执行的工具调用次数。
    ///     Total number of tool invocations actually executed.
    /// </summary>
    public int ToolExecutions { get; init; }

    /// <summary>
    ///     本次运行成功执行的上下文压缩次数。
    ///     Total number of context compactions successfully applied during the run.
    /// </summary>
    public int Compactions { get; init; }

    /// <summary>
    ///     服务端报告的输入 token 总和（含压缩摘要请求）；任一请求未报告可靠 usage 时不计入，
    ///     全程无可靠 usage 时保持 null——缺失表示“未报告”，不是零。
    ///     Sum of server-reported input tokens (compaction summary requests included);
    ///     requests without reliable usage contribute nothing and an all-null run keeps
    ///     null — absence means "not reported", not zero.
    /// </summary>
    public long? InputTokens { get; init; }

    /// <summary>
    ///     服务端报告的输出 token 总和；缺失语义与 <see cref="InputTokens" /> 相同。
    ///     Sum of server-reported output tokens; absence means the same as
    ///     <see cref="InputTokens" />.
    /// </summary>
    public long? OutputTokens { get; init; }

    /// <summary>
    ///     运行失败或取消时的错误说明。
    ///     Error detail set when the run failed or was cancelled.
    /// </summary>
    public string? Error { get; init; }
}
