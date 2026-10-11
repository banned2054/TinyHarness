using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.Context;

namespace TinyHarness.Core.Models.Session;

/// <summary>
///     宿主事件的种类（PLAN §6 的最小起步集）。M12.1 只发布运行生命周期与压缩事件；
///     文本增量、工具生命周期、审批、usage/预算等事件在 M12.2 按同一契约扩展，
///     新成员使用新的显式值。
///     Host event kinds (the minimal starting set of PLAN §6). M12.1 publishes run
///     lifecycle and compaction events only; text deltas, tool lifecycle, approvals and
///     usage/budget events extend the same contract in M12.2 with new explicit values.
/// </summary>
public enum HostEventKind
{
    /// <summary>一次运行开始处理一个回合；无负载。A run started processing a turn; no payload.</summary>
    RunStarted = 1,

    /// <summary>一次成功的上下文压缩；负载为 <see cref="ContextChange" />。One successful context compaction; payload is a <see cref="ContextChange" />.</summary>
    CompactionApplied = 2,

    /// <summary>
    ///     一次运行到达终态；负载为 <see cref="AgentResult" />，包含最终状态、步数与错误
    ///     说明。终态事件不因取消而丢弃。
    ///     A run reached its terminal state; payload is the <see cref="AgentResult" /> with
    ///     the final status, step counts and error detail. The terminal event is never
    ///     dropped because of cancellation.
    /// </summary>
    RunFinished = 3
}
