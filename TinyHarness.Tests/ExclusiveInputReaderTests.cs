using TinyHarness.Cli.Services;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         单一 stdin 协调契约：同一时刻只允许一个读取在途，第二个并发读取立即失败；顺序
///         读取不受影响；被包装的读取器不被关闭。
///     </para>
///     <para>
///         The single stdin coordination contract: only one read may be in flight, a second
///         concurrent read fails immediately, sequential reads are unaffected, and the
///         wrapped reader is never closed.
///     </para>
/// </summary>
public class ExclusiveInputReaderTests
{
    [Test]
    public async Task SecondConcurrentRead_ThrowsInsteadOfInterleaving()
    {
        using var gate   = new ManualResetEventSlim(false);
        using var inner  = new BlockingReader(gate);
        using var reader = new ExclusiveInputReader(inner);

        var first = reader.ReadLineAsync(CancellationToken.None);
        var second =
            await TestAssert.ThrowsAsync<InvalidOperationException>(async () =>
                                                                        await reader.ReadLineAsync(CancellationToken
                                                                                    .None));

        Assert.That(second.Message, Does.Contain("single reader"));
        gate.Set();
        Assert.That(await first, Is.Null);
    }

    [Test]
    public async Task SyncReadLine_WhileAsyncReadInFlight_Throws()
    {
        using var gate   = new ManualResetEventSlim(false);
        using var inner  = new BlockingReader(gate);
        using var reader = new ExclusiveInputReader(inner);

        var first = reader.ReadLineAsync(CancellationToken.None);
        TestAssert.Throws<InvalidOperationException>(() => reader.ReadLine());

        gate.Set();
        Assert.That(await first, Is.Null);
    }

    [Test]
    public void CharacterAndWholeTextReads_AreRejected()
    {
        using var reader = new ExclusiveInputReader(new StringReader("one"));

        // 出入口只分发按行读取；其他形式会绕过单读取者不变量，必须失败关闭。
        TestAssert.Throws<InvalidOperationException>(() => reader.Peek());
        TestAssert.Throws<InvalidOperationException>(() => reader.Read());
        TestAssert.Throws<InvalidOperationException>(() => reader.ReadToEnd());
        TestAssert.Throws<InvalidOperationException>(() => reader.Read(new char[4], 0, 4));
    }

    [Test]
    public async Task SequentialReads_AreUnaffected()
    {
        using var reader = new ExclusiveInputReader(new StringReader("one\ntwo"));

        Assert.That(await reader.ReadLineAsync(CancellationToken.None), Is.EqualTo("one"));
        Assert.That(await reader.ReadLineAsync(CancellationToken.None), Is.EqualTo("two"));
        Assert.That(await reader.ReadLineAsync(CancellationToken.None), Is.Null);
    }

    [Test]
    public void WrappedReader_IsNotClosedByDisposal()
    {
        var inner = new TrackedReader();
        using (var wrapper = new ExclusiveInputReader(inner))
        {
            Assert.That(inner.Closed, Is.False);
        }

        // 释放包装器不得关闭被包装的读取器（Console.In）。
        Assert.That(inner.Closed, Is.False);
    }

    private sealed class BlockingReader(ManualResetEventSlim gate) : TextReader
    {
        // 等待不得在调用线程上同步完成，否则唯一的测试线程会阻塞在此调用中，
        // 永远到不了 gate.Set()。
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
