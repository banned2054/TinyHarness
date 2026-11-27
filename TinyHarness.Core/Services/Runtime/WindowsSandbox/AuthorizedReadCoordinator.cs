using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using System.Text.Json;
using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     <para>
///         C2 获批读授权通道的派发前协调器：识别携带 authorized_read_paths 的授权执行并施加三道
///         门——禁用门（恢复失败/回收未知后的持久化禁用，含每实例一次的恢复尝试）、能力门（部署的
///         helper 必须声明 v7 授权代能力）与串行门（Global 互斥体限定的授权窗口），门内完成授权
///         refresh、执行与恢复 refresh（v7 空 authorized_read_paths 重同步授权组全量 deny；deny_read_paths
///         叠加 helper 拒读簿记的只增并集，与后端 refresh 共用同一实现），禁用
///         状态的恢复刷新同属授权 ACL 变更，亦持窗口执行；恢复 refresh 成功后、内层命令启动前，
///         与后端共用同一后刷新校验（写 ACL 继承校验 + 组读根就绪门）。普通
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
///         with empty authorized_read_paths resyncing the authorization group's full deny; deny_read_paths
///         folds the helper deny-read bookkeeping in as an add-only union sharing the backend's
///         implementation), and the disable-state recovery refresh, being the same kind of
///         authorized-ACL mutation, also runs inside the window; after a successful recovery refresh
///         and before the inner command starts, the shared post-refresh validation (write-ACL
///         inheritance validation plus the group read-root readiness gate) runs like the backend's.
///         Normal
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
///     <para>
///         锁序固定为授权窗口（<see cref="GlobalAuthorizedReadWindowLock.MutexName" />）→ 拒读
///         刷新门（按 home 键控的 <c>Global\TinyHarness-DenyRead-Refresh-*</c> 互斥体），无反向
///         获取路径，无死锁；拒读刷新门使恢复/退出 refresh 与其他进程的普通每命令 refresh 跨进程
///         串行化，门等待失败（超时或内核对象不可用）按既有"恢复失败/退出刷新失败"语义转为
///         禁用或拒绝，不破坏授权窗口。
///     </para>
///     <para>
///         The lock order is fixed as the authorization window
///         (<see cref="GlobalAuthorizedReadWindowLock.MutexName" />) → the deny-read refresh gate
///         (the home-keyed <c>Global\TinyHarness-DenyRead-Refresh-*</c> mutex), with no reverse
///         acquisition path and no deadlock; the deny-read refresh gate serializes the recovery /
///         exit refreshes against other processes' ordinary per-command refreshes across processes,
///         and a failed gate wait (timeout or an unusable kernel object) turns into the existing
///         recovery-failure / exit-refresh-failure semantics (disable or refuse) without breaking
///         the authorization window.
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
    private readonly Func<string, (string FinalPath, ulong VolumeSerialNumber, string FileIdHex)?>
                                                         _captureHomeIdentity;
    private readonly WindowsSandboxComponents            _components;

    private readonly TimeSpan                    _denyReadGateTimeout;
    private readonly ISandboxDenyReadRefreshGate _denyReadRefreshGate;
    private readonly IAuthorizedReadDisableStore _disableStore;
    private readonly IProcessExecutionBackend    _inner;
    private readonly Func<int?>                  _readSystemBootId;
    private readonly ISandboxSetupInvoker        _restoreInvoker;
    private readonly IAuthorizedReadWindowLock   _windowLock;
    private readonly TimeSpan                    _windowTimeout;
    private readonly Func<SandboxSetupPayload, CancellationToken, Task> _postRefreshValidation;
    private          string?                     _inMemoryRestoreFailedReason;
    private          string?                     _inMemoryUncertainReason;
    private          int                         _recoveryAttempted;

    /// <summary>
    ///     <para>
    ///         构造协调器；恢复 refresh 调用器、窗口锁、禁用状态存储与能力探测缺省取进程实现
    ///         （ProcessSandboxSetupInvoker / Global 互斥体 / 凭据目录状态文件 / 凭据文件版本探测），
    ///         拒读刷新门缺省为按 home 键控的 Global 互斥体、门期限缺省
    ///         <see cref="GlobalDenyReadRefreshGate.DefaultAcquireTimeout" />，后刷新校验缺省为共享
    ///         步骤（写 ACL 继承校验 + 组读根就绪门），测试注入全部可替换。
    ///     </para>
    ///     <para>
    ///         Builds the coordinator; the restore invoker, the window lock, the disable store, and the
    ///         capability probe default to the process-backed implementations
    ///         (<see cref="ProcessSandboxSetupInvoker" />, the Global mutex, the credentials-directory
    ///         state file, and the credentials-file version probe), the deny-read refresh gate defaults
    ///         to the home-keyed Global mutex with
    ///         <see cref="GlobalDenyReadRefreshGate.DefaultAcquireTimeout" /> as its deadline, the
    ///         post-refresh validation
    ///         defaults to the shared step (write-ACL inheritance validation plus the group read-root
    ///         readiness gate), and all of them are replaceable by test injection.
    ///     </para>
    /// </summary>
    public AuthorizedReadCoordinator(
        IProcessExecutionBackend             innerBackend,
        WindowsSandboxComponents             components,
        SandboxIsolationPolicy               policy,
        ISandboxSetupInvoker?                restoreInvoker       = null,
        IAuthorizedReadWindowLock?           windowLock           = null,
        IAuthorizedReadDisableStore?         disableStore         = null,
        Func<CancellationToken, Task<uint>>? capabilityProbe      = null,
        TimeSpan?                            windowTimeout        = null,
        Func<SandboxSetupPayload, CancellationToken, Task>? postRefreshValidation = null,
        ISandboxDenyReadRefreshGate?         denyReadRefreshGate  = null,
        TimeSpan?                            denyReadGateTimeout  = null)
        : this(innerBackend, components, policy, restoreInvoker, windowLock, disableStore, capabilityProbe,
               windowTimeout, postRefreshValidation, denyReadRefreshGate, denyReadGateTimeout, null)
    {
    }

    internal AuthorizedReadCoordinator(
        IProcessExecutionBackend                            innerBackend,
        WindowsSandboxComponents                            components,
        SandboxIsolationPolicy                              policy,
        ISandboxSetupInvoker?                               restoreInvoker,
        IAuthorizedReadWindowLock?                          windowLock,
        IAuthorizedReadDisableStore?                        disableStore,
        Func<CancellationToken, Task<uint>>?                capabilityProbe,
        TimeSpan?                                           windowTimeout,
        Func<SandboxSetupPayload, CancellationToken, Task>? postRefreshValidation,
        ISandboxDenyReadRefreshGate?                        denyReadRefreshGate,
        TimeSpan?                                           denyReadGateTimeout,
        Func<int?>?                                         readSystemBootId,
        Func<string, (string FinalPath, ulong VolumeSerialNumber, string FileIdHex)?>? captureHomeIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(innerBackend);
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(policy);
        _inner                 = innerBackend;
        _components            = components;
        Policy                 = policy;
        _restoreInvoker        = restoreInvoker      ?? new ProcessSandboxSetupInvoker(components);
        _windowLock            = windowLock          ?? new GlobalAuthorizedReadWindowLock();
        _disableStore          = disableStore        ?? new AuthorizedReadDisableFile(components);
        _capabilityProbe       = capabilityProbe     ?? (token => ReadProvisionedVersionAsync(components, token));
        _windowTimeout         = windowTimeout       ?? DefaultWindowTimeout;
        _denyReadRefreshGate   = denyReadRefreshGate ?? new GlobalDenyReadRefreshGate();
        _denyReadGateTimeout   = denyReadGateTimeout ?? GlobalDenyReadRefreshGate.DefaultAcquireTimeout;
        _postRefreshValidation = postRefreshValidation ?? SandboxPostRefreshValidation.Default;
        _readSystemBootId      = readSystemBootId    ?? SandboxProvisionLeaseStore.TryReadSystemBootId;
        _captureHomeIdentity   = captureHomeIdentity ?? AuthorizedReadDisableFile.TryCaptureCanonicalHomeIdentity;
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

        // 只读门在窗口外做快速拒绝：能力不足、不可靠刷新解禁或本实例已尝试过恢复的禁用均不必排队等窗口。
        await EvaluateEntryGatesAsync(split, cancellationToken).ConfigureAwait(false);

        IAuthorizedReadWindowHandle? window = null;
        try
        {
            window = await _windowLock.TryAcquireAsync(_windowTimeout, cancellationToken).ConfigureAwait(false) ??
                     throw SerialWindowTimeout();
            SandboxDiagnosticLog.Event("authorized-read",
                                       $"window acquired authorizedCount={split.AuthorizedReadRoots.Count} cwd='{execution.WorkingDirectory}'");

            // 持窗口锁后一次性捕获并锁定物理源 home 身份；锁内禁用读取、可选恢复、WAL 守卫、
            // 后端授权刷新、退出恢复与失败禁用全程绑定该同一源目录，绝不再次解析可变别名。
            var windowHome = CaptureWindowHome();
            var lockedState = await EvaluateEffectiveDisableStateAsync(windowHome, cancellationToken)
                                   .ConfigureAwait(false);
            if (lockedState.Disabled)
            {
                EnsureRefreshRecoveryAllowed(lockedState, windowHome);
                await RecoverInsideWindowAsync(execution, lockedState, windowHome, cancellationToken)
                   .ConfigureAwait(false);
            }

            // 打开授权窗口前先原子落盘 WAL 状态（退出未确认 + 当前 BootId）：若宿主在执行期间崩溃或被强杀，
            // 遗弃互斥体绝不会让后续进程在无退出证明的情况下重新打开授权窗口。
            await PersistPreWindowGuardAsync(windowHome, cancellationToken).ConfigureAwait(false);

            ProcessExecutionResult result;
            try
            {
                using (AuthorizedReadWindowContext.Enter(windowHome))
                {
                    result = await _inner.ExecuteAsync(execution, stdoutCapture, stderrCapture, cancellationToken)
                                         .ConfigureAwait(false);
                }
            }
            catch (Exception failure)
            {
                await LeaveWindowAsync(execution, windowHome, failure, cancellationToken).ConfigureAwait(false);
                throw;
            }

            // 正常完成：runner 回收已由后端等待链确认；须先恢复 refresh 重同步授权组全量 deny，成功后清除 WAL 状态。
            await RestoreAndClearWindowGuardAsync(execution, windowHome, cancellationToken).ConfigureAwait(false);
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
    ///         禁用门与能力门（窗口外只读预检）：能力探测读到低于授权代版本（含缺失/损坏）即以"helper
    ///         能力不足"拒绝；禁用状态若属于不可仅凭 ACL 刷新解禁的类别（整树退出未知且未重启，或状态损坏），
    ///         或本实例已尝试过恢复，则在窗口外直接拒绝，避免无意义排队。拿到窗口锁后仍会重新评估禁用状态。
    ///     </para>
    ///     <para>
    ///         The read-only disable and capability pre-check outside the window: a capability probe below the
    ///         authorized generation refuses with the helper-capability error; a disable state that cannot be
    ///         cleared by ACL refresh alone (unconfirmed whole-tree exit on the same boot, or corrupt/unknown
    ///         state), or one this instance already tried to recover, is refused immediately without queuing.
    ///         The disable state is always re-read after acquiring the window lock.
    ///     </para>
    /// </summary>
    private async Task EvaluateEntryGatesAsync(
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

        var state = await EvaluateEffectiveDisableStateAsync(windowHome: null, cancellationToken).ConfigureAwait(false);
        // 锁外看到 WindowOpen 时可能是正常并发持有者正在窗口内执行，仅当存在非 WindowOpen 的不可恢复约束
        // （或本实例已尝试过恢复且不存在仅因 WindowOpen 待排队的条目）时才提前拒绝，否则排队拿锁后复读。
        if (state.Disabled && !ShouldQueueForWindowOpen(state))
            EnsureRefreshRecoveryAllowed(state, expectedWindowHome: null);
    }

    private async Task<AuthorizedReadDisableState> EvaluateEffectiveDisableStateAsync(
        AuthorizedReadWindowHome? windowHome, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _inMemoryUncertainReason) is { } uncertainReason)
        {
            var (home, vol, fileId) = ResolveFallbackHomeIdentity(windowHome);
            return AuthorizedReadDisableState.DisabledState(
                uncertainReason, AuthorizedReadDisableKind.ReclamationUncertain, _readSystemBootId(), home, vol, fileId);
        }

        var persisted = windowHome is not null
            ? await _disableStore.ReadStateAsync(windowHome, cancellationToken).ConfigureAwait(false)
            : await _disableStore.ReadStateAsync(cancellationToken).ConfigureAwait(false);
        if (persisted.Disabled) return persisted;

        if (Volatile.Read(ref _inMemoryRestoreFailedReason) is { } restoreReason)
        {
            var (home, vol, fileId) = ResolveFallbackHomeIdentity(windowHome);
            return AuthorizedReadDisableState.DisabledState(
                restoreReason, AuthorizedReadDisableKind.RestoreFailed, null, home, vol, fileId);
        }

        return AuthorizedReadDisableState.NotDisabled;
    }

    private (string? Home, ulong? VolumeSerialNumber, string? FileIdHex) ResolveFallbackHomeIdentity(
        AuthorizedReadWindowHome? windowHome)
    {
        if (windowHome is not null)
            return (windowHome.CanonicalPath, windowHome.VolumeSerialNumber, windowHome.FileIdHex);

        var currentIdentity = _captureHomeIdentity(_components.SandboxHome);
        var currentHome = currentIdentity?.FinalPath
                       ?? AuthorizedReadDisableFile.TryResolveCanonicalHomePath(_components.SandboxHome,
                                                                                allowMissing: true);
        return (currentHome, currentIdentity?.VolumeSerialNumber, currentIdentity?.FileIdHex);
    }

    private bool ShouldQueueForWindowOpen(AuthorizedReadDisableState state)
    {
        var entries = state.ActiveEntries;
        if (entries.Count == 0) return false;
        var hasWindowOpen = false;
        foreach (var entry in entries)
        {
            if (entry.Kind == AuthorizedReadDisableKind.WindowOpen)
            {
                hasWindowOpen = true;
                continue;
            }

            // 若同时还存在一条非 WindowOpen 的不可恢复约束（如旧文件中的同开机周期 ReclamationUncertain），
            // 无需排队等锁，直接快速拒绝。
            if (!CanRecoverEntry(entry, expectedWindowHome: null, out _, out _)) return false;
        }

        return hasWindowOpen;
    }

    private void EnsureRefreshRecoveryAllowed(
        AuthorizedReadDisableState state, AuthorizedReadWindowHome? expectedWindowHome)
    {
        if (!CanRecoverByRefresh(state, expectedWindowHome, out var blockReason, out _))
            throw NonRecoverableDisabledException(state, blockReason);
        // 禁用只经成功的恢复刷新清除：每个协调器实例（一次组合）先持窗口重试一次恢复 refresh。
        if (Volatile.Read(ref _recoveryAttempted) != 0) throw DisabledException(state.Reason);
    }

    private bool CanRecoverByRefresh(
        AuthorizedReadDisableState state,
        AuthorizedReadWindowHome?  expectedWindowHome,
        out string?                blockReason,
        out string?                verifiedHomePath)
    {
        verifiedHomePath = null;
        var entries = state.ActiveEntries;
        if (entries.Count == 0)
        {
            blockReason = "the persisted disable state has no active entries to verify";
            return false;
        }

        // 合取检查：双读合并后的每一条记录都必须同时满足各自的退出约束与同一源 home 身份约束，缺一不可。
        string? agreedHome = null;
        foreach (var entry in entries)
        {
            if (!CanRecoverEntry(entry, expectedWindowHome, out blockReason, out var entryVerifiedHome))
                return false;

            if (agreedHome is null)
            {
                agreedHome = entryVerifiedHome;
            }
            else if (!string.Equals(agreedHome, entryVerifiedHome, StringComparison.OrdinalIgnoreCase))
            {
                blockReason = $"The persisted disable state spans multiple sandbox homes ('{agreedHome}' and '{entryVerifiedHome}'); " +
                              "a single recovery refresh cannot prove grants in both homes were revoked.";
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(agreedHome))
        {
            blockReason = "The originating sandbox home path could not be resolved for recovery.";
            return false;
        }

        verifiedHomePath = agreedHome;
        blockReason      = null;
        return true;
    }

    private bool CanRecoverEntry(
        AuthorizedReadDisableEntry entry,
        AuthorizedReadWindowHome?  expectedWindowHome,
        out string?                blockReason,
        out string?                verifiedHomePath)
    {
        verifiedHomePath = null;
        switch (entry.Kind)
        {
            case AuthorizedReadDisableKind.RestoreFailed:
                break;
            case AuthorizedReadDisableKind.ReclamationUncertain:
            case AuthorizedReadDisableKind.WindowOpen:
                // 退出未知与宿主崩溃遗留的 WindowOpen：只有系统重启（BootId 变化）才能证明旧授权进程已不存在。
                if (entry.BootId is not { } recordedBootId ||
                    _readSystemBootId() is not { } currentBootId ||
                    currentBootId == recordedBootId)
                {
                    blockReason = "An ACL restore refresh alone cannot prove the previous authorized process tree has exited; " +
                                  "reboot the machine (changing BootId) or explicitly clear the state after verifying no sandbox " +
                                  "authorization process remains running.";
                    return false;
                }

                break;
            default:
                blockReason = "The persisted disable state cannot be classified as an ACL-only restore failure; " +
                              "re-run provisioning or explicitly clear the disable state after verifying machine state.";
                return false;
        }

        // 固定 helper 仅按 payload.codex_home 撤销旧授权：必须由产生禁用的源 sandboxHome 执行恢复刷新。
        // Windows 上必须从同句柄捕获物理最终路径与 128 位目录身份（卷序列号 + 128 位 FileId），并按已存记录精确核对。
        return VerifyOriginatingHomeIdentity(expectedWindowHome, entry, out blockReason, out verifiedHomePath);
    }

    private bool VerifyOriginatingHomeIdentity(
        AuthorizedReadWindowHome?  expectedWindowHome,
        AuthorizedReadDisableEntry entry,
        out string?                blockReason,
        out string?                verifiedHomePath)
    {
        verifiedHomePath = null;
        if (!OperatingSystem.IsWindows())
        {
            var currentLexical = expectedWindowHome?.CanonicalPath ??
                                 AuthorizedReadDisableFile.TryResolveCanonicalHomePath(
                                     _components.SandboxHome, allowMissing: false);
            if (currentLexical is null || string.IsNullOrWhiteSpace(entry.SourceSandboxHome))
            {
                blockReason = "The persisted disable state does not identify which sandbox home created it, " +
                              "so auto-recovery cannot prove prior authorization grants were revoked.";
                return false;
            }

            var sourceLexical = AuthorizedReadDisableFile.TrimStoredTrailingSeparators(entry.SourceSandboxHome);
            if (!string.Equals(sourceLexical, currentLexical, StringComparison.OrdinalIgnoreCase))
            {
                blockReason = $"The disable state was recorded by sandbox home '{sourceLexical}', whereas this session uses " +
                              $"'{currentLexical}'. Refreshing '{currentLexical}' does not read '{sourceLexical}' bookkeeping or " +
                              "revoke grants created there; recover from the originating sandbox home or explicitly reset.";
                return false;
            }

            verifiedHomePath = currentLexical;
            blockReason      = null;
            return true;
        }

        (string FinalPath, ulong VolumeSerialNumber, string FileIdHex)? currentIdentity;
        if (expectedWindowHome is not null)
        {
            if (expectedWindowHome.VolumeSerialNumber is not { } expectedVolume ||
                string.IsNullOrWhiteSpace(expectedWindowHome.FileIdHex))
            {
                blockReason = $"The physical final path and 128-bit directory identity of sandbox home '{expectedWindowHome.CanonicalPath}' " +
                              "could not be captured, so this session cannot verify that it matches the originating sandbox home; " +
                              "explicitly clear the disable state after verifying directory targets and ACLs.";
                return false;
            }

            currentIdentity = (expectedWindowHome.CanonicalPath, expectedVolume, expectedWindowHome.FileIdHex.Trim());
        }
        else
        {
            currentIdentity = _captureHomeIdentity(_components.SandboxHome);
            if (currentIdentity is null)
            {
                blockReason = $"The physical final path and 128-bit directory identity of sandbox home '{_components.SandboxHome}' " +
                              "could not be captured, so this session cannot verify that it matches the originating sandbox home; " +
                              "explicitly clear the disable state after verifying directory targets and ACLs.";
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(entry.SourceSandboxHome))
        {
            blockReason = "The persisted disable state does not identify which sandbox home created it, so refreshing " +
                          $"'{currentIdentity.Value.FinalPath}' cannot prove prior authorization grants in another home were revoked; " +
                          "restore from the originating sandbox home or explicitly clear the disable state after verifying ACLs.";
            return false;
        }

        var sourceHome = AuthorizedReadDisableFile.TrimStoredTrailingSeparators(entry.SourceSandboxHome);
        if (entry.SourceHomeVolumeSerial is not { } sourceVolume ||
            string.IsNullOrWhiteSpace(entry.SourceHomeFileId))
        {
            blockReason = $"The persisted disable state for '{sourceHome}' is missing its 128-bit directory identity " +
                          "(source_home_volume_serial / source_home_file_id), so refreshing " +
                          $"'{currentIdentity.Value.FinalPath}' cannot prove the directory was not deleted and recreated; " +
                          "explicitly clear the disable state after verifying directory identity and ACLs.";
            return false;
        }

        var sourceFileId = entry.SourceHomeFileId.Trim();
        if (!string.Equals(sourceHome, currentIdentity.Value.FinalPath, StringComparison.OrdinalIgnoreCase) ||
            sourceVolume != currentIdentity.Value.VolumeSerialNumber ||
            !string.Equals(sourceFileId, currentIdentity.Value.FileIdHex, StringComparison.OrdinalIgnoreCase))
        {
            blockReason = $"The disable state was recorded by sandbox home '{sourceHome}' " +
                          $"(volume 0x{sourceVolume:x16}, file 0x{sourceFileId}), whereas this session resolves to " +
                          $"'{currentIdentity.Value.FinalPath}' (volume 0x{currentIdentity.Value.VolumeSerialNumber:x16}, " +
                          $"file 0x{currentIdentity.Value.FileIdHex}). Refreshing this directory does not prove prior " +
                          "bookkeeping or authorization grants were revoked; recover from the originating sandbox home or explicitly reset.";
            return false;
        }

        verifiedHomePath = currentIdentity.Value.FinalPath;
        blockReason      = null;
        return true;
    }

    /// <summary>
    ///     <para>
    ///         窗口内恢复：以恢复 refresh（v7 空 authorized_read_paths）重同步授权组全量 deny，成功
    ///         即清除持久化禁用；失败以"主体已禁用"拒绝并保留禁用状态。恢复属授权 ACL 变更，持
    ///         窗口执行避免撤掉其他进程正在使用的授权读；[读簿记 → 合并 → refresh → 持久化] 另持
    ///         按已验证物理源目录键控的拒读刷新门与其他进程串行化，门超时（null）与等待失败都由
    ///         现有 catch 转为禁用语义。
    ///     </para>
    ///     <para>
    ///         The in-window recovery: a restore refresh (a v7 payload with empty authorized_read_paths)
    ///         resyncs the authorization group's full deny and a success clears the persisted disable
    ///         state, while a failure refuses with the disabled error and keeps the state. Being an
    ///         authorized-ACL mutation, the recovery runs inside the window so it never strips an
    ///         approved read another process is currently using; [read bookkeeping → merge → refresh →
    ///         persistence] additionally holds the deny-read refresh gate keyed on the verified physical
    ///         originating directory, and a gate timeout (null) or a failed wait converts to the disable
    ///         semantics through the existing catch.
    ///     </para>
    /// </summary>
    private async Task RecoverInsideWindowAsync(
        PreparedProcessExecution execution,
        AuthorizedReadDisableState lockedState,
        AuthorizedReadWindowHome   windowHome,
        CancellationToken          cancellationToken)
    {
        Volatile.Write(ref _recoveryAttempted, 1);
        var reason             = lockedState.Reason ?? "the persisted disable state carries no reason";
        string? verifiedLegacyPath = null;
        SandboxSetupPayload      payload;
        try
        {
            // 门按开窗时锁定的物理源目录键控，不复用可变别名；等待复用授权窗口已取得的 token；超时（null）
            // 与等待失败（含取消）都由现有 catch 转为禁用语义，绝不无门恢复。
            ISandboxDenyReadRefreshGateHandle? gate =
                await _denyReadRefreshGate
                      .TryAcquireAsync(windowHome.CanonicalPath, _denyReadGateTimeout, cancellationToken)
                      .ConfigureAwait(false)
                ?? throw new
                    InvalidOperationException("The recovery refresh could not acquire the deny-read refresh gate " +
                                              $"(home '{windowHome.CanonicalPath}') within {(int)_denyReadGateTimeout.TotalSeconds}s; " +
                                              "the recovery is refused instead of refreshing ungated.");
            try
            {
                // 持门后再次通过 _components.SandboxHome 核对物理最终路径与 128 位目录身份：防止进门前瞬间目录被替换或 junction 改指。
                if (!CanRecoverByRefresh(lockedState, expectedWindowHome: null, out var blockReason,
                                         out var confirmedHomePath) ||
                    !string.Equals(confirmedHomePath, windowHome.CanonicalPath, StringComparison.OrdinalIgnoreCase))
                {
                    SandboxDiagnosticLog.Event("authorized-read", "recovery home identity mismatch inside deny-read gate");
                    throw NonRecoverableDisabledException(
                        lockedState,
                        blockReason ??
                        $"The resolved sandbox home changed from '{windowHome.CanonicalPath}' to '{confirmedHomePath}' " +
                        "while acquiring the deny-read refresh gate.");
                }

                if (!string.IsNullOrWhiteSpace(lockedState.LegacyStatePath))
                {
                    var expectedLegacyPath = Path.Combine(
                        windowHome.CanonicalPath, ".sandbox-secrets", "authorized_read_state.json");
                    if (!string.Equals(Path.GetFullPath(lockedState.LegacyStatePath),
                                       Path.GetFullPath(expectedLegacyPath),
                                       StringComparison.OrdinalIgnoreCase))
                    {
                        SandboxDiagnosticLog.Event("authorized-read", "recovery legacy state path mismatch");
                        throw NonRecoverableDisabledException(
                            lockedState,
                            $"The legacy disable state path '{lockedState.LegacyStatePath}' does not belong to the " +
                            $"verified originating sandbox home '{windowHome.CanonicalPath}'; refusing to clear an unrelated file.");
                    }

                    verifiedLegacyPath = expectedLegacyPath;
                }

                // 钉住已验证物理源目录（不含 FILE_SHARE_DELETE），使保护路径校验、簿记读取与 helper 均直接作用于该目录。
                string? pinBlockReason = null;
                using var pinnedHomeHandle = OperatingSystem.IsWindows()
                    ? AuthorizedReadDisableFile.TryPinVerifiedWindowHome(
                        windowHome, "recovery", out pinBlockReason, lockedState.ActiveEntries)
                    : null;
                if (OperatingSystem.IsWindows() && pinnedHomeHandle is null)
                {
                    SandboxDiagnosticLog.Event("authorized-read", "recovery home pin failed inside deny-read gate");
                    throw NonRecoverableDisabledException(
                        lockedState,
                        pinBlockReason ??
                        $"Could not pin the verified recovery sandbox home '{windowHome.CanonicalPath}' before refreshing.");
                }

                payload = BuildRestorePayload(execution, windowHome.CanonicalPath);
                await _restoreInvoker.RefreshAsync(payload, CancellationToken.None)
                                     .ConfigureAwait(false);

                // 刷新返回后、清除禁用前仍在门内复核目标源目录身份：无法确认刷新确实作用于该目录时保留禁用。
                if (!AuthorizedReadDisableFile.VerifyPostRefreshWindowHome(
                        windowHome, pinnedHomeHandle, payload, _captureHomeIdentity, "recovery",
                        out var postBlockReason))
                {
                    SandboxDiagnosticLog.Event("authorized-read", "recovery post-refresh home identity mismatch");
                    throw NonRecoverableDisabledException(lockedState, postBlockReason);
                }
            }
            catch (Exception ex) when (SandboxRefreshExitUncertainty.TryGetConfirmation(ex, out var confirmation))
            {
                // helper 退出状态未知：门经 ReleaseWhenConfirmed 转入后台确认后释放（fail-closed），
                // 所有权在此移交并置空，finally 不再 Dispose；异常继续流向既有恢复失败 catch。
                SandboxDiagnosticLog.Event("authorized-read",
                                           $"helper-exit-uncertain phase=recovery type={ex.GetType().Name}");
                gate.ReleaseWhenConfirmed(confirmation!);
                gate = null;
                throw;
            }
            finally
            {
                // RefreshAsync 返回或超时/取消路径确认退出即 helper 不再写簿记，此刻释放门安全；
                // 退出未确认时门已移交 ReleaseWhenConfirmed，由后台确认任务在 helper 退出后才释放。
                gate?.Dispose();
            }
        }
        catch (ProcessExecutionStartException)
        {
            throw;
        }
        catch (Exception recoveryFailure)
        {
            SandboxDiagnosticLog.Event("authorized-read",
                                       $"recovery failed type={recoveryFailure.GetType().Name}");
            throw DisabledException($"{reason}; the recovery attempt failed: {recoveryFailure.Message}",
                                    recoveryFailure);
        }

        await _disableStore.ClearRecoveredStateAsync(verifiedLegacyPath, cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _inMemoryRestoreFailedReason, null);
        SandboxDiagnosticLog.Event("authorized-read", "disable cleared after successful restore");

        // 恢复 refresh 成功后、内层命令启动前：与后端 refresh 共用同一后刷新校验（写 ACL 继承
        // 校验 + 组读根就绪门）。失败 fail-closed：命令保持未启动、窗口随 finally 释放；恢复本身
        // 已完成（禁用状态已清除），下一次授权执行无需再次恢复。
        try
        {
            await _postRefreshValidation(payload, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ProcessExecutionStartException)
        {
            throw;
        }
        catch (Exception validationFailure)
        {
            SandboxDiagnosticLog.Event("authorized-read",
                                       $"post-refresh validation failed type={validationFailure.GetType().Name}");
            throw new
                ProcessExecutionStartException($"The sandbox post-refresh validation failed after the recovery refresh: {validationFailure.Message}",
                                               validationFailure);
        }
    }

    /// <summary>
    ///     <para>
    ///         授权窗口退出路径：回收未知（后端抛出不确定性异常）先以独立于命令取消的令牌持久化禁用，再在
    ///         <c>finally</c> 中执行恢复 refresh；禁用落盘与拒读恢复均不受已取消的命令令牌打断，原始终止语义
    ///         按收到的异常如实上抛。
    ///     </para>
    ///     <para>
    ///         The authorization window's exit path: unknown reclamation (the backend's uncertainty
    ///         exception) persists the disable state independently of command cancellation first, then
    ///         executes the restore refresh in <c>finally</c>; neither disable persistence nor deny
    ///         restoration is skipped because the command token was cancelled, and the original termination
    ///         semantics rethrow as received.
    ///     </para>
    /// </summary>
    private AuthorizedReadWindowHome CaptureWindowHome()
    {
        if (!OperatingSystem.IsWindows())
        {
            var lexical = AuthorizedReadDisableFile.TryResolveCanonicalHomePath(
                _components.SandboxHome, allowMissing: false);
            if (lexical is null)
                throw new ProcessExecutionStartException(
                    $"The canonical path of sandbox home '{_components.SandboxHome}' could not be resolved before " +
                    "opening the authorization window.",
                    new InvalidOperationException("Window home canonical path could not be resolved."));

            return new AuthorizedReadWindowHome(lexical, null, null);
        }

        var captured = _captureHomeIdentity(_components.SandboxHome);
        if (captured is null)
            throw new ProcessExecutionStartException(
                $"The physical final path and 128-bit directory identity of sandbox home '{_components.SandboxHome}' " +
                "could not be captured before opening the authorization window.",
                new InvalidOperationException("Window home physical identity could not be captured."));

        return new AuthorizedReadWindowHome(
            captured.Value.FinalPath, captured.Value.VolumeSerialNumber, captured.Value.FileIdHex);
    }

    private async Task PersistPreWindowGuardAsync(
        AuthorizedReadWindowHome windowHome, CancellationToken cancellationToken)
    {
        try
        {
            await DisableAsync(
                    "authorized-read window was opened without confirmed completion or process-tree exit",
                    AuthorizedReadDisableKind.WindowOpen,
                    _readSystemBootId(),
                    windowHome,
                    cancellationToken)
               .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception persistError)
        {
            SandboxDiagnosticLog.Event("authorized-read",
                                       $"pre-window guard persist failed type={persistError.GetType().Name}");
            throw new ProcessExecutionStartException(
                $"Failed to persist the pre-window authorized-read guard state before opening the authorization window: {persistError.Message}",
                persistError);
        }
    }

    private async Task LeaveWindowAsync(
        PreparedProcessExecution execution,
        AuthorizedReadWindowHome windowHome,
        Exception                failure,
        CancellationToken        cancellationToken)
    {
        var              isUncertain     = failure is ToolExecutionUncertainException;
        var              uncertainReason = isUncertain
            ? $"process reclamation could not be confirmed: {failure.Message}"
            : null;
        List<Exception>? persistErrors   = null;
        var              restoreSucceeded = false;
        try
        {
            if (isUncertain)
            {
                // 先在实例内存落锁：即使磁盘落盘失败，本实例也不会再次打开授权窗口。
                Volatile.Write(ref _inMemoryUncertainReason, uncertainReason);
                try
                {
                    // 命令取消触发的不确定收尾不得复用已取消的 cancellationToken，否则禁用落盘与后续恢复会被跳过。
                    await DisableAsync(uncertainReason!, AuthorizedReadDisableKind.ReclamationUncertain,
                                       _readSystemBootId(), windowHome, CancellationToken.None)
                       .ConfigureAwait(false);
                }
                catch (Exception persistError)
                {
                    SandboxDiagnosticLog.Event("authorized-read",
                                               $"disable persist failed type={persistError.GetType().Name}");
                    persistErrors ??= [];
                    persistErrors.Add(persistError);
                }
            }
        }
        finally
        {
            var (succeeded, restorePersistError) = await RestoreAndCapturePersistErrorAsync(
                execution, windowHome, cancellationToken, uncertainReason).ConfigureAwait(false);
            restoreSucceeded = succeeded;
            if (restorePersistError is not null)
            {
                persistErrors ??= [];
                persistErrors.Add(restorePersistError);
            }
        }

        if (!isUncertain && restoreSucceeded)
        {
            // 非不确定异常（命令确认未启动或已确认回收）且拒读恢复已成功：只清除共享 WAL 守卫，绝不经可变别名删旧文件。
            try
            {
                await _disableStore.ClearSharedGuardAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception clearError)
            {
                SandboxDiagnosticLog.Event("authorized-read",
                                           $"pre-window guard clear failed type={clearError.GetType().Name}");
                persistErrors ??= [];
                persistErrors.Add(clearError);
            }
        }

        if (persistErrors is null) return;

        if (isUncertain)
        {
            // 绝不能让落盘异常覆盖 ToolExecutionUncertainException，否则上层 AgentLoop 不再按“执行结果未知”处理。
            var detail = string.Join("; ", persistErrors.Select(error => error.Message));
            throw new ToolExecutionUncertainException(
                $"{failure.Message} Additionally, persisting the authorized-read disable state failed: {detail}",
                new AggregateException(persistErrors.Prepend(failure)));
        }

        if (failure is OperationCanceledException)
        {
            // 绝不能让守卫清理/落盘异常覆盖原始 OperationCanceledException，否则上层会丢失取消语义；
            // 同时置位实例内存锁死，磁盘上仍留有 WindowOpen 守卫供跨进程 fail-closed。
            var detail = string.Join("; ", persistErrors.Select(error => error.Message));
            SandboxDiagnosticLog.Event("authorized-read",
                                       $"guard cleanup failed during cancellation count={persistErrors.Count}");
            Volatile.Write(ref _inMemoryUncertainReason,
                           $"authorized-read guard cleanup failed during cancellation: {detail}");
            return;
        }

        ExceptionDispatchInfo.Capture(persistErrors[0]).Throw();
    }

    private async Task RestoreAndClearWindowGuardAsync(
        PreparedProcessExecution execution,
        AuthorizedReadWindowHome windowHome,
        CancellationToken        cancellationToken)
    {
        var (restoreSucceeded, persistError) = await RestoreAndCapturePersistErrorAsync(
            execution, windowHome, cancellationToken, null).ConfigureAwait(false);
        if (persistError is not null)
        {
            // 窗口前 WindowOpen WAL 守卫仍在磁盘上；同时置位实例内存锁死，绝不以守卫落盘异常覆盖已确认的命令结果。
            SandboxDiagnosticLog.Event("authorized-read",
                                       $"guard persist failed after command completion type={persistError.GetType().Name}");
            Volatile.Write(ref _inMemoryUncertainReason,
                           $"failed to persist authorized-read restore failure after command completion: {persistError.Message}");
            return;
        }

        if (!restoreSucceeded) return;

        try
        {
            await _disableStore.ClearSharedGuardAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception clearError)
        {
            // 清除守卫失败时磁盘仍留有 WindowOpen 守卫供跨进程 fail-closed；同时置位实例内存锁死，
            // 防止同实例忽略未清除守卫，且绝不以守卫清理异常覆盖已确认的命令结果。
            SandboxDiagnosticLog.Event("authorized-read",
                                       $"pre-window guard clear failed phase=complete type={clearError.GetType().Name}");
            Volatile.Write(ref _inMemoryUncertainReason,
                           $"failed to clear the pre-window authorized-read guard after restore: {clearError.Message}");
        }
    }

    private async Task<(bool RestoreSucceeded, Exception? PersistError)> RestoreAndCapturePersistErrorAsync(
        PreparedProcessExecution execution,
        AuthorizedReadWindowHome windowHome,
        CancellationToken        cancellationToken,
        string?                  uncertainDisableReason)
    {
        try
        {
            // 门按开窗时锁定的物理源目录键控，等待用 None（恢复刷新不随用户取消，有缺省 90s 上界）；
            // 超时或门内物理源身份校验/钉住/后刷新复核失败均落入下方 catch 转为持久化禁用，绝不放行 ClearAsync。
            ISandboxDenyReadRefreshGateHandle? gate =
                await _denyReadRefreshGate
                      .TryAcquireAsync(windowHome.CanonicalPath, _denyReadGateTimeout, CancellationToken.None)
                      .ConfigureAwait(false)
                ?? throw new
                    InvalidOperationException("The restore refresh could not acquire the deny-read refresh gate " +
                                              $"(home '{windowHome.CanonicalPath}') within {(int)_denyReadGateTimeout.TotalSeconds}s; " +
                                              "the gate wait timed out and no refresh was executed.");
            try
            {
                string? pinBlockReason = null;
                using var pinnedHomeHandle = OperatingSystem.IsWindows()
                    ? AuthorizedReadDisableFile.TryPinVerifiedWindowHome(
                        windowHome, "exit restore", out pinBlockReason)
                    : null;
                if (OperatingSystem.IsWindows() && pinnedHomeHandle is null)
                {
                    SandboxDiagnosticLog.Event("authorized-read", "exit restore home pin failed inside deny-read gate");
                    throw new InvalidOperationException(
                        pinBlockReason ??
                        $"Could not pin the originating sandbox home '{windowHome.CanonicalPath}' before exit restore.");
                }

                var payload = BuildRestorePayload(execution, windowHome.CanonicalPath);
                await _restoreInvoker.RefreshAsync(payload, CancellationToken.None)
                                     .ConfigureAwait(false);

                if (!AuthorizedReadDisableFile.VerifyPostRefreshWindowHome(
                        windowHome, pinnedHomeHandle, payload, _captureHomeIdentity, "exit restore",
                        out var postBlockReason))
                {
                    SandboxDiagnosticLog.Event("authorized-read", "exit restore post-refresh home identity mismatch");
                    throw new InvalidOperationException(
                        postBlockReason ??
                        $"The exit restore refresh could not be confirmed on '{windowHome.CanonicalPath}'.");
                }

                SandboxDiagnosticLog.Event("authorized-read", "restore ok");
                return (true, null);
            }
            catch (Exception ex) when (SandboxRefreshExitUncertainty.TryGetConfirmation(ex, out var confirmation))
            {
                // helper 退出状态未知：门经 ReleaseWhenConfirmed 转入后台确认后释放（fail-closed），
                // 所有权在此移交并置空，finally 不再 Dispose；异常继续流向既有禁用 catch。
                SandboxDiagnosticLog.Event("authorized-read",
                                           $"helper-exit-uncertain phase=restore type={ex.GetType().Name}");
                gate.ReleaseWhenConfirmed(confirmation!);
                gate = null;
                throw;
            }
            finally
            {
                // RefreshAsync 返回或超时/取消路径确认退出即 helper 不再写簿记，此刻释放门安全；
                // 退出未确认时门已移交 ReleaseWhenConfirmed，由后台确认任务在 helper 退出后才释放。
                gate?.Dispose();
            }
        }
        catch (Exception failure)
        {
            // 授权组停留"全量 deny − 获批路径"状态：禁用后续授权执行，错误在下次入口可见。
            // 若本次本就处于退出未知禁用，绝不降级成 RestoreFailed，防止后续请求仅凭 ACL 刷新解禁；
            // 且始终持久化开窗时的原 windowHome 身份，避免改指后的别名混入禁用记录。
            SandboxDiagnosticLog.Event("authorized-read",
                                       $"restore failed type={failure.GetType().Name} cancelRequested={cancellationToken.IsCancellationRequested}");
            var kind = uncertainDisableReason is not null
                ? AuthorizedReadDisableKind.ReclamationUncertain
                : AuthorizedReadDisableKind.RestoreFailed;
            var reason = uncertainDisableReason is not null
                ? $"{uncertainDisableReason}; the restore refresh also failed: {failure.Message}"
                : $"the restore refresh failed: {failure.Message}";
            var bootId = kind == AuthorizedReadDisableKind.ReclamationUncertain ? _readSystemBootId() : null;

            if (kind == AuthorizedReadDisableKind.ReclamationUncertain)
                Volatile.Write(ref _inMemoryUncertainReason, reason);
            else
                Volatile.Write(ref _inMemoryRestoreFailedReason, reason);

            try
            {
                await DisableAsync(reason, kind, bootId, windowHome, CancellationToken.None).ConfigureAwait(false);
                return (false, null);
            }
            catch (Exception persistError)
            {
                SandboxDiagnosticLog.Event("authorized-read",
                                           $"disable persist failed phase=restore type={persistError.GetType().Name}");
                return (false, persistError);
            }
        }
    }

    /// <summary>
    ///     <para>
    ///         构造恢复 payload：与授权执行同一 grant 范围，authorized_read_paths 置空数组（v7）；
    ///         构造后在门内复核所有写根不得与共享 home 已登记的保护路径冲突，随后在冻结语义集之上叠加
    ///         helper 拒读簿记的只增并集（与后端 refresh 共用
    ///         <see cref="SandboxDenyReadBookkeeping.Merge" /> 同一实现）——授权组与普通组的 deny
    ///         基面都只收窄不扩权，恢复/退出 refresh 不撤销另一端写入簿记的条目。调用方须持有按
    ///         home 键控的拒读刷新门，使保护清单校验、簿记读取与 refresh 跨进程串行化。
    ///     </para>
    ///     <para>
    ///         Builds the restore payload: the same grant scope with an empty authorized_read_paths
    ///         array (v7); after building it, validates under the gate that no write root overlaps any
    ///         protected path registered for the shared home, then folds the helper deny-read
    ///         bookkeeping into the frozen semantic set as an add-only union (sharing
    ///         <see cref="SandboxDenyReadBookkeeping.Merge" /> with the backend's refresh) — the deny
    ///         base of both the authorization and the ordinary group only narrows, and the restore /
    ///         exit refresh never revokes entries another side wrote into the bookkeeping. The caller
    ///         must hold the home-keyed deny-read refresh gate so the protected-path check, the
    ///         bookkeeping read, and the refresh stay serialized across processes.
    ///     </para>
    /// </summary>
    private SandboxSetupPayload BuildRestorePayload(PreparedProcessExecution execution, string? targetSandboxHome = null)
    {
        var effectiveHome = string.IsNullOrWhiteSpace(targetSandboxHome)
            ? _components.SandboxHome
            : targetSandboxHome;
        var effectiveComponents = string.Equals(effectiveHome, _components.SandboxHome, StringComparison.Ordinal)
            ? _components
            : _components with { SandboxHome = effectiveHome };

        // 与后端 Preflight 的 payload 构造同一份确定性合并：read_roots 已剔除 deny 冲突根。
        var payload = Policy.CreateSetupPayload(effectiveComponents, execution.WorkingDirectory, Environment.UserName, true,
                                                execution.GrantScope);
        // 门内复核恢复写根不得覆盖共享 home 下新登记的另一端组件或 C1 路径；抛出后由恢复/退出路径
        // 既有 catch 保留持久化禁用语义，不向 helper 下发恢复 refresh。
        WindowsSandboxComposer.ValidateWriteRootsAgainstRegisteredProtectedPaths(payload.WriteRoots,
                                                                                 effectiveHome,
                                                                                 "authorized-read restore write root");
        var (denyPaths, bookkeepingEntries, bookkeepingMerged) = SandboxDenyReadBookkeeping.Merge(
            "authorized-read refresh", payload.DenyReadPaths, payload.ReadRoots, effectiveHome);
        SandboxDiagnosticLog.Event("deny-read-union",
                                   $"authorized bookkeepingEntries={bookkeepingEntries} bookkeepingMerged={bookkeepingMerged}");
        return payload with
        {
            DenyReadPaths       = SandboxIsolationPolicy.Deduplicate(denyPaths),
            AuthorizedReadPaths = []
        };
    }

    /// <summary>持久化禁用状态；写入失败随当前异常路径如实上抛。 Persists the disable state; a write failure propagates on the current exception path.</summary>
    private async Task DisableAsync(
        string                    reason,
        AuthorizedReadDisableKind kind,
        int?                      bootId,
        AuthorizedReadWindowHome  originatingHome,
        CancellationToken         cancellationToken)
    {
        await _disableStore.DisableAsync(reason, kind, bootId, originatingHome, cancellationToken)
                           .ConfigureAwait(false);
        SandboxDiagnosticLog.Event("authorized-read", $"disable persisted kind={kind}");
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
    ///     <para>构造不可仅凭 ACL 刷新解禁的错误：退出未知或状态损坏时拒绝重开授权窗口。</para>
    ///     <para>
    ///         Builds the non-auto-recoverable disable error: refuses to reopen the authorization window when
    ///         process exit is unconfirmed or the persisted state is unknown/corrupt.
    ///     </para>
    /// </summary>
    private ProcessExecutionStartException NonRecoverableDisabledException(
        AuthorizedReadDisableState state, string? blockReason)
    {
        var detail = state.Reason ?? "the persisted disable state carries no reason";
        var guidance = blockReason ??
                       "The persisted disable state cannot be cleared by refreshing this sandbox home alone.";
        return new ProcessExecutionStartException(
            $"Authorized execution is disabled on this machine and cannot be auto-cleared by an ACL refresh: {detail}. " +
            $"{guidance}",
            new InvalidOperationException("The authorization subject is disabled pending process-exit proof or explicit reset."));
    }

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
