using System.Text;
using System.Text.Json;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         验证列表索引对账：缺失与过期条目整份重建、未变来源零正文扫描、变短／替换来源先标记后重建、
///         孤儿行移除与新版本拒绝。
///     </para>
///     <para>
///         Verifies list-index reconciliation: missing and stale entries rebuild wholly, unchanged sources skip body
///         scans, shrunk or replaced sources mark failed before rebuilding, orphan rows are removed, and newer
///         schemas are refused.
///     </para>
/// </summary>
public class SessionIndexReconcilerTests
{
    private static readonly SessionModelIdentity Identity =
        new("offline", "https://offline.invalid/v1", ChatApiKind.ChatCompletions);

    /// <summary>
    ///     <para>空索引整份建立全部条目，归档投影从可读来源同步。</para>
    ///     <para>An empty index builds every entry wholly, syncing the archived projection from a readable source.</para>
    /// </summary>
    [Test]
    public async Task Reconcile_BuildsMissingEntriesAndSyncsArchive()
    {
        using var dir   = new TestTempDir();
        var       index = new SessionIndexStore(dir.Root);
        await index.InitializeAsync(CancellationToken.None);
        var ids = await SeedSessionsAsync(dir.Root, "question 0", "question 1");
        await SessionStoreArchiveStore.SetArchivedAsync(dir.Root, ids[0], true, CancellationToken.None);

        var report = await new SessionIndexReconciler(index).ReconcileAsync(CancellationToken.None);
        Assert.That(report.Availability.Status, Is.EqualTo(SessionIndexAvailabilityStatus.Available));
        Assert.That(report.SourceCount, Is.EqualTo(2));
        Assert.That(report.Rebuilt, Is.EqualTo(2));
        Assert.That(report.ArchiveSynced, Is.True);

        var archivedRow    = await index.GetRowAsync(ids[0], CancellationToken.None);
        var unarchivedRow  = await index.GetRowAsync(ids[1], CancellationToken.None);
        var storedArchived = await SessionStoreReader.ReadAsync(dir.Root, ids[0], CancellationToken.None);
        Assert.That(archivedRow!.Archived, Is.True);
        Assert.That(unarchivedRow!.Archived, Is.False);
        Assert.That(archivedRow.Summary!.CommitSequence, Is.EqualTo(storedArchived!.CommitSequence));
        Assert.That(archivedRow.Summary.TurnCount, Is.EqualTo(1));
    }

    /// <summary>
    ///     <para>来源标记一致时跳过正文扫描：预置的哨兵标题原样保留。</para>
    ///     <para>
    ///         Matching source markers skip body scans: a preseeded sentinel title survives reconciliation.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Reconcile_SkipsUnchangedSourcesWithoutRescanning()
    {
        using var dir   = new TestTempDir();
        var       index = new SessionIndexStore(dir.Root);
        await index.InitializeAsync(CancellationToken.None);
        var ids = await SeedSessionsAsync(dir.Root, "marked question");
        await new SessionIndexReconciler(index).ReconcileAsync(CancellationToken.None);
        var row = await index.GetRowAsync(ids[0], CancellationToken.None);

        // 改写行内摘要并保持来源标记不变；对账必须零正文扫描地保留该行。
        await index.WriteSummaryAsync(row!.Summary! with { Title = "sentinel-title" },
                                      row.SourceLength, row.SourceModifiedUtc, CancellationToken.None);
        var second = await new SessionIndexReconciler(index).ReconcileAsync(CancellationToken.None);
        Assert.That(second.Unchanged, Is.EqualTo(1));
        Assert.That(second.Rebuilt, Is.EqualTo(0));
        Assert.That((await index.GetRowAsync(ids[0], CancellationToken.None))!.Summary!.Title,
                    Is.EqualTo("sentinel-title"));
    }

