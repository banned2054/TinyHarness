using Microsoft.Data.Sqlite;
using System.Text.Json;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Project;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Persistence;

namespace TinyHarness.Tests;

public class SessionStoreOperationsTests
{
    private static readonly SessionModelIdentity Identity =
        new("offline", "https://offline.invalid/v1", ChatApiKind.ChatCompletions);

    /// <summary>
    ///     <para>置顶准备迁移旧列表一次；重复准备不重复合并，旧标记持久化。</para>
    ///     <para>
    ///         Pin preparation migrates legacy lists once; repeated preparation does not merge again and the
    ///         legacy marker persists.
    ///     </para>
    /// </summary>
    [Test]
    public async Task PreparePinState_MigratesLegacyListsOnce()
    {
        using var dir   = new TestTempDir();
        var       store = dir.CreateDirectory("sessions");
        var       ops   = new SessionStoreOperations();

        var first = await ops.PreparePinStateAsync(store, Resolve(store), ["p1"], ["s1"],
                                                   CancellationToken.None);
        Assert.That(first.LegacyPinsPresent, Is.True);
        Assert.That(ops.PinsForStore(store).PinnedProjectIds, Is.EqualTo(new[] { "p1" }));
        Assert.That(ops.PinsForStore(store).PinnedSessionIds, Is.EqualTo(new[] { "s1" }));
        Assert.That((await SessionStorePinStore.LoadAsync(store, CancellationToken.None)).LegacySidebarPinsImported,
                    Is.True);

        var second = await ops.PreparePinStateAsync(store, Resolve(store), [], [], CancellationToken.None);
        Assert.That(second.LegacyPinsPresent, Is.False);
        Assert.That(ops.PinsForStore(store).PinnedProjectIds, Is.EqualTo(new[] { "p1" }));
        Assert.That(ops.PinsForStore(store).PinnedSessionIds, Is.EqualTo(new[] { "s1" }));
    }

    /// <summary>
    ///     <para>受保护目录与传入 store 不一致时拒绝准备并标记置顶不可用。</para>
    ///     <para>
    ///         A protected directory mismatching the requested store refuses preparation and marks pins
    ///         unavailable.
    ///     </para>
    /// </summary>
    [Test]
    public async Task PreparePinState_ResolutionMismatch_ThrowsAndMarksUnavailable()
    {
        using var dir   = new TestTempDir();
        var       store = dir.CreateDirectory("sessions");
        var       other = dir.CreateDirectory("other");
        var       ops   = new SessionStoreOperations();

        await TestAssert.ThrowsAsync<SessionStoreException>(() => ops.PreparePinStateAsync(store, Resolve(other), [],
                                                                [],
                                                                CancellationToken.None));
        Assert.That(ops.IsPinStateUnavailable(store), Is.True);
        Assert.That(ops.PinsForStore(store).PinnedSessionIds, Is.Empty);
    }

    /// <summary>
    ///     <para>置顶文件损坏时标记不可用并抛出原异常；修复后再次准备恢复可用。</para>
    ///     <para>
    ///         A damaged pin file marks pins unavailable and rethrows; preparing again after repair restores
    ///         availability.
    ///     </para>
    /// </summary>
    [Test]
    public async Task PreparePinState_DamagedFile_MarksUnavailableAndRecovers()
    {
        using var dir   = new TestTempDir();
        var       store = dir.CreateDirectory("sessions");
        var       ops   = new SessionStoreOperations();
        await File.WriteAllTextAsync(SessionStorePinStore.FilePath(store), "not json",
                                     CancellationToken.None);

        await TestAssert.ThrowsAsync<SessionStorePinException>(() => ops.PreparePinStateAsync(store, Resolve(store), [],
                                                                   [],
                                                                   CancellationToken.None));
        Assert.That(ops.IsPinStateUnavailable(store), Is.True);

        File.Delete(SessionStorePinStore.FilePath(store));
        await ops.PreparePinStateAsync(store, Resolve(store), [], [], CancellationToken.None);
        Assert.That(ops.IsPinStateUnavailable(store), Is.False);
        Assert.That(ops.PinsForStore(store).PinnedSessionIds, Is.Empty);
    }

    /// <summary>
    ///     <para>归档准备加载快照；损坏时标记不可用，目录解析不一致时抛出拒绝。</para>
    ///     <para>
    ///         Archive preparation loads the snapshot; damage marks it unavailable and a resolution mismatch
    ///         throws in refusal.
    ///     </para>
    /// </summary>
    [Test]
    public async Task PrepareArchiveState_LoadsSnapshotAndMarksUnavailableOnDamage()
    {
        using var dir       = new TestTempDir();
        var       store     = dir.CreateDirectory("sessions");
        var       other     = dir.CreateDirectory("other");
        var       ops       = new SessionStoreOperations();
        var       sessionId = await SeedSessionAsync(store);

        await TestAssert.ThrowsAsync<SessionStoreException>(() => ops.PrepareArchiveStateAsync(store, Resolve(other),
                                                                CancellationToken.None));

        await SessionStoreArchiveStore.SetArchivedAsync(store, sessionId, true, CancellationToken.None);
        var archives = await ops.PrepareArchiveStateAsync(store, Resolve(store), CancellationToken.None);
        Assert.That(archives.ArchivedSessionIds, Does.Contain(sessionId));
        Assert.That(ops.ArchivesForStore(store).ArchivedSessionIds, Does.Contain(sessionId));

        await File.WriteAllTextAsync(SessionStoreArchiveStore.FilePath(store), "not json",
                                     CancellationToken.None);
        await TestAssert.ThrowsAsync<SessionStoreArchiveException>(() => ops.PrepareArchiveStateAsync(store,
                                                                       Resolve(store),
                                                                       CancellationToken.None));
        Assert.That(ops.IsArchiveStateUnavailable(store), Is.True);
    }

