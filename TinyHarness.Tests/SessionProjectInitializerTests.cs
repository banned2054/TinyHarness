using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Project;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Persistence;

namespace TinyHarness.Tests;

public class SessionProjectInitializerTests
{
    private static readonly SessionModelIdentity Model =
        new("test-model", "https://example.invalid/v1", ChatApiKind.ChatCompletions);

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static string OrderedId(char suffix) => "0000000000000000000000000000000" + suffix;

    private static void AddTurn(ChatSession session, string input, string answer)
    {
        var turn = new SessionTurn(Guid.NewGuid().ToString("N"), input);
        var run  = new SessionRun(Guid.NewGuid().ToString("N"), session.Id, turn.Id);
        session.AddTurn(turn);
        turn.AddRun(run);
        run.Finish(new AgentResult { Status = AgentStatus.Completed, FinalMessage = answer, Steps = 1 });
    }

    /// <summary>
    ///     <para>创建含两条历史条目的未绑定日志并返回会话 ID。</para>
    ///     <para>Creates an unbound log with two history items and returns its session ID.</para>
    /// </summary>
    private static async Task<string> CommitUnboundSessionAsync(
        string storeRoot, string workspaceRoot, string? sessionId = null)
    {
        var session = new ChatSession(sessionId ?? Guid.NewGuid().ToString("N"), workspaceRoot, Model);
        var context = new ConversationContext();
        context.Append(ChatMessage.System("system"));
        AddTurn(session, "question", "answer");
        context.Append(ChatMessage.User("question"));
        context.Append(ChatMessage.Assistant("answer"));
        using (var writer = new SessionStoreWriter(storeRoot, session.Id))
        {
            await writer.CommitAsync(session, context, CancellationToken.None);
            var turnId = session.Turns[0].Id;
            await writer.AppendRecordsAsync(
            [
                new SessionLogRecord(string.Empty, "history",
                                     JsonSerializer.SerializeToElement(new SessionHistoryItem
                                     {
                                         Id        = string.Empty,
                                         Sequence  = 0,
                                         SessionId = session.Id,
                                         TurnId    = turnId,
                                         Kind      = SessionHistoryKind.User,
                                         Text      = "question"
                                     }, SessionHistoryJsonContext.Default.SessionHistoryItem)),
                new SessionLogRecord(string.Empty, "history",
                                     JsonSerializer.SerializeToElement(new SessionHistoryItem
                                     {
                                         Id        = string.Empty,
                                         Sequence  = 0,
                                         SessionId = session.Id,
                                         TurnId    = turnId,
                                         Kind      = SessionHistoryKind.Final,
                                         Text      = "answer"
                                     }, SessionHistoryJsonContext.Default.SessionHistoryItem))
            ], CancellationToken.None);
        }

        return session.Id;
    }

