using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
#if DEBUG
using TinyHarness.Core.Services.Runtime;
#endif

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     <para>
///         每命令 ACL 刷新：以 refresh_only=true 调用 setup.exe（普通权限，绝不提权，失败不自动
///         转 UAC 或 provisioning）。
///     </para>
///     <para>
///         Per-command ACL refresh: invokes setup.exe with refresh_only=true at normal privilege
///         (never elevating, never falling back to UAC or provisioning on failure).
///     </para>
/// </summary>
/// <remarks>
///     <para>只表达 refresh 语义；provisioning 属于独立管理入口。</para>
///     <para>It models refresh semantics only; provisioning belongs to the separate management entry point.</para>
/// </remarks>
public interface ISandboxSetupInvoker
{
    Task RefreshAsync(SandboxSetupPayload payload, CancellationToken cancellationToken);
}

/// <summary>
///     <para>
///         setup.exe 的进程实现：payload base64 后作为唯一位置参数；超过 argv 安全阈值时切换
///         --launch-payload-env 分块环境通道。
///     </para>
///     <para>
///         Process-backed implementation for setup.exe: the base64 payload is passed as the single
///         positional argument, switching to the --launch-payload-env chunked environment channel
///         past the argv safety threshold.
///     </para>
/// </summary>
/// <remarks>
///     <para>失败读取 setup_error.json 报告具体错误。</para>
///     <para>Failures read setup_error.json for the concrete error report.</para>
///     <para>
///         退出契约定收三态：方法返回即 helper 已退出（exit code 已知，门可释放）；超时、取消或
///         初次等待失败在终止并确认退出后保持原 TimeoutException、取消或原异常语义；退出仍未确认
///         时抛出携带后台确认任务的不确定异常（超时形态派生自 TimeoutException，取消形态派生自
///         OperationCanceledException 并保留原 token，等待失败形态以原异常为 inner），进程所有权
///         随异常移交，调用方据此后台释放拒读刷新门。
///     </para>
///     <para>
///         The exit contract converges on three states: a normal return means the helper has
///         exited (the exit code is known and the gate may release); when the timeout,
///         cancellation, and initial-wait-failure paths terminate the helper and confirm the exit,
///         the original TimeoutException, cancellation, and exception semantics are kept; when the
///         exit stays unconfirmed an uncertainty exception carrying the background confirmation
///         task is thrown (the timeout form derives from TimeoutException, the cancellation form
///         derives from OperationCanceledException keeping the original token, and the wait-failure
///         form carries the original exception as its inner), with process ownership transferred
///         along with the exception so callers release the deny-read refresh gate in the
///         background.
///     </para>
/// </remarks>
public sealed class ProcessSandboxSetupInvoker(WindowsSandboxComponents components, TimeSpan? refreshTimeout = null)
    : ISandboxSetupInvoker
{
    /// <summary>
    ///     <para>base64 超过约 24000 个 UTF-16 单元必须走分块通道；此阈值留安全余量。</para>
    ///     <para>
    ///         Past roughly 24000 UTF-16 units of base64 the chunked channel is mandatory;
    ///         this threshold keeps a safety margin.
    ///     </para>
    /// </summary>
    internal const int PayloadArgumentCharacterLimit = 23_000;

    private static readonly TimeSpan DefaultRefreshTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    ///     <para>
    ///         退出确认等待的独立有界预算：不链接调用方 token（确认退出不随用户取消）；预算超界
    ///         即按退出未确认处理，转入后台确认任务。
    ///     </para>
    ///     <para>
    ///         The independent bounded budget for the exit-confirmation wait: it never links the
    ///         caller's token (confirming the exit does not follow user cancellation); exceeding the
    ///         budget counts the exit as unconfirmed and hands over to the background confirmation
    ///         task.
    ///     </para>
    /// </summary>
    internal static readonly TimeSpan ExitConfirmationBudget = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     <para>仅测试注入的终止步骤接缝：null 走真实终止与确认路径；生产代码不得设置。</para>
    ///     <para>
    ///         The test-only injection seam for the termination step: null runs the real termination
    ///         and confirmation path; production code must never set it.
    ///     </para>
    /// </summary>
    internal Func<Process, Task<SandboxHelperTermination>>? TerminationForTesting { get; set; }

    /// <summary>
    ///     <para>仅测试注入的初次等待接缝：null 走真实 WaitForExitAsync；生产代码不得设置。</para>
    ///     <para>
    ///         The test-only injection seam for the initial wait: null runs the real
    ///         WaitForExitAsync; production code must never set it.
    ///     </para>
    /// </summary>
    internal Func<Process, CancellationToken, Task>? InitialWaitForTesting { get; set; }

    /// <summary>
    ///     <para>
    ///         退出未确认时附加到异常消息的说明（超时、取消或初次等待失败路径）：调用方（backend
    ///         与授权协调器）的拒读刷新门据此后台等待确认而非立即归还。
    ///     </para>
    ///     <para>
    ///         The notice appended to exception messages when the exit is left unconfirmed (the
    ///         timeout, cancellation, and initial-wait-failure paths): the deny-read refresh gate
    ///         holders (the backend and the authorization coordinator) release through the
    ///         background confirmation instead of returning immediately.
    ///     </para>
    /// </summary>
    private const string ExitUnknownNotice =
        " The helper exit state is unknown (the initial wait, termination, or the confirmation wait failed); " +
        "it may still be writing deny-read bookkeeping, so the deny-read refresh gate stays held until the " +
        "exit is confirmed.";

    public async Task RefreshAsync(SandboxSetupPayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!payload.RefreshOnly)
            throw new
                ArgumentException("The process invoker runs refreshes only; provisioning must go through the management entry point.",
                                  nameof(payload));

        var payloadJson =
            JsonSerializer.SerializeToUtf8Bytes(payload, WindowsSandboxJsonContext.Default.SandboxSetupPayload);
        var base64Payload = Convert.ToBase64String(payloadJson);
        var stopwatch     = Stopwatch.StartNew();
#if DEBUG
        // 启动前先取写根 ACL 快照；快照差异只反映宿主可观察的目录状态，不代表 helper 内部 API 返回。
        var aclBefore = WorkspaceAclInheritanceDiagnostics.CaptureWriteRootAcls(payload);
        WorkspaceAclInheritanceDiagnostics.LogWriteRootAclSnapshot("before-helper", aclBefore);
#endif
        SandboxDiagnosticLog.Event("helper",
                                   $"start path='{components.SetupExecutablePath}' cwd='{payload.CommandWorkingDirectory}' " +
                                   $"home='{components.SandboxHome}' version={payload.Version} " +
                                   $"channel={(base64Payload.Length <= PayloadArgumentCharacterLimit ? "argv" : "env-chunks")} " +
                                   $"payloadChars={base64Payload.Length} " +
#if DEBUG
                                   // helper 二进制哈希帮助把日志与用户 VS 实际编译的 setup.exe 对应起来。
                                   $"sha256={SandboxProcessDiagnostics.DescribeFileSha256(components.SetupExecutablePath)} " +
#endif
                                   $"timeoutSecs={(int)(refreshTimeout ?? DefaultRefreshTimeout).TotalSeconds}");

        using var timeoutSource = new CancellationTokenSource(refreshTimeout ?? DefaultRefreshTimeout);
        using var linkedSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var process = new Process();
        process.StartInfo = BuildStartInfo(base64Payload);
        // 进程所有权显式管理：退出未确认时移交给后台确认任务，其余路径（含启动失败）由 finally 释放。
        var exitConfirmationOwningProcess = false;
        try
        {
            try
            {
                process.Start();
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                // 诊断只记异常类型、Win32 错误码与已知路径；ex.Message 不属白名单。
                SandboxDiagnosticLog.Event("helper",
                                           $"launch-failed type={ex.GetType().Name} " +
                                           $"win32Error={(ex is Win32Exception win32 ? win32.NativeErrorCode.ToString() : "<none>")} " +
                                           $"path='{components.SetupExecutablePath}'");
                throw new
                    InvalidOperationException($"Failed to launch the sandbox setup helper '{components.SetupExecutablePath}': {ex.Message}",
                                              ex);
            }

#if DEBUG
            // 只在存活窗口内读取 helper 实际 token；查询失败只记 win32Error，不改变启动顺序与后续等待。
            SandboxProcessDiagnostics.LogChildProcessToken(process);
#endif

            try
            {
                // 初次等待可经测试接缝替换（生产路径仍是真实等待）；接缝注入的失败与真实等待异常
                // 走同一组 catch，收敛到相同的三态契约。
                var waitTask = InitialWaitForTesting is { } initialWaitTesting
                                   ? initialWaitTesting(process, linkedSource.Token)
                                   : process.WaitForExitAsync(linkedSource.Token);
                await waitTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
            {
                SandboxDiagnosticLog.Event("helper", $"timeout elapsedMs={stopwatch.ElapsedMilliseconds}");
#if DEBUG
                WorkspaceAclInheritanceDiagnostics.LogWriteRootAclComparison("after-helper-timeout", aclBefore, payload);
#endif
                var termination = await TerminateAsync(process).ConfigureAwait(false);
                var message     = $"The sandbox setup helper did not finish refreshing ACLs within " +
                                  $"{(int)(refreshTimeout ?? DefaultRefreshTimeout).TotalSeconds}s.";
                if (termination.ExitConfirmed)
                    throw new TimeoutException(message);

                // 退出未确认：异常携带后台确认任务上抛，进程所有权随之移交，helper 可能仍在写簿记。
                exitConfirmationOwningProcess = true;
                throw new SandboxRefreshExitUncertainException(message + ExitUnknownNotice,
                                                               StartExitConfirmationInBackground(process),
                                                               termination.TerminationFailure);
            }
            catch (OperationCanceledException)
            {
                SandboxDiagnosticLog.Event("helper", $"cancelled elapsedMs={stopwatch.ElapsedMilliseconds}");
#if DEBUG
                WorkspaceAclInheritanceDiagnostics.LogWriteRootAclComparison("after-helper-cancel", aclBefore, payload);
#endif
                var termination = await TerminateAsync(process).ConfigureAwait(false);
                if (termination.ExitConfirmed) throw;

                // 退出未确认：保留原 token 的取消子类上抛（上层取消语义不变），进程所有权随之移交。
                exitConfirmationOwningProcess = true;
                throw new SandboxRefreshExitUncertainCancellationException(
                    $"The sandbox setup helper refresh was cancelled.{ExitUnknownNotice}",
                    cancellationToken, StartExitConfirmationInBackground(process));
            }
            catch (Exception ex)
            {
                // 初次等待的非取消异常（等待基础设施故障）：放任传播会让调用方在 helper 可能仍写
                // 簿记时提前释放拒读刷新门，故与超时路径同构收敛——终止后确认退出原样重抛（门释放
                // 安全，真实错误呈现给调用方）；未确认按退出未知上抛，原异常作 inner，所有权移交。
                SandboxDiagnosticLog.Event("helper",
                                           $"wait-failed type={ex.GetType().Name} elapsedMs={stopwatch.ElapsedMilliseconds}");
#if DEBUG
                WorkspaceAclInheritanceDiagnostics.LogWriteRootAclComparison("after-helper-wait-failed", aclBefore, payload);
#endif
                var termination = await TerminateAsync(process).ConfigureAwait(false);
                if (termination.ExitConfirmed) throw;

                exitConfirmationOwningProcess = true;
                throw new SandboxRefreshExitUncertainException(
                    $"The sandbox setup helper refresh wait failed ({ex.GetType().Name}).{ExitUnknownNotice}",
                    StartExitConfirmationInBackground(process), ex);
            }

            SandboxDiagnosticLog.Event("helper", $"exit code={process.ExitCode} elapsedMs={stopwatch.ElapsedMilliseconds}");
#if DEBUG
            WorkspaceAclInheritanceDiagnostics.LogWriteRootAclComparison("after-helper-exit", aclBefore, payload);
#endif
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Sandbox ACL refresh failed with exit code {process.ExitCode}: " +
                                                    $"{await SandboxSetupErrorReader.DescribeAsync(components.SetupErrorPath).ConfigureAwait(false)}; " +
                                                    $"command cwd: '{payload.CommandWorkingDirectory}'; helper: '{components.SetupExecutablePath}'; " +
                                                    $"home: '{components.SandboxHome}'; see '{components.SandboxDirectory}\\sandbox.<UTC-date>.log'. " +
                                                    "Refresh does not request UAC or retry provisioning; use /trust to inspect directory authorization.");
        }
        finally
        {
            // 所有权未随不确定异常移交给后台确认任务时在此释放；移交后由确认任务的 finally 释放。
            if (!exitConfirmationOwningProcess)
                process.Dispose();
        }
    }

    internal ProcessStartInfo BuildStartInfo(string base64Payload)
    {
        // helper 协议不使用 stdin/stdout；不重定向流可避免缓冲死锁与机器级信息被意外捕获。
        if (base64Payload.Length <= PayloadArgumentCharacterLimit)
            return new ProcessStartInfo(components.SetupExecutablePath)
            {
                Arguments        = base64Payload,
                UseShellExecute  = false,
                CreateNoWindow   = true,
                WorkingDirectory = Path.GetDirectoryName(components.SetupExecutablePath)!
            };

        // 大型 payload 走分块环境通道（CODEX_SANDBOX_LAUNCH_*），helper 改从环境变量读取而非 argv。
        var startInfo = new ProcessStartInfo(components.SetupExecutablePath)
        {
            Arguments        = "--launch-payload-env",
            UseShellExecute  = false,
            CreateNoWindow   = true,
            WorkingDirectory = Path.GetDirectoryName(components.SetupExecutablePath)!
        };
        var chunkSize  = 16                                     * 1024;
        var chunkCount = (base64Payload.Length + chunkSize - 1) / chunkSize;
        for (var index = 0; index < chunkCount; index++)
            startInfo.Environment[$"CODEX_SANDBOX_LAUNCH_{index}"] =
                base64Payload.Substring(index * chunkSize,
                                        Math.Min(chunkSize, base64Payload.Length - index * chunkSize));

        startInfo.Environment["CODEX_SANDBOX_LAUNCH_COUNT"] = chunkCount.ToString();
        startInfo.Environment["CODEX_SANDBOX_LAUNCH_BYTES"] = base64Payload.Length.ToString();
        return startInfo;
    }

    /// <summary>
    ///     <para>
    ///         终止步骤：全树 Kill 后以独立有界预算确认退出。kill 与已退出竞态可继续确认；其他
    ///         终止异常与确认等待异常记为终止失败且不再向上传播——退出状态由确认结果判定，测试
    ///         接缝存在时整个步骤可被替换。
    ///     </para>
    ///     <para>
    ///         The termination step: kills the full tree, then confirms the exit within the
    ///         independent bounded budget. The kill-versus-exited race may continue confirming; any
    ///         other termination exception and confirmation-wait exception is recorded as the
    ///         termination failure and never propagates — the exit state is decided by the
    ///         confirmation outcome, and the whole step is replaceable through the test seam.
    ///     </para>
    /// </summary>
    private Task<SandboxHelperTermination> TerminateAsync(Process process) =>
        TerminationForTesting is { } testing ? testing(process) : TerminateForRealAsync(process);

    private static async Task<SandboxHelperTermination> TerminateForRealAsync(Process process)
    {
        Exception? terminationFailure = null;
        try
        {
            process.Kill(true);
        }
        catch (InvalidOperationException)
        {
            // 取消与 kill 之间进程已自行退出：可继续确认等待。
        }
        catch (Exception ex)
        {
            // Win32Exception 等终止失败：不再向上传播（原行为会丢失 TimeoutException/OCE 语义），
            // 保留为终止结果，仅在退出未确认时随异常上抛。
            terminationFailure = ex;
        }

        try
        {
            // 确认预算独立成 CTS，不链接调用方 token：确认退出不随用户取消。
            using var confirmationSource = new CancellationTokenSource(ExitConfirmationBudget);
            await process.WaitForExitAsync(confirmationSource.Token).ConfigureAwait(false);
            if (terminationFailure is not null)
            {
                // 返回契约不变（仍报告退出已确认）；终止失败补记一条诊断，不静默丢弃。
                SandboxDiagnosticLog.Event("helper",
                                           $"termination-failed type={terminationFailure.GetType().Name} exit-confirmed");
            }

            return new SandboxHelperTermination(true, null);
        }
        catch (OperationCanceledException)
        {
            // 确认预算超界：helper 仍可能存活并写簿记，退出状态未确认。
            return new SandboxHelperTermination(false, terminationFailure);
        }
        catch (Exception ex)
        {
            // 确认等待的其他意外异常与句柄异常同型：记为终止失败（kill 失败优先），退出状态未
            // 确认且不向上传播——否则意外异常会绕过退出未确认处理，让调用方在退出未知时提前放门。
            return new SandboxHelperTermination(false, terminationFailure ?? ex);
        }
    }

    /// <summary>
    ///     <para>
    ///         启动后台退出确认任务：等待完成即确认退出（helper 不再写簿记，拒读刷新门可释放）；
    ///         等待异常按未确认处理（门保持持有）；诊断只记固定短语与类型名，进程释放在任务
    ///         finally 中完成——所有权已随不确定异常移交。
    ///     </para>
    ///     <para>
    ///         Starts the background exit-confirmation task: a completed wait confirms the exit (the
    ///         helper no longer writes bookkeeping and the deny-read refresh gate may release); a
    ///         failed wait counts as unconfirmed (the gate stays held); diagnostics record only
    ///         fixed phrases and type names, and the process is disposed in the task's finally
    ///         because ownership has already transferred with the uncertainty exception.
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
                    SandboxDiagnosticLog.Event("helper", "exit-confirmed gate-release-safe");
                    confirmation.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    // 等待失败保持未确认语义：门保持持有，不因确认失败而释放。
                    SandboxDiagnosticLog.Event("helper", $"exit-confirmation-failed type={ex.GetType().Name}");
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
