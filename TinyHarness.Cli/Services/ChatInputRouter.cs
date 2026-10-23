namespace TinyHarness.Cli.Services;

/// <summary>
///     <para>以唯一按行读取泵分发空闲输入、审批和运行控制；空闲时最多保留一行。</para>
///     <para>Routes idle input, approvals and run control through one line-reading pump, buffering at most one idle line.</para>
/// </summary>
internal sealed class ChatInputRouter(TextReader inner) : TextReader
{
    private readonly Lock                    _gate     = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim           _wake     = new(0, 1);

    private TaskCompletionSource?          _activeDispatch;
    private Func<string?, Task>?           _activeHandler;
    private string?                        _buffered;
    private bool                           _disposed;
    private bool                           _eof;
    private Exception?                     _fault;
    private TaskCompletionSource<string?>? _prompt;
    private Task?                          _pump;

    internal void SetActiveHandler(Func<string?, Task>? handler)
    {
        lock (_gate)
        {
            _activeHandler = handler;
            Wake();
        }
    }

    /// <summary>
    ///     <para>停止运行输入分发，并等待已取出的输入处理完毕，避免跨会话延迟执行命令。</para>
    ///     <para>Stops run-input dispatch and drains an already dequeued input to prevent delayed commands crossing sessions.</para>
    /// </summary>
    internal Task ClearActiveHandlerAsync()
    {
        lock (_gate)
        {
            _activeHandler = null;
            Wake();
            return _activeDispatch?.Task ?? Task.CompletedTask;
        }
    }

    public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource<string?> prompt;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (_prompt is not null)
                throw new InvalidOperationException("Only one prompt may consume chat input at a time.");
            if (_fault is not null) throw new IOException("The chat input pump failed.", _fault);
            if (_buffered is { } line)
            {
                _buffered = null;
                return line;
            }

            if (_eof) return null;
            prompt  =   new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _prompt =   prompt;
            _pump   ??= Task.Run(PumpAsync);
            Wake();
        }

        try
        {
            return await prompt.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_prompt, prompt)) _prompt = null;
            }
        }
    }

    public override Task<string?> ReadLineAsync() => ReadLineAsync(CancellationToken.None).AsTask();

    private void Wake()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    private async Task PumpAsync()
    {
        try
        {
            while (true)
            {
                await _wake.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_prompt is null && _activeHandler is null) continue;
                }

                var                  line = await inner.ReadLineAsync(_lifetime.Token).ConfigureAwait(false);
                Func<string?, Task>? handler;
                lock (_gate)
                {
                    handler = _activeHandler;
                    if (line is null)
                    {
                        _eof = true;
                        _prompt?.TrySetResult(null);
                        _prompt = null;
                    }
                    else if (_prompt is not null &&
                             (handler is null || IsApprovalReply(line)))
                    {
                        _prompt.TrySetResult(line);
                        _prompt = null;
                        handler = null;
                    }
                    else if (handler is null)
                    {
                        _buffered = line;
                    }

                    if (handler is not null)
                        _activeDispatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                if (handler is not null)
                    try
                    {
                        await handler(line).ConfigureAwait(false);
                    }
                    finally
                    {
                        lock (_gate)
                        {
                            _activeDispatch!.TrySetResult();
                            _activeDispatch = null;
                        }
                    }

                if (line is null) return;
                lock (_gate)
                {
                    if (_prompt is not null || _activeHandler is not null) Wake();
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Func<string?, Task>? handler;
            lock (_gate)
            {
                _fault = ex;
                _prompt?.TrySetException(new IOException("The chat input pump failed.", ex));
                _prompt = null;
                handler = _activeHandler;
            }

            if (handler is not null) await handler(null).ConfigureAwait(false);
        }
    }

    private static bool IsApprovalReply(string line) => line.Trim().ToLowerInvariant() is
        "a" or "allow" or "once" or "allow once" or "y" or "yes" or
        "s" or "session" or "allow session" or "d" or "deny" or "n" or "no";

    protected override void Dispose(bool disposing)
    {
        if (!disposing) return;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed      = true;
            _activeHandler = null;
            _prompt?.TrySetCanceled();
            _prompt = null;
            _lifetime.Cancel();
        }

        // 底层 Console.ReadLine 可能不支持中途取消；保留唯一在途读取，不启动替代读取或等待阻塞退出。
        base.Dispose(disposing);
    }
}
