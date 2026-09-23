namespace TinyHarness.Core.Models.Worker;

/// <summary>
///     一次性只读 worker 的可信执行选项：只能由组合根在构造 runner 时提供，
///     MCP 请求不能提供、选择或修改其中任何一项（模型、预算、超时都是宿主的信任决策）。
///     所有预算都是正值上限，"恰好等于上限"允许，"导致下一项动作越界"在该动作之前拦截。
///     Trusted execution options for the one-shot read-only worker. Only the composition root supplies
///     them when constructing the runner; an MCP request can never provide, select or override any of
///     them — model, budgets and timeouts are host trust decisions. Every budget is a positive ceiling:
///     exactly reaching it is allowed, and the action that would exceed it is intercepted before it runs.
/// </summary>
public sealed record WorkerExecutionOptions
{
    /// <summary>发起模型请求使用的模型 ID。 Model id used for every model request of the run.</summary>
    public required string Model { get; init; }

    /// <summary>单次 run 的总超时；超时映射为未完成，不伪造结论。 Total run timeout; a timeout maps to an incomplete result.</summary>
    public required TimeSpan RunTimeout { get; init; }

    /// <summary>
    ///     模型请求步数上限：每次发给模型的请求占一步（与 Agent Loop 的步数语义一致）。
    ///     Maximum number of model request steps; every request sent to the model consumes one step.
    /// </summary>
    public required int MaxAgentSteps { get; init; }

    /// <summary>单次工具执行的默认超时秒数。 Default per-tool-execution timeout in seconds.</summary>
    public required int? DefaultToolTimeoutSeconds { get; init; }

    /// <summary>
    ///     任务包总字符数上限（TASK、KNOWN FACTS、FOCUS HINTS、EXPECTED OUTPUT 的渲染总量）。
    ///     只能收紧既有字段级限制之外的总量；超限的请求不发起任何模型请求。
    ///     Total character cap for the rendered task package (TASK, KNOWN FACTS, FOCUS HINTS and
    ///     EXPECTED OUTPUT combined). It only tightens the per-field limits; an over-cap package never
    ///     triggers a model request.
    /// </summary>
    public required int MaxTaskPackageCharacters { get; init; }

    /// <summary>
    ///     单次 run 实际执行的工具调用次数上限；超限的工具调用不会触达底层工具。
    ///     Maximum number of tool invocations actually executed per run; an over-limit call never
    ///     reaches the underlying tool.
    /// </summary>
    public required int MaxToolCalls { get; init; }

    /// <summary>
    ///     累计返回给模型的工具输出字符数上限；恰好用尽前按剩余空间截断，用尽后不再执行工具。
    ///     Cumulative character cap for tool output returned to the model; output is cut to the
    ///     remaining space and tools stop executing once the budget is used up.
    /// </summary>
    public required int MaxToolOutputCharacters { get; init; }

    /// <summary>
    ///     累计上下文 token 估值上限（每次请求的 messages 与 tools 都计入，重复历史重复计费，
    ///     使用保守 TokenEstimator）；超限的请求在真实 client 之前被拦截。
    ///     Cumulative cap on estimated context tokens (every request's messages and tools count, repeated
    ///     history counts every time, conservative TokenEstimator); an over-cap request is intercepted
    ///     before the real client.
    /// </summary>
    public required int MaxCumulativeContextTokens { get; init; }

    /// <summary>
    ///     单次模型请求的上下文 token 估值上限，必须由宿主按所选模型显式声明的 context window 留出输出空间后提供。
    ///     Per-request context token estimate cap, supplied by the host after reserving output space from
    ///     the explicitly configured context window of the selected model.
    /// </summary>
    public required int MaxContextTokensPerRequest { get; init; }

    /// <summary>
    ///     单次模型响应流的字符数上限（增量计数文本 delta 与工具 ID/名/参数片段）；
    ///     到达边界即停止，不完整工具调用不会被执行。
    ///     Per-response stream character cap (incrementally counting text deltas and tool id/name/argument
    ///     fragments); the stream stops at the boundary and incomplete tool calls are never executed.
    /// </summary>
    public required int MaxModelResponseCharacters { get; init; }
}
