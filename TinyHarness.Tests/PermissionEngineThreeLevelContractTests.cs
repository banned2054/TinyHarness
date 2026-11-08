using System.Text.Json.Nodes;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         三级执行体系契约的引擎级行为测试（2026-11-08 冻结契约）：L1 广泛读基线、C2 读保护敏感档、
///         派生运行时写根 L1 化与 C1 不可授权硬拒。夹具用真实 <see cref="ResourceAccessPolicy" />
///         与唯一临时目录；目标文件唯一并前置清理，不依赖陈旧文件的存在性判定。
///     </para>
///     <para>
///         Engine-level behavior tests for the three-level execution contract (frozen
///         2026-11-08): the L1 broad-read baseline, the C2 read-protected sensitive class,
///         derived runtime write roots at Level 1, and C1 ungrantable hard denial. Fixtures
///         use the real <see cref="ResourceAccessPolicy" /> with unique temp directories;
///         target files are unique and cleaned up beforehand, never judged by stale files.
///     </para>
/// </summary>
public class PermissionEngineThreeLevelContractTests
{
    private static ToolPreparation Prep(string capability, IReadOnlyList<string> targets,
                                        string argumentKey = "x", int argumentValue = 1) => new()
    {
        ToolName    = "apply_patch",
        CallId      = "call_1",
        Arguments   = new JsonObject { [argumentKey] = argumentValue },
        Capability  = capability,
        Summary     = "summary",
        TargetPaths = targets
    };

    private static PermissionEngine Engine(TestTempDir workspace, ResourceAccessPolicy? policy = null) =>
        new(workspace.Root, resourcePolicy : policy ?? new ResourceAccessPolicy(workspace.Root));

    private static TestTempDir OutsideDir() => new();

    [Test]
    public void OrdinaryReadOutsideWorkspace_IsLevel1Allowed()
    {
        using var workspace = new TestTempDir();
        using var outside   = OutsideDir();
        var       target    = outside.WriteFile("notes.txt", "plain content");
        var evaluation = Engine(workspace).Evaluate(Prep("filesystem.read", [target]));

        Assert.Multiple(() =>
        {
            Assert.That(evaluation.Decision, Is.EqualTo(PermissionDecision.Allow));
            Assert.That(evaluation.Level, Is.EqualTo(PermissionLevel.Level1));
            Assert.That(evaluation.MissingCapabilities, Is.Empty);
        });
    }

    [Test]
    public void SensitiveLocationRead_OutsideWorkspace_AsksLevel2_WithAnnotation()
    {
        using var workspace = new TestTempDir();
        using var outside   = OutsideDir();
        var       target    = Path.Combine(outside.Root, ".ssh", "id_rsa");
        var evaluation = Engine(workspace).Evaluate(Prep("filesystem.read", [target]));

        Assert.Multiple(() =>
        {
            Assert.That(evaluation.Decision, Is.EqualTo(PermissionDecision.Ask));
            Assert.That(evaluation.Level, Is.EqualTo(PermissionLevel.Level2));
            Assert.That(evaluation.MissingCapabilities, Does.Contain("additional-path-read"));
            Assert.That(evaluation.Reason, Does.Contain("reads outside"));
            Assert.That(evaluation.Reason, Does.Contain("(known sensitive location)"));
        });
    }

    [Test]
    public void SensitiveLocationWrite_OutsideWorkspace_AsksLevel2_WithAnnotation()
    {
        using var workspace = new TestTempDir();
        using var outside   = OutsideDir();
        var       target    = Path.Combine(outside.Root, "credentials.json");
        var evaluation = Engine(workspace).Evaluate(Prep("filesystem.write", [target]));

        Assert.Multiple(() =>
        {
            Assert.That(evaluation.Decision, Is.EqualTo(PermissionDecision.Ask));
            Assert.That(evaluation.Level, Is.EqualTo(PermissionLevel.Level2));
            Assert.That(evaluation.MissingCapabilities, Does.Contain("additional-path-write"));
            Assert.That(evaluation.Reason, Does.Contain("(known sensitive location)"));
        });
    }

