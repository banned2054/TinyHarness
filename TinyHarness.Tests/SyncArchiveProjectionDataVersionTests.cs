using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Persistence;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         固化归档投影同步的同值守卫契约：无来源变化的对账不再推进常驻监测连接读到的 data_version，
///         归档集合的真实变化经一轮对账收敛到索引投影，archived 为 NULL 的行在同步时初始化为 0 或 1。
///     </para>
///     <para>
///         Pins the same-value guard contract of archive-projection sync: a reconcile round with no source changes
///         no longer advances the data_version read by the standing monitor connection, real archive-set changes
///         converge into the index projection through one reconcile round, and rows with a NULL archived value are
///         initialized to 0 or 1 by the sync.
///     </para>
/// </summary>
public class SyncArchiveProjectionDataVersionTests
{
    private static readonly SessionModelIdentity Identity =
        new("offline", "https://offline.invalid/v1", ChatApiKind.ChatCompletions);

    /// <summary>
    ///     <para>
    ///         无来源变化的对账轮中，同值守卫跳过全部同值健康行：归档同步的写事务不产生行写入，
    ///         监测连接读到的 data_version 保持不变。
    ///     </para>
    ///     <para>
    ///         In a reconcile round with no source changes, the same-value guard skips every matching healthy row:
    ///         the archive sync's write transaction produces no row writes and the monitor connection's
    ///         data_version stays unchanged.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Reconcile_WithNoSourceChanges_DoesNotBumpDataVersion()
    {
        using var dir        = new TestTempDir();
        var       indexStore = new SessionIndexStore(dir.Root);
        var       sessionIds = new List<string>(3);
        for (var i = 0; i < 3; i++) sessionIds.Add(await WriteLegacyLogAsync(dir.Root));
        var reconciler = new SessionIndexReconciler(indexStore);
        var build      = await reconciler.ReconcileAsync(CancellationToken.None);
        Assert.That(build.Rebuilt, Is.EqualTo(3), "前置：对账先建库并重建全部条目。");

        await using var watcher = await indexStore.OpenVersionWatcherAsync(CancellationToken.None);
        var             before  = await watcher.ReadAsync(CancellationToken.None);

        var round = await reconciler.ReconcileAsync(SessionIndexReconcileOptions.Default, CancellationToken.None);
        var after = await watcher.ReadAsync(CancellationToken.None);

        TestContext.Progress.WriteLine($"data_version before={before} after={after} "          +
                                       $"unchanged={round.Unchanged} rebuilt={round.Rebuilt} " +
                                       $"archiveSynced={round.ArchiveSynced}");
        Assert.That(round.Unchanged, Is.EqualTo(3), "前置：本轮无任何来源变化，全部条目零正文扫描跳过。");
        Assert.That(round.ArchiveSynced, Is.True, "前置：归档投影同步正常完成。");
        // 同值守卫：无来源变化时全部健康行与目标集合同值，同步不再重写任何行，写事务不推进
        // data_version，常驻刷新循环不再把无变化对账误判为数据库变化。
        Assert.That(after, Is.EqualTo(before),
                    "无来源变化的对账不得推进 data_version：同值守卫跳过同值行后写事务不产生任何写入。");
    }

    /// <summary>
    ///     <para>归档集合真实变化后，一轮对账把 archived 投影收敛到索引；未归档行保持 false。</para>
    ///     <para>
    ///         After a real archive-set change, one reconcile round converges the archived projection into the
    ///         index; unarchived rows stay false.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ArchiveProjectionChange_ConvergesThroughReconcile()
    {
        using var dir        = new TestTempDir();
        var       indexStore = new SessionIndexStore(dir.Root);
        var       sessionIds = new List<string>(3);
        for (var i = 0; i < 3; i++) sessionIds.Add(await WriteLegacyLogAsync(dir.Root));
        var reconciler = new SessionIndexReconciler(indexStore);
        await reconciler.ReconcileAsync(CancellationToken.None);

        var archivedId = sessionIds[1];
        await SessionStoreArchiveStore.SetArchivedAsync(dir.Root, archivedId, true, CancellationToken.None);
        var round = await reconciler.ReconcileAsync(SessionIndexReconcileOptions.Default, CancellationToken.None);

        Assert.That(round.ArchiveSynced, Is.True, "归档来源可读时投影同步必须完成。");
        Assert.That(round.Unchanged, Is.EqualTo(3), "归档变化不改写日志，摘要条目保持零正文扫描。");
        var archivedRow = await indexStore.GetRowAsync(archivedId, CancellationToken.None);
        Assert.That(archivedRow, Is.Not.Null);
        Assert.That(archivedRow!.Archived, Is.True, "归档会话的 archived 投影必须经对账收敛为 true。");
        foreach (var id in sessionIds.Where(id => id != archivedId))
            Assert.That((await indexStore.GetRowAsync(id, CancellationToken.None))!.Archived, Is.False,
                        "未归档会话的 archived 投影必须保持 false。");
    }

    /// <summary>
    ///     <para>
    ///         archived 为 NULL 的健康行在同步时视为与目标不同：即使同值守卫跳过同值行，
    ///         NULL 行仍按归档集合初始化为 0 或 1。
    ///     </para>
    ///     <para>
    ///         Healthy rows with a NULL archived value count as differing from the target during sync: even though
    ///         the same-value guard skips matching rows, NULL rows are still initialized to 0 or 1 from the
    ///         archive set.
    ///     </para>
    /// </summary>
    [Test]
    public async Task SyncArchiveProjection_NullArchivedRows_AreInitialized()
    {
        using var dir   = new TestTempDir();
        var       store = new SessionIndexStore(dir.Root);
        await store.InitializeAsync(CancellationToken.None);
        var idA = HexId('a');
        var idB = HexId('b');
        // 摘要插入即 archived = NULL，且后续摘要推进不改写该列，恰好构造守卫必须处理的 NULL 行。
        await store.WriteSummaryAsync(Summary(idA, DateTimeOffset.UtcNow), 10, null, CancellationToken.None);
        await store.WriteSummaryAsync(Summary(idB, DateTimeOffset.UtcNow), 10, null, CancellationToken.None);
        Assert.That((await store.GetRowAsync(idA, CancellationToken.None))!.Archived, Is.Null);
        Assert.That((await store.GetRowAsync(idB, CancellationToken.None))!.Archived, Is.Null);

        await store.SyncArchiveProjectionAsync(new HashSet<string>([idA], StringComparer.Ordinal),
                                               CancellationToken.None);

        Assert.That((await store.GetRowAsync(idA, CancellationToken.None))!.Archived, Is.True,
                    "NULL 行必须视为与目标不同并写入 1，不得因 NULL 比较求值为未知而留在未初始化状态。");
        Assert.That((await store.GetRowAsync(idB, CancellationToken.None))!.Archived, Is.False,
                    "NULL 行必须视为与目标不同并写入 0。");
    }

    /// <summary>
    ///     <para>提交一份仅含身份批次的最小合法日志，返回会话 ID。</para>
    ///     <para>Commits one minimal valid log holding only the identity batch and returns the session id.</para>
    /// </summary>
    private static async Task<string> WriteLegacyLogAsync(string storeDirectory)
    {
        var       session = new ChatSession(Guid.NewGuid().ToString("N"), storeDirectory, Identity);
        using var writer  = new SessionStoreWriter(storeDirectory, session.Id);
        await writer.CommitAsync(session, new ConversationContext(new ContextOptions()), CancellationToken.None);
        return session.Id;
    }

    private static string HexId(char seed) => new(seed, 32);

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
}
