namespace TinyHarness.Cli.Services;

/// <summary>
///     单一 CLI 输入协调（PLAN §6：CLI 不同时以多个读取循环争抢 stdin；审批、用户输入与
///     控制命令由单一输入协调机制分发）。本读取器是 stdin 的唯一读出口：同一时刻只允许
///     一个按行读取在途，第二个并发读取立即失败而不是把两次提示的行交错；字符级与整段
///     读取成员一律抛出异常，防止未来的读取路径绕过单一出口。M12.2 中审批提示经它读取；
///     M12.3 的用户输入与控制命令接入同一出口，保持单一读取不变量。
///     Single CLI input coordination (PLAN §6: the CLI must not run competing stdin read
///     loops; approvals, user input and control commands are dispatched by one input
///     coordinator). This reader is the single stdin outlet: only one line read may be in
///     flight at a time, and a second concurrent read fails immediately instead of
///     interleaving lines across prompts; every character-level or whole-text read member
///     throws, so no future input path can bypass the single outlet. M12.2 routes approval
///     prompts through it; the M12.3 user input and control commands join the same outlet,
///     keeping the single-reader invariant.
/// </summary>
internal sealed class ExclusiveInputReader : TextReader
{
    private readonly TextReader _inner;

    private int _reading;

    public ExclusiveInputReader(TextReader inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <summary>
    ///     读取一行；已有在途读取时抛出说明性异常。读取结束后释放独占位。
    ///     Reads one line; throws an explanatory exception when another read is in
    ///     flight. The exclusive slot is released when the read ends.
    /// </summary>
    public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _reading, 1, 0) != 0)
            throw AlreadyReading();

        try
        {
            return await _inner.ReadLineAsync(cancellationToken);
        }
        finally
        {
            Volatile.Write(ref _reading, 0);
        }
    }

    public override Task<string?> ReadLineAsync() => ReadLineAsync(CancellationToken.None).AsTask();

    public override string? ReadLine()
    {
        if (Interlocked.CompareExchange(ref _reading, 1, 0) != 0)
            throw AlreadyReading();

        try
        {
            return _inner.ReadLine();
        }
        finally
        {
            Volatile.Write(ref _reading, 0);
        }
    }

    /// <summary>按字符与整段读取被拒绝：本出口只分发按行读取。Character and whole-text reads are rejected: this outlet dispatches line reads only.</summary>
    public override int Peek()
    {
        throw SingleOutletViolation();
    }

    public override int Read()
    {
        throw SingleOutletViolation();
    }

    public override int Read(char[] buffer, int index, int count)
    {
        throw SingleOutletViolation();
    }

    public override int Read(Span<char> buffer)
    {
        throw SingleOutletViolation();
    }

    public override int ReadBlock(char[] buffer, int index, int count)
    {
        throw SingleOutletViolation();
    }

    public override int ReadBlock(Span<char> buffer)
    {
        throw SingleOutletViolation();
    }

    public override string ReadToEnd()
    {
        throw SingleOutletViolation();
    }

    public override Task<string> ReadToEndAsync()
    {
        throw SingleOutletViolation();
    }

    public override Task<string> ReadToEndAsync(CancellationToken cancellationToken)
    {
        throw SingleOutletViolation();
    }

    public override Task<int> ReadAsync(char[] buffer, int index, int count)
    {
        throw SingleOutletViolation();
    }

    public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
    {
        throw SingleOutletViolation();
    }

    public override Task<int> ReadBlockAsync(char[] buffer, int index, int count)
    {
        throw SingleOutletViolation();
    }

    public override ValueTask<int> ReadBlockAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
    {
        throw SingleOutletViolation();
    }

    private static InvalidOperationException AlreadyReading() =>
        new("stdin is already being read by another prompt; this CLI dispatches all input " +
            "through a single reader (PLAN §6).");

    private static InvalidOperationException SingleOutletViolation() =>
        new("Only line reads are dispatched through this outlet; every other TextReader " +
            "read member is rejected so no input path bypasses the single reader (PLAN §6).");

    /// <summary>
    ///     不关闭被包装的读取器：包装 <see cref="Console.In" /> 时关闭会终止进程的标准输入。Does not close the wrapped reader: closing a wrapped
    ///     <see cref="Console.In" /> would end the process's stdin.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
    }
}
