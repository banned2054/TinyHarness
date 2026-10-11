using TinyHarness.Core.Models.Agent;

namespace TinyHarness.Core.Models.Session;

/// <summary>
///     处理某一回合的一次执行尝试（PLAN §5）：独立 ID、启动时间与终态结果。运行恰好记录
///     一个终态；取消之后继续产生新运行，绝不重启或改写已结束的运行。
///     One execution attempt processing a turn (PLAN §5): its own id, start time and
///     terminal result. A run records exactly one terminal outcome; after a cancellation
///     new runs continue — a finished run is never restarted or rewritten.
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

    /// <summary>终态写入时间；尚未结束时为 <see langword="null" />。When the terminal result was recorded; null until finished.</summary>
    public DateTimeOffset? FinishedUtc { get; private set; }

    /// <summary>
    ///     由执行栈产生的终态结果；尚未结束时为 <see langword="null" />。The terminal result produced by the execution stack; null until
    ///     finished.
    /// </summary>
    public AgentResult? Result { get; private set; }

    /// <summary>运行是否已到达终态。Whether the run has reached a terminal result.</summary>
    public bool IsFinished => Result is not null;

    /// <summary>
    ///     记录唯一一次终态；重复结束是生命周期违规。
    ///     Records the single terminal result; finishing twice is a lifecycle violation.
    /// </summary>
    internal void Finish(AgentResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (Result is not null)
            throw new
                InvalidOperationException($"Run '{Id}' already finished; a run records exactly one terminal result.");

        Result      = result;
        FinishedUtc = DateTimeOffset.UtcNow;
    }
}
