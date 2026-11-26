using System.ComponentModel;
using System.Diagnostics;
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

    /// <summary>
    ///     <para>
    ///         拒读刷新门的限时获取期限；默认须覆盖另一端最坏 refresh 时长（调用器默认 60s 超时 +
    ///         余量），超时按命令未启动 fail-closed。
    ///     </para>
    ///     <para>
    ///         The bounded acquisition deadline of the deny-read refresh gate; the default must cover
    ///         the other side's worst refresh duration (the invoker's default 60s timeout plus
    ///         margin), and a timeout fails closed with the command not started.
    ///     </para>
    /// </summary>
    public TimeSpan DenyReadRefreshGateTimeout { get; init; } = TimeSpan.FromSeconds(90);

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
///         启动前在按 home 键控的拒读刷新门内检查状态（生效在线时另查在线账户就绪；home 缺失走
///         无门预检路径保持 "not ready" 语义）并完成 [读簿记并集 → ACL 刷新]（门等待超时按命令
///         未启动 fail-closed）、加载凭据，并在同一键控门的再次限时获取内解析 capability SID
///         （get-or-create 与整表写回同另一端解析及门内 helper 的表写互斥；门等待超时按命令未启动
///         fail-closed），再创建私有桌面；spawn 网络与代理环境按生效网络填写，账户按生效网络选择。
///         生效在线时以 LOGON_WITH_PROFILE 加载在线账户 profile，使登录会话具备 schannel
///         客户端凭据；offline 不加载 profile。
///         退出码仅来自 exit 帧，超时标志独立保留；缺失终态不补造退出码。
///         运行期协议失败或回收无法确认时保留执行不确定性，停止后续工具。
///     </para>
///     <para>
///         Before launch, the backend checks readiness inside the home-keyed deny-read refresh gate
///         (plus online-account readiness whenever the effective network is online; a missing home
///         takes the ungated pre-check path keeping the "not ready" semantics) and completes
///         [bookkeeping union → ACL refresh] there (a gate-wait timeout fails closed with the command
///         not started), loads credentials, and resolves capability SIDs behind a second bounded
///         acquisition of the same keyed gate (the get-or-create plus whole-table write-back is
///         mutually exclusive with the other side's resolution and the helper's in-gate table
///         writes; a gate-wait timeout fails closed with the command not started), then creates a
///         private desktop; the spawn network and proxy environment follow the effective network,
///         which also selects the launch account. While online, the launch
///         loads the online account profile via LOGON_WITH_PROFILE so the logon session gains
///         schannel client credentials; offline launches load no profile.
///         Exit codes come only from exit frames;
///         timeout flags remain independent and missing verdicts never fabricate exit codes.
///         Runtime protocol failures or unconfirmed reclamation preserve execution uncertainty and stop subsequent tools.
///     </para>
/// </remarks>
public sealed class WindowsSandboxBackend : IProcessExecutionBackend
{
    private const ulong MaximumRunnerTimeoutMilliseconds = 4_294_967_294;

    private readonly Action<SecurityIdentifier> _allowNullDeviceAccess;
    private readonly SandboxCapabilitySidStore  _capabilitySidStore;

    private readonly WindowsSandboxComponents                            _components;
    private readonly ISandboxCredentialSource                            _credentialSource;
    private readonly ISandboxDenyReadRefreshGate                         _denyReadRefreshGate;
    private readonly ISandboxDesktopFactory                              _desktopFactory;
    private readonly Func<CancellationToken, Task<WindowsSandboxStatus>> _inspectOnlineAccount;
    private readonly IReadOnlyDictionary<string, string>                 _knownSecrets;
    private readonly WindowsSandboxBackendLimits                         _limits;
    private readonly SandboxIsolationPolicy                              _policy;
    private readonly IReadOnlyDictionary<string, string>                 _proxyEnvironment;
    private readonly ISandboxRunnerLauncher                              _runnerLauncher;
    private readonly ISandboxSetupInvoker                                _setupInvoker;
    private readonly bool                                                _standingNetworkAccessEnabled;
    private readonly WindowsSandboxStateInspector                        _stateInspector;
    private readonly IReadOnlyDictionary<string, string>                 _targetEnvironment;