    /// <summary>
    ///     <para>来源在无索引更新下提交（模拟提交后崩溃或旧端写入）由对账追平。</para>
    ///     <para>
    ///         A source committed without an index update (simulating a post-commit crash or a legacy writer) is
    ///         caught up by reconciliation.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Reconcile_CatchesUpSourceCommittedWithoutIndex()
    {
        using var dir   = new TestTempDir();
        var       index = new SessionIndexStore(dir.Root);
        await index.InitializeAsync(CancellationToken.None);
        var ids = await SeedSessionsAsync(dir.Root, "catch-up question");
        await new SessionIndexReconciler(index).ReconcileAsync(CancellationToken.None);

        // 追加归属批次：JSONL 已提交而索引未更新。
        var projectId = "0123456789abcdef0123456789abcdef";
        using (var writer =
               await SessionStoreWriter.OpenExistingAsync(dir.Root, ids[0], null, CancellationToken.None))
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

        var report = await new SessionIndexReconciler(index).ReconcileAsync(CancellationToken.None);
        Assert.That(report.Rebuilt, Is.EqualTo(1));
        Assert.That(report.Unchanged, Is.EqualTo(0));
        var stored = await SessionStoreReader.ReadAsync(dir.Root, ids[0], CancellationToken.None);
        var row    = await index.GetRowAsync(ids[0], CancellationToken.None);
        Assert.That(row!.Summary!.ProjectId, Is.EqualTo(projectId));
        Assert.That(row.Summary.CommitSequence, Is.EqualTo(stored!.CommitSequence));
    }

    /// <summary>
    ///     <para>来源变短（截断或替换）时健康行先标记失败再重建，不被条件提交拒绝。</para>
    ///     <para>
    ///         A shrunk source (truncation or replacement) marks the healthy row failed before rebuilding, never
    ///         rejected by the conditional commit.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Reconcile_ShrunkSource_MarksFailedThenRebuilds()
    {
        using var dir   = new TestTempDir();
        var       index = new SessionIndexStore(dir.Root);
        await index.InitializeAsync(CancellationToken.None);
        var ids = await SeedSessionsAsync(dir.Root, "first question", "second question");
        await new SessionIndexReconciler(index).ReconcileAsync(CancellationToken.None);

        // 来源被替换为只含首个批次的更短日志。
        var logPath = Path.Combine(dir.Root, ids[0] + SessionStoreWriter.LogFileSuffix);
        var lines   = await File.ReadAllLinesAsync(logPath, CancellationToken.None);
        await File.WriteAllBytesAsync(logPath, Encoding.UTF8.GetBytes(lines[0] + "\n"), CancellationToken.None);

        var report = await new SessionIndexReconciler(index).ReconcileAsync(CancellationToken.None);
        Assert.That(report.MarkedFailed, Is.EqualTo(1));
        Assert.That(report.Rebuilt, Is.EqualTo(1));
        var row = await index.GetRowAsync(ids[0], CancellationToken.None);
        Assert.That(row!.State, Is.EqualTo(SessionIndexEntryState.Current));
        Assert.That(row.Summary!.CommitSequence, Is.EqualTo(1));
        Assert.That(row.Summary.TurnCount, Is.EqualTo(1));
        Assert.That(row.Error, Is.Null);
    }

    /// <summary>
    ///     <para>同长但修改时间变化的替换被检出；替换内容损坏时保留旧摘要并显式标记失败。</para>
    ///     <para>
    ///         A same-length replacement is caught via its modification time; damaged replacement content keeps the
    ///         old summary with an explicit failure mark.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Reconcile_SameLengthReplacement_DetectsAndKeepsOldSummary()
    {
        using var dir   = new TestTempDir();
        var       index = new SessionIndexStore(dir.Root);
        await index.InitializeAsync(CancellationToken.None);
        var ids = await SeedSessionsAsync(dir.Root, "first question", "second question");
        await new SessionIndexReconciler(index).ReconcileAsync(CancellationToken.None);
        var before = await index.GetRowAsync(ids[0], CancellationToken.None);

        // 同字节长度破坏末行 JSON：长度与旧标记一致，仅修改时间变化。
        var logPath  = Path.Combine(dir.Root, ids[0] + SessionStoreWriter.LogFileSuffix);
        var lines    = await File.ReadAllLinesAsync(logPath, CancellationToken.None);
        var tampered = lines[^1][..^1] + "x";
        await File.WriteAllBytesAsync(logPath,
                                      Encoding.UTF8.GetBytes(string.Join('\n', lines[..^1]) + "\n" + tampered + "\n"),
                                      CancellationToken.None);

        var report = await new SessionIndexReconciler(index).ReconcileAsync(CancellationToken.None);
        Assert.That(report.Rebuilt, Is.EqualTo(0));
        var row = await index.GetRowAsync(ids[0], CancellationToken.None);
        Assert.That(row!.State, Is.EqualTo(SessionIndexEntryState.Failed));
        Assert.That(row.Error, Is.Not.Null);
        Assert.That(row.Summary, Is.Not.Null, "失败标记保留旧摘要供展示");
        Assert.That(row.Summary!.CommitSequence, Is.EqualTo(before!.Summary!.CommitSequence));
    }

