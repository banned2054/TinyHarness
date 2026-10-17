using System.Text.Json;
using System.Text.Json.Nodes;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Persistence;

namespace TinyHarness.Tests;

public class SessionStoreTests
{
    private static ChatSession NewSession(string? id = null) =>
        new(id ?? Guid.NewGuid().ToString("N"), Path.Combine(Path.GetTempPath(), "tinyharness-log-tests"),
            new SessionModelIdentity("test-model", "https://example.invalid/v1", ChatApiKind.ChatCompletions));

    private static void AddTurn(ChatSession session, string input, string answer)
    {
        var turn = new SessionTurn(Guid.NewGuid().ToString("N"), input);
        var run  = new SessionRun(Guid.NewGuid().ToString("N"), session.Id, turn.Id);
        session.AddTurn(turn);
        turn.AddRun(run);
        run.Finish(new AgentResult { Status = AgentStatus.Completed, FinalMessage = answer, Steps = 1 });
    }

    [Test]
    public async Task CommitAndRead_RoundTripIncrementalTurnsAndMessages()
    {
        using var dir     = new TestTempDir();
        var       session = NewSession();
        var       context = new ConversationContext();
        context.Append(ChatMessage.System("system"));
        AddTurn(session, "question one", "answer one");
        context.Append(ChatMessage.User("question one"));
        context.Append(ChatMessage.Assistant("answer one"));
        using (var writer = new SessionStoreWriter(dir.Root, session.Id))
        {
            await writer.CommitAsync(session, context, CancellationToken.None);
            AddTurn(session, "question two", "answer two");
            context.Append(ChatMessage.User("question two"));
            context.Append(ChatMessage.Assistant("answer two"));
            await writer.CommitAsync(session, context, CancellationToken.None);
            Assert.That(writer.CommitSequence, Is.EqualTo(2));
        }

        var path = Path.Combine(dir.Root, session.Id              + SessionStoreWriter.LogFileSuffix);
        Assert.That(Path.GetFileName(path), Is.EqualTo(session.Id + ".session.jsonl"));
        var lines = await ReadLinesSharedAsync(path);
        Assert.That(lines.Length, Is.EqualTo(2));
        Assert.That(JsonNode.Parse(lines[0])!["format"]!.GetValue<string>(), Is.EqualTo("tinyharness.session-log/1"));
        var restored = await SessionStoreReader.ReadAsync(dir.Root, session.Id, CancellationToken.None);
        TestAssert.NotNull(restored);
        Assert.That(restored!.CommitSequence, Is.EqualTo(2));
        Assert.That(restored.Turns.Count, Is.EqualTo(2));
        Assert.That(restored.Turns[1].Runs.Single().FinalMessage, Is.EqualTo("answer two"));
        Assert.That(restored.Conversation!.Messages.Count, Is.EqualTo(5));
    }

    [Test]
    public async Task Read_RejectsTruncatedFinalLine()
    {
        using var dir     = new TestTempDir();
        var       session = NewSession();
        AddTurn(session, "q", "a");
        using (var writer = new SessionStoreWriter(dir.Root, session.Id))
        {
            await writer.CommitAsync(session, new ConversationContext(), CancellationToken.None);
        }

        var path  = Path.Combine(dir.Root, session.Id + SessionStoreWriter.LogFileSuffix);
        var bytes = await File.ReadAllBytesAsync(path);
        await File.WriteAllBytesAsync(path, bytes[..^1]);

        var error =
            await TestAssert.ThrowsAsync<SessionStoreException>(() => SessionStoreReader.ReadAsync(dir.Root, session.Id,
                                                                    CancellationToken.None));
        Assert.That(error.Message, Does.Contain("reliable prefix ends at sequence 0"), error.Message);
    }

