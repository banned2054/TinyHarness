using System.Diagnostics;
using System.Text.Json;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         顶层读根基线（替换全 profile 敏感名扫描，计划 §1.4 步骤 1）的定向断言：走真实
///         Composer/Planner 与 <see cref="SandboxIsolationPolicy.CreateSetupPayload" /> 组装链，
///         断言顶层枚举分类（固定名 + 顶层命中 + 无递归）、C1/ensureDirectory/cap_sid 配对、
///         重解析点落点裁决（收窄契约：不可解析/profile 外/被覆盖一律跳过授读）、helper 拒读
///         簿记合并（缺省空集、有效条目并入、无效条目过滤、损坏 fail-closed）、fail-closed 守卫
///         与放宽后的组合校验（兄弟 deny）。
///     </para>
///     <para>
///         Targeted assertions for the top-level read-root baseline (replacing the full-profile
///         sensitive-name scan, plan §1.4 step 1): through the real Composer/Planner and
///         <see cref="SandboxIsolationPolicy.CreateSetupPayload" /> assembly chain, asserting the
///         top-level classification (fixed names + top-level hits + no recursion), the C1 /
///         ensureDirectory / cap_sid pairing, the reparse-point landing verdicts (the narrowed
///         contract: unresolvable, outside-profile, and covered landings all skip the grant), the
///         helper deny-read bookkeeping merge (missing = empty, valid entries merged, invalid
///         entries filtered, corrupt fails closed), the fail-closed guards, and the relaxed
///         composition validation (sibling denies).
///     </para>
/// </summary>
public class SandboxTopLevelReadProtectionTests
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
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The top-level read boundary fixtures need Windows junctions.");
    }

    private static bool ContainsOrdinal(IReadOnlyList<string> paths, string candidate) =>
        paths.Any(path => string.Equals(path, candidate, StringComparison.OrdinalIgnoreCase));

    private static SandboxBaselineReadProtection PlanTopLevel(
        string                                 root,
        IReadOnlyList<string>?                 protectedSecretPaths = null,
        string?                                ensureDirectory      = null,
        WindowsSandboxSettings?                sandboxSettings      = null,
        Func<string, IEnumerable<string>>?     enumerateEntries     = null,
        Func<string, FileAttributes, string?>? resolveLanding       = null,
        Func<string, string?>?                 readDenyBookkeeping  = null) =>
        WindowsSandboxComposer.PlanBaselineReadProtectionTopLevelCore(root, protectedSecretPaths ?? [], ensureDirectory,
                                                                      sandboxSettings, CancellationToken.None,
                                                                      enumerateEntries, resolveLanding,
                                                                      readDenyBookkeeping);

    private static string DenyBookkeepingJson(params (string Sid, string[] Paths)[] principals)
    {
        var principalsMap = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var (sid, paths) in principals) principalsMap[sid] = paths;

        // fork PersistentDenyReadAclState 同源形态：{"principals":{"<SID>":["C:\\path", ...]}}。
        return JsonSerializer.Serialize(new { principals = principalsMap });
    }

    // 簿记状态文件固定位置：<home>\.sandbox\deny_read_acl_state.json（helper 按沙箱 home 写入）。
    private static void WriteDenyBookkeeping(string home, string json)
    {
        Directory.CreateDirectory(Path.Combine(home, ".sandbox"));
        File.WriteAllText(Path.Combine(home, ".sandbox", "deny_read_acl_state.json"), json);
    }

    private static void CreateJunction(string junction, string target)
    {
        using var setup = Process.Start(new ProcessStartInfo
        {
            FileName               = "cmd.exe",
            Arguments              = $"/c mklink /J \"{junction}\" \"{target}\"",
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true
        });
        Assert.That(setup, Is.Not.Null);
        Assert.That(setup!.WaitForExit(30_000), Is.True, "junction creation failed");
    }

    // ---------------------------------------------------------------- 顶层规划

    [Test]
    public void TopLevel_SyntheticProfile_ShapeReadRootsAndDenySet()
    {
        using var root   = new TestTempDir();
        var       docs   = root.CreateDirectory("docs");
        var       pics   = root.CreateDirectory("pics");
        var       readme = root.WriteFile("readme.txt", "hello");
        root.WriteFile("docs/deep/.ssh/id_rsa", "private");
        root.WriteFile("docs/deep/.env", "SECRET=1");
        root.CreateDirectory(".ssh");
        root.WriteFile(".env", "SECRET=1");
        root.WriteFile("server.pem", "cert");
        root.CreateDirectory(".sandbox-secrets");

        var plan = PlanTopLevel(root.Root);

        Assert.Multiple(() =>
        {
            // 读根=普通顶层子项（普通目录与普通文件）；profile 根本身不再是读根。
            Assert.That(plan.ReadRoots, Has.Count.EqualTo(3), "docs, pics, readme.txt only");
            Assert.That(ContainsOrdinal(plan.ReadRoots, docs), Is.True, "docs");
            Assert.That(ContainsOrdinal(plan.ReadRoots, pics), Is.True, "pics");
            Assert.That(ContainsOrdinal(plan.ReadRoots, readme), Is.True, "readme.txt");
            Assert.That(ContainsOrdinal(plan.ReadRoots, root.Root), Is.False, "profile root is no longer a read root");

            // 固定敏感段（5）+ .codex + 顶层 .env + 顶层 .pem = 8 条；已存在的固定名子项不重复入集。
            Assert.That(plan.DenyReadPaths, Has.Count.EqualTo(8), "5 segments + .codex + .env + .pem");
            foreach (var segment in new[] { ".ssh", ".aws", ".docker", ".gnupg", ".kube", ".codex" })
                Assert.That(ContainsOrdinal(plan.DenyReadPaths, Path.Combine(root.Root, segment)), Is.True, segment);
            Assert.That(ContainsOrdinal(plan.DenyReadPaths, Path.Combine(root.Root, ".env")), Is.True,
                        "top-level .env hit");
            Assert.That(ContainsOrdinal(plan.DenyReadPaths, Path.Combine(root.Root, "server.pem")), Is.True,
                        "top-level .pem hit");

            // .sandbox-secrets 沿用现状：排除出读根但不入 deny 集。
            Assert.That(ContainsOrdinal(plan.DenyReadPaths, Path.Combine(root.Root, ".sandbox-secrets")), Is.False,
                        ".sandbox-secrets stays out of the deny set");
            Assert.That(ContainsOrdinal(plan.ReadRoots, Path.Combine(root.Root, ".sandbox-secrets")), Is.False,
                        ".sandbox-secrets is still excluded from the read roots");

            // 无递归：深层嵌套敏感文件绝不进 deny 集（首版语义，不是缺陷）。
            Assert.That(plan.DenyReadPaths.Any(p => p.Contains("deep", StringComparison.OrdinalIgnoreCase)),
                        Is.False, "no recursion below the top level");
        });
    }

    [Test]
    public void TopLevel_MissingProtectedSecretInsideProfile_FailsClosed()
    {
        using var root  = new TestTempDir();
        var       ghost = Path.Combine(root.Root, "th-wp1-ghost-secret.json");

        // 覆盖范围内缺失的 C1 文件不可安全 deny（helper 会物化成目录）：就地 fail-closed。
        var ex = TestAssert.Throws<InvalidOperationException>(() => PlanTopLevel(root.Root, [ghost]));
        Assert.That(ex.Message, Does.Contain("does not exist"));
    }

    [Test]
    public void TopLevel_ProtectedSecrets_PairIntoDenySetAndTopLevelFormExcludesReadRoot()
    {
        using var root   = new TestTempDir();
        var       docs   = root.CreateDirectory("docs");
        // WriteFile 保留子目录段的 "/"：与配对输出的 GetFullPath 规范形态对齐（既有夹具同口径）。
        var       nested = Path.GetFullPath(root.WriteFile("docs/store.json", "{}"));
        root.CreateDirectory("pics");

        // 顶层形态的 C1 路径：拒读 + 排除出读根（否则 deny 等于读根会被组合校验拒绝）。
        var topLevelSecret = root.WriteFile("trust.json", "{}");

        var plan = PlanTopLevel(root.Root, [topLevelSecret, nested]);

        Assert.Multiple(() =>
        {
            Assert.That(ContainsOrdinal(plan.DenyReadPaths, topLevelSecret), Is.True, "top-level secret denied");
            Assert.That(ContainsOrdinal(plan.DenyReadPaths, nested), Is.True, "nested secret denied");
            Assert.That(ContainsOrdinal(plan.ReadRoots, topLevelSecret), Is.False, "top-level secret is no read root");
            Assert.That(ContainsOrdinal(plan.ReadRoots, docs), Is.True,
                        "docs stays a read root (the nested deny sits inside its coverage)");
            Assert.That(ContainsOrdinal(plan.ReadRoots, Path.Combine(root.Root, "pics")), Is.True, "pics");
        });
    }

    [Test]
    public void TopLevel_EnsureDirectory_TopLevelFormExcludedAndNestedFormPaired()
    {
        using var root = new TestTempDir();
        root.CreateDirectory("docs");

        // 顶层形态：先创建、拒读、并经拒读覆盖排除出读根。
        var topLevelStore = Path.Combine(root.Root, "store");
        // 嵌套形态：落在普通读根覆盖内，先创建再配对，读根保持不变。
        var nestedStore = Path.Combine(root.Root, "docs", "sessions");

        var plan       = PlanTopLevel(root.Root, ensureDirectory : topLevelStore);
        var nestedPlan = PlanTopLevel(root.Root, ensureDirectory : nestedStore);

        Assert.Multiple(() =>
        {
            Assert.That(Directory.Exists(topLevelStore), Is.True, "top-level store created");
            Assert.That(ContainsOrdinal(plan.DenyReadPaths, topLevelStore), Is.True, "top-level store denied");
            Assert.That(ContainsOrdinal(plan.ReadRoots, topLevelStore), Is.False, "denied store is no read root");
            Assert.That(Directory.Exists(nestedStore), Is.True, "nested store created");
            Assert.That(ContainsOrdinal(nestedPlan.DenyReadPaths, nestedStore), Is.True, "nested store denied");
            Assert.That(ContainsOrdinal(nestedPlan.ReadRoots, Path.Combine(root.Root, "docs")), Is.True,
                        "docs stays a read root");
        });
    }

    [Test]
    public void TopLevel_SandboxHomeInsideProfile_CapSidDeniedAndHomeStaysReadRoot()
    {
        using var root = new TestTempDir();
        var       home = root.CreateDirectory("sandbox-home");
        root.CreateDirectory("docs");
        var settings = new WindowsSandboxSettings { Enabled = true, SandboxHome = home };

        var plan = PlanTopLevel(root.Root, sandboxSettings : settings);

        var capSid = Path.Combine(home, "cap_sid");
        Assert.Multiple(() =>
        {
            Assert.That(ContainsOrdinal(plan.DenyReadPaths, capSid), Is.True, "cap_sid file-level deny");
            Assert.That(ContainsOrdinal(plan.ReadRoots, home), Is.True, "home itself stays a read root");
            Assert.That(ContainsOrdinal(plan.ReadRoots, Path.Combine(root.Root, "docs")), Is.True, "docs");
            // cap_sid 落在 home 读根覆盖内：组合覆盖校验自然通过。
            Assert.That(plan.ReadRoots.Any(readRoot => Workspace.IsInside(readRoot, capSid)), Is.True,
                        "cap_sid is covered by a read root");
            // home 不排除、不 deny：home 子树内除 cap_sid 外无其他 deny 项。
            Assert.That(plan.DenyReadPaths.Where(p => p.StartsWith(home, StringComparison.OrdinalIgnoreCase))
                            .Any(p => !p.EndsWith("cap_sid", StringComparison.OrdinalIgnoreCase)),
                        Is.False, "no other deny inside the home subtree");
        });
    }

    [Test]
    public void TopLevel_SandboxHomeOutsideProfile_OmitsCapSidEntry()
    {
        using var root        = new TestTempDir();
        using var outsideHome = new TestTempDir();
        var settings = new WindowsSandboxSettings { Enabled = true, SandboxHome = outsideHome.Root };

        var plan = PlanTopLevel(root.Root, sandboxSettings : settings);

        Assert.That(plan.DenyReadPaths.Any(p => p.StartsWith(outsideHome.Root, StringComparison.OrdinalIgnoreCase)),
                    Is.False, "an outside-profile home has no deny entry");
    }

    [Test]
    public void TopLevel_VanishedEntries_AreSkippedWithoutMaterialization()
    {
        using var root      = new TestTempDir();
        var       plain     = root.WriteFile("plain.txt", "x");
        var       ghostFile = Path.Combine(root.Root, "gone.env");
        var       ghostDir  = Path.Combine(root.Root, "gone-dir");

        IEnumerable<string> Entries(string directory)
        {
            yield return ghostFile;
            yield return ghostDir;
            yield return plain;
        }

        var plan = PlanTopLevel(root.Root, enumerateEntries : Entries);

        Assert.Multiple(() =>
        {
            Assert.That(ContainsOrdinal(plan.DenyReadPaths, ghostFile), Is.False, "vanished sensitive file never denied");
            Assert.That(ContainsOrdinal(plan.DenyReadPaths, ghostDir), Is.False, "vanished directory never denied");
            Assert.That(Directory.Exists(ghostFile) || Directory.Exists(ghostDir), Is.False, "no materialization");
            Assert.That(ContainsOrdinal(plan.ReadRoots, plain), Is.True, "the normal entry is still granted");
            Assert.That(ContainsOrdinal(plan.DenyReadPaths, Path.Combine(root.Root, ".ssh")), Is.True,
                        "fixed-name deny entries stay unconditional");
        });
    }

    // ------------------------------------------------- 顶层枚举与检查 fail-closed

    [Test]
    public void TopLevel_EnumerationFailure_FailsClosedWithoutPartialPlan()
    {
        using var root = new TestTempDir();

        // 顶层枚举失败：带具体路径与阶段的异常，绝不回退到授权整个 profile。
        var ex = TestAssert.Throws<InvalidOperationException>(() =>
            PlanTopLevel(root.Root, enumerateEntries : _ =>
                throw new UnauthorizedAccessException("simulated unreadable profile root")));
        Assert.Multiple(() =>
        {
            Assert.That(ex.Message, Does.Contain("could not enumerate").And.Contain(root.Root));
            Assert.That(ex.InnerException, Is.TypeOf<UnauthorizedAccessException>());
        });
    }

    [Test]
    public void TopLevel_UninspectableEntry_FailsClosed()
    {
        SkipOnNonWindows();
        using var root = new TestTempDir();

        // 夹具：DOS 空设备（NUL）在任何目录下都解析为设备条目——名称存在但属性不可读，
        // 与既有扫描夹具同一手法（实测 File.GetAttributes 完全忽略 DACL，ACL deny 无法制造真实异常）。
        var locked = Path.Combine(root.Root, "NUL");
        TestAssert.Throws<IOException>(() => File.GetAttributes(locked));

        IEnumerable<string> Entries(string directory)
        {
            yield return locked;
        }

        var ex = TestAssert.Throws<InvalidOperationException>(() =>
            PlanTopLevel(root.Root, enumerateEntries : Entries));
        Assert.Multiple(() =>
        {
            Assert.That(ex.Message, Does.Contain("could not inspect").And.Contain("NUL"));
            Assert.That(ex.InnerException, Is.InstanceOf<IOException>());
            Assert.That(ex.InnerException is FileNotFoundException or DirectoryNotFoundException, Is.False,
                        "the wrapped error must not be a vanished-entry derivative");
        });
    }

    // ---------------------------------------------------------------- 重解析点落点裁决

    [Test]
    public void TopLevel_ReparseLandingInsideExcludedDirectory_IsNotGranted()
    {
        SkipOnNonWindows();
        using var root = new TestTempDir();
        var       ssh  = root.CreateDirectory(".ssh");
        var       docs = root.CreateDirectory("docs");
        var       junction = Path.Combine(root.Root, "link");
        CreateJunction(junction, ssh);
        try
        {
            // 真实 junction + 真实 BCL 解析：落点在被排除敏感目录内 → 不授读、不下发 deny。
            var plan = PlanTopLevel(root.Root);

            Assert.Multiple(() =>
            {
                Assert.That(ContainsOrdinal(plan.ReadRoots, junction), Is.False, "the excluded landing is never granted");
                Assert.That(ContainsOrdinal(plan.DenyReadPaths, junction), Is.False,
                            "the existing junction gets no deny entry (materialization does not apply)");
                Assert.That(ContainsOrdinal(plan.DenyReadPaths, ssh), Is.True, ".ssh deny entry kept");
                Assert.That(ContainsOrdinal(plan.ReadRoots, docs), Is.True, "docs");
            });
        }
        finally
        {
            Directory.Delete(junction, false);
        }
    }

    [Test]
    public void TopLevel_ReparseLandingOrdinary_IsGrantedAsReadRoot()
    {
        SkipOnNonWindows();
        using var root = new TestTempDir();
        var       docs = root.CreateDirectory("docs");
        var       junction = Path.Combine(root.Root, "link");
        CreateJunction(junction, docs);
        try
        {
            // 落点可解析且不在排除/拒读范围内 → 作为普通读根授予。
            var plan = PlanTopLevel(root.Root);

            Assert.Multiple(() =>
            {
                Assert.That(ContainsOrdinal(plan.ReadRoots, junction), Is.True, "the ordinary landing is granted");
                Assert.That(ContainsOrdinal(plan.ReadRoots, docs), Is.True, "the landing read root is kept");
                Assert.That(ContainsOrdinal(plan.DenyReadPaths, junction), Is.False, "no deny for a granted link");
            });
        }
        finally
        {
            Directory.Delete(junction, false);
        }
    }

    [Test]
    public void TopLevel_ReparseLandingOutsideProfile_IsSkippedWithoutGrant()
    {
        SkipOnNonWindows();
        using var root = new TestTempDir();
        var       docs = root.CreateDirectory("docs");
        var       junction = Path.Combine(root.Root, "link");
        CreateJunction(junction, docs);
        try
        {
            // 注入解析返回 profile 外的合成落点（盘符外形态）：跳过授读，不抛异常、不下发 deny。
            var plan = PlanTopLevel(root.Root, resolveLanding : (_, _) => "Q:\\wp1f-outside-landing");

            Assert.Multiple(() =>
            {
                Assert.That(ContainsOrdinal(plan.ReadRoots, junction), Is.False,
                            "an outside-profile landing is never granted");
                Assert.That(ContainsOrdinal(plan.DenyReadPaths, junction), Is.False,
                            "no deny for a skipped link");
                Assert.That(ContainsOrdinal(plan.ReadRoots, docs), Is.True, "docs stays a read root");
            });
        }
        finally
        {
            Directory.Delete(junction, false);
        }
    }

    [Test]
    public void TopLevel_UnresolvableOrErroringLanding_IsSkippedWithoutGrant()
    {
        SkipOnNonWindows();
        using var root = new TestTempDir();
        var       docs = root.CreateDirectory("docs");
        var linkA = Path.Combine(root.Root, "link-a");
        var linkB = Path.Combine(root.Root, "link-b");
        CreateJunction(linkA, Path.Combine(root.Root, "docs"));
        CreateJunction(linkB, Path.Combine(root.Root, "docs"));
        try
        {
            // 真实 junction + 注入解析：null 落点（云端占位符形态）→ 跳过授读，不抛异常、
            // 不下发 deny（收窄契约：占位符不再拒绝整个基线）。
            var nullPlan = PlanTopLevel(root.Root, enumerateEntries : _ => new[] { docs, linkA },
                                        resolveLanding : (_, _) => null);
            Assert.Multiple(() =>
            {
                Assert.That(ContainsOrdinal(nullPlan.ReadRoots, linkA), Is.False,
                            "an unresolvable landing is never granted");
                Assert.That(ContainsOrdinal(nullPlan.DenyReadPaths, linkA), Is.False,
                            "no deny for a skipped link");
                Assert.That(ContainsOrdinal(nullPlan.ReadRoots, Path.Combine(root.Root, "docs")), Is.True,
                            "docs stays a read root");
            });

            // 解析 IO/权限错 → 同样跳过授读（原 fail-closed 契约撤销）。
            var errorPlan = PlanTopLevel(root.Root, enumerateEntries : _ => new[] { linkB },
                                         resolveLanding : (_, _) =>
                                             throw new UnauthorizedAccessException("simulated denied link resolution"));
            Assert.That(ContainsOrdinal(errorPlan.ReadRoots, linkB), Is.False,
                        "an erroring resolution is never granted");

            // 解析期消失（FNF/DNF）→ 词法跳过，无暴露面。
            var vanishedPlan = PlanTopLevel(root.Root, enumerateEntries : _ => new[] { linkB },
                                            resolveLanding : (_, _) =>
                                                throw new DirectoryNotFoundException("the link vanished mid-resolution"));
            Assert.That(ContainsOrdinal(vanishedPlan.ReadRoots, linkB), Is.False, "a vanished link is skipped");
        }
        finally
        {
            Directory.Delete(linkA, false);
            Directory.Delete(linkB, false);
        }
    }

    // ---------------------------------------------------------------- helper 拒读簿记合并

    [Test]
    public void Bookkeeping_MissingStateFile_ContributesNoEntries()
    {
        using var root = new TestTempDir();
        var       home = root.CreateDirectory("sandbox-home");
        var settings = new WindowsSandboxSettings { Enabled = true, SandboxHome = home };

        // 真实读取路径 + 簿记文件缺失（全新 home 无 deny 历史）：空集贡献，语义集与读根不受影响。
        var plan = PlanTopLevel(root.Root, sandboxSettings : settings);

        Assert.Multiple(() =>
        {
            Assert.That(plan.DenyReadPaths.Any(p => p.StartsWith(Path.Combine(home, ".sandbox"),
                                                                 StringComparison.OrdinalIgnoreCase)),
                        Is.False, "no bookkeeping entries without the state file");
            Assert.That(ContainsOrdinal(plan.DenyReadPaths, Path.Combine(home, "cap_sid")), Is.True, "cap_sid");
            Assert.That(ContainsOrdinal(plan.ReadRoots, home), Is.True, "home stays a read root");
        });
    }

    [Test]
    public void Bookkeeping_ValidEntries_MergedIntoFinalDenySet()
    {
        using var root = new TestTempDir();
        var       home = root.CreateDirectory("sandbox-home");
        var       docs = root.CreateDirectory("docs");
        var settings = new WindowsSandboxSettings { Enabled = true, SandboxHome = home };
        // 兄弟条目（与读根同父，被排除出读根的顶层项形态）与覆盖内条目（落在 docs 覆盖内）均可并入。
        var legacySibling = Path.Combine(root.Root, "legacy-scan-hit");
        var legacyNested  = Path.Combine(root.Root, "docs", "legacy-nested-hit");

        WriteDenyBookkeeping(home,
                             DenyBookkeepingJson(("S-1-5-21-9001", [legacySibling, legacyNested])));
        var plan = PlanTopLevel(root.Root, sandboxSettings : settings);

        Assert.Multiple(() =>
        {
            Assert.That(ContainsOrdinal(plan.DenyReadPaths, legacySibling), Is.True,
                        "a sibling bookkeeping entry merges");
            Assert.That(ContainsOrdinal(plan.DenyReadPaths, legacyNested), Is.True,
                        "an entry covered by a read root merges");
            Assert.That(ContainsOrdinal(plan.ReadRoots, docs), Is.True, "docs stays a read root");
        });
    }

    [Test]
    public void Bookkeeping_InvalidEntries_FilteredFromMerge()
    {
        using var root = new TestTempDir();
        var       home = root.CreateDirectory("sandbox-home");
        var       docs = root.CreateDirectory("docs");
        var settings = new WindowsSandboxSettings { Enabled = true, SandboxHome = home };
        // 无效条目：既不落在任何读根覆盖内也与读根非兄弟（深层路径）+ 等于读根本身；有效兄弟作对照。
        var deepHit        = Path.Combine(root.Root, "sub", "deep", "legacy-hit");
        var asReadRoot     = Path.Combine(root.Root, "docs");
        var legacySibling  = Path.Combine(root.Root, "legacy-scan-hit");

        WriteDenyBookkeeping(home,
                             DenyBookkeepingJson(("S-1-5-21-9001", [deepHit, asReadRoot, legacySibling])));
        var plan = PlanTopLevel(root.Root, sandboxSettings : settings);

        Assert.Multiple(() =>
        {
            Assert.That(ContainsOrdinal(plan.DenyReadPaths, deepHit), Is.False,
                        "an entry neither covered nor sibling is filtered");
            Assert.That(ContainsOrdinal(plan.DenyReadPaths, asReadRoot), Is.False,
                        "an entry equal to a read root itself is filtered");
            Assert.That(ContainsOrdinal(plan.DenyReadPaths, legacySibling), Is.True,
                        "the valid sibling still merges (the CLI restores filtered entries)");
            Assert.That(ContainsOrdinal(plan.ReadRoots, docs), Is.True, "docs stays a read root");
        });
    }

    [Test]
    public void Bookkeeping_CorruptJson_FailsClosed()
    {
        using var root = new TestTempDir();
        var       home = root.CreateDirectory("sandbox-home");
        var settings = new WindowsSandboxSettings { Enabled = true, SandboxHome = home };

        // 簿记损坏（JSON 解析失败）：fail-closed 带上下文，协调失败绝不静默收缩 CLI 条目。
        WriteDenyBookkeeping(home, "{ not json at all");
        var ex = TestAssert.Throws<InvalidOperationException>(() => PlanTopLevel(root.Root, sandboxSettings : settings));
        Assert.Multiple(() =>
        {
            Assert.That(ex.Message, Does.Contain("deny-read bookkeeping").And.Contain("deny_read_acl_state.json"));
            Assert.That(ex.InnerException, Is.InstanceOf<JsonException>());
        });
    }

    [Test]
    public void Bookkeeping_UnreadableInjectedRead_FailsClosed()
    {
        using var root = new TestTempDir();
        var       home = root.CreateDirectory("sandbox-home");
        var settings = new WindowsSandboxSettings { Enabled = true, SandboxHome = home };

        // 注入读取抛 IO/权限错：fail-closed，原异常保留为 inner exception（无需磁盘夹具）。
        var ex = TestAssert.Throws<InvalidOperationException>(() =>
            PlanTopLevel(root.Root, sandboxSettings : settings,
                         readDenyBookkeeping : _ =>
                             throw new UnauthorizedAccessException("simulated denied bookkeeping read")));
        Assert.Multiple(() =>
        {
            Assert.That(ex.Message, Does.Contain("deny-read bookkeeping"));
            Assert.That(ex.InnerException, Is.TypeOf<UnauthorizedAccessException>());
        });
    }

    [Test]
    public void Bookkeeping_SemanticOverlap_Deduplicated()
    {
        using var root = new TestTempDir();
        var       home = root.CreateDirectory("sandbox-home");
        var settings = new WindowsSandboxSettings { Enabled = true, SandboxHome = home };
        // 固定敏感段已在语义集内：簿记重叠条目去重为一条。
        var ssh = Path.Combine(root.Root, ".ssh");

        WriteDenyBookkeeping(home,
                             DenyBookkeepingJson(("S-1-5-21-9001", [ssh])));
        var plan = PlanTopLevel(root.Root, sandboxSettings : settings);

        Assert.That(plan.DenyReadPaths.Count(p => string.Equals(p, ssh, StringComparison.OrdinalIgnoreCase)),
                    Is.EqualTo(1), "the semantic entry and the bookkeeping entry dedup to one");
    }

    [Test]
    public void Bookkeeping_ReadRootsUnaffected()
    {
        using var root = new TestTempDir();
        var       home = root.CreateDirectory("sandbox-home");
        var       docs = root.CreateDirectory("docs");
        var settings = new WindowsSandboxSettings { Enabled = true, SandboxHome = home };
        var legacySibling = Path.Combine(root.Root, "legacy-scan-hit");

        WriteDenyBookkeeping(home,
                             DenyBookkeepingJson(("S-1-5-21-9001", [legacySibling])));
        var plan  = PlanTopLevel(root.Root, sandboxSettings : settings);
        // 同配置注入空簿记：读根清单必须与并入簿记的清单完全一致。
        var plain = PlanTopLevel(root.Root, sandboxSettings : settings, readDenyBookkeeping : _ => null);

        Assert.Multiple(() =>
        {
            Assert.That(plan.ReadRoots, Is.EqualTo(plain.ReadRoots),
                        "read roots identical with and without bookkeeping");
            Assert.That(ContainsOrdinal(plan.DenyReadPaths, legacySibling), Is.True,
                        "control: the bookkeeping entry merged");
        });
    }

    // ---------------------------------------------------------------- 组合校验（兄弟 deny 放宽）

    [Test]
    public void Compose_SiblingDenyReadPath_PairsWithoutReadCoverage()
    {
        SkipOnNonWindows();
        using var home      = new TestTempDir();
        using var scope     = new TestTempDir();
        using var workspace = new TestTempDir();
        var       readRoot  = Directory.CreateDirectory(Path.Combine(scope.Root, "docs")).FullName;
        var       deny      = Path.Combine(scope.Root, ".ssh");
        var       protection = new SandboxBaselineReadProtection([readRoot], [deny]);

        // deny 与读根同父（已被排除出读根的顶层项）：放宽后合法配对。
        var composed = WindowsSandboxComposer.Compose(TrustedSettingsIn(home), workspace.Root, null, null, protection);

        Assert.Multiple(() =>
        {
            Assert.That(composed.Policy.BaselineReadRoots, Is.EqualTo(protection.ReadRoots));
            Assert.That(composed.Policy.DenyReadPaths, Is.EqualTo(protection.DenyReadPaths));
            var payload = composed.Policy.CreateSetupPayload(composed.Components, workspace.Root, "runner", true, null);
            Assert.That(payload.ReadRoots.Any(p => string.Equals(p, readRoot, StringComparison.OrdinalIgnoreCase)),
                        Is.True, "read_roots");
            Assert.That(payload.DenyReadPaths.Any(p => string.Equals(p, deny, StringComparison.OrdinalIgnoreCase)),
                        Is.True, "deny_read_paths");
        });
    }

    [Test]
    public void Compose_MultipleReadRoots_PayloadAndIdentityCarryEveryRoot()
    {
        SkipOnNonWindows();
        using var home      = new TestTempDir();
        using var scope     = new TestTempDir();
        using var workspace = new TestTempDir();
        var       docs      = Path.Combine(scope.Root, "docs");
        var       pics      = Path.Combine(scope.Root, "pics");
        var       deny      = Path.Combine(scope.Root, ".ssh");
        var       protection = new SandboxBaselineReadProtection([docs, pics], [deny]);

        var composed       = WindowsSandboxComposer.Compose(TrustedSettingsIn(home), workspace.Root, null, null,
                                                            protection);
        var payload        = composed.Policy.CreateSetupPayload(composed.Components, workspace.Root, "runner", true, null);
        var identityLines  = composed.ExecutionPolicy.PolicyIdentity.Split('\n');

        Assert.Multiple(() =>
        {
            Assert.That(composed.Policy.BaselineReadRoots, Is.EqualTo(protection.ReadRoots), "both roots enter the policy");
            Assert.That(payload.ReadRoots.Any(p => string.Equals(p, docs, StringComparison.OrdinalIgnoreCase)) &&
                        payload.ReadRoots.Any(p => string.Equals(p, pics, StringComparison.OrdinalIgnoreCase)),
                        Is.True, "read_roots carry every baseline root");
            // 策略身份 v8 按多读根列表入串（AppendRoots 全列表，规范化键）。
            var canonicalDocs = SandboxCapabilitySidStore.CanonicalRootKey(docs);
            var canonicalPics = SandboxCapabilitySidStore.CanonicalRootKey(pics);
            var canonicalDeny = SandboxCapabilitySidStore.CanonicalRootKey(deny);
            Assert.That(identityLines.Single(line => line.StartsWith("baselineReadRoots=", StringComparison.Ordinal))
                                      .Contains(canonicalDocs, StringComparison.Ordinal) &&
                        identityLines.Single(line => line.StartsWith("baselineReadRoots=", StringComparison.Ordinal))
                                      .Contains(canonicalPics, StringComparison.Ordinal),
                        Is.True, "identity baselineReadRoots carries both roots");
            Assert.That(identityLines.Where(line => line.StartsWith("denyReadPaths=", StringComparison.Ordinal))
                                     .Any(line => line.Contains(canonicalDeny, StringComparison.Ordinal)),
                        Is.True, "identity denyReadPaths");
        });
    }

    [Test]
    public void Compose_DenyReadPathAsReadRootItself_MultiRootForm_FailsClosed()
    {
        SkipOnNonWindows();
        using var home      = new TestTempDir();
        using var scope     = new TestTempDir();
        using var workspace = new TestTempDir();
        var docs = Path.Combine(scope.Root, "docs");
        var pics = Path.Combine(scope.Root, "pics");

        var ex = TestAssert.Throws<InvalidOperationException>(() =>
            WindowsSandboxComposer.Compose(TrustedSettingsIn(home), workspace.Root, null, null,
                                           new SandboxBaselineReadProtection([docs, pics], [docs])));
        Assert.That(ex.Message, Does.Contain("must not be a baseline read root itself"));
    }

    [Test]
    public void Compose_DenyReadPathNeitherCoveredNorSibling_FailsClosed()
    {
        SkipOnNonWindows();
        using var home      = new TestTempDir();
        using var scope     = new TestTempDir();
        using var workspace = new TestTempDir();
        var readRoot = Path.Combine(scope.Root, "docs");
        var deepDeny = Path.Combine(scope.Root, "sub", "deep");

        var ex = TestAssert.Throws<InvalidOperationException>(() =>
            WindowsSandboxComposer.Compose(TrustedSettingsIn(home), workspace.Root, null, null,
                                           new SandboxBaselineReadProtection([readRoot], [deepDeny])));
        Assert.That(ex.Message, Does.Contain("not covered by any baseline read root").And.Contain("sibling"));
    }

    [Test]
    public void Compose_DenyReadPathCoveringSandboxHome_MultiRootForm_FailsClosed()
    {
        SkipOnNonWindows();
        using var home      = new TestTempDir();
        using var workspace = new TestTempDir();
        var settings   = TrustedSettingsIn(home);
        var secondRoot = Path.Combine(ProfileRoot, "th-wp1-second-read-root");

        // deny 覆盖沙箱 home（多读根形态下仍拒绝）：home 在 %TEMP%（profile 内），第一条读根覆盖它。
        var protection = new SandboxBaselineReadProtection([ProfileRoot, secondRoot], [home.Root]);

        var ex = TestAssert.Throws<InvalidOperationException>(() =>
            WindowsSandboxComposer.Compose(settings, workspace.Root, null, null, protection));
        Assert.That(ex.Message, Does.Contain("covers the sandbox runtime path"));
    }

    [Test]
    public void Compose_TopLevelPlanFromSyntheticProfile_PayloadCarriesPairing()
    {
        SkipOnNonWindows();
        using var root      = new TestTempDir();
        using var workspace = new TestTempDir();
        var       home      = root.CreateDirectory("sandbox-home");
        var       settings  = new WindowsSandboxSettings
        {
            Enabled              = true,
            SetupExecutablePath  = Path.Combine(home, "setup.exe"),
            RunnerExecutablePath = Path.Combine(home, "runner.exe"),
            SandboxHome          = home
        };
        File.WriteAllText(settings.SetupExecutablePath, "stub");
        File.WriteAllText(settings.RunnerExecutablePath, "stub");
        var docs = root.CreateDirectory("docs");

        // 顶层规划 → 组合全链：兄弟 deny（固定名）+ 覆盖内 deny（cap_sid）同时成立。
        var plan     = PlanTopLevel(root.Root, sandboxSettings : settings);
        var composed = WindowsSandboxComposer.Compose(settings, workspace.Root, null, null, plan);

        Assert.Multiple(() =>
        {
            Assert.That(composed.Policy.BaselineReadRoots, Is.EqualTo(plan.ReadRoots));
            Assert.That(composed.Policy.DenyReadPaths, Is.EqualTo(plan.DenyReadPaths));
            var payload = composed.Policy.CreateSetupPayload(composed.Components, workspace.Root, "runner", true, null);
            Assert.That(payload.ReadRoots.Any(p => string.Equals(p, docs, StringComparison.OrdinalIgnoreCase)),
                        Is.True, "docs read root");
            Assert.That(payload.ReadRoots.Any(p => string.Equals(p, home, StringComparison.OrdinalIgnoreCase)),
                        Is.True, "home read root");
            Assert.That(payload.DenyReadPaths.Any(p => p.EndsWith(".codex", StringComparison.OrdinalIgnoreCase)),
                        Is.True, ".codex sibling deny");
            Assert.That(payload.DenyReadPaths.Any(p => p.EndsWith("cap_sid", StringComparison.OrdinalIgnoreCase)),
                        Is.True, "cap_sid deny inside home coverage");
        });
    }
}
