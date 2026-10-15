namespace TinyHarness.Core.Models.Agent;

/// <summary>
///     <para>一次 Agent 运行的终态结果。</para>
///     <para>The terminal outcome of one Agent run.</para>
/// </summary>
public sealed record AgentResult
{
    public required AgentStatus Status { get; init; }

    /// <summary>
    ///     <para>运行产出的最终 assistant 文本。</para>
    ///     <para>The final assistant text, when the run produced one.</para>
    /// </summary>
    public string FinalMessage { get; init; } = string.Empty;

    /// <summary>
    ///     <para>已执行的模型请求步数。</para>
    ///     <para>The number of model request steps executed.</para>
    /// </summary>
    public int Steps { get; init; }

    /// <summary>
    ///     <para>实际执行的工具调用次数。</para>
    ///     <para>The number of tool invocations actually executed.</para>
    /// </summary>
    public int ToolExecutions { get; init; }

    /// <summary>
    ///     <para>本次运行成功应用的上下文压缩次数。</para>
    ///     <para>The number of context compactions successfully applied during the run.</para>
    /// </summary>
    public int Compactions { get; init; }

    /// <summary>
    ///     <para>服务端报告的输入 token 总和，含压缩摘要请求。</para>
    ///     <para>The sum of server-reported input tokens, including compaction summary requests.</para>
    /// </summary>
    /// <remarks>
    ///     <para>不计入缺少可靠 usage 的请求；全程无可靠 usage 时为 null，表示未报告而非零。</para>
    ///     <para>
    ///         Requests without reliable usage are excluded; null means no reliable usage was reported,
    ///         not zero.
    ///     </para>
    /// </remarks>
    public long? InputTokens { get; init; }

    /// <summary>
    ///     <para>服务端报告的输出 token 总和；缺失语义与 <see cref="InputTokens" /> 相同。</para>
    ///     <para>
    ///         The sum of server-reported output tokens; absence has the same meaning as
    ///         <see cref="InputTokens" />.
    ///     </para>
    /// </summary>
    public long? OutputTokens { get; init; }

    /// <summary>
    ///     <para>运行失败或取消时的错误说明。</para>
    ///     <para>Error details when the run failed or was cancelled.</para>
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    ///     <para>本次运行请求的工具调用的执行意图与确认记录，不可用于重放执行或恢复授权。</para>
    ///     <para>
    ///         Execution intent and confirmation records for requested tool calls; never used to replay
    ///         execution or restore authorization.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         执行窗口内取消或失败时保留未知结果；宿主据此判定 NeedsInspection，
    ///         并把历史回退到最近完整协议边界。
    ///     </para>
    ///     <para>
    ///         Cancellation or failure within the execution window preserves an unknown result;
    ///         the host uses it to derive NeedsInspection and rewind history to the last complete protocol boundary.
    ///     </para>
    /// </remarks>
    public IReadOnlyList<ToolCallRecord>? ToolCalls { get; init; }
}
