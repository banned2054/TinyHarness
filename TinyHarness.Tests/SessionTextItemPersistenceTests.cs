using System.Text.Json;
using System.Text.Json.Nodes;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Session;

namespace TinyHarness.Tests;

/// <summary>
///     <para>验证文本条目的可选日志扩展、严格校验和跨模型恢复，不调用模型服务。</para>
///     <para>Verifies optional text item logs, strict validation and cross-model restoration without model services.</para>
/// </summary>
public sealed class SessionTextItemPersistenceTests
{
    private static ChatSession NewSession(string workspace) =>
        new(Guid.NewGuid().ToString("N"), workspace,
            new SessionModelIdentity("test", "https://example.invalid/v1", ChatApiKind.Responses));

    private static ConversationContext NewContext(ChatMessage message)
    {
        var context = new ConversationContext();
        context.Append(ChatMessage.System("system"));
        context.Append(ChatMessage.User("question"));
        context.Append(message);
        return context;
    }

    private static IEnumerable<TestCaseData> InvalidMessages()
    {
        var valid = new PersistedMessage
        {
            Role = "Assistant", Content = "answer", TextItems = [new AssistantTextItem("answer", "msg", "future")]
        };
        yield return new TestCaseData(valid with { Content = "different" }).SetArgDisplayNames("mismatched-content");
        yield return new TestCaseData(valid with { TextItems = [] }).SetArgDisplayNames("empty-items-with-content");
        yield return new TestCaseData(valid with { Content = null! }).SetArgDisplayNames("null-content");
        yield return new TestCaseData(valid with { Role = "User" }).SetArgDisplayNames("user-items");
        yield return new TestCaseData(valid with { Role = "System", Content = "", TextItems = [] })
           .SetArgDisplayNames("system-empty-items");
        yield return new TestCaseData(valid with { Role = "Tool" }).SetArgDisplayNames("tool-items");
        yield return new TestCaseData(valid with { TextItems = [null!] }).SetArgDisplayNames("null-item");
        yield return new TestCaseData(valid with { TextItems = [new AssistantTextItem(null!)] })
           .SetArgDisplayNames("null-text");
        foreach (var id in new[] { "", " \t" })
            yield return new TestCaseData(valid with { TextItems = [new AssistantTextItem("answer", id)] })
               .SetArgDisplayNames(id.Length == 0 ? "empty-id" : "whitespace-id");
    }

    [TestCaseSource(nameof(InvalidMessages))]
    public async Task Commit_RejectsInvalidTextItemsWithoutAdvancingOrPoisoningWriter(PersistedMessage invalid)
    {
        using var dir     = new TestTempDir();
        var       session = NewSession(dir.Root);
        var       context = NewContext(ChatMessage.Assistant("answer"));
        using var writer  = new SessionStoreWriter(dir.Root, session.Id);
        await writer.CommitAsync(session, context, CancellationToken.None);
        var bytesBefore = new FileInfo(writer.SnapshotPath).Length;
        context.Append(new ChatMessage
        {
            Role      = Enum.Parse<ChatRole>(invalid.Role),
            Content   = invalid.Content,
            TextItems = invalid.TextItems
        });

        await Assert.ThrowsAsync<SessionStoreException>(async () =>
                                                            await writer.CommitAsync(session, context,
                                                                     CancellationToken.None));
        Assert.That(writer.CommitSequence, Is.EqualTo(1));
        Assert.That(writer.IsHealthy, Is.True);
        Assert.That(new FileInfo(writer.SnapshotPath).Length, Is.EqualTo(bytesBefore));

        context.TruncateToMessageCount(3);
        context.Append(ChatMessage.Assistant("valid") with { TextItems = [new AssistantTextItem("valid")] });
        await writer.CommitAsync(session, context, CancellationToken.None);
        Assert.That(writer.CommitSequence, Is.EqualTo(2));
        var snapshot = await SessionStoreReader.ReadAsync(dir.Root, session.Id, CancellationToken.None);
        Assert.That(snapshot!.Conversation!.Messages[^1].TextItems, Is.EqualTo(context.Messages[^1].TextItems));
    }

