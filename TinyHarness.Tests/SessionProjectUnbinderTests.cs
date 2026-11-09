using System.Text.Json;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Persistence;

namespace TinyHarness.Tests;

public class SessionProjectUnbinderTests
{
    /// <summary>
    ///     <para>健康索引避免读取无关正文，并增量解绑目标，保留历史、活动时间与归档投影。</para>
    ///     <para>A healthy index skips unrelated bodies and incrementally unassigns targets while preserving history, activity, and archive state.</para>
    /// </summary>
    [Test]
    [Platform("Win")]
    public async Task CurrentIndex_SkipsUnreadableUnrelatedBodyAndAdvancesTargetOnly()
    {
        using var dir = new TestTempDir();
        var projectId = Guid.NewGuid().ToString("N");
        var target = await SeedAsync(dir.Root, projectId);
        var other = await SeedAsync(dir.Root, Guid.NewGuid().ToString("N"));
        var index = new SessionIndexStore(dir.Root);
        await new SessionIndexReconciler(index).ReconcileAsync(CancellationToken.None);
        await index.SyncArchiveProjectionAsync(new HashSet<string> { target }, CancellationToken.None);
        var before = (await index.GetRowAsync(target, CancellationToken.None))!.Summary!;
        var prefix = await File.ReadAllBytesAsync(LogPath(dir.Root, target));

        using var held = new FileStream(LogPath(dir.Root, other), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = await SessionProjectUnbinder.UnassignAsync(dir.Root, projectId);

        Assert.That(result.IsComplete, Is.True);
        Assert.That(result.Outcomes.Select(outcome => outcome.SessionId), Is.EquivalentTo(new[] { target }));
        var row = (await index.GetRowAsync(target, CancellationToken.None))!;
        Assert.That(row.Summary!.ProjectId, Is.Null);
        Assert.That(row.Summary.ProjectAssignmentState, Is.EqualTo(SessionProjectAssignmentState.NoProject));
        Assert.That(row.Summary.CommitSequence, Is.EqualTo(before.CommitSequence + 1));
        Assert.That(row.Summary.LastActivityUtc, Is.EqualTo(before.LastActivityUtc));
        Assert.That(row.Archived, Is.True);
        var after = await File.ReadAllBytesAsync(LogPath(dir.Root, target));
        Assert.That(after.Take(prefix.Length), Is.EqualTo(prefix));

        var retry = await SessionProjectUnbinder.UnassignAsync(dir.Root, projectId);
        Assert.That(retry.IsComplete, Is.True);
        Assert.That(await File.ReadAllBytesAsync(LogPath(dir.Root, target)), Is.EqualTo(after));
    }

    /// <summary>
    ///     <para>来源变化时不能按过期的无项目摘要跳过目标。</para>
    ///     <para>A changed source must not be skipped because of an outdated unassigned summary.</para>
    /// </summary>
    [Test]
    public async Task ChangedSource_DiscoversBindingMissingFromIndex()
    {
        using var dir = new TestTempDir();
        var projectId = Guid.NewGuid().ToString("N");
        var target = await SeedAsync(dir.Root, null);
        var index = new SessionIndexStore(dir.Root);
        await new SessionIndexReconciler(index).ReconcileAsync(CancellationToken.None);
        using (var writer = await SessionStoreWriter.OpenExistingAsync(dir.Root, target, null, CancellationToken.None))
            await writer.AppendRecordsAsync([Binding(projectId)], CancellationToken.None);

        var result = await SessionProjectUnbinder.UnassignAsync(dir.Root, projectId);

        Assert.That(result.IsComplete, Is.True);
        Assert.That(result.Outcomes.Select(outcome => outcome.SessionId), Is.EquivalentTo(new[] { target }));
        Assert.That((await SessionStoreReader.ReadAsync(dir.Root, target, CancellationToken.None))!.ProjectId, Is.Null);
        Assert.That((await index.GetRowAsync(target, CancellationToken.None))!.Summary!.ProjectAssignmentState,
                    Is.EqualTo(SessionProjectAssignmentState.NoProject));
    }

    /// <summary>
    ///     <para>已有数据库但缺少目标行时仍发现并解绑新日志。</para>
    ///     <para>New target journals are discovered and unassigned even when the database has no matching row.</para>
    /// </summary>
    [Test]
    public async Task MissingRow_DiscoversNewTargetAlongsideExistingIndex()
    {
        using var dir = new TestTempDir();
        var index = new SessionIndexStore(dir.Root);
        await index.InitializeAsync(CancellationToken.None);
        var projectId = Guid.NewGuid().ToString("N");
        var target = await SeedAsync(dir.Root, projectId);

        var result = await SessionProjectUnbinder.UnassignAsync(dir.Root, projectId);

        Assert.That(result.IsComplete, Is.True);
        Assert.That((await index.GetRowAsync(target, CancellationToken.None))!.Summary!.ProjectId, Is.Null);
        Assert.That(result.Outcomes, Has.Count.EqualTo(1));
    }

    /// <summary>
    ///     <para>数据库缺失或损坏不阻止权威日志解绑，损坏库保持原样。</para>
    ///     <para>A missing or damaged database does not block authoritative unassignment, and damaged bytes stay intact.</para>
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task MissingOrDamagedIndex_FallsBackWithoutLosingUnassignment(bool damaged)
    {
        using var dir = new TestTempDir();
        var projectId = Guid.NewGuid().ToString("N");
        var target = await SeedAsync(dir.Root, projectId);
        if (damaged) await File.WriteAllTextAsync(SessionIndexStore.PathFor(dir.Root), "damaged database");

        var result = await SessionProjectUnbinder.UnassignAsync(dir.Root, projectId);

        Assert.That(result.IsComplete, Is.True);
        Assert.That((await SessionStoreReader.ReadAsync(dir.Root, target, CancellationToken.None))!
                    .ProjectAssignmentState, Is.EqualTo(SessionProjectAssignmentState.NoProject));
        if (damaged)
            Assert.That(await File.ReadAllTextAsync(SessionIndexStore.PathFor(dir.Root)), Is.EqualTo("damaged database"));
    }

    /// <summary>
    ///     <para>目标 writer 被占用时保留待处理状态，释放后可继续。</para>
    ///     <para>A held target writer remains pending and can be retried after release.</para>
    /// </summary>
    [Test]
    [Platform("Win")]
    public async Task HeldTargetWriter_RemainsPendingAndCanResume()
    {
        using var dir = new TestTempDir();
        var projectId = Guid.NewGuid().ToString("N");
        var target = await SeedAsync(dir.Root, projectId);
        await new SessionIndexReconciler(new SessionIndexStore(dir.Root)).ReconcileAsync(CancellationToken.None);
        using (var writer = await SessionStoreWriter.OpenExistingAsync(dir.Root, target, null, CancellationToken.None))
        {
            var pending = await SessionProjectUnbinder.UnassignAsync(dir.Root, projectId);
            Assert.That(pending.IsComplete, Is.False);
            Assert.That(pending.Outcomes.Single().Status, Is.EqualTo(SessionProjectUnassignmentStatus.Pending));
        }

        Assert.That((await SessionProjectUnbinder.UnassignAsync(dir.Root, projectId)).IsComplete, Is.True);
    }

    /// <summary>
    ///     <para>无法扫描的日志阻止完成删除，过期的其他项目摘要不能掩盖损坏。</para>
    ///     <para>An unreadable journal blocks completion; an outdated other-project summary cannot hide damage.</para>
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task DamagedLog_BlocksCompletionEvenWhenOldIndexSaysOtherProject(bool indexed)
    {
        using var dir = new TestTempDir();
        var other = await SeedAsync(dir.Root, Guid.NewGuid().ToString("N"));
        if (indexed)
            await new SessionIndexReconciler(new SessionIndexStore(dir.Root)).ReconcileAsync(CancellationToken.None);
        await File.AppendAllTextAsync(LogPath(dir.Root, other), "broken tail");
        var before = await File.ReadAllBytesAsync(LogPath(dir.Root, other));

        var result = await SessionProjectUnbinder.UnassignAsync(dir.Root, Guid.NewGuid().ToString("N"));

        Assert.That(result.IsComplete, Is.False);
        Assert.That(result.Outcomes.Single().Status, Is.EqualTo(SessionProjectUnassignmentStatus.Failed));
        Assert.That(await File.ReadAllBytesAsync(LogPath(dir.Root, other)), Is.EqualTo(before));
    }

    /// <summary>
    ///     <para>预先取消时返回未完成，且不追加解除归属。</para>
    ///     <para>A pre-cancelled request returns incomplete without appending an unassignment.</para>
    /// </summary>
    [Test]
    public async Task CancelledRequest_DoesNotAppendUnassignment()
    {
        using var dir = new TestTempDir();
        var projectId = Guid.NewGuid().ToString("N");
        var target = await SeedAsync(dir.Root, projectId);
        var before = await File.ReadAllBytesAsync(LogPath(dir.Root, target));
        using var source = new CancellationTokenSource();
        source.Cancel();

        var result = await SessionProjectUnbinder.UnassignAsync(dir.Root, projectId, source.Token);

        Assert.That(result.Cancelled, Is.True);
        Assert.That(result.IsComplete, Is.False);
        Assert.That(await File.ReadAllBytesAsync(LogPath(dir.Root, target)), Is.EqualTo(before));
    }

    private static string LogPath(string store, string id) => Path.Combine(store, id + SessionStoreWriter.LogFileSuffix);

    private static SessionLogRecord Binding(string projectId) =>
        new(string.Empty, "project-binding",
            JsonSerializer.SerializeToElement(new SessionLogProjectBinding(SessionLogProjectBinding.SchemaName, projectId),
                                               SessionLogJsonContext.Default.SessionLogProjectBinding));

    private static async Task<string> SeedAsync(string store, string? projectId)
    {
        var session = new ChatSession(Guid.NewGuid().ToString("N"), Path.GetTempPath(),
                                      new SessionModelIdentity("offline", "https://offline.invalid/v1", ChatApiKind.ChatCompletions),
                                      null, projectId);
        using var writer = new SessionStoreWriter(store, session.Id);
        await writer.CommitAsync(session, new ConversationContext(), CancellationToken.None);
        return session.Id;
    }
}
