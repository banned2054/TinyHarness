using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.Session;

namespace TinyHarness.Desktop.Runtime;

/// <summary>
/// <para>拥有一个宿主；页面选择不影响运行，释放必须等待取消与保存结束，客户端随宿主绑定释放。</para>
/// <para>Owns one host; page selection never affects runs, disposal awaits cancellation and saving, and the model client is released with the host's binding.</para>
/// </summary>
public sealed class DesktopSession(ChatSessionHost host, DesktopApprovalProvider approvals) : IAsyncDisposable
{
    private readonly object                   _sync     = new();
    private readonly CancellationTokenSource  _lifetime = new();
    private          Task<SessionRun>?        _run;
    private          CancellationTokenSource? _runCancellation;
    private          Task?                    _disposeTask;
    private          bool                     _closing;

    public ChatSessionHost         Host      { get; } = host;
    public DesktopApprovalProvider Approvals { get; } = approvals;
    public string                  Id        => Host.Session.Id;

    public bool IsRunning
    {
        get { lock (_sync) return _run is { IsCompleted: false }; }
    }

    /// <summary>
    /// <para>预留本会话运行槽后在后台执行；重复提交在创建回合前被拒绝。</para>
    /// <para>Reserves this session's run slot before background execution; duplicate submissions fail before creating a turn.</para>
    /// </summary>
    public Task<SessionRun> SubmitAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            if (_run is { IsCompleted: false }) throw new InvalidOperationException("This session already has an active run.");
            _runCancellation?.Dispose();
            var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
            _runCancellation = linked;
            _run = Task.Run(async () =>
            {
                return await Host.SubmitTurnAsync(text, linked.Token).ConfigureAwait(false);
            });
            return _run;
        }
    }

    /// <summary>
    /// <para>取消当前运行；该返回值不代表工具已清理或日志已保存。</para>
    /// <para>Requests cancellation; its return value does not confirm tool cleanup or journal persistence.</para>
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
            Task<SessionRun>? run;
            lock (_sync) run = _run;
            if (run is not null) await run.ConfigureAwait(false);
        }
        finally
        {
            try { Host.Dispose(); }
            finally
            {
                _runCancellation?.Dispose();
                _lifetime.Dispose();
            }
        }
    }
}