    /// <summary>
    ///     <para>来源被外部独占不可读时条目标记失败并保留旧摘要，本轮其余条目与归档同步继续，解锁后重建恢复。</para>
    ///     <para>
    ///         An externally locked, unreadable source marks its entry failed with the old summary kept while the
    ///         round continues; unlocking lets a later round rebuild it back to health.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Reconcile_OccupiedSource_MarksFailedAndContinuesRound()
    {
        using var dir   = new TestTempDir();
        var       index = new SessionIndexStore(dir.Root);
        await index.InitializeAsync(CancellationToken.None);
        var ids = await SeedSessionsAsync(dir.Root, "occupied question", "free question");
        await new SessionIndexReconciler(index).ReconcileAsync(CancellationToken.None);
        var before = await index.GetRowAsync(ids[0], CancellationToken.None);

        // 追加归属批次使来源标记过期，迫使对账读取正文；再独占日志模拟外部占用。
        // Appending an ownership batch stales the source markers and forces a body read; the exclusive handle
        // then simulates an external occupant.
        var projectId = "0123456789abcdef0123456789abcdef";
        using (var writer =
               await SessionStoreWriter.OpenExistingAsync(dir.Root, ids[0], null, CancellationToken.None))
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

        var logPath = Path.Combine(dir.Root, ids[0] + SessionStoreWriter.LogFileSuffix);
        using (new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var report = await new SessionIndexReconciler(index).ReconcileAsync(CancellationToken.None);
            Assert.That(report.MarkedFailed, Is.EqualTo(1));
            Assert.That(report.Unchanged, Is.EqualTo(1), "被占用条目不得中止本轮，其余条目继续对账");
            Assert.That(report.ArchiveSynced, Is.True);
            var occupied = await index.GetRowAsync(ids[0], CancellationToken.None);
            Assert.That(occupied!.State, Is.EqualTo(SessionIndexEntryState.Failed));
            Assert.That(occupied.Error, Is.Not.Null);
            Assert.That(occupied.Summary!.CommitSequence,
                        Is.EqualTo(before!.Summary!.CommitSequence), "失败标记保留旧摘要");
        }

        var recovery = await new SessionIndexReconciler(index).ReconcileAsync(CancellationToken.None);
        Assert.That(recovery.Rebuilt, Is.EqualTo(1));
        var restored = await index.GetRowAsync(ids[0], CancellationToken.None);
        Assert.That(restored!.State, Is.EqualTo(SessionIndexEntryState.Current));
        Assert.That(restored.Error, Is.Null);
        Assert.That(restored.Summary!.ProjectId, Is.EqualTo(projectId));
    }

    /// <summary>
    ///     <para>来源日志删除后索引行随之移除。</para>
    ///     <para>Deleting a source log removes its index row.</para>
    /// </summary>
    [Test]
    public async Task Reconcile_RemovesRowsWithoutSourceLogs()
    {
        using var dir   = new TestTempDir();
        var       index = new SessionIndexStore(dir.Root);
        await index.InitializeAsync(CancellationToken.None);
        var ids = await SeedSessionsAsync(dir.Root, "doomed question");
        await new SessionIndexReconciler(index).ReconcileAsync(CancellationToken.None);
        File.Delete(Path.Combine(dir.Root, ids[0] + SessionStoreWriter.LogFileSuffix));

        var report = await new SessionIndexReconciler(index).ReconcileAsync(CancellationToken.None);
        Assert.That(report.Removed, Is.EqualTo(1));
        Assert.That(await index.GetRowAsync(ids[0], CancellationToken.None), Is.Null);
    }

