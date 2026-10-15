using TinyHarness.Core.Models.Agent;

namespace TinyHarness.Core.Models.Session;

/// <summary>
///     <para>处理一个回合的独立执行尝试，记录启动时间与唯一终态；已结束的运行不重启或改写。</para>
///     <para>
///         An independent attempt to process a turn, recording its start time and a single terminal outcome;
///         a finished run is never restarted or rewritten.
///     </para>
/// </summary>
public sealed class SessionRun
{
    public SessionRun(string id, string sessionId, string turnId, DateTimeOffset? startedUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(turnId);
        Id         = id;
        SessionId  = sessionId;
        TurnId     = turnId;
        StartedUtc = startedUtc ?? DateTimeOffset.UtcNow;
    }

    public string Id { get; }

    public string SessionId { get; }

    public string TurnId { get; }

    public DateTimeOffset StartedUtc { get; }

    /// <summary>
    ///     <para>终态写入时间；尚未结束时为 <see langword="null" />。</para>
    ///     <para>When the terminal result was recorded; <see langword="null" /> until finished.</para>
    /// </summary>
    public DateTimeOffset? FinishedUtc { get; private set; }

    /// <summary>
    ///     <para>执行栈产生的终态结果；尚未结束时为 <see langword="null" />。</para>
    ///     <para>The terminal result produced by the execution stack; <see langword="null" /> until finished.</para>
    /// </summary>
    public AgentResult? Result { get; private set; }

    internal IReadOnlyList<ToolCallRecord>? ExecutionRecords { get; private set; }

    /// <summary>
    ///     <para>运行是否已到达终态。</para>
    ///     <para>Whether the run has reached a terminal result.</para>
    /// </summary>
    public bool IsFinished => Result is not null;

    internal void RecordExecutionIntent(IReadOnlyList<ToolCallRecord> records)
    {
        if (IsFinished)
            throw new InvalidOperationException($"Run '{Id}' already finished; execution intent cannot change.");

        ExecutionRecords = records;
    }

    /// <summary>
    ///     <para>记录唯一终态；重复结束违反运行生命周期。</para>
    ///     <para>Records the single terminal result; finishing twice violates the run lifecycle.</para>
    /// </summary>
    /// <param name="finishedUtc">
    ///     <para>恢复时保留的原始终态时间；省略时使用当前时间。</para>
    ///     <para>The original terminal time retained during restoration; defaults to the current time.</para>
    /// </param>
    internal void Finish(AgentResult result, DateTimeOffset? finishedUtc = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (Result is not null)
            throw new
                InvalidOperationException($"Run '{Id}' already finished; a run records exactly one terminal result.");

        Result      = result;
        FinishedUtc = finishedUtc ?? DateTimeOffset.UtcNow;
    }
}
