using System.Text.Json;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

public sealed class SessionTitleTests
{
    [TestCase("  \"修复   会话列表\".  ", "修复 会话列表")]
    [TestCase("“Fix\n session titles!”", "Fix session titles")]
    [TestCase("```Fix titles```", "Fix titles")]
    public void NormalizationRemovesQuotesWhitespaceAndPunctuation(string input, string expected) =>
        Assert.That(SessionTitle.Normalize(input), Is.EqualTo(expected));

    [Test]
    public void UnicodeLengthLimitDoesNotSplitSurrogatePairs()
    {
        var title = SessionTitle.Normalize(new string('中', 35) + "😀更多");
        Assert.That(title, Is.EqualTo(new string('中', 35)      + "😀"));
        Assert.DoesNotThrow(() => new SessionTitle { Name = title, Pending = false }.Validate());
    }

    [Test]
    public async Task GeneratorUsesAnIsolatedBoundedToolFreeRequest()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("\"Fix ABC-123 titles!\""));
        var title = await SessionTitleGenerator.GenerateAsync(client, "title-model", new string('中', 5000), default);
        Assert.That(title, Is.EqualTo("Fix ABC-123 titles"));
        var request = client.RequestLog.Single();
        Assert.That(request.Model, Is.EqualTo("title-model"));
        Assert.That(request.Tools, Is.Null);
        Assert.That(request.ReasoningEffort, Is.Null);
        Assert.That(request.Messages.Select(item => item.Role), Is.EqualTo(new[] { ChatRole.System, ChatRole.User }));
        Assert.That(request.Messages[1].Content, Has.Length.EqualTo(2048));
    }

    [TestCase("length")]
    [TestCase("tool_calls")]
    [TestCase("content_filter")]
    public void AbnormalCompletionIsNotAccepted(string finish)
    {
        var client = new FakeChatClient();
        client.Enqueue([
            new ChatStreamEvent { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = "Partial title" },
            new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason          = finish }
        ]);
        Assert.ThrowsAsync<InvalidDataException>(() =>
                                                     SessionTitleGenerator.GenerateAsync(client, "title-model",
                                                         "question", default));
    }

    [Test]
    public void ToolCallsAndIncompleteStreamsAreRejected()
    {
        var tool = new FakeChatClient();
        tool.Enqueue(FakeChatClient.ToolCall("shell", "{}"));
        Assert.ThrowsAsync<InvalidDataException>(() =>
                                                     SessionTitleGenerator.GenerateAsync(tool, "title-model",
                                                         "question", default));
        var incomplete = new FakeChatClient();
        incomplete.Enqueue([new ChatStreamEvent { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = "title" }]);
        Assert.ThrowsAsync<InvalidDataException>(() =>
                                                     SessionTitleGenerator.GenerateAsync(incomplete, "title-model",
                                                         "question", default));
    }

    [Test]
    public async Task FinalMessageWinsWithoutDuplicatingStreamDeltas()
    {
        var client = new FakeChatClient();
        client.Enqueue([
            new ChatStreamEvent { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = "thinking" },
            new ChatStreamEvent
            {
                Kind         = ChatStreamEventKind.TextMessageCompleted,
                ContentDelta = "thinking", TextPhase = "commentary"
            },
            new ChatStreamEvent { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = "Fix titles" },
            new ChatStreamEvent
            {
                Kind         = ChatStreamEventKind.TextMessageCompleted,
                ContentDelta = "Fix titles", TextPhase = "final_answer"
            },
            new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason = "completed" }
        ]);
        Assert.That(await SessionTitleGenerator.GenerateAsync(client, "title-model", "question", default),
                    Is.EqualTo("Fix titles"));
    }

    [Test]
    public async Task PendingAndGeneratedNamesSurviveRestoreAndIndexRebuildWithoutChangingActivity()
    {
        using var directory = new TestTempDir();
        var       client    = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("answer"));
        client.Enqueue(FakeChatClient.Text("next answer"));
        var index   = new SessionIndexStore(directory.Root);
        var updater = new SessionIndexUpdater(index);
        using var host = new ChatSessionHost(Path.GetTempPath(),
                                             new SessionModelIdentity("offline", "https://offline.invalid/v1",
                                                                      ChatApiKind.ChatCompletions),
                                             new AgentOptions
                                             {
                                                 Model = "offline", MaxAgentSteps = 2, DefaultToolTimeoutSeconds = 10
                                             },
                                             client, new ToolRegistry([]), "title test",
                                             sessionWriterFactory : id => new SessionStoreWriter(directory.Root, id,
                                                 batchCommitted : updater.HandleBatchCommittedAsync));
        await host.SubmitTurnAsync("第一条输入\n其他内容", default);
        var before = (await SessionStoreReader.ReadAsync(directory.Root, host.Session.Id, default))!;
        Assert.That(before.Title.Pending, Is.True);
        Assert.That(await host.PrepareTitleGenerationAsync(default), Is.True);
        var pending = (await SessionStoreReader.ReadAsync(directory.Root, host.Session.Id, default))!;
        Assert.That(pending.Title.Pending, Is.True);
        Assert.That(pending.LastActivityUtc, Is.EqualTo(before.LastActivityUtc));
        Assert.That((await SessionSummaryBuilder.BuildAsync(directory.Root, host.Session.Id, default))!.Summary.Title,
                    Is.EqualTo("第一条输入"));
        Assert.That(SessionResume.Restore(pending, Path.GetTempPath(), "title test").Session.Title.Pending, Is.True);

        Assert.That(await host.TrySetGeneratedTitleAsync("生成的名称", default), Is.True);
        Assert.That(await host.TrySetGeneratedTitleAsync("迟到的名称", default), Is.False);
        var named = (await SessionStoreReader.ReadAsync(directory.Root, host.Session.Id, default))!;
        Assert.That(named.Title.Name, Is.EqualTo("生成的名称"));
        Assert.That(named.Title.Pending, Is.False);
        Assert.That(named.LastActivityUtc, Is.EqualTo(before.LastActivityUtc));
        Assert.That(SessionResume.Restore(named, Path.GetTempPath(), "title test").Session.Title.Name,
                    Is.EqualTo("生成的名称"));
        Assert.That((await index.GetRowAsync(host.Session.Id, default))!.Summary!.Title, Is.EqualTo("生成的名称"));
        await host.SubmitTurnAsync("后续话题", default);
        Assert.That(await host.PrepareTitleGenerationAsync(default), Is.False);
        Assert.That((await index.GetRowAsync(host.Session.Id, default))!.Summary!.Title, Is.EqualTo("生成的名称"));
        await index.RebuildSchemaAsync(default);
        var rebuilt = await SessionSummaryBuilder.BuildAsync(directory.Root, host.Session.Id, default);
        Assert.That(rebuilt!.Summary.Title, Is.EqualTo("生成的名称"));
        Assert.That(updater.LastFailure, Is.Null);
        var history =
            await new SessionHistoryReader().ReadPageAsync(directory.Root, host.Session.Id, 200, null, default);
        Assert.That(history.Items.Count(item => item.Kind == SessionHistoryKind.User), Is.EqualTo(2));
    }

    [Test]
    public void TitleRecordRequiresAnExplicitValidState()
    {
        Assert.Throws<JsonException>(() =>
                                         JsonSerializer.Deserialize("{}", SessionLogJsonContext.Default.SessionTitle));
        Assert.Throws<InvalidDataException>(() => new SessionTitle { Pending = false }.Validate());
        Assert.Throws<InvalidDataException>(() => new SessionTitle { Schema  = "unknown" }.Validate());
    }
}
