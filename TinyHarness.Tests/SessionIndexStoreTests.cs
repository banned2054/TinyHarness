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
///     <para>验证 SQLite 列表索引的 schema 版本、条件提交、归档投影隔离与查询契约。</para>
///     <para>
///         Verifies the SQLite list index's schema versioning, conditional commits, archive-projection isolation,
///         and query contract.
///     </para>
/// </summary>
public class SessionIndexStoreTests
{
    private static readonly SessionModelIdentity Identity =
        new("offline", "https://offline.invalid/v1", ChatApiKind.ChatCompletions);

    /// <summary>
    ///     <para>首次使用返回 NeedsSchema，Initialize 后可用，重开保持版本。</para>
    ///     <para>First use reports NeedsSchema, initialization makes it available, and reopening keeps the version.</para>
    /// </summary>
    [Test]
    public async Task Availability_InitializeAndReopen_ReportsExpectedStates()
    {
        using var dir   = new TestTempDir();
        var       store = new SessionIndexStore(dir.Root);
        Assert.That((await store.GetAvailabilityAsync(CancellationToken.None)).Status,
                    Is.EqualTo(SessionIndexAvailabilityStatus.Missing));

        File.WriteAllText(store.DatabasePath, string.Empty);
        Assert.That((await store.GetAvailabilityAsync(CancellationToken.None)).Status,
                    Is.EqualTo(SessionIndexAvailabilityStatus.NeedsSchema));

        await store.InitializeAsync(CancellationToken.None);
        Assert.That(await store.GetAvailabilityAsync(CancellationToken.None),
                    Is.EqualTo(SessionIndexAvailability.Ok));

        var reopened = new SessionIndexStore(dir.Root);
        Assert.That(await reopened.GetAvailabilityAsync(CancellationToken.None),
                    Is.EqualTo(SessionIndexAvailability.Ok));
    }

    /// <summary>
    ///     <para>更高版本被拒绝且不被清空；损坏文件按 Damaged 报告。</para>
    ///     <para>A newer version is refused without being cleared; a damaged file reports Damaged.</para>
    /// </summary>
    [Test]
    public async Task Availability_UnsupportedVersionAndDamage_AreReportedSeparately()
    {
        using var dir   = new TestTempDir();
        var       store = new SessionIndexStore(dir.Root);
        await store.InitializeAsync(CancellationToken.None);
        await store.SetMetaAsync("schema_version", "999", CancellationToken.None);

        Assert.That((await store.GetAvailabilityAsync(CancellationToken.None)).Status,
                    Is.EqualTo(SessionIndexAvailabilityStatus.Unsupported));
        Assert.ThrowsAsync<SessionIndexException>(() => store.InitializeAsync(CancellationToken.None));
        // 拒绝不得破坏既有数据库内容。
        // The refusal must not damage the existing database content.
        Assert.That(await store.GetMetaAsync("schema_version", CancellationToken.None), Is.EqualTo("999"));

        // 非数据库文件按损坏报告，不得被读取路径静默清空。
        // A non-database file reports as damaged and is never silently cleared by the read path.
        var damagedDirectory = Path.Combine(dir.Root, "damaged-store");
        Directory.CreateDirectory(damagedDirectory);
        var damagedStore = new SessionIndexStore(damagedDirectory);
        await File.WriteAllBytesAsync(damagedStore.DatabasePath, [0x50, 0x4b, 0x03, 0x04, 1, 2, 3]);
        Assert.That((await damagedStore.GetAvailabilityAsync(CancellationToken.None)).Status,
                    Is.EqualTo(SessionIndexAvailabilityStatus.Damaged));
    }

