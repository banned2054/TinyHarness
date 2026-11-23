using System.Text.Json;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         helper 拒读簿记共享并集实现（<see cref="SandboxDenyReadBookkeeping" />）的定向断言：
///         语义集+有效条目合并、无效条目过滤并计数、可确认缺失=空集（全新 home 无 .sandbox 目录、
///         目录存在但文件缺失两种形态）、瞬态读取重试后成功（IO 与权限类）、持续读取失败
///         fail-closed（错误含阶段与状态文件路径，权限类兼作不可读绝不归为空集的回归：注入
///         持续失败与状态文件路径被目录占据的真实读取差分）、JSON 损坏立即 fail-closed 不重试
///         （注入计数器断言尝试恰为 1 次）。两个 refresh 点的接线由 WindowsSandboxBackendTests
///         与 AuthorizedReadCoordinatorTests 覆盖。
///     </para>
///     <para>
///         Targeted assertions for the shared deny-read bookkeeping union
///         (<see cref="SandboxDenyReadBookkeeping" />): semantic set plus valid entries merged,
///         invalid entries filtered with counts, a confirmed absence contributes an empty set
///         (both a fresh home without the .sandbox directory and a missing file inside an existing
///         one), transient read failures retry to success (I/O and permission classes), a
///         persistent read failure fails closed with the stage and state-file path in the error
///         (the permission-class case doubles as the regression that an unreadable bookkeeping is
///         never treated as an empty set: the injected persistent failure and the real-read
///         differential with the state-file path occupied by a directory), and corrupt JSON fails
///         closed immediately without retrying (an injected counter asserts exactly one read
///         attempt). The wiring of the two refresh points is covered by WindowsSandboxBackendTests
///         and AuthorizedReadCoordinatorTests.
///     </para>
/// </summary>
public class SandboxDenyReadBookkeepingTests
{
    private static string DenyBookkeepingJson(params (string Sid, string[] Paths)[] principals)
    {
        var principalsMap = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var (sid, paths) in principals) principalsMap[sid] = paths;

