using Microsoft.Data.Sqlite;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Project;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Persistence;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         S5 删除墓碑测试：迁移后的会话删除先落墓碑再删日志与投影、日志被占用时保持待收尾并由恢复入口续作、
///         读取与列表拒绝已删除会话、对账不复活残留日志，以及项目删除在单个事务内解绑并清理组织引用。
///     </para>
///     <para>
///         S5 tombstone tests: a post-migration session deletion records the tombstone before removing the log and
///         projections, a held log stays pending for the recovery entry, reads and listing refuse deleted sessions,
///         reconciliation never resurrects a leftover log, and project deletion unassigns plus clears organization
///         references inside one transaction.
///     </para>
/// </summary>
public class SessionStoreTombstoneTests
{
    private static readonly SessionModelIdentity Model =
        new("test-model", "https://example.invalid/v1", ChatApiKind.ChatCompletions);

    private static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>
    ///     <para>构造一个已完成迁移的 store：业务库建库、写入库身份与迁移标识，并写下三批已切换的迁移标记。</para>
    ///     <para>Builds a fully migrated store: creates the business database, records its identity and migration id, and writes the marker with all three batches switched.</para>
    /// </summary>
    private static async Task CreateMigratedStoreAsync(string storeDirectory)
    {
        var database = new SessionStoreDatabase(storeDirectory);
        await database.InitializeAsync(CancellationToken.None);
        var     migrationId = NewId();
        string? identity;
        await using (var connection = await database.OpenExistingForInternalUseAsync(CancellationToken.None))
        {
            identity = await SessionStoreDatabase.ReadStoreIdentityAsync(connection, CancellationToken.None);
            await SessionStoreDatabase.WriteMetaValueAsync(connection, null, "migration_id", migrationId,
                                                           CancellationToken.None);
        }

        var now    = DateTimeOffset.UtcNow;
        var marker = SessionStoreMigrationMarker.Start(migrationId, now)
                                                .WithStoreIdentity(
                                                                   identity ?? throw new InvalidOperationException(
                                                                       "The test database has no identity."), now)
                                                .WithBatchSwitched(SessionStoreMigrationBatch.OrganizationState,
                                                                   "org-fingerprint", now)
                                                .WithBatchSwitched(SessionStoreMigrationBatch.ProjectCatalog,
                                                                   "projects-fingerprint", now)
                                                .WithBatchSwitched(SessionStoreMigrationBatch.SessionPlacement,
                                                                   "placement-fingerprint", now);
        await SessionStoreMigration.WriteMarkerAsync(storeDirectory, marker, CancellationToken.None);
    }

    private static async Task<string> CommitSessionAsync(string storeDirectory, string workspaceRoot,
                                                         string? projectId = null)
    {
        var state = projectId is null
            ? SessionProjectAssignmentState.PendingInitialization
            : SessionProjectAssignmentState.Project;
        var       session = new ChatSession(NewId(), workspaceRoot, Model, null, projectId, state);
        var       context = new ConversationContext();
        context.Append(ChatMessage.System("system"));
        context.Append(ChatMessage.User("hello"));
        using var writer = new SessionStoreWriter(storeDirectory, session.Id);
        await writer.CommitAsync(session, context, CancellationToken.None);
        return session.Id;
    }

    private static async Task BuildIndexAsync(string storeDirectory)
    {
        var index = new SessionIndexStore(storeDirectory);
        await index.InitializeAsync(CancellationToken.None);
        await new SessionIndexReconciler(index).ReconcileAsync(CancellationToken.None);
    }

