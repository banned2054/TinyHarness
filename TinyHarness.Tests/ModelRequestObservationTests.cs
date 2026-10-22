using System.Runtime.CompilerServices;
using System.Text.Json;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Agent;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

public sealed class ModelRequestObservationTests
{
    private static AgentOptions Options() => new()
    {
        Model                     = "offline",
        MaxAgentSteps             = 4,
        DefaultToolTimeoutSeconds = 30
    };

    /// <summary>
    ///     <para>首次阶段有序且仅发布一次；推理条目完整文本不重复计数，子计数不增加总预算。</para>
    ///     <para>
    ///         First-phase notices are ordered and unique; full reasoning items are not double-counted and subsets do not
    ///         inflate budgets.
    ///     </para>
    /// </summary>
    [TestCase(null)]
    [TestCase(0)]
    [TestCase(80)]
    public async Task TracksPhasesAndReportedTokenSubsets(int? cached)
    {
        var model = new FakeChatClient();
        model.Enqueue([
            new ChatStreamEvent { Kind = ChatStreamEventKind.ResponseStarted },
            new ChatStreamEvent { Kind = ChatStreamEventKind.ReasoningDelta, ReasoningDelta = "private-thought" },
            new ChatStreamEvent
            {
                Kind                   = ChatStreamEventKind.ReasoningItem, ReasoningDelta = "private-thought",
                ReasoningProtectedData = "opaque", ReasoningItemId                         = "r1"
            },
            new ChatStreamEvent { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = "done" },
            new ChatStreamEvent
            {
                Kind = ChatStreamEventKind.End, FinishReason = "stop", InputTokens = 100, OutputTokens = 20,
                CachedInputTokens = cached, ReasoningTokens = 7
            }
        ]);
        var sink = new TestHostEventSink();
        var loop = new AgentLoop(model, new ToolRegistry([]), Options(), null, null, events : sink);

        var result = await loop.RunAsync("sys", "go", default);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(result.Budget!.AccountedTokens, Is.EqualTo(120));
        var activities = sink.Events.Where(e => e.Kind == HostEventKind.ModelRequestActivity)
                             .Select(e => (ModelRequestProgress)e.Payload!).ToArray();
        Assert.That(activities.Select(p => p.Phase), Is.EqualTo([
            ModelRequestPhase.ReceivingResponse, ModelRequestPhase.ReceivingReasoning, ModelRequestPhase.ReceivingText
        ]));
        var final =
            (ModelRequestProgress)sink.Events.Single(e => e.Kind == HostEventKind.ModelRequestFinished).Payload!;
        Assert.That(final.FirstStreamEventMilliseconds, Is.Not.Null);
        Assert.That(final.FirstReasoningMilliseconds,
                    Is.GreaterThanOrEqualTo(final.FirstStreamEventMilliseconds!.Value));
        Assert.That(final.FirstTextMilliseconds, Is.GreaterThanOrEqualTo(final.FirstReasoningMilliseconds!.Value));
        Assert.That(final.ElapsedMilliseconds, Is.GreaterThanOrEqualTo(final.LastStreamEventMilliseconds!.Value));
        Assert.That(final.ReasoningCharacters, Is.EqualTo("private-thought".Length));
        Assert.That(final.TextCharacters, Is.EqualTo(4));
        Assert.That(final.InputTokens, Is.EqualTo(100));
        Assert.That(final.CachedInputTokens, Is.EqualTo(cached));
        Assert.That(final.ReasoningTokens, Is.EqualTo(7));
        Assert.That(final.InputMessageCount, Is.EqualTo(2));
        Assert.That(final.EstimatedInputTokens, Is.GreaterThan(0));
        var usage = (ModelUsage)sink.Events.Single(e => e.Kind == HostEventKind.ModelUsage).Payload!;
        Assert.That(usage.CachedInputTokens, Is.EqualTo(cached));
        Assert.That(usage.ReasoningTokens, Is.EqualTo(7));
        Assert.That(sink.Events.Any(e => e.Payload is string text &&
                                         text.Contains("private-thought", StringComparison.Ordinal)),
                    Is.False);
    }

