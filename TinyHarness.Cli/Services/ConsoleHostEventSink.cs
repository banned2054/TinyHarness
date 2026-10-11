using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.Session;

namespace TinyHarness.Cli.Services;

/// <summary>
///     run 命令的宿主事件展示：目前只把压缩事件写出到控制台，文案与旧的
///     <c>ContextCompacted</c> 订阅完全一致；run 的其余输出来自最终结果摘要。
///     交互会话的完整事件展示（文本增量、工具生命周期、审批等）按 M12.2 扩展。
///     The run command's host-event display: for now only compaction events are written
///     to the console, with text identical to the old <c>ContextCompacted</c>
///     subscription; the other run output comes from the final result summary. The full
///     interactive-session event display (text deltas, tool lifecycle, approvals)
///     expands per M12.2.
/// </summary>
internal sealed class ConsoleHostEventSink : IHostEventSink
{
    public async ValueTask PublishAsync(HostEvent @event, CancellationToken cancellationToken)
    {
        if (@event.Kind == HostEventKind.CompactionApplied && @event.Payload is ContextChange change)
            await Console.Out.WriteLineAsync($"Context: {change.BeforeTokens:N0} -> " +
                                             $"{change.AfterTokens:N0} tokens after compaction");
    }
}