    [Test]
    public void OrdinaryOutsideRead_MixedWithSensitiveTarget_TakesStrictest()
    {
        using var workspace = new TestTempDir();
        using var outside   = OutsideDir();
        outside.WriteFile("notes.txt", "plain content");
        var sensitive = Path.Combine(outside.Root, ".ssh", "id_rsa");
        var evaluation = Engine(workspace).Evaluate(Prep("filesystem.read",
                                                         [Path.Combine(outside.Root, "notes.txt"), sensitive]));

        Assert.Multiple(() =>
        {
            Assert.That(evaluation.Decision, Is.EqualTo(PermissionDecision.Ask));
            Assert.That(evaluation.Level, Is.EqualTo(PermissionLevel.Level2));
            Assert.That(evaluation.MissingCapabilities, Does.Contain("additional-path-read"));
            Assert.That(evaluation.Reason, Does.Contain("(known sensitive location)"));
        });
    }

    [Test]
    public void OrdinaryOutsideRead_MixedWithSystemProtectedTarget_IsDenied()
    {
        using var workspace = new TestTempDir();
        using var outside   = OutsideDir();
        var       protected_ = outside.WriteFile("trusted.json", "{}");
        var       policy     = new ResourceAccessPolicy(workspace.Root, permissionSystemRoots : [protected_]);
        outside.WriteFile("notes.txt", "plain content");
        var evaluation = Engine(workspace, policy)
            .Evaluate(Prep("filesystem.read", [Path.Combine(outside.Root, "notes.txt"), protected_]));

        Assert.Multiple(() =>
        {
            Assert.That(evaluation.Decision, Is.EqualTo(PermissionDecision.Deny));
            Assert.That(evaluation.Level, Is.EqualTo(PermissionLevel.None));
        });
    }

    [Test]
    public void SensitiveHitInsideWorkspace_StaysBaselineLevel1()
    {
        using var workspace = new TestTempDir();
        var       target    = workspace.WriteFile(".env", "SECRET=1");
        var       engine    = Engine(workspace);

        foreach (var capability in new[] { "filesystem.read", "filesystem.write" })
        {
            var evaluation = engine.Evaluate(Prep(capability, [target]));
            Assert.Multiple(() =>
            {
                Assert.That(evaluation.Decision, Is.EqualTo(PermissionDecision.Allow), capability);
                Assert.That(evaluation.Level, Is.EqualTo(PermissionLevel.Level1), capability);
            });
        }
    }