    /// <summary>
    ///     <para>会话置顶更新快照并失效 ID 集合缓存；取消置顶后缓存查询不再包含该会话。</para>
    ///     <para>
    ///         Session pin writes update the snapshot and invalidate the id-set cache; unpinning removes the
    ///         session from cached lookups.
    ///     </para>
    /// </summary>
    [Test]
    public async Task SetSessionPinned_UpdatesSnapshotAndInvalidatesIdCache()
    {
        using var dir       = new TestTempDir();
        var       store     = dir.CreateDirectory("sessions");
        var       ops       = new SessionStoreOperations();
        var       sessionId = await SeedSessionAsync(store);

        await ops.SetSessionPinnedAsync(store, sessionId, true, CancellationToken.None);
        Assert.That(ops.PinnedSessionIdsForStore(store), Does.Contain(sessionId));
        Assert.That(ops.PinsForStore(store).PinnedSessionIds, Does.Contain(sessionId));

        await ops.SetSessionPinnedAsync(store, sessionId, false, CancellationToken.None);
        Assert.That(ops.PinnedSessionIdsForStore(store), Does.Not.Contain(sessionId));
        Assert.That(ops.PinsForStore(store).PinnedSessionIds, Is.Empty);
    }

    /// <summary>
    ///     <para>项目置顶进入列表顶部，拖放排序保持既有插入语义；未置顶项目不参与移动。</para>
    ///     <para>
    ///         Newly pinned projects enter at the top, drag reordering keeps the established insertion
    ///         semantics, and unpinned projects never move.
    ///     </para>
    /// </summary>
    [Test]
    public async Task SetProjectPinned_OrdersTopAndMovePreservesInsertionSemantics()
    {
        using var dir   = new TestTempDir();
        var       store = dir.CreateDirectory("sessions");
        var       ops   = new SessionStoreOperations();

        await ops.SetProjectPinnedAsync(store, "p1", true, null, CancellationToken.None);
        await ops.SetProjectPinnedAsync(store, "p2", true, null, CancellationToken.None);
        await ops.SetProjectPinnedAsync(store, "p3", true, null, CancellationToken.None);
        Assert.That(ops.PinsForStore(store).PinnedProjectIds, Is.EqualTo(new[] { "p3", "p2", "p1" }));

        await ops.MovePinnedProjectAsync(store, "p3", "p1", true, null, CancellationToken.None);
        Assert.That(ops.PinsForStore(store).PinnedProjectIds, Is.EqualTo(new[] { "p2", "p1", "p3" }));

        await ops.MovePinnedProjectAsync(store, "p2", "p3", false, null, CancellationToken.None);
        Assert.That(ops.PinsForStore(store).PinnedProjectIds, Is.EqualTo(new[] { "p1", "p2", "p3" }));

        await ops.MovePinnedProjectAsync(store, "unpinned", "p1", false, null, CancellationToken.None);
        Assert.That(ops.PinsForStore(store).PinnedProjectIds, Is.EqualTo(new[] { "p1", "p2", "p3" }));
    }

    /// <summary>
    ///     <para>项目置顶写入的让位谓词在取得串行门后才求值：命中时不落盘、不更新快照并静默返回让位。</para>
    ///     <para>
    ///         A project pin write's yield predicate is evaluated only after the serial gate is acquired: a hit
    ///         writes nothing, keeps the snapshot, and silently returns yielded.
    ///     </para>
    /// </summary>
    [Test]
    public async Task SetProjectPinned_YieldPredicateHitsInsideGate_SkipsWriteAndSnapshot()
    {
        using var dir   = new TestTempDir();
        var       store = dir.CreateDirectory("sessions");
        var       ops   = new SessionStoreOperations();
        await ops.SetProjectPinnedAsync(store, "p1", true, null, CancellationToken.None);

        var evaluations = 0;

        bool Yield()
        {
            Interlocked.Increment(ref evaluations);
            return true;
        }

        var gate = ops.MutationGate;
        await gate.WaitAsync(CancellationToken.None);
        Task<bool> writeTask;
        try
        {
            writeTask = ops.SetProjectPinnedAsync(store, "p1", false, Yield, CancellationToken.None);
            await Task.Delay(300, CancellationToken.None);
            Assert.That(writeTask.IsCompleted, Is.False, "置顶写入必须等待共享串行门。");
            Assert.That(evaluations, Is.EqualTo(0), "让位谓词不得在取得串行门之前求值。");
        }
        finally
        {
            gate.Release();
        }

        Assert.That(await writeTask, Is.False, "命中让位谓词时写入必须静默让位。");
        Assert.That(evaluations, Is.EqualTo(1));
        Assert.That((await SessionStorePinStore.LoadAsync(store, CancellationToken.None)).PinnedProjectIds,
                    Does.Contain("p1"), "让位时不得改写置顶文件。");
        Assert.That(ops.PinsForStore(store).PinnedProjectIds, Does.Contain("p1"), "让位时不得更新快照。");

        // 对照：谓词未命中时同一写入照常执行并更新快照。
        Assert.That(await ops.SetProjectPinnedAsync(store, "p1", false, null, CancellationToken.None), Is.True);
        Assert.That(ops.PinsForStore(store).PinnedProjectIds, Is.Empty);
    }

    /// <summary>
    ///     <para>置顶项目排序写入命中让位谓词时静默放弃：磁盘与快照保持原顺序。</para>
    ///     <para>
    ///         A pinned-project reorder silently yields when its write precondition hits: disk and snapshot keep
    ///         the original order.
    ///     </para>
    /// </summary>
    [Test]
    public async Task MovePinnedProject_YieldPredicateHit_SkipsWriteAndSnapshot()
    {
        using var dir   = new TestTempDir();
        var       store = dir.CreateDirectory("sessions");
        var       ops   = new SessionStoreOperations();
        await ops.SetProjectPinnedAsync(store, "p1", true, null, CancellationToken.None);
        await ops.SetProjectPinnedAsync(store, "p2", true, null, CancellationToken.None);

        var applied = await ops.MovePinnedProjectAsync(store, "p1", "p2", false, () => true,
                                                       CancellationToken.None);

        Assert.That(applied, Is.False, "命中让位谓词时排序写入必须静默让位。");
        Assert.That((await SessionStorePinStore.LoadAsync(store, CancellationToken.None)).PinnedProjectIds,
                    Is.EqualTo(new[] { "p2", "p1" }), "让位时不得改写置顶文件。");
        Assert.That(ops.PinsForStore(store).PinnedProjectIds, Is.EqualTo(new[] { "p2", "p1" }),
                    "让位时不得更新快照。");
    }