    [Test]
    public async Task Read_RejectsDuplicateBatchSequence()
    {
        using var dir     = new TestTempDir();
        var       session = NewSession();
        AddTurn(session, "q", "a");
        var context = new ConversationContext();
        using (var writer = new SessionStoreWriter(dir.Root, session.Id))
        {
            await writer.CommitAsync(session, context, CancellationToken.None);
            context.Append(ChatMessage.User("second commit marker"));
            await writer.CommitAsync(session, context, CancellationToken.None);
        }

        var path  = Path.Combine(dir.Root, session.Id + SessionStoreWriter.LogFileSuffix);
        var lines = (await ReadLinesSharedAsync(path)).ToList();
        lines[1] = lines[0];
        await File.WriteAllLinesAsync(path, lines);

        var error =
            await TestAssert.ThrowsAsync<SessionStoreException>(() => SessionStoreReader.ReadAsync(dir.Root, session.Id,
                                                                    CancellationToken.None));
        TestAssert.Contains("duplicate", error.Message, StringComparison.Ordinal);
    }

    [Test]
    public async Task Read_RejectsSequenceGap()
    {
        using var dir     = new TestTempDir();
        var       session = NewSession();
        AddTurn(session, "q", "a");
        var context = new ConversationContext();
        using (var writer = new SessionStoreWriter(dir.Root, session.Id))
        {
            await writer.CommitAsync(session, context, CancellationToken.None);
            context.Append(ChatMessage.User("second commit marker"));
            await writer.CommitAsync(session, context, CancellationToken.None);
        }

        var path   = Path.Combine(dir.Root, session.Id + SessionStoreWriter.LogFileSuffix);
        var lines  = await ReadLinesSharedAsync(path);
        var second = JsonNode.Parse(lines[1])!.AsObject();
        second["sequence"] = 3;
        await File.WriteAllTextAsync(path, lines[0] + "\n" + second.ToJsonString() + "\n");

        var error =
            await TestAssert.ThrowsAsync<SessionStoreException>(() => SessionStoreReader.ReadAsync(dir.Root, session.Id,
                                                                    CancellationToken.None));
        TestAssert.Contains("out-of-order", error.Message, StringComparison.Ordinal);
    }

    [Test]
    public async Task OpenExisting_SeedsSequenceAndContinuesAppending()
    {
        using var dir     = new TestTempDir();
        var       session = NewSession();
        AddTurn(session, "q1", "a1");
        using (var writer = new SessionStoreWriter(dir.Root, session.Id))
        {
            await writer.CommitAsync(session, new ConversationContext(), CancellationToken.None);
        }

        using var resumed =
            await SessionStoreWriter.OpenExistingAsync(dir.Root, session.Id, null, CancellationToken.None);
        Assert.That(resumed.CommitSequence, Is.EqualTo(1));
        AddTurn(session, "q2", "a2");
        await resumed.CommitAsync(session, new ConversationContext(), CancellationToken.None);
        Assert.That(resumed.CommitSequence, Is.EqualTo(2));
    }

    [Test]
    public async Task Commit_OversizedBatchDoesNotAdvanceTurnCursor()
    {
        using var dir     = new TestTempDir();
        var       session = NewSession();
        AddTurn(session, "preserve this turn", "answer");
        var context = new ConversationContext();
        context.Append(ChatMessage.System("system"));
        context.Append(ChatMessage.Assistant(new string('x', SessionStoreWriter.MaxSnapshotBytes)));

        using var writer = new SessionStoreWriter(dir.Root, session.Id);
        var error =
            await TestAssert.ThrowsAsync<SessionStoreException>(() => writer.CommitAsync(session, context,
                                                                    CancellationToken.None));
        TestAssert.Contains("exceeds", error.Message, StringComparison.Ordinal);
        Assert.That(writer.CommitSequence, Is.EqualTo(0));

        context.TruncateToMessageCount(1);
        await writer.CommitAsync(session, context, CancellationToken.None);
        var restored = await SessionStoreReader.ReadAsync(dir.Root, session.Id, CancellationToken.None);
        TestAssert.NotNull(restored);
        Assert.That(restored!.Turns.Single().UserInput, Is.EqualTo("preserve this turn"));
    }

