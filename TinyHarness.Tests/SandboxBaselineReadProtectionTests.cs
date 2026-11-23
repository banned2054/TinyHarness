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
                        Has.All.Matches<string>(path => path is not null && Workspace.IsInside(profile, path)),
                        "coverage");
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
                        Has.All.Matches<string>(path => path is not null &&
                                                        Workspace.IsInside(policy.BaselineReadRoots[0], path)),
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
        var planned = WindowsSandboxComposer.PlanBaselineReadProtection([], null, settings, static _ => []);
        Assert.That(planned, Is.Not.Null);
        var protection = planned! with
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
            Assert.That(plan.DenyReadPaths,
                        Has.All.Matches<string>(p => p is not null && Workspace.IsInside(root.Root, p)), "coverage");
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

    [Test]
    public void Enumerator_CancelledBeforeStart_DoesNotEnumerate()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => WindowsSandboxComposer.EnumerateBaselineSensitiveNameHits(
                                                   "unused", cancellationToken : cancellation.Token,
                                                   enumerateEntries : _ =>
                                                       throw new AssertionException("Must not enumerate.")));
    }

    [Test]
    public void Enumerator_CancelledDuringEnumeration_DoesNotReturnPartialHits()
    {
        using var root         = new TestTempDir();
        var       sensitive    = root.WriteFile(".env", "fixture");
        using var cancellation = new CancellationTokenSource();
        var       disposed     = false;

        IEnumerable<string> Entries(string directory)
        {
            try
            {
                yield return sensitive;
                cancellation.Cancel();
                yield return Path.Combine(directory, "unvisited.txt");
            }
            finally
            {
                disposed = true;
            }
        }

        Assert.Throws<OperationCanceledException>(() => WindowsSandboxComposer.EnumerateBaselineSensitiveNameHits(
                                                   root.Root, cancellationToken : cancellation.Token,
                                                   enumerateEntries : Entries));
        Assert.That(disposed, Is.True);
    }

    [Test]
    public void Enumerator_BudgetBoundsStreamingEnumeration()
    {
        using var root       = new TestTempDir();
        var       enumerated = 0;

        IEnumerable<string> Entries(string directory)
        {
            while (true)
            {
                enumerated++;
                yield return Path.Combine(directory, "missing.txt");
            }
        }

        Assert.Throws<InvalidOperationException>(() => WindowsSandboxComposer.EnumerateBaselineSensitiveNameHits(
                                                  root.Root, 3, enumerateEntries : Entries));
        Assert.That(enumerated, Is.EqualTo(4), "不能先完整物化目录再检查预算。");
    }

    [Test]
    public void CoreScan_ExplicitBudgetExceeded_PropagatesWithoutPartialPlan()
    {
        using var root = new TestTempDir();
        for (var index = 0; index < 6; index++) root.WriteFile($"f{index}.txt", "x");

        // Core 层显式有限预算超限：异常原样传播，绝不返回部分计划。
        var ex = TestAssert.Throws<InvalidOperationException>(() =>
            WindowsSandboxComposer.PlanBaselineReadProtectionCore(root.Root, [], null, null, null,
                                                                  CancellationToken.None, 3));
        Assert.That(ex.Message, Does.Contain("enumeration budget"));
    }

    [Test]
    public void CoreScan_CancelledDuringScan_PropagatesWithoutPartialPlan()
    {
        using var root         = new TestTempDir();
        var       hit          = root.WriteFile(".env", "fixture");
        using var cancellation = new CancellationTokenSource();

        IEnumerable<string> Entries(string directory)
        {
            yield return hit;
            cancellation.Cancel();
            yield return Path.Combine(directory, "unvisited.txt");
        }

        // Core 层扫描中途取消：OCE 传播，绝不返回部分计划（默认预算不放松取消语义）。
        Assert.Throws<OperationCanceledException>(() =>
            WindowsSandboxComposer.PlanBaselineReadProtectionCore(root.Root, [], null, null, null,
                                                                  cancellation.Token,
                                                                  enumerateEntries : Entries));
    }

    // ------------------------------------------------- 不可认证扫描 fail-closed（不返回部分拒读集）

    [Test]
    public void CoreScan_DirectoryIoErrorAfterHits_FailsClosedWithoutPartialPlan()
    {
        using var root = new TestTempDir();
        var       hit  = root.WriteFile("real/.env", "SECRET=1");

        // 已产出命中后再遇目录级 IO 错误（模拟 MoveNext 失败）：计划失败，绝不返回部分拒读集。
        IEnumerable<string> Entries(string directory)
        {
            yield return hit;
            throw new UnauthorizedAccessException($"simulated unreadable listing of '{directory}'");
        }

        var ex = TestAssert.Throws<InvalidOperationException>(() =>
            WindowsSandboxComposer.PlanBaselineReadProtectionCore(root.Root, [], null, null, null,
                                                                  CancellationToken.None,
                                                                  enumerateEntries : Entries));
        Assert.Multiple(() =>
        {
            Assert.That(ex.Message, Does.Contain("could not enumerate").And.Contain(root.Root));
            Assert.That(ex.InnerException, Is.TypeOf<UnauthorizedAccessException>());
        });
    }

    [Test]
    public void CoreScan_RootEnumerationFailure_FailsClosedInsteadOfEmptyHits()
    {
        using var root = new TestTempDir();

        // 根目录自身枚举失败（模拟初始化抛出）：不是"空命中成功"，而是整个计划失败。
        var ex = TestAssert.Throws<InvalidOperationException>(() =>
            WindowsSandboxComposer.PlanBaselineReadProtectionCore(root.Root, [], null, null, null,
                                                                  CancellationToken.None,
                                                                  enumerateEntries : _ =>
                                                                      throw new UnauthorizedAccessException("simulated unreadable root")));
        Assert.Multiple(() =>
        {
            Assert.That(ex.Message, Does.Contain("could not enumerate").And.Contain(root.Root));
            Assert.That(ex.InnerException, Is.TypeOf<UnauthorizedAccessException>());
        });
    }

    [Test]
    public void CoreScan_UninspectableEntry_FailsClosed()
    {
        SkipOnNonWindows();
        using var root = new TestTempDir();

        // 夹具：DOS 空设备（NUL）在任何目录下都解析为设备条目——名称存在但属性不可读。
        // 实测（见验证记录）：File.GetAttributes 完全忽略文件/目录 DACL，ACL deny 无法制造
        // 真实 UnauthorizedAccessException；NUL 确定性抛出非"消失"类的 IOException，与
        // UnauthorizedAccessException 共用同一条"不可检查→fail-closed"过滤臂。
        var locked = Path.Combine(root.Root, "NUL");

        // 夹具自检：属性读取确实抛出非"消失"类 IO 异常，防止设备语义变化造成假通过。
        // 精确类型断言排除 FileNotFoundException/DirectoryNotFoundException（均为 IOException 派生）。
        TestAssert.Throws<IOException>(() => File.GetAttributes(locked));

        IEnumerable<string> Entries(string directory)
        {
            yield return locked;
        }

        var ex = TestAssert.Throws<InvalidOperationException>(() =>
            WindowsSandboxComposer.PlanBaselineReadProtectionCore(root.Root, [], null, null, null,
                                                                  CancellationToken.None,
                                                                  enumerateEntries : Entries));
        Assert.Multiple(() =>
        {
            Assert.That(ex.Message, Does.Contain("could not inspect").And.Contain("NUL"));
            Assert.That(ex.InnerException, Is.InstanceOf<IOException>());
            Assert.That(ex.InnerException is FileNotFoundException or DirectoryNotFoundException, Is.False,
                        "the wrapped error must not be a vanished-entry derivative");
        });
    }

    [Test]
    public void CoreScan_VanishedEntry_IsLexicallyJudgedAndPlanSucceeds()
    {
        using var root  = new TestTempDir();
        var       plain = root.WriteFile("plain.txt", "x");
        // 叶子缺失 → FileNotFoundException；父目录缺失 → DirectoryNotFoundException：两种
        // "消失"派生都走词法判定。
        var ghostFile   = Path.Combine(root.Root, ".env");
        var ghostParent = Path.Combine(root.Root, "gone-dir", ".env");

        // 消失条目（并发删除竞态）无暴露面：词法判定入命中，扫描与计划成功；幽灵路径不进入
        // deny 集（配对阶段如实跳过，绝不物化）。
        IEnumerable<string> Entries(string directory)
        {
            yield return ghostFile;
            yield return ghostParent;
            yield return plain;
        }

        var hits = WindowsSandboxComposer.EnumerateBaselineSensitiveNameHits(root.Root,
                                                                            enumerateEntries : Entries);
        var plan = WindowsSandboxComposer.PlanBaselineReadProtectionCore(root.Root, [], null, null, null,
                                                                        CancellationToken.None,
                                                                        enumerateEntries : Entries);
        Assert.Multiple(() =>
        {
            Assert.That(hits.Any(p => string.Equals(p, ghostFile, StringComparison.OrdinalIgnoreCase)), Is.True,
                        "vanished file judged lexically");
            Assert.That(hits.Any(p => string.Equals(p, ghostParent, StringComparison.OrdinalIgnoreCase)), Is.True,
                        "vanished under missing parent judged lexically");
            Assert.That(plan.DenyReadPaths.Any(p => p is not null &&
                                                    (string.Equals(p, ghostFile, StringComparison.OrdinalIgnoreCase) ||
                                                     p.StartsWith(Path.Combine(root.Root, "gone-dir"),
                                                                  StringComparison.OrdinalIgnoreCase))),
                        Is.False, "vanished entries never paired or materialized");
        });
    }

    // ---------------------------------------------------------------- 墙钟时间预算（fail-closed）

    [Test]
    public void Enumerator_TimeBudgetExceeded_FailsClosedWithMetering()
    {
        using var root   = new TestTempDir();
        var       first  = root.WriteFile("a.txt", "x");
        var       second = root.WriteFile("b.txt", "x");

        IEnumerable<string> Entries(string directory)
        {
            yield return first;
            Thread.Sleep(50);
            yield return second;
        }

        var ex = TestAssert.Throws<InvalidOperationException>(() =>
            WindowsSandboxComposer.EnumerateBaselineSensitiveNameHits(root.Root,
                                                                     enumerateEntries : Entries,
                                                                     timeBudget : TimeSpan.FromMilliseconds(10)));
        Assert.Multiple(() =>
        {
            Assert.That(ex.Message, Does.Contain("wall-clock time budget"));
            Assert.That(ex.Message, Does.Match(@"after enumerating \d+ entries"));
            Assert.That(ex.Message, Does.Match(@"elapsed \d+ ms"));
        });
    }

    [Test]
    public void CoreScan_TimeBudgetExceeded_PropagatesWithoutPartialPlan()
    {
        using var root   = new TestTempDir();
        var       first  = root.WriteFile("a.txt", "x");
        var       second = root.WriteFile("b.txt", "x");

        IEnumerable<string> Entries(string directory)
        {
            yield return first;
            Thread.Sleep(50);
            yield return second;
        }

        var ex = TestAssert.Throws<InvalidOperationException>(() =>
            WindowsSandboxComposer.PlanBaselineReadProtectionCore(root.Root, [], null, null, null,
                                                                  CancellationToken.None,
                                                                  enumerateEntries : Entries,
                                                                  scanTimeBudget : TimeSpan.FromMilliseconds(10)));
        Assert.That(ex.Message, Does.Contain("wall-clock time budget"));
    }

    [Test]
    public void CoreScan_ExplicitTimeBudget_CompletesNormally()
    {
        using var root = new TestTempDir();
        // 命中路径显式规范化：WriteFile 保留子目录段的 "/"，配对阶段输出 GetFullPath 规范形态。
        var       hit  = Path.GetFullPath(root.WriteFile("deep/.env", "SECRET=1"));

        // 显式充足预算：正常完成路径不受影响。
        var plan = WindowsSandboxComposer.PlanBaselineReadProtectionCore(
                                                                     root.Root, [], null, null, null,
                                                                     CancellationToken.None,
                                                                     enumerateEntries : _ => new[] { hit },
                                                                     scanTimeBudget : TimeSpan.FromSeconds(30));
        Assert.That(plan.DenyReadPaths.Any(p => string.Equals(p, hit, StringComparison.OrdinalIgnoreCase)),
                    Is.True, "hit pairs into the deny set");
    }

    [Test]
    public void Enumerator_NegativeTimeBudget_FailsImmediatelyWithoutEnumerating()
    {
        using var root = new TestTempDir();

        // 负数预算=立即超界（与条目预算的负数哨兵一致）：枚举尚未开始即 fail-closed。
        var ex = TestAssert.Throws<InvalidOperationException>(() =>
            WindowsSandboxComposer.EnumerateBaselineSensitiveNameHits(
                                                                     root.Root,
                                                                     enumerateEntries : _ =>
                                                                         throw new AssertionException("Must not enumerate."),
                                                                     timeBudget : TimeSpan.FromSeconds(-1)));
        Assert.That(ex.Message, Does.Contain("wall-clock time budget"));
    }

    [Test]
    public void Enumerator_ZeroTimeBudget_DisablesWallClockBudget()
    {
        using var root = new TestTempDir();
        var       hit  = root.WriteFile("slow/.env", "SECRET=1");

        // 0=不设时间预算：慢枚举不被立即超界或小预算拦截。
        IEnumerable<string> Entries(string directory)
        {
            Thread.Sleep(30);
            yield return hit;
        }

        var hits = WindowsSandboxComposer.EnumerateBaselineSensitiveNameHits(
                                                                       root.Root, enumerateEntries : Entries,
                                                                       timeBudget : TimeSpan.Zero);
        Assert.That(hits, Has.Count.EqualTo(1), "the slow enumeration completes under a zero time budget");
    }

    // --------------------------------------- 命中复检、覆盖剪枝与配对期预算（复审回归）

    [Test]
    public void CoreScan_RecheckHitWithPermissionError_FailsClosedWithoutPartialPlan()
    {
        using var root = new TestTempDir();
        var       hit  = Path.GetFullPath(root.WriteFile("real/.env", "SECRET=1"));

        // 已命中条目在配对复检时不可检查（注入权限错误）：File/Directory.Exists 会把它吞成
        // "已消失"跳过——修复后必须带上下文使整个规划失败，原异常保留为 inner exception。
        var ex = TestAssert.Throws<InvalidOperationException>(() =>
            WindowsSandboxComposer.PlanBaselineReadProtectionCore(root.Root, [], null, null,
                                                                  _ => [hit], CancellationToken.None,
                                                                  inspectEntry : _ =>
                                                                      throw new UnauthorizedAccessException("simulated denied re-check")));
        Assert.Multiple(() =>
        {
            Assert.That(ex.Message, Does.Contain("could not re-check").And.Contain(hit));
            Assert.That(ex.InnerException, Is.TypeOf<UnauthorizedAccessException>());
        });
    }

    [Test]
    public void CoreScan_DenyCoveredSubtree_IsNotDescendedButDenyEntryRemains()
    {
        using var root = new TestTempDir();
        // .codex 已无条件进入基线拒读集：真实目录内的命中绝不该再经枚举发现。
        root.WriteFile(".codex/inner/id_rsa", "private");
        // 同名前缀兄弟 .sshx 不被 .ssh 条目覆盖：必须照常下降，其内嵌命中照常配对。
        var sshxHit  = Path.GetFullPath(root.WriteFile(".sshx/nested/id_rsa", "private"));
        var plainHit = Path.GetFullPath(root.WriteFile("plain/.env", "SECRET=1"));
        var codexDir = Path.Combine(root.Root, ".codex");
        var sshxDir  = Path.Combine(root.Root, ".sshx");
        var visited  = new List<string>();

        IEnumerable<string> Entries(string directory)
        {
            visited.Add(directory);
            return Directory.EnumerateFileSystemEntries(directory);
        }

        var plan = WindowsSandboxComposer.PlanBaselineReadProtectionCore(
                                                                root.Root, [], null, null, null,
                                                                CancellationToken.None,
                                                                enumerateEntries : Entries);

        Assert.Multiple(() =>
        {
            // 已覆盖子树不会调用其枚举器（根与未覆盖子树照常枚举）。
            Assert.That(visited, Does.Contain(root.Root));
            Assert.That(visited, Does.Contain(sshxDir));
            Assert.That(visited.Any(path => path.StartsWith(codexDir, StringComparison.OrdinalIgnoreCase)),
                        Is.False, "deny-covered .codex subtree never enumerated");
            // 对应的整目录拒读项仍在最终计划中。
            Assert.That(ContainsOrdinal(plan.DenyReadPaths, codexDir), Is.True, ".codex deny entry kept");
            // 剪枝不吞未覆盖命中：前缀兄弟与普通子树内的敏感命中照常配对。
            Assert.That(ContainsOrdinal(plan.DenyReadPaths, sshxHit), Is.True, "sibling-prefix hit paired");
            Assert.That(ContainsOrdinal(plan.DenyReadPaths, plainHit), Is.True, "uncovered hit paired");
        });
    }

    [Test]
    public void CoreScan_UncoveredSubtreeEnumerationError_StillFailsClosed()
    {
        using var root = new TestTempDir();
        // 已覆盖的 .codex 真实存在但绝不下降；未覆盖子树的枚举错误仍使规划失败。
        root.CreateDirectory(".codex");
        root.WriteFile("plain/.env", "SECRET=1");

        IEnumerable<string> Entries(string directory)
        {
            if (directory.Equals(Path.Combine(root.Root, "plain"), StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException($"simulated unreadable listing of '{directory}'");

            return Directory.EnumerateFileSystemEntries(directory);
        }

        var ex = TestAssert.Throws<InvalidOperationException>(() =>
            WindowsSandboxComposer.PlanBaselineReadProtectionCore(root.Root, [], null, null, null,
                                                                  CancellationToken.None,
                                                                  enumerateEntries : Entries));
        Assert.Multiple(() =>
        {
            Assert.That(ex.Message, Does.Contain("could not enumerate").And.Contain(root.Root));
            Assert.That(ex.InnerException, Is.TypeOf<UnauthorizedAccessException>());
        });
    }

    [Test]
    public void CoreScan_TrailingSeparatorDenyDirectory_PrunesDescentAndKeepsDenyEntry()
    {
        using var root = new TestTempDir();
        // 会话存储配置允许目录路径以分隔符结尾：ensureDirectory 以带尾分隔符形态进入拒读集。
        var storeDir       = Path.Combine(root.Root, "store");
        var storeDirectory = storeDir + Path.DirectorySeparatorChar;
        var storeLeak      = Path.GetFullPath(root.WriteFile("store/leak.env", "SECRET=1"));
        var siblingDir     = Path.Combine(root.Root, "store-archive");
        var siblingHit     = Path.GetFullPath(root.WriteFile("store-archive/.env", "SECRET=1"));
        var visited        = new List<string>();

        IEnumerable<string> Entries(string directory)
        {
            visited.Add(directory);
            return Directory.EnumerateFileSystemEntries(directory);
        }

        var plan = WindowsSandboxComposer.PlanBaselineReadProtectionCore(
                                                                root.Root, [], storeDirectory, null, null,
                                                                CancellationToken.None,
                                                                enumerateEntries : Entries);

        Assert.Multiple(() =>
        {
            // 已覆盖目录（带尾分隔符）不会调用其枚举器；根与前缀兄弟照常枚举。
            Assert.That(visited, Does.Contain(root.Root));
            Assert.That(visited, Does.Contain(siblingDir));
            Assert.That(visited, Does.Not.Contain(storeDir), "trailing-separator covered store never enumerated");
            // 整目录拒读项仍保留，且保持进入 payload 的原始形态（规范化只影响查找键）。
            Assert.That(ContainsOrdinal(plan.DenyReadPaths, storeDirectory), Is.True, "whole-directory deny entry kept");
            // 同名前缀兄弟目录的命中照常配对；已覆盖目录内部命中绝不重复进入拒读集。
            Assert.That(ContainsOrdinal(plan.DenyReadPaths, siblingHit), Is.True, "sibling-prefix hit paired");
            Assert.That(ContainsOrdinal(plan.DenyReadPaths, storeLeak), Is.False, "covered inner hit never re-paired");
        });
    }

    [Test]
    public void CoreScan_TrailingSeparatorDenyAncestor_InnerHitNotDuplicated()
    {
        using var root = new TestTempDir();
        // 内部敏感命中真实落盘（复检按存在处理），祖先条目带尾分隔符：配对必须按祖先继承跳过。
        var innerHit       = Path.GetFullPath(root.WriteFile("store/leak.env", "SECRET=1"));
        var storeDirectory = Path.Combine(root.Root, "store") + Path.DirectorySeparatorChar;

        // 注入扫描替代枚举：直接考察配对阶段的祖先去重，不依赖枚举行为。
        var plan = WindowsSandboxComposer.PlanBaselineReadProtectionCore(
                                                                root.Root, [], storeDirectory, null,
                                                                _ => [innerHit]);

        Assert.Multiple(() =>
        {
            Assert.That(ContainsOrdinal(plan.DenyReadPaths, innerHit), Is.False,
                        "inner hit deduped by the trailing-separator ancestor");
            Assert.That(ContainsOrdinal(plan.DenyReadPaths, storeDirectory), Is.True,
                        "whole-directory deny entry kept");
            // 6 条基线（5 个顶层敏感目录段 + .codex）+ 1 条存储目录：计数锁死未重复。
            Assert.That(plan.DenyReadPaths, Has.Count.EqualTo(7), "baseline plus the store entry only");
        });
    }

    [Test]
    public void CoreScan_TimeBudgetExceededDuringPairing_FailsClosedWithoutPartialPlan()
    {
        using var root = new TestTempDir();
        // 两个独立命中：互不为祖先、不在基线敏感段内，配对循环必须逐一处理。
        var firstHit       = Path.GetFullPath(root.WriteFile("one/first.env", "SECRET=1"));
        var secondHit      = Path.GetFullPath(root.WriteFile("two/second.env", "SECRET=1"));
        var rechecked      = new List<string>();
        var budgetExceeded = false;

        IReadOnlyList<string> Scan(string directory) => [firstHit, secondHit];

        TimeSpan Elapsed() => budgetExceeded ? TimeSpan.FromMinutes(1) : TimeSpan.Zero;

        FileAttributes Inspect(string path)
        {
            rechecked.Add(path);
            // 第一个命中实际进入属性复检后时钟才超界：扫描返回时预算充足，配对循环得以进入；
            // 超界必须由循环内的逐命中检查拦截，第二个命中因此不得再被复检。
            budgetExceeded = true;
            return FileAttributes.Normal;
        }

        var ex = TestAssert.Throws<InvalidOperationException>(() =>
            WindowsSandboxComposer.PlanBaselineReadProtectionCore(root.Root, [], null, null, Scan,
                                                                  CancellationToken.None,
                                                                  elapsedSinceStart : Elapsed,
                                                                  scanTimeBudget : TimeSpan.FromSeconds(10),
                                                                  inspectEntry : Inspect));
        Assert.Multiple(() =>
        {
            // 整个规划抛预算异常并整体丢弃：配对阶段超界，绝不返回部分计划。
            Assert.That(ex.Message, Does.Contain("wall-clock time budget").And.Contain("pairing"));
            Assert.That(ex.Message, Does.Match(@"elapsed \d+ ms"));
            // 第二个命中未被复检：证明拦截发生在配对循环内部而非返回前检查。
            Assert.That(rechecked, Has.Count.EqualTo(1), "the second hit must not be re-checked");
            Assert.That(rechecked[0], Is.EqualTo(firstHit).IgnoreCase, "the first hit entered the re-check");
        });
    }

    [Test]
    public void CoreScan_ManyIndependentHits_PairAndDedupAtScale()
    {
        using var root = new TestTempDir();
        const int hitCount = 2000;

        // 大量独立敏感命中（2000 个独立目录 × 目录段命中 + 内嵌命中 + 扩展名命中）：
        // 目录段命中先行入集覆盖子树，内嵌 id_rsa 必须被祖先去重，token.pem 独立配对，
        // 各目录之间不得互相误判覆盖。
        IReadOnlyList<string> Scan(string directory)
        {
            var hits = new List<string>(hitCount * 3);
            for (var index = 0; index < hitCount; index++)
            {
                var hitDirectory = Path.Combine(directory, $"hit-{index:D4}");
                hits.Add(Path.Combine(hitDirectory, ".ssh"));
                hits.Add(Path.Combine(hitDirectory, ".ssh", "id_rsa"));
                hits.Add(Path.Combine(hitDirectory, "token.pem"));
            }

            return hits;
        }

        // 注入属性探测：命中路径为虚构（未落盘），复检按"存在"处理，隔离配对与祖先去重逻辑。
        var plan = WindowsSandboxComposer.PlanBaselineReadProtectionCore(
                                                                root.Root, [], null, null, Scan,
                                                                CancellationToken.None,
                                                                inspectEntry : _ => FileAttributes.Normal);

        Assert.Multiple(() =>
        {
            // 基线 6 条（5 个顶层敏感目录段 + .codex）+ 2000×2 独立命中；内嵌命中被祖先去重。
            Assert.That(plan.DenyReadPaths, Has.Count.EqualTo(6 + hitCount * 2), "baseline plus paired hits");
            Assert.That(plan.DenyReadPaths.Count(path => path.Contains("hit-", StringComparison.Ordinal)),
                        Is.EqualTo(hitCount * 2), "independent hits all pair");
            Assert.That(plan.DenyReadPaths.Any(path => path.EndsWith("id_rsa", StringComparison.OrdinalIgnoreCase) &&
                                                        path.Contains("hit-", StringComparison.Ordinal)),
                        Is.False, "inner hits deduped by deny ancestor");
            Assert.That(ContainsOrdinal(plan.DenyReadPaths, Path.Combine(root.Root, "hit-0000", ".ssh")),
                        Is.True, "first directory hit paired");
            Assert.That(ContainsOrdinal(plan.DenyReadPaths, Path.Combine(root.Root, $"hit-{hitCount - 1:D4}", "token.pem")),
                        Is.True, "last extension hit paired");
        });
    }

    // ---------------------------------------------------------------- 计划相等语义

    [Test]
    public void PlanEquality_IdenticalPlansWithIndependentLists_AreEqualIncludingOperator()
    {
        var first  = new SandboxBaselineReadProtection(new List<string> { @"C:\users\a" },
                                                       new List<string> { @"C:\users\a\.ssh" });
        var second = new SandboxBaselineReadProtection(new List<string> { @"C:\users\a" },
                                                       new List<string> { @"C:\users\a\.ssh" });

        // 独立列表实例、相同内容：序列相等、哈希相同；==/!= 与 Equals 绑定一致（以测试锁死）。
        Assert.Multiple(() =>
        {
            Assert.That(first.Equals(second), Is.True);
            Assert.That(second.Equals(first), Is.True);
            Assert.That(first.Equals((object)second), Is.True);
            Assert.That(first.GetHashCode(), Is.EqualTo(second.GetHashCode()));
            Assert.That(first == second, Is.True, "record == must bind to the overridden Equals");
            Assert.That(first != second, Is.False);
        });
    }

    [Test]
    public void PlanEquality_DifferentRootsDenySetsOrOrder_AreNotEqual()
    {
        var plan = new SandboxBaselineReadProtection([@"C:\users\a"], [@"C:\users\a\.ssh", @"C:\users\a\.kube"]);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Equals(new SandboxBaselineReadProtection([@"C:\users\b"],
                                     [@"C:\users\a\.ssh", @"C:\users\a\.kube"])),
                        Is.False, "read roots differ");
            Assert.That(plan.Equals(new SandboxBaselineReadProtection([@"C:\users\a"],
                                     [@"C:\users\a\.kube", @"C:\users\a\.ssh"])),
                        Is.False, "deny order is significant");
            Assert.That(plan.Equals(new SandboxBaselineReadProtection([@"C:\users\a"], [@"C:\users\a\.ssh"])),
                        Is.False, "deny set differs");
            Assert.That(plan.Equals(new SandboxBaselineReadProtection([@"C:\users\a", @"C:\users\b"],
                                     [@"C:\users\a\.ssh", @"C:\users\a\.kube"])),
                        Is.False, "extra read root");
            Assert.That(plan.Equals(null), Is.False);
        });
    }
}
