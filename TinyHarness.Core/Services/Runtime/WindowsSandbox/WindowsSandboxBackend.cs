using System.ComponentModel;
using System.Security.Principal;
using System.Text;
using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     后端各阶段的独立期限：refresh、spawn_ready、命令通信（超时后的宽限）、终止排空、
///     runner 回收与终止后的杀除宽限。取消后的清理使用独立期限，不复用已取消的 token。
///     Independent deadlines for every backend stage: refresh, spawn_ready,
///     command communication (grace past the command timeout), termination drain,
///     runner reclamation, and the post-kill grace. Cancellation cleanup uses its
///     own deadline and never reuses an already-cancelled token.
/// </summary>
public sealed record WindowsSandboxBackendLimits
{
    public static readonly WindowsSandboxBackendLimits Default = new();

    public TimeSpan RefreshTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan SpawnReadyTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    ///     命令超时后等待 runner 终态的额外宽限；runner 自身执行超时判定。
    ///     Extra grace past the command timeout while waiting for the runner's
    ///     verdict; the runner enforces the command timeout itself.
    /// </summary>
    public TimeSpan CommunicationGrace { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan TerminationDrainTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan RunnerExitTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan TerminationKillGrace { get; init; } = TimeSpan.FromSeconds(5);

    internal TimeSpan TerminateWriteTimeout { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
///     Windows 沙箱执行后端：编排状态检查 → ACL refresh → 凭据/capability SID → 私有桌面 →
///     runner 启动与 v6 帧协议会话。任何前置失败、组件缺失或协议失败均 fail closed（抛
///     ProcessExecutionStartException / 带上下文异常），绝不静默回退宿主执行。命令结果只来自
///     runner 的 exit 帧（exit_code + timed_out 独立保留）；缺 exit 不补造退出码。
///     The Windows sandbox execution backend: orchestrates the readiness check →
///     ACL refresh → credentials/capability SIDs → private desktop → runner
///     launch and the v6 frame session. Any preflight failure, missing component,
///     or protocol failure fails closed (throwing ProcessExecutionStartException
///     or a contextual exception) and never falls back to host execution. Command
///     results come only from the runner's exit frame (exit_code and timed_out
///     kept independent); a missing exit never invents an exit code.
/// </summary>
public sealed class WindowsSandboxBackend : IProcessExecutionBackend
{
    private const ulong MaximumRunnerTimeoutMilliseconds = 4_294_967_294;

    private readonly Action<SecurityIdentifier> _allowNullDeviceAccess;
    private readonly SandboxCapabilitySidStore  _capabilitySidStore;

    private readonly WindowsSandboxComponents            _components;
    private readonly ISandboxCredentialSource            _credentialSource;
    private readonly ISandboxDesktopFactory              _desktopFactory;
    private readonly IReadOnlyDictionary<string, string> _knownSecrets;
    private readonly WindowsSandboxBackendLimits         _limits;
    private readonly SandboxIsolationPolicy              _policy;
    private readonly ISandboxRunnerLauncher              _runnerLauncher;
    private readonly ISandboxSetupInvoker                _setupInvoker;
    private readonly WindowsSandboxStateInspector        _stateInspector;
    private readonly IReadOnlyDictionary<string, string> _targetEnvironment;

    public WindowsSandboxBackend(
        WindowsSandboxComponents             components,
        SandboxIsolationPolicy               policy,
        IReadOnlyDictionary<string, string>  targetEnvironment,
        IReadOnlyDictionary<string, string>? knownSecrets          = null,
        ISandboxSetupInvoker?                setupInvoker          = null,
        ISandboxCredentialSource?            credentialSource      = null,
        SandboxCapabilitySidStore?           capabilitySidStore    = null,
        ISandboxDesktopFactory?              desktopFactory        = null,
        ISandboxRunnerLauncher?              runnerLauncher        = null,
        WindowsSandboxBackendLimits?         limits                = null,
        Action<SecurityIdentifier>?          allowNullDeviceAccess = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The Windows sandbox backend is available on Windows only.");

        ArgumentNullException.ThrowIfNull(targetEnvironment);

        _components = components;
        _policy     = policy;
        // The frozen target environment comes from composition (built from
        // trusted settings); it is the same instance the policy's temp roots
        // were derived from, so ACLs and the spawned env cannot drift apart.
        _targetEnvironment  = targetEnvironment;
        _knownSecrets       = knownSecrets ?? new Dictionary<string, string>(StringComparer.Ordinal);
        _stateInspector     = new WindowsSandboxStateInspector(components);
        _setupInvoker       = setupInvoker       ?? new ProcessSandboxSetupInvoker(components, limits?.RefreshTimeout);
        _credentialSource   = credentialSource   ?? new SandboxCredentialReader(components);
        _capabilitySidStore = capabilitySidStore ?? new SandboxCapabilitySidStore(components);
        _desktopFactory     = desktopFactory     ?? new WindowsSandboxDesktopFactory();
        _runnerLauncher     = runnerLauncher     ?? new WindowsSandboxRunnerLauncher();
        _limits             = limits             ?? WindowsSandboxBackendLimits.Default;
        // The NUL device ACE is machine-level shared state; tests inject a
        // no-op so the default suite never modifies host ACLs.
        _allowNullDeviceAccess = allowNullDeviceAccess ?? SandboxNullDeviceAcl.AllowCapabilitySid;
    }

    public async Task<ProcessExecutionResult> ExecuteAsync(
        PreparedProcessExecution execution,
        IProcessOutputCapture    stdoutCapture,
        IProcessOutputCapture    stderrCapture,
        CancellationToken        cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(stdoutCapture);
        ArgumentNullException.ThrowIfNull(stderrCapture);
        cancellationToken.ThrowIfCancellationRequested();

        if (!Workspace.IsInside(_policy.WorkspaceRoot, execution.WorkingDirectory))
            throw new
                ProcessExecutionStartException($"The command working directory '{execution.WorkingDirectory}' is outside the sandbox workspace root '{_policy.WorkspaceRoot}'.",
                                               new
                                                   InvalidOperationException("Working directory escaped the frozen sandbox policy workspace root."));

        await PreflightAsync(execution, cancellationToken).ConfigureAwait(false);

        var       account        = await LoadAccount(cancellationToken).ConfigureAwait(false);
        var       capabilitySids = await ResolveCapabilitySids(cancellationToken).ConfigureAwait(false);
        using var desktop        = CreateDesktop(account);

        var                     request = BuildSpawnRequest(execution, capabilitySids, desktop.Name);
        SandboxRunnerConnection connection;
        try
        {
            connection = await _runnerLauncher
                              .LaunchAsync(new RunnerLaunchRequest(_components.RunnerExecutablePath, account,
                                                                   execution.WorkingDirectory), cancellationToken)
                              .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ProcessExecutionStartException($"Failed to launch the sandbox runner: {ex.Message}", ex);
        }

        using (connection)
        using (var session = RunnerSession.Start(connection, stdoutCapture, stderrCapture))
        using (var writerGate = new SemaphoreSlim(1, 1))
        {
            if (!await TrySendFrameAsync(connection, writerGate,
                                         stream => RunnerFrameCodec.WriteFrameAsync(stream, "spawn_request", request,
                                                  CancellationToken.None),
                                         cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ReclaimRunnerAsync(connection).ConfigureAwait(false);
                await AwaitReadLoopAsync(session).ConfigureAwait(false);
                stdoutCapture.Discard();
                stderrCapture.Discard();
                throw new
                    ProcessExecutionStartException("Failed to send the spawn_request frame to the sandbox runner.",
                                                   new IOException("The spawn_request write did not complete."));
            }

            RunnerHandshake handshake;
            try
            {
                handshake = await session.HandshakeTask.WaitAsync(_limits.SpawnReadyTimeout, cancellationToken)
                                         .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                await ReclaimRunnerAsync(connection).ConfigureAwait(false);
                await AwaitReadLoopAsync(session).ConfigureAwait(false);
                stdoutCapture.Discard();
                stderrCapture.Discard();
                throw new
                    ProcessExecutionStartException($"The sandbox runner did not report spawn_ready within {(int)_limits.SpawnReadyTimeout.TotalSeconds}s.",
                                                   new TimeoutException("The spawn_ready deadline elapsed."));
            }
            catch (OperationCanceledException)
            {
                await ReclaimRunnerAsync(connection).ConfigureAwait(false);
                await AwaitReadLoopAsync(session).ConfigureAwait(false);
                stdoutCapture.Discard();
                stderrCapture.Discard();
                throw;
            }
            catch (Exception ex)
            {
                await ReclaimRunnerAsync(connection).ConfigureAwait(false);
                await AwaitReadLoopAsync(session).ConfigureAwait(false);
                stdoutCapture.Discard();
                stderrCapture.Discard();
                throw new
                    ProcessExecutionStartException($"The sandbox runner session failed during handshake: {ex.Message}",
                                                   ex);
            }

            if (handshake.Error is not { } runnerError)
                return await RunSessionToEndAsync(execution, connection, session, writerGate,
                                                  stdoutCapture, stderrCapture, cancellationToken)
                   .ConfigureAwait(false);
            await ReclaimRunnerAsync(connection).ConfigureAwait(false);
            await AwaitReadLoopAsync(session).ConfigureAwait(false);
            stdoutCapture.Discard();
            stderrCapture.Discard();
            var windowsCode = runnerError.WindowsErrorCode is { } code ? $" (Win32 error {code})" : string.Empty;
            throw new
                ProcessExecutionStartException($"The sandbox runner failed at stage '{runnerError.Stage}'{windowsCode}: {runnerError.Message}",
                                               new
                                                   InvalidOperationException($"Runner error at stage '{runnerError.Stage}'."));
        }
    }

    private async Task PreflightAsync(PreparedProcessExecution execution, CancellationToken cancellationToken)
    {
        var status = await _stateInspector.InspectAsync(cancellationToken).ConfigureAwait(false);
        if (!status.Ready)
            throw new ProcessExecutionStartException("The Windows sandbox is not ready; refusing to execute: " +
                                                     string.Join("; ", status.Problems),
                                                     new
                                                         InvalidOperationException("Sandbox readiness check failed; failing closed without fallback."));

        try
        {
            await _setupInvoker
                 .RefreshAsync(_policy.CreateSetupPayload(_components, execution.WorkingDirectory, Environment.UserName,
                                                          true), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ProcessExecutionStartException($"The sandbox ACL refresh failed: {ex.Message}", ex);
        }
    }

    private async Task<WindowsSandboxAccount> LoadAccount(CancellationToken cancellationToken)
    {
        try
        {
            return await _credentialSource.LoadOfflineAccount(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ProcessExecutionStartException($"Failed to load the sandbox account credentials: {ex.Message}",
                                                     ex);
        }
    }

    private async Task<IReadOnlyList<string>> ResolveCapabilitySids(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The Windows sandbox backend is available on Windows only.");

        try
        {
            var capabilitySids = await _capabilitySidStore.ResolveForPolicy(_policy, cancellationToken)
                                                          .ConfigureAwait(false);
            // Mirror the library's NUL device allowance using the first
            // capability SID; the runner repeats this for the child.
            _allowNullDeviceAccess(new SecurityIdentifier(capabilitySids[0]));
            return capabilitySids;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ProcessExecutionStartException($"Failed to resolve sandbox capability SIDs: {ex.Message}", ex);
        }
    }

    private PrivateDesktop CreateDesktop(WindowsSandboxAccount account)
    {
        try
        {
            return _desktopFactory.CreatePrivateDesktop(account.AccountSid);
        }
        catch (Exception ex)
        {
            throw new ProcessExecutionStartException($"Failed to create the private sandbox desktop: {ex.Message}", ex);
        }
    }

    private RunnerSpawnRequest BuildSpawnRequest(
        PreparedProcessExecution execution, IReadOnlyList<string> capabilitySids, string desktopName)
    {
        var timeoutMilliseconds = (ulong)execution.TimeoutSeconds * 1000;
        if (timeoutMilliseconds > MaximumRunnerTimeoutMilliseconds)
            timeoutMilliseconds = MaximumRunnerTimeoutMilliseconds;

        return new RunnerSpawnRequest
        {
            Command                    = [execution.Executable, .. execution.Arguments],
            WorkingDirectory           = execution.WorkingDirectory,
            Environment                = BuildSpawnEnvironment(),
            PermissionProfile          = _policy.SpawnProfile,
            WorkspaceRoots             = [_policy.WorkspaceRoot],
            SandboxDirectory           = _components.SandboxDirectory,
            RealSandboxHome            = _components.SandboxHome,
            CapabilitySids             = capabilitySids,
            NetworkProxyRestrictingSid = null,
            TimeoutMilliseconds        = timeoutMilliseconds,
            Tty                        = false,
            StdinOpen                  = false,
            PrivateDesktopName         = desktopName
        };
    }

    /// <summary>
    ///     从组合期冻结的目标环境构造 spawn 环境字典（wire 模型要求可变 Dictionary）。已知
    ///     secret 键最后再移除一次：环境本就由可信 allowlist 构建，这里是防止密键复用的防线，
    ///     不是唯一的清理点。
    ///     Builds the spawn environment dictionary from the composition-time frozen
    ///     target environment (the wire model requires a mutable Dictionary). Known
    ///     secret keys are removed once more at the end: the environment is already
    ///     built from a trusted allowlist, so this is a defense against key reuse,
    ///     not the only cleanup point.
    /// </summary>
    private Dictionary<string, string> BuildSpawnEnvironment()
    {
        var environment = new Dictionary<string, string>(_targetEnvironment, StringComparer.OrdinalIgnoreCase);
        foreach (var secret in _knownSecrets) environment.Remove(secret.Key);

        return environment;
    }

    private async Task<ProcessExecutionResult> RunSessionToEndAsync(
        PreparedProcessExecution execution,  SandboxRunnerConnection connection,    RunnerSession         session,
        SemaphoreSlim            writerGate, IProcessOutputCapture   stdoutCapture, IProcessOutputCapture stderrCapture,
        CancellationToken        cancellationToken)
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(execution.TimeoutSeconds) +
                                                         _limits.CommunicationGrace);
        using var         linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, watchdog.Token);
        RunnerExitOutcome outcome;
        try
        {
            outcome = await session.ExitTask.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (watchdog.IsCancellationRequested &&
                                                 !cancellationToken.IsCancellationRequested)
        {
            return await HandleUnresponsiveRunnerAsync(connection, session, writerGate,
                                                       stdoutCapture, stderrCapture).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return await HandleUserCancellationAsync(connection, session, writerGate,
                                                     stdoutCapture, stderrCapture,
                                                     cancellationToken).ConfigureAwait(false);
        }

        if (!await ReclaimRunnerAsync(connection).ConfigureAwait(false))
            throw new
                InvalidOperationException($"The sandbox runner did not exit within {(int)_limits.RunnerExitTimeout.TotalSeconds}s " +
                                          "after reporting the command exit and could not be reclaimed.");

        await AwaitReadLoopAsync(session).ConfigureAwait(false);
        await session.FlushDecodersAsync().ConfigureAwait(false);
        await CompleteCapturesAsync(stdoutCapture, stderrCapture).ConfigureAwait(false);

        return outcome.FailureMessage is { } failure
            ? new ProcessExecutionResult(null, false, new ProcessExecutionFailure(failure))
            : new ProcessExecutionResult(outcome.ExitCode, outcome.TimedOut, null);
    }

    /// <summary>
    ///     命令超时后 runner 无响应：发送 terminate、限期等待终态；仍无终态则杀 runner 并按
    ///     后端超时语义返回（保留已捕获输出）。杀除失败或无法回收抛异常。
    ///     The runner is unresponsive past the command timeout: send terminate,
    ///     wait a bounded time for the verdict, and kill the runner if none
    ///     arrives, returning with backend timeout semantics (captured output
    ///     kept). Kill or reclamation failures throw.
    /// </summary>
    private async Task<ProcessExecutionResult> HandleUnresponsiveRunnerAsync(
        SandboxRunnerConnection connection,    RunnerSession         session, SemaphoreSlim writerGate,
        IProcessOutputCapture   stdoutCapture, IProcessOutputCapture stderrCapture)
    {
        await TrySendTerminateAsync(connection, writerGate).ConfigureAwait(false);
        RunnerExitOutcome outcome;
        try
        {
            outcome = await session.ExitTask.WaitAsync(_limits.TerminationDrainTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await KillAndReclaimAsync(connection).ConfigureAwait(false);
            await AwaitReadLoopAsync(session).ConfigureAwait(false);
            await session.FlushDecodersAsync().ConfigureAwait(false);
            await CompleteCapturesAsync(stdoutCapture, stderrCapture).ConfigureAwait(false);
            return new ProcessExecutionResult(null, true, null);
        }

        if (!await ReclaimRunnerAsync(connection).ConfigureAwait(false))
            throw new
                InvalidOperationException($"The sandbox runner did not exit within {(int)_limits.RunnerExitTimeout.TotalSeconds}s " +
                                          "after the command timeout and could not be reclaimed.");

        await AwaitReadLoopAsync(session).ConfigureAwait(false);
        await session.FlushDecodersAsync().ConfigureAwait(false);
        await CompleteCapturesAsync(stdoutCapture, stderrCapture).ConfigureAwait(false);

        // The backend enforced the timeout itself; a non-timeout verdict from a
        // terminated run still means the command did not finish on its own.
        return new ProcessExecutionResult(outcome.ExitCode, true,
                                          outcome.FailureMessage is { } failure
                                              ? new ProcessExecutionFailure(failure)
                                              : null);
    }

    private async Task<ProcessExecutionResult> HandleUserCancellationAsync(
        SandboxRunnerConnection connection,    RunnerSession         session, SemaphoreSlim writerGate,
        IProcessOutputCapture   stdoutCapture, IProcessOutputCapture stderrCapture,
        CancellationToken       cancellationToken)
    {
        // Cleanup uses its own deadline, never the cancelled token.
        await TrySendTerminateAsync(connection, writerGate).ConfigureAwait(false);
        try
        {
            await session.ExitTask.WaitAsync(_limits.TerminationDrainTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await KillAndReclaimAsync(connection).ConfigureAwait(false);
        }

        if (!await ReclaimRunnerAsync(connection).ConfigureAwait(false))
            throw new
                InvalidOperationException($"The sandbox runner did not exit within {(int)_limits.RunnerExitTimeout.TotalSeconds}s " +
                                          "after cancellation and could not be reclaimed.");

        await AwaitReadLoopAsync(session).ConfigureAwait(false);
        stdoutCapture.Discard();
        stderrCapture.Discard();
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("Unreachable: cancellation must have thrown above.");
    }

    /// <summary>
    ///     限时回收 runner：先等自然退出，超时后终止（已退出的竞态不视为失败）再等杀除宽限。
    ///     Reclaims the runner within its deadline: wait for a natural exit, then
    ///     terminate on timeout (losing the exit race is not a failure) and wait
    ///     out the kill grace.
    /// </summary>
    private async Task<bool> ReclaimRunnerAsync(SandboxRunnerConnection connection)
    {
        if (await connection.WaitForExitAsync(_limits.RunnerExitTimeout).ConfigureAwait(false)) return true;

        try
        {
            connection.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // The process exited between the wait and the kill.
        }

        return await connection.WaitForExitAsync(_limits.TerminationKillGrace).ConfigureAwait(false);
    }

    private async Task KillAndReclaimAsync(SandboxRunnerConnection connection)
    {
        try
        {
            connection.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Already gone; the wait below confirms.
        }

        if (!await connection.WaitForExitAsync(_limits.TerminationKillGrace).ConfigureAwait(false))
            throw new
                TimeoutException($"Terminating the unresponsive sandbox runner did not complete within {(int)_limits.TerminationKillGrace.TotalSeconds}s.");
    }

    /// <summary>
    ///     限时等待会话读循环退出后再操作捕获器：消除 fire-and-forget 读循环与结束路径对捕获器
    ///     的并发 Append 竞态。读循环内部捕获全部异常不会 fault，等待超时被有意吞掉——循环可能
    ///     只是未在期限内确认退出，继续按后续兜底路径收尾；任何等待异常同样不外抛，不掩盖原有
    ///     结束语义。
    ///     Waits a bounded time for the session read loop to exit before touching
    ///     the captures, removing the concurrent-Append race between the
    ///     fire-and-forget loop and the teardown paths. The loop catches every
    ///     exception internally and never faults; a wait timeout is deliberately
    ///     swallowed — the loop merely failed to confirm its exit within the
    ///     deadline, so the fallback teardown below proceeds — and any other wait
    ///     failure is equally contained so it never masks the original teardown
    ///     semantics.
    /// </summary>
    private async Task AwaitReadLoopAsync(RunnerSession session)
    {
        try
        {
            await session.ReadTask.WaitAsync(_limits.TerminationDrainTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Deliberate: continue with the fallback teardown.
        }
        catch (Exception)
        {
            // Defensive: ReadFramesAsync never faults by design; a wait failure
            // must not introduce a new exception during teardown.
        }
    }

    private static async Task CompleteCapturesAsync(IProcessOutputCapture stdoutCapture,
                                                    IProcessOutputCapture stderrCapture)
    {
        await stdoutCapture.AppendAsync(ReadOnlyMemory<char>.Empty, CancellationToken.None).ConfigureAwait(false);
        await stderrCapture.AppendAsync(ReadOnlyMemory<char>.Empty, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<bool> TrySendFrameAsync(SandboxRunnerConnection connection, SemaphoreSlim     writerGate,
                                               Func<Stream, Task>      writeFrame, CancellationToken cancellationToken)
    {
        try
        {
            await writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        try
        {
            await writeFrame(connection.Downstream).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException
                                      or TimeoutException or OperationCanceledException)
        {
            // OperationCanceledException here is the terminate frame's bounded
            // write deadline (TerminateWriteTimeout) firing, not user
            // cancellation — treat it as any other send failure. The
            // spawn_request path passes the user token, and ExecuteAsync
            // re-raises user cancellation via ThrowIfCancellationRequested,
            // so cancellation semantics stay intact.
            return false;
        }
        finally
        {
            writerGate.Release();
        }
    }

    private Task<bool> TrySendTerminateAsync(SandboxRunnerConnection connection, SemaphoreSlim writerGate)
    {
        using var writeCancellation = new CancellationTokenSource(_limits.TerminateWriteTimeout);
        return TrySendFrameAsync(connection, writerGate,
                                 stream => RunnerFrameCodec.WriteControlFrameAsync(stream, "terminate",
                                          writeCancellation.Token),
                                 CancellationToken.None);
    }

    /// <summary>
    ///     一次 runner 会话的读循环：spawn_ready/error 完成 HandshakeTask，exit 完成 ExitTask；
    ///     output 帧经 base64 解码与跨帧 UTF-8 增量解码推入捕获器。循环不被用户 token 取消，
    ///     只随流关闭/Dispose 结束。
    ///     The read loop of one runner session: spawn_ready/error complete the
    ///     HandshakeTask and exit completes the ExitTask; output frames are
    ///     base64-decoded and incrementally UTF-8-decoded across frames into the
    ///     captures. The loop is never cancelled by user tokens — it ends with
    ///     the stream closing or Dispose.
    /// </summary>
    private sealed class RunnerSession : IDisposable
    {
        private readonly CancellationTokenSource _disposeSource = new();

        private readonly TaskCompletionSource<RunnerExitOutcome> _exit =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource<RunnerHandshake> _handshake =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly IProcessOutputCapture _stderrCapture;
        private readonly Decoder               _stderrDecoder = Encoding.UTF8.GetDecoder();
        private readonly IProcessOutputCapture _stdoutCapture;
        private readonly Decoder               _stdoutDecoder = Encoding.UTF8.GetDecoder();
        private          bool                  _exitCompleted;
        private          bool                  _handshakeCompleted;

        private RunnerSession(IProcessOutputCapture stdoutCapture, IProcessOutputCapture stderrCapture)
        {
            _stdoutCapture = stdoutCapture;
            _stderrCapture = stderrCapture;
        }

        public Task<RunnerHandshake> HandshakeTask => _handshake.Task;

        public Task<RunnerExitOutcome> ExitTask => _exit.Task;

        /// <summary>
        ///     读循环任务；未启动时为 CompletedTask。读循环内部捕获全部异常，不会进入 Faulted。
        ///     The read-loop task; CompletedTask before start. The loop catches
        ///     every exception internally and never faults.
        /// </summary>
        public Task ReadTask { get; private set; } = Task.CompletedTask;

        public void Dispose()
        {
            _disposeSource.Cancel();
        }

        public static RunnerSession Start(SandboxRunnerConnection connection, IProcessOutputCapture stdoutCapture,
                                          IProcessOutputCapture   stderrCapture)
        {
            var session = new RunnerSession(stdoutCapture, stderrCapture);
            session.ReadTask = session.ReadFramesAsync(connection.Upstream);
            return session;
        }

        /// <summary>
        ///     冲刷两条流残留的 UTF-8 解码状态（flush 收尾），剩余字符（含替换符）推入对应捕获器；
        ///     仅在读循环退出后调用一次。
        ///     Flushes the residual UTF-8 decoder state of both streams (final
        ///     flush), pushing any remaining characters (including replacement
        ///     characters) into the matching captures; call once, after the read
        ///     loop has exited.
        /// </summary>
        public async Task FlushDecodersAsync()
        {
            await FlushDecoderAsync(_stdoutDecoder, _stdoutCapture).ConfigureAwait(false);
            await FlushDecoderAsync(_stderrDecoder, _stderrCapture).ConfigureAwait(false);
        }

        private static async Task FlushDecoderAsync(Decoder decoder, IProcessOutputCapture capture)
        {
            // An empty final flush can only emit the replacement character for
            // a trailing incomplete sequence.
            var characters = new char[Encoding.UTF8.GetMaxCharCount(0)];
            var decoded    = decoder.GetChars(ReadOnlySpan<byte>.Empty, characters, true);
            if (decoded > 0)
                await capture.AppendAsync(characters.AsMemory(0, decoded), CancellationToken.None)
                             .ConfigureAwait(false);
        }

        private async Task ReadFramesAsync(Stream upstream)
        {
            try
            {
                while (true)
                {
                    var frame = await RunnerFrameCodec.ReadFrameAsync(upstream, _disposeSource.Token)
                                                      .ConfigureAwait(false);
                    if (frame is null)
                    {
                        CompleteEof();
                        return;
                    }

                    switch (frame.Type)
                    {
                        case "spawn_ready" :
                            if (_handshakeCompleted)
                                throw new InvalidDataException("The runner sent a duplicate spawn_ready frame.");

                            var ready = RunnerFrameCodec.ParsePayload<RunnerSpawnReady>(frame);
                            CompleteHandshake(new RunnerHandshake(ready.ProcessId, null));
                            break;
                        case "output" :
                            if (!_handshakeCompleted)
                                throw new InvalidDataException("The runner sent output before spawn_ready.");

                            await PushOutputAsync(RunnerFrameCodec.ParsePayload<RunnerOutputPayload>(frame))
                               .ConfigureAwait(false);
                            break;
                        case "error" :
                            if (_handshakeCompleted)
                                throw new InvalidDataException("The runner sent an error frame after spawn_ready.");

                            var error = RunnerFrameCodec.ParsePayload<RunnerErrorPayload>(frame);
                            CompleteHandshake(new RunnerHandshake(null, error));
                            break;
                        case "exit" :
                            if (!_handshakeCompleted)
                                throw new InvalidDataException("The runner sent exit before spawn_ready.");

                            var exit = RunnerFrameCodec.ParsePayload<RunnerExitPayload>(frame);
                            if (_exitCompleted)
                                throw new InvalidDataException("The runner sent a duplicate exit frame.");

                            _exitCompleted = true;
                            _exit.TrySetResult(new RunnerExitOutcome(exit.ExitCode, exit.TimedOut, null));
                            return;
                        default :
                            throw new InvalidDataException($"The runner sent an unknown frame type '{frame.Type}'.");
                    }
                }
            }
            catch (Exception ex)
            {
                FailSession(ex);
            }
        }

        private void CompleteHandshake(RunnerHandshake handshake)
        {
            _handshakeCompleted = true;
            _handshake.TrySetResult(handshake);
        }

        private void CompleteEof()
        {
            if (!_handshakeCompleted)
            {
                _handshake.TrySetException(new IOException(
                                                           "The runner closed the pipe before spawn_ready; no command was started."));
            }
            else if (!_exitCompleted)
            {
                _exitCompleted = true;
                _exit.TrySetResult(new RunnerExitOutcome(
                                                         null, false,
                                                         "The runner pipe closed before the exit frame; no command exit code is available."));
            }
        }

        private void FailSession(Exception ex)
        {
            if (!_handshakeCompleted)
            {
                _handshake.TrySetException(new IOException($"The runner session failed: {ex.Message}", ex));
            }
            else if (!_exitCompleted)
            {
                _exitCompleted = true;
                _exit.TrySetResult(new RunnerExitOutcome(
                                                         null, false,
                                                         $"The runner session ended with a protocol failure: {ex.Message}"));
            }
        }

        private async Task PushOutputAsync(RunnerOutputPayload output)
        {
            if (output.Stream is not ("stdout" or "stderr"))
                throw new InvalidDataException($"The runner sent output for an unknown stream '{output.Stream}'.");

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(output.DataBase64);
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException($"The runner sent malformed base64 output: {ex.Message}", ex);
            }

            if (bytes.Length == 0) return;

            var decoder    = output.Stream == "stderr" ? _stderrDecoder : _stdoutDecoder;
            var capture    = output.Stream == "stderr" ? _stderrCapture : _stdoutCapture;
            var characters = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
            var decoded    = decoder.GetChars(bytes, characters, false);
            if (decoded > 0)
                await capture.AppendAsync(characters.AsMemory(0, decoded), CancellationToken.None)
                             .ConfigureAwait(false);
        }
    }
}

/// <summary>
///     runner 握手结果：spawn_ready（进程 PID）或 error 帧（启动阶段失败）。
///     The runner handshake result: spawn_ready (with the process PID) or an
///     error frame (a launch-stage failure).
/// </summary>
internal sealed record RunnerHandshake(uint? ProcessId, RunnerErrorPayload? Error);

/// <summary>
///     会话终态：exit 帧（退出码 + 超时标志）或带 FailureMessage 的协议终态（无退出码）。
///     The session end state: an exit frame (exit code plus timeout flag) or a
///     protocol end with a FailureMessage and no exit code.
/// </summary>
internal sealed record RunnerExitOutcome(int? ExitCode, bool TimedOut, string? FailureMessage);
