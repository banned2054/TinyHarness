using System.Text.Json;
using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     <para>
///         C2 获批读授权通道的派发前协调器：识别携带 authorized_read_paths 的授权执行并施加三道
///         门——禁用门（恢复失败/回收未知后的持久化禁用，含每实例一次的恢复尝试）、能力门（部署的
///         helper 必须声明 v7 授权代能力）与串行门（Global 互斥体限定的授权窗口），门内完成授权
///         refresh、执行与恢复 refresh（v7 空 authorized_read_paths 重同步授权组全量 deny），禁用
///         状态的恢复刷新同属授权 ACL 变更，亦持窗口执行。普通
///         执行原样透传，行为零变化；任何门失败都以明确错误拒绝该次授权执行，绝不降级或静默去除
///         授权路径后放行。
///     </para>
///     <para>
///         The pre-dispatch coordinator of the C2 approved-read authorization channel: it recognizes
///         authorized executions carrying authorized_read_paths and applies three gates — the disable
///         gate (persisted after a failed restore or unknown reclamation, with one recovery attempt per
///         instance), the capability gate (the deployed helper must declare the v7 authorized
///         generation), and the serialization gate (a Global-mutex-bounded authorization window). The
///         window covers the authorized refresh, the execution, and the restore refresh (a v7 payload
///         with empty authorized_read_paths resyncing the authorization group's full deny); the
///         disable-state recovery refresh, being the same kind of authorized-ACL mutation, also runs
///         inside the window. Normal
///         executions pass through untouched with zero behavior change; every gate failure refuses the
///         authorized execution with a distinct error instead of downgrading or silently dropping the
///         approved paths.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         进程回收确认沿用后端既有等待链：runner 退出经限时等待确认（超时先终止再等待杀除宽限），
///         目标子进程树由 fork runner 自身负责；后端仅在无法确认回收时抛
///         <see cref="ToolExecutionUncertainException" />，该异常即"回收结果未知"，协调器据此禁用
///         授权主体。取消路径同样先确认 runner 回收再抛出取消。授权执行由后端按同一份分流纯函数
///         切换到专用授权账户对（离线↔AuthOff、在线↔AuthOn），获批读落在授权组的
///         "全量 deny − 获批路径"面上；普通执行维持普通沙箱账户。
///     </para>
///     <para>
///         Process-reclamation confirmation reuses the backend's existing wait chain: the runner's
///         exit is confirmed by bounded waits (terminate followed by the kill grace on timeout), and
///         the target process tree remains the fork runner's own responsibility; the backend throws
///         <see cref="ToolExecutionUncertainException" /> only when reclamation cannot be confirmed,
///         which is exactly the unknown-recclamation signal that disables the authorization subject
///         here. The cancellation path confirms runner reclamation before rethrowing too. The backend
///         selects the dedicated authorization account pair for authorized executions by the same
///         split pure function (offline↔AuthOff, online↔AuthOn), so the approved read lands on the
///         authorization group's deny-minus-approved face; ordinary executions keep the ordinary
///         accounts.
///     </para>
/// </remarks>
public sealed class AuthorizedReadCoordinator : IProcessExecutionBackend
{
    /// <summary>
    ///     授权窗口等待的缺省有界期限；超时即本次授权执行失败可见。 The default bounded deadline for waiting on the authorization window; a timeout
    ///     fails the authorized execution visibly.
    /// </summary>
    public static readonly TimeSpan DefaultWindowTimeout = TimeSpan.FromSeconds(30);

    private readonly Func<CancellationToken, Task<uint>> _capabilityProbe;
    private readonly WindowsSandboxComponents            _components;

    private readonly IAuthorizedReadDisableStore _disableStore;
    private readonly IProcessExecutionBackend    _inner;
    private readonly ISandboxSetupInvoker        _restoreInvoker;
    private readonly IAuthorizedReadWindowLock   _windowLock;
    private readonly TimeSpan                    _windowTimeout;
    private          bool                        _recoveryAttempted;

