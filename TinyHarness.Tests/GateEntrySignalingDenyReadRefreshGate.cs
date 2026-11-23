using System.Collections;
using System.Text;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         带进入信号的门装饰器（测试专用），只交给交错测试的 B 方：进入
///         <see cref="TryAcquireAsync" /> 时先置位 <see cref="EnteredGateWait" />，再把参数与取消令牌
///         原样转发给内层门，返回值、异常与句柄原样透传，不改变内层门的任何时序与语义。A 方持门
///         直接用真实门实例、不经过本装饰器，信号因此只会由 B 置位；门等待观察与释放门后的收尾
///         等待也集中在本类型，供各交错测试共用。
///     </para>
///     <para>
///         An entry-signaling gate decorator (test-only), handed only to side B of an interleaving
///         test: entering <see cref="TryAcquireAsync" /> first signals <see cref="EnteredGateWait" />
///         and then forwards the arguments and cancellation token to the inner gate verbatim,
///         passing the result, exceptions, and handle through untouched, without altering any
///         timing or semantics of the inner gate. Side A holds the gate through the real gate
///         instance and never goes through this decorator, so only B can raise the signal; the
///         gate-wait observation and the post-release drain wait also live here, shared by the
///         interleaving tests.
///     </para>
/// </summary>
internal sealed class GateEntrySignalingDenyReadRefreshGate(ISandboxDenyReadRefreshGate inner)
    : ISandboxDenyReadRefreshGate
{
    /// <summary>
    ///     <para>B 进入门等待的上限：超过即判 B 既未进入门等待、也未结束。</para>
    ///     <para>The deadline for B to enter the gate wait: past it, B is judged as neither having entered the gate wait nor finished.</para>
    /// </summary>
    private static readonly TimeSpan EntryWaitTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     <para>进入信号到达后的短负等待：此窗口内 B 必须仍被门挡住而未完成。</para>
    ///     <para>The short negative wait after the entry signal: within it B must still be blocked on the gate and incomplete.</para>
    /// </summary>
    private static readonly TimeSpan NegativeWait = TimeSpan.FromMilliseconds(200);

    /// <summary>
    ///     <para>A 释放门后等待 B 结束的上限。</para>
    ///     <para>The deadline for B to finish once A has released the gate.</para>
    /// </summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    ///     <para>
    ///         进入门等待的确定性信号：装饰器的 <see cref="TryAcquireAsync" /> 被调用的那一刻完成；续体
    ///         异步调度，不在 B 的调用栈上内联执行。重复进入不抛，任务保持已完成。
    ///     </para>
    ///     <para>
    ///         The deterministic signal for entering the gate wait: completes the moment the decorator's
    ///         <see cref="TryAcquireAsync" /> is called; continuations are scheduled asynchronously and
    ///         never run inline on B's call stack. Repeated entries do not throw and the task stays
    ///         completed.
    ///     </para>
    /// </summary>
    public Task EnteredGateWait => _entered.Task;

    /// <summary>
    ///     <para>先置位进入信号，再把全部参数原样转发给内层门；内层同步抛出的异常同样原样传播。</para>
    ///     <para>Signals the entry first, then forwards every argument to the inner gate verbatim; an exception thrown synchronously by the inner gate propagates verbatim as well.</para>
    /// </summary>
    public Task<ISandboxDenyReadRefreshGateHandle?> TryAcquireAsync(
        string sandboxHome, TimeSpan timeout, CancellationToken cancellationToken)
    {
        _entered.TrySetResult();
        return inner.TryAcquireAsync(sandboxHome, timeout, cancellationToken);
    }

    /// <summary>
    ///     <para>
    ///         等 B 的进入信号并确认 B 被门挡住：三路等待取先完成者——信号先到则做短负等待，确认 B
    ///         仍未完成后执行 <paramref name="assertStillBlocked" />；B 抢先结束、或三路都未在期限内
    ///         完成，都判为失败。返回 null 表示通过，否则返回如实描述 B 真实终态（含完整异常链）的
    ///         失败说明。
    ///     </para>
    ///     <para>
    ///         Waits for B's entry signal and confirms B is blocked on the gate: a three-way wait takes
    ///         the first to finish — if the signal wins, a short negative wait confirms B is still
    ///         incomplete and then runs <paramref name="assertStillBlocked" />; B finishing first, or
    ///         none of the three finishing within the deadline, both fail. Returns null on success,
    ///         otherwise a failure text that faithfully describes B's real outcome including the full
    ///         exception chain.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>本方法不直接断言失败：调用方须先写回完整表、释放 A 持有的门并收尾 B，再用返回的说明断言。</para>
    ///     <para>This method does not assert the failure itself: the caller must first write back the complete table, release the gate A holds and drain B, then assert with the returned text.</para>
    /// </remarks>
    public async Task<string?> ObserveGateWaitAsync(Task taskB, Action? assertStillBlocked = null)
    {
        var finished = await Task.WhenAny(EnteredGateWait, taskB, Task.Delay(EntryWaitTimeout)).ConfigureAwait(false);
        if (ReferenceEquals(finished, taskB))
            return "B finished before entering the gate wait, so the entry under test never reached the gate: "
                   + DescribeOutcome(taskB);

        if (!ReferenceEquals(finished, EnteredGateWait))
            return $"B neither entered the gate wait nor finished within {EntryWaitTimeout.TotalSeconds}s; " +
                   $"EnteredGateWait was never signalled. {DescribeOutcome(taskB)}";

        await Task.Delay(NegativeWait).ConfigureAwait(false);
        if (taskB.IsCompleted)
            return $"B entered the gate wait yet completed within the {NegativeWait.TotalMilliseconds}ms negative " +
                   "wait, so it was not blocked on the gate held by A: " + DescribeOutcome(taskB);

        assertStillBlocked?.Invoke();
        return null;
    }

    /// <summary>
    ///     <para>
    ///         A 释放门后的有界收尾等待：B 在期限内正常完成则原样返回 <paramref name="failure" />。
    ///         已有原始失败（<paramref name="hasPrimaryFailure" />）时只观察 B 的真实终态并追加到失败
    ///         说明，等待异常绝不覆盖原始失败；没有原始失败时等待异常原样传播，与直接等待 B 一致。
    ///     </para>
    ///     <para>
    ///         The bounded drain wait after A releases the gate: when B completes normally within the
    ///         deadline the <paramref name="failure" /> text is returned unchanged. With a primary
    ///         failure already recorded (<paramref name="hasPrimaryFailure" />), it only observes B's
    ///         real outcome and appends it to the failure text — a wait exception never replaces the
    ///         primary failure; with no primary failure the wait exception propagates as is, matching
    ///         a direct await of B.
    ///     </para>
    /// </summary>
    public async Task<string?> DrainAfterReleaseAsync(Task taskB, string? failure, bool hasPrimaryFailure)
    {
        try
        {
            await taskB.WaitAsync(DrainTimeout).ConfigureAwait(false);
            return failure;
        }
        catch (Exception waitError) when (hasPrimaryFailure)
        {
            // 已有原始失败：只观察收尾，等待异常不得覆盖它。
            var outcome = taskB.IsCompleted
                ? DescribeOutcome(taskB)
                : $"Task status: {taskB.Status} (still not completed {DrainTimeout.TotalSeconds}s after the gate " +
                  $"was released); the drain wait ended with: {DescribeException(waitError)}";
            return failure + Environment.NewLine + "B after the gate was released: " + outcome;
        }
    }

    /// <summary>
    ///     <para>如实描述任务终态：未完成、正常返回、已取消，或失败时逐层列出异常类型、消息、Data 与完整文本。</para>
    ///     <para>Describes a task's real outcome: incomplete, returned normally, cancelled, or — when faulted — every exception level with its type, message, Data and full text.</para>
    /// </summary>
    private static string DescribeOutcome(Task task)
    {
        if (!task.IsCompleted) return $"Task status: {task.Status} (still running; not completed).";

        var text = new StringBuilder($"Task status: {task.Status}. ");
        switch (task.Status)
        {
            case TaskStatus.RanToCompletion:
                text.Append("B returned normally without throwing; it did not wait on the gate held by A.");
                break;
            case TaskStatus.Canceled:
                text.Append("B ended cancelled.");
                break;
            case TaskStatus.Faulted:
                text.Append("B threw; its full exception chain follows.");
                if (task.Exception is { } aggregate)
                {
                    foreach (var exception in aggregate.InnerExceptions)
                        AppendExceptionChain(text, exception);
                }

                break;
        }

        return text.ToString();
    }

    private static string DescribeException(Exception exception)
    {
        var text = new StringBuilder();
        AppendExceptionChain(text, exception);
        return text.ToString();
    }

    private static void AppendExceptionChain(StringBuilder text, Exception exception)
    {
        // 逐层列出类型、消息与 Data；末尾附根异常的完整 ToString，它已含内层链与各层堆栈。
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            text.AppendLine().Append("---- ").Append(current.GetType().FullName);
            if (!ReferenceEquals(current, exception)) text.Append(" (inner exception)");
            text.Append(" ----").AppendLine();
            text.Append("Message: ").Append(current.Message).AppendLine();
            if (current.Data.Count == 0) continue;

            text.Append("Data:");
            foreach (DictionaryEntry entry in current.Data)
                text.Append(" [").Append(entry.Key ?? "<null>").Append(" = ").Append(entry.Value ?? "<null>").Append(']');

            text.AppendLine();
        }

        text.AppendLine().Append(exception.ToString());
    }
}
