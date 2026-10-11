using TinyHarness.Cli.Services;

namespace TinyHarness.Tests;

/// <summary>
///     单一 stdin 协调契约：同一时刻只允许一个读取在途，第二个并发读取立即失败；顺序
///     读取不受影响；被包装的读取器不被关闭。
///     The single stdin coordination contract: only one read may be in flight, a second
///     concurrent read fails immediately, sequential reads are unaffected, and the
///     wrapped reader is never closed.
/// </summary>
public class ExclusiveInputReaderTests
{
    [Fact]
    public async Task SecondConcurrentRead_ThrowsInsteadOfInterleaving()
    {
        using var gate   = new ManualResetEventSlim(false);
        using var inner  = new BlockingReader(gate);
        using var reader = new ExclusiveInputReader(inner);

        var first = reader.ReadLineAsync(CancellationToken.None);
        var second =
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                                                                    await reader.ReadLineAsync(CancellationToken.None));

        Assert.Contains("single reader", second.Message);
        gate.Set();
        Assert.Null(await first);
    }

    [Fact]
    public async Task SyncReadLine_WhileAsyncReadInFlight_Throws()
    {
        using var gate   = new ManualResetEventSlim(false);
        using var inner  = new BlockingReader(gate);
        using var reader = new ExclusiveInputReader(inner);

        var first = reader.ReadLineAsync(CancellationToken.None);
        Assert.Throws<InvalidOperationException>(reader.ReadLine);

        gate.Set();
        Assert.Null(await first);
    }

    [Fact]
    public void CharacterAndWholeTextReads_AreRejected()
    {
        using var reader = new ExclusiveInputReader(new StringReader("one"));

        // The outlet dispatches line reads only; anything else would bypass the
        // single-reader invariant and must fail closed.
        Assert.Throws<InvalidOperationException>(() => reader.Peek());
        Assert.Throws<InvalidOperationException>(() => reader.Read());
        Assert.Throws<InvalidOperationException>(() => reader.ReadToEnd());
        Assert.Throws<InvalidOperationException>(() => reader.Read(new char[4], 0, 4));
    }

    [Fact]
    public async Task SequentialReads_AreUnaffected()
    {
        using var reader = new ExclusiveInputReader(new StringReader("one\ntwo"));

        Assert.Equal("one", await reader.ReadLineAsync(CancellationToken.None));
        Assert.Equal("two", await reader.ReadLineAsync(CancellationToken.None));
        Assert.Null(await reader.ReadLineAsync(CancellationToken.None));
    }

    [Fact]
    public void WrappedReader_IsNotClosedByDisposal()
    {
        var inner = new TrackedReader();
        using (var wrapper = new ExclusiveInputReader(inner))
        {
            Assert.False(inner.Closed);
        }

        // Disposing the wrapper must not close the wrapped reader (Console.In).
        Assert.False(inner.Closed);
    }

    private sealed class BlockingReader(ManualResetEventSlim gate) : TextReader
    {
        // The wait must not complete synchronously on the caller's thread, or the only
        // test thread would block inside this call and never reach gate.Set().
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            await Task.Run(() => gate.Wait(cancellationToken));
            return null;
        }
    }

    private sealed class TrackedReader : TextReader
    {
        public bool Closed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Closed = true;
        }
    }
}