    /// <summary>
    ///     <para>查询按活动时间降序加 ID 次键排序，键集分页连续且无重复。</para>
    ///     <para>
    ///         Queries order by activity time descending with the id tie-break, and keyset pagination is continuous
    ///         without repeats.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ListSummaries_OrdersByActivityAndPaginatesByKeyset()
    {
        using var dir      = new TestTempDir();
        var       store    = await NewInitializedStoreAsync(dir.Root);
        var       baseTime = DateTimeOffset.UtcNow;
        var       allIds   = Enumerable.Range(0, 5).Select(Id).ToArray();
        for (var i = 0; i < allIds.Length; i++)
            await store.WriteSummaryAsync(Summary(allIds[i], baseTime.AddMinutes(i)), 100 + i, null,
                                          CancellationToken.None);

        var page = await store.ListSummariesAsync(new SessionSummaryQuery { Limit = 2 }, CancellationToken.None);
        Assert.That(page.Select(summary => summary.Id), Is.EqualTo(new[] { allIds[4], allIds[3] }));

        var last = page[^1];
        var second = await store.ListSummariesAsync(
                                                    new SessionSummaryQuery
                                                    {
                                                        Limit = 10,
                                                        AfterLastActivityUtcMs =
                                                            last.LastActivityUtc.ToUnixTimeMilliseconds(),
                                                        AfterId = last.Id
                                                    }, CancellationToken.None);
        Assert.That(second.Select(summary => summary.Id),
                    Is.EqualTo(new[] { allIds[2], allIds[1], allIds[0] }));
    }

    /// <summary>
    ///     <para>归档过滤：未知状态包含在非归档视图；归档投影与摘要互不覆盖。</para>
    ///     <para>
    ///         Archive filtering: unknown states stay in the non-archived view; the archive projection and summaries
    ///         never overwrite each other.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ArchiveProjection_IsolatesFromSummaries()
    {
        using var dir   = new TestTempDir();
        var       store = await NewInitializedStoreAsync(dir.Root);
        var       idA   = HexId('a');
        var       idB   = HexId('b');
        var       idC   = HexId('c');
        await store.WriteSummaryAsync(Summary(idA, DateTimeOffset.UtcNow), 10, null, CancellationToken.None);
        await store.WriteSummaryAsync(Summary(idB, DateTimeOffset.UtcNow), 10, null, CancellationToken.None);
        await store.WriteSummaryAsync(Summary(idC, DateTimeOffset.UtcNow), 10, null, CancellationToken.None);

        await store.SyncArchiveProjectionAsync(new HashSet<string>([idA], StringComparer.Ordinal),
                                               CancellationToken.None);

        // 无过滤返回全部条目；非归档视图排除已归档但包含未知状态。
        // The unfiltered query returns everything; the non-archived view excludes archived entries but includes
        // unknown ones.
        var allView = await store.ListSummariesAsync(new SessionSummaryQuery { Limit = 10 },
                                                     CancellationToken.None);
        Assert.That(allView.Select(summary => summary.Id), Is.EquivalentTo(new[] { idA, idB, idC }));

        var defaultView = await store.ListSummariesAsync(new SessionSummaryQuery { Limit = 10, Archived = false },
                                                         CancellationToken.None);
        Assert.That(defaultView.Select(summary => summary.Id), Is.EquivalentTo(new[] { idB, idC }));

        var archivedView = await store.ListSummariesAsync(new SessionSummaryQuery { Limit = 10, Archived = true },
                                                          CancellationToken.None);
        Assert.That(archivedView.Select(summary => summary.Id), Is.EquivalentTo([idA]));

        // 摘要更新不得覆盖归档投影。
        // A summary update must not overwrite the archive projection.
        await store.WriteSummaryAsync(Summary(idA, DateTimeOffset.UtcNow.AddMinutes(1)) with
        {
            CommitSequence = 2, IndexedBytes = 11
        }, 11, null, CancellationToken.None);
        var rowAfterSummary = await store.GetRowAsync(idA, CancellationToken.None);
        Assert.That(rowAfterSummary!.Archived, Is.True);
        Assert.That(rowAfterSummary.Summary!.CommitSequence, Is.EqualTo(2));

        // 归档同步不得改写摘要。
        // An archive sync must not rewrite the summary.
        await store.SyncArchiveProjectionAsync(new HashSet<string>(StringComparer.Ordinal), CancellationToken.None);
        var rowAfterSync = await store.GetRowAsync(idA, CancellationToken.None);
        Assert.That(rowAfterSync!.Archived, Is.False);
        Assert.That(rowAfterSync.Summary!.CommitSequence, Is.EqualTo(2));
    }