    [TestCaseSource(nameof(InvalidMessages))]
    public async Task ReadAndRestore_RejectInvalidTextItems(PersistedMessage invalid)
    {
        using var dir     = new TestTempDir();
        var       session = NewSession(dir.Root);
        var       context = NewContext(ChatMessage.Assistant("answer"));
        string    path;
        using (var writer = new SessionStoreWriter(dir.Root, session.Id))
        {
            await writer.CommitAsync(session, context, CancellationToken.None);
            path = writer.SnapshotPath;
        }

        var snapshot = await SessionStoreReader.ReadAsync(dir.Root, session.Id, CancellationToken.None);
        var damaged = snapshot! with
        {
            Conversation = snapshot.Conversation! with
            {
                Messages = [.. snapshot.Conversation.Messages.Take(2), invalid]
            }
        };
        Assert.Throws<SessionStoreException>(() => SessionResume.Restore(damaged, dir.Root, "system"));

        var batch = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        var messageRecord =
            batch["records"]!.AsArray().Last(record => record!["kind"]!.GetValue<string>() == "message")!;
        messageRecord["payload"]!["message"] =
            JsonSerializer.SerializeToNode(invalid, SessionLogJsonContext.Default.PersistedMessage);
        await File.WriteAllTextAsync(path, batch.ToJsonString() + "\n");
        await Assert.ThrowsAsync<SessionStoreException>(async () =>
                                                            await SessionStoreReader.ReadAsync(dir.Root, session.Id,
                                                                     CancellationToken.None));
        await Assert.ThrowsAsync<SessionStoreException>(async () =>
        {
            using var reopened = await SessionStoreWriter.OpenExistingAsync(dir.Root, session.Id, null,
                                                                            CancellationToken.None);
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task LegacyLog_MissingOrNullTextItemsRemainsResumable(bool explicitNull)
    {
        using var dir     = new TestTempDir();
        var       session = NewSession(dir.Root);
        string    path;
        using (var writer = new SessionStoreWriter(dir.Root, session.Id))
        {
            await writer.CommitAsync(session, NewContext(ChatMessage.Assistant("answer")), CancellationToken.None);
            path = writer.SnapshotPath;
        }

        var batch = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        foreach (var record in batch["records"]!.AsArray()
                                                .Where(record => record!["kind"]!.GetValue<string>() == "message"))
        {
            var message = record!["payload"]!["message"]!.AsObject();
            Assert.That(message.ContainsKey("textItems"), Is.False);
            if (explicitNull) message["textItems"] = null;
        }

        await File.WriteAllTextAsync(path, batch.ToJsonString() + "\n");
        using var reopened =
            await SessionStoreWriter.OpenExistingAsync(dir.Root, session.Id, null, CancellationToken.None);
        var restored = SessionResume.Restore(reopened.LoadedSnapshot!, dir.Root, "system");
        Assert.That(restored.Context.Messages[^1].Content, Is.EqualTo("answer"));
        Assert.That(restored.Context.Messages.All(message => message.TextItems is null), Is.True);
        restored.Context.Append(ChatMessage.User("continue"));
        await reopened.CommitAsync(restored.Session, restored.Context, CancellationToken.None);
        Assert.That(reopened.CommitSequence, Is.EqualTo(2));
    }

    [Test]
    public async Task ModelSwitchAndReopen_PreserveTextItemsAndIsolateOnlyOldReasoning()
    {
        using var dir     = new TestTempDir();
        var       session = NewSession(dir.Root);
        AssistantTextItem[] textItems =
        [
            new(" inspecting ", "msg-commentary", "commentary"),
            new("future text\n", "msg-future", "future-phase"),
            new("", null, "")
        ];
        var assistant = ChatMessage.Assistant(string.Concat(textItems.Select(item => item.Text)), null,
                                              [new ReasoningContent("reason", "opaque", "reason-id")]) with
        {
            TextItems = textItems
        };
        var context = NewContext(assistant);
        using (var writer = new SessionStoreWriter(dir.Root, session.Id))
        {
            await writer.CommitAsync(session, context, CancellationToken.None);
            var selection = new SessionModelSelection
            {
                Revision                   = 1,
                PreviousRevision           = 0,
                SelectedUtc                = DateTimeOffset.UtcNow,
                Model                      = session.InitialModel with { ModelId = "other" },
                ReasoningMappingVersion    = ReasoningEffortMappings.CurrentVersion,
                ProjectionVersion          = SessionModelSelection.CurrentProjectionVersion,
                ContinuationEpoch          = 1,
                MessageBoundary            = context.Messages.Count,
                ReasoningStartMessageIndex = context.Messages.Count
            };
            session.RecordModelSelection(selection);
            context.BindModel(selection);
            await writer.CommitAsync(session, context, CancellationToken.None);
        }

        using var reopened =
            await SessionStoreWriter.OpenExistingAsync(dir.Root, session.Id, null, CancellationToken.None);
        var snapshot = reopened.LoadedSnapshot!;
        Assert.That(snapshot.Conversation!.Messages[^1].TextItems, Is.EqualTo(textItems));
        var restored  = SessionResume.Restore(snapshot, dir.Root, "system");
        var canonical = restored.Context.Messages[^1];
        Assert.That(canonical.TextItems, Is.EqualTo(textItems));
        Assert.That(canonical.Reasoning, Is.EqualTo(assistant.Reasoning));
        var view = restored.Context.BuildModelView()[^1];
        Assert.That(view.Content, Is.EqualTo(assistant.Content));
        Assert.That(view.TextItems, Is.EqualTo(textItems));
        Assert.That(view.Reasoning, Is.Null);
    }
}
