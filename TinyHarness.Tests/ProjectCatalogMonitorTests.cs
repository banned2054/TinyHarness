using System.Text;
using TinyHarness.Core.Models.Project;
using TinyHarness.Core.Services.Persistence;

namespace TinyHarness.Tests;

public class ProjectCatalogMonitorTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 5, 8, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task Check_WithoutConfirmedBaselineLoadsMissingCatalog()
    {
        using var store   = new TestTempDir();
        var       monitor = new ProjectCatalogMonitor();

        var result = await monitor.CheckAsync(store.Root, CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(ProjectCatalogCheckStatus.Loaded), "基线未确认时首次检查必须加载内容。");
        var catalog = TestAssert.NotNull(result.Catalog);
        Assert.That(catalog.Revision, Is.EqualTo(0));
        Assert.That(catalog.Projects, Is.Empty);
        Assert.That(result.FileState.Exists, Is.False, "缺失文件按 Loaded 加空目录册与不存在形态呈现。");
        Assert.That(result.FileState.Length, Is.EqualTo(0));
        Assert.That(result.FileState.LastWriteTimeUtc, Is.EqualTo(DateTime.MinValue));
    }

    [Test]
    public async Task Check_AfterConfirmReturnsUnchangedForSameFile()
    {
        using var store = new TestTempDir();
        var       path  = ProjectStore.FilePath(store.Root);
        await ProjectStore.GetOrCreateAsync(path, store.Root, FixedNow, CancellationToken.None);
        var monitor = new ProjectCatalogMonitor();

        var first = await monitor.CheckAsync(store.Root, CancellationToken.None);
        monitor.Confirm(first.FileState);
        var second = await monitor.CheckAsync(store.Root, CancellationToken.None);

        Assert.That(second.Status, Is.EqualTo(ProjectCatalogCheckStatus.Unchanged), "确认基线后同一未变文件不得重复加载。");
        Assert.That(second.Catalog, Is.Null, "Unchanged 不携带目录册内容。");
        Assert.That(second.FileState, Is.EqualTo(first.FileState));
    }

    [Test]
    public async Task Check_KeepsReloadingUntilConfirmed()
    {
        // 未 Confirm 前重复检查不得吞掉变化：每次都重新加载。
        using var store = new TestTempDir();
        var       path  = ProjectStore.FilePath(store.Root);
        await ProjectStore.GetOrCreateAsync(path, store.Root, FixedNow, CancellationToken.None);
        var monitor = new ProjectCatalogMonitor();

        var first  = await monitor.CheckAsync(store.Root, CancellationToken.None);
        var second = await monitor.CheckAsync(store.Root, CancellationToken.None);

        Assert.That(first.Status, Is.EqualTo(ProjectCatalogCheckStatus.Loaded));
        Assert.That(second.Status, Is.EqualTo(ProjectCatalogCheckStatus.Loaded), "未确认基线时重复检查必须持续加载。");
    }

    [Test]
    public async Task Check_LoadsChangedContentAfterRename()
    {
        using var store   = new TestTempDir();
        var       path    = ProjectStore.FilePath(store.Root);
        var       monitor = new ProjectCatalogMonitor();
        var       created = await ProjectStore.GetOrCreateAsync(path, store.Root, FixedNow, CancellationToken.None);

        var first = await monitor.CheckAsync(store.Root, CancellationToken.None);
        monitor.Confirm(first.FileState);

        await Task.Delay(20);
        await ProjectStore.RenameAsync(path, created.Id, "改名后的项目", created.Revision, FixedNow.AddHours(1),
                                       CancellationToken.None);
        var second = await monitor.CheckAsync(store.Root, CancellationToken.None);

        Assert.That(second.Status, Is.EqualTo(ProjectCatalogCheckStatus.Loaded), "目录册内容变化后必须重新加载。");
        var catalog = TestAssert.NotNull(second.Catalog);
        Assert.That(catalog.Revision, Is.EqualTo(2));
        Assert.That(catalog.Projects[0].Name, Is.EqualTo("改名后的项目"));
    }

    [Test]
    public async Task Check_DetectsSameLengthRewriteThroughWriteTime()
    {
        // 字节数相同、内容不同的重写只能靠修改时间线索发现；修订号 1→2 保持长度不变。
        using var store   = new TestTempDir();
        var       path    = ProjectStore.FilePath(store.Root);
        var       monitor = new ProjectCatalogMonitor();
        await ProjectStore.GetOrCreateAsync(path, store.Root, FixedNow, CancellationToken.None);

        var first = await monitor.CheckAsync(store.Root, CancellationToken.None);
        Assert.That(first.Status, Is.EqualTo(ProjectCatalogCheckStatus.Loaded));
        monitor.Confirm(first.FileState);

        var original  = await File.ReadAllTextAsync(path, CancellationToken.None);
        var rewritten = original.Replace("\"revision\": 1", "\"revision\": 2");
        Assert.That(rewritten, Is.Not.EqualTo(original));
        Assert.That(Encoding.UTF8.GetByteCount(rewritten), Is.EqualTo(Encoding.UTF8.GetByteCount(original)),
                    "前提：重写后的字节数与原文件相同，差异只能来自修改时间。");
        await Task.Delay(20);
        await File.WriteAllTextAsync(path, rewritten, CancellationToken.None);

        var second = await monitor.CheckAsync(store.Root, CancellationToken.None);

        Assert.That(second.Status, Is.EqualTo(ProjectCatalogCheckStatus.Loaded), "长度相同但修改时间推进的重写必须重新加载。");
        Assert.That(TestAssert.NotNull(second.Catalog).Revision, Is.EqualTo(2));
    }

    [Test]
    public async Task Check_FailedThenRecoversAfterRepair()
    {
        using var store   = new TestTempDir();
        var       path    = ProjectStore.FilePath(store.Root);
        var       monitor = new ProjectCatalogMonitor();
        await File.WriteAllTextAsync(path, "{ not json", CancellationToken.None);

        var damaged = await monitor.CheckAsync(store.Root, CancellationToken.None);

        Assert.That(damaged.Status, Is.EqualTo(ProjectCatalogCheckStatus.Failed), "损坏目录册按 Failed 报告，不抛异常。");
        Assert.That(damaged.Catalog, Is.Null);

        // Failed 不改基线：移除损坏文件并重建合法目录册后，下一轮检查自然恢复。
        File.Delete(path);
        await ProjectStore.GetOrCreateAsync(path, store.Root, FixedNow, CancellationToken.None);
        var repaired = await monitor.CheckAsync(store.Root, CancellationToken.None);

        Assert.That(repaired.Status, Is.EqualTo(ProjectCatalogCheckStatus.Loaded), "修复后下一轮检查必须恢复为 Loaded。");
        Assert.That(TestAssert.NotNull(repaired.Catalog).Projects.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task Reset_ForcesReloadOfConfirmedFile()
    {
        using var store = new TestTempDir();
        var       path  = ProjectStore.FilePath(store.Root);
        await ProjectStore.GetOrCreateAsync(path, store.Root, FixedNow, CancellationToken.None);
        var monitor = new ProjectCatalogMonitor();

        var first = await monitor.CheckAsync(store.Root, CancellationToken.None);
        monitor.Confirm(first.FileState);
        var second = await monitor.CheckAsync(store.Root, CancellationToken.None);
        Assert.That(second.Status, Is.EqualTo(ProjectCatalogCheckStatus.Unchanged));

        monitor.Reset();
        var third = await monitor.CheckAsync(store.Root, CancellationToken.None);

        Assert.That(third.Status, Is.EqualTo(ProjectCatalogCheckStatus.Loaded), "Reset 作废基线后必须重新加载。");
    }

    [Test]
    public async Task Check_TracksMissingExistingMissingRoundtrip()
    {
        using var store   = new TestTempDir();
        var       path    = ProjectStore.FilePath(store.Root);
        var       monitor = new ProjectCatalogMonitor();

        var missing = await monitor.CheckAsync(store.Root, CancellationToken.None);
        Assert.That(missing.Status, Is.EqualTo(ProjectCatalogCheckStatus.Loaded));
        Assert.That(missing.FileState.Exists, Is.False);
        monitor.Confirm(missing.FileState);
        Assert.That((await monitor.CheckAsync(store.Root, CancellationToken.None)).Status,
                    Is.EqualTo(ProjectCatalogCheckStatus.Unchanged), "确认基线后持续缺失保持 Unchanged。");

        // 缺失到存在的翻转必须重新加载。
        await ProjectStore.GetOrCreateAsync(path, store.Root, FixedNow, CancellationToken.None);
        var existing = await monitor.CheckAsync(store.Root, CancellationToken.None);
        Assert.That(existing.Status, Is.EqualTo(ProjectCatalogCheckStatus.Loaded), "缺失到存在的翻转必须重新加载。");
        Assert.That(existing.FileState.Exists, Is.True);
        monitor.Confirm(existing.FileState);
        Assert.That((await monitor.CheckAsync(store.Root, CancellationToken.None)).Status,
                    Is.EqualTo(ProjectCatalogCheckStatus.Unchanged));

        // 存在到缺失的翻转必须重新加载并按空目录册呈现。
        File.Delete(path);
        var gone = await monitor.CheckAsync(store.Root, CancellationToken.None);
        Assert.That(gone.Status, Is.EqualTo(ProjectCatalogCheckStatus.Loaded), "存在到缺失的翻转必须重新加载。");
        Assert.That(gone.FileState.Exists, Is.False);
        Assert.That(TestAssert.NotNull(gone.Catalog).Projects, Is.Empty);
        monitor.Confirm(gone.FileState);
        Assert.That((await monitor.CheckAsync(store.Root, CancellationToken.None)).Status,
                    Is.EqualTo(ProjectCatalogCheckStatus.Unchanged));
    }

    [Test]
    public async Task Check_PropagatesCancellation()
    {
        using var store   = new TestTempDir();
        var       monitor = new ProjectCatalogMonitor();
        using var cts     = new CancellationTokenSource();
        await cts.CancelAsync();

        // 已取消令牌必须以 OperationCanceledException 原样传播，不吞成 Failed。
        await Assert.CatchAsync<OperationCanceledException>(() => monitor.CheckAsync(store.Root, cts.Token));
    }

    [Test]
    public async Task ConcurrentCheckAndConfirmDoesNotThrow()
    {
        using var store = new TestTempDir();
        var       path  = ProjectStore.FilePath(store.Root);
        await ProjectStore.GetOrCreateAsync(path, store.Root, FixedNow, CancellationToken.None);
        var monitor = new ProjectCatalogMonitor();

        // 简单并发冒烟：并发 Check 与 Confirm 不得抛出意外异常或死锁。
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < 25; i++)
            {
                var result = await monitor.CheckAsync(store.Root, CancellationToken.None);
                if (result.Status == ProjectCatalogCheckStatus.Loaded) monitor.Confirm(result.FileState);
            }
        })));

        var final = await monitor.CheckAsync(store.Root, CancellationToken.None);
        Assert.That(final.Status,
                    Is.EqualTo(ProjectCatalogCheckStatus.Loaded)
                      .Or.EqualTo(ProjectCatalogCheckStatus.Unchanged),
                    "并发冒烟后的收尾检查必须仍返回有效结果。");
    }

    [Test]
    public async Task Check_ReportsFailedWhileFileExclusivelyLocked()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var store = new TestTempDir();
        var       path  = ProjectStore.FilePath(store.Root);
        await ProjectStore.GetOrCreateAsync(path, store.Root, FixedNow, CancellationToken.None);
        var monitor = new ProjectCatalogMonitor();

        // FileShare.None 独占句柄模拟外部程序占用：共享冲突必须归入 Failed 而非异常逃逸。
        await using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var result = await monitor.CheckAsync(store.Root, CancellationToken.None)
                                  .WaitAsync(TimeSpan.FromSeconds(15));

        Assert.That(result.Status, Is.EqualTo(ProjectCatalogCheckStatus.Failed), "共享冲突必须按 Failed 处理。");
        Assert.That(result.Catalog, Is.Null);

        await held.DisposeAsync();
        var recovered = await monitor.CheckAsync(store.Root, CancellationToken.None);
        Assert.That(recovered.Status, Is.EqualTo(ProjectCatalogCheckStatus.Loaded), "占用释放后检查必须恢复正常加载。");
    }

    [Test]
    public void Project_ContentEquals_TrueForIdenticalContent()
    {
        using var store = new TestTempDir();
        var       left  = MakeProject(store.Root);
        var       right = MakeProject(store.Root);

        Assert.That(left.ContentEquals(right), Is.True, "全部持久化语义字段一致时内容相等。");
        Assert.That(left.ContentEquals(left), Is.True, "引用相等短路为 true。");
    }

    [Test]
    public void Project_ContentEquals_FalseForAnyFieldDifference()
    {
        using var store    = new TestTempDir();
        var       altRoot  = Path.Combine(store.Root, "alt");
        var       baseline = MakeProject(store.Root);
        var cases = new (string description, Project other)[]
        {
            ("id", MakeProject(store.Root, new string('b', 32))),
            ("name", MakeProject(store.Root, name : "Two")),
            ("revision", MakeProject(store.Root, revision : 2)),
            ("isDeleting", MakeProject(store.Root, isDeleting : true)),
            ("createdUtc", MakeProject(store.Root, createdUtc : FixedNow.AddHours(1))),
            ("updatedUtc", MakeProject(store.Root, updatedUtc : FixedNow.AddHours(2))),
            ("primaryRoot", MakeProject(store.Root, primaryRoot : altRoot)),
            ("rootPath", MakeProject(store.Root, roots : [altRoot], primaryRoot : store.Root))
        };

        Assert.Multiple(() =>
        {
            foreach (var (description, other) in cases)
                Assert.That(baseline.ContentEquals(other), Is.False,
                            $"字段 {description} 差异必须判定为内容不等。");
        });
    }

    [Test]
    public void Project_ContentEquals_MatchesRootPathCaseOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var store      = new TestTempDir();
        var       baseline   = MakeProject(store.Root);
        var       uppercased = MakeProject(store.Root.ToUpperInvariant());

        Assert.That(baseline.ContentEquals(uppercased), Is.True, "Windows 上目录路径大小写差异按平台规则视为等价。");
    }

    [Test]
    public void Project_ContentEquals_RootOrderIsSemantic()
    {
        using var store    = new TestTempDir();
        var       first    = Path.Combine(store.Root, "first");
        var       second   = Path.Combine(store.Root, "second");
        var       baseline = MakeProject(store.Root, roots : [first, second], primaryRoot : first);
        var       swapped  = MakeProject(store.Root, roots : [second, first], primaryRoot : first);

        Assert.That(baseline.ContentEquals(MakeProject(store.Root, roots : [first, second], primaryRoot : first)),
                    Is.True, "相同顺序的目录列表内容相等。");
        Assert.That(baseline.ContentEquals(swapped), Is.False, "目录列表按位置比较，顺序差异必须判定为不等。");
    }

    [Test]
    public void Project_ContentEquals_NullIsNeverEqual()
    {
        using var store   = new TestTempDir();
        var       project = MakeProject(store.Root);

        Assert.That(project.ContentEquals(null), Is.False, "null 不与任何项目内容相等。");
    }

    [Test]
    public void Catalog_ContentEquals_TrueForSameContentIncludingEmpty()
    {
        using var store = new TestTempDir();
        var       left  = MakeCatalog(1, MakeProject(store.Root));
        var       right = MakeCatalog(1, MakeProject(store.Root));

        Assert.That(left.ContentEquals(right), Is.True, "修订号与逐位置项目内容一致时目录册相等。");
        Assert.That(ProjectCatalog.Empty().ContentEquals(ProjectCatalog.Empty()), Is.True, "空目录册彼此相等。");
    }

    [Test]
    public void Catalog_ContentEquals_FalseForRevisionCountContentOrNullDifferences()
    {
        using var store    = new TestTempDir();
        var       baseline = MakeCatalog(1, MakeProject(store.Root));

        Assert.That(baseline.ContentEquals(null), Is.False, "null 不与任何目录册内容相等。");
        Assert.That(baseline.ContentEquals(MakeCatalog(2, MakeProject(store.Root))), Is.False,
                    "修订号差异必须判定为内容不等。");
        Assert.That(baseline.ContentEquals(MakeCatalog(1)), Is.False, "项目数量差异必须判定为内容不等。");
        Assert.That(baseline.ContentEquals(MakeCatalog(1, MakeProject(store.Root, name : "Two"))), Is.False,
                    "同数量不同项目内容必须判定为内容不等。");
        Assert.That(baseline.ContentEquals(baseline), Is.True, "引用相等短路为 true。");
    }

    [Test]
    public void Catalog_ContentEquals_IgnoresSchema()
    {
        using var store    = new TestTempDir();
        var       baseline = MakeCatalog(1, MakeProject(store.Root));
        var otherSchema = new ProjectCatalog
        {
            Schema   = "tinyharness.projects/0",
            Revision = 1,
            Projects = [MakeProject(store.Root)]
        };

        Assert.That(baseline.ContentEquals(otherSchema), Is.True, "schema 不参与内容比较，只比较修订号与项目内容。");
    }

    private static Project MakeProject(string          root,
                                       string?         id          = null,
                                       string          name        = "One",
                                       long            revision    = 1,
                                       bool            isDeleting  = false,
                                       DateTimeOffset? createdUtc  = null,
                                       DateTimeOffset? updatedUtc  = null,
                                       string[]?       roots       = null,
                                       string?         primaryRoot = null)
    {
        var paths = roots ?? [root];
        return new Project
        {
            Id          = id ?? new string('a', 32),
            Name        = name,
            Roots       = [.. paths.Select(path => new ProjectRoot(path))],
            PrimaryRoot = primaryRoot ?? paths[0],
            Revision    = revision,
            IsDeleting  = isDeleting,
            CreatedUtc  = createdUtc ?? FixedNow,
            UpdatedUtc  = updatedUtc ?? FixedNow
        };
    }

    private static ProjectCatalog MakeCatalog(long revision, params Project[] projects) => new()
    {
        Schema   = ProjectCatalog.CurrentSchema,
        Revision = revision,
        Projects = projects
    };
}