    [Test]
    public async Task Delete_MigratedStore_RecordsTombstoneAndClearsLogAndProjections()
    {
        using var temp      = new TestTempDir();
        var       workspace = temp.CreateDirectory("workspace");
        var       store     = temp.CreateDirectory("sessions");
        await CreateMigratedStoreAsync(store);

        var project = await SessionStoreProjectCatalogStore.GetOrCreateAsync(store, workspace, null,
                                                                             CancellationToken.None);
        var sessionId = await CommitSessionAsync(store, workspace, project.Id);
        await BuildIndexAsync(store);
        var operations = new SessionStoreOperations();
        await operations.SetSessionPinnedAsync(store, sessionId, true, CancellationToken.None);
        await SessionStoreArchiveStore.SetArchivedAsync(store, sessionId, true, CancellationToken.None);

        var issues = await operations.DeleteSessionPermanentlyAsync(store, workspace, sessionId,
                                                                    CancellationToken.None);

        Assert.That(issues, Is.Empty);
        Assert.That(File.Exists(Path.Combine(store, sessionId + SessionStoreWriter.LogFileSuffix)), Is.False,
                    "删除必须移除日志。");
        Assert.That(await SessionStoreTombstoneStore.IsTombstonedAsync(store, sessionId, CancellationToken.None),
                    Is.True, "删除完成后墓碑必须保留。");
        Assert.That(await SessionStoreTombstoneStore.ListPendingAsync(store, CancellationToken.None), Is.Empty,
                    "删除完成后不应留下待收尾墓碑。");
        var index = new SessionIndexStore(store);
        Assert.That(await index.GetRowAsync(sessionId, CancellationToken.None), Is.Null, "索引行必须被清理。");
        Assert.That((await SessionStorePlacementStore.GetAsync(store, sessionId, CancellationToken.None)).State,
                    Is.EqualTo(SessionProjectAssignmentState.PendingInitialization),
                    "归属行必须随删除移除。");

        // 重复删除按幂等完成返回：墓碑已置为 deleted，不再触碰日志与投影。
        var repeated = await operations.DeleteSessionPermanentlyAsync(store, workspace, sessionId,
                                                                      CancellationToken.None);
        Assert.That(repeated, Is.Empty);
    }

    [Test]
    public async Task Delete_WhenWriterHeld_LeavesPendingTombstoneAndRecoveryCompletesIt()
    {
        using var temp      = new TestTempDir();
        var       workspace = temp.CreateDirectory("workspace");
        var       store     = temp.CreateDirectory("sessions");
        await CreateMigratedStoreAsync(store);
        var sessionId = await CommitSessionAsync(store, workspace);
        var logPath   = Path.Combine(store, sessionId + SessionStoreWriter.LogFileSuffix);
        var operations = new SessionStoreOperations();

        using (await SessionStoreWriter.OpenExistingAsync(store, sessionId, null, CancellationToken.None))
        {
            await TestAssert.ThrowsAsync<SessionStoreException>(
                () => operations.DeleteSessionPermanentlyAsync(store, workspace, sessionId, CancellationToken.None));
            Assert.That(File.Exists(logPath), Is.True, "writer 占用时不得删除日志。");
            Assert.That(await SessionStoreTombstoneStore.ListPendingAsync(store, CancellationToken.None),
                        Has.Count.EqualTo(1), "被占用的删除必须留下待收尾墓碑。");
        }

        var issues = await operations.RecoverPendingSessionDeletionsAsync(store, CancellationToken.None);

        Assert.That(issues, Is.Empty);
        Assert.That(File.Exists(logPath), Is.False, "恢复入口必须续作删除。");
        Assert.That(await SessionStoreTombstoneStore.ListPendingAsync(store, CancellationToken.None), Is.Empty);
        Assert.That(await SessionStoreTombstoneStore.IsTombstonedAsync(store, sessionId, CancellationToken.None),
                    Is.True);
    }

