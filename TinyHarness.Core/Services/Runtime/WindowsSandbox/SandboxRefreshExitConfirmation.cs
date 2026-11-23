namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     <para>
///         helper 终止或退出确认等待失败导致退出状态未知时抛出的超时形态：保持 refresh 的
///         TimeoutException 语义（backend 通用失败路径照常包装），同时携带后台退出确认任务——
///         任务在 helper 进程退出后完成 true（确认等待再失败则完成 false），调用方据此后台释放
///         拒读刷新门；确认完成前门保持持有，helper 可能仍在写拒读簿记。
///     </para>
///     <para>
///         The timeout form thrown when the helper's exit state is unknown because termination or
///         the exit-confirmation wait failed: the refresh keeps its TimeoutException semantics (the
///         backend's generic failure path wraps it as before) while carrying the background exit-
///         confirmation task, which completes true once the helper process exits (false when the
///         confirmation wait fails again); callers use it to release the deny-read refresh gate in
///         the background, and the gate stays held until the confirmation because the helper may
///         still be writing deny-read bookkeeping.
///     </para>
/// </summary>
public sealed class SandboxRefreshExitUncertainException(string            message,
                                                         Task<bool>        exitConfirmation,
                                                         Exception?        terminationFailure)
    : TimeoutException(message, terminationFailure)
{
    /// <summary>
    ///     <para>
    ///         后台退出确认任务：helper 进程退出后完成 true；确认等待再失败完成 false；任务本身
    ///         不失败、不取消。异常上抛后所有权即随异常转移，调用方不再承担确认等待。
    ///     </para>
    ///     <para>
    ///         The background exit-confirmation task: completes true after the helper process exits
    ///         and false when the confirmation wait fails again; the task itself never faults or
    ///         cancels. Ownership transfers with the exception, so callers never wait for the
    ///         confirmation themselves.
    ///     </para>
    /// </summary>
    public Task<bool> ExitConfirmation { get; } = exitConfirmation;
}

/// <summary>
///     <para>
///         helper 终止或退出确认等待失败导致退出状态未知时抛出的取消形态：保持 refresh 的取消
///         语义（派生自 <see cref="OperationCanceledException" /> 并保留原始 token，上层取消处理
///         照常命中），同时携带后台退出确认任务——任务在 helper 进程退出后完成 true（确认等待再
///         失败则完成 false），调用方据此后台释放拒读刷新门；确认完成前门保持持有。
///     </para>
///     <para>
///         The cancellation form thrown when the helper's exit state is unknown because termination
///         or the exit-confirmation wait failed: the refresh keeps its cancellation semantics
///         (derived from <see cref="OperationCanceledException" /> with the original token
///         preserved, so upper-layer cancellation handling matches as before) while carrying the
///         background exit-confirmation task, which completes true once the helper process exits
///         (false when the confirmation wait fails again); callers use it to release the deny-read
///         refresh gate in the background, and the gate stays held until the confirmation.
///     </para>
/// </summary>
public sealed class SandboxRefreshExitUncertainCancellationException(string          message,
                                                                     CancellationToken cancellationToken,
                                                                     Task<bool>        exitConfirmation)
    : OperationCanceledException(message, cancellationToken)
{
    /// <summary>
    ///     <para>
    ///         后台退出确认任务：helper 进程退出后完成 true；确认等待再失败完成 false；任务本身
    ///         不失败、不取消。异常上抛后所有权即随异常转移，调用方不再承担确认等待。
    ///     </para>
    ///     <para>
    ///         The background exit-confirmation task: completes true after the helper process exits
    ///         and false when the confirmation wait fails again; the task itself never faults or
    ///         cancels. Ownership transfers with the exception, so callers never wait for the
    ///         confirmation themselves.
    ///     </para>
    /// </summary>
    public Task<bool> ExitConfirmation { get; } = exitConfirmation;
}

/// <summary>
///     <para>
///         退出不确定异常的识别入口：调用方（backend 与授权协调器）在拒读刷新门内以异常过滤器
///         命中两种形态并把确认任务移交给门，不再自行等待确认。
///     </para>
///     <para>
///         The recognition entry for exit-uncertainty exceptions: callers (the backend and the
///         authorization coordinator) match both forms through exception filters inside the
///         deny-read refresh gate and hand the confirmation task over to the gate instead of
///         waiting for the confirmation themselves.
///     </para>
/// </summary>
internal static class SandboxRefreshExitUncertainty
{
    /// <summary>
    ///     <para>
    ///         命中退出不确定异常（超时或取消形态）时返回 true 并给出后台退出确认任务；其他异常
    ///         返回 false 且确认任务为 null，过滤器照常放行原有异常路径。
    ///     </para>
    ///     <para>
    ///         Returns true with the background exit-confirmation task when the exception is an
    ///         exit-uncertainty form (timeout or cancellation); returns false with a null task for
    ///         any other exception, leaving the existing exception paths untouched.
    ///     </para>
    /// </summary>
    public static bool TryGetConfirmation(Exception exception, out Task<bool>? confirmation)
    {
        if (exception is SandboxRefreshExitUncertainCancellationException cancellationForm)
        {
            confirmation = cancellationForm.ExitConfirmation;
            return true;
        }

        if (exception is SandboxRefreshExitUncertainException timeoutForm)
        {
            confirmation = timeoutForm.ExitConfirmation;
            return true;
        }

        confirmation = null;
        return false;
    }
}

/// <summary>
///     <para>
///         helper 终止步骤的结果：退出是否已在调用方等待前确认，以及终止或确认等待的失败
///         （已确认时为 null）。未确认时该失败随不确定异常作 inner 上抛。
///     </para>
///     <para>
///         The helper termination step's result: whether the exit was confirmed before the caller's
///         wait ended, plus the termination or confirmation-wait failure (null once confirmed). The
///         failure surfaces as the inner exception of the uncertainty exception when unconfirmed.
///     </para>
/// </summary>
internal readonly record struct SandboxHelperTermination(bool ExitConfirmed, Exception? TerminationFailure);