    /// <summary>
    ///     <para>合成 End 不产生首个响应时间，缺失缓存信息保持 null。</para>
    ///     <para>A synthetic End does not create a first-response time, and missing cache information stays null.</para>
    /// </summary>
    [Test]
    public async Task EmptySdkStreamDoesNotInventResponseTiming()
    {
        var model = new FakeChatClient();
        model.Enqueue([new ChatStreamEvent { Kind = ChatStreamEventKind.End }]);
        var sink   = new TestHostEventSink();
        var loop   = new AgentLoop(model, new ToolRegistry([]), Options(), null, null, events : sink);
        var result = await loop.RunAsync("sys", "go", default);
        var final =
            (ModelRequestProgress)sink.Events.Single(e => e.Kind == HostEventKind.ModelRequestFinished).Payload!;
        Assert.That(result.Status, Is.EqualTo(AgentStatus.Failed));
        Assert.That(final.Phase, Is.EqualTo(ModelRequestPhase.WaitingForResponse));
        Assert.That(final.FirstStreamEventMilliseconds, Is.Null);
        Assert.That(final.CachedInputTokens, Is.Null);
    }

    /// <summary>
    ///     <para>取消时将已接收推理的阶段及计数写入同一日志，诊断不包含推理原文。</para>
    ///     <para>
    ///         Cancellation persists received reasoning activity in the same journal without including reasoning text in
    ///         diagnostics.
    ///     </para>
    /// </summary>
    [Test]
    public async Task CancelledReasoningIsPersistedAndOldAuditStillReads()
    {
        using var directory = new TestTempDir();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var sink = new CancelReasoningSink(cancellation.Cancel);
        var identity = new SessionModelIdentity("offline", "https://example.invalid/v1", ChatApiKind.ChatCompletions);
        string id;
        using (var host = new ChatSessionHost(directory.Root, identity, Options(), new ReasoningWaitClient(),
                                              new ToolRegistry([]), "sys", events : sink,
                                              sessionWriterFactory : sessionId =>
                                                  new SessionStoreWriter(directory.Root, sessionId)))
        {
            var result = (await host.SubmitTurnAsync("go", cancellation.Token)).Result!;
            Assert.That(result.Status, Is.EqualTo(AgentStatus.Cancelled));
            Assert.That(host.PersistenceWarning, Is.Null);
            id = host.Session.Id;
        }

        var observations = await ReadObservationsAsync(directory.Root, id);
        var persisted    = observations.Single();
        Assert.That(persisted.Outcome, Is.EqualTo("cancelled"));
        Assert.That(persisted.Phase, Is.EqualTo(ModelRequestPhase.ReceivingReasoning));
        Assert.That(persisted.ReasoningCharacters, Is.EqualTo("private-only".Length));
        Assert.That(persisted.FirstStreamEventMilliseconds, Is.Not.Null);
        Assert.That(persisted.FirstReasoningMilliseconds, Is.Not.Null);
        Assert.That(persisted.FirstTextMilliseconds, Is.Null);
        Assert.That(persisted.CachedInputTokens, Is.Null);
        var restored = (await SessionStoreReader.ReadAsync(directory.Root, id, default))!;
        Assert.That(SessionResume.Restore(restored, directory.Root, identity, "sys").Context.Messages.Count,
                    Is.EqualTo(2));
        var oldAudit = JsonSerializer.Deserialize("""{"runId":"old","kind":"model_usage","inputTokens":42}""",
                                                  PersistenceJsonContext.Default.AuditRecord)!;
        Assert.That(oldAudit.ModelRequest, Is.Null);
        Assert.That(JsonSerializer.Serialize(new TinyHarness.Core.Models.Persistence.AuditRecord { ModelRequest = persisted },
                                             PersistenceJsonContext.Default.AuditRecord),
                    Does.Not.Contain("private-only"));
    }

    /// <summary>
    ///     <para>诊断保存失败阻止新工具，已有模型失败原因仍保留并报告保存警告。</para>
    ///     <para>
    ///         A diagnostic write failure blocks new tools; an existing model failure remains intact with a persistence
    ///         warning.
    ///     </para>
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task ObservationWriteFailureDoesNotStartToolsOrHideModelFailure(bool modelFails)
    {
        var model = new FakeChatClient();
        if (modelFails) model.ThrownError = new IOException("original transport failure");
        else model.Enqueue(FakeChatClient.ToolCall("probe", "{}"));
        var tool = new FakeTool("probe");
        var loop = new AgentLoop(model, new ToolRegistry([tool]), Options(), null, null,
                                 new FailingObservationRecorder());
        var result = await loop.RunAsync("sys", "go", default);
        Assert.That(result.Status, Is.EqualTo(AgentStatus.Failed));
        Assert.That(result.PersistenceWarning, Does.Contain("observation write failure"));
        Assert.That(tool.ExecuteCount, Is.Zero);
        if (modelFails) Assert.That(result.Error, Does.Contain("original transport failure"));
    }

