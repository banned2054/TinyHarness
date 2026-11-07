using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Models.Permissions;

namespace TinyHarness.Desktop.Runtime;

/// <summary>
///     <para>拥有一个宿主；页面选择不影响运行，释放必须等待取消与保存结束，客户端随宿主绑定释放。</para>
///     <para>
///         Owns one host; page selection never affects runs, disposal awaits cancellation and saving, and the model
///         client is released with the host's binding.
///     </para>
/// </summary>
public sealed class DesktopSession(
    ChatSessionHost         host,
    DesktopApprovalProvider approvals,
    Func<string, string, ModelReasoningEffort?, long, CancellationToken, Task<SessionModelSelection>>? changeModel =
        null,
    Func<CancellationToken, Task>?  validateCurrent = null,
    DesktopWorkspaceCoordinator?    workspaceRepair = null,
    DesktopWorkspaceOperationScope? workspaceScope  = null) : IAsyncDisposable
{
    private readonly CancellationTokenSource  _lifetime = new();
    private readonly object                   _sync     = new();
    private          Task?                    _change;
    private          bool                     _closing;
    private          Task?                    _disposeTask;
    private          Task<SessionRun>?        _run;
    private          CancellationTokenSource? _runCancellation;

    public ChatSessionHost              Host            { get; } = host;
    public DesktopApprovalProvider      Approvals       { get; } = approvals;
    public string                       Id              => Host.Session.Id;
    public DesktopWorkspaceCoordinator? WorkspaceRepair { get; } = workspaceRepair;

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _run is { IsCompleted: false };
            }
        }
    }

    /// <summary>
    ///     <para>阻止后续提交，取消并等待本会话所有工作，然后随宿主释放 writer 与模型客户端。</para>
    ///     <para>
    ///         Rejects later submissions, cancels and awaits this session's work, then releases the writer and model client
    ///         with the host.
    ///     </para>
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
    ///     <para>在与发送、关闭共用的操作槽内可靠切换审批模式；活动运行必须先停止。</para>
    ///     <para>Switches the approval mode durably in the shared send/close operation slot; active runs must stop first.</para>
    /// </summary>
    public Task<SessionApprovalMode> ChangeApprovalModeAsync(SessionApprovalMode mode,
                                                             CancellationToken   cancellationToken = default)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            if (_run is { IsCompleted: false } || _change is { IsCompleted: false })
                throw new InvalidOperationException("请先停止当前运行，再切换权限。");
            var task = Task.Run(async () =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
                return await Host.ChangeApprovalModeAsync(mode, linked.Token).ConfigureAwait(false);
            });
            _change = task;
            return task;
        }
    }

    /// <summary>
    ///     <para>在会话操作槽内确认权限迁移；运行、其他变更和关闭期间拒绝操作。</para>
    ///     <para>Confirms permission migration in the session operation slot; rejects while running, changing, or closing.</para>
    /// </summary>
    public Task<SessionApprovalMode> ConfirmPermissionMigrationAsync(
        SessionApprovalMode mode, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            if (_run is { IsCompleted: false } || _change is { IsCompleted: false })
                throw new InvalidOperationException("请先停止当前运行，再确认权限迁移。");
            var task = Task.Run(async () =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
                return await Host.ConfirmPermissionMigrationAsync(mode, linked.Token).ConfigureAwait(false);
            });
            _change = task;
            return task;
        }
    }

    /// <summary>
    ///     <para>在会话操作槽内可靠解除项目归属；活动运行或其他会话变更期间拒绝执行。</para>
    ///     <para>
    ///         Durably removes project ownership inside the session operation slot; rejects while a run or another session
    ///         change is active.
    ///     </para>
    /// </summary>
    /// <param name="projectId">
    ///     <para>必须与当前会话归属一致的项目 ID。</para>
    ///     <para>The project id that must match the current session assignment.</para>
    /// </param>
    /// <param name="cancellationToken">
    ///     <para>取消本次日志追加。</para>
    ///     <para>Cancels this journal append.</para>
    /// </param>
    public Task UnassignProjectAsync(string projectId, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            if (_run is { IsCompleted: false } || _change is { IsCompleted: false })
                throw new InvalidOperationException("请先停止当前运行，再删除所属项目。");

            var task = Task.Run(async () =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token,
                         cancellationToken);
                await Host.UnassignProjectAsync(projectId, linked.Token).ConfigureAwait(false);
            });
            _change = task;
            return task;
        }
    }

    /// <summary>
    ///     <para>预留本会话运行槽后在后台执行；重复提交在创建回合前被拒绝。</para>
    ///     <para>
    ///         Reserves this session's run slot before background execution; duplicate submissions fail before creating a
    ///         turn.
    ///     </para>
    /// </summary>
    public Task<SessionRun> SubmitAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            if (_change is { IsCompleted: false })
                throw new InvalidOperationException("Model selection is in progress.");
            if (_run is { IsCompleted: false })
                throw new InvalidOperationException("This session already has an active run.");
            var lease = (workspaceScope ?? DesktopWorkspaceOperationScope.Shared).EnterRun(Host.Session.WorkspaceRoot);
            _runCancellation?.Dispose();
            var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
            _runCancellation = linked;
            _run = Task.Run(async () =>
            {
                using (lease)
                {
                    if (validateCurrent is not null) await validateCurrent(linked.Token).ConfigureAwait(false);
                    return await Host.SubmitTurnAsync(text, linked.Token).ConfigureAwait(false);
                }
            });
            return _run;
        }
    }

    /// <summary>
    ///     <para>在本页生命周期内准备并提交选择；发送和关闭等待同一操作槽。</para>
    ///     <para>Prepares and commits selection within this page's lifetime; sending and closing share the operation slot.</para>
    /// </summary>
    public Task<SessionModelSelection> ChangeModelAsync(string profile,  string model, ModelReasoningEffort? effort,
                                                        long   revision, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            if (_run is { IsCompleted: false } || _change is { IsCompleted: false })
                throw new InvalidOperationException("当前运行结束后可切换。");
            if (changeModel is null) throw new InvalidOperationException("Model selection is unavailable.");
            var task = Task.Run(async () =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
                return await changeModel(profile, model, effort, revision, linked.Token).ConfigureAwait(false);
            });
            _change = task;
            return task;
        }
    }

    /// <summary>
    ///     <para>取消当前运行；该返回值不代表工具已清理或日志已保存。</para>
    ///     <para>Requests cancellation; its return value does not confirm tool cleanup or journal persistence.</para>
    /// </summary>
    public bool Cancel()
    {
        lock (_sync)
        {
            if (_run is not { IsCompleted: false }) return false;
            List<Exception> errors = [];
            try
            {
                _runCancellation?.Cancel();
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }

            try
            {
                Host.CancelRun();
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }

            if (errors.Count > 0) throw new AggregateException("请求取消运行时发生错误；仍需等待清理和保存。", errors);
            return true;
        }
    }

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
            Host.CancelRun();
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }

        try
        {
            if (WorkspaceRepair is not null) await WorkspaceRepair.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }

        try
        {
            Task? run;
            Task? change;
            lock (_sync)
            {
                run    = _run;
                change = _change;
            }

            if (run is not null)
                try
                {
                    await run.ConfigureAwait(false);
                }
                catch (Exception) when (run.IsCanceled || run.IsFaulted)
                {
                    // 发送前刷新失败由发起者报告；关闭只等待收尾，不再次抛出同一错误。
                }

            if (change is not null)
                try
                {
                    await change.ConfigureAwait(false);
                }
                catch (Exception) when (change.IsCanceled || change.IsFaulted)
                {
                    /* 选择错误由发起者报告，关闭仍释放资源。 */
                }
        }
        finally
        {
            try
            {
                Host.Dispose();
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
            finally
            {
                _runCancellation?.Dispose();
                _lifetime.Dispose();
            }
        }

        if (errors.Count > 0) throw new AggregateException("关闭会话时发生错误；已完成其余清理。", errors);
    }
}
