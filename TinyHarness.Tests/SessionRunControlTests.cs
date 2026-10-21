using System.Runtime.CompilerServices;
using System.Text.Json;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Agent;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

public class SessionRunControlTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private static readonly SessionModelIdentity Identity =
        new("test", "https://example.invalid", ChatApiKind.ChatCompletions);

    private static AgentOptions Options => new() { Model = "test", MaxAgentSteps = 20, DefaultToolTimeoutSeconds = 30 };

    private static ChatSessionHost Host(IChatCompletionClient model, ITool? tool = null, IHostEventSink? sink = null,
                                        string? directory = null, IApprovalProvider? approver = null,
                                        IReadOnlyDictionary<string, string>? secrets = null) =>
        new(Path.GetTempPath(), Identity, Options, model, new ToolRegistry(tool is null ? [] : [tool]), "sys",
            approver is null ? null : new PermissionEngine(Path.GetTempPath()), approver, sink,
            sessionWriterFactory : directory is null ? null : id => new SessionStoreWriter(directory, id, secrets),
            knownSecrets : secrets);

    [TestCase(false)]
    [TestCase(true)]
    public async Task Pause_BetweenToolsWaitsWithoutInterruptingThePreviousTool(bool cancel)
    {
        var model = new FakeChatClient();
        model.Enqueue(FakeChatClient.ToolCalls("probe", 2, "batch"));
        model.Enqueue(FakeChatClient.Text("done"));
        var              tool   = new FakeTool("probe");
        var              paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ChatSessionHost? host   = null;
        var sink = new CallbackSink(async value =>
        {
            if (value.Kind == HostEventKind.ToolFinished && tool.ExecuteCount == 1) host!.RequestPause();
            if (value.Kind == HostEventKind.RunControlChanged && value.Payload as string == "paused")
                paused.TrySetResult();
            await Task.CompletedTask;
        });
        using (host = Host(model, tool, sink))
        {
            var running = host.SubmitTurnAsync("go", default);
            await paused.Task.WaitAsync(TestTimeout);
            Assert.That(tool.ExecuteCount, Is.EqualTo(1));
            Assert.That(running.IsCompleted, Is.False);
            if (cancel) host.CancelRun();
            else host.ResumeRun();
            var result = (await running.WaitAsync(TestTimeout)).Result!;
            Assert.That(result.Status, Is.EqualTo(cancel ? AgentStatus.Cancelled : AgentStatus.Completed));
            Assert.That(tool.ExecuteCount, Is.EqualTo(cancel ? 1 : 2));
            Assert.That(host.InspectionPending, Is.False);
            Assert.That(host.RequestPause(), Is.False);
        }
    }

    [Test]
    public async Task AmendmentDuringModel_DefersWholeOldRoundAndPersistsExactUserSource()
    {
        using var store  = new TestTempDir();
        var       script = new FakeChatClient();
        script.Enqueue(FakeChatClient.ToolCalls("probe", 3, "old"));
        script.Enqueue(FakeChatClient.Text("new answer"));
        var          model  = new GateModel(script);
        var          tool   = new FakeTool("probe");
        const string secret = "synthetic-secret-m15";
        using var host = Host(model, tool, directory : store.Root,
                              secrets : new Dictionary<string, string> { ["KEY"] = secret });
        var running = host.SubmitTurnAsync("initial", default);
        await model.Entered.Task.WaitAsync(TestTimeout);
        var accepted = await host.QueueAmendmentAsync($"only discuss {secret}", default);
        Assert.That(accepted.CanonicalMessageIndex, Is.Null);
        Assert.That(accepted.DisplayText, Does.Not.Contain(secret));
        model.Release.TrySetResult();
        var result = (await running.WaitAsync(TestTimeout)).Result!;
        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed), result.Error);
        Assert.That(tool.ExecuteCount, Is.Zero);
        Assert.That(result.ToolCalls!.All(call => call.State == ToolCallExecutionState.KnownNotStarted), Is.True);
        Assert.That(script.RequestLog[1].Messages[^1].Content, Is.EqualTo(accepted.Text));
        Assert.DoesNotThrow(() => ChatHistoryInvariants.EnsureCompleteToolGroups(script.RequestLog[1].Messages));
        var snapshot = (await SessionStoreReader.ReadAsync(store.Root, host.Session.Id, default))!;
        Assert.That(snapshot.Amendments.Single().CanonicalMessageIndex, Is.Not.Null);
        var restored = SessionResume.Restore(snapshot, Path.GetTempPath(), Identity, "sys");
        Assert.That(restored.Session.Amendments.Single().Text, Is.EqualTo(accepted.Text));
        Assert.That(restored.Context.Messages.Select(message => message.Content), Does.Contain(accepted.Text));
        var history = await new SessionHistoryReader().ReadPageAsync(store.Root, host.Session.Id, 200, null, default);
        Assert.That(history.Items.Where(item => item.Kind == "user").Select(item => item.Text),
                    Does.Contain(accepted.DisplayText));
        Assert.That(history.Items.Any(item => item.Text?.Contains(secret) == true), Is.False);
        Assert.That(script.Requests, Is.EqualTo(2));
        Assert.That(host.PersistenceWarning, Is.Null);
    }

    [Test]
    public async Task AmendmentAfterFirstTool_PreservesConfirmedFactAndDefersTheRest()
    {
        using var store = new TestTempDir();
        var       model = new FakeChatClient();
        model.Enqueue(FakeChatClient.ToolCalls("probe", 3, "batch"));
        model.Enqueue(FakeChatClient.Text("replanned"));
        var              tool = new FakeTool("probe");
        ChatSessionHost? host = null;
        var sink = new CallbackSink(async value =>
        {
            if (value.Kind == HostEventKind.ToolFinished && tool.ExecuteCount == 1)
                await host!.QueueAmendmentAsync("stop further changes", default);
        });
        using (host = Host(model, tool, sink, store.Root))
        {
            var result = (await host.SubmitTurnAsync("go", default)).Result!;
            Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed), result.Error);
            Assert.That(tool.ExecuteCount, Is.EqualTo(1));
            Assert.That(result.ToolCalls!.Select(call => call.State), Is.EqualTo(new[]
            {
                ToolCallExecutionState.ConfirmedResult, ToolCallExecutionState.KnownNotStarted,
                ToolCallExecutionState.KnownNotStarted
            }));
            Assert.That(host.InspectionPending, Is.False);
            var snapshot = (await SessionStoreReader.ReadAsync(store.Root, host.Session.Id, default))!;
            Assert.That(snapshot.Turns.Single().Runs.Single().ToolCalls!.Count, Is.EqualTo(3));
            Assert.That(snapshot.Amendments.Single().CanonicalMessageIndex, Is.Not.Null);
            Assert.That(host.PersistenceWarning, Is.Null);
        }
    }

    [Test]
    public async Task AmendmentDuringApproval_InterruptsWaitingApprovalWithoutAllowingTheTool()
    {
        using var store = new TestTempDir();
        var       model = new FakeChatClient();
        model.Enqueue(FakeChatClient.ToolCall("probe", "{}"));
        model.Enqueue(FakeChatClient.Text("discussion"));
        var       approver = new BlockingApprover();
        var       tool     = new FakeTool("probe");
        using var host     = Host(model, tool, directory : store.Root, approver : approver);
        var       running  = host.SubmitTurnAsync("go", default);
        await approver.Entered.Task.WaitAsync(TestTimeout);
        await host.QueueAmendmentAsync("do not modify", default);
        var result = (await running.WaitAsync(TestTimeout)).Result!;
        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed), result.Error);
        Assert.That(tool.ExecuteCount, Is.Zero);
        Assert.That(result.ToolCalls!.Single().State, Is.EqualTo(ToolCallExecutionState.KnownNotStarted));
        Assert.That(host.PersistenceWarning, Is.Null);
        Assert.That((await SessionStoreReader.ReadAsync(store.Root, host.Session.Id, default))!.Amendments.Count,
                    Is.EqualTo(1));
    }

    [Test]
    public async Task Amendment_ClearsRunScopedAllowsBeforeReplanning()
    {
        var model = new FakeChatClient();
        model.Enqueue(FakeChatClient.ToolCall("probe", "{}", "old"));
        model.Enqueue(FakeChatClient.ToolCall("probe", "{}", "new"));
        model.Enqueue(FakeChatClient.Text("done"));
        var              approver = new CountingApprover();
        var              tool     = new FakeTool("probe");
        ChatSessionHost? host     = null;
        var              added    = false;
        var sink = new CallbackSink(async value =>
        {
            if (!added && value.Kind == HostEventKind.ApprovalResolved)
            {
                added = true;
                await host!.QueueAmendmentAsync("replan with the new requirement", default);
            }
        });
        using (host = Host(model, tool, sink, approver : approver))
        {
            Assert.That((await host.SubmitTurnAsync("go", default)).Result!.Status, Is.EqualTo(AgentStatus.Completed));
            Assert.That(approver.Calls, Is.EqualTo(2));
            Assert.That(tool.ExecuteCount, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Queue_IsBoundedAndUnappliedInputsRemainInspectableAfterCancellation()
    {
        using var store  = new TestTempDir();
        var       script = new FakeChatClient();
        var       model  = new GateModel(script);
        string    id;
        using (var host = Host(model, directory : store.Root))
        {
            id = host.Session.Id;
            var running = host.SubmitTurnAsync("go", default);
            await model.Entered.Task.WaitAsync(TestTimeout);
            for (var index = 0; index < 32; index++) await host.QueueAmendmentAsync($"amendment {index}", default);
            await TestAssert.ThrowsAsync<InvalidOperationException>(() => host.QueueAmendmentAsync("one too many",
                                                                        default));
            host.CancelRun();
            Assert.That((await running.WaitAsync(TestTimeout)).Result!.Status, Is.EqualTo(AgentStatus.Cancelled));
        }

        var snapshot = (await SessionStoreReader.ReadAsync(store.Root, id, default))!;
        Assert.That(snapshot.Amendments.Count, Is.EqualTo(32));
        Assert.That(snapshot.Amendments.All(item => item.CanonicalMessageIndex is null), Is.True);
        var restored = SessionResume.Restore(snapshot, Path.GetTempPath(), Identity, "sys");
        Assert.That(restored.Context.Messages.Count, Is.EqualTo(2));
        using var restoredHost = new ChatSessionHost(restored.Session, restored.Context, Options, new FakeChatClient(),
                                                     new ToolRegistry([]), "sys");
        Assert.That(restoredHost.PauseRequested, Is.False);
        Assert.That(restoredHost.CancelRun(), Is.False);
    }

    [Test]
    public async Task FailedAmendmentCommit_StopsBeforeFurtherModelRequestsOrTools()
    {
        using var store  = new TestTempDir();
        var       script = new FakeChatClient();
        script.Enqueue(FakeChatClient.ToolCall("probe", "{}"));
        var model = new GateModel(script);
        var tool  = new FakeTool("probe");
        using var host = new ChatSessionHost(Path.GetTempPath(), Identity, Options, model, new ToolRegistry([tool]),
                                             "sys",
                                             sessionWriterFactory : id => new SessionStoreWriter(store.Root, id)
                                             {
                                                 InjectWriteFault = (batch, phase) =>
                                                 {
                                                     if (phase == "before-write" &&
                                                         batch.Records.Any(record => record.Kind == "run-amendment" &&
                                                                               record.Payload
                                                                                  .GetProperty("canonicalMessageIndex")
                                                                                  .ValueKind == JsonValueKind.Number))
                                                         throw
                                                             new IOException("injected amendment application failure");
                                                 }
                                             });
        var running = host.SubmitTurnAsync("go", default);
        await model.Entered.Task.WaitAsync(TestTimeout);
        await host.QueueAmendmentAsync("only discuss", default);
        model.Release.TrySetResult();
        var result = (await running.WaitAsync(TestTimeout)).Result!;
        Assert.That(result.Status, Is.EqualTo(AgentStatus.Failed));
        Assert.That(host.PersistenceWarning, Is.Not.Null);
        Assert.That(tool.ExecuteCount, Is.Zero);
        Assert.That(script.Requests, Is.EqualTo(1));
        var snapshot = (await SessionStoreReader.ReadAsync(store.Root, host.Session.Id, default))!;
        Assert.That(snapshot.Amendments.Single().CanonicalMessageIndex, Is.Null);
        Assert.That(snapshot.Conversation!.Messages.Select(message => message.Content),
                    Does.Not.Contain("only discuss"));
    }

    private sealed class CallbackSink(Func<HostEvent, Task> action) : IHostEventSink
    {
        public async ValueTask PublishAsync(HostEvent value, CancellationToken cancellationToken) =>
            await action(value);
    }

    private sealed class GateModel(IChatCompletionClient inner) : IChatCompletionClient
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<ChatStreamEvent> CompleteAsync(
            ChatCompletionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (!Entered.Task.IsCompleted)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }

            await foreach (var item in inner.CompleteAsync(request, cancellationToken)) yield return item;
        }
    }

    private sealed class BlockingApprover : IApprovalProvider
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ApprovalAction> PromptAsync(ToolPreparation preparation, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return ApprovalAction.Deny;
        }
    }

    private sealed class CountingApprover : IApprovalProvider
    {
        internal int Calls { get; private set; }

        public Task<ApprovalAction> PromptAsync(ToolPreparation preparation, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(ApprovalAction.AllowSession);
        }
    }
}