    /// <summary>
    ///     <para>条件提交：更新快照接受，更旧快照被拒绝，失败条目接受任何重建结果。</para>
    ///     <para>
    ///         Conditional commits: fresher snapshots apply, older snapshots are rejected, and failed entries accept
    ///         any rebuild result.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ConditionalCommits_AcceptOnlyFresherSnapshots()
    {
        using var dir   = new TestTempDir();
        var       store = await NewInitializedStoreAsync(dir.Root);
        var       id    = HexId('d');
        var       newer = Summary(id, DateTimeOffset.UtcNow) with { CommitSequence = 5, IndexedBytes = 500 };
        var older = Summary(id, DateTimeOffset.UtcNow.AddMinutes(-1))
            with
            {
                CommitSequence = 4, IndexedBytes = 400
            };

        Assert.That(await store.TryAdvanceFromCommitAsync(newer, 500, null, CancellationToken.None), Is.True);
        Assert.That(await store.TryAdvanceFromCommitAsync(older, 400, null, CancellationToken.None), Is.False);
        var row = await store.GetRowAsync(id, CancellationToken.None);
        Assert.That(row!.Summary!.CommitSequence, Is.EqualTo(5));
        Assert.That(row.SourceLength, Is.EqualTo(500));

        await store.MarkFailedAsync(id, "damaged log", CancellationToken.None);
        var failedRow = await store.GetRowAsync(id, CancellationToken.None);
        Assert.That(failedRow!.State, Is.EqualTo(SessionIndexEntryState.Failed));
        Assert.That(failedRow.Error, Is.EqualTo("damaged log"));
        Assert.That(failedRow.Summary!.CommitSequence, Is.EqualTo(5), "失败标记保留旧摘要供展示");

        // 失败条目接受重建结果并恢复健康状态。
        // A failed entry accepts a rebuild result and returns to the healthy state.
        await store.WriteSummaryAsync(older with { CommitSequence = 6, IndexedBytes = 600 }, 600, null,
                                      CancellationToken.None);
        var rebuilt = await store.GetRowAsync(id, CancellationToken.None);
        Assert.That(rebuilt!.State, Is.EqualTo(SessionIndexEntryState.Current));
        Assert.That(rebuilt.Error, Is.Null);
        Assert.That(rebuilt.Summary!.CommitSequence, Is.EqualTo(6));
    }

    /// <summary>
    ///     <para>失败占位行没有摘要但保持可见；健康查询不返回失败条目。</para>
    ///     <para>Failure placeholder rows have no summary yet stay visible; healthy queries exclude failed entries.</para>
    /// </summary>
    [Test]
    public async Task MarkFailed_InsertsPlaceholderAndExcludesFromHealthyQueries()
    {
        using var dir   = new TestTempDir();
        var       store = await NewInitializedStoreAsync(dir.Root);
        var       id    = HexId('e');
        await store.MarkFailedAsync(id, "log ends with an incomplete line", CancellationToken.None);

        var row = await store.GetRowAsync(id, CancellationToken.None);
        Assert.That(row, Is.Not.Null);
        Assert.That(row!.State, Is.EqualTo(SessionIndexEntryState.Failed));
        Assert.That(row.Summary, Is.Null);
        Assert.That(row.Error, Is.EqualTo("log ends with an incomplete line"));

        var healthy = await store.ListSummariesAsync(new SessionSummaryQuery { Limit = 10 }, CancellationToken.None);
        Assert.That(healthy, Is.Empty);
        Assert.That(await store.CountSummariesAsync(CancellationToken.None), Is.EqualTo(0));
    }

    /// <summary>
    ///     <para>Remove 与 Clear 只影响条目，保留 schema 版本与元数据。</para>
    ///     <para>Remove and Clear affect entries only, keeping the schema version and metadata.</para>
    /// </summary>
    [Test]
    public async Task RemoveAndClear_KeepSchemaAndMeta()
    {
        using var dir   = new TestTempDir();
        var       store = await NewInitializedStoreAsync(dir.Root);
        await store.SetMetaAsync("build_state", "partial", CancellationToken.None);
        var id = HexId('f');
        await store.WriteSummaryAsync(Summary(id, DateTimeOffset.UtcNow), 10, null, CancellationToken.None);
        await store.RemoveAsync(id, CancellationToken.None);
        Assert.That(await store.GetRowAsync(id, CancellationToken.None), Is.Null);

        await store.WriteSummaryAsync(Summary(id, DateTimeOffset.UtcNow), 10, null, CancellationToken.None);
        await store.ClearAsync(CancellationToken.None);
        Assert.That(await store.CountSummariesAsync(CancellationToken.None), Is.EqualTo(0));
        Assert.That(await store.GetMetaAsync("schema_version", CancellationToken.None), Is.EqualTo("1"));
        Assert.That(await store.GetMetaAsync("build_state", CancellationToken.None), Is.Null);
    }