    /// <summary>
    ///     <para>
    ///         构造协调器；恢复 refresh 调用器、窗口锁、禁用状态存储与能力探测缺省取进程实现
    ///         （ProcessSandboxSetupInvoker / Global 互斥体 / 凭据目录状态文件 / 凭据文件版本探测），
    ///         测试注入全部可替换。
    ///     </para>
    ///     <para>
    ///         Builds the coordinator; the restore invoker, the window lock, the disable store, and the
    ///         capability probe default to the process-backed implementations
    ///         (<see cref="ProcessSandboxSetupInvoker" />, the Global mutex, the credentials-directory
    ///         state file, and the credentials-file version probe), all replaceable by test injection.
    ///     </para>
    /// </summary>
    public AuthorizedReadCoordinator(
        IProcessExecutionBackend             innerBackend,
        WindowsSandboxComponents             components,
        SandboxIsolationPolicy               policy,
        ISandboxSetupInvoker?                restoreInvoker  = null,
        IAuthorizedReadWindowLock?           windowLock      = null,
        IAuthorizedReadDisableStore?         disableStore    = null,
        Func<CancellationToken, Task<uint>>? capabilityProbe = null,
        TimeSpan?                            windowTimeout   = null)
    {
        ArgumentNullException.ThrowIfNull(innerBackend);
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(policy);
        _inner           = innerBackend;
        _components      = components;
        Policy           = policy;
        _restoreInvoker  = restoreInvoker  ?? new ProcessSandboxSetupInvoker(components);
        _windowLock      = windowLock      ?? new GlobalAuthorizedReadWindowLock();
        _disableStore    = disableStore    ?? new AuthorizedReadDisableFile(components);
        _capabilityProbe = capabilityProbe ?? (token => ReadProvisionedVersionAsync(components, token));
        _windowTimeout   = windowTimeout   ?? DefaultWindowTimeout;
    }

    /// <summary>
    ///     冻结的有效隔离策略；authorized 集判定与恢复 payload 构造共用。 The frozen effective isolation policy, shared by authorized-set
    ///     detection and restore-payload construction.
    /// </summary>
    public SandboxIsolationPolicy Policy { get; }

    /// <summary>
    ///     <para>
    ///         执行已准备的进程计划：authorized 集为空时透传内层后端；否则施加三道门并在授权窗口内
    ///         执行，窗口退出前完成恢复 refresh。恢复失败或回收未知持久化禁用状态。
    ///     </para>
    ///     <para>
    ///         Executes a prepared process plan: an empty authorized set passes through to the inner
    ///         backend; otherwise the three gates apply and the execution runs inside the authorization
    ///         window, which closes only after the restore refresh. Restore failures and unknown
    ///         reclamation persist the disable state.
    ///     </para>
    /// </summary>
    public async Task<ProcessExecutionResult> ExecuteAsync(
        PreparedProcessExecution execution,
        IProcessOutputCapture    stdoutCapture,
        IProcessOutputCapture    stderrCapture,
        CancellationToken        cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(stdoutCapture);
        ArgumentNullException.ThrowIfNull(stderrCapture);

        // authorized 集判定与 payload 构造共用同一份分流纯函数：grant 只读根中被 deny 集覆盖的
        // 子集只能走授权通道；普通执行（含未接入快照源的计划）原样透传。
        var split = SandboxAuthorizedReads.Split(Policy.DenyReadPaths, execution.GrantScope?.ReadRoots ?? []);
        if (!split.HasAuthorizedReads)
            return await _inner.ExecuteAsync(execution, stdoutCapture, stderrCapture, cancellationToken)
                               .ConfigureAwait(false);

        // 只读门在窗口外评估：能力不足与本实例已尝试过恢复的禁用都是无副作用拒绝，不必排队等窗口。
        var reason = await EvaluateEntryGatesAsync(split, cancellationToken).ConfigureAwait(false);

        IAuthorizedReadWindowHandle? window = null;
        try
        {
            window = await _windowLock.TryAcquireAsync(_windowTimeout, cancellationToken).ConfigureAwait(false) ??
                     throw SerialWindowTimeout();
            SandboxDiagnosticLog.Event("authorized-read",
                                       $"window acquired authorizedCount={split.AuthorizedReadRoots.Count} cwd='{execution.WorkingDirectory}'");
            if (reason is not null)
                await RecoverInsideWindowAsync(execution, reason, cancellationToken).ConfigureAwait(false);

            ProcessExecutionResult result;
            try
            {
                result = await _inner.ExecuteAsync(execution, stdoutCapture, stderrCapture, cancellationToken)
                                     .ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                await LeaveWindowAsync(execution, failure, cancellationToken).ConfigureAwait(false);
                throw;
            }

            // 正常完成：runner 回收已由后端等待链确认；仍须恢复 refresh 重同步授权组全量 deny。
            await RestoreAsync(execution, cancellationToken).ConfigureAwait(false);
            SandboxDiagnosticLog.Event("authorized-read", "window released");
            return result;
        }
        finally
        {
            window?.Dispose();
        }
    }

