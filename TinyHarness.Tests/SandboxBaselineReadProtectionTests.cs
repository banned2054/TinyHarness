using System.Diagnostics;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         基线广泛读保护（GAP-1 OS 读边界，2026-11-08 冻结契约）的组合断言：走真实
///         Composer/Planner 与 <see cref="SandboxIsolationPolicy.CreateSetupPayload" /> 组装链，
///         不 mock payload。断言读根/deny 集结构、fail-closed 守卫与 profile 外自定义 home 行为。
///     </para>
///     <para>
///         Composition assertions for the baseline broad-read protection (the GAP-1 OS read
///         boundary, frozen contract 2026-11-08): through the real Composer/Planner and
///         <see cref="SandboxIsolationPolicy.CreateSetupPayload" /> assembly chain with no
///         payload mocking. Asserts the read-root and deny-set structure, the fail-closed
///         guards, and the custom-home-outside-profile behavior.
///     </para>
/// </summary>
public class SandboxBaselineReadProtectionTests
{
    private static string ProfileRoot =>
        Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    private static WindowsSandboxSettings TrustedSettingsIn(TestTempDir home) => new()
    {
        Enabled              = true,
        SetupExecutablePath  = home.WriteBytes("setup.exe", [0x4d, 0x5a]),
        RunnerExecutablePath = home.WriteBytes("runner.exe", [0x4d, 0x5a]),
        SandboxHome          = home.Root
    };