    private readonly Func<SandboxSetupPayload, CancellationToken, Task> _validateWriteAcls;

    private readonly Func<SandboxSetupPayload, CancellationToken, Task> _validateReadRootReadiness;

    public WindowsSandboxBackend(
        WindowsSandboxComponents                            components,
        SandboxIsolationPolicy                              policy,
        IReadOnlyDictionary<string, string>                 targetEnvironment,
        IReadOnlyDictionary<string, string>?                knownSecrets                 = null,
        ISandboxSetupInvoker?                               setupInvoker                 = null,
        ISandboxCredentialSource?                           credentialSource             = null,
        SandboxCapabilitySidStore?                          capabilitySidStore           = null,
        ISandboxDesktopFactory?                             desktopFactory               = null,
        ISandboxRunnerLauncher?                             runnerLauncher               = null,
        WindowsSandboxBackendLimits?                        limits                       = null,
        Action<SecurityIdentifier>?                         allowNullDeviceAccess        = null,
        Func<SandboxSetupPayload, CancellationToken, Task>? validateWriteAcls            = null,
        Func<SandboxSetupPayload, CancellationToken, Task>? validateReadRootReadiness    = null,
        bool                                                standingNetworkAccessEnabled = false,
        IReadOnlyDictionary<string, string>?                proxyEnvironment             = null,
        ISandboxDenyReadRefreshGate?                        denyReadRefreshGate          = null)
        : this(components, policy, targetEnvironment, knownSecrets, setupInvoker, credentialSource,
               capabilitySidStore, desktopFactory, runnerLauncher, limits, allowNullDeviceAccess,
               validateWriteAcls, validateReadRootReadiness, standingNetworkAccessEnabled, proxyEnvironment,
               null, denyReadRefreshGate)
    {
    }

    /// <summary>
    ///     <para>创建沙箱后端，并允许测试替换在线账户就绪检查。</para>
    ///     <para>Creates the sandbox backend and allows tests to replace the online-account readiness check.</para>
    /// </summary>
    internal WindowsSandboxBackend(
        WindowsSandboxComponents                             components,
        SandboxIsolationPolicy                               policy,
        IReadOnlyDictionary<string, string>                  targetEnvironment,
        IReadOnlyDictionary<string, string>?                 knownSecrets,
        ISandboxSetupInvoker?                                setupInvoker,
        ISandboxCredentialSource?                            credentialSource,
        SandboxCapabilitySidStore?                           capabilitySidStore,
        ISandboxDesktopFactory?                              desktopFactory,
        ISandboxRunnerLauncher?                              runnerLauncher,
        WindowsSandboxBackendLimits?                         limits,
        Action<SecurityIdentifier>?                          allowNullDeviceAccess,
        Func<SandboxSetupPayload, CancellationToken, Task>?  validateWriteAcls,
        Func<SandboxSetupPayload, CancellationToken, Task>?  validateReadRootReadiness,
        bool                                                 standingNetworkAccessEnabled,
        IReadOnlyDictionary<string, string>?                 proxyEnvironment,
        Func<CancellationToken, Task<WindowsSandboxStatus>>? inspectOnlineAccount,
        ISandboxDenyReadRefreshGate?                         denyReadRefreshGate)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The Windows sandbox backend is available on Windows only.");

        ArgumentNullException.ThrowIfNull(targetEnvironment);

