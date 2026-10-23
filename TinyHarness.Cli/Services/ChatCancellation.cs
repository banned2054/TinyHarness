namespace TinyHarness.Cli.Services;

/// <summary>
///     chat 会话的 Ctrl+C 协调（PLAN §6：取消使用既有清理并把控制入口与展示分离）。处理器始终
///     阻止默认进程终止，再按当前状态分发：回合执行中取消该回合（运行以 Cancelled 终态结束，
///     会话继续，之后可发起新回合——不重启旧运行）；空闲等待输入时结束整个会话（退出码 130）。
///     生产环境注册 <see cref="Console.CancelKeyPress" />；测试经 <see cref="RaiseCancel" /> 驱动
///     同一决策路径。
///     Ctrl+C coordination for the chat session (PLAN §6: cancellation reuses the existing
///     cleanup and control stays separate from display). The handler always suppresses the
///     default process termination and dispatches by state: during an active turn it cancels
///     that turn (the run ends with a Cancelled terminal, the session continues and a new turn
///     may be started — the old run is never restarted); while idle it ends the whole session
///     (exit code 130). Production registers <see cref="Console.CancelKeyPress" />; tests drive
///     the same decision path through <see cref="RaiseCancel" />.
/// </summary>
internal sealed class ChatCancellation : IDisposable
{
    private readonly ConsoleCancelEventHandler _handler;
    private readonly Lock                      _inputGate = new();
    private readonly bool                      _registerConsole;
    private readonly CancellationTokenSource   _session = new();
    private          long                      _inputInterruptVersion;
    private TaskCompletionSource _inputInterrupted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Invariant: _turnActive == 1 implies _turn is set (until the dispose race, whose
    // ObjectDisposedException is swallowed in RaiseCancel). The flag is written with
    // release semantics after _turn and read with acquire semantics before it, so the
    // handler thread always observes a consistent pair. BeginTurn also bumps
    // _turnGeneration after publishing _turn: RaiseCancel samples the generation before
    // its state reads and re-checks it before acting, so a keypress that raced a turn
    // teardown never cancels the next turn.
    private CancellationTokenSource? _turn;

    private int _turnActive;
    private int _turnGeneration;

    public ChatCancellation(bool registerConsole = true)
    {
        _registerConsole = registerConsole;
        _handler         = OnCancelKey;
        if (registerConsole) Console.CancelKeyPress += _handler;
    }

    /// <summary>会话生命周期令牌；空闲 Ctrl+C 使其取消。The session-lifetime token; idle Ctrl+C cancels it.</summary>
    public CancellationToken SessionToken => _session.Token;

    /// <summary>会话是否已被请求结束。Whether the session has been asked to end.</summary>
    public bool IsSessionCancelled => _session.IsCancellationRequested;

    internal long InputInterruptVersion
    {
        get
        {
            lock (_inputGate)
            {
                return _inputInterruptVersion;
            }
        }
    }

    public void Dispose()
    {
        if (_registerConsole) Console.CancelKeyPress -= _handler;
        _session.Dispose();
    }

    /// <summary>
    ///     <para>等待指定读取开始后的控制键处理完成；不消费信号，允许读取完成与按键回调交错。</para>
    ///     <para>
    ///         Waits for control-key processing after a read began without consuming the signal, allowing read/callback
    ///         races.
    ///     </para>
    /// </summary>
    internal Task WaitForInputInterruptAsync(long version)
    {
        lock (_inputGate)
        {
            return version != _inputInterruptVersion ? Task.CompletedTask : _inputInterrupted.Task;
        }
    }

    /// <summary>
    ///     标记一个回合开始并返回其取消令牌：链接会话令牌与外部令牌，Ctrl+C 在活动状态下
    ///     取消的就是它。每个回合恰好一次 <see cref="BeginTurn" /> 与一次 <see cref="EndTurn" />。
    ///     Marks a turn as active and returns its cancellation token: linked to the session and
    ///     external tokens, and the one active-state Ctrl+C cancels. Pair every
    ///     <see cref="BeginTurn" /> with exactly one <see cref="EndTurn" />.
    /// </summary>
    public CancellationToken BeginTurn(CancellationToken externalToken)
    {
        var turn = CancellationTokenSource.CreateLinkedTokenSource(_session.Token, externalToken);
        _turn = turn;
        Interlocked.Increment(ref _turnGeneration);
        Volatile.Write(ref _turnActive, 1);
        return turn.Token;
    }

    /// <summary>标记回合结束并释放其取消源。Marks the turn inactive and releases its cancellation source.</summary>
    public void EndTurn()
    {
        Volatile.Write(ref _turnActive, 0);
        var turn = Interlocked.Exchange(ref _turn, null);
        turn?.Dispose();
    }

    /// <summary>
    ///     应用一次 Ctrl+C 决策：回合活动时取消该回合，否则取消会话。先采样回合代数、行动前
    ///     复查：一次与回合收尾交错的按键不得取消到下一个回合；被取消的回合已自行结束时，
    ///     本次决策作废，留给下一次按键。
    ///     Applies one Ctrl+C decision: cancel the active turn when one is running, otherwise
    ///     cancel the session. The turn generation is sampled first and re-checked before
    ///     acting: a keypress that raced a turn teardown neither cancels the next turn nor
    ///     acts on stale state — that decision is left to the next keypress. Shared by the
    ///     console handler and tests.
    /// </summary>
    public void RaiseCancel()
    {
        try
        {
            CancelCurrentScope();
        }
        finally
        {
            // 先完成回合/会话取消，再允许输入端重读，避免退出时又留下新的 stdin 读取。
            lock (_inputGate)
            {
                _inputInterruptVersion++;
                _inputInterrupted.TrySetResult();
                _inputInterrupted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    private void CancelCurrentScope()
    {
        var generation = Volatile.Read(ref _turnGeneration);
        if (Volatile.Read(ref _turnActive) == 1)
        {
            var turn = _turn;
            if (turn is null) return;

            if (Volatile.Read(ref _turnGeneration) != generation) return;

            try
            {
                turn.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The turn ended concurrently with the keypress; there is nothing left to
                // cancel and the session decision stays with the next keypress. This is a
                // teardown race, not a swallowed failure.
            }
        }
        else
        {
            _session.Cancel();
        }
    }

    private void OnCancelKey(object? sender, ConsoleCancelEventArgs args)
    {
        // Never let the default handler terminate the process: the session decides —
        // cancel the turn, or end the session through its own shutdown path.
        args.Cancel = true;
        RaiseCancel();
    }
}
