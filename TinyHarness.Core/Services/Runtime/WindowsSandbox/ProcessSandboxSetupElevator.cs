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
    public required SandboxSetupElevationOutcome Outcome { get; init; }

    /// <summary>helper 进程退出码；仅 Outcome == Completed 时有值。</summary>
    public int? ExitCode { get; init; }

    public static readonly SandboxSetupElevationResult Completed0 = new()
    {
        Outcome = SandboxSetupElevationOutcome.Completed, ExitCode = 0
    };

    public static SandboxSetupElevationResult Completed(int exitCode)
    {
        return new SandboxSetupElevationResult { Outcome = SandboxSetupElevationOutcome.Completed, ExitCode = exitCode };
    }

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
    Task<SandboxSetupElevationResult> ElevateAsync(WindowsSandboxComponents components,
                                                   string                   base64Payload,
                                                   CancellationToken        cancellationToken);
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

    public async Task<SandboxSetupElevationResult> ElevateAsync(WindowsSandboxComponents components,
                                                                string                   base64Payload,
                                                                CancellationToken        cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(components);
        ArgumentException.ThrowIfNullOrWhiteSpace(base64Payload);
        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo(components.SetupExecutablePath)
        {
            Arguments        = base64Payload,
            UseShellExecute  = true,
            Verb             = "runas",
            // CreateNoWindow is ignored under UseShellExecute; a console-subsystem
            // helper may briefly show a window.
            WorkingDirectory = Path.GetDirectoryName(components.SetupExecutablePath)!
        };

        using var process = new Process();
        process.StartInfo = startInfo;
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

        using var timeoutSource     = new CancellationTokenSource(_timeout);
        using var linkedSource      = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
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
            return SandboxSetupElevationResult.TimedOut;
        }
        catch (OperationCanceledException)
        {
            // The caller cancelled; same unknown-state reasoning as timeouts.
            return SandboxSetupElevationResult.Cancelled;
        }
    }
}