    /// <summary>
    ///     <para>置顶与归档写入共用一把串行门：门被持有时两类写入都不进入。</para>
    ///     <para>
    ///         Pin and archive writes share one serial gate: while the gate is held, neither write proceeds.
    ///     </para>
    /// </summary>
    [Test]
    public async Task MutationGate_SerializesPinAndArchiveWrites()
    {
        using var dir       = new TestTempDir();
        var       store     = dir.CreateDirectory("sessions");
        var       ops       = new SessionStoreOperations();
        var       sessionId = await SeedSessionAsync(store);
        var       gate      = ops.MutationGate;
        await gate.WaitAsync(CancellationToken.None);
        try
        {
            var pinTask = ops.SetSessionPinnedAsync(store, sessionId, true, CancellationToken.None);
            var archiveTask = ops.SetSessionArchivedAsync(store, Resolve(store), sessionId, true,
                                                          CancellationToken.None);
            await Task.Delay(300, CancellationToken.None);
            Assert.That(pinTask.IsCompleted, Is.False, "置顶写入必须等待共享串行门。");
            Assert.That(archiveTask.IsCompleted, Is.False, "归档写入必须等待共享串行门。");
            gate.Release();
            await Task.WhenAll(pinTask, archiveTask);
            Assert.That(ops.PinsForStore(store).PinnedSessionIds, Does.Contain(sessionId));
            Assert.That(ops.ArchivesForStore(store).ArchivedSessionIds, Does.Contain(sessionId));
        }
        finally
        {
            if (gate.CurrentCount == 0) gate.Release();
        }
    }

    /// <summary>
    ///     <para>归档保存后尽力同步索引投影：可用索引的归档位随保存更新。</para>
    ///     <para>
    ///         After the archive save the index projection syncs best-effort: an available index tracks the
    ///         saved archive bit.
    ///     </para>
    /// </summary>
    [Test]
    public async Task SetSessionArchived_SyncsIndexProjectionWhenAvailable()
    {
        using var dir       = new TestTempDir();
        var       store     = dir.CreateDirectory("sessions");
        var       ops       = new SessionStoreOperations();
        var       sessionId = await SeedSessionAsync(store);
        var       index     = new SessionIndexStore(store);
        await new SessionIndexReconciler(index).ReconcileAsync(CancellationToken.None);

        var archives = await ops.SetSessionArchivedAsync(store, Resolve(store), sessionId, true,
                                                         CancellationToken.None);
        Assert.That(archives.ArchivedSessionIds, Does.Contain(sessionId));
        Assert.That((await index.GetRowAsync(sessionId, CancellationToken.None))!.Archived, Is.True);

        await ops.SetSessionArchivedAsync(store, Resolve(store), sessionId, false, CancellationToken.None);
        Assert.That((await index.GetRowAsync(sessionId, CancellationToken.None))!.Archived, Is.False);
    }

    /// <summary>
    ///     <para>索引缺失时跳过投影同步且不创建库文件，归档权威保存不受影响。</para>
    ///     <para>
    ///         A missing index skips the projection sync without creating a database file, leaving the
    ///         authoritative archive save untouched.
    ///     </para>
    /// </summary>
    [Test]
    public async Task SetSessionArchived_MissingIndex_SkipsSyncWithoutCreatingDatabase()
    {
        using var dir       = new TestTempDir();
        var       store     = dir.CreateDirectory("sessions");
        var       ops       = new SessionStoreOperations();
        var       sessionId = await SeedSessionAsync(store);

        var archives = await ops.SetSessionArchivedAsync(store, Resolve(store), sessionId, true,
                                                         CancellationToken.None);
        Assert.That(archives.ArchivedSessionIds, Does.Contain(sessionId));
        Assert.That(SessionIndexStore.Exists(store), Is.False, "缺失索引不得被归档同步创建。");
        Assert.That((await SessionStoreArchiveStore.LoadAsync(store, CancellationToken.None)).ArchivedSessionIds,
                    Does.Contain(sessionId));
    }

    /// <summary>
    ///     <para>索引不可用时跳过同步不回滚归档保存；损坏库文件保持原样，不抛出阻断。</para>
    ///     <para>
    ///         An unusable index skips the sync without rolling the archive save back; the damaged database
    ///         file stays as is and nothing blocks the caller.
    ///     </para>
    /// </summary>
    [Test]
    public async Task SetSessionArchived_UnusableIndex_SkipsSyncWithoutRollingBackArchiveSave()
    {
        using var dir       = new TestTempDir();
        var       store     = dir.CreateDirectory("sessions");
        var       ops       = new SessionStoreOperations();
        var       sessionId = await SeedSessionAsync(store);
        var       index     = new SessionIndexStore(store);
        await new SessionIndexReconciler(index).ReconcileAsync(CancellationToken.None);
        var databasePath = SessionIndexStore.PathFor(store);
        await File.WriteAllTextAsync(databasePath, "corrupted database bytes", CancellationToken.None);

        var archives = await ops.SetSessionArchivedAsync(store, Resolve(store), sessionId, true,
                                                         CancellationToken.None);
        Assert.That(archives.ArchivedSessionIds, Does.Contain(sessionId));
        Assert.That(await File.ReadAllTextAsync(databasePath, CancellationToken.None),
                    Is.EqualTo("corrupted database bytes"), "损坏库文件不得被同步覆盖或重建。");
    }