    /// <summary>
    ///     <para>构造含指定时间和目录的身份批次，用于测试非法日志。</para>
    ///     <para>Builds an identity batch with the supplied timestamp and root for invalid-log tests.</para>
    /// </summary>
    private static async Task WriteHandCraftedIdentityBatchAsync(
        string storeRoot, string sessionId, JsonNode writtenUtc, string workspaceRoot)
    {
        var batch = new JsonObject
        {
            ["format"]     = SessionLogFormat.FormatName,
            ["sessionId"]  = sessionId,
            ["sequence"]   = 1,
            ["batchId"]    = $"{sessionId}:{1:D20}",
            ["writtenUtc"] = writtenUtc,
            ["records"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"]   = $"{sessionId}:{1:D20}:0000",
                    ["kind"] = "identity",
                    ["payload"] = new JsonObject
                    {
                        ["id"]            = sessionId,
                        ["workspaceRoot"] = workspaceRoot,
                        ["model"] = new JsonObject
                        {
                            ["modelId"]  = "test-model",
                            ["endpoint"] = "https://example.invalid/v1",
                            ["chatApi"]  = "ChatCompletions"
                        },
                        ["createdUtc"] = DateTimeOffset.UtcNow
                    }
                }
            }
        };
        var path = Path.Combine(storeRoot, sessionId + SessionStoreWriter.LogFileSuffix);
        await File.AppendAllTextAsync(path, batch.ToJsonString() + Environment.NewLine);
    }

    private static async Task<Dictionary<string, byte[]>> ReadAllLogBytesAsync(string storeRoot)
    {
        var bytesByFile = new Dictionary<string, byte[]>();
        foreach (var path in Directory.GetFiles(storeRoot, "*" + SessionStoreWriter.LogFileSuffix))
            bytesByFile[Path.GetFileName(path)] = await File.ReadAllBytesAsync(path);
        return bytesByFile;
    }

    private static byte[] CatalogBytes(string storeRoot)
    {
        var path = ProjectStore.FilePath(storeRoot);
        return File.Exists(path) ? File.ReadAllBytes(path) : [];
    }

    /// <summary>
    ///     <para>通过真实 writer 追加测试会话的项目归属。</para>
    ///     <para>Appends test-session ownership through the real writer.</para>
    /// </summary>
    private static Task RealAppendAsync(SessionStoreWriter writer, string projectId, CancellationToken token) =>
        writer.AppendRecordsAsync(
        [
            new SessionLogRecord(string.Empty, "project-binding",
                                 JsonSerializer.SerializeToElement(
                                     new SessionLogProjectBinding(SessionLogProjectBinding.SchemaName, projectId),
                                     SessionLogJsonContext.Default.SessionLogProjectBinding))
        ], token);

    [Test]
    public async Task SameRootSessions_BindToOneProject()
    {
        using var store     = new TestTempDir();
        using var workspace = new TestTempDir();
        var ids = new List<string>();
        for (var i = 0; i < 3; i++) ids.Add(await CommitUnboundSessionAsync(store.Root, workspace.Root));

        var result = await SessionProjectInitializer.InitializeAsync(store.Root);

        Assert.That(result.Cancelled, Is.False);
        Assert.That(result.HasMore, Is.False);
        Assert.That(result.Outcomes.Count, Is.EqualTo(3));
        Assert.That(result.Outcomes.All(outcome => outcome.Status == SessionProjectBindingStatus.Bound), Is.True);
        var projectIds = result.Outcomes.Select(outcome => outcome.ProjectId).ToHashSet();
        Assert.That(projectIds.Count, Is.EqualTo(1));
        Assert.That(result.Projects.Count, Is.EqualTo(1));

        var catalog = await ProjectStore.LoadAsync(ProjectStore.FilePath(store.Root), CancellationToken.None);
        Assert.That(catalog.Projects.Count, Is.EqualTo(1));
        Assert.That(catalog.Projects[0].Id, Is.EqualTo(projectIds.Single()));
        Assert.That(ProjectRoots.AreEqual(catalog.Projects[0].PrimaryRoot, workspace.Root), Is.True);
        foreach (var id in ids)
        {
            var restored = await SessionStoreReader.ReadAsync(store.Root, id, CancellationToken.None);
            TestAssert.NotNull(restored);
            Assert.That(restored!.ProjectId, Is.EqualTo(projectIds.Single()));
        }
    }

    [Test]
    [Platform("Win")]
    public async Task EquivalentRootCasing_UsesOneProjectGroup()
    {
        using var store     = new TestTempDir();
        using var workspace = new TestTempDir();
        await CommitUnboundSessionAsync(store.Root, workspace.Root.ToLowerInvariant());
        await CommitUnboundSessionAsync(store.Root, workspace.Root.ToUpperInvariant());

        var result = await SessionProjectInitializer.InitializeAsync(store.Root);

        Assert.That(result.Outcomes.All(outcome => outcome.Status == SessionProjectBindingStatus.Bound), Is.True);
        Assert.That(result.Projects.Count, Is.EqualTo(1));
        Assert.That(result.Outcomes.Select(outcome => outcome.ProjectId).Distinct().Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task Rerun_IsIdempotent_AndLeavesBytesUnchanged()
    {
        using var store     = new TestTempDir();
        using var workspace = new TestTempDir();
        for (var i = 0; i < 2; i++) await CommitUnboundSessionAsync(store.Root, workspace.Root);

        var first = await SessionProjectInitializer.InitializeAsync(store.Root);
        Assert.That(first.Outcomes.All(outcome => outcome.Status == SessionProjectBindingStatus.Bound), Is.True);
        var logBytes     = await ReadAllLogBytesAsync(store.Root);
        var catalogBytes = CatalogBytes(store.Root);
        Assert.That(catalogBytes.Length, Is.GreaterThan(0));

        var second = await SessionProjectInitializer.InitializeAsync(store.Root);

        Assert.That(second.Cancelled, Is.False);
        Assert.That(second.Outcomes.All(outcome => outcome.Status == SessionProjectBindingStatus.AlreadyBound), Is.True);
        Assert.That(second.Outcomes.Select(outcome => outcome.ProjectId).ToHashSet(),
                    Is.EquivalentTo(first.Outcomes.Select(outcome => outcome.ProjectId).ToHashSet()));
        var afterLogBytes = await ReadAllLogBytesAsync(store.Root);
        Assert.That(afterLogBytes.Keys, Is.EquivalentTo(logBytes.Keys));
        foreach (var key in logBytes.Keys)
            Assert.That(afterLogBytes[key].AsSpan().SequenceEqual(logBytes[key]), Is.True,
                        $"Log '{key}' changed during an idempotent rerun.");
        Assert.That(CatalogBytes(store.Root).AsSpan().SequenceEqual(catalogBytes), Is.True);
    }

    [Test]
    public async Task BoundSession_WithMissingCatalog_KeepsBindingWithoutRebuild()
    {
        using var store     = new TestTempDir();
        using var workspace = new TestTempDir();
        var sessionId = await CommitUnboundSessionAsync(store.Root, workspace.Root);
        var first    = await SessionProjectInitializer.InitializeAsync(store.Root);
        var       boundId   = first.Outcomes.Single().ProjectId;

        File.Delete(ProjectStore.FilePath(store.Root));

        var second = await SessionProjectInitializer.InitializeAsync(store.Root);
        var outcome = second.Outcomes.Single();
        Assert.That(outcome.Status, Is.EqualTo(SessionProjectBindingStatus.AlreadyBound));
        Assert.That(outcome.SessionId, Is.EqualTo(sessionId));
        Assert.That(outcome.ProjectId, Is.EqualTo(boundId));
        // 已绑定条目不触发任何项目写入，目录册不被重建。
        Assert.That(File.Exists(ProjectStore.FilePath(store.Root)), Is.False);
        Assert.That(second.Projects, Is.Empty);
    }

    [Test]
    public async Task DifferentRoots_DoNotMerge_AndMissingDirectoryStillBinds()
    {
        using var store = new TestTempDir();
        using var workspaceA = new TestTempDir();
        var missingRoot = Path.Combine(Path.GetTempPath(), "tinyharness-init-missing-" + Guid.NewGuid().ToString("N"));
        await CommitUnboundSessionAsync(store.Root, workspaceA.Root);
        await CommitUnboundSessionAsync(store.Root, missingRoot);

        var result = await SessionProjectInitializer.InitializeAsync(store.Root);

        Assert.That(result.Outcomes.Count, Is.EqualTo(2));
        Assert.That(result.Outcomes.All(outcome => outcome.Status == SessionProjectBindingStatus.Bound), Is.True);
        var byRoot = result.Outcomes.ToDictionary(outcome => outcome.WorkspaceRoot, outcome => outcome.ProjectId);
        Assert.That(byRoot[workspaceA.Root], Is.Not.EqualTo(byRoot[missingRoot]));
        var catalog = await ProjectStore.LoadAsync(ProjectStore.FilePath(store.Root), CancellationToken.None);
        Assert.That(catalog.Projects.Count, Is.EqualTo(2));
        Assert.That(catalog.Projects.Count(project => ProjectRoots.AreEqual(project.PrimaryRoot, missingRoot)),
                    Is.EqualTo(1));
    }

    [Test]
    public async Task RelativeSavedRoot_FailsItsItem_OthersContinue()
    {
        using var store     = new TestTempDir();
        using var workspace = new TestTempDir();
        var validId = await CommitUnboundSessionAsync(store.Root, workspace.Root);
        // 手工构造相对根日志：ChatSession 构造会规范化相对路径，无法经真实 writer 产生该形态。
        var       relativeId = OrderedId('b');
        await WriteHandCraftedIdentityBatchAsync(store.Root, relativeId, JsonValue.Create(DateTimeOffset.UtcNow),
                                                 "relative-root");

        var result = await SessionProjectInitializer.InitializeAsync(store.Root);

        var byId = result.Outcomes.ToDictionary(outcome => outcome.SessionId);
        Assert.That(byId[validId].Status, Is.EqualTo(SessionProjectBindingStatus.Bound));
        Assert.That(byId[relativeId].Status, Is.EqualTo(SessionProjectBindingStatus.Failed));
        TestAssert.Contains("not an absolute path", byId[relativeId].Detail!, StringComparison.Ordinal);
        var restored = await SessionStoreReader.ReadAsync(store.Root, relativeId, CancellationToken.None);
        TestAssert.NotNull(restored);
        Assert.That(restored!.ProjectId, Is.Null);
    }

    [Test]
    public async Task InvalidAbsoluteSavedRoot_FailsItsItem_OthersContinue()
    {
        using var store     = new TestTempDir();
        using var workspace = new TestTempDir();
        var       invalidId = OrderedId('a');
        await WriteHandCraftedIdentityBatchAsync(store.Root, invalidId, JsonValue.Create(DateTimeOffset.UtcNow),
                                                 Path.Combine(workspace.Root, "bad\0root"));
        var validId = await CommitUnboundSessionAsync(store.Root, workspace.Root, OrderedId('b'));
        var invalidPath = Path.Combine(store.Root, invalidId + SessionStoreWriter.LogFileSuffix);
        var original    = await File.ReadAllBytesAsync(invalidPath);

        var result = await SessionProjectInitializer.InitializeAsync(store.Root);

        var byId = result.Outcomes.ToDictionary(outcome => outcome.SessionId);
        Assert.That(byId[invalidId].Status, Is.EqualTo(SessionProjectBindingStatus.Failed));
        TestAssert.Contains("cannot be normalized", byId[invalidId].Detail!, StringComparison.Ordinal);
        Assert.That(byId[validId].Status, Is.EqualTo(SessionProjectBindingStatus.Bound));
        Assert.That(await File.ReadAllBytesAsync(invalidPath), Is.EqualTo(original));
    }

    [Test]
    public async Task HeldWriter_MarksPending_OthersContinue_RetryAfterReleaseBinds()
    {
        using var store     = new TestTempDir();
        using var workspace = new TestTempDir();
        var ids = new List<string>();
        for (var i = 0; i < 3; i++) ids.Add(await CommitUnboundSessionAsync(store.Root, workspace.Root));
        var heldId = ids[1];

        using (var heldWriter = await SessionStoreWriter.OpenExistingAsync(store.Root, heldId, null,
                                                                           CancellationToken.None))
        {
            var result = await SessionProjectInitializer.InitializeAsync(store.Root);
            var byId = result.Outcomes.ToDictionary(outcome => outcome.SessionId);
            Assert.That(byId[ids[0]].Status, Is.EqualTo(SessionProjectBindingStatus.Bound));
            Assert.That(byId[heldId].Status, Is.EqualTo(SessionProjectBindingStatus.Pending));
            TestAssert.Contains("another writer", byId[heldId].Detail!, StringComparison.Ordinal);
            Assert.That(byId[ids[2]].Status, Is.EqualTo(SessionProjectBindingStatus.Bound));
            Assert.That(result.Projects.Count, Is.EqualTo(1));
        }

        var retry     = await SessionProjectInitializer.InitializeAsync(store.Root);
        var retryById = retry.Outcomes.ToDictionary(outcome => outcome.SessionId);
        Assert.That(retryById[heldId].Status, Is.EqualTo(SessionProjectBindingStatus.Bound));
        Assert.That(retryById[ids[0]].Status, Is.EqualTo(SessionProjectBindingStatus.AlreadyBound));
        Assert.That(retryById[ids[2]].Status, Is.EqualTo(SessionProjectBindingStatus.AlreadyBound));
        Assert.That(retry.Outcomes.Select(outcome => outcome.ProjectId).ToHashSet().Count, Is.EqualTo(1));
    }

    [Test]
    public async Task BoundedBatch_DoesNotReadLaterBrokenLog_AndCursorResumes()
    {
        using var store     = new TestTempDir();
        using var workspace = new TestTempDir();
        var validId  = await CommitUnboundSessionAsync(store.Root, workspace.Root, OrderedId('a'));
        var       brokenId   = await CommitUnboundSessionAsync(store.Root, workspace.Root, OrderedId('b'));
        var       brokenPath = Path.Combine(store.Root, brokenId + SessionStoreWriter.LogFileSuffix);
        await File.AppendAllTextAsync(brokenPath, "{\"format\": \"tinyharness");
        var bytesBefore = await ReadAllLogBytesAsync(store.Root);

        var first = await SessionProjectInitializer.InitializeAsync(store.Root, afterSessionId: null, maxSessions: 1);

        // 第一批只检查了前一条日志：损坏日志不在结果中（未读取），也未被动过。
        Assert.That(first.Outcomes.Count, Is.EqualTo(1));
        Assert.That(first.Outcomes.Single().SessionId, Is.EqualTo(validId));
        Assert.That(first.Outcomes.Single().Status, Is.EqualTo(SessionProjectBindingStatus.Bound));
        Assert.That(first.HasMore, Is.True);
        Assert.That(first.NextCursor, Is.EqualTo(validId));
        Assert.That((await ReadAllLogBytesAsync(store.Root))[Path.GetFileName(brokenPath)]
                    .AsSpan().SequenceEqual(bytesBefore[Path.GetFileName(brokenPath)]), Is.True);

        var second = await SessionProjectInitializer.InitializeAsync(store.Root, afterSessionId: first.NextCursor,
                                                                     maxSessions: 1);
        var broken = second.Outcomes.Single();
        Assert.That(broken.SessionId, Is.EqualTo(brokenId));
        Assert.That(broken.Status, Is.EqualTo(SessionProjectBindingStatus.Failed));
        Assert.That(second.HasMore, Is.False);
        Assert.That(second.NextCursor, Is.EqualTo(brokenId));
        // 损坏日志原字节不变。
        Assert.That((await ReadAllLogBytesAsync(store.Root))[Path.GetFileName(brokenPath)]
                    .AsSpan().SequenceEqual(bytesBefore[Path.GetFileName(brokenPath)]), Is.True);
    }

    [Test]
    public async Task BoundedBatch_CursorWalksAllSessions_ThenHeadRerunIsIdempotent()
    {
        using var store     = new TestTempDir();
        using var workspace = new TestTempDir();
        var idA = await CommitUnboundSessionAsync(store.Root, workspace.Root, OrderedId('a'));
        var idB = await CommitUnboundSessionAsync(store.Root, workspace.Root, OrderedId('b'));
        var idC = await CommitUnboundSessionAsync(store.Root, workspace.Root, OrderedId('c'));

        var first  = await SessionProjectInitializer.InitializeAsync(store.Root, maxSessions : 1);
        var second = await SessionProjectInitializer.InitializeAsync(store.Root, afterSessionId: first.NextCursor,
                                                                     maxSessions : 1);
        var third = await SessionProjectInitializer.InitializeAsync(store.Root, afterSessionId: second.NextCursor,
                                                                     maxSessions : 1);

        Assert.That(first.NextCursor, Is.EqualTo(idA));
        Assert.That(first.HasMore, Is.True);
        Assert.That(second.NextCursor, Is.EqualTo(idB));
        Assert.That(second.HasMore, Is.True);
        Assert.That(third.NextCursor, Is.EqualTo(idC));
        Assert.That(third.HasMore, Is.False);
        Assert.That(first.Outcomes.Single().Status, Is.EqualTo(SessionProjectBindingStatus.Bound));
        Assert.That(second.Outcomes.Single().Status, Is.EqualTo(SessionProjectBindingStatus.Bound));
        Assert.That(third.Outcomes.Single().Status, Is.EqualTo(SessionProjectBindingStatus.Bound));

        var headRerun = await SessionProjectInitializer.InitializeAsync(store.Root);
        Assert.That(headRerun.Outcomes.All(outcome => outcome.Status == SessionProjectBindingStatus.AlreadyBound),
                    Is.True);
        Assert.That(headRerun.Outcomes.Count, Is.EqualTo(3));
    }

    [Test]
    public async Task Cursor_PendingDoesNotStarveLaterSessions()
    {
        using var store     = new TestTempDir();
        using var workspace = new TestTempDir();
        var idA = await CommitUnboundSessionAsync(store.Root, workspace.Root, OrderedId('a'));
        var idB = await CommitUnboundSessionAsync(store.Root, workspace.Root, OrderedId('b'));

        SessionStoreWriter heldWriter;
        using (heldWriter = await SessionStoreWriter.OpenExistingAsync(store.Root, idA, null, CancellationToken.None))
        {
            // 批次一：A 被占用仅 Pending；批次二从 A 之后续扫，B 不被靠前的 Pending 饿死。
            var batchOne = await SessionProjectInitializer.InitializeAsync(store.Root, maxSessions: 1);
            var pending  = batchOne.Outcomes.Single();
            Assert.That(pending.SessionId, Is.EqualTo(idA));
            Assert.That(pending.Status, Is.EqualTo(SessionProjectBindingStatus.Pending));
            Assert.That(batchOne.HasMore, Is.True);

            var batchTwo = await SessionProjectInitializer.InitializeAsync(store.Root,
                                                                           afterSessionId: batchOne.NextCursor,
                                                                           maxSessions: 1);
            var bound = batchTwo.Outcomes.Single();
            Assert.That(bound.SessionId, Is.EqualTo(idB));
            Assert.That(bound.Status, Is.EqualTo(SessionProjectBindingStatus.Bound));
        }

        // 末批结束后从头重试：释放占用的 A 可被绑定。
        var headRetry = await SessionProjectInitializer.InitializeAsync(store.Root);
        var byId      = headRetry.Outcomes.ToDictionary(outcome => outcome.SessionId);
        Assert.That(byId[idA].Status, Is.EqualTo(SessionProjectBindingStatus.Bound));
        Assert.That(byId[idB].Status, Is.EqualTo(SessionProjectBindingStatus.AlreadyBound));
    }

    [Test]
    public async Task CancelBeforeAppend_ReturnsCompletedFactsWithoutFailure()
    {
        using var store     = new TestTempDir();
        using var workspace = new TestTempDir();
        var idA = await CommitUnboundSessionAsync(store.Root, workspace.Root, OrderedId('a'));
        var idB = await CommitUnboundSessionAsync(store.Root, workspace.Root, OrderedId('b'));
        using var cts = new CancellationTokenSource();
        var result = await SessionProjectInitializer.InitializeAsync(store.Root, null, null, null,
                                                                     (sessionId, writer, projectId, token) =>
                                                                     {
                                                                         if (sessionId != idB)
                                                                             return RealAppendAsync(writer, projectId, token);
                                                                         cts.Cancel();
                                                                         throw new OperationCanceledException(token);
                                                                     }, cts.Token);

        var byId = result.Outcomes.ToDictionary(outcome => outcome.SessionId);
        Assert.That(result.Cancelled, Is.True);
        Assert.That(byId[idA].Status, Is.EqualTo(SessionProjectBindingStatus.Bound));
        Assert.That(byId.ContainsKey(idB), Is.False);
        Assert.That(result.NextCursor, Is.EqualTo(idA));
        Assert.That(result.HasMore, Is.True);
        var restored = await SessionStoreReader.ReadAsync(store.Root, idB, CancellationToken.None);
        Assert.That(restored!.ProjectId, Is.Null);

        var rerun = await SessionProjectInitializer.InitializeAsync(store.Root, afterSessionId: result.NextCursor);
        Assert.That(rerun.Cancelled, Is.False);
        Assert.That(rerun.Outcomes.Single().SessionId, Is.EqualTo(idB));
        Assert.That(rerun.Outcomes.Single().Status, Is.EqualTo(SessionProjectBindingStatus.Bound));
        Assert.That(rerun.HasMore, Is.False);
    }

    [Test]
    public async Task CancelWithInterleavedRoots_CursorDoesNotSkipUnfinishedGroup()
    {
        using var store      = new TestTempDir();
        using var workspaceA = new TestTempDir();
        using var workspaceB = new TestTempDir();
        var idA = await CommitUnboundSessionAsync(store.Root, workspaceA.Root, OrderedId('a'));
        var idB = await CommitUnboundSessionAsync(store.Root, workspaceB.Root, OrderedId('b'));
        var idC = await CommitUnboundSessionAsync(store.Root, workspaceA.Root, OrderedId('c'));
        using var cts = new CancellationTokenSource();

        async Task AppendAsync(string id, SessionStoreWriter writer, string projectId, CancellationToken token)
        {
            await RealAppendAsync(writer, projectId, token);
            if (id == idC) cts.Cancel();
        }

        var result = await SessionProjectInitializer.InitializeAsync(store.Root, null, null, null, AppendAsync,
                                                                     cts.Token);
        Assert.That(result.Cancelled, Is.True);
        Assert.That(result.Outcomes.Select(outcome => outcome.SessionId), Is.EquivalentTo(new[] { idA, idC }));
        Assert.That(result.NextCursor, Is.EqualTo(idA));
        Assert.That(result.HasMore, Is.True);

        var resumed = await SessionProjectInitializer.InitializeAsync(store.Root, result.NextCursor);
        var byId    = resumed.Outcomes.ToDictionary(outcome => outcome.SessionId);
        Assert.That(byId[idB].Status, Is.EqualTo(SessionProjectBindingStatus.Bound));
        Assert.That(byId[idC].Status, Is.EqualTo(SessionProjectBindingStatus.AlreadyBound));
        Assert.That(resumed.HasMore, Is.False);
    }

    [Test]
    public async Task ConcurrentInitializations_KeepAppendInjectionIsolated()
    {
        using var storeA    = new TestTempDir();
        using var storeB    = new TestTempDir();
        using var workspace = new TestTempDir();
        var       id        = OrderedId('a');
        await CommitUnboundSessionAsync(storeA.Root, workspace.Root, id);
        await CommitUnboundSessionAsync(storeB.Root, workspace.Root, id);
        using var cts = new CancellationTokenSource();
        var       entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var       release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task CancelAppendAsync(string _, SessionStoreWriter writer, string projectId, CancellationToken token)
        {
            entered.SetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cts.Cancel();
            token.ThrowIfCancellationRequested();
        }

        var cancelledTask = SessionProjectInitializer.InitializeAsync(storeA.Root, null, null, null,
                                                                       CancelAppendAsync, cts.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var independent = await SessionProjectInitializer.InitializeAsync(storeB.Root);
            Assert.That(independent.Cancelled, Is.False);
            Assert.That(independent.Outcomes.Single().Status, Is.EqualTo(SessionProjectBindingStatus.Bound));
        }
        finally
        {
            release.TrySetResult();
            await cancelledTask;
        }

        var cancelled = await cancelledTask;
        Assert.That(cancelled.Cancelled, Is.True);
        Assert.That(cancelled.Outcomes, Is.Empty);
        Assert.That(cancelled.NextCursor, Is.Null);
        Assert.That(cancelled.HasMore, Is.True);
        var resumed = await SessionProjectInitializer.InitializeAsync(storeA.Root, cancelled.NextCursor);
        Assert.That(resumed.Outcomes.Single().Status, Is.EqualTo(SessionProjectBindingStatus.Bound));
    }

    [Test]
    public void OpenFailureClassification_ExcludesCancellationAndUnexpectedExceptions()
    {
        Assert.That(SessionStoreWriter.IsRecoverableOpenFailure(new IOException()), Is.True);
        Assert.That(SessionStoreWriter.IsRecoverableOpenFailure(new InvalidOperationException()), Is.True);
        Assert.That(SessionStoreWriter.IsRecoverableOpenFailure(new FormatException()), Is.True);
        Assert.That(SessionStoreWriter.IsRecoverableOpenFailure(new OperationCanceledException()), Is.False);
        Assert.That(SessionStoreWriter.IsRecoverableOpenFailure(new OutOfMemoryException()), Is.False);
        Assert.That(SessionStoreWriter.IsRecoverableOpenFailure(new NullReferenceException()), Is.False);
    }

    [Test]
    public async Task CancelWrappedAppendFailure_KeepsFailedFactAndStops()
    {
        using var store     = new TestTempDir();
        using var workspace = new TestTempDir();
        var idA = await CommitUnboundSessionAsync(store.Root, workspace.Root, OrderedId('a'));
        var idB = await CommitUnboundSessionAsync(store.Root, workspace.Root, OrderedId('b'));
        var idC = await CommitUnboundSessionAsync(store.Root, workspace.Root, OrderedId('c'));
        using var cts = new CancellationTokenSource();
        var result = await SessionProjectInitializer.InitializeAsync(store.Root, null, null, null,
                                                                     (sessionId, writer, projectId, token) =>
                                                                     {
                                                                         if (sessionId != idB)
                                                                             return RealAppendAsync(writer, projectId, token);
                                                                         cts.Cancel();
                                                                         throw new SessionStoreException("injected uncertain append");
                                                                     }, cts.Token);

        Assert.That(result.Cancelled, Is.True);
        var byId = result.Outcomes.ToDictionary(outcome => outcome.SessionId);
        Assert.That(byId[idA].Status, Is.EqualTo(SessionProjectBindingStatus.Bound));
        Assert.That(byId[idB].Status, Is.EqualTo(SessionProjectBindingStatus.Failed));
        TestAssert.Contains("injected uncertain append", byId[idB].Detail!, StringComparison.Ordinal);
        Assert.That(byId.ContainsKey(idC), Is.False);
        Assert.That(result.NextCursor, Is.EqualTo(idB));
        Assert.That(result.HasMore, Is.True);
        var restoredC = await SessionStoreReader.ReadAsync(store.Root, idC, CancellationToken.None);
        Assert.That(restoredC!.ProjectId, Is.Null);
        var remaining = await SessionProjectInitializer.InitializeAsync(store.Root, result.NextCursor);
        Assert.That(remaining.Outcomes.Single().SessionId, Is.EqualTo(idC));
        Assert.That(remaining.Outcomes.Single().Status, Is.EqualTo(SessionProjectBindingStatus.Bound));
    }

    [Test]
    public async Task CancelAfterLastItem_IsReflectedAsCancelled()
    {
        using var store     = new TestTempDir();
        using var workspace = new TestTempDir();
        await CommitUnboundSessionAsync(store.Root, workspace.Root, OrderedId('a'));
        using var cts = new CancellationTokenSource();
        var result = await SessionProjectInitializer.InitializeAsync(store.Root, null, null, null,
                                                                     async (_, writer, projectId, token) =>
                                                                     {
                                                                         await RealAppendAsync(writer, projectId, token);
                                                                         cts.Cancel();
                                                                     }, cts.Token);

        Assert.That(result.Cancelled, Is.True);
        Assert.That(result.Outcomes.Single().Status, Is.EqualTo(SessionProjectBindingStatus.Bound));
        Assert.That(result.HasMore, Is.False);
    }

    [Test]
    public async Task CancelledRun_ReturnsFactsNotFailures_AndRerunCompletes()
    {
        using var store     = new TestTempDir();
        using var workspace = new TestTempDir();
        var ids = new List<string>();
        for (var i = 0; i < 2; i++) ids.Add(await CommitUnboundSessionAsync(store.Root, workspace.Root));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = await SessionProjectInitializer.InitializeAsync(store.Root, cancellationToken : cts.Token);

        Assert.That(cancelled.Cancelled, Is.True);
        Assert.That(cancelled.Outcomes, Is.Empty);
        Assert.That(cancelled.HasMore, Is.True);
        Assert.That(File.Exists(ProjectStore.FilePath(store.Root)), Is.False);
        foreach (var id in ids)
        {
            var snapshot = await SessionStoreReader.ReadAsync(store.Root, id, CancellationToken.None);
            Assert.That(snapshot!.ProjectId, Is.Null);
        }

        var rerun = await SessionProjectInitializer.InitializeAsync(store.Root);
        Assert.That(rerun.Cancelled, Is.False);
        Assert.That(rerun.Outcomes.Count, Is.EqualTo(2));
        Assert.That(rerun.Outcomes.All(outcome => outcome.Status == SessionProjectBindingStatus.Bound), Is.True);
    }

    [Test]
    public async Task BrokenLogs_ReportedFailedWithoutRepair()
    {
        using var store     = new TestTempDir();
        using var workspace = new TestTempDir();
        var validId = await CommitUnboundSessionAsync(store.Root, workspace.Root);

        // 截断：末尾半行。
        var truncatedId   = await CommitUnboundSessionAsync(store.Root, workspace.Root);
        var truncatedPath = Path.Combine(store.Root, truncatedId + SessionStoreWriter.LogFileSuffix);
        await File.AppendAllTextAsync(truncatedPath, "{\"format\": \"tinyharness");

        // 损坏：第二批次中部字节翻转。
        var corruptId       = await CommitUnboundSessionAsync(store.Root, workspace.Root);
        var corruptPath     = Path.Combine(store.Root, corruptId + SessionStoreWriter.LogFileSuffix);
        var corruptBytes    = await File.ReadAllBytesAsync(corruptPath);
        var secondLineStart = corruptBytes.AsSpan().IndexOf((byte)'\n') + 1;
        corruptBytes[secondLineStart + 5] ^= 0x20;
        await File.WriteAllBytesAsync(corruptPath, corruptBytes);

        // 超限：文件长度超过日志预算。
        var oversizedId   = await CommitUnboundSessionAsync(store.Root, workspace.Root);
        var oversizedPath = Path.Combine(store.Root, oversizedId + SessionStoreWriter.LogFileSuffix);
        using (var stream = File.Open(oversizedPath, FileMode.Open))
        {
            stream.SetLength(SessionStoreWriter.MaxLogBytes + 1);
        }

        var bytesBefore = await ReadAllLogBytesAsync(store.Root);
        var result = await SessionProjectInitializer.InitializeAsync(store.Root);
        var byId = result.Outcomes.ToDictionary(outcome => outcome.SessionId);

        Assert.That(byId[validId].Status, Is.EqualTo(SessionProjectBindingStatus.Bound));
        foreach (var brokenId in new[] { truncatedId, corruptId, oversizedId })
        {
            Assert.That(byId[brokenId].Status, Is.EqualTo(SessionProjectBindingStatus.Failed), brokenId);
            Assert.That(byId[brokenId].Detail, Is.Not.Null);
        }

        // 损坏日志原字节保持不变，不修复、不截断、不追加。
        var bytesAfter = await ReadAllLogBytesAsync(store.Root);
        foreach (var brokenId in new[] { truncatedId, corruptId, oversizedId })
        {
            var key = brokenId + SessionStoreWriter.LogFileSuffix;
            Assert.That(bytesAfter[key].AsSpan().SequenceEqual(bytesBefore[key]), Is.True,
                        $"Broken log '{key}' was modified.");
        }
    }

    [Test]
    public async Task DamagedCatalog_ReportedFailed_AndNotOverwritten()
    {
        using var store     = new TestTempDir();
        using var workspace = new TestTempDir();
        await CommitUnboundSessionAsync(store.Root, workspace.Root);
        var catalogPath = ProjectStore.FilePath(store.Root);
        var damaged     = Encoding.UTF8.GetBytes("{ this is not a valid catalog");
        await File.WriteAllBytesAsync(catalogPath, damaged);

        var result = await SessionProjectInitializer.InitializeAsync(store.Root);

        var outcome = result.Outcomes.Single();
        Assert.That(outcome.Status, Is.EqualTo(SessionProjectBindingStatus.Failed));
        Assert.That(outcome.Detail, Is.Not.Null);
        Assert.That(await File.ReadAllBytesAsync(catalogPath), Is.EqualTo(damaged));
        var sessionLog = Directory.GetFiles(store.Root, "*" + SessionStoreWriter.LogFileSuffix).Single();
        var restored = await SessionStoreReader.ReadAsync(store.Root,
            Path.GetFileName(sessionLog)[..^SessionStoreWriter.LogFileSuffix.Length], CancellationToken.None);
        TestAssert.NotNull(restored);
        Assert.That(restored!.ProjectId, Is.Null);
    }

    [Test]
    public async Task CatalogIoError_FailsCandidatesItemByItem_BoundEntryUnaffected()
    {
        using var store     = new TestTempDir();
        using var workspace = new TestTempDir();
        // 目录册位置被目录占位：保存/读取目录册产生 IO 错误。
        Directory.CreateDirectory(ProjectStore.FilePath(store.Root));
        var unboundId = await CommitUnboundSessionAsync(store.Root, workspace.Root, OrderedId('a'));
        var boundSession = new ChatSession(OrderedId('b'), workspace.Root, Model, null,
                                           Guid.NewGuid().ToString("N"));
        var boundContext = new ConversationContext();
        boundContext.Append(ChatMessage.System("system"));
        AddTurn(boundSession, "q", "a");
        boundContext.Append(ChatMessage.User("q"));
        using (var writer = new SessionStoreWriter(store.Root, boundSession.Id))
        {
            await writer.CommitAsync(boundSession, boundContext, CancellationToken.None);
        }

        var result = await SessionProjectInitializer.InitializeAsync(store.Root);

        var byId = result.Outcomes.ToDictionary(outcome => outcome.SessionId);
        Assert.That(byId[unboundId].Status, Is.EqualTo(SessionProjectBindingStatus.Failed));
        Assert.That(byId[unboundId].Detail, Is.Not.Null);
        Assert.That(byId[boundSession.Id].Status, Is.EqualTo(SessionProjectBindingStatus.AlreadyBound));
        // 目录占位不被覆盖，未绑定日志未被写入。
        Assert.That(Directory.Exists(ProjectStore.FilePath(store.Root)), Is.True);
        Assert.That(Directory.GetFiles(ProjectStore.FilePath(store.Root)), Is.Empty);
        var restored = await SessionStoreReader.ReadAsync(store.Root, unboundId, CancellationToken.None);
        Assert.That(restored!.ProjectId, Is.Null);
    }

    [Test]
    public async Task Initialization_PreservesPrefixHistoryAndActivityTimes()
    {
        using var store     = new TestTempDir();
        using var workspace = new TestTempDir();
        var sessionId = await CommitUnboundSessionAsync(store.Root, workspace.Root);
        var       historyBefore = await ReadAllHistoryPagesAsync(store.Root, sessionId);
        Assert.That(historyBefore.Count, Is.EqualTo(2));
        var path = Path.Combine(store.Root, sessionId + SessionStoreWriter.LogFileSuffix);
        var prefix = await File.ReadAllBytesAsync(path);
        var before = await SessionStoreReader.ReadAsync(store.Root, sessionId, CancellationToken.None);
        TestAssert.NotNull(before);
        Assert.That(before!.ProjectId, Is.Null);
        Assert.That(before.LastActivityUtc, Is.EqualTo(before.UpdatedUtc));

        var result = await SessionProjectInitializer.InitializeAsync(store.Root);
        Assert.That(result.Outcomes.Single().Status, Is.EqualTo(SessionProjectBindingStatus.Bound));

        var bytes = await File.ReadAllBytesAsync(path);
        Assert.That(bytes.AsSpan(0, prefix.Length).SequenceEqual(prefix), Is.True);
        var after = await SessionStoreReader.ReadAsync(store.Root, sessionId, CancellationToken.None);
        TestAssert.NotNull(after);
        Assert.That(after!.ProjectId, Is.EqualTo(result.Outcomes.Single().ProjectId));
        Assert.That(after.CommitSequence, Is.EqualTo(before.CommitSequence + 1));
        Assert.That(after.UpdatedUtc, Is.GreaterThanOrEqualTo(before.UpdatedUtc));
        Assert.That(after.LastActivityUtc, Is.EqualTo(before.UpdatedUtc));
        Assert.That(after.Turns.Count, Is.EqualTo(before.Turns.Count));
        Assert.That(after.Turns[0].Runs.Single().FinalMessage, Is.EqualTo(before.Turns[0].Runs.Single().FinalMessage));
        Assert.That(after.Conversation!.Messages.Count, Is.EqualTo(before.Conversation!.Messages.Count));
        var historyAfter = await ReadAllHistoryPagesAsync(store.Root, sessionId);
        Assert.That(historyAfter.Count, Is.EqualTo(historyBefore.Count));
    }

    [Test]
    public async Task TryOpenExisting_ClassifiesOpenedLockHeldAndFailed()
    {
        using var store     = new TestTempDir();
        using var workspace = new TestTempDir();
        var sessionId = await CommitUnboundSessionAsync(store.Root, workspace.Root);

        var opened = await SessionStoreWriter.TryOpenExistingAsync(store.Root, sessionId, null,
                                                                   CancellationToken.None);
        Assert.That(opened.Status, Is.EqualTo(SessionWriterOpenStatus.Opened));
        Assert.That(opened.Writer, Is.Not.Null);
        Assert.That(opened.Writer!.IsHealthy, Is.True);
        Assert.That(opened.Writer.LoadedSnapshot, Is.Not.Null);
        opened.Writer.Dispose();

        using (var held = await SessionStoreWriter.OpenExistingAsync(store.Root, sessionId, null,
                                                                     CancellationToken.None))
        {
            var blocked = await SessionStoreWriter.TryOpenExistingAsync(store.Root, sessionId, null,
                                                                        CancellationToken.None);
            Assert.That(blocked.Status, Is.EqualTo(SessionWriterOpenStatus.LockHeld));
            Assert.That(blocked.Writer, Is.Null);
            TestAssert.Contains("another writer", blocked.Error!.Message, StringComparison.Ordinal);
        }

        var missing = await SessionStoreWriter.TryOpenExistingAsync(store.Root, NewId(), null,
                                                                    CancellationToken.None);
        Assert.That(missing.Status, Is.EqualTo(SessionWriterOpenStatus.Failed));
        Assert.That(missing.Writer, Is.Null);
        Assert.That(missing.Error, Is.Not.Null);
    }

    [Test]
    public async Task TryOpenExisting_NonWhitelistedException_ReleasesLockImmediately()
    {
        using var store     = new TestTempDir();
        using var workspace = new TestTempDir();
        // writtenUtc 为数字触发严格读取中的 InvalidOperationException（不在任何异常白名单内）。
        var       sessionId = OrderedId('a');
        await WriteHandCraftedIdentityBatchAsync(store.Root, sessionId, JsonValue.Create(12345), workspace.Root);

        var first = await SessionStoreWriter.TryOpenExistingAsync(store.Root, sessionId, null,
                                                                  CancellationToken.None);
        Assert.That(first.Status, Is.EqualTo(SessionWriterOpenStatus.Failed));
        Assert.That(first.Writer, Is.Null);
        TestAssert.Contains("ownership initialization", first.Error!.Message, StringComparison.Ordinal);

        // 锁未被泄漏：立即再次尝试得到同样的读取失败，而不是 LockHeld。
        var second = await SessionStoreWriter.TryOpenExistingAsync(store.Root, sessionId, null,
                                                                   CancellationToken.None);
        Assert.That(second.Status, Is.EqualTo(SessionWriterOpenStatus.Failed));
        Assert.That(second.Error, Is.Not.Null);

        // 删除损坏日志换写合法内容后，同一会话立即可正常打开。
        var path = Path.Combine(store.Root, sessionId + SessionStoreWriter.LogFileSuffix);
        File.Delete(path);
        File.Delete(Path.Combine(store.Root, sessionId + ".lock"));
        var sessionId2 = await CommitUnboundSessionAsync(store.Root, workspace.Root, sessionId);
        var opened = await SessionStoreWriter.TryOpenExistingAsync(store.Root, sessionId2, null,
                                                                   CancellationToken.None);
        Assert.That(opened.Status, Is.EqualTo(SessionWriterOpenStatus.Opened));
        opened.Writer!.Dispose();
    }

    private static async Task<List<SessionHistoryItem>> ReadAllHistoryPagesAsync(string storeRoot, string sessionId)
    {
        var                   reader = new SessionHistoryReader();
        var                   items  = new List<SessionHistoryItem>();
        SessionHistoryCursor? cursor = null;
        do
        {
            var page = await reader.ReadPageAsync(storeRoot, sessionId, SessionHistoryReader.MaxPageItems, cursor,
                                                  CancellationToken.None);
            items.AddRange(page.Items);
            cursor = page.NextCursor;
        } while (cursor is not null);

        return items;
    }
}
