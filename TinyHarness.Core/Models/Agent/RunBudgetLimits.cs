namespace TinyHarness.Core.Models.Agent;

/// <summary>
///     <para>由可信宿主提供的单次运行累计上限；压缩请求也计入请求数与 token。</para>
///     <para>Trusted host limits for one run; compaction requests also consume requests and tokens.</para>
/// </summary>
public sealed record RunBudgetLimits
{
    public int MaxModelRequests  { get; init; } = 128;
    public int MaxToolExecutions { get; init; } = 256;

    /// <summary>
    ///     <para>单次运行总时长上限；默认 0 表示只记录耗时，正数按秒启用截止时间。</para>
    ///     <para>Run duration limit; the default zero records elapsed time only, while positive seconds enable a deadline.</para>
    /// </summary>
    public int MaxWallClockSeconds { get; init; }

    /// <summary>
    ///     <para>累计 token 上限；默认 0 表示只记录用量，不因 token 累计停止运行。</para>
    ///     <para>Cumulative token limit; the default zero records usage without stopping the run for tokens.</para>
    /// </summary>
    public long MaxTotalTokens { get; init; }

    /// <summary>
    ///     <para>token 与时长上限允许为零；拒绝负值、非正请求／工具上限和超出计时器范围的时长。</para>
    ///     <para>
    ///         Allows zero token and duration limits; rejects negative values, nonpositive request/tool limits and invalid
    ///         timer ranges.
    ///     </para>
    /// </summary>
    public void Validate()
    {
        if (MaxModelRequests  <= 0                    ||
            MaxToolExecutions <= 0                    ||
            MaxWallClockSeconds is < 0 or > 4_294_966 ||
            MaxTotalTokens < 0)
            throw new ArgumentOutOfRangeException(nameof(RunBudgetLimits),
                                                  "Request/tool limits must be positive; token and duration limits may be zero, with duration within the timer range.");
    }
}

/// <summary>
///     <para>运行累计事实；AccountedTokens 含缺失 usage 的估算，不是供应商精确扣费。</para>
///     <para>Cumulative run facts; AccountedTokens includes estimates for missing usage, not exact provider charges.</para>
/// </summary>
public sealed record RunBudgetSnapshot
{
    public int     ModelRequests       { get; init; }
    public int     ToolExecutions      { get; init; }
    public long    AccountedTokens     { get; init; }
    public bool    HasEstimatedTokens  { get; init; }
    public long    ElapsedMilliseconds { get; init; }
    public string? LimitReason         { get; init; }

    /// <summary>
    ///     <para>拒绝负数、未知原因和计数矛盾，旧日志缺失时由调用方保留 null。</para>
    ///     <para>Rejects negative counts, unknown reasons and inconsistent counters; callers retain null for old logs.</para>
    /// </summary>
    internal void Validate(int toolExecutions)
    {
        if (ModelRequests       < 0               ||
            ToolExecutions      < 0               ||
            AccountedTokens     < 0               ||
            ElapsedMilliseconds < 0               ||
            ToolExecutions      != toolExecutions ||
            LimitReason is not (null or "wall-clock" or "tokens" or "model-requests" or "tool-executions"))
            throw new InvalidDataException("The run budget snapshot contains invalid or inconsistent facts.");
    }
}
