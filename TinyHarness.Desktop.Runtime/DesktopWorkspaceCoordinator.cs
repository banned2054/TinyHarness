using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Services.Runtime;

namespace TinyHarness.Desktop.Runtime;

/// <summary>
///     <para>保存进程内目录运行与修复互斥；未知授权结果禁止本进程再次授权。</para>
///     <para>Coordinates directory runs and repairs in-process; an unknown grant prevents further grants in this process.</para>
/// </summary>
public sealed class DesktopWorkspaceOperationScope
{
    private readonly HashSet<string>                _repairs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int>        _runs    = new(StringComparer.OrdinalIgnoreCase);
    private readonly object                         _sync    = new();
    private          bool                           _grantUncertain;
    public static    DesktopWorkspaceOperationScope Shared { get; } = new();

    public bool GrantUncertain
    {
        get
        {
            lock (_sync)
            {
                return _grantUncertain;
            }
        }
    }

    /// <summary>
    ///     <para>为一次完整运行占槽；修复或未知授权状态下拒绝同根运行。</para>
    ///     <para>Reserves a complete run, rejecting same-root work during repair or an unknown grant.</para>
    /// </summary>
    public IDisposable EnterRun(string root)
    {
        root = Normalize(root);
        lock (_sync)
        {
            if (_repairs.Any(repair => Overlaps(root, repair)) || _grantUncertain)
                throw new InvalidOperationException("目录权限修复尚未安全结束；本次未发送，请检查后再试。");
            _runs[root] = _runs.GetValueOrDefault(root) + 1;
        }

        return new Lease(() =>
        {
            lock (_sync)
            {
                if (--_runs[root] == 0) _runs.Remove(root);
            }
        });
    }

    /// <summary>
    ///     <para>仅在同根运行全部结束后预留修复；不排队或自动重试。</para>
    ///     <para>Reserves repair only after every same-root run ends, without queuing or automatic retries.</para>
    /// </summary>
    public IDisposable EnterRepair(string root)
    {
        root = Normalize(root);
        lock (_sync)
        {
            if (_grantUncertain) throw new InvalidOperationException("目录授权结果未知；检查权限并重启应用后才能再次修复。");
            if (_runs.Keys.Any(run => Overlaps(root, run)) || _repairs.Any(repair => Overlaps(root, repair)))
                throw new InvalidOperationException("该目录或其父子目录仍有运行或修复，请等待结束后重试。");
            _repairs.Add(root);
        }

        return new Lease(() =>
        {
            lock (_sync)
            {
                _repairs.Remove(root);
            }
        });
    }

    /// <summary>
    ///     <para>记录不可撤销的未知授权结果；只随进程退出解除。</para>
    ///     <para>Latches an indeterminate, non-revocable grant until process exit.</para>
    /// </summary>
    public void MarkGrantUncertain()
    {
        lock (_sync)
        {
            _grantUncertain = true;
        }
    }

    private static bool Overlaps(string left, string right) =>
        Workspace.IsInside(left, right) || Workspace.IsInside(right, left);

    private static string Normalize(string root) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;
        public  void    Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}

/// <summary>
///     <para>可注入的目录准备与授权边界；普通准备及提权均只在用户确认后调用。</para>
///     <para>Injectable preparation and grant boundaries, both invoked only after explicit user consent.</para>
/// </summary>
public sealed record DesktopWorkspaceRepairServices(
    Func<CancellationToken, Task<bool>> Prepare,
    IWorkspaceAclGrant                  Grant,
    Func<CancellationToken, Task>       Validate,
    Action?                             ValidateScope = null);

/// <summary>
///     <para>一次会话的权限问题与修复状态，不写入模型上下文或会话日志。</para>
///     <para>A session's permission problem and repair state, excluded from model context and session logs.</para>
/// </summary>
public sealed record DesktopWorkspaceRepairState(
    long                 Revision,
    WorkspaceAclProblem? Problem,
    string               Phase,
    string               Message,
    bool                 CanRepair,
    bool                 NeedsElevation = false);

/// <summary>
///     <para>拥有单会话显式修复任务、确认版本与取消源；修复从不重新提交命令。</para>
///     <para>Owns a session's explicit repair task, consent revision and cancellation; repair never resubmits commands.</para>
/// </summary>
public sealed class DesktopWorkspaceCoordinator : IAsyncDisposable
{
    private readonly Func<DesktopWorkspaceRepairState, ValueTask> _changed;
    private readonly WorkspaceDirectoryIdentity                   _identity;
    private readonly CancellationTokenSource                      _lifetime = new();
    private readonly DesktopWorkspaceOperationScope               _scope;
    private readonly DesktopWorkspaceRepairServices               _services;
    private readonly object                                       _sync = new();
    private          CancellationTokenSource?                     _cancellation;
    private          bool                                         _closing;
    private          Task?                                        _disposeTask;
    private          IDisposable?                                 _lease;
    private          Task?                                        _operation;
    private          DesktopWorkspaceRepairState                  _state = new(0, null, "idle", string.Empty, false);