    /// <summary>
    ///     <para>
    ///         禁用门与能力门（只读评估）：能力探测读到低于授权代版本（含缺失/损坏）即以"helper
    ///         能力不足"拒绝；禁用状态在本实例尚未尝试过恢复时返回待恢复原因（恢复 refresh 属于
    ///         授权 ACL 变更，必须持窗口执行），已尝试过则以"主体已禁用"明确拒绝。普通执行不经
    ///         过本方法不受影响。
    ///     </para>
    ///     <para>
    ///         The read-only disable and capability gate evaluation: a capability probe below the
    ///         authorized generation (including missing or unreadable credentials) refuses with the
    ///         helper-capability error; a disable state returns the owed-recovery reason while this
    ///         instance has not attempted recovery yet (the recovery refresh mutates the authorized
    ///         ACLs and must run inside the window), and refuses with the explicit disabled error once
    ///         it has. Normal executions never enter this method.
    ///     </para>
    /// </summary>
    /// <returns>
    ///     <para>待恢复原因；未禁用返回 null（无需窗口内恢复）。</para>
    ///     <para>The owed-recovery reason, or null when not disabled (no in-window recovery needed).</para>
    /// </returns>
    private async Task<string?> EvaluateEntryGatesAsync(
        SandboxAuthorizedReadSplit split, CancellationToken cancellationToken)
    {
        var provisionedVersion = await _capabilityProbe(cancellationToken).ConfigureAwait(false);
        if (provisionedVersion < WindowsSandboxComponents.AuthorizedSetupVersion)
            throw new
                ProcessExecutionStartException($"The deployed sandbox helper does not declare the authorized-read capability: " +
                                               $"the provisioned version {provisionedVersion} in '{_components.UsersFilePath}' is below " +
                                               $"{WindowsSandboxComponents.AuthorizedSetupVersion}. The approved read of " +
                                               $"'{string.Join(";", split.AuthorizedReadRoots)}' cannot be dispatched without downgrading it, so the command fails here; " +
                                               "re-run provisioning with a v7-capable helper to enable authorized reads.",
                                               new InvalidOperationException("Authorized-read capability not declared."));

        var (disabled, reason) = await _disableStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!disabled) return null;

