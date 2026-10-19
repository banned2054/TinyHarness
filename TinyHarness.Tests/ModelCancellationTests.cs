using TinyHarness.Cli.Services;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.Agent;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

/// <summary>
///     <para>区分调用方取消与模型传输自行中断，验证失败终态、持久化和继续对话。</para>
///     <para>
///         Distinguishes caller cancellation from interrupted model transport, checking failure, persistence and
///         continuation.
///     </para>
/// </summary>
public class ModelCancellationTests
{
    private const string SystemPrompt   = "Inspect this offline test workspace.";
    private const string TimeoutMessage = "The model response stream exceeded its read timeout.";

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task ModelCancellation_WithoutCallerCancellation_ReturnsFailed(
        bool taskCancellation, bool afterPartialResponse)
    {
        var error = taskCancellation
            ? new TaskCanceledException(TimeoutMessage, new TimeoutException("Read deadline elapsed."))
            : new OperationCanceledException(TimeoutMessage);
        var client = InterruptedClient(error, afterPartialResponse);
        var events = new TestHostEventSink();
        var loop   = new AgentLoop(client, new ToolRegistry([]), Options(), null, null, events : events);

        var result = await loop.RunAsync(SystemPrompt, "inspect", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Failed));
        Assert.That(result.Error, Is.EqualTo(TimeoutMessage));
        Assert.That(result.Steps, Is.EqualTo(1));
        Assert.That(result.ToolExecutions, Is.Zero);
        Assert.That(result.FinalMessage, Is.Empty);
        Assert.That(client.Requests, Is.EqualTo(1));
        TestAssert.DoesNotContainAny(message => message.Role == ChatRole.Assistant, loop.History);
        Assert.That(events.Events.Any(item => item.Kind == HostEventKind.AssistantTextDelta),
                    Is.EqualTo(afterPartialResponse));
        Assert.That(events.Events.Single(item => item.Kind == HostEventKind.RunError).Payload,
                    Is.EqualTo(TimeoutMessage));
        Assert.That(((AgentResult)events.Events.Single(item => item.Kind == HostEventKind.RunFinished).Payload!).Status,
                    Is.EqualTo(AgentStatus.Failed));
    }

    [Test]
    public async Task ModelCancellation_DuringIncompleteToolCall_DoesNotExecuteTheTool()
    {
        var client = new FakeChatClient();
        var tool   = new FakeTool("write");
        client.Enqueue(FakeChatClient.ToolCall("write", "{}"));
        client.CancelMidStream = () => throw new TaskCanceledException(TimeoutMessage);
        var loop = new AgentLoop(client, new ToolRegistry([tool]), Options());

        var result = await loop.RunAsync(SystemPrompt, "write", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Failed));
        Assert.That(result.Error, Is.EqualTo(TimeoutMessage));
        Assert.That(tool.ExecuteCount, Is.Zero);
        Assert.That(result.ToolExecutions, Is.Zero);
        Assert.That(client.Requests, Is.EqualTo(1));
        TestAssert.DoesNotContainAny(message => message.Role is ChatRole.Assistant or ChatRole.Tool, loop.History);
    }

    [Test]
    public async Task Session_ModelCancellation_CommitsFailureAndAllowsNextTurn()
    {
        using var workspace = new TestTempDir();
        using var store     = new TestTempDir();
        var       client    = InterruptedClient(new TaskCanceledException(TimeoutMessage), true);
        client.Enqueue(FakeChatClient.Text("Recovered answer."));
        using var host = CreateHost(client, workspace.Root, id => new SessionStoreWriter(store.Root, id));

        var failed = await host.SubmitTurnAsync("first", CancellationToken.None);

        Assert.That(failed.Result!.Status, Is.EqualTo(AgentStatus.Failed));
        Assert.That(failed.Result.Error, Is.EqualTo(TimeoutMessage));
        Assert.That(failed.Result.Steps, Is.EqualTo(1));
        Assert.That(host.InspectionPending, Is.False);
        Assert.That(host.PersistenceWarning, Is.Null);
        var saved    = await SessionStoreReader.ReadAsync(store.Root, host.Session.Id, CancellationToken.None);
        var savedRun = saved!.Turns.Single().Runs.Single();
        Assert.That(savedRun.Status, Is.EqualTo(nameof(AgentStatus.Failed)));
        Assert.That(savedRun.Error, Is.EqualTo(TimeoutMessage));
        TestAssert.DoesNotContainAny(message => message.Role == "Assistant", saved.Conversation!.Messages);

        var recovered = await host.SubmitTurnAsync("second", CancellationToken.None);

        Assert.That(recovered.Result!.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(recovered.Result.FinalMessage, Is.EqualTo("Recovered answer."));
        Assert.That(client.Requests, Is.EqualTo(2));
        TestAssert.DoesNotContainAny(message => message.Role == ChatRole.Assistant, client.RequestLog[1].Messages);
        var continued = await SessionStoreReader.ReadAsync(store.Root, host.Session.Id, CancellationToken.None);
        Assert.That(continued!.Turns.Select(turn => turn.Runs.Single().Status),
                    Is.EqualTo([nameof(AgentStatus.Failed), nameof(AgentStatus.Completed)]));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Session_CancellationOutsideTheLoop_UsesTheCallerToken(bool callerCancelled)
    {
        var       client       = new FakeChatClient();
        using var cancellation = new CancellationTokenSource();
        if (callerCancelled) await cancellation.CancelAsync();
        using var host =
            CreateHost(client, recorderFactory : _ => throw new OperationCanceledException(TimeoutMessage));

        var run = await host.SubmitTurnAsync("inspect", cancellation.Token);

        Assert.That(run.Result!.Status, Is.EqualTo(callerCancelled ? AgentStatus.Cancelled : AgentStatus.Failed));
        Assert.That(run.Result.Error, Is.EqualTo(callerCancelled ? "Run cancelled" : TimeoutMessage));
        Assert.That(host.HasActiveRun, Is.False);
        Assert.That(client.Requests, Is.Zero);
    }

    [Test]
    public async Task Console_ModelCancellation_ReportsFailureAndContinuesTheSession()
    {
        var client = InterruptedClient(new TaskCanceledException(TimeoutMessage), true);
        client.Enqueue(FakeChatClient.Text("Recovered answer."));
        var       output = new StringWriter();
        using var host = CreateHost(client, events : new ConsoleHostEventSink(null, output));
        using var cancellation = new ChatCancellation(false);
        using var console = new ChatSessionConsole(host, new StringReader("first\nsecond\n"), output, cancellation);

        var exitCode = await console.RunAsync();

        Assert.That(exitCode, Is.Zero);
        Assert.That(output.ToString(), Does.Contain("turn 1 finished: failed (steps: 1, tools: 0)"));
        Assert.That(output.ToString(), Does.Contain(TimeoutMessage));
        Assert.That(output.ToString(), Does.Contain("turn 2 finished: completed"));
        Assert.That(output.ToString(), Does.Not.Contain("finished: cancelled"));
        Assert.That(output.ToString(), Does.Contain("Recovered answer."));
        Assert.That(cancellation.IsSessionCancelled, Is.False);
    }

    private static AgentOptions Options() => new()
    {
        Model                     = "test-model",
        MaxAgentSteps             = 5,
        DefaultToolTimeoutSeconds = 30
    };

    private static FakeChatClient InterruptedClient(Exception error, bool afterPartialResponse)
    {
        var client = new FakeChatClient();
        if (afterPartialResponse)
        {
            client.Enqueue(FakeChatClient.Text("Partial answer.", "Not received."));
            client.CancelMidStream = () =>
            {
                client.CancelMidStream = null;
                throw error;
            };
        }
        else
        {
            client.ThrownError = error;
        }

        return client;
    }

    private static ChatSessionHost CreateHost(IChatCompletionClient              client, string? workspaceRoot = null,
                                              Func<string, ISessionStoreWriter>? writerFactory   = null,
                                              IHostEventSink?                    events          = null,
                                              Func<string, IRunRecorder>?        recorderFactory = null) =>
        new(workspaceRoot ?? Path.Combine(Path.GetTempPath(), "tinyharness-model-cancellation-tests"),
            new SessionModelIdentity("test-model", "https://example.invalid/v1", ChatApiKind.ChatCompletions),
            Options(), client, new ToolRegistry([]), SystemPrompt, events : events,
            recorderFactory : recorderFactory, sessionWriterFactory : writerFactory);
}