    /// <summary>
    ///     <para>项目过滤返回匹配条目。</para>
    ///     <para>The project filter returns matching entries.</para>
    /// </summary>
    [Test]
    public async Task ListSummaries_FiltersByProject()
    {
        using var dir     = new TestTempDir();
        var       store   = await NewInitializedStoreAsync(dir.Root);
        var       project = "0123456789abcdef0123456789abcdef";
        var       idA     = HexId('a');
        var       idB     = HexId('b');
        await store.WriteSummaryAsync(Summary(idA, DateTimeOffset.UtcNow) with { ProjectId = project }, 10, null,
                                      CancellationToken.None);
        await store.WriteSummaryAsync(Summary(idB, DateTimeOffset.UtcNow), 10, null, CancellationToken.None);

        var filtered = await store.ListSummariesAsync(new SessionSummaryQuery { Limit = 10, ProjectId = project },
                                                      CancellationToken.None);
        Assert.That(filtered.Select(summary => summary.Id), Is.EquivalentTo([idA]));
    }

    /// <summary>
    ///     <para>摘要与严格读取的列表字段逐项一致（标题、归属、时间、回合数、序号与字节）。</para>
    ///     <para>
    ///         Summary list fields match a strict read field for field: title, ownership, times, turn count,
    ///         sequence, and bytes.
    ///     </para>
    /// </summary>
    [Test]
    public async Task SummaryBuilder_MatchesStrictReadFields()
    {
        using var dir    = new TestTempDir();
        var       store  = dir.Root;
        var       client = new FakeChatClient();
        using var host   = NewHost(store, client);
        client.Enqueue(FakeChatClient.Text("first answer"));
        await host.SubmitTurnAsync("first question\nwith a second line", CancellationToken.None);
        client.Enqueue(FakeChatClient.ToolCall("read_thing", "{\"path\":\"src/x.cs\"}"));
        client.Enqueue(FakeChatClient.Text("second answer"));
        await host.SubmitTurnAsync("second question", CancellationToken.None);
        var sessionId = host.Session.Id;

        var build     = await SessionSummaryBuilder.BuildAsync(store, sessionId, CancellationToken.None);
        var stored    = await SessionStoreReader.ReadAsync(store, sessionId, CancellationToken.None);
        var summary   = build!.Summary;
        var logLength = new FileInfo(Path.Combine(store, sessionId + SessionStoreWriter.LogFileSuffix)).Length;

        Assert.That(summary.Id, Is.EqualTo(stored!.Id));
        Assert.That(summary.WorkspaceRoot, Is.EqualTo(stored.WorkspaceRoot));
        Assert.That(summary.ProjectId, Is.EqualTo(stored.ProjectId));
        Assert.That(summary.ProjectAssignmentState, Is.EqualTo(stored.ProjectAssignmentState));
        Assert.That(summary.Title, Is.EqualTo("first question"));
        Assert.That(summary.CreatedUtc, Is.EqualTo(stored.CreatedUtc));
        Assert.That(summary.UpdatedUtc, Is.EqualTo(stored.UpdatedUtc));
        Assert.That(summary.LastActivityUtc, Is.EqualTo(stored.LastActivityUtc));
        Assert.That(summary.TurnCount, Is.EqualTo(stored.Turns.Count));
        Assert.That(summary.PermissionMode, Is.EqualTo(stored.PermissionSelection?.Mode));
        Assert.That(summary.CommitSequence, Is.EqualTo(stored.CommitSequence));
        Assert.That(summary.IndexedBytes, Is.EqualTo(logLength));
        Assert.That(build.SourceLength, Is.EqualTo(logLength));
    }