    [Test]
    public async Task Reads_ExcludeSessionWhoseDeletionIsStillPending()
    {
        using var temp      = new TestTempDir();
        var       workspace = temp.CreateDirectory("workspace");
        var       store     = temp.CreateDirectory("sessions");
        await CreateMigratedStoreAsync(store);
        var sessionId = await CommitSessionAsync(store, workspace);
        await BuildIndexAsync(store);

        // 模拟"墓碑已提交、日志尚未删除"的崩溃窗口：日志仍在，但会话必须视为已删除。
        await SessionStoreTombstoneStore.MarkPendingAsync(store, sessionId, CancellationToken.None);

        var readError = await TestAssert.ThrowsAsync<SessionStoreException>(
            () => SessionStoreReader.ReadAsync(store, sessionId, CancellationToken.None));
        Assert.That(readError.Message, Does.Contain("deleted"));
        Assert.That(await SessionStoreReader.ListAsync(store, CancellationToken.None), Is.Empty,
                    "日志仍在也不能出现在列表里。");
        Assert.That(await new SessionListProjection(store).ScanSummariesAsync(CancellationToken.None), Is.Empty);
        Assert.That(await new SessionListProjection(store).ListSummariesAsync(new SessionSummaryQuery(),
                                                                              CancellationToken.None), Is.Empty,
                    "索引分页路径同样必须过滤墓碑会话。");
    }

    [Test]
    public async Task Reconciliation_RemovesTombstonedRowWhoseLogIsAlreadyGone()
    {
        using var temp      = new TestTempDir();
        var       workspace = temp.CreateDirectory("workspace");
        var       store     = temp.CreateDirectory("sessions");
        await CreateMigratedStoreAsync(store);
        var sessionId = await CommitSessionAsync(store, workspace);
        await BuildIndexAsync(store);
        var index = new SessionIndexStore(store);
        Assert.That(await index.GetRowAsync(sessionId, CancellationToken.None), Is.Not.Null);

        // "日志已删除、索引清理尚未成功"的中断窗口：日志不在了，索引行与墓碑都还在。
        // 残留行不属于来源集合，只有孤儿清理能处理它；按墓碑整体排除会让它永久留在索引里。
        // The interrupted window after the log is deleted but before the index cleanup succeeded: the log is gone
        // while both the index row and the tombstone remain. The leftover row is not a source, so only the orphan
        // pass can remove it; excluding the whole tombstone set would keep it in the index forever.
        await SessionStoreTombstoneStore.MarkPendingAsync(store, sessionId, CancellationToken.None);
        File.Delete(Path.Combine(store, sessionId + SessionStoreWriter.LogFileSuffix));

        await new SessionIndexReconciler(index).ReconcileAsync(CancellationToken.None);

        Assert.That(await index.GetRowAsync(sessionId, CancellationToken.None), Is.Null,
                    "日志已消失的墓碑索引行必须由对账清理。");
    }

    [Test]
    public async Task Delete_WhenProjectionCleanupFails_LeavesTombstonePendingForRetry()
    {
        using var temp      = new TestTempDir();
        var       workspace = temp.CreateDirectory("workspace");
        var       store     = temp.CreateDirectory("sessions");
        await CreateMigratedStoreAsync(store);
        var sessionId = await CommitSessionAsync(store, workspace);
        await BuildIndexAsync(store);
        var operations = new SessionStoreOperations();

        List<SessionStoreCleanupIssue> issues;
        // 独立连接持索引写事务：迁移后归档与置顶清理走业务库，索引仍是唯一可稳定注入失败的投影清理项。
        // A separate connection holds an index write transaction: after migration the archive and pin cleanup go to
        // the business database, leaving the index as the only projection cleanup that can be failed on purpose.
        await using (var connection = OpenRawIndexConnection(store))
        {
            await connection.OpenAsync(CancellationToken.None);
            await using var writeTransaction = await connection.BeginTransactionAsync(CancellationToken.None);
            issues = (await operations.DeleteSessionPermanentlyAsync(store, workspace, sessionId,
                                                                     CancellationToken.None)).ToList();
        }

        Assert.That(issues.Select(issue => issue.Area), Is.EqualTo(new[] { SessionStoreCleanupArea.SessionIndex }));
        Assert.That(File.Exists(Path.Combine(store, sessionId + SessionStoreWriter.LogFileSuffix)), Is.False);
        var index = new SessionIndexStore(store);
        Assert.That(await index.GetRowAsync(sessionId, CancellationToken.None), Is.Not.Null,
                    "前置：索引清理确实失败并留下残留行。");
        Assert.That(await SessionStoreTombstoneStore.ListPendingAsync(store, CancellationToken.None),
                    Has.Count.EqualTo(1), "清理未完成时墓碑必须保持待收尾，否则残留投影再也无法补做。");

        var recovered = await operations.RecoverPendingSessionDeletionsAsync(store, CancellationToken.None);

        Assert.That(recovered, Is.Empty);
        Assert.That(await index.GetRowAsync(sessionId, CancellationToken.None), Is.Null, "续作必须补齐残留索引行。");
        Assert.That(await SessionStoreTombstoneStore.ListPendingAsync(store, CancellationToken.None), Is.Empty,
                    "续作成功后墓碑必须转为已删除。");
    }

