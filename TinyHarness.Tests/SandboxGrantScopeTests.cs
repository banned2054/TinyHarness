using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

namespace TinyHarness.Tests;

/// <summary>
///     grant 驱动沙箱能力扩展的单元测试：grant 根的 fail-closed 校验、payload 合并与
///     deny 收缩、策略身份的 grant 段与撤销回落。以参数化的根列表代替 grant 来源适配层。
///     Unit tests for the grant-driven sandbox capability extension: fail-closed
///     validation of grant roots, payload merging and deny reduction, and the policy
///     identity's grant section with revocation fall-back. Parameterized root lists
///     stand in for the grant-source adapter.
/// </summary>
public class SandboxGrantScopeTests
{
    // ---------------------------------------------------------------- 校验

    [Test]
    public void ResolveGrantScope_EmptyInputs_ReturnsEmptyScope()
    {
        using var dir = Fixtures();

        var scope = WindowsSandboxComposer.ResolveGrantScope(Policy(dir), Components(dir), null, null, null);

        Assert.That(scope.IsEmpty, Is.True);
        Assert.That(scope.ReadRoots, Is.Empty);
        Assert.That(scope.WriteRoots, Is.Empty);
        Assert.That(scope.MetadataWritePaths, Is.Empty);
    }

    [Test]
    public void ResolveGrantScope_NormalizesRootsAndDeduplicatesCaseInsensitively()
    {
        using var dir = Fixtures();
        var grantRead = dir.WriteFile("grants/read/marker.txt", "x");
        var upper     = grantRead.ToUpperInvariant();

        var scope = WindowsSandboxComposer.ResolveGrantScope(Policy(dir), Components(dir),
                                                             [grantRead, grantRead, upper], null, null);

        Assert.That(scope.ReadRoots, Is.EqualTo([Path.GetFullPath(grantRead)]));
    }

    [Test]
    public void ResolveGrantScope_RejectsRelativeRoots()
    {
        using var dir = Fixtures();

        var ex = Assert.Throws<InvalidOperationException>(() =>
                   WindowsSandboxComposer.ResolveGrantScope(Policy(dir), Components(dir),
                                                            ["grants/relative"], null, null));
        Assert.That(ex!.Message, Does.Contain("fully qualified absolute path"));
    }

    [Test]
    public void ResolveGrantScope_RejectsReadRootOverlappingCredentialsDirectory()
    {
        using var dir = Fixtures();
        var credentials = Path.Combine(dir.Root, "home", ".sandbox-secrets");

        var ex = Assert.Throws<InvalidOperationException>(() =>
                   WindowsSandboxComposer.ResolveGrantScope(Policy(dir), Components(dir),
                                                            [credentials], null, null));
        Assert.That(ex!.Message, Does.Contain("credentials directory"));
    }

    [Test]
    public void ResolveGrantScope_RejectsWriteRootInsideCredentialsAncestor()
    {
        using var dir = Fixtures();
        // 写根覆盖整个 home 时必然覆盖凭据目录：双向重叠语义必须拒绝。
        var home = Path.Combine(dir.Root, "home");

        var ex = Assert.Throws<InvalidOperationException>(() =>
                   WindowsSandboxComposer.ResolveGrantScope(Policy(dir), Components(dir),
                                                            null, [home], null));
        Assert.That(ex!.Message, Does.Contain("credentials directory"));
    }

    [Test]
    public void ResolveGrantScope_RejectsWriteRootCoveringTrustedComponent()
    {
        using var dir   = Fixtures();
        using var tools = new TestTempDir();
        // 组件放在 grant 写根之外的家目录之外：凭据校验不触发，仅组件保护触发。
        var setup  = tools.WriteFile("bin/codex-windows-sandbox-setup.exe", "x");
        var runner = tools.WriteFile("bin/codex-command-runner.exe", "x");
        var components = Components(dir) with
        {
            SetupExecutablePath  = setup,
            RunnerExecutablePath = runner
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
                   WindowsSandboxComposer.ResolveGrantScope(Policy(dir), components,
                                                            null, [Path.Combine(tools.Root, "bin")], null));
        Assert.That(ex!.Message, Does.Contain("trusted sandbox path"));
    }

