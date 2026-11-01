using System.Text;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Services.Persistence;

namespace TinyHarness.Tests;

/// <summary>
///     <para>验证 Core 会话存储置顶状态的持久化、迁移与并发更新。</para>
///     <para>Tests Core session-store pin persistence, migration, and concurrent updates.</para>
/// </summary>
public sealed class SessionStorePinStoreTests
{
    /// <summary>
    ///     <para>确认缺失的状态文件返回空状态且不创建存储目录。</para>
    ///     <para>Verifies that a missing state file returns empty state without creating its store directory.</para>
    /// </summary>
    [Test]
    public async Task Load_MissingStateReturnsEmptyWithoutCreatingStore()
    {
        using var temp  = new TestTempDir();
        var       store = Path.Combine(temp.Root, "missing", "sessions");

        var pins = await SessionStorePinStore.LoadAsync(store, CancellationToken.None);

        Assert.That(pins.PinnedProjectIds, Is.Empty);
        Assert.That(pins.PinnedSessionIds, Is.Empty);
        Assert.That(Directory.Exists(store), Is.False);
    }

    /// <summary>
    ///     <para>确认旧置顶列表只迁移一次，后续 Core 取消置顶不会被恢复。</para>
    ///     <para>Verifies one-time legacy migration and that later Core unpins stay removed.</para>
    /// </summary>
    [Test]
    public async Task LoadOrMigrate_ImportsLegacyStateOnceAndPreservesCoreUnpins()
    {
        using var temp       = new TestTempDir();
        var       projectIds = new[] { "project-a", "project-b" };
        var       sessionIds = new[] { "session-a" };

        var migrated = await SessionStorePinStore.LoadOrMigrateAsync(temp.Root, projectIds, sessionIds,
                                                                     CancellationToken.None);
        Assert.That(migrated.PinnedProjectIds, Is.EqualTo(projectIds));
        Assert.That(migrated.PinnedSessionIds, Is.EqualTo(sessionIds));
        Assert.That(File.Exists(SessionStorePinStore.FilePath(temp.Root)), Is.True);

        var afterUnpin = await SessionStorePinStore.ToggleProjectPinAsync(temp.Root, "project-a",
                                                                          CancellationToken.None);
        var reloaded = await SessionStorePinStore.LoadOrMigrateAsync(temp.Root, projectIds, sessionIds,
                                                                     CancellationToken.None);

        Assert.That(afterUnpin.PinnedProjectIds, Is.EqualTo(new[] { "project-b" }));
        Assert.That(reloaded.PinnedProjectIds, Is.EqualTo(new[] { "project-b" }));
        Assert.That(reloaded.PinnedSessionIds, Is.EqualTo(sessionIds));
    }

    /// <summary>
    ///     <para>确认旧置顶列表会合并到已存在的 Core 状态且只导入一次。</para>
    ///     <para>Verifies legacy pins merge into existing Core state and are imported only once.</para>
    /// </summary>
    [Test]
    public async Task LoadOrMigrate_MergesExistingCoreStateOnce()
    {
        using var temp = new TestTempDir();
        await SessionStorePinStore.SetProjectPinnedAsync(temp.Root, "core-project", true,
                                                         CancellationToken.None);

        var migrated = await SessionStorePinStore.LoadOrMigrateAsync(temp.Root,
                                                                     ["legacy-project", "legacy-project-2"],
                                                                     ["legacy-session"], CancellationToken.None);
        await SessionStorePinStore.SetProjectPinnedAsync(temp.Root, "legacy-project", false,
                                                         CancellationToken.None);
        var reloaded = await SessionStorePinStore.LoadOrMigrateAsync(temp.Root,
                                                                     ["legacy-project", "legacy-project-2"],
                                                                     ["legacy-session"], CancellationToken.None);

        Assert.That(migrated.PinnedProjectIds,
                    Is.EqualTo(new[] { "core-project", "legacy-project", "legacy-project-2" }));
        Assert.That(reloaded.PinnedProjectIds, Is.EqualTo(new[] { "core-project", "legacy-project-2" }));
        Assert.That(reloaded.PinnedSessionIds, Is.EqualTo(new[] { "legacy-session" }));
        Assert.That(reloaded.LegacySidebarPinsImported, Is.True);
    }