        // 禁用只经成功的恢复刷新清除：每个协调器实例（一次组合）先持窗口重试一次恢复 refresh。
        if (_recoveryAttempted) throw DisabledException(reason);
        return reason ?? "the persisted disable state carries no reason";
    }

    /// <summary>
    ///     <para>
    ///         窗口内恢复：以恢复 refresh（v7 空 authorized_read_paths）重同步授权组全量 deny，成功
    ///         即清除持久化禁用；失败以"主体已禁用"拒绝并保留禁用状态。恢复属授权 ACL 变更，持
    ///         窗口执行避免撤掉其他进程正在使用的授权读。
    ///     </para>
    ///     <para>
    ///         The in-window recovery: a restore refresh (a v7 payload with empty authorized_read_paths)
    ///         resyncs the authorization group's full deny and a success clears the persisted disable
    ///         state, while a failure refuses with the disabled error and keeps the state. Being an
    ///         authorized-ACL mutation, the recovery runs inside the window so it never strips an
    ///         approved read another process is currently using.
    ///     </para>
    /// </summary>
    private async Task RecoverInsideWindowAsync(
        PreparedProcessExecution execution, string reason, CancellationToken cancellationToken)
    {
        _recoveryAttempted = true;
        try
        {
            await _restoreInvoker.RefreshAsync(BuildRestorePayload(execution), CancellationToken.None)
                                 .ConfigureAwait(false);
        }
        catch (Exception recoveryFailure)
        {
            SandboxDiagnosticLog.Event("authorized-read",
                                       $"recovery failed type={recoveryFailure.GetType().Name}");
            throw DisabledException($"{reason}; the recovery attempt failed: {recoveryFailure.Message}",
                                    recoveryFailure);
        }

        await _disableStore.ClearAsync(cancellationToken).ConfigureAwait(false);
        SandboxDiagnosticLog.Event("authorized-read", "disable cleared after successful restore");
    }

    /// <summary>
    ///     <para>
    ///         授权窗口退出路径：回收未知（后端抛出不确定性异常）先持久化禁用，再执行恢复 refresh；
    ///         恢复失败同样持久化禁用，原始终止语义按收到的异常如实上抛。
    ///     </para>
    ///     <para>
    ///         The authorization window's exit path: unknown reclamation (the backend's uncertainty
    ///         exception) disables first, then the restore refresh runs; a restore failure disables too,
    ///         and the original termination semantics rethrow as received.
    ///     </para>
    /// </summary>
    private async Task LeaveWindowAsync(
        PreparedProcessExecution execution, Exception failure, CancellationToken cancellationToken)
    {
        if (failure is ToolExecutionUncertainException)
            await DisableAsync($"process reclamation could not be confirmed: {failure.Message}", cancellationToken)
               .ConfigureAwait(false);

        await RestoreAsync(execution, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>
    ///         恢复 refresh：以同一 grant 范围构造 v7 空 authorized_read_paths payload，对授权组重同步
    ///         全量 deny。恢复使用独立期限（调用器自带刷新超时），用户取消后仍须完成；失败持久化
    ///         禁用状态并留诊断，不覆盖调用方正在处理的原始终止语义。
    ///     </para>
    ///     <para>
    ///         The restore refresh: a v7 payload with empty authorized_read_paths built from the same
    ///         grant scope resyncs the authorization group's full deny. The restore keeps its own
    ///         deadline (the invoker's refresh timeout) and still runs after a user cancellation; a
    ///         failure persists the disable state with a diagnostic and never overrides the original
    ///         termination semantics the caller is already handling.
    ///     </para>
    /// </summary>
    private async Task RestoreAsync(PreparedProcessExecution execution, CancellationToken cancellationToken)
    {
        try
        {
            await _restoreInvoker.RefreshAsync(BuildRestorePayload(execution), CancellationToken.None)
                                 .ConfigureAwait(false);
            SandboxDiagnosticLog.Event("authorized-read", "restore ok");
        }
        catch (Exception failure)
        {
            // 授权组停留"全量 deny − 获批路径"状态：禁用后续授权执行，错误在下次入口可见。
            SandboxDiagnosticLog.Event("authorized-read",
                                       $"restore failed type={failure.GetType().Name} cancelRequested={cancellationToken.IsCancellationRequested}");
            await DisableAsync($"the restore refresh failed: {failure.Message}", CancellationToken.None)
               .ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     构造恢复 payload：与授权执行同一 grant 范围，authorized_read_paths 置空数组（v7）。 Builds the restore payload: the same grant scope
    ///     with an empty authorized_read_paths array (v7).
    /// </summary>
    private SandboxSetupPayload BuildRestorePayload(PreparedProcessExecution execution)
    {
        // 与后端 Preflight 的 payload 构造同一份确定性合并：read_roots 已剔除 deny 冲突根。
        var payload = Policy.CreateSetupPayload(_components, execution.WorkingDirectory, Environment.UserName, true,
                                                execution.GrantScope);
        return payload with { AuthorizedReadPaths = [] };
    }

    /// <summary>持久化禁用状态；写入失败随当前异常路径如实上抛。 Persists the disable state; a write failure propagates on the current exception path.</summary>
    private async Task DisableAsync(string reason, CancellationToken cancellationToken)
    {
        await _disableStore.DisableAsync(reason, cancellationToken).ConfigureAwait(false);
        SandboxDiagnosticLog.Event("authorized-read", "disable persisted");
    }

    /// <summary>
    ///     构造"主体已禁用"错误：与能力不足、串行超时两种原因在文案上明确区分。 Builds the disabled-subject error, kept clearly distinct from the
    ///     capability and serialization-timeout errors.
    /// </summary>
    private ProcessExecutionStartException DisabledException(string? reason, Exception? inner = null) =>
        new("Authorized execution is disabled on this machine until a successful restore refresh clears the state: "  +
            $"{reason ?? "the persisted disable state carries no reason"}. Approved reads are refused at the entry; " +
            "resolve the cause and retry an approved read so the recovery attempt can run.",
            inner ?? new InvalidOperationException("The authorization subject is disabled."));

    /// <summary>
    ///     构造"串行超时"错误：窗口等待有界，超时即本次授权执行失败可见，不阻塞普通执行。 Builds the serialization-timeout error: a bounded window wait whose
    ///     timeout fails only this authorized execution.
    /// </summary>
    private ProcessExecutionStartException SerialWindowTimeout() =>
        new($"Another authorized execution holds the authorization window; the Global lock was not acquired within " +
            $"{(int)_windowTimeout.TotalSeconds}s, so this approved read fails visibly while normal executions stay unaffected.",
            new TimeoutException("The authorized-read window lock timed out."));

    /// <summary>
    ///     <para>
    ///         缺省能力探测：读取凭据文件的 version 字段（Full provisioning 写入 = helper
    ///         SETUP_VERSION）。文件缺失或不可读一律返回 0（能力未声明），由入口拒绝对授权执行可见。
    ///     </para>
    ///     <para>
    ///         The default capability probe: reads the credentials file's version field (written as the
    ///         helper SETUP_VERSION by Full provisioning). A missing or unreadable file returns 0 (the
    ///         capability is undeclared), making the entry refusal visible for authorized executions.
    ///     </para>
    /// </summary>
    private static async Task<uint> ReadProvisionedVersionAsync(
        WindowsSandboxComponents components, CancellationToken cancellationToken)
    {
        if (!File.Exists(components.UsersFilePath)) return 0;

        try
        {
            var content = await File.ReadAllTextAsync(components.UsersFilePath, cancellationToken)
                                    .ConfigureAwait(false);
            var users = JsonSerializer.Deserialize(content, WindowsSandboxJsonContext.Default.SandboxUsersFile);
            return users?.Version ?? 0;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
