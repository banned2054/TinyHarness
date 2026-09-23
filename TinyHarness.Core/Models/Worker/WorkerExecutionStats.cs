namespace TinyHarness.Core.Models.Worker;

/// <summary>
///     一次 worker run 的有界执行统计，只陈述实际消耗量，用于调用方判断结果的可信度与成本。
///     Bounded execution statistics for one worker run; it reports what the run actually consumed so the
///     caller can judge confidence and cost.
/// </summary>
public sealed record WorkerExecutionStats
{
    /// <summary>已发生的模型请求数。 Model requests that were actually issued.</summary>
    public int ModelRequests { get; init; }

    /// <summary>已实际执行的工具调用次数。 Tool invocations that were actually executed.</summary>
    public int ToolCalls { get; init; }

    /// <summary>工具输出进入上下文的总字符数。 Total characters of tool output admitted into the context.</summary>
    public int ToolOutputCharacters { get; init; }

    /// <summary>run 的总耗时。 Total wall-clock duration of the run.</summary>
    public TimeSpan Elapsed { get; init; }
}