    private static void SkipOnNonWindows()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The baseline read boundary is Windows-only.");
    }

    private static bool ContainsOrdinal(IReadOnlyList<string> paths, string candidate) =>
        paths.Any(path => string.Equals(path, candidate, StringComparison.OrdinalIgnoreCase));

    // ---------------------------------------------------------------- 规划结构

    [Test]
    public void Plan_DefaultHomeUnderProfile_ShapeReadRootAndDenySet()
    {
        SkipOnNonWindows();
        var profile = ProfileRoot;

        // 规划纯函数：无保护密钥路径时零机器副作用；缺省 home 解析进 profile → cap_sid 入集。
        var protection = WindowsSandboxComposer.PlanBaselineReadProtection(
                                                                           [], null,
                                                                           new WindowsSandboxSettings
                                                                               { Enabled = true },
                                                                           static _ => []);

        Assert.That(protection, Is.Not.Null);
        Assert.Multiple(() =>
        {
            // 读根=用户 profile 根（单一条目）。
            Assert.That(protection!.ReadRoots, Has.Count.EqualTo(1));
            Assert.That(protection.ReadRoots[0], Is.EqualTo(profile).IgnoreCase);

            // deny 集结构化断言：C2 五段 + 上游 .codex + 缺省 home 的 cap_sid；共 7 条且无重复。
            Assert.That(protection.DenyReadPaths, Has.Count.EqualTo(7));
            foreach (var segment in new[] { ".ssh", ".aws", ".docker", ".gnupg", ".kube" })
                Assert.That(ContainsOrdinal(protection.DenyReadPaths, Path.Combine(profile, segment)), Is.True,
                            segment);
            Assert.That(ContainsOrdinal(protection.DenyReadPaths, Path.Combine(profile, ".codex")), Is.True, ".codex");
            Assert.That(ContainsOrdinal(protection.DenyReadPaths,
                                        Path.Combine(WindowsSandboxComponents.DefaultSandboxHome, "cap_sid")),
                        Is.True, "cap_sid");

            // .sandbox-secrets 由 provisioning 永久 DENY，不进 deny 簿记。
            Assert.That(ContainsOrdinal(protection.DenyReadPaths, Path.Combine(profile, ".sandbox-secrets")),
                        Is.False, ".sandbox-secrets");

            // 不变量：deny 全部落在读根覆盖内，且没有盘根或读根本身。
            Assert.That(protection.DenyReadPaths,
                        Has.All.Matches<string>(path => Workspace.IsInside(profile, path)), "coverage");
            Assert.That(ContainsOrdinal(protection.DenyReadPaths, profile), Is.False, "read-root itself");
        });
    }

    [Test]
    public void Plan_MissingProtectedSecretInsideProfile_FailsClosed()
    {
        SkipOnNonWindows();
        var ghost = Path.Combine(ProfileRoot, "th3-missing-secret-guard.json");

        // 覆盖范围内缺失的 C1 文件不可安全 deny（helper 会物化成目录）：就地 fail-closed。
        var ex = TestAssert.Throws<InvalidOperationException>(() =>
                                                                  WindowsSandboxComposer
                                                                     .PlanBaselineReadProtection([ghost], null, null,
                                                                          static _ => []));
        Assert.That(ex.Message, Does.Contain("does not exist"));
    }

    [Test]
    public void Plan_ExistingProtectedSecretInsideProfile_IsPairedIntoDenySet()
    {
        SkipOnNonWindows();
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        // Roaming 必然存在：作为覆盖范围内已存在的保护路径应被配对入 deny 集。
        var protection = WindowsSandboxComposer.PlanBaselineReadProtection([roaming], null, null, static _ => []);

        Assert.That(protection, Is.Not.Null);
        Assert.That(ContainsOrdinal(protection!.DenyReadPaths, Path.GetFullPath(roaming)), Is.True);
    }

    [Test]
    public void Plan_EnsureDirectoryOutsideProfile_IsSkippedWithoutCreation()
    {
        SkipOnNonWindows();
        var driveRoot = Path.GetPathRoot(ProfileRoot)!;
        var outside   = Path.Combine(driveRoot, "Code", "Temp", "th3-ensure-outside-probe");
        if (Directory.Exists(outside))
            Assert.Ignore("The probe directory already exists; skipping the creation check.");

        var protection = WindowsSandboxComposer.PlanBaselineReadProtection([], outside, null, static _ => []);

        // 覆盖范围外的 ensureDirectory 不创建、不入 deny 集。
        Assert.Multiple(() =>
        {
            Assert.That(protection, Is.Not.Null);
            Assert.That(Directory.Exists(outside), Is.False);
            Assert.That(protection!.DenyReadPaths.Any(path =>
                                                          path.StartsWith(outside, StringComparison.OrdinalIgnoreCase)),
                        Is.False);
        });
    }

    [Test]
    public void Plan_CustomHomeOutsideProfile_OmitsCapSidEntry()
    {
        SkipOnNonWindows();
        var driveRoot   = Path.GetPathRoot(ProfileRoot)!;
        var outsideHome = Path.Combine(driveRoot, "Code", "Temp", "th3-custom-home-probe");

        // profile 外自定义 home 无 OS 广泛读暴露：cap_sid 条目缺席（加入反而违反覆盖校验）。
        var protection = WindowsSandboxComposer.PlanBaselineReadProtection(
                                                                           [], null,
                                                                           new WindowsSandboxSettings
                                                                           {
                                                                               Enabled = true, SandboxHome = outsideHome
                                                                           },
                                                                           static _ => []);

        Assert.That(protection, Is.Not.Null);
        Assert.That(protection!.DenyReadPaths.Any(path => path.StartsWith(outsideHome,
                                                                          StringComparison.OrdinalIgnoreCase)),
                    Is.False);
    }

    // ---------------------------------------------------------------- 组合与 payload/身份

    [Test]
    public void Compose_WithBaseline_PayloadAndIdentityCarryReadBoundary()
    {
        SkipOnNonWindows();
        using var home       = new TestTempDir();
        using var workspace  = new TestTempDir();
        var       settings   = TrustedSettingsIn(home);
        var       protection = WindowsSandboxComposer.PlanBaselineReadProtection([], null, settings, static _ => []);
        var       composed   = WindowsSandboxComposer.Compose(settings, workspace.Root, null, null, protection);
        var       policy     = composed.Policy;

        Assert.Multiple(() =>
        {
            Assert.That(policy.BaselineReadRoots, Is.EqualTo(protection!.ReadRoots));
            Assert.That(policy.DenyReadPaths, Is.EqualTo(protection.DenyReadPaths));

            // 真实 payload 组装：read_roots 含 profile 根；deny_read_paths 原样下发。
            var payload = policy.CreateSetupPayload(composed.Components, workspace.Root, "runner", true, null);
            Assert.That(payload.ReadRoots.Any(path => string.Equals(path, ProfileRoot,
                                                                    StringComparison.OrdinalIgnoreCase)),
                        Is.True, "read_roots");
            Assert.That(payload.DenyReadPaths, Is.EquivalentTo(protection.DenyReadPaths).IgnoreCase, "deny_read_paths");

            // 覆盖不变量：deny ⊆ 读根覆盖。
            Assert.That(policy.DenyReadPaths,
                        Has.All.Matches<string>(path => Workspace.IsInside(policy.BaselineReadRoots[0], path)),
                        "coverage");
        });

        // 身份串（v8 授权读段随本轮引入）：版本与既有段以规范化键进入；结构化断言行内容而非逐字全串。
        var identity      = composed.ExecutionPolicy.PolicyIdentity;
        var identityLines = identity.Split('\n');
        var baselineKey   = SandboxCapabilitySidStore.CanonicalRootKey(ProfileRoot);
        var denyCodexKey  = SandboxCapabilitySidStore.CanonicalRootKey(Path.Combine(ProfileRoot, ".codex"));
        Assert.Multiple(() =>
        {
            Assert.That(identity, Does.Contain("policyVersion=8"));
            Assert.That(identityLines.Any(line => line.StartsWith("baselineReadRoots=" + baselineKey,
                                                                  StringComparison.Ordinal)),
                        Is.True, "baselineReadRoots section");
            Assert.That(identityLines.Where(line => line.StartsWith("denyReadPaths=", StringComparison.Ordinal))
                                     .Any(line => line.Contains(denyCodexKey, StringComparison.Ordinal)),
                        Is.True, "denyReadPaths section");
        });
    }

    [Test]
    public void Compose_DenyReadPathIsFileSystemRoot_FailsClosed()
    {
        SkipOnNonWindows();
        using var home       = new TestTempDir();
        var       protection = new SandboxBaselineReadProtection([ProfileRoot], [Path.GetPathRoot(ProfileRoot)!]);

        var ex = TestAssert.Throws<InvalidOperationException>(() =>
                                                                  WindowsSandboxComposer
                                                                     .Compose(TrustedSettingsIn(home), home.Root, null,
                                                                              null, protection));
        Assert.That(ex.Message, Does.Contain("must not be a filesystem root"));
    }

    [Test]
    public void Compose_DenyReadPathAsReadRootItself_FailsClosed()
    {
        SkipOnNonWindows();
        using var home       = new TestTempDir();
        var       protection = new SandboxBaselineReadProtection([ProfileRoot], [ProfileRoot]);

        var ex = TestAssert.Throws<InvalidOperationException>(() =>
                                                                  WindowsSandboxComposer
                                                                     .Compose(TrustedSettingsIn(home), home.Root, null,
                                                                              null, protection));
        Assert.That(ex.Message, Does.Contain("must not be a baseline read root itself"));
    }

    [Test]
    public void Compose_DenyReadPathOutsideReadRootCoverage_FailsClosed()
    {
        SkipOnNonWindows();
        using var home       = new TestTempDir();
        var       outside    = Path.Combine(Path.GetPathRoot(ProfileRoot)!, "Program Files");
        var       protection = new SandboxBaselineReadProtection([ProfileRoot], [outside]);

        var ex = TestAssert.Throws<InvalidOperationException>(() =>
                                                                  WindowsSandboxComposer
                                                                     .Compose(TrustedSettingsIn(home), home.Root, null,
                                                                              null, protection));
        Assert.That(ex.Message, Does.Contain("not covered by any baseline read root"));
    }

    [Test]
    public void Compose_DenyReadPathCoveringSandboxHome_FailsClosed()
    {
        SkipOnNonWindows();
        using var home      = new TestTempDir();
        using var workspace = new TestTempDir();
        var       settings  = TrustedSettingsIn(home);

        // deny 覆盖沙箱 home（相等语义同样拒绝）：沙箱必须读取自身运行时状态与组件。
        var protection = WindowsSandboxComposer.PlanBaselineReadProtection([], null, settings, static _ => [])
            with
            {
                DenyReadPaths = [home.Root]
            };

        var ex = TestAssert.Throws<InvalidOperationException>(() =>
                                                                  WindowsSandboxComposer.Compose(settings,
                                                                      workspace.Root, null, null, protection));
        Assert.That(ex.Message, Does.Contain("covers the sandbox runtime path"));
    }

    [Test]
    public void Compose_CustomHomeOutsideProfile_ComposesWithoutCapSidEntry()
    {
        SkipOnNonWindows();
        var driveRoot   = Path.GetPathRoot(ProfileRoot)!;
        var outsideHome = Path.Combine(driveRoot, "Code", "Temp", "th3-custom-home-probe");
        if (!Directory.Exists(outsideHome)) Directory.CreateDirectory(outsideHome);
        try
        {
            var settings = new WindowsSandboxSettings
            {
                Enabled              = true,
                SetupExecutablePath  = Path.Combine(outsideHome, "setup.exe"),
                RunnerExecutablePath = Path.Combine(outsideHome, "runner.exe"),
                SandboxHome          = outsideHome
            };
            File.WriteAllBytes(settings.SetupExecutablePath, [0x4d, 0x5a]);
            File.WriteAllBytes(settings.RunnerExecutablePath, [0x4d, 0x5a]);
            using var workspace = new TestTempDir();
            var protection = WindowsSandboxComposer.PlanBaselineReadProtection([], null, settings, static _ => []);
            var composed = WindowsSandboxComposer.Compose(settings, workspace.Root, null, null, protection);

            Assert.Multiple(() =>
            {
                Assert.That(composed.Policy.DenyReadPaths.Any(path => path.StartsWith(outsideHome,
                                                                  StringComparison.OrdinalIgnoreCase)), Is.False);
                Assert.That(composed.Policy.BaselineReadRoots, Has.Count.EqualTo(1));
            });
        }
        finally
        {
            Directory.Delete(outsideHome, true);
        }
    }

    // ---------------------------------------------------------------- 有界敏感名扫描（C2 OS 层补强）

    [Test]
    public void CoreScan_NestedSensitiveDirsAndNameHits_PairIntoDenySetWithoutNoise()
    {
        using var root = new TestTempDir();
        root.WriteFile("project/deep/.ssh/id_rsa", "private");
        root.WriteFile("project/.env", "SECRET=1");
        root.WriteFile("vault/server.pem", "cert");
        root.WriteFile("project/notes.txt", "normal");
        // home uses its own subdirectory: a home equal to the scan root would let the
        // home-subtree exclusion swallow every hit.
        var home = Directory.CreateDirectory(Path.Combine(root.Root, "sandbox-home"));
        var settings = new WindowsSandboxSettings
        {
            Enabled              = true,
            SetupExecutablePath  = Path.Combine(home.FullName, "setup.exe"),
            RunnerExecutablePath = Path.Combine(home.FullName, "runner.exe"),
            SandboxHome          = home.FullName
        };
        File.WriteAllText(settings.SetupExecutablePath, "stub");
        File.WriteAllText(settings.RunnerExecutablePath, "stub");

        var plan = WindowsSandboxComposer.PlanBaselineReadProtectionCore(
                                                                         root.Root, [], null, settings, null);

        Assert.Multiple(() =>
        {
            // nested sensitive dirs and file-name/extension hits all pair; plain files stay out
            Assert.That(plan.DenyReadPaths.Any(p => p.EndsWith("deep" + Path.DirectorySeparatorChar + ".ssh",
                                                               StringComparison.OrdinalIgnoreCase) ||
                                                    p.EndsWith("deep/.ssh", StringComparison.OrdinalIgnoreCase)),
                        Is.True, "nested .ssh dir");
            Assert.That(plan.DenyReadPaths.Any(p => p.EndsWith(".env", StringComparison.OrdinalIgnoreCase)),
                        Is.True, ".env file hit");
            Assert.That(plan.DenyReadPaths.Any(p => p.EndsWith("server.pem", StringComparison.OrdinalIgnoreCase)),
                        Is.True, ".pem file hit");
            Assert.That(plan.DenyReadPaths.Any(p => p.EndsWith("notes.txt", StringComparison.OrdinalIgnoreCase)),
                        Is.False, "normal file stays out");
            Assert.That(plan.DenyReadPaths.Any(p => p.EndsWith("cap_sid", StringComparison.OrdinalIgnoreCase)),
                        Is.True, "cap_sid entry");
            Assert.That(plan.DenyReadPaths, Has.All.Matches<string>(p => Workspace.IsInside(root.Root, p)),
                        "coverage");
        });
    }

    [Test]
    public void CoreScan_HomeInsideScanRoot_HomeSubtreeIsExcludedFromDenySet()
    {
        using var root = new TestTempDir();
        var       home = Directory.CreateDirectory(Path.Combine(root.Root, "sandbox-home"));
        File.WriteAllText(Path.Combine(home.FullName, "credentials"), "dpapi");
        root.WriteFile("elsewhere/.env", "SECRET=1");

        var settings = new WindowsSandboxSettings
        {
            Enabled              = true,
            SetupExecutablePath  = Path.Combine(home.FullName, "setup.exe"),
            RunnerExecutablePath = Path.Combine(home.FullName, "runner.exe"),
            SandboxHome          = home.FullName
        };
        File.WriteAllText(settings.SetupExecutablePath, "stub");
        File.WriteAllText(settings.RunnerExecutablePath, "stub");

        var plan = WindowsSandboxComposer.PlanBaselineReadProtectionCore(
                                                                         root.Root, [], null, settings, null);

        Assert.Multiple(() =>
        {
            // home-subtree sensitive hits (credentials) stay out of the deny set: the deny set
            // never touches the sandbox runtime face (the explicit cap_sid entry lives under the
            // home by design and is outside this assertion).
            Assert.That(plan.DenyReadPaths.Where(p => p.StartsWith(home.FullName, StringComparison.OrdinalIgnoreCase))
                            .Any(p => !p.EndsWith("cap_sid", StringComparison.OrdinalIgnoreCase)),
                        Is.False, "home-subtree hits excluded");
            // hits inside the scan root but outside the home still pair
            Assert.That(plan.DenyReadPaths.Any(p => p.EndsWith(".env", StringComparison.OrdinalIgnoreCase)),
                        Is.True, "outside-home hit included");
        });
    }

    [Test]
    public void CoreScan_VanishedHit_IsSkippedWithoutMaterialization()
    {
        using var root = new TestTempDir();
        var       real = Path.Combine(root.Root, "real", ".env");
        root.WriteFile("real/.env", "SECRET=1");
        var ghost = Path.Combine(root.Root, "real", "gone.env");

        // the injected scanner replaces enumeration, so it carries both the real hit and one
        // that vanished after the scan; the vanished hit must not pair or materialize
        var plan = WindowsSandboxComposer.PlanBaselineReadProtectionCore(
                                                                         root.Root, [], null, null, _ => [ghost, real]);

        Assert.Multiple(() =>
        {
            Assert.That(plan.DenyReadPaths.Any(p => string.Equals(p, ghost, StringComparison.OrdinalIgnoreCase)),
                        Is.False, "vanished hit skipped");
            Assert.That(Directory.Exists(ghost), Is.False, "no materialization");
            Assert.That(plan.DenyReadPaths.Any(p => string.Equals(p, real, StringComparison.OrdinalIgnoreCase)),
                        Is.True, "existing hit still paired");
        });
    }

    [Test]
    public void CoreScan_HitCoveredByDenyAncestor_IsNotDuplicated()
    {
        using var root = new TestTempDir();
        Directory.CreateDirectory(Path.Combine(root.Root, ".ssh"));
        var nested = Path.Combine(root.Root, ".ssh", "extra.env");

        // the top-level .ssh dir is already in the set unconditionally (helper materialization
        // semantics), so a hit inside its subtree must not pair twice
        var plan = WindowsSandboxComposer.PlanBaselineReadProtectionCore(
                                                                         root.Root, [], null, null, _ => [nested]);

        Assert.Multiple(() =>
        {
            Assert.That(plan.DenyReadPaths.Count(p => p.Contains(".ssh", StringComparison.OrdinalIgnoreCase)),
                        Is.EqualTo(1), "ancestor coverage dedupes the hit");
            Assert.That(Directory.Exists(nested), Is.False, "no materialization for the nested hit");
        });
    }

    [Test]
    public void Enumerator_SyntheticTree_FindsHitsAndNeverDescendsReparsePoints()
    {
        SkipOnNonWindows();
        using var root = new TestTempDir();
        root.WriteFile(".env", "SECRET=1");
        root.WriteFile("deep/nested/.ssh/id_rsa", "private");
        root.WriteFile("keys/server.pem", "cert");
        root.WriteFile("plain/readme.txt", "hello");

        // 重解析点：名称不敏感的目录链接，目标内藏敏感文件——词法判定不命中、绝不下降到目标。
        var outside = Path.Combine(Path.GetTempPath(), "th3-scan-reparse-target");
        Directory.CreateDirectory(Path.Combine(outside, ".ssh"));
        File.WriteAllText(Path.Combine(outside, ".ssh", "id_rsa"), "private-target");
        var junction = Path.Combine(root.Root, "link");
        using var setup = Process.Start(new ProcessStartInfo
        {
            FileName               = "cmd.exe",
            Arguments              = $"/c mklink /J \"{junction}\" \"{outside}\"",
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true
        });
        Assert.That(setup, Is.Not.Null);
        Assert.That(setup!.WaitForExit(30_000), Is.True, "junction creation failed");
        try
        {
            var hits = WindowsSandboxComposer.EnumerateBaselineSensitiveNameHits(root.Root);

            Assert.Multiple(() =>
            {
                Assert.That(hits.Any(p => p.EndsWith(".env", StringComparison.OrdinalIgnoreCase)), Is.True);
                // a hit directory pairs itself and is never descended (its subtree is deny-covered)
                Assert.That(hits.Any(p => p.EndsWith(".ssh", StringComparison.OrdinalIgnoreCase)), Is.True);
                Assert.That(hits.Any(p => p.EndsWith("server.pem", StringComparison.OrdinalIgnoreCase)), Is.True);
                Assert.That(hits.Any(p => p.Contains("readme", StringComparison.OrdinalIgnoreCase)), Is.False);
                // junction 名称不敏感 → 不命中；其目标内的敏感文件绝不经链接下降被命中。
                Assert.That(hits.Count(p => p.Contains("link", StringComparison.OrdinalIgnoreCase)), Is.EqualTo(0),
                            "no hits through reparse points");
            });
        }
        finally
        {
            Directory.Delete(outside, true);
        }
    }

    [Test]
    public void Enumerator_BudgetExhaustion_FailsClosedInsteadOfPartialScan()
    {
        using var root = new TestTempDir();
        for (var index = 0; index < 6; index++) root.WriteFile($"f{index}.txt", "x");

        // 预算按枚举条目计：6 个条目、预算 3 → 抛出而非返回未扫完的部分结果。
        var ex = TestAssert.Throws<InvalidOperationException>(() => WindowsSandboxComposer
                                                                 .EnumerateBaselineSensitiveNameHits(root.Root, 3));
        Assert.That(ex.Message, Does.Contain("enumeration budget"));
    }
}