        _components = components;
        _policy = policy;
        // 与策略临时根共用可信配置构建的冻结环境，避免 ACL 与实际进程环境漂移。
        _targetEnvironment = targetEnvironment;
        _knownSecrets = knownSecrets ?? new Dictionary<string, string>(StringComparer.Ordinal);
        _standingNetworkAccessEnabled = standingNetworkAccessEnabled;
        _proxyEnvironment = proxyEnvironment ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _stateInspector = new WindowsSandboxStateInspector(components);
        _inspectOnlineAccount = inspectOnlineAccount ?? _stateInspector.InspectOnlineAccountAsync;
        _setupInvoker = setupInvoker ?? new ProcessSandboxSetupInvoker(components, limits?.RefreshTimeout);
        // 门按 home 键控串行化 [读簿记→合并→refresh→持久化]；不可用时拒绝本次 refresh（fail-closed）。
        _denyReadRefreshGate = denyReadRefreshGate ?? new GlobalDenyReadRefreshGate();
        _credentialSource = credentialSource ?? new SandboxCredentialReader(components);
        _capabilitySidStore = capabilitySidStore ?? new SandboxCapabilitySidStore(components);
        _desktopFactory = desktopFactory ?? new WindowsSandboxDesktopFactory();
        _runnerLauncher = runnerLauncher ?? new WindowsSandboxRunnerLauncher();
        _limits = limits ?? WindowsSandboxBackendLimits.Default;
        // NUL 设备 ACE 是机器级共享状态；默认测试注入空操作，避免修改宿主 ACL。
        _allowNullDeviceAccess = allowNullDeviceAccess ?? SandboxNullDeviceAcl.AllowCapabilitySid;
        _validateWriteAcls = validateWriteAcls ?? WorkspaceAclInheritance.ValidateAsync;
        // 读根就绪门与写校验同形注入：生产默认走真实 DACL 就绪判定，测试可整体替换。
        _validateReadRootReadiness = validateReadRootReadiness ?? SandboxReadRootReadiness.ValidateAsync;
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

        // 生效网络 = 常设在线策略（构造传入或冻结策略携带）或该命令的逐次审批授予；
        // 都不满足时保持离线，绝不降级或回退宿主。
        var networkAccessEnabled = _standingNetworkAccessEnabled || _policy.NetworkEnabled ||
                                   execution.NetworkAccessEnabled;

        // TCP 环回模式使用独立账户对（TcpOff/TcpOn）与独立 home，其凭据文件的 online 条目不具备
        // 外联能力；生效在线（无论来自常设策略还是逐次授予）都会以在线账户启动并呈现误导性网络
        // 身份。与组合器的常设互斥守卫语义一致，对逐次授予路径同样 fail closed。这是启动期校验，
        // 与同函数其他启动期拒绝一致抛 ProcessExecutionStartException。
        // The TCP loopback mode runs on a separate TcpOff/TcpOn account pair whose credentials
        // carry no egress capability; any effective-online state (standing or per-command grant)
        // would launch under the online account with a misleading network identity. Matching the
        // composer's standing mutual-exclusion guard, per-command grants fail closed here too.
        // This is a pre-launch validation and throws ProcessExecutionStartException like the
        // other pre-launch refusals in this method.
        if (_components.AllowTcpLoopback && networkAccessEnabled)
            throw new
                ProcessExecutionStartException("The TCP loopback mode and online network access are mutually exclusive: " +
                                               "tcp-loopback runs on a separate TcpOff/TcpOn account pair with a separate home whose " +
                                               "credentials file online entry carries no egress capability, and per-command online grants " +
                                               "are not supported in this mode; disable settings.windowsSandbox.allowTcpLoopback or run without the network request.",
                                               new
                                                   InvalidOperationException("TCP loopback mode forbids effective online network access."));

        if (!Workspace.IsInside(_policy.WorkspaceRoot, execution.WorkingDirectory))
            throw new
                ProcessExecutionStartException($"The command working directory '{execution.WorkingDirectory}' is outside the sandbox workspace root '{_policy.WorkspaceRoot}'.",
                                               new
                                                   InvalidOperationException("Working directory escaped the frozen sandbox policy workspace root."));

        await PreflightAsync(execution, networkAccessEnabled, cancellationToken).ConfigureAwait(false);