    /// <summary>
    ///     <para>归档保存失败时抛出原异常且快照不变；不影响后续置顶查询。</para>
    ///     <para>
    ///         A failed archive save rethrows and keeps the snapshot unchanged; later pin lookups are
    ///         unaffected.
    ///     </para>
    /// </summary>
    [Test]
    public async Task SetSessionArchived_ArchiveSaveFailure_KeepsSnapshotAndThrows()
    {
        using var dir       = new TestTempDir();
        var       store     = dir.CreateDirectory("sessions");
        var       ops       = new SessionStoreOperations();
        var       sessionId = await SeedSessionAsync(store);
        Directory.CreateDirectory(SessionStoreArchiveStore.FilePath(store));

        await TestAssert.ThrowsAsync<SessionStoreArchiveException>(() => ops.SetSessionArchivedAsync(store,
                                                                       Resolve(store),
                                                                       sessionId, true,
                                                                       CancellationToken.None));
        Assert.That(ops.ArchivesForStore(store).ArchivedSessionIds, Is.Empty);
        Directory.Delete(SessionStoreArchiveStore.FilePath(store));
    }

    /// <summary>
    ///     <para>永久删除会话移除日志、归档、置顶与索引投影；全部清理成功时无清理问题。</para>
    ///     <para>
    ///         Permanent session deletion removes the log, archive, pin, and index projections; a fully
    ///         successful cleanup reports no issues.
    ///     </para>
    /// </summary>
    [Test]
    public async Task DeleteSessionPermanently_RemovesLogAndAllProjections()
    {
        using var dir       = new TestTempDir();
        var       store     = dir.CreateDirectory("sessions");
        var       workspace = dir.CreateDirectory("workspace");
        var       ops       = new SessionStoreOperations();
        var       sessionId = await SeedSessionAsync(store);
        var       index     = new SessionIndexStore(store);
        await new SessionIndexReconciler(index).ReconcileAsync(CancellationToken.None);
        await SessionStoreArchiveStore.SetArchivedAsync(store, sessionId, true, CancellationToken.None);
        await SessionStorePinStore.SetSessionPinnedAsync(store, sessionId, true, CancellationToken.None);
        await ops.PrepareArchiveStateAsync(store, Resolve(store), CancellationToken.None);

        var issues = await ops.DeleteSessionPermanentlyAsync(store, workspace, sessionId, CancellationToken.None);

        Assert.That(issues, Is.Empty);
        Assert.That(File.Exists(Path.Combine(store, sessionId + SessionStoreWriter.LogFileSuffix)), Is.False);
        Assert.That((await SessionStoreArchiveStore.LoadAsync(store, CancellationToken.None)).ArchivedSessionIds,
                    Does.Not.Contain(sessionId));
        Assert.That((await SessionStorePinStore.LoadAsync(store, CancellationToken.None)).PinnedSessionIds,
                    Does.Not.Contain(sessionId));
        Assert.That(await index.GetRowAsync(sessionId, CancellationToken.None), Is.Null);
        Assert.That(ops.ArchivedSessionIdsForStore(store), Does.Not.Contain(sessionId));
    }

    /// <summary>
    ///     <para>writer 被占用时删除抛出且不做任何投影清理，日志与归档状态保持原样。</para>
    ///     <para>
    ///         A held writer makes deletion throw without any projection cleanup, leaving the log and archive
    ///         state untouched.
    ///     </para>
    /// </summary>
    [Test]
    public async Task DeleteSessionPermanently_WriterHeld_ThrowsWithoutAnyCleanup()
    {
        using var dir       = new TestTempDir();
        var       store     = dir.CreateDirectory("sessions");
        var       workspace = dir.CreateDirectory("workspace");
        var       ops       = new SessionStoreOperations();
        var       sessionId = await SeedSessionAsync(store);
        await new SessionIndexReconciler(new SessionIndexStore(store)).ReconcileAsync(CancellationToken.None);
        await SessionStoreArchiveStore.SetArchivedAsync(store, sessionId, true, CancellationToken.None);

        using var writer = await SessionStoreWriter.OpenExistingAsync(store, sessionId, null,
                                                                      CancellationToken.None);
        await TestAssert.ThrowsAsync<SessionStoreException>(() => ops.DeleteSessionPermanentlyAsync(store, workspace,
                                                                sessionId,
                                                                CancellationToken.None));
        Assert.That(File.Exists(Path.Combine(store, sessionId + SessionStoreWriter.LogFileSuffix)), Is.True);
        Assert.That((await SessionStoreArchiveStore.LoadAsync(store, CancellationToken.None)).ArchivedSessionIds,
                    Does.Contain(sessionId));
    }

    /// <summary>
    ///     <para>归档投影清理失败记入 issues 并继续置顶与索引清理，不阻断删除结果。</para>
    ///     <para>
    ///         A failed archive cleanup is recorded as an issue while pin and index cleanup continue, never
    ///         blocking the deletion outcome.
    ///     </para>
    /// </summary>
    [Test]
    public async Task DeleteSessionPermanently_FailedArchiveCleanup_ReturnsIssueAndContinuesRest()
    {
        using var dir       = new TestTempDir();
        var       store     = dir.CreateDirectory("sessions");
        var       workspace = dir.CreateDirectory("workspace");
        var       ops       = new SessionStoreOperations();
        var       sessionId = await SeedSessionAsync(store);
        var       index     = new SessionIndexStore(store);
        await new SessionIndexReconciler(index).ReconcileAsync(CancellationToken.None);
        await SessionStorePinStore.SetSessionPinnedAsync(store, sessionId, true, CancellationToken.None);
        // 归档状态叶子为目录占位：归档清理立即失败，其余清理继续。
        Directory.CreateDirectory(SessionStoreArchiveStore.FilePath(store));

        var issues = await ops.DeleteSessionPermanentlyAsync(store, workspace, sessionId, CancellationToken.None);

        Assert.That(issues.Select(issue => issue.Area), Is.EqualTo(new[] { SessionStoreCleanupArea.ArchiveState }));
        Assert.That(File.Exists(Path.Combine(store, sessionId + SessionStoreWriter.LogFileSuffix)), Is.False);
        Assert.That((await SessionStorePinStore.LoadAsync(store, CancellationToken.None)).PinnedSessionIds,
                    Does.Not.Contain(sessionId));
        Assert.That(await index.GetRowAsync(sessionId, CancellationToken.None), Is.Null);
    }