    [Test]
    public void ResolveGrantScope_RejectsWriteRootOverlappingAPermissionSystemPath()
    {
        using var dir   = Fixtures();
        using var store = new TestTempDir();
        var userConfig = dir.WriteFile("config/user-config.json", "{}");
        var trustFile  = dir.WriteFile("config/workspace-trust.json", "{}");
        var permissionSystemPaths = new[] { userConfig, trustFile, store.Root };

        // 覆盖方向：grant 写根覆盖用户配置/信任文件所在目录 → fail closed。
        var covering = Assert.Throws<InvalidOperationException>(() =>
            WindowsSandboxComposer.ResolveGrantScope(Policy(dir), Components(dir), null,
                                                     [Path.Combine(dir.Root, "config")], null,
                                                     permissionSystemPaths));
        Assert.That(covering!.Message, Does.Contain("permission-system protected path"));

        // 落入方向：grant 写根落在会话存储目录内部同样拒绝。
        var inside = Assert.Throws<InvalidOperationException>(() =>
            WindowsSandboxComposer.ResolveGrantScope(Policy(dir), Components(dir), null,
                                                     [Path.Combine(store.Root, "sessions")], null,
                                                     permissionSystemPaths));
        Assert.That(inside!.Message, Does.Contain("permission-system protected path"));

        // 对照：未携带 C1 实例路径时同一写根不触发该保护（组合点没有实例的既有语义不变）。
        var scope = WindowsSandboxComposer.ResolveGrantScope(Policy(dir), Components(dir), null,
                                                             [Path.Combine(dir.Root, "config")], null);
        Assert.That(scope.WriteRoots, Is.EqualTo([Path.GetFullPath(Path.Combine(dir.Root, "config"))]));
    }

    [Test]
    public void ResolveGrantScope_RejectsMetadataWriteOutsideWhitelist()
    {
        using var dir = Fixtures();
        dir.CreateDirectory("other");

        var ex = Assert.Throws<InvalidOperationException>(() =>
                   WindowsSandboxComposer.ResolveGrantScope(Policy(dir), Components(dir),
                                                            null, null, [Path.Combine(dir.Root, "other")]));
        Assert.That(ex!.Message, Does.Contain("protected metadata directory"));
    }

    [Test]
    public void ResolveGrantScope_AcceptsMetadataWriteForBuiltInSubdirectories()
    {
        using var dir = Fixtures();
        dir.CreateDirectory(".git");

        var scope = WindowsSandboxComposer.ResolveGrantScope(Policy(dir), Components(dir),
                                                             null, null, [Path.Combine(dir.Root, ".git")]);

        Assert.That(scope.MetadataWritePaths, Is.EqualTo([Path.Combine(dir.Root, ".git")]));
    }

    // ---------------------------------------------------------------- payload 合并

    [Test]
    public void CreateSetupPayload_MergesGrantRootsIntoReadAndWriteLists()
    {
        using var dir       = Fixtures();
        var       grantRead = dir.WriteFile("grants/read/marker.txt", "x");
        var grantWrite = dir.WriteFile("grants/write/marker.txt", "x");
        var policy     = Policy(dir);

        var payload = policy.CreateSetupPayload(Components(dir), dir.Root, Environment.UserName, true,
                                                WindowsSandboxComposer.ResolveGrantScope(policy, Components(dir),
                                                                                        [grantRead],
                                                                                        [grantWrite], null));

        Assert.That(payload.ReadRoots, Does.Contain(Path.GetFullPath(grantRead)));
        Assert.That(payload.WriteRoots, Does.Contain(Path.GetFullPath(grantWrite)));
        // 基线范围保持不变：工作区写根与临时读根仍在；合并后无重复项。
        Assert.That(payload.WriteRoots, Does.Contain(dir.Root));
        Assert.That(payload.ReadRoots, Has.Count.EqualTo(payload.ReadRoots.Distinct(StringComparer.OrdinalIgnoreCase)
                                                                        .Count()));
    }

    [Test]
    public void CreateSetupPayload_ShrinksDenyWritePathsOnlyForGrantedMetadata()
    {
        using var dir = Fixtures();
        dir.CreateDirectory(".git");
        dir.CreateDirectory(".agents");
        var policy = Policy(dir);
        var grant  = WindowsSandboxComposer.ResolveGrantScope(policy, Components(dir),
                                                             null, null, [Path.Combine(dir.Root, ".git")]);

        var payload = policy.CreateSetupPayload(Components(dir), dir.Root, Environment.UserName, true, grant);

        Assert.That(payload.DenyWritePaths, Does.Not.Contain(Path.Combine(dir.Root, ".git")));
        Assert.That(payload.DenyWritePaths, Does.Contain(Path.Combine(dir.Root, ".agents")));
    }

