using System.Text.Json;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

/// <summary>
///     <para>验证实际宿主压缩日志、恢复和失败原因，不执行真实模型请求。</para>
///     <para>Verifies actual host compaction journals, recovery and failure reasons without real model requests.</para>
/// </summary>
public sealed class CompactionRecoveryTests
{
    private static readonly SessionModelIdentity Identity =
        new("test-model", "https://example.invalid/v1", ChatApiKind.ChatCompletions);

    /// <summary>
    ///     <para>建立可触发压缩的宿主选项。</para>
    ///     <para>Creates host options that trigger compaction.</para>
    /// </summary>
    private static AgentOptions Options(int window = 1000, int threshold = 0) => new()
    {
        Model                     = Identity.ModelId,
        MaxAgentSteps             = 8,
        DefaultToolTimeoutSeconds = 30,
        Context = new ContextOptions
        {
            ContextWindowTokens       = window,
            ReservedOutputTokens      = 30,
            CompactionThresholdTokens = threshold
        }
    };

    [Test]
    public async Task HostWritesOneCheckpointPath_AndResumeDoesNotReplayTools()
    {
        using var workspace = new TestTempDir();
        using var store     = new TestTempDir();
        var       client    = new FakeChatClient();
        var       tool      = new FakeTool("t") { ResultContent = new string('x', 1600) };
        for (var i = 0; i < 3; i++) client.Enqueue(FakeChatClient.ToolCall("t", "{}", "read" + i));
        client.Enqueue(FakeChatClient.Text("Only discuss. Three reads completed. No modification was authorized."));
        client.Enqueue(FakeChatClient.Text("done"));
        string id;
        var    sink = new TestHostEventSink();
        using (var host =
               new ChatSessionHost(workspace.Root, Identity, Options(), client, new ToolRegistry([tool]), "sys",
                                   events : sink,
                                   sessionWriterFactory : value => new SessionStoreWriter(store.Root, value)))
        {
            var run = await host.SubmitTurnAsync("Discuss only; do not modify.", CancellationToken.None);
            Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Completed));
            Assert.That(run.Result.Compactions, Is.EqualTo(1));
            Assert.That(host.PersistenceWarning, Is.Null);
            id = host.Session.Id;
        }

        Assert.That(sink.Compactions().Count, Is.EqualTo(1));
        var log = await File.ReadAllTextAsync(Path.Combine(store.Root, id + SessionStoreWriter.LogFileSuffix));
        Assert.That(log, Does.Contain("\"kind\":\"compaction-checkpoint\""));
        Assert.That(log, Does.Not.Contain("\"kind\":\"checkpoint\""));
        Assert.That(log, Does.Not.Contain("stateJson"));
        using var writer = await SessionStoreWriter.OpenExistingAsync(store.Root, id, null, CancellationToken.None);
        var restored = SessionResume.Restore(writer.LoadedSnapshot!, workspace.Root, Identity, "sys",
                                             Options().Context);
        var savedView = restored.Context.BuildModelView().ToArray();
        var continued = new FakeChatClient();
        continued.Enqueue(FakeChatClient.Text("still only discussing"));
        using var resumed = new ChatSessionHost(restored.Session, restored.Context, Options(), continued,
                                                new ToolRegistry([tool]), "sys", sessionWriter : writer);
        // 恢复时上下文须与固化选择一致；改用大窗口按设计走显式模型选择变更。
        await resumed.ChangeModelAsync(Identity, continued, Options(100_000).Context, null,
                                       CancellationToken.None);
        var next = await resumed.SubmitTurnAsync("Continue discussing.", CancellationToken.None);
        Assert.That(next.Result!.Status, Is.EqualTo(AgentStatus.Completed), next.Result.Error);
        Assert.That(tool.ExecuteCount, Is.EqualTo(3));
        Assert.That(continued.RequestLog.Single().Messages.Take(savedView.Length), Is.EqualTo(savedView));
        Assert.That(restored.Context.State.Summary, Does.Contain("No modification"));
    }

    /// <summary>
    ///     <para>摘要 usage 改变估算校准时，压缩事件仍用同一口径比较前后视图。</para>
    ///     <para>Compares compaction views on one scale when summary usage changes estimator calibration.</para>
    /// </summary>
    [Test]
    public async Task CompactionEvent_UsesTheUpdatedCalibrationForBothViews()
    {
        using var workspace = new TestTempDir();
        var       options   = Options(5000, 600);
        var       context   = new ConversationContext(options.Context);
        context.Append(ChatMessage.System("sys"));
        context.Append(ChatMessage.User("Only discuss."));
        for (var i = 0; i < 5; i++)
        {
            context.Append(ChatMessage.Assistant("", [new ChatToolCall("old" + i, "t", "{}")]));
            context.Append(ChatMessage.Tool("t", "old" + i, new string('x', 1000)));
        }

        const string input = "Keep discussing.";
        var expectedBefore =
            TokenEstimator.EstimateMessages(context.BuildModelView().Append(ChatMessage.User(input))) * 4;
        var client = new FakeChatClient();
        client.Enqueue([
            new ChatStreamEvent
            {
                Kind         = ChatStreamEventKind.ContentDelta,
                ContentDelta = "Only discussion is authorized."
            },
            new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason = "stop", InputTokens = 1_000_000 }
        ]);
        client.Enqueue(FakeChatClient.Text("discussion only"));
        var sink    = new TestHostEventSink();
        var session = new ChatSession(Guid.NewGuid().ToString("N"), workspace.Root, Identity);
        using var host =
            new ChatSessionHost(session, context, options, client, new ToolRegistry([new FakeTool("t")]), "sys",
                                events : sink);
        var run = await host.SubmitTurnAsync(input, CancellationToken.None);
        Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Completed));
        var change = sink.Compactions().Single();
        Assert.That(change.BeforeTokens, Is.EqualTo(expectedBefore));
        Assert.That(change.AfterTokens, Is.LessThan(change.BeforeTokens));
    }

    [TestCase("legacy")]
    [TestCase("unknown-schema")]
    public async Task UnsupportedCheckpointIsRejectedWithoutRewritingItsLog(string kind)
    {
        using var workspace = new TestTempDir();
        using var store     = new TestTempDir();
        var       session   = new ChatSession(Guid.NewGuid().ToString("N"), workspace.Root, Identity);
        var       context   = new ConversationContext();
        context.Append(ChatMessage.System("sys"));
        string path;
        string invalidBatch;
        using (var writer = new SessionStoreWriter(store.Root, session.Id))
        {
            await writer.CommitAsync(session, context, CancellationToken.None);
            using var payload = JsonDocument.Parse(kind == "legacy"
                                                       ? """{"stateJson":"{}","hasState":true,"foldStartMessageIndex":1,"foldEndMessageIndex":2}"""
                                                       : """{"state":{"schema":"unknown","summary":"text","retainedUserMessages":[]},"hasState":true,"foldStartMessageIndex":1,"foldEndMessageIndex":2}""");
            var sequence = writer.CommitSequence + 1;
            var batch = new SessionLogBatch
            {
                Format     = SessionLogFormat.FormatName,
                SessionId  = session.Id,
                Sequence   = sequence,
                BatchId    = $"{session.Id}:{sequence:D20}",
                WrittenUtc = DateTimeOffset.UtcNow,
                Records =
                [
                    new SessionLogRecord($"{session.Id}:{sequence:D20}:0000",
                                         kind == "legacy" ? "checkpoint" : "compaction-checkpoint",
                                         payload.RootElement.Clone())
                ]
            };
            invalidBatch = JsonSerializer.Serialize(batch, SessionLogJsonContext.Default.SessionLogBatch);
            path         = writer.SnapshotPath;
        }

        // 绕过生产 writer 构造磁盘故障样本，不允许应用写入非法记录。
        await File.AppendAllTextAsync(path, invalidBatch + "\n");
        var bytes = await File.ReadAllBytesAsync(path);
        await Assert.ThrowsAsync<SessionStoreException>(async () =>
            await SessionStoreReader.ReadAsync(store.Root, session.Id, CancellationToken.None));
        Assert.That(await File.ReadAllBytesAsync(path), Is.EqualTo(bytes));
    }

    [TestCase("empty", CompactionFailureReason.EmptySummary)]
    [TestCase("truncated", CompactionFailureReason.IncompleteResponse)]
    [TestCase("no-end", CompactionFailureReason.IncompleteResponse)]
    [TestCase("tool-call", CompactionFailureReason.ToolCallsNotAllowed)]
    public async Task FailureReasonIsObservable_AndNoSummaryOrToolIsCommitted(
        string mode, CompactionFailureReason expected)
    {
        using var workspace = new TestTempDir();
        using var store     = new TestTempDir();
        var       options   = Options(5000, 600);
        var       context   = new ConversationContext(options.Context);
        context.Append(ChatMessage.System("sys"));
        context.Append(ChatMessage.User("Only discuss."));
        for (var i = 0; i < 5; i++)
        {
            context.Append(ChatMessage.Assistant("", [new ChatToolCall("old" + i, "t", "{}")]));
            context.Append(ChatMessage.Tool("t", "old" + i, new string('x', 1000)));
        }

        var canonical = context.Messages.ToArray();
        var client    = new FakeChatClient();
        client.Enqueue(mode switch
        {
            "empty" => FakeChatClient.Text("  "),
            "tool-call" => FakeChatClient.ToolCall("t", "{}", "forbidden"),
            "no-end" => [new ChatStreamEvent { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = "unfinished" }],
            _ =>
            [
                new ChatStreamEvent { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = "partial handoff" },
                new ChatStreamEvent
                {
                    Kind         = ChatStreamEventKind.End,
                    FinishReason = "length",
                    InputTokens  = 100,
                    OutputTokens = 5
                }
            ]
        });
        client.Enqueue(FakeChatClient.Text("discussion only"));
        var       tool    = new FakeTool("t");
        var       sink    = new TestHostEventSink();
        var       session = new ChatSession(Guid.NewGuid().ToString("N"), workspace.Root, Identity);
        using var writer  = new SessionStoreWriter(store.Root, session.Id);
        using var host = new ChatSessionHost(session, context, options, client, new ToolRegistry([tool]), "sys",
                                             events : sink, sessionWriter : writer);
        var run = await host.SubmitTurnAsync("Keep discussing.", CancellationToken.None);
        Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(run.Result.Compactions, Is.Zero);
        Assert.That(context.Position.HasState, Is.False);
        Assert.That(context.Messages.Take(canonical.Length), Is.EqualTo(canonical));
        Assert.That(tool.ExecuteCount, Is.Zero);
        Assert.That(sink.Events.Single(item => item.Kind == HostEventKind.CompactionFailed).Payload,
                    Is.EqualTo(expected));
        await using var stream =
            new FileStream(writer.SnapshotPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                           4096, FileOptions.Asynchronous);
        using var reader = new StreamReader(stream);
        Assert.That(await reader.ReadToEndAsync(),
                    Does.Contain("context.compaction-failed").And.Contain(expected.ToString()));
        if (mode == "truncated") Assert.That(run.Result.InputTokens, Is.EqualTo(100));
    }
}
