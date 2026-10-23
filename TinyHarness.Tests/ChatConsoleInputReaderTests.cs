using System.Threading.Channels;
using TinyHarness.Cli.Services;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

/// <summary>
///     <para>验证控制键引起的空读取不会结束活动会话，真实 EOF 和空闲取消仍能退出。</para>
///     <para>
///         Verifies that control-key empty reads keep active sessions usable while genuine EOF and idle cancellation
///         still exit.
///     </para>
/// </summary>
public class ChatConsoleInputReaderTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [TestCase(false)]
    [TestCase(true)]
    public async Task TurnInterrupt_RetriesEmptyReadRegardlessOfCallbackOrder(bool emptyReadFirst)
    {
        using var cancellation = new ChatCancellation(false);
        using var input        = new ConsoleInput();
        using var reader       = new ChatConsoleInputReader(input, cancellation, true);
        cancellation.BeginTurn(default);
        var reading = reader.ReadLineAsync(default).AsTask();
        await input.Started.Task.WaitAsync(Timeout);
        if (emptyReadFirst)
        {
            input.Lines.Writer.TryWrite(null);
            await input.ReturnedEmpty.Task.WaitAsync(Timeout);
            await Task.Delay(20);
            cancellation.RaiseCancel();
        }
        else
        {
            cancellation.RaiseCancel();
            input.Lines.Writer.TryWrite(null);
        }

        cancellation.EndTurn();
        input.Lines.Writer.TryWrite("continue in the same session");
        Assert.That(await reading.WaitAsync(Timeout), Is.EqualTo("continue in the same session"));
        Assert.That(cancellation.IsSessionCancelled, Is.False);
        Assert.That(input.Reads, Is.EqualTo(2));
    }

    [Test]
    public async Task RealEofAfterInterrupt_IsNotMistakenForAnotherControlKey()
    {
        using var cancellation = new ChatCancellation(false);
        using var input        = new ConsoleInput();
        using var reader       = new ChatConsoleInputReader(input, cancellation, true);
        cancellation.BeginTurn(default);
        var first = reader.ReadLineAsync(default).AsTask();
        await input.Started.Task.WaitAsync(Timeout);
        cancellation.RaiseCancel();
        cancellation.EndTurn();
        input.Lines.Writer.TryWrite(null);
        input.Lines.Writer.TryWrite("next");
        Assert.That(await first.WaitAsync(Timeout), Is.EqualTo("next"));
        input.Lines.Writer.TryComplete();
        Assert.That(await reader.ReadLineAsync(default), Is.Null);
        Assert.That(input.Reads, Is.EqualTo(3));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task IdleInterrupt_DoesNotStartAReplacementRead(bool emptyReadFirst)
    {
        using var cancellation = new ChatCancellation(false);
        using var input        = new ConsoleInput();
        using var reader       = new ChatConsoleInputReader(input, cancellation, true);
        var       reading      = reader.ReadLineAsync(default).AsTask();
        await input.Started.Task.WaitAsync(Timeout);
        if (emptyReadFirst)
        {
            input.Lines.Writer.TryWrite(null);
            await input.ReturnedEmpty.Task.WaitAsync(Timeout);
            await Task.Delay(20);
            cancellation.RaiseCancel();
        }
        else
        {
            cancellation.RaiseCancel();
            input.Lines.Writer.TryWrite(null);
        }

        Assert.That(await reading.WaitAsync(Timeout), Is.Null);
        Assert.That(cancellation.IsSessionCancelled, Is.True);
        Assert.That(input.Reads, Is.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task EofWithoutControlKey_RemainsEof(bool interactive)
    {
        using var cancellation = new ChatCancellation(false);
        using var input        = new ConsoleInput();
        using var reader       = new ChatConsoleInputReader(input, cancellation, interactive);
        input.Lines.Writer.TryComplete();
        Assert.That(await reader.ReadLineAsync(default), Is.Null);
        Assert.That(input.Reads, Is.EqualTo(1));
    }

    [Test]
    public async Task RedirectedInput_DoesNotRetryEvenWhenTheTurnWasCancelled()
    {
        using var cancellation = new ChatCancellation(false);
        using var input        = new ConsoleInput();
        using var reader       = new ChatConsoleInputReader(input, cancellation, false);
        cancellation.BeginTurn(default);
        var reading = reader.ReadLineAsync(default).AsTask();
        await input.Started.Task.WaitAsync(Timeout);
        cancellation.RaiseCancel();
        cancellation.EndTurn();
        input.Lines.Writer.TryWrite(null);
        Assert.That(await reading.WaitAsync(Timeout), Is.Null);
        Assert.That(input.Reads, Is.EqualTo(1));
    }

    [Test]
    public async Task CancelledRead_StopsRecoveryWithoutConsumingTheNextLine()
    {
        using var cancellation = new ChatCancellation(false);
        using var input        = new ConsoleInput();
        using var reader       = new ChatConsoleInputReader(input, cancellation, true);
        using var stop         = new CancellationTokenSource();
        input.Lines.Writer.TryWrite(null);
        var reading = reader.ReadLineAsync(stop.Token).AsTask();
        await input.ReturnedEmpty.Task.WaitAsync(Timeout);
        stop.Cancel();
        await Assert.ThatAsync(async () => await reading, Throws.InstanceOf<OperationCanceledException>());
        input.Lines.Writer.TryWrite("preserved");
        Assert.That(await reader.ReadLineAsync(default), Is.EqualTo("preserved"));
    }

    [Test]
    public async Task ConsoleLoop_AfterControlKeyEmptyReadAcceptsAnotherTurnAndOnlyExitsAtRealEof()
    {
        var client = new GateChatClient();
        using var host = new ChatSessionHost(Path.GetTempPath(),
                                             new SessionModelIdentity("test", "https://example.invalid",
                                                                      ChatApiKind.ChatCompletions),
                                             new AgentOptions
                                                 { Model = "test", MaxAgentSteps = 8, DefaultToolTimeoutSeconds = 30 },
                                             client, new ToolRegistry([]), "sys");
        using var cancellation = new ChatCancellation(false);
        using var input        = new ConsoleInput();
        using var consoleInput = new ChatConsoleInputReader(input, cancellation, true);
        using var router       = new ChatInputRouter(new ExclusiveInputReader(consoleInput));
        var       output       = new PromptWriter();
        using var console      = new ChatSessionConsole(host, router, output, cancellation);
        input.Lines.Writer.TryWrite("first");
        var       running = console.RunAsync();
        using var timeout = new CancellationTokenSource(Timeout);
        await client.WaitStartedAsync(timeout.Token);
        await input.SecondReadStarted.Task.WaitAsync(Timeout);
        cancellation.RaiseCancel();
        input.Lines.Writer.TryWrite(null);
        await output.SecondPrompt.Task.WaitAsync(Timeout);
        Assert.That(running.IsCompleted, Is.False);
        client.Open();
        input.Lines.Writer.TryWrite("followup");
        await output.ThirdPrompt.Task.WaitAsync(Timeout);
        Assert.That(running.IsCompleted, Is.False);
        Assert.That(host.Session.Turns.Select(turn => turn.Runs.Single().Result!.Status),
                    Is.EqualTo(new[] { AgentStatus.Cancelled, AgentStatus.Completed }));
        input.Lines.Writer.TryComplete();
        Assert.That(await running.WaitAsync(Timeout), Is.Zero);
    }

    private sealed class ConsoleInput : TextReader
    {
        internal Channel<string?>     Lines   { get; } = Channel.CreateUnbounded<string?>();
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource SecondReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReturnedEmpty { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int                  Reads         { get; private set; }

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            Reads++;
            Started.TrySetResult();
            if (Reads == 2) SecondReadStarted.TrySetResult();
            var line = await Lines.Reader.WaitToReadAsync(cancellationToken)
                ? await Lines.Reader.ReadAsync(cancellationToken)
                : null;
            if (line is null) ReturnedEmpty.TrySetResult();
            return line;
        }
    }

    private sealed class PromptWriter : StringWriter
    {
        private  int                  _prompts;
        internal TaskCompletionSource SecondPrompt { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ThirdPrompt  { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task WriteAsync(string? value)
        {
            var written = base.WriteAsync(value);
            if (value == "you> ")
            {
                if (++_prompts == 2) SecondPrompt.TrySetResult();
                if (_prompts   == 3) ThirdPrompt.TrySetResult();
            }

            return written;
        }
    }
}