    [Test]
    public void CreateSetupPayload_EmptyGrant_MatchesGrantFreePayload()
    {
        using var dir       = Fixtures();
        dir.CreateDirectory(".git");
        var       policy    = Policy(dir);
        var       baseline  = policy.CreateSetupPayload(Components(dir), dir.Root, Environment.UserName, true);
        var       withEmpty = policy.CreateSetupPayload(Components(dir), dir.Root, Environment.UserName, true,
                                                        SandboxGrantScope.Empty);

        Assert.That(withEmpty.ReadRoots, Is.EqualTo(baseline.ReadRoots));
        Assert.That(withEmpty.WriteRoots, Is.EqualTo(baseline.WriteRoots));
        Assert.That(withEmpty.DenyWritePaths, Is.EqualTo(baseline.DenyWritePaths));
    }

    // ---------------------------------------------------------------- 策略身份

    [Test]
    public void BuildGrantPolicyIdentity_ContainsGrantSection_AndIsStable()
    {
        using var dir       = Fixtures();
        var       grantRead = dir.WriteFile("grants/read/marker.txt", "x");
        var scope = WindowsSandboxComposer.ResolveGrantScope(Policy(dir), Components(dir), [grantRead], null, null);

        var baseline = WindowsSandboxComposer.BuildPolicyIdentity(Policy(dir), EmptyEnv(), EmptyEnv());
        var first  = WindowsSandboxComposer.BuildGrantPolicyIdentity(baseline, scope);
        var second = WindowsSandboxComposer.BuildGrantPolicyIdentity(baseline, scope);

        Assert.That(first, Is.EqualTo(second));
        Assert.That(first, Does.Contain("grantReadRoots="));
    }

    [Test]
    public void BuildGrantPolicyIdentity_DiffersByGrantRoots()
    {
        using var dir        = Fixtures();
        var       grantReadA = dir.WriteFile("grants/a/marker.txt", "x");
        var       grantReadB = dir.WriteFile("grants/b/marker.txt", "x");
        var scopeA = WindowsSandboxComposer.ResolveGrantScope(Policy(dir), Components(dir), [grantReadA], null, null);
        var scopeB = WindowsSandboxComposer.ResolveGrantScope(Policy(dir), Components(dir), [grantReadB], null, null);

        var baseline = WindowsSandboxComposer.BuildPolicyIdentity(Policy(dir), EmptyEnv(), EmptyEnv());
        var identityA = WindowsSandboxComposer.BuildGrantPolicyIdentity(baseline, scopeA);
        var identityB = WindowsSandboxComposer.BuildGrantPolicyIdentity(baseline, scopeB);

        Assert.That(identityA, Is.Not.EqualTo(identityB));
    }

    [Test]
    public void BuildGrantPolicyIdentity_EmptyOrRevokedGrant_MatchesBaselineIdentity()
    {
        using var dir = Fixtures();
        var grantFree = WindowsSandboxComposer.BuildPolicyIdentity(Policy(dir), EmptyEnv(), EmptyEnv());
        var baseline = WindowsSandboxComposer.BuildGrantPolicyIdentity(grantFree, SandboxGrantScope.Empty);

        Assert.That(baseline, Is.EqualTo(grantFree));
        // 撤销（回落空范围）即恢复基线身份：旧授权不再匹配带 grant 的身份，符合预期成本。
        Assert.That(baseline, Does.Not.Contain("grantReadRoots="));
    }

    // ---------------------------------------------------------------- 装置

    /// <summary>空目标/代理环境字典，供身份串构造使用。 Empty target/proxy dictionaries for identity construction.</summary>
    private static IReadOnlyDictionary<string, string> EmptyEnv() => new Dictionary<string, string>();

    /// <summary>构造 home 与凭据目录并返回工作区临时目录。 Builds a home with a credentials directory inside the temp root.</summary>
    private static TestTempDir Fixtures()
    {
        var dir = new TestTempDir();
        Directory.CreateDirectory(Path.Combine(dir.Root, "home", ".sandbox-secrets"));
        return dir;
    }

    private static WindowsSandboxComponents Components(TestTempDir dir) => new()
    {
        SetupExecutablePath  = Path.Combine(dir.Root, "home", "codex-windows-sandbox-setup.exe"),
        RunnerExecutablePath = Path.Combine(dir.Root, "home", "codex-command-runner.exe"),
        SandboxHome          = Path.Combine(dir.Root, "home")
    };

    private static SandboxIsolationPolicy Policy(TestTempDir dir) => new()
    {
        Kind               = SandboxPolicyKind.WorkspaceWrite,
        WorkspaceRoot      = dir.Root,
        SpawnProfile       = new SandboxPermissionProfile
        {
            FileSystem = new SandboxFileSystemRestriction { Entries = [] },
            Network    = "restricted"
        },
        EffectiveReadRoots = [],
        EffectiveWriteRoots = [dir.Root],
        DenyWritePaths     = [Path.Combine(dir.Root, ".git"), Path.Combine(dir.Root, ".agents")],
        TempWriteRoots     = []
    };
}