        // 账户三态选择与 payload 构造共用同一份授权分流纯函数：获批读执行必须以专用授权账户
        // 启动 runner——其 token 的授权组 deny 面是“全量 deny − 获批路径”，获批读才真实可达；
        // 普通沙箱账户的全量 deny 会压制获批读，绝不回退。离线/在线语义映射到授权账户对
        // （AuthOff 携带离线防火墙面、AuthOn 仅在生效在线时使用），读授权不顺带开放网络。
        // 管道 ACL 与私有桌面按返回账户的 SID 构造，授权账户自动获得同等隔离形态。
        // The three-way account selection shares the same authorization-split pure function as
        // payload construction: an approved-read execution must launch the runner under the
        // dedicated authorization account — whose token's authorization-group deny face is
        // "full deny minus the approved paths", the only face under which the approved read is
        // actually reachable; the ordinary sandbox account's full deny would suppress it, so
        // there is no fallback. The offline/online semantics map onto the authorization pair
        // (AuthOff carries the offline firewall face, AuthOn is used only while effectively
        // online), so an approved read never opens the network along the way. The pipe ACL and
        // the private desktop are built from the returned account's SID, giving the authorization
        // account the same isolation shape automatically.
        var authorizedReads = SandboxAuthorizedReads.Split(_policy.DenyReadPaths,
                                                           execution.GrantScope?.ReadRoots ?? []);
        var account = await LoadAccount(networkAccessEnabled, authorizedReads.HasAuthorizedReads,
                                        cancellationToken).ConfigureAwait(false);
        var       capabilitySids = await ResolveCapabilitySids(execution, cancellationToken).ConfigureAwait(false);
        using var desktop        = CreateDesktop(account);

