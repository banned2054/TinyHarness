using TinyHarness.Core.Models.Session;

namespace TinyHarness.Core.Services.Session;

/// <summary>
///     窄的宿主事件契约（PLAN §4.1/§6）：执行栈把统一信封发布给宿主，宿主负责展示、缓冲
///     与背压。订阅者不得经由事件改变执行状态——事件通道只有发布语义，审批与控制走各自的
///     入口（<see cref="TinyHarness.Core.Services.Permissions.IApprovalProvider" /> 与取消令牌）。
///     The narrow host event contract (PLAN §4.1/§6): the execution stack publishes a
///     unified envelope to the host, which owns display, buffering and backpressure.
///     Subscribers must not mutate execution state through events — the channel is
///     publish-only; approvals and control use their own entries
///     (<see cref="TinyHarness.Core.Services.Permissions.IApprovalProvider" /> and the
///     cancellation token).
/// </summary>
public interface IHostEventSink
{
    /// <summary>
    ///     <para>显式选择接收普通请求的可读推理增量；默认关闭，摘要和 opaque 数据始终不发布。</para>
    ///     <para>
    ///         Opts into readable reasoning deltas for regular requests; defaults off and excludes compaction and opaque
    ///         data.
    ///     </para>
    /// </summary>
    bool IncludeReasoningText => false;

    /// <summary>
    ///     发布一条事件。事件按发出顺序送达。M12.2 落定的交付策略：发布方内联等待接收方
    ///     返回——不设中间队列，内存占用有界，缓慢的消费者直接拖慢发布方（模型流在事件边界
    ///     停止），不会无限缓存或丢失事件；需要解耦的宿主自行持有有界缓冲与合并策略。
    ///     接收方失败按非致命的展示故障处理：运行继续，首个故障说明随终态结果上报，终态
    ///     事件（与失败终态前的错误事件）不带取消令牌发布，其交付本身从不被丢弃。
    ///     Publishes one event, delivered in emission order. The M12.2 delivery strategy:
    ///     the publisher awaits this call inline — no intermediate queue, bounded memory,
    ///     and a slow consumer throttles the publisher directly (the model stream stalls
    ///     at the event boundary), so events are never buffered without bound or dropped;
    ///     hosts that need decoupling own their own bounded buffers and merge policies.
    ///     A sink failure is treated as a non-fatal display fault: the run continues, the
    ///     first fault note is surfaced on the terminal result, and the terminal event
    ///     (with the error event preceding a failed terminal) is published without a
    ///     cancellation token, so its delivery is never dropped.
    /// </summary>
    ValueTask PublishAsync(HostEvent @event, CancellationToken cancellationToken);
}
