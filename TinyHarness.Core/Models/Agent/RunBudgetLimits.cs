namespace TinyHarness.Core.Models.Agent;

/// <summary>
///     <para>由可信宿主提供的单次运行累计上限；压缩请求也计入请求数与 token。</para>
///     <para>Trusted host limits for one run; compaction requests also consume requests and tokens.</para>
/// </summary>
public sealed record RunBudgetLimits
{
    public int  MaxModelRequests    { get; init; } = 128;
    public int  MaxToolExecutions   { get; init; } = 256;
    public int  MaxWallClockSeconds { get; init; } = 900;
    public long MaxTotalTokens      { get; init; } = 2_000_000;

    /// <summary>
    ///     <para>拒绝非正数和无法表达为计时器间隔的墙钟上限。</para>
    ///     <para>Rejects nonpositive limits and wall-clock limits outside the timer range.</para>
    /// </summary>
    public void Validate()
    {
        if (MaxModelRequests <= 0 ||
            MaxToolExecutions <= 0 ||
            MaxWallClockSeconds is <= 0 or > 4_294_966 ||
            MaxTotalTokens <= 0)
            throw new ArgumentOutOfRangeException(nameof(RunBudgetLimits),
                                                  "Run budget limits must be positive and within the timer range.");
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
        if (ModelRequests < 0 ||
            ToolExecutions < 0 ||
            AccountedTokens < 0 ||
            ElapsedMilliseconds < 0 ||
            ToolExecutions != toolExecutions ||
            LimitReason is not (null or "wall-clock" or "tokens" or "model-requests" or "tool-executions"))
            throw new InvalidDataException("The run budget snapshot contains invalid or inconsistent facts.");
    }
}
