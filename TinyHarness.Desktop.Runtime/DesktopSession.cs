using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Services.Session;

namespace TinyHarness.Desktop.Runtime;

/// <summary>
///     <para>拥有一个宿主；页面选择不影响运行，释放必须等待取消与保存结束，客户端随宿主绑定释放。</para>
///     <para>Owns one host; page selection never affects runs, disposal awaits cancellation and saving, and the model client is released with the host's binding.</para>
/// </summary>
public sealed class DesktopSession(
    ChatSessionHost         host,
    DesktopApprovalProvider approvals,
    Func<string, string, ModelReasoningEffort?, long, CancellationToken, Task<SessionModelSelection>>? changeModel =
        null,
    Func<CancellationToken, Task>? validateCurrent = null,
    DesktopWorkspaceCoordinator? workspaceRepair = null,
    DesktopWorkspaceOperationScope? workspaceScope = null) : IAsyncDisposable
{
    private readonly object                   _sync     = new();
    private readonly CancellationTokenSource  _lifetime = new();
    private          Task<SessionRun>?        _run;
    private          CancellationTokenSource? _runCancellation;
    private          Task?                    _disposeTask;
    private          bool                     _closing;
    private          Task?                    _change;

    public ChatSessionHost         Host      { get; } = host;
    public DesktopApprovalProvider Approvals { get; } = approvals;
    public string                  Id        => Host.Session.Id;
    public DesktopWorkspaceCoordinator? WorkspaceRepair { get; } = workspaceRepair;

    public bool IsRunning
    {
        get
        {
            lock (_sync) return _run is { IsCompleted: false };
        }
    }

    /// <summary>
    ///     <para>预留本会话运行槽后在后台执行；重复提交在创建回合前被拒绝。</para>
    ///     <para>Reserves this session's run slot before background execution; duplicate submissions fail before creating a turn.</para>
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
            _runCancellation?.Cancel();
            Host.CancelRun();
            return true;
        }
    }

    /// <summary>
    /// <para>阻止后续提交，取消并等待本会话所有工作，然后随宿主释放 writer 与模型客户端。</para>
    /// <para>Rejects later submissions, cancels and awaits this session's work, then releases the writer and model client with the host.</para>
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            _closing = true;
            _disposeTask ??= Task.Run(DisposeCoreAsync);
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        Host.CancelRun();
        try
        {
            if (WorkspaceRepair is not null)
                await WorkspaceRepair.DisposeAsync().ConfigureAwait(false);

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
            finally
            {
                _runCancellation?.Dispose();
                _lifetime.Dispose();
            }
        }
    }
}