    public DesktopWorkspaceCoordinator(WorkspaceDirectoryIdentity identity, DesktopWorkspaceRepairServices services,
                                       Func<DesktopWorkspaceRepairState, ValueTask> changed,
                                       DesktopWorkspaceOperationScope? scope = null)
    {
        _identity = identity;
        _services = services;
        _changed  = changed;
        _scope    = scope ?? DesktopWorkspaceOperationScope.Shared;
    }

    public DesktopWorkspaceRepairState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    /// <summary>
    ///     <para>关闭时取消并等待修复收尾，保留未知授权阻断。</para>
    ///     <para>Cancels and awaits repair cleanup on shutdown, preserving an unknown-grant latch.</para>
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            _closing     =   true;
            _disposeTask ??= Task.Run(DisposeCoreAsync);
            return new ValueTask(_disposeTask);
        }
    }

    /// <summary>
    ///     <para>仅接收后端结构化问题；附加写根只展示详情，不扩大当前工作区授权。</para>
    ///     <para>Accepts structured backend problems only; extra roots remain detail-only, never expanding workspace consent.</para>
    /// </summary>
    public async ValueTask ReportAsync(WorkspaceAclProblem problem)
    {
        DesktopWorkspaceRepairState state;
        lock (_sync)
        {
            if (_closing) return;
            var repairable = SameRoot(problem.WriteRoot, _identity.Path) &&
                             problem.Category is WorkspaceAclProblemCategory.MissingInheritedAccess or
                                                 WorkspaceAclProblemCategory.ManagementDenied;
            state = _state = new DesktopWorkspaceRepairState(_state.Revision + 1, problem, "problem",
                                                             repairable
                                                                 ? "沙箱目录权限准备失败；可查看详情并显式修复。未启动该命令。"
                                                                 : "沙箱写根检查失败；此问题仅提供详情，不自动扩展目录授权。", repairable);
        }

        await _changed(state).ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>按已展示版本确认普通准备，或额外确认 UAC；过期确认和重复操作被拒绝。</para>
    ///     <para>
    ///         Confirms normal preparation or, separately, UAC against the displayed revision; stale or duplicate consent is
    ///         rejected.
    ///     </para>
    /// </summary>
    public Task RepairAsync(long revision, bool elevation = false)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            if (_operation is { IsCompleted: false }) throw new InvalidOperationException("目录权限修复正在进行。");
            if (revision != _state.Revision || !_state.CanRepair || elevation != _state.NeedsElevation)
                throw new InvalidOperationException("修复范围或状态已改变，请重新查看并确认。");
            _identity.EnsureUnchanged();
            _lease ??= _scope.EnterRepair(_identity.Path);
            _cancellation?.Dispose();
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _operation    = Task.Run(() => RepairCoreAsync(elevation, _cancellation.Token));
            return _operation;
        }
    }

    /// <summary>
    ///     <para>取消当前修复或撤回额外授权确认；已启动的提权可能仍产生权限变更。</para>
    ///     <para>Cancels repair or withdraws pending elevation consent; an elevated child may still change permissions.</para>
    /// </summary>
    public async Task CancelAsync()
    {
        List<Exception> errors = [];
        Task?           operation;
        lock (_sync)
        {
            try
            {
                _cancellation?.Cancel();
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }

            operation = _operation;
        }

        try
        {
            if (operation is not null) await operation.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }

        try
        {
            if (State.NeedsElevation)
            {
                ReleaseLease();
                await SetStateAsync("cancelled", "已取消额外授权；未启动或重放命令。", true).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }

        if (errors.Count > 0) throw new AggregateException("取消目录准备时发生错误。", errors);
    }

    private async Task RepairCoreAsync(bool elevation, CancellationToken token)
    {
        var keepLease      = false;
        var grantStarted   = false;
        var scopeValidated = false;
        try
        {
            token.ThrowIfCancellationRequested();
            _services.ValidateScope?.Invoke();
            scopeValidated = true;
            await SetStateAsync(elevation ? "elevating" : "preparing",
                                elevation ? "正在请求 Windows 目录授权…" : "正在以普通权限准备当前工作区…", false)
               .ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            _identity.EnsureUnchanged();
            if (!elevation && !await _services.Prepare(token).ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();
                await SetStateAsync("consent", "普通权限不足；需要再次确认后才请求 Windows UAC。", true, true)
                   .ConfigureAwait(false);
                keepLease = true;
                return;
            }

            if (elevation)
            {
                grantStarted = true;
                var outcome = await _services.Grant.GrantAsync(_identity, token).ConfigureAwait(false);
                grantStarted = false;
                if (outcome == WorkspaceGrantOutcome.DidNotFinish)
                {
                    _scope.MarkGrantUncertain();
                    await SetStateAsync("unknown", "目录授权结果未知，可能仍在运行或已部分修改。检查并重启应用后才能重试。", false)
                       .ConfigureAwait(false);
                    return;
                }

                if (outcome != WorkspaceGrantOutcome.Completed)
                {
                    await SetStateAsync(outcome == WorkspaceGrantOutcome.Declined ? "cancelled" : "failed",
                                        $"目录授权未完成（{outcome}）；未启动或重放命令。", true)
                       .ConfigureAwait(false);
                    return;
                }
            }

            token.ThrowIfCancellationRequested();
            _identity.EnsureUnchanged();
            await _services.Validate(token).ConfigureAwait(false);
            await SetStateAsync("completed", "目录权限准备已验证；没有重放命令。需要时请自行发送新的请求，正常审批及中断检查仍有效。", false)
               .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (grantStarted) _scope.MarkGrantUncertain();
            await SetStateAsync(grantStarted ? "unknown" : "cancelled",
                                grantStarted ? "授权取消后结果未知；检查并重启应用后才能重试。" : "目录准备已取消，可能已有部分权限变更；没有重放命令。",
                                !grantStarted).ConfigureAwait(false);
        }
        catch (WorkspaceGrantFailureException ex)
        {
            await SetStateAsync("failed", $"目录授权失败：{ex.Message}", true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (grantStarted) _scope.MarkGrantUncertain();
            await SetStateAsync(grantStarted ? "unknown" : "failed",
                                grantStarted
                                    ? "目录授权通信失败，结果未知；检查并重启应用后才能重试。" + ex.Message
                                    : !scopeValidated
                                        ? $"目录授权前检查失败：{ex.Message}"
                                        : $"目录修复失败，可能已有部分权限变更；没有重放命令。{ex.Message}", !grantStarted)
               .ConfigureAwait(false);
        }
        finally
        {
            if (!keepLease) ReleaseLease();
        }
    }

    private async ValueTask SetStateAsync(string phase, string message, bool canRepair, bool needsElevation = false)
    {
        DesktopWorkspaceRepairState state;
        lock (_sync)
        {
            state = _state = _state with
            {
                Revision = _state.Revision + 1, Phase = phase, Message = message,
                CanRepair = canRepair && !_scope.GrantUncertain, NeedsElevation = needsElevation
            };
        }

        await _changed(state).ConfigureAwait(false);
    }

    private void ReleaseLease()
    {
        lock (_sync)
        {
            _lease?.Dispose();
            _lease = null;
        }
    }

    private static bool SameRoot(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                      Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);

    private async Task DisposeCoreAsync()
    {
        List<Exception> errors = [];
        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }

        try
        {
            await CancelAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }
        finally
        {
            ReleaseLease();
            _cancellation?.Dispose();
            _lifetime.Dispose();
        }

        if (errors.Count > 0) throw new AggregateException("关闭目录准备时发生错误；已完成其余清理。", errors);
    }
}

/// <summary>
///     <para>在工具转换异常之前保留结构化 ACL 问题；失败仍原样交还工具层。</para>
///     <para>Retains structured ACL problems before tool exception conversion, rethrowing the original failure.</para>
/// </summary>
public sealed class DesktopWorkspaceBackend(IProcessExecutionBackend backend, DesktopWorkspaceCoordinator coordinator)
    : IProcessExecutionBackend
{
    public async Task<ProcessExecutionResult> ExecuteAsync(
        PreparedProcessExecution execution, IProcessOutputCapture stdoutCapture, IProcessOutputCapture stderrCapture,
        CancellationToken        cancellationToken)
    {
        try
        {
            return await backend.ExecuteAsync(execution, stdoutCapture, stderrCapture, cancellationToken)
                                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            for (var current = ex; current is not null; current = current.InnerException)
                if (current is WorkspaceAclPreparationException acl)
                {
                    await coordinator.ReportAsync(acl.Problem).ConfigureAwait(false);
                    break;
                }

            throw;
        }
    }
}