    /// <summary>
    ///     <para>纯归属维护批次推进更新时间但不推进活动时间。</para>
    ///     <para>An ownership-only batch advances the update time but not the activity time.</para>
    /// </summary>
    [Test]
    public async Task SummaryBuilder_OwnershipOnlyBatchDoesNotAdvanceActivity()
    {
        using var dir    = new TestTempDir();
        var       store  = dir.Root;
        var       client = new FakeChatClient();
        string    sessionId;
        using (var host = NewHost(store, client))
        {
            client.Enqueue(FakeChatClient.Text("answer"));
            await host.SubmitTurnAsync("question", CancellationToken.None);
            sessionId = host.Session.Id;
        }

        var projectId = "0123456789abcdef0123456789abcdef";

        using (var writer =
               await SessionStoreWriter.OpenExistingAsync(store, sessionId, null, CancellationToken.None))
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

        var after   = await SessionStoreReader.ReadAsync(store, sessionId, CancellationToken.None);
        var build   = await SessionSummaryBuilder.BuildAsync(store, sessionId, CancellationToken.None);
        var summary = build!.Summary;
        Assert.That(after!.ProjectId, Is.EqualTo(projectId));
        Assert.That(after.UpdatedUtc, Is.GreaterThan(after.LastActivityUtc), "归属批次应只推进更新时间");
        Assert.That(summary.ProjectId, Is.EqualTo(projectId));
        Assert.That(summary.ProjectAssignmentState, Is.EqualTo(SessionProjectAssignmentState.Project));
        Assert.That(summary.UpdatedUtc, Is.EqualTo(after.UpdatedUtc));
        Assert.That(summary.LastActivityUtc, Is.EqualTo(after.LastActivityUtc));
    }

    /// <summary>
    ///     <para>首回合到达前标题为 ID 占位；提交回合批次后标题切换为输入首行。</para>
    ///     <para>
    ///         The title stays the id placeholder until the first turn lands, then switches to the input's first
    ///         line once a turn batch commits.
    ///     </para>
    /// </summary>
    [Test]
    public async Task SummaryBuilder_TitlePlaceholderSwitchesOnFirstTurn()
    {
        using var dir       = new TestTempDir();
        var       store     = dir.Root;
        var       sessionId = Guid.NewGuid().ToString("N");
        var       created   = DateTimeOffset.UtcNow;

        // 无回合提交只写身份与状态批次。
        // A turn-less commit writes only the identity and state batches.
        using (var writer = new SessionStoreWriter(store, sessionId))
        {
            await writer.CommitAsync(new ChatSession(sessionId, dir.Root, Identity, created),
                                     new ConversationContext(new ContextOptions()), CancellationToken.None);
        }

        var initial = await SessionSummaryBuilder.BuildAsync(store, sessionId, CancellationToken.None);
        Assert.That(initial!.Summary.TurnCount, Is.EqualTo(0));
        Assert.That(initial.Summary.Title, Is.EqualTo(sessionId[..8]));

        // 第二次提交携带首个回合；writer 按已提交序号续写。
        // The second commit carries the first turn; the writer continues from the committed sequence.
        var session = new ChatSession(sessionId, dir.Root, Identity, created);
        session.AddTurn(new SessionTurn("turn-1", "seeded projection title"));
        using (var reopened =
               await SessionStoreWriter.OpenExistingAsync(store, sessionId, null, CancellationToken.None))
        {
            await reopened.CommitAsync(session, new ConversationContext(new ContextOptions()),
                                       CancellationToken.None);
        }

        var build = await SessionSummaryBuilder.BuildAsync(store, sessionId, CancellationToken.None);
        Assert.That(build!.Summary.Title, Is.EqualTo("seeded projection title"));
        Assert.That(build.Summary.TurnCount, Is.EqualTo(1));
    }