    /// <summary>
    ///     <para>置顶投影清理失败记入 issues 并继续归档与索引清理，不阻断删除结果。</para>
    ///     <para>
    ///         A failed pin cleanup is recorded as an issue while archive and index cleanup continue, never
    ///         blocking the deletion outcome.
    ///     </para>
    /// </summary>
    [Test]
    public async Task DeleteSessionPermanently_FailedPinCleanup_ReturnsIssueAndContinuesRest()
    {
        using var dir       = new TestTempDir();
        var       store     = dir.CreateDirectory("sessions");
        var       workspace = dir.CreateDirectory("workspace");
        var       ops       = new SessionStoreOperations();
        var       sessionId = await SeedSessionAsync(store);
        var       index     = new SessionIndexStore(store);
        await new SessionIndexReconciler(index).ReconcileAsync(CancellationToken.None);
        await SessionStoreArchiveStore.SetArchivedAsync(store, sessionId, true, CancellationToken.None);
        await SessionStorePinStore.SetSessionPinnedAsync(store, sessionId, true, CancellationToken.None);
        // 置顶状态叶子为目录占位：置顶清理立即失败，其余清理继续。
        File.Delete(SessionStorePinStore.FilePath(store));
        Directory.CreateDirectory(SessionStorePinStore.FilePath(store));

        var issues = await ops.DeleteSessionPermanentlyAsync(store, workspace, sessionId, CancellationToken.None);

        Assert.That(issues.Select(issue => issue.Area), Is.EqualTo(new[] { SessionStoreCleanupArea.PinState }));
        Assert.That(File.Exists(Path.Combine(store, sessionId + SessionStoreWriter.LogFileSuffix)), Is.False);
        Assert.That((await SessionStoreArchiveStore.LoadAsync(store, CancellationToken.None)).ArchivedSessionIds,
                    Does.Not.Contain(sessionId), "置顶清理失败不得回滚已完成的归档清理。");
        Assert.That(await index.GetRowAsync(sessionId, CancellationToken.None), Is.Null);
    }

    /// <summary>
    ///     <para>索引投影清理失败记入 issues，归档与置顶清理照常完成，不阻断删除结果。</para>
    ///     <para>
    ///         A failed index cleanup is recorded as an issue while archive and pin cleanup complete as usual,
    ///         never blocking the deletion outcome.
    ///     </para>
    /// </summary>
    [Test]
    public async Task DeleteSessionPermanently_FailedIndexCleanup_ReturnsIssueAndContinuesRest()
    {
        using var dir       = new TestTempDir();
        var       store     = dir.CreateDirectory("sessions");
        var       workspace = dir.CreateDirectory("workspace");
        var       ops       = new SessionStoreOperations();
        var       sessionId = await SeedSessionAsync(store);
        var       index     = new SessionIndexStore(store);
        await new SessionIndexReconciler(index).ReconcileAsync(CancellationToken.None);
        await SessionStoreArchiveStore.SetArchivedAsync(store, sessionId, true, CancellationToken.None);
        await SessionStorePinStore.SetSessionPinnedAsync(store, sessionId, true, CancellationToken.None);

        // 独立连接持写事务占用索引写锁：WAL 下可用性探测（读者）照常可用，删除写入等待超时后失败。
        await using var connection = OpenRawIndexConnection(store);
        await connection.OpenAsync(CancellationToken.None);
        await using var writeTransaction = await connection.BeginTransactionAsync(CancellationToken.None);

        var issues = await ops.DeleteSessionPermanentlyAsync(store, workspace, sessionId, CancellationToken.None);

        Assert.That(issues.Select(issue => issue.Area), Is.EqualTo(new[] { SessionStoreCleanupArea.SessionIndex }));
        Assert.That(File.Exists(Path.Combine(store, sessionId + SessionStoreWriter.LogFileSuffix)), Is.False);
        Assert.That((await SessionStoreArchiveStore.LoadAsync(store, CancellationToken.None)).ArchivedSessionIds,
                    Does.Not.Contain(sessionId));
        Assert.That((await SessionStorePinStore.LoadAsync(store, CancellationToken.None)).PinnedSessionIds,
                    Does.Not.Contain(sessionId));
        Assert.That(await index.GetRowAsync(sessionId, CancellationToken.None), Is.Not.Null,
                    "索引清理失败时行必须保留，等待后台对账追平。");
    }