    /// <summary>
    ///     <para>确认项目与会话置顶顺序可独立往返保存。</para>
    ///     <para>Verifies independent round trips for ordered project and session pins.</para>
    /// </summary>
    [Test]
    public async Task ToggleAndMove_PersistIndependentOrderedProjectAndSessionPins()
    {
        using var temp = new TestTempDir();
        await SessionStorePinStore.ToggleProjectPinAsync(temp.Root, "project-a", CancellationToken.None);
        await SessionStorePinStore.ToggleProjectPinAsync(temp.Root, "project-b", CancellationToken.None);
        await SessionStorePinStore.ToggleSessionPinAsync(temp.Root, "session-a", CancellationToken.None);
        await SessionStorePinStore.ToggleSessionPinAsync(temp.Root, "session-b", CancellationToken.None);

        var moved = await SessionStorePinStore.MovePinnedProjectAsync(temp.Root, "project-a", "project-b",
                                                                      false, CancellationToken.None);
        var loaded = await SessionStorePinStore.LoadAsync(temp.Root, CancellationToken.None);

        Assert.That(moved.PinnedProjectIds, Is.EqualTo(new[] { "project-a", "project-b" }));
        Assert.That(loaded.PinnedProjectIds, Is.EqualTo(new[] { "project-a", "project-b" }));
        Assert.That(loaded.PinnedSessionIds, Is.EqualTo(new[] { "session-b", "session-a" }));
    }

    /// <summary>
    ///     <para>确认重复设置相同目标状态不会重复插入或改变顺序。</para>
    ///     <para>Verifies repeated desired-state writes do not duplicate pins or change their order.</para>
    /// </summary>
    [Test]
    public async Task SetPinState_RepeatedDesiredStateIsIdempotent()
    {
        using var temp = new TestTempDir();
        await SessionStorePinStore.SetProjectPinnedAsync(temp.Root, "project-a", true, CancellationToken.None);
        await SessionStorePinStore.SetProjectPinnedAsync(temp.Root, "project-b", true, CancellationToken.None);
        await SessionStorePinStore.SetProjectPinnedAsync(temp.Root, "project-a", true, CancellationToken.None);
        await SessionStorePinStore.SetSessionPinnedAsync(temp.Root, "session-a", true, CancellationToken.None);
        await SessionStorePinStore.SetSessionPinnedAsync(temp.Root, "session-a", true, CancellationToken.None);

        var loaded = await SessionStorePinStore.LoadAsync(temp.Root, CancellationToken.None);

        Assert.That(loaded.PinnedProjectIds, Is.EqualTo(new[] { "project-b", "project-a" }));
        Assert.That(loaded.PinnedSessionIds, Is.EqualTo(new[] { "session-a" }));
    }

    /// <summary>
    ///     <para>确认并发置顶更新不会相互覆盖。</para>
    ///     <para>Verifies concurrent pin updates do not overwrite one another.</para>
    /// </summary>
    [Test]
    public async Task ToggleSessionPin_ConcurrentUpdatesDoNotOverwriteEachOther()
    {
        using var temp       = new TestTempDir();
        var       sessionIds = Enumerable.Range(0, 12).Select(index => $"session-{index}").ToArray();

        await Task.WhenAll(sessionIds.Select(id =>
                                                 SessionStorePinStore.ToggleSessionPinAsync(temp.Root, id,
                                                          CancellationToken.None)));
        var loaded = await SessionStorePinStore.LoadAsync(temp.Root, CancellationToken.None);

        Assert.That(loaded.PinnedSessionIds.Order(StringComparer.Ordinal),
                    Is.EqualTo(sessionIds.Order(StringComparer.Ordinal)));
    }

    /// <summary>
    ///     <para>确认损坏状态会被报告且原文件保留。</para>
    ///     <para>Verifies damaged state is reported while the original file is preserved.</para>
    /// </summary>
    [Test]
    public async Task Toggle_DamagedStateIsReportedAndNotOverwritten()
    {
        using var temp = new TestTempDir();
        var       path = SessionStorePinStore.FilePath(temp.Root);
        var       bad  = Encoding.UTF8.GetBytes("{ not json");
        await File.WriteAllBytesAsync(path, bad, CancellationToken.None);

        await TestAssert.ThrowsAsync<SessionStorePinException>(() =>
                                                                   SessionStorePinStore.ToggleSessionPinAsync(temp.Root,
                                                                            "session-a", CancellationToken.None));

        Assert.That(File.ReadAllBytes(path), Is.EqualTo(bad));
    }

    /// <summary>
    ///     <para>确认未知 schema 会被拒绝且不替换原文件。</para>
    ///     <para>Verifies an unsupported schema is rejected without replacing the original file.</para>
    /// </summary>
    [Test]
    public async Task Load_UnsupportedSchemaIsReportedWithoutReplacingTheFile()
    {
        using var temp = new TestTempDir();
        var       path = SessionStorePinStore.FilePath(temp.Root);
        var bad = Encoding.UTF8.GetBytes(
                                         "{\"schema\":\"tinyharness.session-store-pins/2\",\"pinnedProjectIds\":[],\"pinnedSessionIds\":[]}");
        await File.WriteAllBytesAsync(path, bad, CancellationToken.None);

        await TestAssert.ThrowsAsync<SessionStorePinException>(() =>
                                                                   SessionStorePinStore.LoadAsync(temp.Root,
                                                                            CancellationToken.None));

        Assert.That(File.ReadAllBytes(path), Is.EqualTo(bad));
    }
}