    /// <summary>
    ///     <para>更高 schema 版本被拒绝对账且不清空既有元数据。</para>
    ///     <para>A newer schema version refuses reconciliation without clearing existing metadata.</para>
    /// </summary>
    [Test]
    public async Task Reconcile_RefusesNewerSchemaWithoutClearing()
    {
        using var dir   = new TestTempDir();
        var       index = new SessionIndexStore(dir.Root);
        await index.InitializeAsync(CancellationToken.None);
        await SeedSessionsAsync(dir.Root, "future question");
        await index.SetMetaAsync("schema_version", "999", CancellationToken.None);

        var report = await new SessionIndexReconciler(index).ReconcileAsync(CancellationToken.None);
        Assert.That(report.Availability.Status, Is.EqualTo(SessionIndexAvailabilityStatus.Unsupported));
        Assert.That(report.SourceCount, Is.EqualTo(0));
        Assert.That(await index.GetMetaAsync("schema_version", CancellationToken.None), Is.EqualTo("999"));
    }

    /// <summary>
    ///     <para>逐条提交回调按重建顺序携带新摘要；失败条目不回调。</para>
    ///     <para>The per-commit callback receives each fresh summary in rebuild order; failed entries never call back.</para>
    /// </summary>
    [Test]
    public async Task Reconcile_SummaryCallbackReceivesCommittedSummaries()
    {
        using var dir   = new TestTempDir();
        var       index = new SessionIndexStore(dir.Root);
        await index.InitializeAsync(CancellationToken.None);
        var ids = await SeedSessionsAsync(dir.Root, "callback question", "second callback question");

        var committed = new List<string>();
        var report = await new SessionIndexReconciler(index)
           .ReconcileAsync(new SessionIndexReconcileOptions
            {
                SummaryCommitted = (summary, _) =>
                {
                    committed.Add(summary.Id);
                    return Task.CompletedTask;
                }
            }, CancellationToken.None);
        Assert.That(report.Rebuilt, Is.EqualTo(2));
        Assert.That(committed, Is.EquivalentTo(ids));
    }

    /// <summary>
    ///     <para>回调抛出非取消异常按有界说明记录：本轮其余条目继续，条目仍健康提交。</para>
    ///     <para>
    ///         A non-cancellation callback exception becomes a bounded note: remaining entries continue and the entry
    ///         still commits as healthy.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Reconcile_CallbackFailureIsNotedAndRoundContinues()
    {
        using var dir   = new TestTempDir();
        var       index = new SessionIndexStore(dir.Root);
        await index.InitializeAsync(CancellationToken.None);
        var ids = await SeedSessionsAsync(dir.Root, "callback failure question");

        var report = await new SessionIndexReconciler(index)
           .ReconcileAsync(new SessionIndexReconcileOptions
            {
                SummaryCommitted = (_, _) => throw new InvalidOperationException("callback boom")
            }, CancellationToken.None);
        Assert.That(report.Rebuilt, Is.EqualTo(1));
        Assert.That(report.Notes, Has.Count.EqualTo(1));
        Assert.That(report.Notes[0], Does.Contain("Summary callback").And.Contain("callback boom"));
        var row = await index.GetRowAsync(ids[0], CancellationToken.None);
        Assert.That(row!.State, Is.EqualTo(SessionIndexEntryState.Current));
    }

    /// <summary>
    ///     <para>用脚本模型逐会话提交回合并返回会话 ID；宿主释放即释放 writer。</para>
    ///     <para>
    ///         Commits turns per session through scripted models and returns the session ids; disposing each host
    ///         releases its writer.
    ///     </para>
    /// </summary>
    private static async Task<List<string>> SeedSessionsAsync(string store, params string[] questions)
    {
        var ids = new List<string>();
        foreach (var question in questions)
        {
            var client = new FakeChatClient();
            client.Enqueue(FakeChatClient.Text($"answer to {question}"));
            using var host = NewHost(store, client);
            await host.SubmitTurnAsync(question, CancellationToken.None);
            ids.Add(host.Session.Id);
        }

        return ids;
    }

    private static ChatSessionHost NewHost(string store, FakeChatClient client) =>
        new(store,
            Identity,
            new AgentOptions { Model = "offline", MaxAgentSteps = 10, DefaultToolTimeoutSeconds = 30 },
            client,
            new ToolRegistry([]),
            "You are a test harness.",
            sessionWriterFactory : id => new SessionStoreWriter(store, id));
}
