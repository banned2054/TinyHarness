using System.ComponentModel;
using System.Diagnostics;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     提权执行 setup.exe 的结果：helper 进程已结束（见 ExitCode）、用户在 UAC 对话框拒绝、
///     超时或等待被取消。超时/取消后 helper 状态未知——绝不试图 kill 提权进程，由调用方如实报告。
///     Result of elevating setup.exe: the helper process finished (see
///     ExitCode), the user declined the UAC prompt, or the wait timed out or
///     was cancelled. After a timeout/cancel the helper state is unknown — the
///     elevated process is never killed; the caller reports that honestly.
/// </summary>
public sealed record SandboxSetupElevationResult
{
    public static readonly SandboxSetupElevationResult Completed0 = new()
    {
        Outcome = SandboxSetupElevationOutcome.Completed, ExitCode = 0
    };

    public static readonly SandboxSetupElevationResult Declined = new()
    {
        Outcome = SandboxSetupElevationOutcome.DeclinedByUser
    };

    public static readonly SandboxSetupElevationResult TimedOut = new()
    {
        Outcome = SandboxSetupElevationOutcome.DidNotFinish
    };

    public static readonly SandboxSetupElevationResult Cancelled = new()
    {
        Outcome = SandboxSetupElevationOutcome.DidNotFinish
    };

    public required SandboxSetupElevationOutcome Outcome { get; init; }

    /// <summary>helper 进程退出码；仅 Outcome == Completed 时有值。</summary>
    public int? ExitCode { get; init; }

    /// <summary>
    ///     <para>
    ///         仅 <see cref="SandboxSetupElevationOutcome.DidNotFinish" /> 时可选携带的后台退出确认任务：
    ///         提权 helper 进程退出后完成 true，等待失败则完成 false。为 null 时表示没有可追踪的退出确认，
    ///         调用方必须 fail-closed 保持刷新门持有而不提前释放。
    ///     </para>
    ///     <para>
    ///         Optional background exit-confirmation task carried on
    ///         <see cref="SandboxSetupElevationOutcome.DidNotFinish" />: completes true once the elevated
    ///         helper exits and false when waiting fails. Null means exit confirmation is unavailable,
    ///         so callers must keep the refresh gate held fail-closed instead of releasing early.
    ///     </para>
    /// </summary>
    public Task<bool>? ExitConfirmation { get; init; }

    /// <summary>
    ///     <para>
    ///         已启动的提权 helper 进程身份（PID 与创建时间），供跨进程持久化租约在调用方 CLI 提前退出后
    ///         仍能向 OS 核对实际写入者是否仍在运行。
    ///     </para>
    ///     <para>
    ///         Started elevated helper process identity (PID and creation time), used by durable
    ///         cross-process leases to verify with the OS whether the actual writer is still running even
    ///         after the calling CLI process exits early.
    ///     </para>
    /// </summary>
    public SandboxElevatedProcessIdentity? ProcessIdentity { get; init; }

    public static SandboxSetupElevationResult Completed(int exitCode) =>
        new() { Outcome = SandboxSetupElevationOutcome.Completed, ExitCode = exitCode };

    public static SandboxSetupElevationResult DidNotFinish(
        Task<bool>?                     exitConfirmation,
        SandboxElevatedProcessIdentity? processIdentity = null) =>
        new()
        {
            Outcome          = SandboxSetupElevationOutcome.DidNotFinish,
            ExitConfirmation = exitConfirmation,
            ProcessIdentity  = processIdentity
        };
}

/// <summary>提权执行的终态：已完成 / 用户拒绝 / 未在期限内完成（超时或取消，状态未知）。</summary>
public enum SandboxSetupElevationOutcome
{
    /// <summary>helper 进程已退出；成败看 ExitCode 与 setup_error.json。</summary>
    Completed,

    /// <summary>用户在 UAC 对话框点击了取消（Win32 错误 1223）。</summary>
    DeclinedByUser,

    /// <summary>超时或等待被取消；helper 状态未知，不 kill 提权进程。</summary>
    DidNotFinish
}

