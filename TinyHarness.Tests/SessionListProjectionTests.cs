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
///         验证列表投影门面：索引查询分页取全、降级摘要扫描不落库、失败条目按行报告。
///     </para>
///     <para>
///         Verifies the list-projection façade: paged index queries fetch everything, the degraded summary scan
///         never touches the database, and failed entries report per row.
///     </para>
/// </summary>
public class SessionListProjectionTests
{
    private static readonly SessionModelIdentity Identity =
        new("offline", "https://offline.invalid/v1", ChatApiKind.ChatCompletions);

    /// <summary>
    ///     <para>索引可用时查询分页取回全部健康摘要，排序为活动时间降序。</para>
    ///     <para>
    ///         With an available index, queries page through every healthy summary ordered by activity time
    ///         descending.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ListSummaries_PagesThroughEveryHealthyEntry()
    {
        using var dir        = new TestTempDir();
        var       projection = new SessionListProjection(dir.Root);
        var       ids        = new List<string>();
        foreach (var question in new[] { "paged question 1", "paged question 2", "paged question 3" })
        {
            var client = new FakeChatClient();
            client.Enqueue(FakeChatClient.Text($"answer to {question}"));
            using var host = NewHost(dir.Root, client);
            await host.SubmitTurnAsync(question, CancellationToken.None);
            ids.Add(host.Session.Id);
        }

        await projection.ReconcileAsync(SessionIndexReconcileOptions.Default, CancellationToken.None);
        Assert.That((await projection.GetAvailabilityAsync(CancellationToken.None)).Status,
                    Is.EqualTo(SessionIndexAvailabilityStatus.Available));

        var summaries = await projection.ListSummariesAsync(
                                                            new SessionSummaryQuery { Limit = 2 },
                                                            CancellationToken.None);
        Assert.That(summaries.Select(summary => summary.Id), Is.EquivalentTo(ids));
        Assert.That(summaries.Select(summary => summary.LastActivityUtc),
                    Is.Ordered.Descending);
    }

    /// <summary>
    ///     <para>索引缺失时降级摘要扫描直接呈现且不创建索引文件；损坏日志按条目失败报告。</para>
    ///     <para>
    ///         With a missing index, the degraded summary scan renders without creating an index file; damaged logs
    ///         report per entry.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ScanSummaries_RendersWithoutCreatingIndexAndReportsDamage()
    {
        using var dir        = new TestTempDir();
        var       projection = new SessionListProjection(dir.Root);
        var       client     = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("scanned answer"));
        using var host = NewHost(dir.Root, client);
        await host.SubmitTurnAsync("scanned question", CancellationToken.None);
        var damagedId   = Guid.NewGuid().ToString("N");
        var damagedPath = Path.Combine(dir.Root, damagedId + SessionStoreWriter.LogFileSuffix);
        await File.WriteAllTextAsync(damagedPath, "{broken", CancellationToken.None);

        Assert.That((await projection.GetAvailabilityAsync(CancellationToken.None)).Status,
                    Is.EqualTo(SessionIndexAvailabilityStatus.Missing));
        var entries = await projection.ScanSummariesAsync(CancellationToken.None);

        Assert.That(File.Exists(SessionIndexStore.PathFor(dir.Root)), Is.False,
                    "降级扫描不得创建索引数据库文件。");
        Assert.That(entries, Has.Count.EqualTo(2));
        var healthy = entries.Single(entry => entry.Summary is not null);
        Assert.That(healthy.Id, Is.EqualTo(host.Session.Id));
        Assert.That(healthy.Summary!.Title, Is.EqualTo("scanned question"));
        var damaged = entries.Single(entry => entry.Summary is null);
        Assert.That(damaged.Id, Is.EqualTo(damagedId));
        Assert.That(damaged.Error, Is.Not.Null);
        Assert.That(entries.Select(entry => entry.Summary?.LastActivityUtc ?? DateTimeOffset.MinValue),
                    Is.Ordered.Descending);
    }

    /// <summary>
    ///     <para>行查询暴露失败条目，健康摘要查询不返回它们。</para>
    ///     <para>Row queries expose failed entries; healthy summary queries never return them.</para>
    /// </summary>
    [Test]
    public async Task ListRows_ExposesFailedEntriesExcludedFromSummaries()
    {
        using var dir        = new TestTempDir();
        var       projection = new SessionListProjection(dir.Root);
        var       client     = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("healthy answer"));
        using var host = NewHost(dir.Root, client);
        await host.SubmitTurnAsync("healthy question", CancellationToken.None);
        var damagedId = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(Path.Combine(dir.Root, damagedId + SessionStoreWriter.LogFileSuffix),
                                     "{broken", CancellationToken.None);
        await projection.ReconcileAsync(SessionIndexReconcileOptions.Default, CancellationToken.None);

        var rows      = await projection.ListRowsAsync(CancellationToken.None);
        var summaries = await projection.ListSummariesAsync(new SessionSummaryQuery(), CancellationToken.None);
        Assert.That(rows.Single(row => row.Id == damagedId).State, Is.EqualTo(SessionIndexEntryState.Failed));
        Assert.That(summaries.Select(summary => summary.Id),
                    Is.EquivalentTo([host.Session.Id]), "失败条目不得出现在健康摘要中。");
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