    /// <summary>
    ///     <para>半行结尾按严格扫描失败报告，不产生半提交摘要。</para>
    ///     <para>A partial-line tail is a strict-scan failure and never yields a half-committed summary.</para>
    /// </summary>
    [Test]
    public async Task SummaryBuilder_RejectsPartialTail()
    {
        using var dir    = new TestTempDir();
        var       store  = dir.Root;
        var       client = new FakeChatClient();
        string    sessionId;
        using (var host = NewHost(store, client))
        {
            client.Enqueue(FakeChatClient.Text("answer"));
            await host.SubmitTurnAsync("question", CancellationToken.None);
            sessionId = host.Session.Id;
        }

        var logPath = Path.Combine(store, sessionId + SessionStoreWriter.LogFileSuffix);
        await File.AppendAllTextAsync(logPath, "{\"format\":\"tinyharness.session-log/1", CancellationToken.None);

        Assert.ThrowsAsync<SessionStoreException>(() =>
                                                      SessionSummaryBuilder.BuildAsync(store, sessionId,
                                                          CancellationToken.None));
    }

    /// <summary>
    ///     <para>缺失日志返回 null。</para>
    ///     <para>A missing log returns null.</para>
    /// </summary>
    [Test]
    public async Task SummaryBuilder_MissingLogReturnsNull()
    {
        using var dir   = new TestTempDir();
        var       build = await SessionSummaryBuilder.BuildAsync(dir.Root, HexId('1'), CancellationToken.None);
        Assert.That(build, Is.Null);
    }

    /// <summary>
    ///     <para>大写十六进制 ID 在读写、失败标记、移除与归档同步入口都归一为同一小写行键。</para>
    ///     <para>
    ///         Uppercase hexadecimal ids normalize to the same lowercase row key across read/write, failure-marking,
    ///         removal, and archive sync.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Ids_UppercaseHex_NormalizesToLowercaseRowKey()
    {
        using var dir     = new TestTempDir();
        var       store   = await NewInitializedStoreAsync(dir.Root);
        var       idLower = HexId('a');
        var       idUpper = idLower.ToUpperInvariant();

        // 大写摘要写入后，读写两侧都以归一行键命中同一行。
        await store.WriteSummaryAsync(Summary(idUpper, DateTimeOffset.UtcNow), 10, null, CancellationToken.None);
        Assert.That((await store.GetRowAsync(idUpper, CancellationToken.None))!.Summary!.Id, Is.EqualTo(idLower));

        await store.MarkFailedAsync(idUpper, "damaged log", CancellationToken.None);
        Assert.That((await store.GetRowAsync(idLower, CancellationToken.None))!.State,
                    Is.EqualTo(SessionIndexEntryState.Failed));
        Assert.That(await store.CountSummariesAsync(CancellationToken.None), Is.EqualTo(0));

        await store.RemoveAsync(idUpper, CancellationToken.None);
        Assert.That(await store.GetRowAsync(idLower, CancellationToken.None), Is.Null);

        // 归档同步集合同样按小写归一命中重建后的行。
        await store.WriteSummaryAsync(Summary(idLower, DateTimeOffset.UtcNow), 10, null, CancellationToken.None);
        await store.SyncArchiveProjectionAsync(new HashSet<string>([idUpper], StringComparer.Ordinal),
                                               CancellationToken.None);
        Assert.That((await store.GetRowAsync(idLower, CancellationToken.None))!.Archived, Is.True);
    }

    /// <summary>
    ///     <para>无 schema 数据库上的读写以 SessionIndexException 报告，不泄漏原生 SQLite 异常。</para>
    ///     <para>
    ///         Reads and writes on a schema-less database report SessionIndexException without leaking raw SQLite
    ///         errors.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Operations_OnSchemalessDatabase_ThrowSessionIndexException()
    {
        using var dir   = new TestTempDir();
        var       store = new SessionIndexStore(dir.Root);
        await File.WriteAllTextAsync(store.DatabasePath, string.Empty, CancellationToken.None);

        Assert.ThrowsAsync<SessionIndexException>(() => store.ListSummariesAsync(new SessionSummaryQuery(),
                                                      CancellationToken.None));
        Assert.ThrowsAsync<SessionIndexException>(() => store.GetRowAsync(HexId('a'), CancellationToken.None));
        Assert.ThrowsAsync<SessionIndexException>(() => store.SetMetaAsync("key", "value", CancellationToken.None));
        Assert.ThrowsAsync<SessionIndexException>(() =>
                                                      store
                                                         .SyncArchiveProjectionAsync(new HashSet<string>(StringComparer
                                                                 .Ordinal),
                                                              CancellationToken.None));
    }