    /// <summary>
    ///     <para>删除前取消时异常原样传播：日志保留，归档、置顶与索引状态原样。</para>
    ///     <para>
    ///         A pre-deletion cancellation propagates untouched: the log stays, and archive, pin, and index
    ///         states remain exactly as seeded.
    ///     </para>
    /// </summary>
    [Test]
    public async Task DeleteSessionPermanently_CancelledBeforeDeletion_KeepsEveryState()
    {
        using var dir       = new TestTempDir();
        var       store     = dir.CreateDirectory("sessions");
        var       workspace = dir.CreateDirectory("workspace");
        var       ops       = new SessionStoreOperations();
        var       sessionId = await SeedSessionAsync(store);
        var       index     = new SessionIndexStore(store);
        await new SessionIndexReconciler(index).ReconcileAsync(CancellationToken.None);
        await SessionStoreArchiveStore.SetArchivedAsync(store, sessionId, true, CancellationToken.None);
        await SessionStorePinStore.SetSessionPinnedAsync(store, sessionId, true, CancellationToken.None);
        using var lifetime = new CancellationTokenSource();
        lifetime.Cancel();

        // TaskCanceledException 是 OperationCanceledException 的子类；按宿主契约断言可被取消分支捕获。
        try
        {
            await ops.DeleteSessionPermanentlyAsync(store, workspace, sessionId, lifetime.Token);
            Assert.Fail("删除前取消必须中止删除。");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }

        Assert.That(File.Exists(Path.Combine(store, sessionId + SessionStoreWriter.LogFileSuffix)), Is.True);
        Assert.That((await SessionStoreArchiveStore.LoadAsync(store, CancellationToken.None)).ArchivedSessionIds,
                    Does.Contain(sessionId));
        Assert.That((await SessionStorePinStore.LoadAsync(store, CancellationToken.None)).PinnedSessionIds,
                    Does.Contain(sessionId));
        Assert.That(await index.GetRowAsync(sessionId, CancellationToken.None), Is.Not.Null);
    }

    /// <summary>
    ///     <para>项目删除按固定顺序编排 gateway 各阶段，并完成解绑、置顶清理与目录册移除。</para>
    ///     <para>
    ///         Project deletion orchestrates the gateway stages in a fixed order, completing unassignment, pin
    ///         cleanup, and catalog removal.
    ///     </para>
    /// </summary>
    [Test]
    public async Task DeleteProject_OrchestratesGatewayStagesInOrder()
    {
        using var dir         = new TestTempDir();
        var       store       = dir.CreateDirectory("sessions");
        var       workspace   = dir.CreateDirectory("workspace");
        var       ops         = new SessionStoreOperations();
        var       catalogPath = ProjectStore.FilePath(store);
        var       project     = await ProjectStore.GetOrCreateAsync(catalogPath, workspace);
        var       sessionId   = await SeedSessionAsync(store, project.Id);
        await new SessionIndexReconciler(new SessionIndexStore(store)).ReconcileAsync(CancellationToken.None);
        await ops.SetProjectPinnedAsync(store, project.Id, true, null, CancellationToken.None);
        var gateway = new RecordingGateway();

        var deleting = await ops.DeleteProjectAsync(store, project.Id, project.Revision, gateway,
                                                    CancellationToken.None);

        Assert.That(deleting.Id, Is.EqualTo(project.Id));
        Assert.That(deleting.IsDeleting, Is.True);
        Assert.That(gateway.Calls, Is.EqualTo(new[]
        {
            nameof(ISessionProjectDeletionGateway.DeletionMarkedAsync),
            nameof(ISessionProjectDeletionGateway.UnbindDraftPagesAsync),
            nameof(ISessionProjectDeletionGateway.UnbindOpenRuntimeSessionsAsync),
            nameof(ISessionProjectDeletionGateway.UnassignmentAppliedAsync),
            nameof(ISessionProjectDeletionGateway.PrepareCompletionAsync),
            nameof(ISessionProjectDeletionGateway.CompletedAsync)
        }));
        Assert.That((await ProjectStore.LoadAsync(catalogPath, CancellationToken.None)).Projects, Is.Empty);
        Assert.That((await SessionStoreReader.ReadAsync(store, sessionId, CancellationToken.None))!.ProjectId,
                    Is.Null);
        Assert.That((await SessionStorePinStore.LoadAsync(store, CancellationToken.None)).PinnedProjectIds,
                    Does.Not.Contain(project.Id));
    }

    /// <summary>
    ///     <para>gateway 钩子抛出异常时编排中止并原样传播，目录册保留删除标记；释放后重试完成。</para>
    ///     <para>
    ///         A gateway hook's exception aborts the orchestration with its original propagation and keeps the
    ///         catalog's deletion marker; after release a retry completes.
    ///     </para>
    /// </summary>
    [Test]
    public async Task DeleteProject_WriterHeldIncompleteUnassignment_AbortsAndResumesOnRetry()
    {
        using var dir         = new TestTempDir();
        var       store       = dir.CreateDirectory("sessions");
        var       workspace   = dir.CreateDirectory("workspace");
        var       ops         = new SessionStoreOperations();
        var       catalogPath = ProjectStore.FilePath(store);
        var       project     = await ProjectStore.GetOrCreateAsync(catalogPath, workspace);
        await SeedSessionAsync(store, project.Id);
        var gateway = new RecordingGateway
        {
            UnassignmentPolicy = result =>
            {
                Assert.That(result.IsComplete, Is.False, "前置：writer 占用必须产生不完整解绑。");
                throw new InvalidOperationException("Incomplete unassignment policy.");
            }
        };

        using (var held = await SessionStoreWriter.OpenExistingAsync(store, await SingleSessionIdAsync(store),
                                                                     null, CancellationToken.None))
        {
            await TestAssert.ThrowsAsync<InvalidOperationException>(() => ops.DeleteProjectAsync(store, project.Id,
                                                                        project.Revision, gateway,
                                                                        CancellationToken.None));
        }

        Assert.That((await ProjectStore.LoadAsync(catalogPath, CancellationToken.None)).Projects.Single().IsDeleting,
                    Is.True);
        Assert.That(gateway.Calls, Does.Not.Contain(nameof(ISessionProjectDeletionGateway.CompletedAsync)));

        await ops.DeleteProjectAsync(store, project.Id, project.Revision, new RecordingGateway(),
                                     CancellationToken.None);
        Assert.That((await ProjectStore.LoadAsync(catalogPath, CancellationToken.None)).Projects, Is.Empty);
    }

