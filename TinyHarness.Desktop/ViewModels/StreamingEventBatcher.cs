using System.Text;
using TinyHarness.Desktop.Runtime;

namespace TinyHarness.Desktop.ViewModels;

/// <summary>
///     <para>合并单会话的流式增量；每批有界且至多一个 UI 发布在途，非增量事件等待前文上屏。</para>
///     <para>
///         Batches one session's deltas with bounded batches and at most one UI publication in flight; other events
///         await prior text.
///     </para>
/// </summary>
internal sealed class StreamingEventBatcher(
    Func<IReadOnlyList<DesktopEvent>, ValueTask> publish,
    Func<Task>?                                  waitForFrame = null)
{
    internal const int MaximumBatchCharacters = 16 * 1024;

    private readonly object                                         _gate    = new();
    private readonly List<(DesktopEvent Event, StringBuilder Text)> _pending = [];
    private          int                                            _characters;
    private          Task?                                          _pump;

    /// <summary>
    ///     <para>接受有序事件；缓冲满时施加背压，边界事件不越过此前的正文或思考。</para>
    ///     <para>
    ///         Accepts ordered events, applies backpressure at capacity, and keeps boundary events behind prior text and
    ///         reasoning.
    ///     </para>
    /// </summary>
    internal async ValueTask PublishAsync(DesktopEvent update)
    {
        if (update.Kind is not ("text" or "reasoning") || string.IsNullOrEmpty(update.Text))
        {
            await FlushAsync().ConfigureAwait(false);
            await publish([update]).ConfigureAwait(false);
            return;
        }

        var offset = 0;
        while (offset < update.Text.Length)
        {
            Task pump;
            lock (_gate)
            {
                // 保留上一批异常，交回模型事件边界处理；不静默重启失败的发布。
                if (_pump is { IsCompleted: true }) _pump.GetAwaiter().GetResult();
                var count = _pending.Count >= 128
                    ? 0
                    : Math.Min(MaximumBatchCharacters - _characters, update.Text.Length - offset);
                if (count > 0)
                {
                    if (_pending.Count           == 0 || _pending[^1].Event.Kind != update.Kind ||
                        _pending[^1].Event.RunId != update.RunId)
                        _pending.Add((update with { Text = null }, new StringBuilder()));
                    _pending[^1].Text.Append(update.Text, offset, count);
                    _characters += count;
                    offset      += count;
                }

                _pump ??= Task.Run(PumpAsync);
                pump  =   _pump;
                if (offset == update.Text.Length && _characters < MaximumBatchCharacters && _pending.Count < 128)
                    return;
            }

            await pump.ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     <para>等待已接受增量全部发布；完成、失败或取消的终态不得遗留定时更新。</para>
    ///     <para>Awaits all accepted deltas so completion, failure, or cancellation leaves no pending timed update.</para>
    /// </summary>
    internal async Task FlushAsync()
    {
        Task? pump;
        lock (_gate)
        {
            pump = _pump;
        }

        if (pump is not null) await pump.ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>终态已观察发布结果后清除失败批次；仅允许在发布任务结束后恢复下一轮。</para>
    ///     <para>
    ///         Clears failed batches after the terminal observer consumes their result, allowing recovery only after
    ///         publication ends.
    ///     </para>
    /// </summary>
    internal void ResetAfterCompletion()
    {
        lock (_gate)
        {
            if (_pump is { IsCompleted: false })
                throw new InvalidOperationException("Cannot reset an active streaming publication.");
            _pump = null;
            _pending.Clear();
            _characters = 0;
        }
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            await (waitForFrame?.Invoke() ?? Task.Delay(TimeSpan.FromMilliseconds(50))).ConfigureAwait(false);
            (DesktopEvent Event, StringBuilder Text)[] batch;
            lock (_gate)
            {
                batch = _pending.ToArray();
                _pending.Clear();
                _characters = 0;
            }

            await publish(batch.Select(item => item.Event with { Text = item.Text.ToString() }).ToArray())
               .ConfigureAwait(false);

            lock (_gate)
            {
                if (_pending.Count != 0) continue;
                _pump = null;
                return;
            }
        }
    }
}
