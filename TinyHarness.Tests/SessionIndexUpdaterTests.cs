using System.Text.Json;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Agent;
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
///     <para>验证 writer 提交通知与索引更新器的增量快路径、整份扫描回退、归档投影隔离与故障隔离。</para>
///     <para>
///         Verifies the writer commit notification and the index updater's incremental fast path, full-scan
///         fallback, archive-projection isolation, and failure isolation.
///     </para>
/// </summary>
public class SessionIndexUpdaterTests
{
    private static readonly SessionModelIdentity Identity =
        new("offline", "https://offline.invalid/v1", ChatApiKind.ChatCompletions);

    /// <summary>
    ///     <para>接线 writer 的提交逐批次发布摘要，摘要更新不覆盖归档投影。</para>
    ///     <para>
    ///         A wired writer publishes summaries batch by batch, and summary updates never overwrite the archived
    ///         projection.
    ///     </para>
    /// </summary>
    [Test]
    public async Task WiredWriter_PublishesSummariesAndKeepsArchiveProjection()
    {
        using var dir   = new TestTempDir();
        var       index = new SessionIndexStore(dir.Root);
        await index.InitializeAsync(CancellationToken.None);
        var    updater = new SessionIndexUpdater(index);
        var    client  = new FakeChatClient();
        string sessionId;
        using (var host = NewHost(dir.Root, client, updater))
        {
            client.Enqueue(FakeChatClient.Text("answer one"));
            await host.SubmitTurnAsync("first question", CancellationToken.None);

            var stored = await SessionStoreReader.ReadAsync(dir.Root, host.Session.Id, CancellationToken.None);
            var row    = await index.GetRowAsync(host.Session.Id, CancellationToken.None);
            Assert.That(row, Is.Not.Null);
            Assert.That(row!.State, Is.EqualTo(SessionIndexEntryState.Current));
            Assert.That(row.Summary!.CommitSequence, Is.EqualTo(stored!.CommitSequence));
            Assert.That(row.Summary.Title, Is.EqualTo("first question"));
            Assert.That(row.Summary.TurnCount, Is.EqualTo(1));

            await index.SyncArchiveProjectionAsync(
                                                   new HashSet<string>([host.Session.Id], StringComparer.Ordinal),
                                                   CancellationToken.None);

            client.Enqueue(FakeChatClient.Text("answer two"));
            await host.SubmitTurnAsync("second question", CancellationToken.None);

            var advanced    = await SessionStoreReader.ReadAsync(dir.Root, host.Session.Id, CancellationToken.None);
            var advancedRow = await index.GetRowAsync(host.Session.Id, CancellationToken.None);
            Assert.That(advancedRow!.Summary!.CommitSequence, Is.EqualTo(advanced!.CommitSequence));
            Assert.That(advancedRow.Summary.TurnCount, Is.EqualTo(2));
            Assert.That(advancedRow.Summary.IndexedBytes,
                        Is.EqualTo(new FileInfo(Path.Combine(dir.Root,
                                                             host.Session.Id +
                                                             SessionStoreWriter.LogFileSuffix)).Length));
            Assert.That(advancedRow.Archived, Is.True, "摘要推进不得覆盖归档投影");
            sessionId = host.Session.Id;
        }

        Assert.That(updater.LastFailure, Is.Null);

        // 摘要与严格读取的列表字段一致；索引按毫秒存储时间，比较按同一截断口径。
        var rebuilt = await SessionSummaryBuilder.BuildAsync(dir.Root, sessionId, CancellationToken.None);
        var final   = await index.GetRowAsync(sessionId, CancellationToken.None);
        Assert.That(final!.Summary!.LastActivityUtc,
                    Is.EqualTo(TruncateToMilliseconds(rebuilt!.Summary.LastActivityUtc)));
        Assert.That(final.Summary.UpdatedUtc, Is.EqualTo(TruncateToMilliseconds(rebuilt.Summary.UpdatedUtc)));
        Assert.That(final.Summary.ProjectAssignmentState, Is.EqualTo(rebuilt.Summary.ProjectAssignmentState));
    }