    [Test]
    public async Task Recovery_WhenProjectionCleanupStillFails_KeepsTombstonePending()
    {
        using var temp      = new TestTempDir();
        var       workspace = temp.CreateDirectory("workspace");
        var       store     = temp.CreateDirectory("sessions");
        await CreateMigratedStoreAsync(store);
        var sessionId = await CommitSessionAsync(store, workspace);
        await BuildIndexAsync(store);
        var operations = new SessionStoreOperations();
        // 直接构造中断现场：墓碑已提交、日志仍在，索引行也还在。
        await SessionStoreTombstoneStore.MarkPendingAsync(store, sessionId, CancellationToken.None);

        List<SessionStoreCleanupIssue> issues;
        await using (var connection = OpenRawIndexConnection(store))
        {
            await connection.OpenAsync(CancellationToken.None);
            await using var writeTransaction = await connection.BeginTransactionAsync(CancellationToken.None);
            issues = (await operations.RecoverPendingSessionDeletionsAsync(store, CancellationToken.None)).ToList();
        }

        Assert.That(issues.Select(issue => issue.Area), Is.EqualTo(new[] { SessionStoreCleanupArea.SessionIndex }));
        Assert.That(await SessionStoreTombstoneStore.ListPendingAsync(store, CancellationToken.None),
                    Has.Count.EqualTo(1), "恢复入口清理失败时同样不得把墓碑置为已删除。");
    }

    /// <summary>
    ///     <para>打开索引库的原始连接，用于从外部持有写事务并稳定制造清理失败。</para>
    ///     <para>Opens a raw index connection so a write transaction can be held externally to fail cleanup deterministically.</para>
    /// </summary>
    private static SqliteConnection OpenRawIndexConnection(string store)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = SessionIndexStore.PathFor(store), Mode = SqliteOpenMode.ReadWrite, Pooling = false
        };
        return new SqliteConnection(builder.ToString());
    }

    [Test]
    public async Task Reconciliation_RemovesAndNeverRewritesTombstonedRow()
    {
        using var temp      = new TestTempDir();
        var       workspace = temp.CreateDirectory("workspace");
        var       store     = temp.CreateDirectory("sessions");
        await CreateMigratedStoreAsync(store);
        var sessionId = await CommitSessionAsync(store, workspace);
        await BuildIndexAsync(store);
        var index = new SessionIndexStore(store);
        Assert.That(await index.GetRowAsync(sessionId, CancellationToken.None), Is.Not.Null);

        await SessionStoreTombstoneStore.MarkPendingAsync(store, sessionId, CancellationToken.None);

        await new SessionIndexReconciler(index).ReconcileAsync(CancellationToken.None);
        Assert.That(await index.GetRowAsync(sessionId, CancellationToken.None), Is.Null,
                    "对账必须移除墓碑会话的残留索引行。");

        // 再跑一轮也不能把仍在磁盘上的日志重新投影回索引。
        await new SessionIndexReconciler(index).ReconcileAsync(CancellationToken.None);
        Assert.That(await index.GetRowAsync(sessionId, CancellationToken.None), Is.Null);
    }
}
