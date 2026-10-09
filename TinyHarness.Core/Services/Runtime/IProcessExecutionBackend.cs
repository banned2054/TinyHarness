using TinyHarness.Core.Models.Runtime;

namespace TinyHarness.Core.Services.Runtime;

/// <summary>
/// 进程执行后端契约：把 Prepare 冻结的进程计划在其所属环境（宿主或 Windows 沙箱）中启动、
/// 回收，并把 stdout/stderr 以块推入调用方提供的捕获器。
///
/// Process-execution backend contract: starts and reclaims the frozen process
/// plan in its own environment (host machine or the Windows sandbox) and pushes
/// stdout/stderr chunks into caller-provided captures.
/// </summary>
public interface IProcessExecutionBackend
{
    /// <summary>
    /// 执行一个已冻结的进程计划。正常返回的前提是进程已退出且双流已完整排入对应捕获器
    /// （含流结束后追加的空块，它触发跨块脱敏的尾部冲刷）。命令超时由后端按
    /// <see cref="PreparedProcessExecution.TimeoutSeconds"/> 实施：终止进程树、排空输出，
    /// 返回 <see cref="ProcessExecutionResult.TimedOut"/> 为 true 的结果并保留已捕获输出。
    /// 用户取消抛 <see cref="OperationCanceledException"/>。输出丢弃由双方共同保证：
    /// Discard 幂等，后端可在取消或失败路径提前丢弃，调用方（工具层）在所有异常路径
    /// 兜底丢弃。进程启动失败抛 ProcessExecutionStartException（internal，继承
    /// InvalidOperationException）；等待、
    /// 输出捕获或进程树回收等基础设施失败按既有语义抛出带上下文的异常。契约不暴露 .NET
    /// <see cref="System.Diagnostics.Process"/>。
    ///
    /// Executes one frozen process plan. Returning normally implies the process
    /// has exited and both streams were fully drained into their captures
    /// (including the empty chunk appended after end-of-stream, which flushes
    /// the cross-chunk redaction tail). The command timeout is enforced by the
    /// backend per <see cref="PreparedProcessExecution.TimeoutSeconds"/>: it
    /// terminates the process tree, drains output, and returns with
    /// <see cref="ProcessExecutionResult.TimedOut"/> set while keeping the
    /// captured output. User cancellation throws
    /// <see cref="OperationCanceledException"/>. Output discarding is a shared
    /// responsibility: Discard is idempotent, the backend may discard early on
    /// cancellation or failure paths, and the caller (tool layer) discards as a
    /// safeguard on every exception path. Launch failures throw
    /// ProcessExecutionStartException (an internal subclass of
    /// InvalidOperationException); wait, output-capture,
    /// and process-tree reclamation failures throw contextual exceptions with
    /// the established semantics. The contract never exposes a .NET
    /// <see cref="System.Diagnostics.Process"/>.
    /// </summary>
    Task<ProcessExecutionResult> ExecuteAsync(
        PreparedProcessExecution execution,
        IProcessOutputCapture     stdoutCapture,
        IProcessOutputCapture     stderrCapture,
        CancellationToken         cancellationToken);
}
