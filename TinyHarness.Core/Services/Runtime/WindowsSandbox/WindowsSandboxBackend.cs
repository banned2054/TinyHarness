using System.ComponentModel;
using System.Security.Principal;
using System.Text;
using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     <para>定义沙箱后端各执行与清理阶段的独立期限。</para>
///     <para>Defines independent deadlines for sandbox execution and cleanup stages.</para>
/// </summary>
/// <remarks>
///     <para>
///         涵盖 refresh、spawn_ready、命令通信宽限、终止排空与 runner 回收。
///         取消后的清理不复用已取消的 token。
///     </para>
///     <para>
///         Covers refresh, spawn_ready, command communication grace, termination drain,
///         and runner reclamation. Cancellation cleanup never reuses an already-cancelled token.
///     </para>
/// </remarks>
public sealed record WindowsSandboxBackendLimits
{
    public static readonly WindowsSandboxBackendLimits Default = new();

    public TimeSpan RefreshTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan SpawnReadyTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    ///     <para>命令超时后等待 runner 终态的额外宽限；命令超时由 runner 自行判定。</para>
    ///     <para>
    ///         Extra grace for the runner's verdict after the command timeout;
    ///         the runner enforces the command timeout itself.
    ///     </para>
    /// </summary>
    public TimeSpan CommunicationGrace { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan TerminationDrainTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan RunnerExitTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan TerminationKillGrace { get; init; } = TimeSpan.FromSeconds(5);

    internal TimeSpan TerminateWriteTimeout { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
///     <para>通过 Windows 沙箱 runner 的 v6 帧协议执行已准备的进程计划，失败时不回退宿主。</para>
///     <para>
///         Executes prepared process plans through the Windows sandbox runner's v6 frame protocol,
///         without falling back to the host on failure.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         启动前检查状态、刷新 ACL、加载凭据与 capability SID，并创建私有桌面。
///         退出码仅来自 exit 帧，超时标志独立保留；缺失终态不补造退出码。
///         运行期协议失败或回收无法确认时保留执行不确定性，停止后续工具。
///     </para>
///     <para>
///         Before launch, checks readiness, refreshes ACLs, loads credentials and capability SIDs,
///         and creates a private desktop. Exit codes come only from exit frames;
///         timeout flags remain independent and missing verdicts never fabricate exit codes.
///         Runtime protocol failures or unconfirmed reclamation preserve execution uncertainty and stop subsequent tools.
///     </para>
/// </remarks>
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

    private readonly Func<SandboxSetupPayload, CancellationToken, Task> _validateWriteAcls;

    public WindowsSandboxBackend(
        WindowsSandboxComponents                            components,
        SandboxIsolationPolicy                              policy,
        IReadOnlyDictionary<string, string>                 targetEnvironment,
        IReadOnlyDictionary<string, string>?                knownSecrets          = null,
        ISandboxSetupInvoker?                               setupInvoker          = null,
        ISandboxCredentialSource?                           credentialSource      = null,
        SandboxCapabilitySidStore?                          capabilitySidStore    = null,
        ISandboxDesktopFactory?                             desktopFactory        = null,
        ISandboxRunnerLauncher?                             runnerLauncher        = null,
        WindowsSandboxBackendLimits?                        limits                = null,
        Action<SecurityIdentifier>?                         allowNullDeviceAccess = null,
        Func<SandboxSetupPayload, CancellationToken, Task>? validateWriteAcls     = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The Windows sandbox backend is available on Windows only.");

        ArgumentNullException.ThrowIfNull(targetEnvironment);

        _components = components;
        _policy     = policy;
        // 与策略临时根共用可信配置构建的冻结环境，避免 ACL 与实际进程环境漂移。
        _targetEnvironment  = targetEnvironment;
        _knownSecrets       = knownSecrets ?? new Dictionary<string, string>(StringComparer.Ordinal);
        _stateInspector     = new WindowsSandboxStateInspector(components);
        _setupInvoker       = setupInvoker       ?? new ProcessSandboxSetupInvoker(components, limits?.RefreshTimeout);
        _credentialSource   = credentialSource   ?? new SandboxCredentialReader(components);
        _capabilitySidStore = capabilitySidStore ?? new SandboxCapabilitySidStore(components);
        _desktopFactory     = desktopFactory     ?? new WindowsSandboxDesktopFactory();
        _runnerLauncher     = runnerLauncher     ?? new WindowsSandboxRunnerLauncher();
        _limits             = limits             ?? WindowsSandboxBackendLimits.Default;
        // NUL 设备 ACE 是机器级共享状态；默认测试注入空操作，避免修改宿主 ACL。
        _allowNullDeviceAccess = allowNullDeviceAccess ?? SandboxNullDeviceAcl.AllowCapabilitySid;
        _validateWriteAcls     = validateWriteAcls     ?? WorkspaceAclInheritance.ValidateAsync;
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
        var       capabilitySids = await ResolveCapabilitySids(execution, cancellationToken).ConfigureAwait(false);
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

        var payload = _policy.CreateSetupPayload(_components, execution.WorkingDirectory, Environment.UserName, true);
        try
        {
            await _setupInvoker
                 .RefreshAsync(payload, cancellationToken).ConfigureAwait(false);
            await _validateWriteAcls(payload, cancellationToken).ConfigureAwait(false);
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

    private async Task<IReadOnlyList<string>> ResolveCapabilitySids(
        PreparedProcessExecution execution, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The Windows sandbox backend is available on Windows only.");

        try
        {
            // cwd 必须与 refresh 的 command_cwd 一致，使 spawn token 的工作区 SID 匹配目录 ACE。
            // 此约束与 Rust 的 workspace_write_cap_sid_for_root 保持一致。
            var capabilitySids = await _capabilitySidStore
                                      .ResolveForPolicy(_policy, execution.WorkingDirectory, cancellationToken)
                                      .ConfigureAwait(false);
            // 使用首个 capability SID 维持库的 NUL 访问契约；runner 还会为子进程设置。
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
    ///     <para>从冻结目标环境构造 spawn 字典，并移除已知秘密对应的环境变量。</para>
    ///     <para>
    ///         Builds the spawn dictionary from the frozen target environment
    ///         and removes environment variables associated with known secrets.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         wire 模型要求可变 Dictionary；原环境已由可信 allowlist 构建，
    ///         再次移除秘密键用于防止键复用，不替代前置清理。
    ///     </para>
    ///     <para>
    ///         The wire model requires a mutable Dictionary; the source environment already uses a trusted allowlist.
    ///         Removing secret keys again guards against key reuse and does not replace earlier cleanup.
    ///     </para>
    /// </remarks>
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
                ToolExecutionUncertainException($"The sandbox runner did not exit within {(int)_limits.RunnerExitTimeout.TotalSeconds}s " +
                                                "after reporting the command exit and could not be reclaimed.");

        await AwaitReadLoopAsync(session).ConfigureAwait(false);
        await session.FlushDecodersAsync().ConfigureAwait(false);
        await CompleteCapturesAsync(stdoutCapture, stderrCapture).ConfigureAwait(false);

        if (outcome.FailureMessage is { } failure)
            throw new ToolExecutionUncertainException(failure);

        return new ProcessExecutionResult(outcome.ExitCode, outcome.TimedOut, null);
    }

    /// <summary>
    ///     <para>终止超时后无响应的 runner，限时回收并保留已捕获输出。</para>
    ///     <para>
    ///         Terminates an unresponsive runner after timeout,
    ///         reclaims it within deadlines, and retains captured output.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         先发送 terminate 并等待终态，超时后强制杀除。
    ///         后端超时标志始终为 true；缺少 exit 帧时退出码为 null，不补造结果。
    ///     </para>
    ///     <para>
    ///         Sends terminate and waits for a verdict before forcing a kill on deadline expiry.
    ///         The backend timeout flag remains true; without an exit frame the exit code is null, never fabricated.
    ///     </para>
    /// </remarks>
    /// <exception cref="ToolExecutionUncertainException">
    ///     <para>无法确认 runner 已回收。</para>
    ///     <para>Runner reclamation cannot be confirmed.</para>
    /// </exception>
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
                ToolExecutionUncertainException($"The sandbox runner did not exit within {(int)_limits.RunnerExitTimeout.TotalSeconds}s " +
                                                "after the command timeout and could not be reclaimed.");

        await AwaitReadLoopAsync(session).ConfigureAwait(false);
        await session.FlushDecodersAsync().ConfigureAwait(false);
        await CompleteCapturesAsync(stdoutCapture, stderrCapture).ConfigureAwait(false);

        // 后端已强制超时；即使 runner 未标超时，被终止的命令也不是自然完成。
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
        // 清理使用独立期限，不能被用户已取消的 token 跳过。
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
                ToolExecutionUncertainException($"The sandbox runner did not exit within {(int)_limits.RunnerExitTimeout.TotalSeconds}s " +
                                                "after cancellation and could not be reclaimed.");

        await AwaitReadLoopAsync(session).ConfigureAwait(false);
        stdoutCapture.Discard();
        stderrCapture.Discard();
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("Unreachable: cancellation must have thrown above.");
    }

    /// <summary>
    ///     <para>限时等待 runner 自然退出，超时后终止并等待杀除宽限。</para>
    ///     <para>
    ///         Waits for the runner to exit within its deadline,
    ///         then terminates it on timeout and waits through the kill grace.
    ///     </para>
    /// </summary>
    /// <returns>
    ///     <para>仅在等待确认退出后返回 true；终止时的退出竞态由后续等待确认。</para>
    ///     <para>
    ///         True only when a wait confirms exit;
    ///         an exit race during termination is resolved by the subsequent wait.
    ///     </para>
    /// </returns>
    private async Task<bool> ReclaimRunnerAsync(SandboxRunnerConnection connection)
    {
        if (await connection.WaitForExitAsync(_limits.RunnerExitTimeout).ConfigureAwait(false)) return true;

        try
        {
            connection.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // 可能与自然退出竞态；后续等待仍须确认退出。
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
            // 可能已经退出，但仍须由后续等待确认。
        }

        if (!await connection.WaitForExitAsync(_limits.TerminationKillGrace).ConfigureAwait(false))
            throw new
                ToolExecutionUncertainException($"Terminating the unresponsive sandbox runner did not complete within {(int)_limits.TerminationKillGrace.TotalSeconds}s.");
    }

    /// <summary>
    ///     <para>在收尾操作捕获器前限时等待会话读循环，不以等待异常覆盖原结束语义。</para>
    ///     <para>
    ///         Waits a bounded time for the session read loop before capture teardown,
    ///         without replacing the original termination semantics with wait failures.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         读循环内部将异常转换为会话状态，不会 fault。
    ///         正常等待结束后不再有并发 Append；超时不证明循环已退出，仍继续兜底收尾。
    ///     </para>
    ///     <para>
    ///         The read loop converts exceptions to session state and never faults.
    ///         Normal completion ends concurrent appends; a timeout does not confirm loop exit,
    ///         but fallback teardown still proceeds.
    ///     </para>
    /// </remarks>
    private async Task AwaitReadLoopAsync(RunnerSession session)
    {
        try
        {
            await session.ReadTask.WaitAsync(_limits.TerminationDrainTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // 未确认读循环退出也继续兜底收尾，不覆盖原结束语义。
        }
        catch (Exception)
        {
            // ReadFramesAsync 内部处理异常；防御性等待失败不得改变收尾结果。
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
            // terminate 写入期限取消按发送失败处理；spawn_request 路径由 ExecuteAsync 重新抛出用户取消。
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
    ///     <para>读取一次 runner 会话的握手、输出与终态帧，并维护对应任务状态。</para>
    ///     <para>
    ///         Reads handshake, output, and terminal frames for one runner session
    ///         and maintains the corresponding task states.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         output 经 base64 与跨帧 UTF-8 增量解码送入捕获器。
    ///         循环不受用户 token 取消，以便排空终态；exit、流关闭或 Dispose 结束读取。
    ///         握手前失败使握手任务失败，握手后缺失 exit 或协议损坏记录无退出码的失败终态。
    ///     </para>
    ///     <para>
    ///         Output is base64-decoded and incrementally UTF-8-decoded across frames into captures.
    ///         User cancellation does not cancel the loop, allowing terminal draining;
    ///         exit, stream closure, or Dispose ends reading.
    ///         Pre-handshake failures fault the handshake task;
    ///         missing exit or protocol damage after handshake records a failed end state without an exit code.
    ///     </para>
    /// </remarks>
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
        ///     <para>读循环任务；未启动时已完成，循环异常转为会话状态而不使此任务 fault。</para>
        ///     <para>
        ///         The read-loop task, completed before startup;
        ///         loop exceptions become session state instead of faulting this task.
        ///     </para>
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
        ///     <para>冲刷两条流的 UTF-8 解码尾部，将剩余字符送入对应捕获器。</para>
        ///     <para>
        ///         Flushes the UTF-8 decoder tails of both streams
        ///         into their corresponding captures.
        ///     </para>
        /// </summary>
        /// <remarks>
        ///     <para>仅在读循环退出后调用一次；不完整字符序列可能产生替换符。</para>
        ///     <para>
        ///         Call once after the read loop exits;
        ///         incomplete character sequences may produce replacement characters.
        ///     </para>
        /// </remarks>
        public async Task FlushDecodersAsync()
        {
            await FlushDecoderAsync(_stdoutDecoder, _stdoutCapture).ConfigureAwait(false);
            await FlushDecoderAsync(_stderrDecoder, _stderrCapture).ConfigureAwait(false);
        }

        private static async Task FlushDecoderAsync(Decoder decoder, IProcessOutputCapture capture)
        {
            // 空输入的最终 flush 只可能为尾部不完整序列产生替换符。
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
                _handshake.TrySetException(new
                                               IOException("The runner closed the pipe before spawn_ready; no command was started."));
            }
            else if (!_exitCompleted)
            {
                _exitCompleted = true;
                _exit.TrySetResult(new RunnerExitOutcome(null, false,
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
                _exit.TrySetResult(new RunnerExitOutcome(null, false,
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
///     <para>runner 握手结果：spawn_ready 的进程 PID 或 error 的启动失败信息。</para>
///     <para>
///         The runner handshake result: a process PID from spawn_ready
///         or launch-failure details from error.
///     </para>
/// </summary>
internal sealed record RunnerHandshake(uint? ProcessId, RunnerErrorPayload? Error);

/// <summary>
///     <para>会话终态：exit 帧的退出码与超时标志，或无退出码的协议失败信息。</para>
///     <para>
///         The session end state: exit-frame code and timeout flag,
///         or protocol-failure details without an exit code.
///     </para>
/// </summary>
internal sealed record RunnerExitOutcome(int? ExitCode, bool TimedOut, string? FailureMessage);