/// <summary>
///     仅管理入口（sandbox provision）使用的 setup.exe 提权执行器。执行路径永远只做
///     refresh_only=true 的普通权限调用（见 <see cref="ISandboxSetupInvoker" />），绝不提权。
///     The setup.exe elevation runner used by the management entry point
///     (`sandbox provision`) only. The execution path always performs the
///     normal-privilege refresh_only=true call (see <see cref="ISandboxSetupInvoker" />);
///     it never elevates.
/// </summary>
public interface ISandboxSetupElevator
{
    /// <summary>
    ///     以 UAC（runas）启动 setup.exe 并有界等待退出。payload base64 是唯一位置参数——
    ///     UseShellExecute=true 下无法使用环境分块通道。
    ///     Starts setup.exe under UAC (runas) and waits for exit within a bound.
    ///     The base64 payload is the single positional argument — the chunked
    ///     environment channel is unavailable with UseShellExecute=true.
    /// </summary>
    Task<SandboxSetupElevationResult> ElevateAsync(
        WindowsSandboxComponents components, string base64Payload, CancellationToken cancellationToken);

    /// <summary>
    ///     <para>
    ///         以 UAC 启动 setup.exe，并在进程启动后立即回调其进程身份，使调用方在等待前即可落盘
    ///         跨进程租约，防止宿主持锁进程中途异常终止导致保护失效。默认实现委托给三参数重载。
    ///     </para>
    ///     <para>
    ///         Starts setup.exe under UAC and immediately reports the started process identity through
    ///         <paramref name="onProcessStarted" /> so the caller can persist the cross-process lease
    ///         before waiting, preserving protection even if the host crashes mid-wait. Defaults to the
    ///         three-argument overload.
    ///     </para>
    /// </summary>
    Task<SandboxSetupElevationResult> ElevateAsync(
        WindowsSandboxComponents               components,
        string                                 base64Payload,
        Action<SandboxElevatedProcessIdentity> onProcessStarted,
        CancellationToken                      cancellationToken) =>
        ElevateAsync(components, base64Payload, cancellationToken);
}

/// <summary>
///     提权执行器的进程实现：UseShellExecute=true + Verb="runas"，不重定向流
///     （helper 不使用 stdin/stdout 协议；CreateNoWindow 在 UseShellExecute 下无效，
///     console 子系统的 helper 可能短暂闪现窗口）。等待有界（默认 10 分钟）；超时/取消不
///     kill 提权进程，如实返回 DidNotFinish。用户拒绝 UAC（Win32 1223）映射为 DeclinedByUser。
///     Process-backed elevation: UseShellExecute=true with Verb="runas",
///     no redirected streams (the helper does not use stdin/stdout for its
///     protocol; CreateNoWindow is ignored under UseShellExecute, so a
///     console-subsystem helper may briefly show a window). The wait is
///     bounded (10 minutes by default); timeouts and cancellation never kill
///     the elevated process and honestly return DidNotFinish. A declined UAC
///     prompt (Win32 1223) maps to DeclinedByUser.
/// </summary>
public sealed class ProcessSandboxSetupElevator(TimeSpan? timeout = null) : ISandboxSetupElevator
{
    /// <summary>
    ///     提权等待期限；provisioning 涉及账户、注册表、防火墙与 ACL，给足固定上限。
    ///     The elevation wait bound; provisioning touches accounts, the
    ///     registry, the firewall, and ACLs, so it gets a generous fixed cap.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;

    public Task<SandboxSetupElevationResult> ElevateAsync(
        WindowsSandboxComponents components, string base64Payload, CancellationToken cancellationToken) =>
        ElevateCoreAsync(components, base64Payload, null, cancellationToken);