    [Test]
    public void SensitiveHitInsideAdditionalRoots_DoesNotEscalate()
    {
        using var workspace = new TestTempDir();
        using var readRoot  = new TestTempDir();
        using var writeRoot = new TestTempDir();
        readRoot.WriteFile(".ssh/id_rsa", "key");
        var writeTarget = Path.Combine(writeRoot.Root, ".env");
        var policy = new ResourceAccessPolicy(workspace.Root,
                                              additionalReadRoots  : [readRoot.Root],
                                              additionalWriteRoots : [writeRoot.Root]);
        var engine = Engine(workspace, policy);

        var read = engine.Evaluate(Prep("filesystem.read", [Path.Combine(readRoot.Root, ".ssh", "id_rsa")]));
        Assert.Multiple(() =>
        {
            Assert.That(read.Decision, Is.EqualTo(PermissionDecision.Allow));
            Assert.That(read.Level, Is.EqualTo(PermissionLevel.Level1));
        });

        // 默认（None）策略：附加写根写入按未确认档逐次审批——敏感注记保留，但拦截来自未确认门禁而非敏感升档。
        var write = engine.Evaluate(Prep("filesystem.write", [writeTarget]));
        Assert.Multiple(() =>
        {
            Assert.That(write.Decision, Is.EqualTo(PermissionDecision.Ask));
            Assert.That(write.Level, Is.EqualTo(PermissionLevel.Level2));
            Assert.That(write.MissingCapabilities, Does.Contain("additional-path-write"));
            Assert.That(write.Reason, Does.Contain("Standing trust is unconfirmed"));
            Assert.That(write.Reason, Does.Contain("(known sensitive location)"));
        });

        // Confirmed：同一写入按显式持久授权放行，附加根内敏感命中仍未发生升档。
        var confirmedPolicy = new ResourceAccessPolicy(workspace.Root,
                                                       additionalReadRoots  : [readRoot.Root],
                                                       additionalWriteRoots : [writeRoot.Root],
                                                       standingTrust        : StandingTrustState.Confirmed);
        var confirmedWrite = Engine(workspace, confirmedPolicy).Evaluate(Prep("filesystem.write", [writeTarget]));
        Assert.Multiple(() =>
        {
            Assert.That(confirmedWrite.Decision, Is.EqualTo(PermissionDecision.Allow));
            Assert.That(confirmedWrite.Level, Is.EqualTo(PermissionLevel.Level1));
        });

        // 分类事实：附加根内敏感命中保留注记，但不改变类别（升档仅限越界位置）。
        Assert.Multiple(() =>
        {
            Assert.That(policy.Classify(Path.Combine(readRoot.Root, ".ssh", "id_rsa")).Class,
                        Is.EqualTo(ResourceClass.AdditionalReadRoot));
            Assert.That(policy.Classify(Path.Combine(readRoot.Root, ".ssh", "id_rsa")).KnownSensitive, Is.True);
            Assert.That(policy.Classify(writeTarget).Class, Is.EqualTo(ResourceClass.AdditionalWriteRoot));
        });
    }

    [Test]
    public void SandboxRuntimeRoot_IsLevel1_ForReadAndWrite()
    {
        using var workspace  = new TestTempDir();
        using var runtimeDir = new TestTempDir();
        runtimeDir.WriteFile("packages/x.dll", "bin");
        var policy = new ResourceAccessPolicy(workspace.Root, sandboxRuntimeRoots : [runtimeDir.Root]);
        var engine = Engine(workspace, policy);
        var file   = Path.Combine(runtimeDir.Root, "packages", "x.dll");
        var created = Path.Combine(runtimeDir.Root, "tools", "jb.exe");

        foreach (var (capability, target) in new[] { ("filesystem.read", file), ("filesystem.write", created) })
        {
            var evaluation = engine.Evaluate(Prep(capability, [target]));
            Assert.Multiple(() =>
            {
                Assert.That(evaluation.Decision, Is.EqualTo(PermissionDecision.Allow), capability);
                Assert.That(evaluation.Level, Is.EqualTo(PermissionLevel.Level1), capability);
                Assert.That(evaluation.MissingCapabilities, Is.Empty, capability);
            });
        }

        Assert.That(policy.Classify(file).Class, Is.EqualTo(ResourceClass.SandboxRuntimeRoot));
    }

    [Test]
    public void SandboxRuntimeRoot_WinsOverAdditionalReadRoot_OnOverlap()
    {
        using var workspace = new TestTempDir();
        using var sharedDir = new TestTempDir();
        sharedDir.WriteFile("x.txt", "content");
        var policy = new ResourceAccessPolicy(workspace.Root,
                                              additionalReadRoots  : [sharedDir.Root],
                                              sandboxRuntimeRoots  : [sharedDir.Root]);

        // 派生运行时根优先于附加读根：OS 侧本就基线可写，弱化类别会保留两层错位。
        Assert.That(policy.Classify(Path.Combine(sharedDir.Root, "x.txt")).Class,
                    Is.EqualTo(ResourceClass.SandboxRuntimeRoot));
    }