    /// <summary>
    ///     <para>提交通知在序号推进后携带批次前缀长度；通知抛出异常不影响提交或 writer 健康。</para>
    ///     <para>
    ///         The commit notification fires after the sequence advance carrying the batch's prefix length; a throwing
    ///         notification leaves the commit and writer health untouched.
    ///     </para>
    /// </summary>
    [Test]
    public async Task BatchCommittedHook_FiresAfterCommitAndSurvivesFailures()
    {
        using var dir           = new TestTempDir();
        var       created       = DateTimeOffset.UtcNow;
        var       sessionId     = Guid.NewGuid().ToString("N");
        var       notifications = new List<(long Sequence, long PrefixLength)>();
        using (var writer = new SessionStoreWriter(dir.Root, sessionId, null,
                                                   (batch, prefixLength, _) =>
                                                   {
                                                       notifications.Add((batch.Sequence, prefixLength));
                                                       return ValueTask.CompletedTask;
                                                   }))
        {
            await writer.CommitAsync(new ChatSession(sessionId, dir.Root, Identity, created),
                                     new ConversationContext(new ContextOptions()), CancellationToken.None);
            Assert.That(writer.CommitSequence, Is.EqualTo(1));
            Assert.That(notifications, Has.Count.EqualTo(1));
            Assert.That(notifications[0].Sequence, Is.EqualTo(1));
            Assert.That(notifications[0].PrefixLength, Is.EqualTo(0), "首个批次前日志为空");
        }

        var failingId = Guid.NewGuid().ToString("N");
        using (var failing = new SessionStoreWriter(dir.Root, failingId, null,
                                                    (_, _, _) => ValueTask.FromException(
                                                     new InvalidOperationException(
                                                      "index down"))))
        {
            await Assert.DoesNotThrowAsync(() => failing.CommitAsync(
                                                                     new ChatSession(failingId, dir.Root, Identity,
                                                                         created),
                                                                     new ConversationContext(new ContextOptions()),
                                                                     CancellationToken.None));
            Assert.That(failing.IsHealthy, Is.True);
        }

        var stored = await SessionStoreReader.ReadAsync(dir.Root, failingId, CancellationToken.None);
        Assert.That(stored, Is.Not.Null, "通知失败不得影响已提交日志");
    }

    /// <summary>
    ///     <para>索引无行时回退整份扫描：旧进程写入的日志在首次提交通知后收敛为完整摘要。</para>
    ///     <para>
    ///         A missing row falls back to a full scan: logs written by a legacy process converge into a complete
    ///         summary at the first commit notification.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Updater_MissingRow_FallsBackToFullRebuild()
    {
        using var dir   = new TestTempDir();
        var       index = new SessionIndexStore(dir.Root);
        await index.InitializeAsync(CancellationToken.None);
        var    client = new FakeChatClient();
        string sessionId;
        using (var host = NewHost(dir.Root, client))
        {
            client.Enqueue(FakeChatClient.Text("legacy answer"));
            await host.SubmitTurnAsync("legacy question", CancellationToken.None);
            sessionId = host.Session.Id;
        }

        var updater   = new SessionIndexUpdater(index);
        var projectId = "0123456789abcdef0123456789abcdef";
        using (var writer =
               await SessionStoreWriter.OpenExistingAsync(dir.Root, sessionId, null, CancellationToken.None,
                                                          updater.HandleBatchCommittedAsync))
        {
            await writer.AppendRecordsAsync(
            [
                new SessionLogRecord(string.Empty, "project-binding",
                                     JsonSerializer.SerializeToElement(
                                                                       new
                                                                           SessionLogProjectBinding(SessionLogProjectBinding
                                                                              .SchemaName, projectId),
                                                                       SessionLogJsonContext.Default
                                                                          .SessionLogProjectBinding))
            ], CancellationToken.None);
        }

        Assert.That(updater.LastFailure, Is.Null);
        var stored = await SessionStoreReader.ReadAsync(dir.Root, sessionId, CancellationToken.None);
        var row    = await index.GetRowAsync(sessionId, CancellationToken.None);
        Assert.That(row!.State, Is.EqualTo(SessionIndexEntryState.Current));
        Assert.That(row.Summary!.ProjectId, Is.EqualTo(projectId));
        Assert.That(row.Summary.ProjectAssignmentState, Is.EqualTo(SessionProjectAssignmentState.Project));
        Assert.That(row.Summary.TurnCount, Is.EqualTo(1));
        Assert.That(row.Summary.CommitSequence, Is.EqualTo(stored!.CommitSequence));
        Assert.That(row.Summary.Title, Is.EqualTo("legacy question"));
        Assert.That(row.Summary.LastActivityUtc, Is.EqualTo(TruncateToMilliseconds(stored.LastActivityUtc)));
    }

