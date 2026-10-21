using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Session;

namespace TinyHarness.Core.Services.Session;

/// <summary>
///     <para>单次运行的有界控制邮箱；暂停只等待安全边界，取消传递给原执行栈。</para>
///     <para>
///         A bounded control mailbox for one run; pause waits at safe boundaries and cancellation reaches the existing
///         stack.
///     </para>
/// </summary>
internal sealed class SessionRunControl : IDisposable
{
    private readonly CancellationTokenSource       _cancel;
    private readonly object                        _gate           = new();
    private readonly Queue<SessionHistoryItem>     _history        = new();
    private readonly Queue<RunAmendment>           _pending        = new();
    private readonly TaskCompletionSource          _ready          = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<CancellationTokenSource> _retiredSignals = [];
    private readonly SemaphoreSlim                 _submissionGate = new(1, 1);

    private int                     _accepted;
    private CancellationTokenSource _amendmentSignal = new();
    private int                     _characters;
    private bool                    _ended;
    private TaskCompletionSource?   _resume;

    internal SessionRunControl(
        SessionRun run, Func<RunAmendment, CancellationToken, Task> apply, CancellationToken cancellationToken)
    {
        Run        = run;
        ApplyAsync = apply;
        _cancel    = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    }

    internal SessionRun                                  Run        { get; }
    internal Func<RunAmendment, CancellationToken, Task> ApplyAsync { get; }

    internal CancellationToken Token => _cancel.Token;

    internal bool IsPaused
    {
        get
        {
            lock (_gate)
            {
                return _resume is not null;
            }
        }
    }

    internal bool HasAmendments
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count > 0;
            }
        }
    }

    internal CancellationToken AmendmentToken
    {
        get
        {
            lock (_gate)
            {
                return _amendmentSignal.Token;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _ended = true;
            _ready.TrySetCanceled();
            _resume?.TrySetCanceled();
            _cancel.Dispose();
            _amendmentSignal.Dispose();
            foreach (var signal in _retiredSignals) signal.Dispose();
        }
    }

    internal void Ready() => _ready.TrySetResult();

    internal void RecordHistory(SessionHistoryItem item)
    {
        lock (_gate)
        {
            _history.Enqueue(item);
        }
    }

    internal IReadOnlyList<SessionHistoryItem> DrainHistory()
    {
        lock (_gate)
        {
            var result = _history.ToArray();
            _history.Clear();
            return result;
        }
    }

    internal bool Pause()
    {
        lock (_gate)
        {
            if (_ended) return false;
            _resume ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return true;
        }
    }

    internal bool Resume()
    {
        lock (_gate)
        {
            if (_ended) return false;
            _resume?.TrySetResult();
            _resume = null;
            return true;
        }
    }

    internal bool Cancel()
    {
        lock (_gate)
        {
            if (_ended) return false;
            _cancel.Cancel();
            return true;
        }
    }

    internal async Task WaitAsync(Func<string, ValueTask> publish, CancellationToken cancellationToken)
    {
        await _submissionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        _submissionGate.Release();
        var announced = false;
        while (true)
        {
            Task? wait;
            lock (_gate)
            {
                wait = _resume?.Task;
            }

            if (wait is null) break;
            if (!announced)
            {
                await publish("paused").ConfigureAwait(false);
                announced = true;
            }

            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (announced) await publish("running").ConfigureAwait(false);
    }

    internal async Task<bool> TryFinishAsync(CancellationToken cancellationToken)
    {
        await _submissionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_pending.Count > 0 || _resume is not null) return false;
                _ended = true;
                return true;
            }
        }
        finally
        {
            _submissionGate.Release();
        }
    }

    internal async Task EnqueueAsync(RunAmendment amendment, Func<Task> persist, CancellationToken cancellationToken)
    {
        await _ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        await _submissionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_ended)
                    throw new InvalidOperationException("The run no longer accepts amendments; submit a new turn.");
                if (_pending.Count >= 32 || _accepted >= 256 || _characters + amendment.Text.Length > 65_536)
                    throw new InvalidOperationException("The run amendment queue is full; wait for a safe boundary.");
            }

            await persist().ConfigureAwait(false);
            lock (_gate)
            {
                _pending.Enqueue(amendment);
                _characters += amendment.Text.Length;
                _accepted++;
                _amendmentSignal.Cancel();
            }
        }
        finally
        {
            _submissionGate.Release();
        }
    }

    internal IReadOnlyList<RunAmendment> Drain()
    {
        lock (_gate)
        {
            var batch = _pending.ToArray();
            _pending.Clear();
            _characters = 0;
            if (batch.Length == 0) return batch;

            _retiredSignals.Add(_amendmentSignal);
            _amendmentSignal = new CancellationTokenSource();
            return batch;
        }
    }

    internal async Task SealAsync()
    {
        await _submissionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                _ended = true;
            }

            _ready.TrySetCanceled();
        }
        finally
        {
            _submissionGate.Release();
        }
    }
}