    /// <summary>
    ///     <para>gateway 不主动拒绝时 Core 自行中止不完整解绑：删除标记、置顶与日志归属保留，等待继续删除。</para>
    ///     <para>
    ///         With a gateway that never rejects, Core aborts an incomplete unassignment itself; the deletion
    ///         marker, pins, and journal ownership are preserved for a later resume.
    ///     </para>
    /// </summary>
    [Test]
    public async Task DeleteProject_PassiveGatewayPendingUnassignment_CoreRejectsIncompleteAndKeepsState()
    {
        using var dir         = new TestTempDir();
        var       store       = dir.CreateDirectory("sessions");
        var       workspace   = dir.CreateDirectory("workspace");
        var       ops         = new SessionStoreOperations();
        var       catalogPath = ProjectStore.FilePath(store);
        var       project     = await ProjectStore.GetOrCreateAsync(catalogPath, workspace);
        var       sessionId   = await SeedSessionAsync(store, project.Id);
        await ops.SetProjectPinnedAsync(store, project.Id, true, null, CancellationToken.None);
        var gateway = new RecordingGateway();

        using (var held = await SessionStoreWriter.OpenExistingAsync(store, sessionId, null, CancellationToken.None))
        {
            var exception = await TestAssert
               .ThrowsAsync<SessionProjectUnassignmentIncompleteException>(() => ops.DeleteProjectAsync(
                                                                            store, project.Id, project.Revision,
                                                                            gateway, CancellationToken.None));
            Assert.That(exception.Result.Outcomes.Select(outcome => outcome.Status),
                        Does.Contain(SessionProjectUnassignmentStatus.Pending), "前置：writer 占用产生 Pending 结果。");
        }

        Assert.That((await ProjectStore.LoadAsync(catalogPath, CancellationToken.None)).Projects.Single().IsDeleting,
                    Is.True);
        Assert.That((await SessionStorePinStore.LoadAsync(store, CancellationToken.None)).PinnedProjectIds,
                    Does.Contain(project.Id));
        // 归属未被改动：释放占用的 writer 后日志仍指向该项目，置顶清理与移除均未执行。
        Assert.That((await SessionStoreReader.ReadAsync(store, sessionId, CancellationToken.None))!.ProjectId,
                    Is.EqualTo(project.Id));
        Assert.That(gateway.Calls, Does.Not.Contain(nameof(ISessionProjectDeletionGateway.PrepareCompletionAsync)));
        Assert.That(gateway.Calls, Does.Not.Contain(nameof(ISessionProjectDeletionGateway.CompletedAsync)));
    }

    /// <summary>
    ///     <para>gateway 不主动拒绝而解绑出现 Failed 结果时 Core 同样中止：目录册删除标记与置顶保留。</para>
    ///     <para>
    ///         When a passive gateway meets a Failed unassignment outcome, Core aborts as well; the catalog's
    ///         deletion marker and pins are preserved.
    ///     </para>
    /// </summary>
    [Test]
    public async Task DeleteProject_PassiveGatewayFailedUnassignment_CoreRejectsIncompleteAndKeepsCatalog()
    {
        using var dir         = new TestTempDir();
        var       store       = dir.CreateDirectory("sessions");
        var       workspace   = dir.CreateDirectory("workspace");
        var       ops         = new SessionStoreOperations();
        var       catalogPath = ProjectStore.FilePath(store);
        var       project     = await ProjectStore.GetOrCreateAsync(catalogPath, workspace);
        // 非法会话 ID 的日志文件使解绑批次产生 Failed 结果，不依赖 writer 占用。
        dir.WriteFile(Path.Combine("sessions", "broken-id" + SessionStoreWriter.LogFileSuffix), string.Empty);
        await ops.SetProjectPinnedAsync(store, project.Id, true, null, CancellationToken.None);
        var gateway = new RecordingGateway();

        var exception = await TestAssert.ThrowsAsync<SessionProjectUnassignmentIncompleteException>(() =>
            ops.DeleteProjectAsync(store, project.Id, project.Revision, gateway,
                                   CancellationToken.None));

        Assert.That(exception.Result.Outcomes.Select(outcome => outcome.Status),
                    Is.EqualTo(new[] { SessionProjectUnassignmentStatus.Failed }), "前置：坏文件名产生 Failed 结果。");
        Assert.That((await ProjectStore.LoadAsync(catalogPath, CancellationToken.None)).Projects.Single().IsDeleting,
                    Is.True);
        Assert.That((await SessionStorePinStore.LoadAsync(store, CancellationToken.None)).PinnedProjectIds,
                    Does.Contain(project.Id));
        Assert.That(gateway.Calls, Does.Not.Contain(nameof(ISessionProjectDeletionGateway.PrepareCompletionAsync)));
        Assert.That(gateway.Calls, Does.Not.Contain(nameof(ISessionProjectDeletionGateway.CompletedAsync)));
    }

    /// <summary>
    ///     <para>编排中途取消时异常原样传播，目录册保留删除标记供后续重试。</para>
    ///     <para>
    ///         A mid-orchestration cancellation propagates untouched and keeps the catalog's deletion marker
    ///         for a later retry.
    ///     </para>
    /// </summary>
    [Test]
    public async Task DeleteProject_CancelledDuringOrchestration_KeepsDeletionMarker()
    {
        using var dir         = new TestTempDir();
        var       store       = dir.CreateDirectory("sessions");
        var       workspace   = dir.CreateDirectory("workspace");
        var       ops         = new SessionStoreOperations();
        var       catalogPath = ProjectStore.FilePath(store);
        var       project     = await ProjectStore.GetOrCreateAsync(catalogPath, workspace);
        await SeedSessionAsync(store, project.Id);
        using var lifetime = new CancellationTokenSource();
        var       gateway  = new RecordingGateway { OnDeletionMarked = () => lifetime.Cancel() };

        // TaskCanceledException 是 OperationCanceledException 的子类；按宿主契约断言可被取消分支捕获。
        try
        {
            await ops.DeleteProjectAsync(store, project.Id, project.Revision, gateway, lifetime.Token);
            Assert.Fail("编排中途取消必须中止删除。");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }

        Assert.That((await ProjectStore.LoadAsync(catalogPath, CancellationToken.None)).Projects.Single().IsDeleting,
                    Is.True);
        Assert.That(gateway.Calls, Does.Not.Contain(nameof(ISessionProjectDeletionGateway.CompletedAsync)));
    }