    [Test]
    public void SandboxRuntimeRoot_DoesNotExpandOrdinaryOutsideWrites()
    {
        using var workspace  = new TestTempDir();
        using var runtimeDir = new TestTempDir();
        using var outside    = OutsideDir();
        var policy = new ResourceAccessPolicy(workspace.Root, sandboxRuntimeRoots : [runtimeDir.Root]);
        var evaluation = Engine(workspace, policy)
            .Evaluate(Prep("filesystem.write", [Path.Combine(outside.Root, "elsewhere.cs")]));

        Assert.Multiple(() =>
        {
            Assert.That(evaluation.Decision, Is.EqualTo(PermissionDecision.Ask));
            Assert.That(evaluation.Level, Is.EqualTo(PermissionLevel.Level2));
            Assert.That(evaluation.MissingCapabilities, Does.Contain("additional-path-write"));
        });
    }

    // ---- M3 派生运行时写根门控（B7R）：未组合不注入 → harness 目录写 L2 Ask；组合后 L1 自动 ----

    [Test]
    public void DerivedRuntimeRoots_Uncomposed_HarnessWriteAsksLevel2()
    {
        using var workspace = new TestTempDir();
        // 真实派生根：默认沙箱 home（TinyHarness home）下的 nuget-cache 探针路径。
        var settings = new WindowsSandboxSettings();
        var derivedRoots = WindowsSandboxComposer.DeriveRuntimeWriteRoots(settings);
        var nugetCacheProbe = Path.Combine(derivedRoots[0], "probe.txt");
        // 未组合：初始 resourcePolicy 不注入派生写根（B7R 门控），harness 目录回落越界档。
        var policy = new ResourceAccessPolicy(workspace.Root);
        var evaluation = Engine(workspace, policy)
            .Evaluate(Prep("filesystem.write", [nugetCacheProbe]));

        Assert.Multiple(() =>
        {
            Assert.That(evaluation.Decision, Is.EqualTo(PermissionDecision.Ask),
                "未组合时派生写根不得注入：harness 目录写必须逐次审批，绝非 L1 自动。");
            Assert.That(evaluation.Level, Is.EqualTo(PermissionLevel.Level2));
            Assert.That(evaluation.MissingCapabilities, Does.Contain("additional-path-write"));
            Assert.That(policy.Classify(nugetCacheProbe).Class,
                        Is.EqualTo(ResourceClass.OutsideWorkspace),
                        "分类层同样不出现 SandboxRuntimeRoot 档。");
        });
    }

    [Test]
    public void DerivedRuntimeRoots_Composed_HarnessWriteIsLevel1Auto()
    {
        using var workspace = new TestTempDir();
        var settings = new WindowsSandboxSettings();
        var derivedRoots = WindowsSandboxComposer.DeriveRuntimeWriteRoots(settings);
        var nugetCacheProbe = Path.Combine(derivedRoots[0], "probe.txt");
        // 组合后（信任激活）：按 sandbox is not null 注入派生写根，harness 目录写 L1 自动。
        var policy = new ResourceAccessPolicy(workspace.Root, sandboxRuntimeRoots : derivedRoots);
        var evaluation = Engine(workspace, policy)
            .Evaluate(Prep("filesystem.write", [nugetCacheProbe]));

        Assert.Multiple(() =>
        {
            Assert.That(evaluation.Decision, Is.EqualTo(PermissionDecision.Allow),
                "组合后派生写根按 L1 自动放行（两层边界对齐）。");
            Assert.That(evaluation.Level, Is.EqualTo(PermissionLevel.Level1));
            Assert.That(evaluation.MissingCapabilities, Is.Empty);
            Assert.That(policy.Classify(nugetCacheProbe).Class,
                        Is.EqualTo(ResourceClass.SandboxRuntimeRoot));
        });
    }

    // ---- 生产组合形态（修复回归锚点）：home 敏感子路径入 C1 + 派生运行时根注入 ----

