using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Project;
using TinyHarness.Core.Services.Persistence;

namespace TinyHarness.Tests;

public class ProjectStoreTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 26, 8, 0, 0, TimeSpan.Zero);

    private const string FixedTimestamp = "2026-10-26T08:00:00.0000000+00:00";

    [Test]
    public async Task Load_MissingFileReturnsEmptyCatalog()
    {
        using var store = new TestTempDir();
        var       path  = ProjectStore.FilePath(store.Root);

        var catalog = await ProjectStore.LoadAsync(path, CancellationToken.None);

        Assert.That(catalog.Schema, Is.EqualTo(ProjectCatalog.CurrentSchema));
        Assert.That(catalog.Revision, Is.EqualTo(0));
        Assert.That(catalog.Projects, Is.Empty);
        // 首次缺失读取不创建目录册，也不创建保存锁或任何文件。
        Assert.That(File.Exists(path), Is.False);
        Assert.That(File.Exists(path + ProjectStore.SaveLockSuffix), Is.False);
    }

    [Test]
    public async Task GetOrCreate_CreatesAndPersistsProject()
    {
        // 嵌套的不存在存储目录由保存路径自动创建。
        using var store  = new TestTempDir();
        var       nested = Path.Combine(store.Root, "nested", "sessions");
        var       path   = ProjectStore.FilePath(nested);

        var created = await ProjectStore.GetOrCreateAsync(path, store.Root, FixedNow, CancellationToken.None);

        Assert.That(created.Id.Length, Is.EqualTo(32));
        Assert.That(created.Id.All(char.IsAsciiHexDigit) && !created.Id.Any(char.IsUpper), Is.True);
        Assert.That(created.Name, Is.EqualTo(Path.GetFileName(store.Root)));
        Assert.That(created.Revision, Is.EqualTo(1));
        Assert.That(created.CreatedUtc, Is.EqualTo(FixedNow));
        Assert.That(created.UpdatedUtc, Is.EqualTo(FixedNow));
        Assert.That(created.Roots.Count, Is.EqualTo(1));
        Assert.That(created.Roots[0].Path, Is.EqualTo(store.Root));
        Assert.That(created.PrimaryRoot, Is.EqualTo(store.Root));

        // 重启一致：重新加载得到相同身份、名称、目录与时间。
        var catalog = await ProjectStore.LoadAsync(path, CancellationToken.None);
        Assert.That(catalog.Revision, Is.EqualTo(1));
        var loaded = TestAssert.Single(catalog.Projects);
        Assert.That(loaded.Id, Is.EqualTo(created.Id));
        Assert.That(loaded.Name, Is.EqualTo(created.Name));
        Assert.That(loaded.Roots[0].Path, Is.EqualTo(created.Roots[0].Path));
        Assert.That(loaded.PrimaryRoot, Is.EqualTo(created.PrimaryRoot));
        Assert.That(loaded.CreatedUtc, Is.EqualTo(FixedNow));
        Assert.That(loaded.UpdatedUtc, Is.EqualTo(FixedNow));
    }

    [Test]
    public async Task GetOrCreate_ReusesProjectForEquivalentLexicalPaths()
    {
        using var store = new TestTempDir();
        var       path  = ProjectStore.FilePath(store.Root);

        var original = await ProjectStore.GetOrCreateAsync(path, store.Root, FixedNow, CancellationToken.None);
        var trailing = await ProjectStore.GetOrCreateAsync(path, store.Root + Path.DirectorySeparatorChar,
                                                          FixedNow, CancellationToken.None);
        var altSeparator = await ProjectStore.GetOrCreateAsync(path, store.Root.Replace('\\', '/') + "/",
                                                              FixedNow, CancellationToken.None);

        Assert.That(trailing.Id, Is.EqualTo(original.Id));
        Assert.That(altSeparator.Id, Is.EqualTo(original.Id));
        var catalog = await ProjectStore.LoadAsync(path, CancellationToken.None);
        Assert.That(catalog.Projects.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task GetOrCreate_MatchesCaseInsensitivelyOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var store = new TestTempDir();
        var       path  = ProjectStore.FilePath(store.Root);

        var original = await ProjectStore.GetOrCreateAsync(path, store.Root, FixedNow, CancellationToken.None);
        var uppercased = await ProjectStore.GetOrCreateAsync(path, store.Root.ToUpperInvariant(), FixedNow,
                                                            CancellationToken.None);

        Assert.That(uppercased.Id, Is.EqualTo(original.Id));
        var catalog = await ProjectStore.LoadAsync(path, CancellationToken.None);
        Assert.That(catalog.Projects.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task GetOrCreate_DistinguishesParentChildAndSameNameDirectories()
    {
        using var store = new TestTempDir();
        var       path  = ProjectStore.FilePath(store.Root);

        var parent = await ProjectStore.GetOrCreateAsync(path, Path.Combine(store.Root, "repo"), FixedNow,
                                                        CancellationToken.None);
        var child = await ProjectStore.GetOrCreateAsync(path, Path.Combine(store.Root, "repo", "sub"), FixedNow,
                                                       CancellationToken.None);
        var sameNameOtherParent =
            await ProjectStore.GetOrCreateAsync(path, Path.Combine(store.Root, "other", "sub"), FixedNow,
                                               CancellationToken.None);

        Assert.That(child.Id, Is.Not.EqualTo(parent.Id));
        Assert.That(sameNameOtherParent.Id, Is.Not.EqualTo(parent.Id));
        Assert.That(sameNameOtherParent.Id, Is.Not.EqualTo(child.Id));
        Assert.That(sameNameOtherParent.Name, Is.EqualTo(child.Name));
        var catalog = await ProjectStore.LoadAsync(path, CancellationToken.None);
        Assert.That(catalog.Projects.Count, Is.EqualTo(3));
    }

    [Test]
    public async Task GetOrCreate_ConcurrentCallsShareSingleProject()
    {
        using var store = new TestTempDir();
        var       path  = ProjectStore.FilePath(store.Root);

        var projects = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            ProjectStore.GetOrCreateAsync(path, store.Root, FixedNow, CancellationToken.None)));

        Assert.That(projects.Select(project => project.Id).Distinct().Count(), Is.EqualTo(1));
        var catalog = await ProjectStore.LoadAsync(path, CancellationToken.None);
        Assert.That(catalog.Projects.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task GetOrCreate_NamesVolumeRootByPath()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var store = new TestTempDir();
        var       path  = ProjectStore.FilePath(store.Root);

        var project = await ProjectStore.GetOrCreateAsync(path, @"C:\", FixedNow, CancellationToken.None);

        Assert.That(project.Name, Is.EqualTo(@"C:\"));
        Assert.That(project.Roots[0].Path, Is.EqualTo(@"C:\"));
    }

    [Test]
    public async Task GetOrCreate_RejectsBlankRoot()
    {
        using var store = new TestTempDir();
        var       path  = ProjectStore.FilePath(store.Root);

        await TestAssert.ThrowsAsync<ArgumentException>(() =>
            ProjectStore.GetOrCreateAsync(path, "   ", FixedNow, CancellationToken.None));
    }

    [Test]
    public async Task Rename_UpdatesNameRevisionAndKeepsIdentity()
    {
        using var store   = new TestTempDir();
        var       path    = ProjectStore.FilePath(store.Root);
        var       created = await ProjectStore.GetOrCreateAsync(path, store.Root, FixedNow, CancellationToken.None);

        var later = FixedNow.AddHours(2);
        var renamed = await ProjectStore.RenameAsync(path, created.Id, "  TinyHarness  ", created.Revision, later,
                                                     CancellationToken.None);

        Assert.That(renamed.Id, Is.EqualTo(created.Id));
        Assert.That(renamed.Name, Is.EqualTo("TinyHarness"));
        Assert.That(renamed.Revision, Is.EqualTo(2));
        Assert.That(renamed.CreatedUtc, Is.EqualTo(FixedNow));
        Assert.That(renamed.UpdatedUtc, Is.EqualTo(later));
        Assert.That(renamed.Roots[0].Path, Is.EqualTo(created.Roots[0].Path));
        Assert.That(renamed.PrimaryRoot, Is.EqualTo(created.PrimaryRoot));

        var catalog = await ProjectStore.LoadAsync(path, CancellationToken.None);
        Assert.That(catalog.Revision, Is.EqualTo(2));
        var loaded = TestAssert.Single(catalog.Projects);
        Assert.That(loaded.Name, Is.EqualTo("TinyHarness"));
        Assert.That(loaded.Revision, Is.EqualTo(2));
    }

    [Test]
    public async Task Rename_StaleRevisionFailsWithoutOverwrite()
    {
        using var store   = new TestTempDir();
        var       path    = ProjectStore.FilePath(store.Root);
        var       created = await ProjectStore.GetOrCreateAsync(path, store.Root, FixedNow, CancellationToken.None);
        var renamed = await ProjectStore.RenameAsync(path, created.Id, "Second", created.Revision, FixedNow,
                                                     CancellationToken.None);

        var before = File.ReadAllBytes(path);
        var stale = await TestAssert.ThrowsAsync<ProjectStoreException>(() =>
            ProjectStore.RenameAsync(path, created.Id, "Third", created.Revision, FixedNow, CancellationToken.None));
        Assert.That(stale.Message, Does.Contain("reload it and retry"));
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));

        // 重载后以最新修订号重试成功。
        var retried = await ProjectStore.RenameAsync(path, created.Id, "Third", renamed.Revision, FixedNow,
                                                     CancellationToken.None);
        Assert.That(retried.Name, Is.EqualTo("Third"));
        Assert.That(retried.Revision, Is.EqualTo(3));
    }

    [Test]
    public async Task Rename_MissingProjectFails()
    {
        using var store = new TestTempDir();
        var       path  = ProjectStore.FilePath(store.Root);
        await ProjectStore.GetOrCreateAsync(path, store.Root, FixedNow, CancellationToken.None);

        await TestAssert.ThrowsAsync<ProjectStoreException>(() =>
            ProjectStore.RenameAsync(path, new string('e', 32), "Any", 1, FixedNow, CancellationToken.None));
    }

    [Test]
    public async Task Rename_RejectsInvalidNames()
    {
        using var store   = new TestTempDir();
        var       path    = ProjectStore.FilePath(store.Root);
        var       created = await ProjectStore.GetOrCreateAsync(path, store.Root, FixedNow, CancellationToken.None);

        await TestAssert.ThrowsAsync<ArgumentException>(() =>
            ProjectStore.RenameAsync(path, created.Id, "   ", created.Revision, FixedNow, CancellationToken.None));
        await TestAssert.ThrowsAsync<ProjectStoreException>(() =>
            ProjectStore.RenameAsync(path, created.Id, new string('n', 201), created.Revision, FixedNow,
                                     CancellationToken.None));
        await TestAssert.ThrowsAsync<ProjectStoreException>(() =>
            ProjectStore.RenameAsync(path, created.Id, "bad\u0001name", created.Revision, FixedNow,
                                     CancellationToken.None));
    }

    [Test]
    public async Task Load_DamagedCatalogIsReportedAndNotOverwritten()
    {
        using var store = new TestTempDir();
        var       path  = ProjectStore.FilePath(store.Root);

        // 空文件按损坏处理，不能当作空目录册。
        await File.WriteAllTextAsync(path, string.Empty, CancellationToken.None);
        await TestAssert.ThrowsAsync<ProjectStoreException>(() => ProjectStore.LoadAsync(path, CancellationToken.None));

        var damaged = Encoding.UTF8.GetBytes("{ not json");
        await File.WriteAllBytesAsync(path, damaged, CancellationToken.None);
        await TestAssert.ThrowsAsync<ProjectStoreException>(() => ProjectStore.LoadAsync(path, CancellationToken.None));
        await TestAssert.ThrowsAsync<ProjectStoreException>(() =>
            ProjectStore.GetOrCreateAsync(path, store.Root, FixedNow, CancellationToken.None));
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(damaged));
    }

    [Test]
    public async Task Load_RejectsInvalidUtf8RootBytesWithoutOverwrite()
    {
        using var store = new TestTempDir();
        var       path  = ProjectStore.FilePath(store.Root);
        await ProjectStore.GetOrCreateAsync(path, store.Root, FixedNow, CancellationToken.None);

        // 同一项目 root.path 与 primaryRoot 值的末段字节改为 0xFF：JSON 结构保持完整、路径仍是绝对形态，
        // 损坏只能由读取边界的严格 UTF-8 解码发现，而非其他校验偶然拦下。
        var original  = await File.ReadAllBytesAsync(path, CancellationToken.None);
        var run       = Encoding.UTF8.GetBytes(JsonEscape(store.Root));
        var tailBytes = Encoding.UTF8.GetBytes(JsonEscape(Path.GetFileName(store.Root)));
        var corrupted = PoisonTailOfEachByteRun(original, run, tailBytes.Length, 0xFF, expectedRuns: 2);
        await File.WriteAllBytesAsync(path, corrupted, CancellationToken.None);

        var loadEx = await TestAssert.ThrowsAsync<ProjectStoreException>(() =>
            ProjectStore.LoadAsync(path, CancellationToken.None));
        Assert.That(loadEx.Message, Does.Contain(path));
        Assert.That(loadEx.Message, Does.Contain("UTF-8"));
        Assert.That(loadEx.InnerException, Is.InstanceOf<DecoderFallbackException>());
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(corrupted));

        // 原目录 GetOrCreate 同样拒绝：不得按空目录册追加项目后覆盖损坏字节。
        var createEx = await TestAssert.ThrowsAsync<ProjectStoreException>(() =>
            ProjectStore.GetOrCreateAsync(path, store.Root, FixedNow, CancellationToken.None));
        Assert.That(createEx.InnerException, Is.InstanceOf<DecoderFallbackException>());
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(corrupted));
    }

    [Test]
    public async Task Load_AcceptsUtf8BomCatalog()
    {
        using var store   = new TestTempDir();
        var       path    = ProjectStore.FilePath(store.Root);
        var       created = await ProjectStore.GetOrCreateAsync(path, store.Root, FixedNow, CancellationToken.None);

        // 前置 UTF-8 BOM 维持既有读取行为：剥离后目录册照常加载。
        var body = await File.ReadAllBytesAsync(path, CancellationToken.None);
        await File.WriteAllBytesAsync(path, [0xEF, 0xBB, 0xBF, .. body], CancellationToken.None);

        var catalog = await ProjectStore.LoadAsync(path, CancellationToken.None);
        var loaded  = TestAssert.Single(catalog.Projects);
        Assert.That(loaded.Id, Is.EqualTo(created.Id));
        Assert.That(loaded.PrimaryRoot, Is.EqualTo(store.Root));
    }

    [Test]
    public async Task Load_RejectsNonUtf8BomCatalogWithoutOverwrite()
    {
        using var store   = new TestTempDir();
        var       path    = ProjectStore.FilePath(store.Root);
        var       project = ProjectJson(new string('a', 32), "One", store.Root);

        // UTF-16LE BOM 加持的合法 JSON：格式契约只允许 UTF-8，BOM 不得切换解码器绕过严格校验。
        var utf16 = Encoding.Unicode.GetPreamble()
                            .Concat(Encoding.Unicode.GetBytes(CatalogJson(ProjectCatalog.CurrentSchema, 1, project)))
                            .ToArray();
        await File.WriteAllBytesAsync(path, utf16, CancellationToken.None);

        var ex = await TestAssert.ThrowsAsync<ProjectStoreException>(() =>
            ProjectStore.LoadAsync(path, CancellationToken.None));
        Assert.That(ex.Message, Does.Contain("UTF-8"));
        await TestAssert.ThrowsAsync<ProjectStoreException>(() =>
            ProjectStore.GetOrCreateAsync(path, store.Root, FixedNow, CancellationToken.None));
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(utf16));
    }

    [Test]
    public async Task Load_UnsupportedSchemaIsReportedAndNotOverwritten()
    {
        using var store = new TestTempDir();
        var       path  = ProjectStore.FilePath(store.Root);
        await ProjectStore.GetOrCreateAsync(path, store.Root, FixedNow, CancellationToken.None);

        var original = await File.ReadAllTextAsync(path, CancellationToken.None);
        var modified = original.Replace(ProjectCatalog.CurrentSchema, "tinyharness.projects/2");
        Assert.That(modified, Is.Not.EqualTo(original));
        await File.WriteAllTextAsync(path, modified, CancellationToken.None);

        await TestAssert.ThrowsAsync<ProjectStoreException>(() => ProjectStore.LoadAsync(path, CancellationToken.None));
        await TestAssert.ThrowsAsync<ProjectStoreException>(() =>
            ProjectStore.GetOrCreateAsync(path, store.Root, FixedNow, CancellationToken.None));
        Assert.That(await File.ReadAllTextAsync(path, CancellationToken.None), Is.EqualTo(modified));
    }

    [Test]
    public async Task Load_RejectsOversizedCatalog()
    {
        using var store = new TestTempDir();
        var       path  = ProjectStore.FilePath(store.Root);
        await File.WriteAllTextAsync(path, new string('x', ProjectStore.MaxCatalogBytes + 1), CancellationToken.None);

        await TestAssert.ThrowsAsync<ProjectStoreException>(() => ProjectStore.LoadAsync(path, CancellationToken.None));
    }

    [Test]
    public async Task Load_RejectsDuplicateRootsAcrossProjects()
    {
        using var store = new TestTempDir();
        var       path  = ProjectStore.FilePath(store.Root);
        await File.WriteAllTextAsync(path,
                                     CatalogJson(ProjectCatalog.CurrentSchema, 2,
                                                 ProjectJson(new string('a', 32), "One", store.Root) + "," +
                                                 ProjectJson(new string('b', 32), "Two", store.Root)),
                                     CancellationToken.None);

        await TestAssert.ThrowsAsync<ProjectStoreException>(() => ProjectStore.LoadAsync(path, CancellationToken.None));
    }

    [Test]
    public async Task Load_RejectsUnknownField()
    {
        using var store = new TestTempDir();
        var       path  = ProjectStore.FilePath(store.Root);
        await File.WriteAllTextAsync(path,
                                     CatalogJson(ProjectCatalog.CurrentSchema, 1,
                                                 ProjectJson(new string('a', 32), "One", store.Root,
                                                             extra : ", \"nickname\": \"x\"")),
                                     CancellationToken.None);

        await TestAssert.ThrowsAsync<ProjectStoreException>(() => ProjectStore.LoadAsync(path, CancellationToken.None));
    }

    [Test]
    public async Task Load_RejectsMissingRequiredField()
    {
        using var store = new TestTempDir();
        var       path  = ProjectStore.FilePath(store.Root);
        await File.WriteAllTextAsync(path,
                                     CatalogJson(ProjectCatalog.CurrentSchema, 1,
                                                 ProjectJson(null, "One", store.Root)),
                                     CancellationToken.None);

        await TestAssert.ThrowsAsync<ProjectStoreException>(() => ProjectStore.LoadAsync(path, CancellationToken.None));
    }

    [Test]
    public async Task Load_RejectsNonNormalizedRoot()
    {
        using var store = new TestTempDir();
        var       path  = ProjectStore.FilePath(store.Root);
        await File.WriteAllTextAsync(path,
                                     CatalogJson(ProjectCatalog.CurrentSchema, 1,
                                                 ProjectJson(new string('a', 32), "One",
                                                             store.Root + Path.DirectorySeparatorChar)),
                                     CancellationToken.None);

        await TestAssert.ThrowsAsync<ProjectStoreException>(() => ProjectStore.LoadAsync(path, CancellationToken.None));
    }

    [Test]
    public async Task Save_CreationOverBudgetFailsWithoutOverwrite()
    {
        using var store = new TestTempDir();
        var       path  = ProjectStore.FilePath(store.Root);
        var       json  = HeavyCatalogJson(200, store.Root);
        Assert.That(Encoding.UTF8.GetByteCount(json), Is.LessThan(ProjectStore.MaxCatalogBytes));
        await File.WriteAllTextAsync(path, json, CancellationToken.None);

        // 构造目录册本身可读且在预算内。
        var readable = await ProjectStore.LoadAsync(path, CancellationToken.None);
        Assert.That(readable.Projects.Count, Is.EqualTo(200));

        var before = File.ReadAllBytes(path);
        await TestAssert.ThrowsAsync<ProjectStoreException>(() =>
            ProjectStore.GetOrCreateAsync(path, store.Root, FixedNow, CancellationToken.None));
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));

        // 失败后原目录册仍可读取。
        var after = await ProjectStore.LoadAsync(path, CancellationToken.None);
        Assert.That(after.Projects.Count, Is.EqualTo(200));
    }

    [Test]
    public async Task Save_RenameOverBudgetFailsWithoutOverwrite()
    {
        using var store = new TestTempDir();
        var       path  = ProjectStore.FilePath(store.Root);
        var       json  = HeavyCatalogJson(200, store.Root);
        Assert.That(Encoding.UTF8.GetByteCount(json), Is.LessThan(ProjectStore.MaxCatalogBytes));
        await File.WriteAllTextAsync(path, json, CancellationToken.None);
        var readable = await ProjectStore.LoadAsync(path, CancellationToken.None);
        var first    = readable.Projects[0];

        var before = File.ReadAllBytes(path);
        await TestAssert.ThrowsAsync<ProjectStoreException>(() =>
            ProjectStore.RenameAsync(path, first.Id, "改名后超预算的中文新名称", first.Revision, FixedNow, CancellationToken.None));
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));

        var after = await ProjectStore.LoadAsync(path, CancellationToken.None);
        Assert.That(after.Projects.Count, Is.EqualTo(200));
    }

    [Test]
    public async Task Load_RejectsDuplicateTopLevelProjects()
    {
        using var store = new TestTempDir();
        var       path  = ProjectStore.FilePath(store.Root);
        var       one   = ProjectJson(new string('a', 32), "One", store.Root);
        await File.WriteAllTextAsync(path,
                                     $"{{ \"schema\": \"{ProjectCatalog.CurrentSchema}\", \"revision\": 1, " +
                                     $"\"projects\": [{one}], \"projects\": [] }}",
                                     CancellationToken.None);

        await TestAssert.ThrowsAsync<ProjectStoreException>(() => ProjectStore.LoadAsync(path, CancellationToken.None));
    }

    [Test]
    public async Task Load_RejectsDuplicateProjectFields()
    {
        using var store   = new TestTempDir();
        var       project = ProjectJson(new string('a', 32), "One", store.Root);
        var cases = new (string description, string json)[]
        {
            ("duplicate id",
             project.Replace("{ \"id\":", $"{{ \"id\": \"{new string('b', 32)}\", \"id\":")),
            ("duplicate roots",
             project.Replace("\"roots\": [", "\"roots\": [], \"roots\": [")),
            ("duplicate name",
             project.Replace("\"name\":", "\"name\": \"Two\", \"name\":"))
        };

        var index = 0;
        foreach (var (description, json) in cases)
        {
            var entryPath = Path.Combine(store.Root, $"catalog-{index++}.json");
            await File.WriteAllTextAsync(entryPath,
                                         CatalogJson(ProjectCatalog.CurrentSchema, 1, json),
                                         CancellationToken.None);
            var ex = await TestAssert.ThrowsAsync<ProjectStoreException>(() =>
                ProjectStore.LoadAsync(entryPath, CancellationToken.None));
            Assert.That(ex.Message, Does.Contain(entryPath), description);
        }
    }

    [Test]
    public async Task Load_RejectsDuplicateRootPath()
    {
        using var store       = new TestTempDir();
        var       path        = ProjectStore.FilePath(store.Root);
        var       escapedRoot = store.Root.Replace("\\", "\\\\");
        // 两个 path 值相同且与 primaryRoot 一致：即使重复属性被静默合并，目录册也整体合法，
        // 只有重复字段拒绝本身能拦下它，测试不靠其他校验偶然通过。
        var project = $"{{ \"id\": \"{new string('a', 32)}\", \"name\": \"One\", "                       +
                      $"\"roots\": [ {{ \"path\": \"{escapedRoot}\", \"path\": \"{escapedRoot}\" }} ], " +
                      $"\"primaryRoot\": \"{escapedRoot}\", \"revision\": 1, "                           +
                      $"\"createdUtc\": \"{FixedTimestamp}\", \"updatedUtc\": \"{FixedTimestamp}\" }}";
        await File.WriteAllTextAsync(path,
                                     CatalogJson(ProjectCatalog.CurrentSchema, 1, project),
                                     CancellationToken.None);

        var ex = await TestAssert.ThrowsAsync<ProjectStoreException>(() =>
            ProjectStore.LoadAsync(path, CancellationToken.None));
        Assert.That(ex.Message, Does.Contain(path));
        // 拒绝必须给出明确的重复字段诊断：点名 duplicate 与重复的 path 字段，来源是 JSON 层的重复属性异常。
        Assert.That(ex.Message, Does.Contain("duplicate").IgnoreCase);
        Assert.That(ex.Message, Does.Contain("'path'"));
        Assert.That(ex.InnerException, Is.InstanceOf<JsonException>());
    }

    [Test]
    public async Task Write_FailsOnDuplicateFieldCatalogWithoutOverwrite()
    {
        using var store = new TestTempDir();
        var       path  = ProjectStore.FilePath(store.Root);
        var       one   = ProjectJson(new string('a', 32), "One", store.Root);
        await File.WriteAllTextAsync(path,
                                     $"{{ \"schema\": \"{ProjectCatalog.CurrentSchema}\", \"revision\": 1, " +
                                     $"\"projects\": [{one}], \"projects\": [] }}",
                                     CancellationToken.None);
        var before = File.ReadAllBytes(path);

        await TestAssert.ThrowsAsync<ProjectStoreException>(() =>
            ProjectStore.GetOrCreateAsync(path, store.Root, FixedNow, CancellationToken.None));
        await TestAssert.ThrowsAsync<ProjectStoreException>(() =>
            ProjectStore.RenameAsync(path, new string('a', 32), "Any", 1, FixedNow, CancellationToken.None));
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
    }

    [Test]
    public async Task Save_ConcurrentReadsAndRenamesStayConsistent()
    {
        using var store   = new TestTempDir();
        var       path    = ProjectStore.FilePath(store.Root);
        var       created = await ProjectStore.GetOrCreateAsync(path, store.Root, FixedNow, CancellationToken.None);

        using var finished   = new CancellationTokenSource();
        var       readErrors = new ConcurrentQueue<string>();
        var       readCount  = 0;
        var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
        {
            while (!finished.IsCancellationRequested)
            {
                try
                {
                    var catalog = await ProjectStore.LoadAsync(path, finished.Token);
                    // 每次读取都必须是完整合法快照，而不是新旧混合内容。
                    if (catalog.Schema != ProjectCatalog.CurrentSchema || catalog.Projects.Count < 1)
                        readErrors.Enqueue($"rev={catalog.Revision} count={catalog.Projects.Count} schema={catalog.Schema}");
                    Interlocked.Increment(ref readCount);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    readErrors.Enqueue(ex.Message);
                }
            }
        })).ToArray();

        var revision = created.Revision;
        try
        {
            for (var i = 0; i < 30; i++)
            {
                var renamed = await ProjectStore.RenameAsync(path, created.Id, $"名称{i}", revision, FixedNow,
                                                             CancellationToken.None);
                revision = renamed.Revision;
            }
        }
        finally
        {
            await finished.CancelAsync();
        }

        await Task.WhenAll(readers);
        Assert.That(readErrors, Is.Empty);
        Assert.That(readCount, Is.GreaterThan(0));
        Assert.That(revision, Is.EqualTo(created.Revision + 30));
    }

    [Test]
    public async Task Save_LockWaitHonorsCancellation()
    {
        using var store   = new TestTempDir();
        var       path    = ProjectStore.FilePath(store.Root);
        var       created = await ProjectStore.GetOrCreateAsync(path, store.Root, FixedNow, CancellationToken.None);

        // 模拟外部写者占住保存锁。
        await using var held = new FileStream(path + ProjectStore.SaveLockSuffix, FileMode.OpenOrCreate,
                                              FileAccess.ReadWrite, FileShare.None);
        using var cts = new CancellationTokenSource(300);
        // 取消以 OperationCanceledException 呈现；CatchAsync 按基类匹配，不依赖等待路径抛出的精确异常类型。
        await Assert.CatchAsync<OperationCanceledException>(() =>
            ProjectStore.RenameAsync(path, created.Id, "等待期间取消", created.Revision, FixedNow, cts.Token));

        // 锁释放后同一操作正常完成。
        await held.DisposeAsync();
        var renamed = await ProjectStore.RenameAsync(path, created.Id, "锁释放后改名", created.Revision, FixedNow,
                                                     CancellationToken.None);
        Assert.That(renamed.Name, Is.EqualTo("锁释放后改名"));
    }

    [Test]
    public async Task Save_ReplaceSharingViolationHonorsCancellation()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var store   = new TestTempDir();
        var       path    = ProjectStore.FilePath(store.Root);
        var       created = await ProjectStore.GetOrCreateAsync(path, store.Root, FixedNow, CancellationToken.None);
        var       before  = await File.ReadAllBytesAsync(path, CancellationToken.None);

        // 用不共享 Delete 的读句柄占用目录册：整体替换需要 Delete 访问，File.Replace 被共享冲突阻止。
        await using var blocked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var       cts     = new CancellationTokenSource(300);

        // 共享冲突重试在延迟期被取消；CatchAsync 按基类匹配，不依赖精确异常类型与触发时序。
        await Assert.CatchAsync<OperationCanceledException>(() =>
            ProjectStore.RenameAsync(path, created.Id, "替换期间取消", created.Revision, FixedNow, cts.Token)
                .WaitAsync(TimeSpan.FromSeconds(10)));

        // 原字节保持不变，被取消的保存没有遗留临时文件。
        Assert.That(await File.ReadAllBytesAsync(path, CancellationToken.None), Is.EqualTo(before));
        Assert.That(Directory.GetFiles(store.Root, "*.tmp-*"), Is.Empty);

        // 占用句柄释放后同一操作正常完成。
        await blocked.DisposeAsync();
        var renamed = await ProjectStore.RenameAsync(path, created.Id, "占用释放后改名", created.Revision, FixedNow,
                                                     CancellationToken.None);
        Assert.That(renamed.Name, Is.EqualTo("占用释放后改名"));
    }

    [Test]
    public async Task Save_OpenSharedDeleteHandleSurvivesRename()
    {
        using var store   = new TestTempDir();
        var       path    = ProjectStore.FilePath(store.Root);
        var       created = await ProjectStore.GetOrCreateAsync(path, store.Root, FixedNow, CancellationToken.None);
        var       before  = await File.ReadAllBytesAsync(path, CancellationToken.None);

        // FileShare.Read | FileShare.Delete 明确允许他人整体替换文件；保存期间保持句柄打开，
        // 以验证旧快照在替换后仍可读取。
        await using var held = new FileStream(path, FileMode.Open, FileAccess.Read,
                                              FileShare.Read | FileShare.Delete);

        var renamed = await ProjectStore.RenameAsync(path, created.Id, "替换期间的旧快照句柄", created.Revision,
                                                     FixedNow, CancellationToken.None)
                                        .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(renamed.Revision, Is.EqualTo(2));

        // 保存完成后，旧句柄继续读到替换前的完整字节。
        held.Seek(0, SeekOrigin.Begin);
        var oldSnapshot = new byte[before.Length];
        var offset      = 0;
        while (offset < oldSnapshot.Length)
        {
            var read = await held.ReadAsync(oldSnapshot.AsMemory(offset), CancellationToken.None);
            if (read == 0) break;
            offset += read;
        }

        Assert.That(offset, Is.EqualTo(before.Length));
        Assert.That(oldSnapshot, Is.EqualTo(before));

        // 替换后的目录册按新内容读取。
        var catalog = await ProjectStore.LoadAsync(path, CancellationToken.None);
        var loaded  = TestAssert.Single(catalog.Projects);
        Assert.That(catalog.Revision, Is.EqualTo(2));
        Assert.That(loaded.Name, Is.EqualTo("替换期间的旧快照句柄"));
    }

    [Test]
    public async Task Save_ReadOnlyTargetFailsWithoutOverwrite()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var store   = new TestTempDir();
        var       path    = ProjectStore.FilePath(store.Root);
        var       created = await ProjectStore.GetOrCreateAsync(path, store.Root, FixedNow, CancellationToken.None);
        var       before  = await File.ReadAllBytesAsync(path, CancellationToken.None);

        // 只读目标是立即失败的替换错误（非共享冲突）：不重试、不覆盖、不遗留临时文件。
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var rename = ProjectStore.RenameAsync(path, created.Id, "只读目标", created.Revision, FixedNow,
                                                  CancellationToken.None);
            await TestAssert.ThrowsAsync<UnauthorizedAccessException>(() => rename.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.That(await File.ReadAllBytesAsync(path, CancellationToken.None), Is.EqualTo(before));
            Assert.That(Directory.GetFiles(store.Root, "*.tmp-*"), Is.Empty);
        }
        finally
        {
            // 恢复属性，保证 TestTempDir 清理不受只读位影响。
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Test]
    public async Task Load_MissingCatalogWaitsForSaveLockHolderInsteadOfReturningEmpty()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var store = new TestTempDir();
        var       path  = ProjectStore.FilePath(store.Root);

        // 协议内保存者先创建保存锁再改目录册：锁被独占持有时，缺失的目录册读取必须等待，不得按空处理。
        await using var writer = new FileStream(path + ProjectStore.SaveLockSuffix, FileMode.OpenOrCreate,
                                                FileAccess.ReadWrite, FileShare.None);
        using var cts = new CancellationTokenSource(300);

        await Assert.CatchAsync<OperationCanceledException>(() =>
            ProjectStore.LoadAsync(path, cts.Token).WaitAsync(TimeSpan.FromSeconds(10)));

        // 写者释放后复查：目录册确实不存在，才按空目录册返回。
        await writer.DisposeAsync();
        var catalog = await ProjectStore.LoadAsync(path, CancellationToken.None);
        Assert.That(catalog.Schema, Is.EqualTo(ProjectCatalog.CurrentSchema));
        Assert.That(catalog.Projects, Is.Empty);
    }

    [Test]
    public async Task Save_AllowsLeftoverLockFile()
    {
        using var store = new TestTempDir();
        var       path  = ProjectStore.FilePath(store.Root);
        await File.WriteAllTextAsync(path + ProjectStore.SaveLockSuffix, "stale", CancellationToken.None);

        var project = await ProjectStore.GetOrCreateAsync(path, store.Root, FixedNow, CancellationToken.None);
        Assert.That(project.Id.Length, Is.EqualTo(32));
        var catalog = await ProjectStore.LoadAsync(path, CancellationToken.None);
        Assert.That(catalog.Projects.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task Save_RejectsDirectoryOccupyingLockFile()
    {
        using var store    = new TestTempDir();
        var       path     = ProjectStore.FilePath(store.Root);
        var       lockPath = path + ProjectStore.SaveLockSuffix;
        Directory.CreateDirectory(lockPath);

        await TestAssert.ThrowsAsync<ProjectStoreException>(() =>
            ProjectStore.GetOrCreateAsync(path, store.Root, FixedNow, CancellationToken.None));
        Assert.That(Directory.Exists(lockPath), Is.True);
    }

    [Test]
    public async Task Save_RejectsLockSymlinkAndKeepsTarget()
    {
        using var store    = new TestTempDir();
        var       path     = ProjectStore.FilePath(store.Root);
        var       lockPath = path + ProjectStore.SaveLockSuffix;
        var       target   = Path.Combine(store.Root, "lock-target.txt");
        await File.WriteAllTextAsync(target, "sentinel", CancellationToken.None);

        try
        {
            File.CreateSymbolicLink(lockPath, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException)
        {
            Assert.Ignore("当前环境缺少创建符号链接的权限（需要管理员或开发者模式），无法验证锁文件链接拒绝行为。");
        }

        await TestAssert.ThrowsAsync<ProjectStoreException>(() =>
            ProjectStore.GetOrCreateAsync(path, store.Root, FixedNow, CancellationToken.None));
        Assert.That(File.ReadAllText(target), Is.EqualTo("sentinel"));

        // 悬空链接同样被拒绝，且不触碰链接本身。
        File.Delete(lockPath);
        File.CreateSymbolicLink(lockPath, Path.Combine(store.Root, "missing-target"));
        await TestAssert.ThrowsAsync<ProjectStoreException>(() =>
            ProjectStore.GetOrCreateAsync(path, store.Root, FixedNow, CancellationToken.None));
        Assert.That((File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0, Is.True);
    }

    [Test]
    public async Task Load_RejectsExplicitNullFields()
    {
        using var store       = new TestTempDir();
        var       escapedRoot = store.Root.Replace("\\", "\\\\");
        var       schema      = ProjectCatalog.CurrentSchema;
        var       normalRoots = $"[ {{ \"path\": \"{escapedRoot}\" }} ]";
        var cases = new (string description, string json)[]
        {
            ("catalog projects null",
             $"{{ \"schema\": \"{schema}\", \"revision\": 1, \"projects\": null }}"),
            ("catalog projects null element",
             $"{{ \"schema\": \"{schema}\", \"revision\": 1, \"projects\": [ null ] }}"),
            ("project id null",
             CatalogJson(schema, 1, ProjectObjectJson("null", "\"One\"", normalRoots, $"\"{escapedRoot}\""))),
            ("project name null",
             CatalogJson(schema, 1, ProjectObjectJson($"\"{new string('a', 32)}\"", "null", normalRoots,
                                                      $"\"{escapedRoot}\""))),
            ("project roots null",
             CatalogJson(schema, 1, ProjectObjectJson($"\"{new string('a', 32)}\"", "\"One\"", "null",
                                                      $"\"{escapedRoot}\""))),
            ("project roots null element",
             CatalogJson(schema, 1, ProjectObjectJson($"\"{new string('a', 32)}\"", "\"One\"", "[ null ]",
                                                      $"\"{escapedRoot}\""))),
            ("project root path null",
             CatalogJson(schema, 1, ProjectObjectJson($"\"{new string('a', 32)}\"", "\"One\"",
                                                      "[ { \"path\": null } ]", $"\"{escapedRoot}\"")))
        };

        var index = 0;
        foreach (var (description, json) in cases)
        {
            var entryPath = Path.Combine(store.Root, $"catalog-{index++}.json");
            await File.WriteAllTextAsync(entryPath, json, CancellationToken.None);
            var ex = await TestAssert.ThrowsAsync<ProjectStoreException>(() =>
                ProjectStore.LoadAsync(entryPath, CancellationToken.None));
            Assert.That(ex.Message, Does.Contain(entryPath), description);
        }

        // 显式 null 目录册之后写操作不得覆盖损坏文件。
        var catalogPath = ProjectStore.FilePath(store.Root);
        await File.WriteAllTextAsync(catalogPath, cases[0].Item2, CancellationToken.None);
        var before = File.ReadAllBytes(catalogPath);
        await TestAssert.ThrowsAsync<ProjectStoreException>(() =>
            ProjectStore.GetOrCreateAsync(catalogPath, store.Root, FixedNow, CancellationToken.None));
        Assert.That(File.ReadAllBytes(catalogPath), Is.EqualTo(before));
    }

    [Test]
    public async Task Load_RejectsDirectoryOccupyingCatalogPath()
    {
        using var store = new TestTempDir();
        var       path  = ProjectStore.FilePath(store.Root);
        Directory.CreateDirectory(path);
        var marker = Path.Combine(path, "marker.txt");
        await File.WriteAllTextAsync(marker, "keep", CancellationToken.None);

        await TestAssert.ThrowsAsync<ProjectStoreException>(() => ProjectStore.LoadAsync(path, CancellationToken.None));
        await TestAssert.ThrowsAsync<ProjectStoreException>(() =>
            ProjectStore.GetOrCreateAsync(path, store.Root, FixedNow, CancellationToken.None));
        Assert.That(Directory.Exists(path), Is.True);
        Assert.That(File.ReadAllText(marker), Is.EqualTo("keep"));
    }

    [Test]
    public async Task Load_RejectsDanglingCatalogLinkWhenEnvironmentAllows()
    {
        using var store = new TestTempDir();
        var       path  = ProjectStore.FilePath(store.Root);
        try
        {
            File.CreateSymbolicLink(path, Path.Combine(store.Root, "missing-target"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException)
        {
            Assert.Ignore("当前环境缺少创建符号链接的权限（需要管理员或开发者模式），无法验证悬空链接拒绝行为。");
        }

        await TestAssert.ThrowsAsync<ProjectStoreException>(() => ProjectStore.LoadAsync(path, CancellationToken.None));
    }

    private static string CatalogJson(string schema, long revision, string projects) =>
        $"{{ \"schema\": \"{schema}\", \"revision\": {revision}, \"projects\": [{projects}] }}";

    private static string ProjectJson(string? id, string name, string root, long revision = 1, string extra = "")
    {
        var escapedRoot = root.Replace("\\", "\\\\");
        var idField     = id is null ? "" : $"\"id\": \"{id}\", ";
        return $"{{ {idField}\"name\": \"{name}\", \"roots\": [ {{ \"path\": \"{escapedRoot}\" }} ], " +
               $"\"primaryRoot\": \"{escapedRoot}\", \"revision\": {revision}, "                       +
               $"\"createdUtc\": \"{FixedTimestamp}\", "                                               +
               $"\"updatedUtc\": \"{FixedTimestamp}\"{extra} }}";
    }

    private static string ProjectObjectJson(string idJson, string nameJson, string rootsJson, string primaryRootJson) =>
        $"{{ \"id\": {idJson}, \"name\": {nameJson}, \"roots\": {rootsJson}, " +
        $"\"primaryRoot\": {primaryRootJson}, \"revision\": 1, "               +
        $"\"createdUtc\": \"{FixedTimestamp}\", \"updatedUtc\": \"{FixedTimestamp}\" }}";

    /// <summary>
    ///     <para>
    ///         生成接近字节预算的合法目录册：root 基于传入的平台原生绝对路径拼接大量中文并做
    ///         JSON 反斜杠转义，非 ASCII 保持原样写入，文件整体仍在预算内；任何一次重新保存
    ///         （.NET 默认序列化把非 ASCII 改写为 \uXXXX 转义）都会越过 1 MiB 预算。
    ///     </para>
    ///     <para>
    ///         Builds a legal catalog close to the byte budget: roots extend the passed platform-native
    ///         absolute path with heavy CJK content and JSON backslash escaping, keeping non-ASCII bytes
    ///         literal so the file stays within budget; any resave (the .NET default serializer rewrites
    ///         non-ASCII as \uXXXX escapes) crosses the 1 MiB budget.
    ///     </para>
    /// </summary>
    private static string HeavyCatalogJson(int projectCount, string baseRoot)
    {
        var builder = new StringBuilder();
        builder.Append("{ \"schema\": \"").Append(ProjectCatalog.CurrentSchema)
               .Append("\", \"revision\": 1, \"projects\": [");
        for (var i = 0; i < projectCount; i++)
        {
            if (i > 0) builder.Append(',');
            var id   = $"{i:x8}{new string('a', 24)}";
            var root = JsonEscape(baseRoot + Path.DirectorySeparatorChar + "目录" + i + new string('节', 700));
            builder.Append(ProjectObjectJson($"\"{id}\"", $"\"项目{i}\"",
                                             $"[ {{ \"path\": \"{root}\" }} ]", $"\"{root}\""));
        }

        builder.Append("] }");
        return builder.ToString();
    }

    /// <summary>
    ///     <para>按 JSON 字符串字面量转义路径中的反斜杠与引号，非 ASCII 字符保持原样以维持预算样本形状。</para>
    ///     <para>
    ///         Escapes backslashes and quotes for a JSON string literal; non-ASCII characters stay literal
    ///         to keep the budget sample's shape.
    ///     </para>
    /// </summary>
    private static string JsonEscape(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>
    ///     <para>
    ///         把 source 中每段与 run 完全相同的字节的末 tailLength 字节替换为 poison，校验命中次数后返回新数组。
    ///     </para>
    ///     <para>
    ///         Replaces the last tailLength bytes of every byte run in source equal to run with poison,
    ///         checking the hit count and returning a new array.
    ///     </para>
    /// </summary>
    private static byte[] PoisonTailOfEachByteRun(
        byte[] source, byte[] run, int tailLength, byte poison, int expectedRuns)
    {
        var result = (byte[])source.Clone();
        var hits   = 0;
        for (var i = 0; i <= result.Length - run.Length; i++)
        {
            var matched = true;
            for (var j = 0; j < run.Length; j++)
                if (result[i + j] != run[j])
                {
                    matched = false;
                    break;
                }

            if (!matched) continue;

            for (var j = run.Length - tailLength; j < run.Length; j++) result[i + j] = poison;
            hits++;
            i += run.Length - 1;
        }

        Assert.That(hits, Is.EqualTo(expectedRuns));
        return result;
    }
}