    /// <summary>
    ///     <para>摘要观测保存失败立即结束运行，不转入下一次普通请求。</para>
    ///     <para>A compaction observation write failure ends the run immediately instead of starting a regular request.</para>
    /// </summary>
    [Test]
    public async Task CompactionObservationWriteFailureStopsFurtherRequests()
    {
        var options = Options() with
        {
            Context = new ContextOptions
            {
                ContextWindowTokens = 5000, ReservedOutputTokens = 30, CompactionThresholdTokens = 600
            }
        };
        var context = new ConversationContext(options.Context);
        context.Append(ChatMessage.System("sys"));
        context.Append(ChatMessage.User("original"));
        for (var index = 0; index < 5; index++)
        {
            context.Append(ChatMessage.Assistant("", [new ChatToolCall("old" + index, "probe", "{}")]));
            context.Append(ChatMessage.Tool("probe", "old" + index, new string('x', 1000)));
        }

        var model = new FakeChatClient();
        model.Enqueue(FakeChatClient.Text("Continue the original task."));
        var loop = new AgentLoop(model, new ToolRegistry([]), options, null, null,
                                 new FailingObservationRecorder(), context : context);
        var result = await loop.RunTurnAsync("sys", "continue", default);
        Assert.That(result.Status, Is.EqualTo(AgentStatus.Failed));
        Assert.That(result.PersistenceWarning, Does.Contain("observation write failure"));
        Assert.That(model.Requests, Is.EqualTo(1));
        Assert.That(model.RequestLog.Single().Tools, Is.Null);
    }

    private static async Task<List<ModelRequestProgress>> ReadObservationsAsync(string directory, string id)
    {
        var observations = new List<ModelRequestProgress>();
        foreach (var line in
                 await File.ReadAllLinesAsync(Path.Combine(directory, id + SessionStoreWriter.LogFileSuffix)))
        {
            using var batch = JsonDocument.Parse(line);
            foreach (var record in batch.RootElement.GetProperty("records").EnumerateArray())
                if (record.GetProperty("kind").GetString() == "audit" &&
                    record.GetProperty("payload").Deserialize(PersistenceJsonContext.Default.AuditRecord)
                         ?.ModelRequest is { } progress)
                    observations.Add(progress);
        }

        return observations;
    }

    private sealed class ReasoningWaitClient : IChatCompletionClient
    {
        public async IAsyncEnumerable<ChatStreamEvent> CompleteAsync(
            ChatCompletionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new ChatStreamEvent { Kind = ChatStreamEventKind.ResponseStarted };
            yield return new ChatStreamEvent
                { Kind = ChatStreamEventKind.ReasoningDelta, ReasoningDelta = "private-only" };
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class CancelReasoningSink(Action cancel) : IHostEventSink
    {
        public ValueTask PublishAsync(HostEvent value, CancellationToken cancellationToken)
        {
            if (value is
                {
                    Kind   : HostEventKind.ModelRequestActivity,
                    Payload: ModelRequestProgress { Phase: ModelRequestPhase.ReceivingReasoning }
                }) cancel();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingObservationRecorder : IRunRecorder
    {
        public Task StartAsync(string systemPrompt, string userInput, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RecordPreparedAsync(ToolPreparation preparation, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RecordPermissionAsync(
            ToolPreparation preparation, PermissionDecision decision, string outcome, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RecordResultAsync(
            ToolPreparation preparation, ToolResult result, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RecordCompactionAsync(ContextChange change, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RecordModelUsageAsync(
            int? inputTokens, int? outputTokens, string? finishReason, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RecordModelRequestAsync(ModelRequestProgress progress, CancellationToken cancellationToken) =>
            throw new IOException("observation write failure");

        public Task CompleteAsync(
            AgentResult result, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