        // fork PersistentDenyReadAclState 同源形态：{"principals":{"<SID>":["C:\\path", ...]}}。
        return JsonSerializer.Serialize(new { principals = principalsMap });
    }

    [Test]
    public void Merge_SemanticSetPlusPairableBookkeepingEntries_Merged()
    {
        using var root  = new TestTempDir();
        var       home  = root.CreateDirectory("sandbox-home");
        var       hit   = Path.Combine(home, "legacy-scan-hit");
        var       hit2  = Path.Combine(home, "deep", "another-hit");
        var semantic = new[] { Path.Combine(home, ".ssh") };
        var readRoots = new[] { home };

        var (denyPaths, entries, merged) = SandboxDenyReadBookkeeping.Merge(
            "per-command refresh", semantic, readRoots, home,
            _ => DenyBookkeepingJson(("S-1-5-21-9001", [hit, hit2])));

        Assert.Multiple(() =>
        {
            Assert.That(entries, Is.EqualTo(2));
            Assert.That(merged, Is.EqualTo(2));
            Assert.That(denyPaths, Does.Contain(semantic[0]));
            Assert.That(denyPaths, Does.Contain(hit));
            Assert.That(denyPaths, Does.Contain(hit2));
        });
    }

    [Test]
    public void Merge_UnpairableEntries_FilteredWithCounts()
    {
        using var root     = new TestTempDir();
        var       home     = root.CreateDirectory("sandbox-home");
        using var outside  = new TestTempDir();
        var       unpaired = Path.Combine(outside.Root, "unrelated-hit");
        var semantic  = new[] { Path.Combine(home, ".ssh") };
        var readRoots = new[] { home };

        // 等于读根本身与脱离覆盖/兄弟两条目均被丢弃；有效兄弟条目并入（计数含被过滤条目）。
        var (denyPaths, entries, merged) = SandboxDenyReadBookkeeping.Merge(
            "per-command refresh", semantic, readRoots, home,
            _ => DenyBookkeepingJson(("S-1-5-21-9001", [home, unpaired, Path.Combine(root.Root, "sibling-hit")])));

        Assert.Multiple(() =>
        {
            Assert.That(entries, Is.EqualTo(3));
            Assert.That(merged, Is.EqualTo(1));
            Assert.That(denyPaths, Does.Contain(semantic[0]));
            Assert.That(denyPaths, Does.Contain(Path.Combine(root.Root, "sibling-hit")));
            Assert.That(denyPaths, Does.Not.Contain(home), "an entry equal to a read root is dropped");
            Assert.That(denyPaths, Does.Not.Contain(unpaired), "an entry outside coverage and not a sibling is dropped");
        });
    }

    [Test]
    public void Merge_FreshHomeWithoutSandboxDirectory_ContributesEmptySet()
    {
        using var root = new TestTempDir();
        var       home = root.CreateDirectory("sandbox-home");
        var semantic = new[] { Path.Combine(home, ".ssh") };

        // 真实读取：home 存在但 .sandbox 子目录不存在（全新 home 冷启动形态）→ 目录确认缺失
        // =空集贡献，不是错误；不可读（访问拒绝等）不会落入这条路径。
        var (denyPaths, entries, merged) = SandboxDenyReadBookkeeping.Merge("per-command refresh",
                                                                           semantic, [home], home);

        Assert.Multiple(() =>
        {
            Assert.That(entries, Is.EqualTo(0));
            Assert.That(merged, Is.EqualTo(0));
            Assert.That(denyPaths, Is.EqualTo(semantic));
        });
    }

    [Test]
    public void Merge_MissingStateFile_ContributesEmptySet()
    {
        using var root = new TestTempDir();
        var       home = root.CreateDirectory("sandbox-home");
        Directory.CreateDirectory(Path.Combine(home, ".sandbox"));
        var semantic = new[] { Path.Combine(home, ".ssh") };

        // 真实读取：.sandbox 目录存在但状态文件缺失=空集贡献，不是错误。
        var (denyPaths, entries, merged) = SandboxDenyReadBookkeeping.Merge("per-command refresh",
                                                                           semantic, [home], home);

        Assert.Multiple(() =>
        {
            Assert.That(entries, Is.EqualTo(0));
            Assert.That(merged, Is.EqualTo(0));
            Assert.That(denyPaths, Is.EqualTo(semantic));
        });
    }

    [Test]
    public void Merge_TransientReadFailuresThenSuccess_Merged()
    {
        using var root = new TestTempDir();
        var       home = root.CreateDirectory("sandbox-home");
        var       hit  = Path.Combine(home, "legacy-scan-hit");
        var semantic = new[] { Path.Combine(home, ".ssh") };
        var attempts = 0;

        // sharing violation 类瞬态：前两次 IO 失败按短间隔重试，第三次成功（有界重试契约）。
        var (denyPaths, entries, merged) = SandboxDenyReadBookkeeping.Merge(
            "per-command refresh", semantic, [home], home,
            _ => ++attempts < 3
                     ? throw new IOException("simulated sharing violation on the bookkeeping file")
                     : DenyBookkeepingJson(("S-1-5-21-9001", [hit])));

        Assert.Multiple(() =>
        {
            Assert.That(attempts, Is.EqualTo(3));
            Assert.That(entries, Is.EqualTo(1));
            Assert.That(merged, Is.EqualTo(1));
            Assert.That(denyPaths, Does.Contain(hit));
        });
    }

    [Test]
    public void Merge_TransientUnauthorizedAccessThenSuccess_Merged()
    {
        using var root = new TestTempDir();
        var       home = root.CreateDirectory("sandbox-home");
        var       hit  = Path.Combine(home, "legacy-scan-hit");
        var semantic = new[] { Path.Combine(home, ".ssh") };
        var attempts = 0;

        // 权限类瞬态：前两次 UnauthorizedAccessException 按短间隔重试，第三次成功（与 IO 瞬态同口径）。
        var (denyPaths, entries, merged) = SandboxDenyReadBookkeeping.Merge(
            "per-command refresh", semantic, [home], home,
            _ => ++attempts < 3
                     ? throw new UnauthorizedAccessException("simulated transient access denied on the bookkeeping file")
                     : DenyBookkeepingJson(("S-1-5-21-9001", [hit])));

        Assert.Multiple(() =>
        {
            Assert.That(attempts, Is.EqualTo(3));
            Assert.That(entries, Is.EqualTo(1));
            Assert.That(merged, Is.EqualTo(1));
            Assert.That(denyPaths, Does.Contain(hit));
        });
    }

    [Test]
    public void Merge_PersistentReadFailure_FailsClosedWithStageAndFile()
    {
        using var root = new TestTempDir();
        var       home = root.CreateDirectory("sandbox-home");
        var semantic = new[] { Path.Combine(home, ".ssh") };

        var ex = TestAssert.Throws<InvalidOperationException>(() =>
            SandboxDenyReadBookkeeping.Merge("per-command refresh", semantic, [home], home,
                                             _ => throw new IOException("simulated persistent read failure")));

        // fail-closed 指明阶段与状态文件：命令不启动，绝不静默收缩 deny。
        Assert.Multiple(() =>
        {
            Assert.That(ex.Message, Does.Contain("per-command refresh"));
            Assert.That(ex.Message, Does.Contain("deny_read_acl_state.json"));
            Assert.That(ex.Message, Does.Contain("fails closed"));
            Assert.That(ex.InnerException, Is.TypeOf<IOException>());
        });
    }

    [Test]
    public void Merge_PersistentUnauthorizedAccess_FailsClosed()
    {
        using var root = new TestTempDir();
        var       home = root.CreateDirectory("sandbox-home");
        var semantic = new[] { Path.Combine(home, ".ssh") };

        var ex = TestAssert.Throws<InvalidOperationException>(() =>
            SandboxDenyReadBookkeeping.Merge("per-command refresh", semantic, [home], home,
                                             _ => throw new UnauthorizedAccessException("simulated persistent access denied")));

        // 权限类持续失败同样 fail-closed 指明阶段与状态文件；兼作 File.Exists 误判的回归：
        // 不可读绝不归为空集，绝不静默收缩另一端依赖的 deny。
        Assert.Multiple(() =>
        {
            Assert.That(ex.Message, Does.Contain("per-command refresh"));
            Assert.That(ex.Message, Does.Contain("deny_read_acl_state.json"));
            Assert.That(ex.Message, Does.Contain("fails closed"));
            Assert.That(ex.InnerException, Is.TypeOf<UnauthorizedAccessException>());
        });
    }

    [Test]
    public void Merge_StateFilePathOccupiedByDirectory_FailsClosed()
    {
        using var root = new TestTempDir();
        var       home = root.CreateDirectory("sandbox-home");
        Directory.CreateDirectory(SandboxDenyReadBookkeeping.StateFilePath(home));
        var semantic = new[] { Path.Combine(home, ".ssh") };

        // 真实默认读取差分：状态文件路径被目录占据=存在但不可读，读取抛 UnauthorizedAccessException；
        // File.Exists 对目录同样返回 false（旧实现据此误判为缺失归空集），新实现经有界重试后
        // fail-closed——不可读绝不归为缺失。
        var ex = TestAssert.Throws<InvalidOperationException>(() =>
            SandboxDenyReadBookkeeping.Merge("per-command refresh", semantic, [home], home));

        Assert.Multiple(() =>
        {
            Assert.That(ex.Message, Does.Contain("per-command refresh"));
            Assert.That(ex.Message, Does.Contain("deny_read_acl_state.json"));
            Assert.That(ex.Message, Does.Contain("fails closed"));
            Assert.That(ex.InnerException, Is.TypeOf<UnauthorizedAccessException>());
        });
    }

    [Test]
    public void Merge_CorruptJson_FailsClosedWithoutRetry()
    {
        using var root = new TestTempDir();
        var       home = root.CreateDirectory("sandbox-home");
        var semantic = new[] { Path.Combine(home, ".ssh") };
        var attempts = 0;

        // 注入计数器精确断言尝试次数：JSON 解析错不落入瞬态重试过滤，首次读取即 fail-closed。
        var ex = TestAssert.Throws<InvalidOperationException>(() =>
            SandboxDenyReadBookkeeping.Merge("per-command refresh", semantic, [home], home,
                                             _ => { attempts++; return "{ not json"; }));

        Assert.Multiple(() =>
        {
            Assert.That(attempts, Is.EqualTo(1), "parse errors fail closed immediately without retry");
            Assert.That(ex.Message, Does.Contain("per-command refresh"));
            Assert.That(ex.Message, Does.Contain("deny_read_acl_state.json"));
            Assert.That(ex.InnerException, Is.TypeOf<JsonException>());
        });
    }
}
