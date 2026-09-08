namespace TinyHarness.Core.Agent;

/// <summary>
/// 一次完整 Agent 运行的最终结果。
///
/// The outcome of an entire Agent run.
/// </summary>
public sealed record AgentResult
{
    public required AgentStatus Status { get; init; }

    /// <summary>
    /// 运行成功产出时的最终 assistant 文本。
    /// The final assistant text, when the run produced one.
    /// </summary>
    public string FinalMessage { get; init; } = string.Empty;

    /// <summary>
    /// 已经执行的模型请求步数。
    /// Total number of model request steps executed.
    /// </summary>
    public int Steps { get; init; }

    /// <summary>
    /// 已经实际执行的工具调用次数。
    /// Total number of tool invocations actually executed.
    /// </summary>
    public int ToolExecutions { get; init; }

    /// <summary>
    /// 运行失败或取消时的错误说明。
    /// Error detail set when the run failed or was cancelled.
    /// </summary>
    public string? Error { get; init; }
}