    /// <summary>
    ///     <para>既有行来源前缀不连续时拒绝增量快路径，整份扫描产出与来源一致的摘要。</para>
    ///     <para>
    ///         A discontinuous source prefix refuses the incremental fast path, and a full scan produces the
    ///         source-consistent summary.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Updater_StaleRowPrefix_TriggersFullRebuild()
    {
        using var dir   = new TestTempDir();
        var       index = new SessionIndexStore(dir.Root);
        await index.InitializeAsync(CancellationToken.None);
        var    client = new FakeChatClient();
        string sessionId;
        using (var host = NewHost(dir.Root, client))
        {
            client.Enqueue(FakeChatClient.Text("answer"));
            await host.SubmitTurnAsync("stale row question", CancellationToken.None);
            sessionId = host.Session.Id;
        }

        // 预置一条来源标记与实际日志不连续的过期健康行。
        var stale = new SessionSummary
        {
            Id                     = sessionId,
            WorkspaceRoot          = "/tmp/workspace",
            ProjectAssignmentState = SessionProjectAssignmentState.NoProject,
            Title                  = "stale",
            CreatedUtc             = DateTimeOffset.UtcNow,
            UpdatedUtc             = DateTimeOffset.UtcNow,
            LastActivityUtc        = DateTimeOffset.UtcNow,
            CommitSequence         = 1,
            IndexedBytes           = 10
        };
        await index.WriteSummaryAsync(stale, 10, null, CancellationToken.None);

        var updater = new SessionIndexUpdater(index);
        using (var writer =
               await SessionStoreWriter.OpenExistingAsync(dir.Root, sessionId, null, CancellationToken.None,
                                                          updater.HandleBatchCommittedAsync))
        {
            await writer.AppendRecordsAsync(
            [
                new SessionLogRecord(string.Empty, "project-assignment",
                                     JsonSerializer.SerializeToElement(
                                                                       new
                                                                           SessionLogProjectAssignment(SessionLogProjectAssignment
                                                                                  .SchemaName,
                                                                               SessionLogProjectAssignment
                                                                                  .NoProject),
                                                                       SessionLogJsonContext.Default
                                                                          .SessionLogProjectAssignment))
            ], CancellationToken.None);
        }

        Assert.That(updater.LastFailure, Is.Null);
        var stored = await SessionStoreReader.ReadAsync(dir.Root, sessionId, CancellationToken.None);
        var row    = await index.GetRowAsync(sessionId, CancellationToken.None);
        Assert.That(row!.State, Is.EqualTo(SessionIndexEntryState.Current));
        Assert.That(row.Summary!.CommitSequence, Is.EqualTo(stored!.CommitSequence));
        Assert.That(row.Summary.IndexedBytes,
                    Is.EqualTo(new FileInfo(Path.Combine(dir.Root,
                                                         sessionId + SessionStoreWriter.LogFileSuffix)).Length));
        Assert.That(row.Summary.ProjectAssignmentState, Is.EqualTo(SessionProjectAssignmentState.NoProject));
        Assert.That(row.Summary.Title, Is.EqualTo("stale row question"), "整份扫描恢复首回合标题");
        Assert.That(row.SourceLength,
                    Is.EqualTo(new FileInfo(Path.Combine(dir.Root,
                                                         sessionId + SessionStoreWriter.LogFileSuffix)).Length));
    }

    /// <summary>
    ///     <para>索引损坏时更新器停用并留痕，writer 提交与日志不受影响。</para>
    ///     <para>
    ///         A damaged index disables the updater with a recorded note while writer commits and the journal stay
    ///         unaffected.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Updater_DamagedIndex_StopsWithoutBreakingCommits()
    {
        using var dir   = new TestTempDir();
        var       index = new SessionIndexStore(dir.Root);
        await index.InitializeAsync(CancellationToken.None);
        await File.WriteAllBytesAsync(index.DatabasePath, [0x50, 0x4b, 0x03, 0x04, 1, 2, 3], CancellationToken.None);
        var    updater = new SessionIndexUpdater(index);
        var    client  = new FakeChatClient();
        string sessionId;
        using (var host = NewHost(dir.Root, client, updater))
        {
            client.Enqueue(FakeChatClient.Text("answer"));
            await host.SubmitTurnAsync("question despite damage", CancellationToken.None);
            sessionId = host.Session.Id;
            var stored = await SessionStoreReader.ReadAsync(dir.Root, sessionId, CancellationToken.None);
            Assert.That(stored, Is.Not.Null, "索引故障不得影响已提交日志");
        }

        Assert.That(updater.LastFailure, Is.Not.Null);
        await Assert.ThrowsAsync<SessionIndexException>(() => index.GetRowAsync(sessionId, CancellationToken.None));
    }

    private static ChatSessionHost NewHost(string store, FakeChatClient client, SessionIndexUpdater? updater = null) =>
        new(store,
            Identity,
            new AgentOptions { Model = "offline", MaxAgentSteps = 10, DefaultToolTimeoutSeconds = 30 },
            client,
            new ToolRegistry([]),
            "You are a test harness.",
            sessionWriterFactory : id => new SessionStoreWriter(store, id, null,
                                                                updater is null
                                                                    ? null
                                                                    : updater.HandleBatchCommittedAsync));

    /// <summary>
    ///     <para>把时间截断到毫秒，与索引存储口径一致。</para>
    ///     <para>Truncates a timestamp to milliseconds, matching the index storage precision.</para>
    /// </summary>
    private static DateTimeOffset TruncateToMilliseconds(DateTimeOffset value) =>
        DateTimeOffset.FromUnixTimeMilliseconds(value.ToUnixTimeMilliseconds());
}
