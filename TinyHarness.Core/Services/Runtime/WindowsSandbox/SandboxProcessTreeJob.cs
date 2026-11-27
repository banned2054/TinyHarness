using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     <para>
///         runner 外层 Job 的进程树策略：禁用（保持既有行为，不创建外层 Job）、后台保留（普通 Shell：命令返回后
///         允许后台后代存活，宿主继续查询外层 Job 直到清空）、严格整树退出（C2：runner 退出后必须确认整棵
///         进程树退出）。
///     </para>
///     <para>
///         The runner's outer-job process-tree policy: disabled (existing behavior, no outer job), background
///         (ordinary shell: descendants may survive after the command returns and the host keeps querying the
///         outer job until it drains), and strict whole-tree exit (C2: the entire process tree must be confirmed
///         exited after the runner exits).
///     </para>
/// </summary>
internal enum SandboxProcessTreeMode
{
    /// <summary>
    ///     <para>不创建外层 Job，启动路径与既有实现完全一致。</para>
    ///     <para>No outer job; the launch path matches the existing implementation exactly.</para>
    /// </summary>
    Disabled,

    /// <summary>
    ///     <para>创建外层 Job 并保留后台后代；宿主以外层 Job 的活跃进程数判断槽位何时可退役。</para>
    ///     <para>
    ///         Creates the outer job and preserves background descendants; the host uses the outer job's active
    ///         process count to decide when the slot may retire.
    ///     </para>
    /// </summary>
    Background,

    /// <summary>
    ///     <para>创建外层 Job，并要求 runner 退出后整棵进程树在限期内清空，否则终止外层 Job 并再次确认。</para>
    ///     <para>
    ///         Creates the outer job and requires the whole tree to drain within a bounded time after the runner
    ///         exits; otherwise the outer job is terminated and confirmation is repeated.
    ///     </para>
    /// </summary>
    Strict
}

/// <summary>
///     <para>
///         宿主侧外层 Job Object：跟踪 runner 及其后代，提供活跃进程数查询、限时清空等待与显式终止。它与 Rust
///         runner 内部 Job 在 Windows 8+ 上形成嵌套，因此不需要修改 runner。
///     </para>
///     <para>
///         The host-side outer Job Object: it tracks the runner and its descendants and offers active-process
///         queries, bounded drain waits, and explicit termination. On Windows 8+ it nests with the Rust
///         runner's own job, so the runner needs no changes.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         不设置 <c>KILL_ON_JOB_CLOSE</c>：关闭句柄不会杀死仍在运行的后台后代，与既有"命令返回后后台进程
///         存活"的行为一致；需要整树退出时由 <see cref="SandboxProcessTreeExit" /> 显式调用
///         <see cref="Terminate" />。
///     </para>
///     <para>
///         <c>KILL_ON_JOB_CLOSE</c> is not set: closing the handle never kills surviving background descendants,
///         matching the existing "background processes outlive the command" behavior; when the whole tree must
///         exit, <see cref="SandboxProcessTreeExit" /> calls <see cref="Terminate" /> explicitly.
///     </para>
/// </remarks>
internal sealed partial class SandboxProcessTreeJob : IDisposable
{
    private const uint BasicAccountingInformationClass = 1;
    private const uint WaitObject0                     = 0;
    private const uint WaitTimeout                     = 258;

    /// <summary>
    ///     <para>等待进程退出的内部期限：终止后用它确认进程确实不再运行。</para>
    ///     <para>Internal deadline used to confirm a terminated process is really gone.</para>
    /// </summary>
    private static readonly TimeSpan TerminateConfirmationTimeout = TimeSpan.FromSeconds(5);

    private readonly SafeFileHandle _job;

    private SandboxProcessTreeJob(SafeFileHandle job) => _job = job;

    /// <summary>
    ///     <para>外层 Job 句柄；仅用于 <c>AssignProcessToJobObject</c>，所有权仍属于本对象。</para>
    ///     <para>The outer job handle, used only for <c>AssignProcessToJobObject</c>; this object keeps ownership.</para>
    /// </summary>
    internal SafeFileHandle Handle => _job;

