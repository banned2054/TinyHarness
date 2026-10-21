using System.Text.Json;
using System.Runtime.CompilerServices;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

public class SessionWorkItemTests
{
    private static readonly SessionModelIdentity Identity =
        new("test", "https://example.invalid", ChatApiKind.ChatCompletions);

    private static AgentOptions Options => new()
        { Model = "test", MaxAgentSteps = 10, DefaultToolTimeoutSeconds = 30, EnableWorkItems = true };

    [Test]
    public async Task WorkItems_ListProvidesUsableCanonicalEvidenceReferences()
    {
        using var store = new TestTempDir();
        var       model = new FakeChatClient();
        var       tool  = new FakeTool("probe");
        model.Enqueue(FakeChatClient.ToolCall("probe", "{}"));
        model.Enqueue(FakeChatClient.ToolCall("work_items", """{"action":"list"}"""));
        model.Enqueue(FakeChatClient.ToolCall("work_items",
                                              """{"action":"update","title":"inspect tool fact","status":"completed","evidenceMessageIndexes":[3]}"""));
        model.Enqueue(FakeChatClient.Text("claim and reference saved"));
        using var host = new ChatSessionHost(Path.GetTempPath(), Identity, Options, model, new ToolRegistry([tool]),
                                             "sys",
                                             sessionWriterFactory : id => new SessionStoreWriter(store.Root, id));
        var result = (await host.SubmitTurnAsync("inspect", default)).Result!;
        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed), result.Error);
        Assert.That(model.RequestLog[2].Messages.Last(message => message.Role == ChatRole.Tool).Content,
                    Does.Contain("3: tool probe").And.Contain("not verified outcomes"));
        Assert.That(host.Session.WorkItems.Single().EvidenceMessageIndexes, Is.EqualTo(new[] { 3 }));
        var snapshot = (await SessionStoreReader.ReadAsync(store.Root, host.Session.Id, default))!;
        Assert.That(snapshot.WorkItems.Single().EvidenceMessageIndexes, Is.EqualTo(new[] { 3 }));
        Assert.That(host.PersistenceWarning, Is.Null);
    }

    [Test]
    public async Task WorkItems_AreIndependentOfCompactedSummariesAndRestoreUnchanged()
    {
        using var store = new TestTempDir();
        var       model = new CompactingModel();
        var options = Options with
        {
            Context = new ContextOptions
                { ContextWindowTokens = 3000, ReservedOutputTokens = 100, CompactionThresholdTokens = 2200 }
        };
        using var host = new ChatSessionHost(Path.GetTempPath(), Identity, options, model, new ToolRegistry([]), "sys",
                                             sessionWriterFactory : id => new SessionStoreWriter(store.Root, id));
        await host.SubmitTurnAsync("track the original goal and keep the API stable", default);
        var original = host.Session.WorkItems.Single();
        for (var turn = 0; turn < 15; turn++)
        {
            var result = (await host.SubmitTurnAsync($"continue discussion {turn}", default)).Result!;
            Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed), result.Error);
        }

        Assert.That(model.Summaries, Is.GreaterThan(0));
        Assert.That(host.Session.WorkItems.Single(), Is.EqualTo(original));
        var snapshot = (await SessionStoreReader.ReadAsync(store.Root, host.Session.Id, default))!;
        Assert.That(snapshot.Conversation!.HasState, Is.True);
        var restored = SessionResume.Restore(snapshot, Path.GetTempPath(), Identity, "sys", options.Context);
        Assert.That(restored.Session.WorkItems.Single().Id, Is.EqualTo(original.Id));
        Assert.That(restored.Session.WorkItems.Single().Title, Is.EqualTo(original.Title));
        Assert.That(restored.Session.WorkItems.Single().Status, Is.EqualTo(original.Status));
        Assert.That(host.PersistenceWarning, Is.Null);
    }

    [Test]
    public async Task WorkItems_PreserveStableIdentitySourceAndClaimsAcrossRestart()
    {
        using var store = new TestTempDir();
        var       model = new FakeChatClient();
        model.Enqueue(FakeChatClient.ToolCall("work_items",
                                              """{"action":"update","title":"keep the public API stable","status":"in_progress"}"""));
        model.Enqueue(FakeChatClient.Text("work tracked"));
        string          id;
        SessionWorkItem created;
        using (var host = new ChatSessionHost(Path.GetTempPath(), Identity, Options, model, new ToolRegistry([]), "sys",
                                              sessionWriterFactory : sessionId =>
                                                  new SessionStoreWriter(store.Root, sessionId)))
        {
            var first = await host.SubmitTurnAsync("implement the change", default);
            Assert.That(first.Result!.Status, Is.EqualTo(AgentStatus.Completed), first.Result.Error);
            created = host.Session.WorkItems.Single();
            Assert.That(created.SourceTurnId, Is.EqualTo(first.TurnId));
            Assert.That(created.Source, Is.EqualTo("model"));
            model.Enqueue(FakeChatClient.ToolCall("work_items",
                                                  $$"""{"action":"update","id":"{{created.Id}}","title":"keep the public API stable","status":"completed"}"""));
            model.Enqueue(FakeChatClient.Text("claimed done, tests not run"));
            var second = await host.SubmitTurnAsync("continue", default);
            Assert.That(second.Result!.Status, Is.EqualTo(AgentStatus.Completed), second.Result.Error);
            var item = host.Session.WorkItems.Single();
            Assert.That(item.Id, Is.EqualTo(created.Id));
            Assert.That(item.SourceTurnId, Is.EqualTo(first.TurnId));
            Assert.That(item.UpdatedRunId, Is.EqualTo(second.Id));
            Assert.That(item.Revision, Is.EqualTo(2));
            Assert.That(item.EvidenceMessageIndexes, Is.Empty);
            Assert.That(host.PersistenceWarning, Is.Null);
            id = host.Session.Id;
        }

        using var writer        = await SessionStoreWriter.OpenExistingAsync(store.Root, id, null, default);
        var       restored      = SessionResume.Restore(writer.LoadedSnapshot!, Path.GetTempPath(), Identity, "sys");
        var       restoredModel = new FakeChatClient();
        restoredModel.Enqueue(FakeChatClient.ToolCall("work_items", """{"action":"list"}"""));
        restoredModel.Enqueue(FakeChatClient.Text("historical claim"));
        using var resumed = new ChatSessionHost(restored.Session, restored.Context, Options, restoredModel,
                                                new ToolRegistry([]), "sys", sessionWriter : writer);
        var result = (await resumed.SubmitTurnAsync("what remains?", default)).Result!;
        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed), result.Error);
        var output = restoredModel.RequestLog[1].Messages.Last(message => message.Role == ChatRole.Tool).Content;
        Assert.That(output, Does.Contain(created.Id).And.Contain("claims, not verified"));
        Assert.That(resumed.Session.WorkItems.Single().Status, Is.EqualTo("completed"));
        Assert.That(model.Requests, Is.EqualTo(4));
    }

    [TestCase("{\"action\":\"update\",\"title\":\"claim\",\"status\":\"completed\",\"evidenceMessageIndexes\":[0]}")]
    [TestCase("{\"action\":\"update\",\"title\":\"claim\",\"status\":\"completed\",\"evidenceMessageIndexes\":[999]}")]
    [TestCase("{\"action\":\"update\",\"title\":\"claim\",\"status\":\"verified\"}")]
    [TestCase("{\"action\":\"update\",\"id\":\"00000000000000000000000000000000\",\"title\":\"claim\",\"status\":\"completed\"}")]
    [TestCase("{\"action\":\"update\",\"title\":\"claim\",\"status\":\"completed\",\"source\":\"user\"}")]
    public async Task WorkItems_RejectInventedEvidenceStatusIdentityAndUserAttribution(string args)
    {
        var model = new FakeChatClient();
        model.Enqueue(FakeChatClient.ToolCall("work_items", args));
        model.Enqueue(FakeChatClient.Text("not verified"));
        using var host = new ChatSessionHost(Path.GetTempPath(), Identity, Options, model, new ToolRegistry([]), "sys");
        var       result = (await host.SubmitTurnAsync("inspect", default)).Result!;
        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(host.Session.WorkItems, Is.Empty);
        Assert.That(model.RequestLog[1].Messages.Last(message => message.Role == ChatRole.Tool).Content,
                    Does.Contain("Invalid").Or.Contain("Unknown").Or.Contain("Evidence"));
    }

    [Test]
    public async Task WorkItems_KeepCanonicalTitleAndRedactedInspectionTitle()
    {
        using var    store   = new TestTempDir();
        const string secret  = "synthetic-work-secret";
        var          secrets = new Dictionary<string, string> { ["KEY"] = secret };
        var          model   = new FakeChatClient();
        model.Enqueue(FakeChatClient.ToolCall("work_items",
                                              $$"""{"action":"update","title":"inspect {{secret}}","status":"pending"}"""));
        model.Enqueue(FakeChatClient.Text("tracked"));
        using var host = new ChatSessionHost(Path.GetTempPath(), Identity, Options, model, new ToolRegistry([]), "sys",
                                             sessionWriterFactory : id =>
                                                 new SessionStoreWriter(store.Root, id, secrets),
                                             knownSecrets : secrets);
        await host.SubmitTurnAsync("inspect", default);
        var snapshot = (await SessionStoreReader.ReadAsync(store.Root, host.Session.Id, default))!;
        Assert.That(snapshot.WorkItems.Single().Title, Does.Contain(secret));
        Assert.That(snapshot.WorkItems.Single().DisplayTitle, Does.Not.Contain(secret));
        var history = await new SessionHistoryReader().ReadPageAsync(store.Root, host.Session.Id, 200, null, default);
        Assert.That(history.Items.Any(item => item.Text?.Contains(secret) == true), Is.False);
    }

    [Test]
    public async Task LogReader_RejectsUnknownWorkSchemaWithoutRewritingTheFile()
    {
        using var store = new TestTempDir();
        var       model = new FakeChatClient();
        model.Enqueue(FakeChatClient.ToolCall("work_items",
                                              """{"action":"update","title":"task","status":"pending"}"""));
        model.Enqueue(FakeChatClient.Text("tracked"));
        string id;
        string path;
        using (var host = new ChatSessionHost(Path.GetTempPath(), Identity, Options, model, new ToolRegistry([]), "sys",
                                              sessionWriterFactory : sessionId =>
                                                  new SessionStoreWriter(store.Root, sessionId)))
        {
            await host.SubmitTurnAsync("go", default);
            id   = host.Session.Id;
            path = host.PersistencePath!;
        }

        var altered =
            (await File.ReadAllTextAsync(path)).Replace("tinyharness.work-item/1", "tinyharness.work-item/99",
                                                        StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, altered);
        await TestAssert.ThrowsAsync<SessionStoreException>(() => SessionStoreReader
                                                               .ReadAsync(store.Root, id, default));
        Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo(altered));
    }

    private sealed class CompactingModel : IChatCompletionClient
    {
        private  bool _created;
        internal int  Summaries { get; private set; }

        public async IAsyncEnumerable<ChatStreamEvent> CompleteAsync(
            ChatCompletionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            IReadOnlyList<ChatStreamEvent> events;
            if (request.Tools is null)
            {
                Summaries++;
                events =
                    FakeChatClient
                       .Text("Continue the conversation. Keep current constraints and consult the independent work list.");
            }
            else if (!_created)
            {
                _created = true;
                events = FakeChatClient.ToolCall("work_items",
                                                 """{"action":"update","title":"original goal; keep the API stable","status":"in_progress"}""");
            }
            else
            {
                events = FakeChatClient.Text(new string('x', 1200));
            }

            foreach (var value in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return value;
                await Task.Yield();
            }
        }
    }
}