    /// <summary>
    ///     <para>受保护目录解析读取配置；落入工作区或沙箱可写根时明确拒绝。</para>
    ///     <para>
    ///         Protected-store resolution reads the configuration and refuses directories inside the
    ///         workspace or a sandbox-writable root.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ResolveProtectedStoreDirectory_AppliesConfigAndRejectsWorkspace()
    {
        using var dir        = new TestTempDir();
        var       workspace  = dir.CreateDirectory("workspace");
        var       store      = dir.CreateDirectory("store");
        var       configPath = dir.WriteFile("user.json", "{}");
        Assert.That(await SessionStoreOperations.ResolveProtectedStoreDirectoryAsync(workspace, configPath,
                        CancellationToken.None),
                    Is.Not.EqualTo(string.Empty), "未配置时按平台默认目录解析，不抛出即认为通过。");

        await UserConfigStore.SaveAsync(configPath, new UserConfig
        {
            Settings = new UserConfigSettings { SessionStoreDirectory = Path.Combine(workspace, "inside") }
        }, CancellationToken.None);
        await TestAssert.ThrowsAsync<SessionStoreException>(() =>
                                                                SessionStoreOperations
                                                                   .ResolveProtectedStoreDirectoryAsync(workspace,
                                                                        configPath,
                                                                        CancellationToken.None));

        await UserConfigStore.SaveAsync(configPath, new UserConfig
        {
            Settings = new UserConfigSettings
            {
                SessionStoreDirectory = store,
                WindowsSandbox = new WindowsSandboxSettings
                {
                    Enabled              = true,
                    // 临时根是 store 的祖先：store 落入沙箱可写根时必须被拒绝。
                    AdditionalWriteRoots = [dir.Root]
                }
            }
        }, CancellationToken.None);
        await TestAssert.ThrowsAsync<SessionStoreException>(() =>
                                                                SessionStoreOperations
                                                                   .ResolveProtectedStoreDirectoryAsync(workspace,
                                                                        configPath,
                                                                        CancellationToken.None));
    }

    private static Func<CancellationToken, Task<string>> Resolve(string directory) => _ => Task.FromResult(directory);

    private static async Task<string> SeedSessionAsync(string store, string? projectId = null)
    {
        var session = new ChatSession(Guid.NewGuid().ToString("N"), Path.GetTempPath(), Identity, null, projectId);
        using var writer = new SessionStoreWriter(store, session.Id);
        await writer.CommitAsync(session, new ConversationContext(), CancellationToken.None);
        return session.Id;
    }

    private static async Task<string> SingleSessionIdAsync(string store)
    {
        var files = Directory.EnumerateFiles(store, "*" + SessionStoreWriter.LogFileSuffix).ToList();
        Assert.That(files.Count, Is.EqualTo(1), "前置：本测试只播种一份日志。");
        return Path.GetFileName(files[0])[..^SessionStoreWriter.LogFileSuffix.Length];
    }

    /// <summary>
    ///     <para>打开测试用的原始 SQLite 连接；禁用连接池，避免测试清理时文件仍被池持有。</para>
    ///     <para>
    ///         Opens a raw SQLite connection for tests; pooling is disabled so test cleanup never fights the
    ///         pool.
    ///     </para>
    /// </summary>
    private static SqliteConnection OpenRawIndexConnection(string store)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = SessionIndexStore.PathFor(store), Mode = SqliteOpenMode.ReadWrite, Pooling = false
        };
        return new SqliteConnection(builder.ToString());
    }

    /// <summary>
    ///     <para>记录阶段调用顺序并可在解绑回调中注入异常的删除编排假接缝。</para>
    ///     <para>
    ///         A fake deletion gateway recording stage order and able to inject an exception from its unassignment
    ///         callback.
    ///     </para>
    /// </summary>
    private sealed class RecordingGateway : ISessionProjectDeletionGateway
    {
        private readonly List<string> _calls = [];

        public IReadOnlyList<string> Calls => _calls;

        public Action? OnDeletionMarked { get; init; }

        public Func<SessionProjectUnassignmentResult, Task>? UnassignmentPolicy { get; init; }

        public Task DeletionMarkedAsync(Project deleting, CancellationToken cancellationToken)
        {
            _calls.Add(nameof(DeletionMarkedAsync));
            OnDeletionMarked?.Invoke();
            return Task.CompletedTask;
        }

        public Task UnbindDraftPagesAsync(string            storeDirectory, string projectId,
                                          CancellationToken cancellationToken)
        {
            _calls.Add(nameof(UnbindDraftPagesAsync));
            return Task.CompletedTask;
        }

        public Task UnbindOpenRuntimeSessionsAsync(string            storeDirectory, string projectId,
                                                   CancellationToken cancellationToken)
        {
            _calls.Add(nameof(UnbindOpenRuntimeSessionsAsync));
            return Task.CompletedTask;
        }

        public async Task UnassignmentAppliedAsync(SessionProjectUnassignmentResult result,
                                                   CancellationToken                cancellationToken)
        {
            _calls.Add(nameof(UnassignmentAppliedAsync));
            if (UnassignmentPolicy is { } policy) await policy(result);
        }

        public Task PrepareCompletionAsync(string            storeDirectory, string projectId,
                                           CancellationToken cancellationToken)
        {
            _calls.Add(nameof(PrepareCompletionAsync));
            return Task.CompletedTask;
        }

        public Task CompletedAsync(Project deleted, CancellationToken cancellationToken)
        {
            _calls.Add(nameof(CompletedAsync));
            return Task.CompletedTask;
        }
    }
}