    /// <summary>
    ///     <para>
    ///         外层 Job 当前活跃进程数（含后台后代）。查询失败抛出 Win32 异常，关闭后抛
    ///         <see cref="ObjectDisposedException" />，绝不把"查不到"报告成 0。
    ///     </para>
    ///     <para>
    ///         The outer job's current active process count (background descendants included). Query failures
    ///         throw, and a disposed job throws <see cref="ObjectDisposedException" />; "unknown" is never reported
    ///         as zero.
    ///     </para>
    /// </summary>
    internal int ActiveProcesses
    {
        get
        {
            ThrowIfDisposed();
            if (!QueryInformationJobObject(_job, BasicAccountingInformationClass, out var information,
                                           (uint)Marshal.SizeOf<JobObjectBasicAccountingInformation>(),
                                           IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastPInvokeError(),
                                         "Failed to query the active process count of the sandbox runner outer job.");
            return (int)information.ActiveProcesses;
        }
    }

    public void Dispose() => _job.Dispose();

    /// <summary>
    ///     <para>创建匿名外层 Job；创建失败抛出 Win32 异常，不返回半成品。</para>
    ///     <para>Creates the anonymous outer job; a failure throws instead of returning a half-built object.</para>
    /// </summary>
    internal static SandboxProcessTreeJob Create()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "The sandbox runner outer job requires Windows Job Object semantics.");

        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            job.Dispose();
            throw new Win32Exception(error, "Failed to create the sandbox runner outer job object.");
        }

        return new SandboxProcessTreeJob(job);
    }

    /// <summary>
    ///     <para>把已存在的进程挂入外层 Job；挂载失败返回 false，错误码由调用方读取。</para>
    ///     <para>
    ///         Assigns an existing process to the outer job; false means the assignment failed and the caller owns
    ///         the error reporting.
    ///     </para>
    /// </summary>
    internal bool TryAssign(SafeProcessHandle process)
    {
        ArgumentNullException.ThrowIfNull(process);
        ThrowIfDisposed();
        return AssignProcessToJobObject(_job, process);
    }

    /// <summary>
    ///     <para>限时等待外层 Job 清空；返回 true 只表示在期限内观察到 <c>ActiveProcesses == 0</c>。</para>
    ///     <para>
    ///         Waits a bounded time for the outer job to drain; true only means
    ///         <c>ActiveProcesses == 0</c> was observed within the deadline.
    ///     </para>
    /// </summary>
    internal async Task<bool> WaitForEmptyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var effectiveTimeout = timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout;
        var interval         = TimeSpan.FromMilliseconds(25);
        var startedAt        = Stopwatch.GetTimestamp();
        while (true)
        {
            if (ActiveProcesses == 0) return true;
            var elapsed = Stopwatch.GetElapsedTime(startedAt);
            if (elapsed >= effectiveTimeout) return false;
            var remaining = effectiveTimeout - elapsed;
            await Task.Delay(remaining < interval ? remaining : interval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     <para>终止外层 Job 中的全部进程；终止失败或 Job 已关闭时抛出，不做静默降级。</para>
    ///     <para>
    ///         Terminates every process in the outer job; termination failures or a disposed job throw instead of
    ///         degrading silently.
    ///     </para>
    /// </summary>
    internal void Terminate()
    {
        ThrowIfDisposed();
        if (!TerminateJobObject(_job, 1))
            throw new Win32Exception(Marshal.GetLastPInvokeError(),
                                     "Failed to terminate the sandbox runner outer job object.");
    }

    /// <summary>
    ///     <para>
    ///         把 <c>CREATE_SUSPENDED</c> 启动的 runner 挂入外层 Job 并恢复主线程：先挂载再恢复，杜绝 runner 在被
    ///         跟踪前派生逃逸子进程；挂载或恢复失败时终止该挂起进程、释放两个句柄并抛出带上下文的异常，绝不把
    ///         挂起进程留在后台。
    ///     </para>
    ///     <para>
    ///         Attaches a <c>CREATE_SUSPENDED</c> runner to the outer job and then resumes it: attach first,
    ///         resume second, so the runner cannot spawn an untracked child before containment is established.
    ///         If the attach or the resume fails, the suspended process is terminated, both handles are released,
    ///         and a contextual exception is thrown — a suspended process is never left behind.
    ///     </para>
    /// </summary>
    /// <param name="process">
    ///     <para>新建挂起进程句柄；成功时所有权交给调用方，失败时由本方法终止并释放。</para>
    ///     <para>The newly created suspended process handle; ownership passes to the caller on success and is terminated and released here on failure.</para>
    /// </param>
    /// <param name="thread">
    ///     <para>主线程句柄；无论成功与否均由本方法关闭。</para>
    ///     <para>The primary thread handle; always closed by this method on both success and failure.</para>
    /// </param>
    /// <param name="tree">
    ///     <para>非空外层 Job；进程挂入该 Job 后才恢复主线程。</para>
    ///     <para>The non-null outer job; the primary thread is resumed only after assignment succeeds.</para>
    /// </param>
    /// <param name="processId">
    ///     <para>runner 进程 ID，仅用于错误消息。</para>
    ///     <para>The runner process ID, used in error messages only.</para>
    /// </param>
    /// <param name="accountName">
    ///     <para>runner 账户名，仅用于错误消息。</para>
    ///     <para>The runner account name, used in error messages only.</para>
    /// </param>
    internal static void AttachSuspendedProcess(
        SafeProcessHandle     process,
        SafeFileHandle        thread,
        SandboxProcessTreeJob tree,
        uint                  processId,
        string                accountName)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(tree);

        var        assigned    = false;
        Exception? assignError = null;
        try
        {
            assigned = tree.TryAssign(process);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or Win32Exception)
        {
            assignError = ex;
        }

        if (!assigned)
        {
            var error   = assignError is null ? Marshal.GetLastPInvokeError() : 0;
            var cleanup = TerminateSuspendedProcess(process, thread, processId);
            var message = assignError is null
                ? $"Failed to assign the suspended sandbox runner process {processId} ('{accountName}') to the host " +
                  $"outer job object (Win32 error {error}); the suspended process was terminated and both handles were released."
                : $"Failed to assign the suspended sandbox runner process {processId} ('{accountName}') to the host " +
                  $"outer job object: {assignError.Message}; the suspended process was terminated and both handles were released.";
            if (cleanup is not null)
                throw new ToolExecutionUncertainException(
                    $"{message} Cleanup also failed: {cleanup}", assignError);
            if (assignError is not null) throw new InvalidOperationException(message, assignError);
            throw new Win32Exception(error, message);
        }

        if (ResumeThread(thread) == uint.MaxValue)
        {
            var error   = Marshal.GetLastPInvokeError();
            var cleanup = TerminateSuspendedProcess(process, thread, processId);
            var message = $"Failed to resume the sandbox runner process {processId} ('{accountName}') after attaching it " +
                          "to the host outer job object; the suspended process was terminated and both handles were released.";
            if (cleanup is not null)
                throw new ToolExecutionUncertainException(
                    $"{message} Cleanup also failed: {cleanup}", new Win32Exception(error, message));
            throw new Win32Exception(error, message);
        }

        thread.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_job.IsClosed || _job.IsInvalid) throw new ObjectDisposedException(nameof(SandboxProcessTreeJob));
    }

    private static string? TerminateSuspendedProcess(SafeProcessHandle process, SafeFileHandle thread, uint processId)
    {
        var terminated = TerminateProcess(process, 1);
        var error      = terminated ? 0 : Marshal.GetLastPInvokeError();
        var waited     = terminated
            ? WaitForSingleObject(process, (uint)TerminateConfirmationTimeout.TotalMilliseconds)
            : WaitTimeout;
        process.Dispose();
        thread.Dispose();

        if (!terminated)
            return $"TerminateProcess on the suspended sandbox runner process {processId} failed (Win32 error {error}).";
        if (waited != WaitObject0)
            return $"The suspended sandbox runner process {processId} did not exit within " +
                   $"{(int)TerminateConfirmationTimeout.TotalSeconds}s after termination.";
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicAccountingInformation
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", StringMarshalling = StringMarshalling.Utf16,
                   SetLastError = true)]
    private static partial SafeFileHandle CreateJobObjectW(IntPtr securityAttributes, string? name);

    [LibraryImport("kernel32.dll", EntryPoint = "AssignProcessToJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryInformationJobObject(
        SafeFileHandle job, uint informationClass, out JobObjectBasicAccountingInformation information,
        uint           bufferLength, IntPtr returnLength);

    [LibraryImport("kernel32.dll", EntryPoint = "TerminateJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateJobObject(SafeFileHandle job, uint exitCode);

    [LibraryImport("kernel32.dll", EntryPoint = "TerminateProcess", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [LibraryImport("kernel32.dll", EntryPoint = "WaitForSingleObject", SetLastError = true)]
    private static partial uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [LibraryImport("kernel32.dll", EntryPoint = "ResumeThread", SetLastError = true)]
    private static partial uint ResumeThread(SafeFileHandle thread);
}

/// <summary>
///     <para>
///         整树退出确认：严格模式在 runner 退出后限时等待外层 Job 清空，超时则终止外层 Job 并再次确认；仍无法
///         确认、查询/终止失败或等待被外部取消中断时均抛出 <see cref="ToolExecutionUncertainException" />，让
///         调用方（C2 恢复路径）持久化禁用而不是把未知或被中断的回收状态当成普通取消或成功。
///     </para>
///     <para>
///         Whole-tree exit confirmation: in strict mode the outer job is given a bounded time to drain after the
///         runner exits, then terminated and re-confirmed on timeout; when confirmation still fails, a query or
///         termination fails, or waiting is interrupted by cancellation,
///         <see cref="ToolExecutionUncertainException" /> is thrown so callers (the C2 recovery path) persist the
///         disable state instead of treating an unconfirmed reclamation as ordinary cancellation or success.
///     </para>
/// </summary>
internal static class SandboxProcessTreeExit
{
    internal static readonly TimeSpan DefaultDrainTimeout     = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan DefaultTerminationGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     <para>
    ///         确认整棵进程树退出；返回 true 表示自然清空，false 表示终止外层 Job 后才清空。C2 严格收尾应传入
    ///         <see cref="CancellationToken.None" /> 靠有界期限控制；若等待期间仍被外部取消，会先尽力终止外层
    ///         Job 再抛出 <see cref="ToolExecutionUncertainException" />，避免跳过禁用保护。
    ///     </para>
    ///     <para>
    ///         Confirms the whole process tree exited; true means it drained naturally and false means it drained
    ///         only after the outer job was terminated. Strict C2 teardown should pass
    ///         <see cref="CancellationToken.None" /> and rely on bounded deadlines; if externally cancelled while
    ///         waiting, attempts to terminate the outer job before throwing
    ///         <see cref="ToolExecutionUncertainException" /> so disable protection is never bypassed.
    ///     </para>
    /// </summary>
    /// <exception cref="ToolExecutionUncertainException">
    ///     <para>终止后仍无法确认整树退出、查询/终止失败，或确认等待被取消打断。</para>
    ///     <para>The tree still could not be confirmed empty after termination, a query/termination failed, or confirmation was interrupted by cancellation.</para>
    /// </exception>
    internal static async Task<bool> ConfirmWholeTreeExitAsync(
        SandboxProcessTreeJob tree,
        TimeSpan              drainTimeout,
        TimeSpan              terminationGrace,
        CancellationToken     cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tree);
        Exception? initialQueryError   = null;
        Exception? firstTerminateError = null;
        try
        {
            var drained = false;
            try
            {
                drained = await tree.WaitForEmptyAsync(drainTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (Win32Exception ex)
            {
                // 首次查询失败时仍须继续执行终止与有界复查，避免跳过终止或漏过可确认的收尾。
                initialQueryError = ex;
            }

            if (drained) return true;

            try
            {
                tree.Terminate();
            }
            catch (Win32Exception ex)
            {
                // 首次终止失败时尝试一次补救终止；若补救成功则继续执行后续有界复查。
                firstTerminateError = ex;
                tree.Terminate();
            }

            if (await tree.WaitForEmptyAsync(terminationGrace, cancellationToken).ConfigureAwait(false))
                return false;

            throw new ToolExecutionUncertainException(
                $"The sandbox runner outer job still reports {tree.ActiveProcesses} active process(es) after termination " +
                $"and a {(int)terminationGrace.TotalSeconds}s grace; the whole process tree exit is unconfirmed.",
                CombineErrors(initialQueryError, firstTerminateError));
        }
        catch (OperationCanceledException ex)
        {
            // 取消绝不能绕过整树回收确认：尽力终止外层 Job 并以不确定异常通知上层（例如 C2 持久化禁用）。
            TryTerminateQuietly(tree);
            throw new ToolExecutionUncertainException(
                "Whole-tree exit confirmation was cancelled before the sandbox runner outer job drained; " +
                "termination was attempted, but whole-tree exit remains unconfirmed.",
                CombineErrors(initialQueryError, firstTerminateError, ex));
        }
        catch (Exception ex) when (ex is Win32Exception or ObjectDisposedException)
        {
            throw new ToolExecutionUncertainException(
                $"The sandbox runner whole process tree exit could not be confirmed: {ex.Message}",
                CombineErrors(initialQueryError, firstTerminateError, ex));
        }
    }

    private static void TryTerminateQuietly(SandboxProcessTreeJob tree)
    {
        try
        {
            tree.Terminate();
        }
        catch (Exception ex) when (ex is Win32Exception or ObjectDisposedException)
        {
        }
    }

    private static Exception? CombineErrors(
        Exception? first, Exception? second = null, Exception? third = null)
    {
        var count = (first is not null ? 1 : 0) + (second is not null ? 1 : 0) + (third is not null ? 1 : 0);
        if (count == 0) return null;
        if (count == 1) return first ?? second ?? third;

        var list = new List<Exception>(count);
        if (first is not null) list.Add(first);
        if (second is not null) list.Add(second);
        if (third is not null) list.Add(third);
        return new AggregateException(list);
    }
}
