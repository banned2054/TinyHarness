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
    ///     发布一条事件。事件按发出顺序送达；M12.1 不定义消费者缓慢或断开时的缓冲策略
    ///     （M12.2 处理）。接收方失败按非致命的展示故障处理：运行继续，首个故障说明随
    ///     终态结果上报，终态事件的交付本身从不被丢弃。
    ///     Publishes one event, delivered in emission order. M12.1 defines no buffering for
    ///     slow or disconnected consumers (handled in M12.2). A sink failure is treated as
    ///     a non-fatal display fault: the run continues, the first fault note is surfaced
    ///     on the terminal result, and the terminal delivery itself is never dropped.
    /// </summary>
    ValueTask PublishAsync(HostEvent @event, CancellationToken cancellationToken);
}