    public Task<SandboxSetupElevationResult> ElevateAsync(
        WindowsSandboxComponents               components,
        string                                 base64Payload,
        Action<SandboxElevatedProcessIdentity> onProcessStarted,
        CancellationToken                      cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onProcessStarted);
        return ElevateCoreAsync(components, base64Payload, onProcessStarted, cancellationToken);
    }

    private async Task<SandboxSetupElevationResult> ElevateCoreAsync(
        WindowsSandboxComponents                components,
        string                                  base64Payload,
        Action<SandboxElevatedProcessIdentity>? onProcessStarted,
        CancellationToken                       cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(components);
        ArgumentException.ThrowIfNullOrWhiteSpace(base64Payload);
        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo(components.SetupExecutablePath)
        {
            Arguments       = base64Payload,
            UseShellExecute = true,
            Verb            = "runas",
            // CreateNoWindow is ignored under UseShellExecute; a console-subsystem
            // helper may briefly show a window.
            WorkingDirectory = Path.GetDirectoryName(components.SetupExecutablePath)!
        };

        var process = new Process();
        process.StartInfo = startInfo;
        // 进程所有权显式管理：超时/取消未退出时移交给后台退出确认任务，其余路径由 finally 释放。
        var exitConfirmationOwningProcess = false;
        try
        {
            try
            {
                process.Start();
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // The user clicked "No" on the UAC consent dialog.
                return SandboxSetupElevationResult.Declined;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                throw new
                    InvalidOperationException($"Failed to launch the elevated sandbox setup helper '{components.SetupExecutablePath}': {ex.Message}",
                                              ex);
            }

            // 进程启动后立刻捕获 PID + 创建时间并通知调用方落盘跨进程租约，防止等待中途进程崩溃失守。
            var processIdentity = SandboxProvisionLeaseStore.TryCaptureProcessIdentity(process);
            if (processIdentity is not null)
                onProcessStarted?.Invoke(processIdentity);

            using var timeoutSource = new CancellationTokenSource(_timeout);
            using var linkedSource =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
            try
            {
                await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
                return SandboxSetupElevationResult.Completed(process.ExitCode);
            }
            catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested &&
                                                     !cancellationToken.IsCancellationRequested)
            {
                // The deadline elapsed; the elevated helper keeps running and its
                // state is unknown, so it is deliberately not killed here.
                exitConfirmationOwningProcess = true;
                return SandboxSetupElevationResult.DidNotFinish(StartExitConfirmationInBackground(process),
                                                                processIdentity);
            }
            catch (OperationCanceledException)
            {
                // The caller cancelled; same unknown-state reasoning as timeouts.
                exitConfirmationOwningProcess = true;
                return SandboxSetupElevationResult.DidNotFinish(StartExitConfirmationInBackground(process),
                                                                processIdentity);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
            {
                // 进程已启动后的等待异常同样无法确认 helper 已退出：收敛为 DidNotFinish，
                // 防止调用方把等待失败误判为"未启动"而提前清除持久租约。
                SandboxDiagnosticLog.Event("provision", $"elevated-wait-failed type={ex.GetType().Name}");
                exitConfirmationOwningProcess = true;
                return SandboxSetupElevationResult.DidNotFinish(StartExitConfirmationInBackground(process),
                                                                processIdentity);
            }
        }
        finally
        {
            if (!exitConfirmationOwningProcess)
                process.Dispose();
        }
    }

    /// <summary>
    ///     <para>
    ///         启动提权 helper 的后台退出确认任务：等待完成即确认进程退出（刷新门可释放）；
    ///         等待失败按未确认处理（门保持持有）；进程句柄在任务 finally 中释放。
    ///     </para>
    ///     <para>
    ///         Starts the background exit-confirmation task for the elevated helper: a completed wait
    ///         confirms process exit (allowing the refresh gate to release); a failed wait counts as
    ///         unconfirmed (keeping the gate held); the process handle is disposed in finally.
    ///     </para>
    /// </summary>
    private static Task<bool> StartExitConfirmationInBackground(Process process)
    {
        var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            try
            {
                try
                {
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                    SandboxDiagnosticLog.Event("provision", "elevated-helper-exit-confirmed gate-release-safe");
                    confirmation.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    // 等待失败保持未确认语义：门保持持有，不因确认失败而释放。
                    SandboxDiagnosticLog.Event("provision",
                                               $"elevated-helper-exit-confirmation-failed type={ex.GetType().Name}");
                    confirmation.TrySetResult(false);
                }
            }
            finally
            {
                process.Dispose();
            }
        });
        return confirmation.Task;
    }
}