    /// <summary>
    ///     <para>
    ///         种子投影恢复执行根等全部字段；不含 identity 的增量批次推进后 workspace 不丢失，纯归属批次不推进活动时间。
    ///     </para>
    ///     <para>
    ///         A seeded projection restores every field including the workspace root; after an identity-less
    ///         incremental batch the workspace survives, and an ownership-only batch does not advance activity time.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Projection_SeedFromSummaryRestoresWorkspaceRootAcrossIncrementalBatches()
    {
        using var dir    = new TestTempDir();
        var       store  = dir.Root;
        var       client = new FakeChatClient();
        string    sessionId;
        using (var host = NewHost(store, client))
        {
            client.Enqueue(FakeChatClient.Text("answer"));
            await host.SubmitTurnAsync("question", CancellationToken.None);
            sessionId = host.Session.Id;
        }

        var build      = await SessionSummaryBuilder.BuildAsync(store, sessionId, CancellationToken.None);
        var projection = new SessionSummaryBuilder.Projection(sessionId);
        projection.SeedFromSummary(build!.Summary);

        var sequence = build.Summary.CommitSequence + 1;
        var written  = build.Summary.UpdatedUtc.AddMinutes(1);
        projection.Apply(new SessionLogBatch
        {
            Format     = SessionLogWriter.FormatName,
            SessionId  = sessionId,
            Sequence   = sequence,
            BatchId    = $"{sessionId}:{sequence:D20}",
            WrittenUtc = written,
            Records =
            [
                new SessionLogRecord($"{sessionId}:{sequence:D20}:0000", "project-binding",
                                     JsonSerializer.SerializeToElement(
                                                                       new
                                                                           SessionLogProjectBinding(SessionLogProjectBinding
                                                                                  .SchemaName,
                                                                               "0123456789abcdef0123456789abcdef"),
                                                                       SessionLogJsonContext.Default
                                                                          .SessionLogProjectBinding))
            ]
        });

        var advanced = projection.ToSummary(sequence, build.SourceLength + 10);
        Assert.That(advanced.WorkspaceRoot, Is.EqualTo(build.Summary.WorkspaceRoot),
                    "增量批次不含 identity，执行根必须来自种子摘要");
        Assert.That(advanced.ProjectId, Is.EqualTo("0123456789abcdef0123456789abcdef"));
        Assert.That(advanced.TurnCount, Is.EqualTo(build.Summary.TurnCount));
        Assert.That(advanced.LastActivityUtc, Is.EqualTo(build.Summary.LastActivityUtc),
                    "纯归属批次不构成对话活动");
        Assert.That(advanced.UpdatedUtc, Is.EqualTo(written));

        Assert.Throws<ArgumentException>(() => projection.SeedFromSummary(build.Summary with { Id = HexId('9') }));
    }

    private static string Id(int index) => index.ToString("x2").PadLeft(32, '0');

    private static string HexId(char seed) => new(seed, 32);

    private static async Task<SessionIndexStore> NewInitializedStoreAsync(string directory)
    {
        var store = new SessionIndexStore(directory);
        await store.InitializeAsync(CancellationToken.None);
        return store;
    }

    private static SessionSummary Summary(string id, DateTimeOffset activity) => new()
    {
        Id                     = id,
        WorkspaceRoot          = "/tmp/workspace",
        ProjectAssignmentState = SessionProjectAssignmentState.NoProject,
        Title                  = "title",
        CreatedUtc             = activity,
        UpdatedUtc             = activity,
        LastActivityUtc        = activity,
        CommitSequence         = 1,
        IndexedBytes           = 100
    };

    private static ChatSessionHost NewHost(string store, FakeChatClient client) =>
        new(store,
            Identity,
            new AgentOptions { Model = "offline", MaxAgentSteps = 10, DefaultToolTimeoutSeconds = 30 },
            client,
            new ToolRegistry([]),
            "You are a test harness.",
            sessionWriterFactory : id => new SessionStoreWriter(store, id));
}