    [Test]
    public async Task Commit_ConcurrentCallsProduceOneUniqueCommittedSequence()
    {
        using var dir     = new TestTempDir();
        var       session = NewSession();
        AddTurn(session, "one", "answer");
        var context = new ConversationContext();
        context.Append(ChatMessage.System("system"));
        context.Append(ChatMessage.User("one"));
        context.Append(ChatMessage.Assistant("answer"));
        using var writer = new SessionStoreWriter(dir.Root, session.Id);

        await Task.WhenAll(
                           writer.CommitAsync(session, context, CancellationToken.None),
                           writer.CommitAsync(session, context, CancellationToken.None));

        var lines     = await ReadLinesSharedAsync(writer.SnapshotPath);
        var sequences = lines.Select(line => JsonNode.Parse(line)!["sequence"]!.GetValue<long>()).ToArray();
        Assert.That(sequences, Is.EqualTo(new long[] { 1 }));
        Assert.That(writer.CommitSequence, Is.EqualTo(1));
    }

    [Test]
    public async Task Commit_TracksAnotherRunAddedToTheSameTurn()
    {
        using var dir      = new TestTempDir();
        var       session  = NewSession();
        var       turn     = new SessionTurn(Guid.NewGuid().ToString("N"), "same turn");
        var       firstRun = new SessionRun(Guid.NewGuid().ToString("N"), session.Id, turn.Id);
        session.AddTurn(turn);
        turn.AddRun(firstRun);
        firstRun.Finish(new AgentResult { Status = AgentStatus.Completed, FinalMessage = "first" });
        using var writer = new SessionStoreWriter(dir.Root, session.Id);
        await writer.CommitAsync(session, new ConversationContext(), CancellationToken.None);

        var secondRun = new SessionRun(Guid.NewGuid().ToString("N"), session.Id, turn.Id);
        turn.AddRun(secondRun);
        secondRun.Finish(new AgentResult { Status = AgentStatus.Failed, FinalMessage = "second" });
        await writer.CommitAsync(session, new ConversationContext(), CancellationToken.None);

        var restored = await SessionStoreReader.ReadAsync(dir.Root, session.Id, CancellationToken.None);
        TestAssert.NotNull(restored);
        Assert.That(restored!.Turns.Single().Runs.Select(run => run.FinalMessage),
                    Is.EqualTo(["first", "second"]));
    }

    [Test]
    public async Task AppendRecords_HistoryIdentityAndSequenceAreWriterBound()
    {
        using var dir     = new TestTempDir();
        var       session = NewSession();
        using var writer  = new SessionStoreWriter(dir.Root, session.Id);
        await writer.CommitAsync(session, new ConversationContext(), CancellationToken.None);
        var item = new SessionHistoryItem
        {
            Id   = "caller-chosen", Sequence     = 999, SessionId = session.Id, TurnId = "turn",
            Kind = SessionHistoryKind.User, Text = "hello"
        };
        var input = new SessionLogRecord("", "history",
                                         JsonSerializer.SerializeToElement(item,
                                                                           SessionHistoryJsonContext.Default
                                                                              .SessionHistoryItem));
        var invalidId = input with { Id = "caller-chosen-record-id" };
        await Assert.ThrowsAsync<SessionStoreException>(async () =>
                                                            await writer.AppendRecordsAsync([invalidId],
                                                                CancellationToken.None));
        Assert.That(writer.CommitSequence, Is.EqualTo(1));

        var committed = await writer.AppendRecordsAsync([input], CancellationToken.None);
        var bound     = committed.Single().Payload.Deserialize(SessionHistoryJsonContext.Default.SessionHistoryItem)!;
        Assert.That(bound.Id, Is.EqualTo(committed[0].Id));
        Assert.That(bound.Sequence, Is.EqualTo(2));
        Assert.That(bound.SessionId, Is.EqualTo(session.Id));
        Assert.That((await SessionStoreReader.ReadAsync(dir.Root, session.Id, CancellationToken.None))!.CommitSequence,
                    Is.EqualTo(2));
    }

    private static async Task<string[]> ReadLinesSharedAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var       reader = new StreamReader(stream);
        var             lines  = new List<string>();
        while (await reader.ReadLineAsync() is { } line) lines.Add(line);
        return lines.ToArray();
    }
}