    [Test]
    public void ProductionCompositionShape_RuntimeRootsReachLevel1_AndHomeSubpathsStayProtected()
    {
        using var workspace = new TestTempDir();
        using var home      = new SandboxTestHome();
        var settings = new WindowsSandboxSettings { SandboxHome = home.Components.SandboxHome };
        var derivedRoots = WindowsSandboxComposer.DeriveRuntimeWriteRoots(settings);
        // 生产组合形态：C1 携带组件整路径与 home 敏感子路径（凭据目录、deny 簿记、授权映射、
        // 执行组件目录），不再把整 home 放入——整 home 会遮蔽位于其下的运行时根。
        var permissionSystemRoots = new List<string>
        {
            home.Components.SetupExecutablePath, home.Components.RunnerExecutablePath
        };
        permissionSystemRoots.AddRange(home.Components.ProtectedHomeSubpaths);
        var policy = new ResourceAccessPolicy(workspace.Root,
                                              permissionSystemRoots : permissionSystemRoots,
                                              sandboxRuntimeRoots   : derivedRoots);
        var engine = new PermissionEngine(workspace.Root, resourcePolicy : policy);

        // <home>\tmp、<home>\tools、<home>\nuget-cache：分类=SandboxRuntimeRoot，读写 L1 自动放行。
        Assert.That(derivedRoots.Count, Is.EqualTo(3));
        foreach (var root in derivedRoots)
        {
            var probe = Path.Combine(root, "probe.txt");
            Assert.That(policy.Classify(probe).Class, Is.EqualTo(ResourceClass.SandboxRuntimeRoot), root);
            foreach (var capability in new[] { "filesystem.read", "filesystem.write" })
            {
                var evaluation = engine.Evaluate(Prep(capability, [probe]));
                Assert.Multiple(() =>
                {
                    Assert.That(evaluation.Decision, Is.EqualTo(PermissionDecision.Allow),
                                $"{capability}: {root}");
                    Assert.That(evaluation.Level, Is.EqualTo(PermissionLevel.Level1), $"{capability}: {root}");
                    Assert.That(evaluation.MissingCapabilities, Is.Empty, $"{capability}: {root}");
                });
            }
        }

        // 四个 home 敏感子路径仍判 C1 硬拒：分类为保护类，引擎不可授权。
        foreach (var protectedPath in home.Components.ProtectedHomeSubpaths)
        {
            var probe = Path.Combine(protectedPath, "probe.txt");
            Assert.That(policy.Classify(probe).Class,
                        Is.EqualTo(ResourceClass.PermissionSystemProtected), protectedPath);
            var evaluation = engine.Evaluate(Prep("filesystem.write", [probe]));
            Assert.Multiple(() =>
            {
                Assert.That(evaluation.Decision, Is.EqualTo(PermissionDecision.Deny), protectedPath);
                Assert.That(evaluation.Level, Is.EqualTo(PermissionLevel.None), protectedPath);
            });
        }

        // 反例锚点：home 根下的普通位置不再因整 home 入 C1 而硬拒（回落普通越界档）。
        var looseProbe = Path.Combine(home.Components.SandboxHome, "loose.txt");
        Assert.That(policy.Classify(looseProbe).Class, Is.Not.EqualTo(ResourceClass.PermissionSystemProtected));
    }

    [Test]
    public void SystemProtectedResource_DeniesEvenWithEveryGrantScope()
    {
        using var workspace   = new TestTempDir();
        using var outside     = OutsideDir();
        var       protected_  = outside.WriteFile("trusted-config.json", "{}");
        var       policy      = new ResourceAccessPolicy(workspace.Root, permissionSystemRoots : [protected_]);
        var       engine      = Engine(workspace, policy);
        var       preparation = Prep("filesystem.read", [protected_]);

        foreach (var scope in new[] { GrantScope.SingleOperation, GrantScope.Turn, GrantScope.Session })
        {
            engine.Grants.Add(scope, preparation);
            Assert.Multiple(() =>
            {
                Assert.That(engine.Evaluate(preparation).Decision, Is.EqualTo(PermissionDecision.Deny), scope.ToString());
                Assert.That(engine.Evaluate(preparation).Level, Is.EqualTo(PermissionLevel.None), scope.ToString());
            });
        }
    }
}