        var request = BuildSpawnRequest(execution, capabilitySids, desktop.Name,
                                        networkAccessEnabled);
        SandboxRunnerConnection connection;
        try
        {
            // 生效在线时加载在线账户 profile：登录会话借此获得 schannel 客户端凭据
            // （否则沙箱内 TLS 全部 SEC_E_NO_CREDENTIALS）；账户 profile 目录成为该
            // 独立账户的可写面属有意权衡。offline 保持不加载。
            connection = await _runnerLauncher
                              .LaunchAsync(new RunnerLaunchRequest(_components.RunnerExecutablePath, account,
                                                                   execution.WorkingDirectory, networkAccessEnabled),
                                           cancellationToken)
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

    private async Task PreflightAsync(
        PreparedProcessExecution execution, bool networkAccessEnabled, CancellationToken cancellationToken)
    {
        SandboxDiagnosticLog.Event("preflight",
                                   $"network={(networkAccessEnabled ? "enabled" : "restricted")} " +
                                   $"proxyVariables={(networkAccessEnabled ? _proxyEnvironment.Count : 0)}");
        // 就绪检查读 cap_sid，门外执行会撞上门内整表写回的中间态（共享冲突/半 JSON），把另一端
        // 正在进行的 refresh 误报成沙箱未就绪；因此就绪检查随 payload 构造与 refresh 一起移入门内
        // （纯读毫秒级）。home 缺失时不可能有同 home 的门内写者，而门键控对缺失目录必抛（最终路径
        // 解析失败），故先廉价预检走既有无门路径，保住 "not ready" 的失败语义。
        if (!Directory.Exists(_components.SandboxHome))
        {
            var missingHomeStatus = await _stateInspector.InspectAsync(cancellationToken).ConfigureAwait(false);
            SandboxDiagnosticLog.Event("readiness",
                                       $"blocked problems=[{string.Join(" | ", missingHomeStatus.Problems)}]");
            throw new ProcessExecutionStartException("The Windows sandbox is not ready; refusing to execute: " +
                                                     string.Join("; ", missingHomeStatus.Problems),
                                                     new
                                                         InvalidOperationException("Sandbox readiness check failed; failing closed without fallback."));
        }

        var stopwatch = Stopwatch.StartNew();
        // 诊断只输出本地 phase 与异常类型；ex.Message 可携带 setup_error.json 的任意 helper 文本，不属白名单。
        var phase     = "deny-read-gate";
        ISandboxDenyReadRefreshGateHandle? gate = null;
        try
        {
            // 门限时获取：超时抛启动期异常（inner=TimeoutException），命令保持未启动，绝不无门
            // refresh——否则后到的旧 payload 会撤销另一端刚同步的共享主体 deny。就绪检查与
            // payload 构造同入门内（见上），不改变任何检查语义。
            gate = await _denyReadRefreshGate
                         .TryAcquireAsync(_components.SandboxHome, _limits.DenyReadRefreshGateTimeout,
                                          cancellationToken).ConfigureAwait(false)
                   ?? throw new
                       ProcessExecutionStartException($"The deny-read refresh gate for sandbox home '{_components.SandboxHome}' " +
                                                      $"was not acquired within {(int)_limits.DenyReadRefreshGateTimeout.TotalSeconds}s; " +
                                                      "the command was not started, so no stale refresh can revoke deny entries " +
                                                      "another side just persisted. Retry the command once the concurrent refresh completes.",
                                                      new TimeoutException("The deny-read refresh gate acquisition timed out."));
            SandboxSetupPayload payload;
            try
            {
                phase = "inspect";
                var status = await _stateInspector.InspectAsync(cancellationToken).ConfigureAwait(false);
                if (!status.Ready)
                {
                    SandboxDiagnosticLog.Event("readiness", $"blocked problems=[{string.Join(" | ", status.Problems)}]");
                    throw new ProcessExecutionStartException("The Windows sandbox is not ready; refusing to execute: " +
                                                             string.Join("; ", status.Problems),
                                                             new
                                                                 InvalidOperationException("Sandbox readiness check failed; failing closed without fallback."));
                }

                if (networkAccessEnabled)
                {
                    var onlineStatus = await _inspectOnlineAccount(cancellationToken).ConfigureAwait(false);
                    if (!onlineStatus.Ready)
                    {
                        SandboxDiagnosticLog.Event("readiness",
                                                   $"online-blocked problems=[{string.Join(" | ", onlineStatus.Problems)}]");
                        throw new
                            ProcessExecutionStartException("The Windows sandbox online account is not ready; refusing network-enabled execution: " +
                                                           string.Join("; ", onlineStatus.Problems),
                                                           new
                                                               InvalidOperationException("Online sandbox readiness check failed; failing closed without fallback."));
                    }
                }

                // 每命令 refresh 的 grant 范围仍冻结于 Prepare（活动快照不在执行期查询）；deny-read 在
                // 冻结语义集之上叠加 helper 拒读簿记的只增并集——该并集是执行期唯一实时源，且只收窄
                // （只追加 deny，绝不授读、绝不撤销既有 deny）。[读簿记 → 合并 → refresh → 簿记持久化]
                // 运行在按 home 键控的跨进程拒读刷新门内：后到的 refresh 必然重新读到先到者已持久化的
                // 条目，撤销集不会收缩共享主体依赖的 deny；门等待本身 fail-closed（超时=命令未启动）。
                phase   = "payload";
                payload = _policy.CreateSetupPayload(_components, execution.WorkingDirectory, Environment.UserName, true,
                                                     execution.GrantScope);
                // 门内复核最终写根（含基线写根、临时写根与 grant 写根）不得覆盖共享 home 下后来登记的另一端组件或 C1 路径：
                // 本校验与组合期登记共用同一把按 home 键控的跨进程刷新门，消除跨端并发竞态。
                phase = "protected-paths-check";
                WindowsSandboxComposer.ValidateWriteRootsAgainstRegisteredProtectedPaths(payload.WriteRoots,
                                                                                         _components.SandboxHome,
                                                                                         "sandbox refresh write root");
#if DEBUG
                // run id 经 AsyncLocal 流入本轮 union/helper/validate 诊断事件；不新增参数，也不改变执行语义。
                SandboxDiagnosticLog.BeginRun(Guid.NewGuid().ToString("N")[..8]);
#endif
                // 门内重读簿记做只增并集：会话冻结期间另一端写入簿记的条目同样被并入，refresh
                // 不撤销共享主体依赖的既有 deny；簿记不可读经瞬态重试后 fail-closed，命令保持未启动。
                phase   = "deny-read-union";
                payload = ApplyDenyReadBookkeepingUnion(payload);
                phase   = "refresh";
                SandboxDiagnosticLog.Event("refresh",
                                           $"request workspace='{_policy.WorkspaceRoot}' cwd='{execution.WorkingDirectory}' " +
                                           $"writeRoots=[{string.Join(";", payload.WriteRoots)}] denyWriteCount={payload.DenyWritePaths.Count} " +
                                           $"denyReadCount={payload.DenyReadPaths.Count} version={payload.Version} refreshOnly={payload.RefreshOnly}");
#if DEBUG
                // 宿主 token 单独成行并显式标注 subject=host；不得与 helper 实际身份混淆。
                SandboxProcessDiagnostics.LogHostToken();
#endif
                await _setupInvoker
                     .RefreshAsync(payload, cancellationToken).ConfigureAwait(false);
                SandboxDiagnosticLog.Event("refresh", $"helper-finished elapsedMs={stopwatch.ElapsedMilliseconds}");
            }
            catch (Exception ex) when (SandboxRefreshExitUncertainty.TryGetConfirmation(ex, out var confirmation))
            {
                // helper 退出状态未知：门经 ReleaseWhenConfirmed 转入后台确认后释放（fail-closed），
                // 此处立即移交所有权并置空，finally 不再 Dispose；异常按原有语义继续上抛。
                SandboxDiagnosticLog.Event("refresh",
                                           $"helper-exit-uncertain type={ex.GetType().Name} elapsedMs={stopwatch.ElapsedMilliseconds}");
                gate.ReleaseWhenConfirmed(confirmation!);
                gate = null;
                throw;
            }
            finally
            {
                // RefreshAsync 的三种退出状态：返回或超时/取消路径确认退出即 helper 不再写簿记，
                // 此刻释放门安全；退出未确认（终止或确认等待失败）时门已移交 ReleaseWhenConfirmed，
                // 由后台确认任务在 helper 退出后才释放，此处不再 Dispose。读根就绪校验留在门外，
                // 不延长门的持有时间。
                gate?.Dispose();
            }

            stopwatch.Restart();
            phase = "validate";
            SandboxDiagnosticLog.Event("validate",
                                       $"start writeRoots=[{string.Join(";", payload.WriteRoots)}] readRootCount={payload.ReadRoots.Count}");
            await SandboxPostRefreshValidation
                 .ValidateAsync(payload, _validateWriteAcls, _validateReadRootReadiness, cancellationToken)
                 .ConfigureAwait(false);
            SandboxDiagnosticLog.Event("validate", $"passed elapsedMs={stopwatch.ElapsedMilliseconds}");
        }
        catch (OperationCanceledException ex)
        {
            SandboxDiagnosticLog.Event("preflight",
                                       $"phase={phase} cancelled type={ex.GetType().Name} elapsedMs={stopwatch.ElapsedMilliseconds}");
            throw;
        }
        catch (ProcessExecutionStartException ex)
        {
            // 读根就绪门已带启动期语义（阶段与未就绪读根在消息内）：保留原始异常，不再叠加 refresh 前缀。
            SandboxDiagnosticLog.Event("preflight",
                                       $"phase={phase} failed elapsedMs={stopwatch.ElapsedMilliseconds} type={ex.GetType().Name}");
            throw;
        }
        catch (Exception ex)
        {
            SandboxDiagnosticLog.Event("preflight",
                                       $"phase={phase} failed elapsedMs={stopwatch.ElapsedMilliseconds} type={ex.GetType().Name}");
            throw new ProcessExecutionStartException($"The sandbox ACL refresh failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    ///     <para>
    ///         在冻结语义 deny 集之上叠加 helper 拒读簿记的只增并集：语义集 ∪ 全部主体的可配对
    ///         簿记条目，去重后替换 payload 的 deny_read_paths。与首次准备共用
    ///         <see cref="SandboxDenyReadBookkeeping.Merge" /> 同一实现；只增不减——会话冻结期间
    ///         另一端写入簿记的条目同样被并入。调用方须持有按 home 键控的拒读刷新门，使
    ///         [读簿记 → 合并 → refresh → 持久化] 跨进程串行化。
    ///     </para>
    ///     <para>
    ///         Folds the helper deny-read bookkeeping into the frozen semantic deny set as an
    ///         add-only union: the semantic set plus every principal's pairable bookkeeping entries,
    ///         deduplicated into the payload's deny_read_paths. Shares
    ///         <see cref="SandboxDenyReadBookkeeping.Merge" /> with the first preparation; strictly
    ///         add-only — entries another side writes while this session is frozen are folded in too.
    ///         The caller must hold the home-keyed deny-read refresh gate so
    ///         [read → merge → refresh → persist] stays serialized across processes.
    ///     </para>
    /// </summary>
    private SandboxSetupPayload ApplyDenyReadBookkeepingUnion(SandboxSetupPayload payload)
    {
        var (denyPaths, bookkeepingEntries, bookkeepingMerged) = SandboxDenyReadBookkeeping.Merge(
            "per-command refresh", payload.DenyReadPaths, payload.ReadRoots, _components.SandboxHome);
        var unioned = SandboxIsolationPolicy.Deduplicate(denyPaths);
        SandboxDiagnosticLog.Event("deny-read-union",
                                   $"bookkeepingEntries={bookkeepingEntries} bookkeepingMerged={bookkeepingMerged} " +
                                   $"denyReadCount={unioned.Count}");
        return payload with { DenyReadPaths = unioned };
    }

    private async Task<WindowsSandboxAccount> LoadAccount(bool              networkAccessEnabled, bool authorizedReads,
                                                          CancellationToken cancellationToken)
    {
        try
        {
            // 生效在线时必须以在线账户启动 runner；任何失败都 fail closed，不降级离线账户。
            // 获批读执行切换到专用授权账户（离线↔AuthOff、在线↔AuthOn），凭据缺失或代际不足
            // 在凭据读取处明确失败，绝不回退普通沙箱账户。
            // While effectively online the runner must launch under the online account; every
            // failure fails closed without demoting to offline. An approved-read execution
            // switches to the dedicated authorization accounts (offline↔AuthOff, online↔AuthOn);
            // missing credentials or an insufficient generation fail explicitly at credential
            // loading and never fall back to the ordinary sandbox accounts.
            return await (authorizedReads
                    ? networkAccessEnabled
                        ? _credentialSource.LoadAuthorizedOnlineAccount(cancellationToken)
                        : _credentialSource.LoadAuthorizedOfflineAccount(cancellationToken)
                    : networkAccessEnabled
                        ? _credentialSource.LoadOnlineAccount(cancellationToken)
                        : _credentialSource.LoadOfflineAccount(cancellationToken))
               .ConfigureAwait(false);
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

    /// <summary>
    ///     <para>
    ///         在按 home 键控的拒读刷新门内解析 capability SID：get-or-create 与整表写回同另一端
    ///         host 解析、门内 helper refresh 的表写共用一张 <c>cap_sid</c>，无门并发会产生共享
    ///         冲突或丢更新（SID/ACE 失配）。仅解析这一小段持门，runner 启动与桌面创建不持门；
    ///         此处无 helper 参与，门经 Dispose 直接归还，门等待超时按命令未启动 fail-closed。
    ///     </para>
    ///     <para>
    ///         Resolves capability SIDs while holding the home-keyed deny-read refresh gate: the
    ///         get-or-create plus whole-table write-back share one cap_sid table with the other
    ///         host's resolution and the helper's in-gate refresh writes, so ungated concurrency
    ///         produces sharing violations or lost updates (SID/ACE mismatch). Only this short
    ///         resolution holds the gate — the runner launch and desktop creation never do; no
    ///         helper participates here, so Dispose returns the gate directly, and a gate-wait
    ///         timeout fails closed with the command not started.
    ///     </para>
    /// </summary>
    private async Task<IReadOnlyList<string>> ResolveCapabilitySids(
        PreparedProcessExecution execution, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The Windows sandbox backend is available on Windows only.");

        ISandboxDenyReadRefreshGateHandle? gate = null;
        try
        {
            // 门限时获取（语义对齐 preflight 的拒读门）：cap_sid 的 get-or-create + 整表写回绝不
            // 在门外进行，否则与另一端 host 解析及门内 helper 的表写并发时共享冲突或丢更新；
            // 超时即命令未启动，绝不无门写表。
            gate = await _denyReadRefreshGate
                         .TryAcquireAsync(_components.SandboxHome, _limits.DenyReadRefreshGateTimeout,
                                          cancellationToken).ConfigureAwait(false)
                   ?? throw new
                       ProcessExecutionStartException($"The deny-read refresh gate for sandbox home '{_components.SandboxHome}' " +
                                                      $"was not acquired within {(int)_limits.DenyReadRefreshGateTimeout.TotalSeconds}s " +
                                                      "before resolving capability SIDs; the command was not started, so the shared " +
                                                      "capability SID table is never written without the gate. Retry the command once " +
                                                      "the concurrent refresh completes.",
                                                      new TimeoutException("The deny-read refresh gate acquisition timed out."));
            // cwd 必须与 refresh 的 command_cwd 一致，使 spawn token 的工作区 SID 匹配目录 ACE。
            // 此约束与 Rust 的 workspace_write_cap_sid_for_root 保持一致。
            // grant 冻结写根一并并入 SID 解析：refresh payload 已含相同根（先 refresh 后解析，
            // 与基线写根共享 helper 写入的同一张 SID 表）。
            var capabilitySids = await _capabilitySidStore
                                      .ResolveForPolicy(_policy, execution.GrantScope?.WriteRoots,
                                                        execution.WorkingDirectory, cancellationToken)
                                      .ConfigureAwait(false);
            // 使用首个 capability SID 维持库的 NUL 访问契约；runner 还会为子进程设置。
            _allowNullDeviceAccess(new SecurityIdentifier(capabilitySids[0]));
            return capabilitySids;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ProcessExecutionStartException)
        {
            // 门限时获取的启动期拒绝原样上抛，不叠加解析失败前缀。
            throw;
        }
        catch (Exception ex)
        {
            throw new ProcessExecutionStartException($"Failed to resolve sandbox capability SIDs: {ex.Message}", ex);
        }
        finally
        {
            // 本段无 helper 参与（helper 的 cap 写只发生在门内 refresh），Dispose 直接归还；
            // 解析失败路径同样先释放，命令保持未启动。
            gate?.Dispose();
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
        PreparedProcessExecution execution, IReadOnlyList<string> capabilitySids, string desktopName,
        bool                     networkAccessEnabled)
    {
        var timeoutMilliseconds = (ulong)execution.TimeoutSeconds * 1000;
        if (timeoutMilliseconds > MaximumRunnerTimeoutMilliseconds)
            timeoutMilliseconds = MaximumRunnerTimeoutMilliseconds;

        return new RunnerSpawnRequest
        {
            Command          = [execution.Executable, .. execution.Arguments],
            WorkingDirectory = execution.WorkingDirectory,
            Environment      = BuildSpawnEnvironment(networkAccessEnabled),
            PermissionProfile = _policy.SpawnProfile with
            {
                Network = SandboxNetworkPolicyParser.ToProfileValue(networkAccessEnabled
                                                                        ? SandboxNetworkPolicy.Enabled
                                                                        : SandboxNetworkPolicy.Restricted)
            },
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
    ///     <para>
    ///         从冻结目标环境构造 spawn 字典；仅生效在线时合并代理环境，并最后移除已知秘密对应的
    ///         环境变量。"受限执行的 env 无代理键"指默认派生路径——代理键只在生效在线时由后端注入；
    ///         可信显式 extraEnvironment 是优先级更高的显式覆盖，可为受限子进程携带代理键（不推荐）。
    ///     </para>
    ///     <para>
    ///         Builds the spawn dictionary from the frozen target environment; the
    ///         proxy environment merges only while the effective network is online,
    ///         and known-secret keys are removed last. "A restricted execution env
    ///         never carries proxy keys" describes the default derived path — the
    ///         backend injects proxy keys only while online; the trusted explicit
    ///         extraEnvironment is a higher-priority explicit override and may carry
    ///         proxy keys for restricted child processes (not recommended).
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
    private Dictionary<string, string> BuildSpawnEnvironment(bool networkAccessEnabled)
    {
        var environment = new Dictionary<string, string>(_targetEnvironment, StringComparer.OrdinalIgnoreCase);
        if (networkAccessEnabled)
            foreach (var (name, value) in _proxyEnvironment)
                environment[name] = value;

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
