using System.Text.Json;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         C2 获批读授权通道的应用侧单元测试：authorized 集分流纯函数、payload v7 语义与 JSON
///         形态、C1 恒拒断言、C1 可信名单文件写入与策略身份的授权读段。全部离线，不触碰机器
///         账户、ACL 或真实 helper。
///     </para>
///     <para>
///         Application-side unit tests for the C2 approved-read authorization channel: the
///         authorized-set splitter, payload v7 semantics and JSON shape, the C1 constant-refusal
///         assertion, the C1 trusted-list file write, and the policy identity's authorized-read
///         section. Everything runs offline without touching machine accounts, ACLs, or a real helper.
///     </para>
/// </summary>
public class SandboxAuthorizedReadTests
{
    // ---------------------------------------------------------------- authorized 集分流

    [Test]
    public void Split_MixedGrantRoots_RoutesOnlyDenyCoveredRootsIntoAuthorizedSet()
    {
        using var dir     = Fixtures();
        var       denyDir = dir.CreateDirectory("sensitive");
        // 与 deny 集不冲突的普通获批根。
        var plainRoot = dir.CreateDirectory("grants/plain");
        // deny 条目的父目录：组通道本可读，保持组通道。
        var parentRoot = dir.Root;

        var split = SandboxAuthorizedReads.Split([denyDir], [denyDir, plainRoot, parentRoot]);

        Assert.Multiple(() =>
        {
            Assert.That(split.AuthorizedReadRoots, Is.EqualTo([Path.GetFullPath(denyDir)]));
            Assert.That(split.GroupReadRoots, Is.EqualTo([Path.GetFullPath(plainRoot), Path.GetFullPath(parentRoot)]));
            Assert.That(split.HasAuthorizedReads, Is.True);
        });
    }

    [Test]
    public void Split_RootInsideDenyEntry_GoesAuthorized()
    {
        using var dir     = Fixtures();
        var       denyDir = dir.CreateDirectory("sensitive");
        var inner       = dir.CreateDirectory(Path.Combine("sensitive", "keys"));

        var split = SandboxAuthorizedReads.Split([denyDir], [inner]);

        Assert.That(split.AuthorizedReadRoots, Is.EqualTo([Path.GetFullPath(inner)]));
        Assert.That(split.GroupReadRoots, Is.Empty);
    }

    [Test]
    public void Split_EmptyDenySet_KeepsWholeGrantOnGroupChannel()
    {
        using var dir       = Fixtures();
        var       plainRoot = dir.CreateDirectory("grants/plain");

        var split = SandboxAuthorizedReads.Split([], [plainRoot]);

        Assert.Multiple(() =>
        {
            Assert.That(split.HasAuthorizedReads, Is.False);
            Assert.That(split.GroupReadRoots, Is.EqualTo([Path.GetFullPath(plainRoot)]));
        });
    }

    // ---------------------------------------------------------------- payload v7 语义与 JSON 形态

    [Test]
    public void CreateSetupPayload_DenyConflictingGrantRoots_MoveToAuthorizedReadPathsAtVersion7()
    {
        using var dir     = Fixtures();
        var       denyDir = dir.CreateDirectory("sensitive");
        var       plain   = dir.CreateDirectory("grants/plain");
        var       policy  = Policy(dir, [denyDir]);
        var       scope   = new SandboxGrantScope
        {
            ReadRoots          = [denyDir, plain],
            WriteRoots         = [],
            MetadataWritePaths = []
        };

        var payload = policy.CreateSetupPayload(Components(dir), dir.Root, "test-user", true, scope);

        Assert.Multiple(() =>
        {
            Assert.That(payload.Version, Is.EqualTo(WindowsSandboxComponents.AuthorizedSetupVersion));
            Assert.That(payload.AuthorizedReadPaths, Is.EqualTo([Path.GetFullPath(denyDir)]));
            // 不冲突的获批根维持 read_roots 组通道；deny 冲突根绝不并入组通道。
            Assert.That(payload.ReadRoots, Does.Contain(Path.GetFullPath(plain)));
            Assert.That(payload.ReadRoots, Does.Not.Contain(Path.GetFullPath(denyDir)));
            // deny_read_paths 保持组合期冻结不动。
            Assert.That(payload.DenyReadPaths, Is.EqualTo([Path.GetFullPath(denyDir)]));
        });

        var json = JsonSerializer.Serialize(payload, WindowsSandboxJsonContext.Default.SandboxSetupPayload);
        TestAssert.Contains("\"version\":7", json, StringComparison.Ordinal);
        TestAssert.Contains("\"authorized_read_paths\"", json, StringComparison.Ordinal);
    }

    [Test]
    public void CreateSetupPayload_NonConflictingGrant_KeepsNormalVersionWithoutAuthorizedField()
    {
        using var dir     = Fixtures();
        var       denyDir = dir.CreateDirectory("sensitive");
        var       plain   = dir.CreateDirectory("grants/plain");
        var       policy  = Policy(dir, [denyDir]);
        var       scope   = new SandboxGrantScope
        {
            ReadRoots          = [plain],
            WriteRoots         = [],
            MetadataWritePaths = []
        };

        var payload = policy.CreateSetupPayload(Components(dir), dir.Root, "test-user", true, scope);

        Assert.Multiple(() =>
        {
            Assert.That(payload.Version, Is.EqualTo(SandboxSetupPayload.RequiredVersion));
            Assert.That(payload.AuthorizedReadPaths, Is.Null);
            Assert.That(payload.ReadRoots, Does.Contain(Path.GetFullPath(plain)));
            Assert.That(payload.DenyReadPaths, Is.EqualTo([Path.GetFullPath(denyDir)]));
        });

        var json = JsonSerializer.Serialize(payload, WindowsSandboxJsonContext.Default.SandboxSetupPayload);
        TestAssert.Contains("\"version\":5", json, StringComparison.Ordinal);
        TestAssert.DoesNotContain("authorized_read_paths", json, StringComparison.Ordinal);
    }

    [Test]
    public void CreateSetupPayload_GrantRootCoveringDenyEntries_StaysOnGroupChannel()
    {
        using var dir     = Fixtures();
        var       denyDir = dir.CreateDirectory("sensitive");
        var       policy  = Policy(dir, [denyDir]);
        var       scope   = new SandboxGrantScope
        {
            ReadRoots          = [dir.Root],
            WriteRoots         = [],
            MetadataWritePaths = []
        };

        var payload = policy.CreateSetupPayload(Components(dir), dir.Root, "test-user", true, scope);

        Assert.Multiple(() =>
        {
            // 父根在组通道本可读：保持组通道，deny 子树仍受保护，授权通道不被牵动。
            Assert.That(payload.Version, Is.EqualTo(SandboxSetupPayload.RequiredVersion));
            Assert.That(payload.AuthorizedReadPaths, Is.Null);
            Assert.That(payload.ReadRoots, Does.Contain(Path.GetFullPath(dir.Root)));
        });
    }

    [Test]
    public void CreateSetupPayload_RestorePayload_CarriesV7WithEmptyAuthorizedArray()
    {
        using var dir     = Fixtures();
        var       denyDir = dir.CreateDirectory("sensitive");
        var       policy  = Policy(dir, [denyDir]);
        var       scope   = new SandboxGrantScope
        {
            ReadRoots          = [denyDir],
            WriteRoots         = [],
            MetadataWritePaths = []
        };

        var authorized = policy.CreateSetupPayload(Components(dir), dir.Root, "test-user", true, scope);
        var restore    = authorized with { AuthorizedReadPaths = [] };

        Assert.Multiple(() =>
        {
            Assert.That(restore.Version, Is.EqualTo(WindowsSandboxComponents.AuthorizedSetupVersion));
            Assert.That(restore.AuthorizedReadPaths, Is.Not.Null.And.Empty);
        });

        var json = JsonSerializer.Serialize(restore, WindowsSandboxJsonContext.Default.SandboxSetupPayload);
        TestAssert.Contains("\"version\":7", json, StringComparison.Ordinal);
        TestAssert.Contains("\"authorized_read_paths\":[]", json, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- C1 恒拒断言

    [Test]
    public void Prepare_AuthorizedReadOverlappingPermissionSystemPath_IsRefusedVisibly()
    {
        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        // C1 实例路径同时是 deny 条目：分流必然把它送进 authorized 集，构造处必须恒拒。
        var protectedPath = workspace.CreateDirectory("protected-session-store");
        var policy = SandboxPolicyResolver.Resolve(SandboxPolicyKind.WorkspaceWrite,
                                                   home.Components.SandboxHome,
                                                   new Dictionary<string, string>
                                                   {
                                                       ["TEMP"] = home.Components.SandboxHome
                                                   }) with
        {
            DenyReadPaths = [protectedPath]
        };
        var source = new SnapshotSource(new ActiveCapabilitySnapshot([protectedPath], [], []));
        var tool = new ShellTool(new Workspace(workspace.Root), processBackend : new FakeProcessExecutionBackend(),
                                 executionPolicy : SandboxPolicy(), activeCapabilities : source,
                                 grantContext : new SandboxGrantContext(policy, home.Components, [protectedPath]));

        var ex = TestAssert.Throws<InvalidOperationException>(() =>
                   tool.Prepare(Call("shell", new { mode = "direct", executable = "cmd.exe" })));
        Assert.That(ex!.Message, Does.Contain("permission-system protected path"));
        Assert.That(ex.Message, Does.Contain("authorized-read channel"));
    }

    // ---------------------------------------------------------------- 策略身份的授权读段

    [Test]
    public void PolicyIdentity_AuthorizedExecution_DiffersFromNormalAndOtherAuthorizedSets()
    {
        using var dir     = Fixtures();
        var       denyA   = dir.CreateDirectory("sensitive-a");
        var       denyB   = dir.CreateDirectory("sensitive-b");
        var       policy  = Policy(dir, [denyA, denyB]);
        var       scopeA  = new SandboxGrantScope
        {
            ReadRoots          = [denyA],
            WriteRoots         = [],
            MetadataWritePaths = []
        };
        var scopeBoth = new SandboxGrantScope
        {
            ReadRoots          = [denyA, denyB],
            WriteRoots         = [],
            MetadataWritePaths = []
        };

        var baseline = WindowsSandboxComposer.BuildPolicyIdentity(policy, EmptyEnv(), EmptyEnv());
        var identityA = WindowsSandboxComposer.AppendAuthorizedReadSection(
            WindowsSandboxComposer.AppendGrantSection(baseline, scopeA),
            SandboxAuthorizedReads.Split(policy.DenyReadPaths, scopeA.ReadRoots).AuthorizedReadRoots);
        var identityBoth = WindowsSandboxComposer.AppendAuthorizedReadSection(
            WindowsSandboxComposer.AppendGrantSection(baseline, scopeBoth),
            SandboxAuthorizedReads.Split(policy.DenyReadPaths, scopeBoth.ReadRoots).AuthorizedReadRoots);

        Assert.Multiple(() =>
        {
            // 授权执行与普通执行身份必不相同，且不同授权集互不相同（授权/复用键不得混用）。
            Assert.That(identityA, Is.Not.EqualTo(baseline));
            Assert.That(identityA, Does.Contain("authorizedReadPaths="));
            TestAssert.Contains(SandboxCapabilitySidStore.CanonicalRootKey(denyA), identityA,
                                StringComparison.Ordinal);
            Assert.That(identityBoth, Is.Not.EqualTo(identityA));
            Assert.That(identityBoth, Does.Contain(SandboxCapabilitySidStore.CanonicalRootKey(denyB)));
        });
    }

    [Test]
    public void Prepare_NonConflictingGrantReads_KeepGrantSectionWithoutAuthorizedReadPaths()
    {
        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        var       plain     = workspace.CreateDirectory("grants/plain");
        var       policy = SandboxPolicyResolver.Resolve(SandboxPolicyKind.WorkspaceWrite,
                                                         home.Components.SandboxHome,
                                                         new Dictionary<string, string>
                                                         {
                                                             ["TEMP"] = home.Components.SandboxHome
                                                         });
        var source = new SnapshotSource(new ActiveCapabilitySnapshot([plain], [], []));
        var tool = new ShellTool(new Workspace(workspace.Root), processBackend : new FakeProcessExecutionBackend(),
                                 executionPolicy : SandboxPolicy(), activeCapabilities : source,
                                 grantContext : new SandboxGrantContext(policy, home.Components));

        var preparation = tool.Prepare(Call("shell", new { mode = "direct", executable = "cmd.exe" }));

        Assert.Multiple(() =>
        {
            // 无 deny 冲突的获批读维持既有组通道：身份只有 grant 段，无授权读段。
            Assert.That(preparation.ExecutionPolicy, Does.Contain("grantReadRoots="));
            Assert.That(preparation.ExecutionPolicy, Does.Not.Contain("authorizedReadPaths="));
        });
    }

    // ---------------------------------------------------------------- C1 可信名单文件

    [Test]
    public void ComposeManifest_MergesInstancePathsWithSandboxIntegrityPaths()
    {
        using var dir = new TestTempDir();
        var trustPath        = Path.Combine(dir.Root, "workspace-trust.json");
        var userConfigPath   = Path.Combine(dir.Root, "user-config.json");
        var sessionStorePath = dir.CreateDirectory("session-store");

        var manifest = SandboxProtectedPathsStore.ComposeManifest(Components(dir),
                                                                  [trustPath, userConfigPath, sessionStorePath]);

        Assert.Multiple(() =>
        {
            foreach (var expected in new[]
                     {
                         Components(dir).SandboxHome, Components(dir).SetupExecutablePath,
                         Components(dir).RunnerExecutablePath, Components(dir).UsersFilePath,
                         Components(dir).CapabilitySidPath, Components(dir).CredentialsDirectory,
                         Components(dir).SandboxDirectory, Components(dir).SandboxBinDirectory,
                         trustPath, userConfigPath, sessionStorePath
                     })
                Assert.That(manifest.Any(path => string.Equals(path, Path.GetFullPath(expected),
                                                                StringComparison.OrdinalIgnoreCase)),
                            Is.True, expected);
        });
    }

    [Test]
    public void WriteIfChanged_WritesCanonicalArray_SkipsIdenticalContent_RewritesOnChange()
    {
        using var dir        = new TestTempDir();
        var       components = Components(dir);
        var first  = new List<string> { Path.Combine(dir.Root, "one") };
        var second = new List<string> { Path.Combine(dir.Root, "one"), Path.Combine(dir.Root, "two") };

        SandboxProtectedPathsStore.WriteIfChanged(components, first);
        var path = SandboxProtectedPathsStore.ResolvePath(components);
        var written = File.ReadAllText(path);
        Assert.That(JsonSerializer.Deserialize(written, WindowsSandboxJsonContext.Default.StringArray),
                    Is.EqualTo(first.ToArray()));

        // 内容一致不覆写；成分变化（新增条目）后重写为新数组。
        SandboxProtectedPathsStore.WriteIfChanged(components, first);
        Assert.That(File.ReadAllText(path), Is.EqualTo(written));
        SandboxProtectedPathsStore.WriteIfChanged(components, second);
        Assert.That(JsonSerializer.Deserialize(File.ReadAllText(path), WindowsSandboxJsonContext.Default.StringArray),
                    Is.EqualTo(second.ToArray()));
    }

    [Test]
    public void WriteIfChanged_FailingWrite_FailsCompositionClosed()
    {
        using var dir        = new TestTempDir();
        var       components = Components(dir);
        // 凭据目录位置被一个文件占用：写入必然失败，组合必须 fail closed。
        var blockingFile = dir.WriteFile("home/.sandbox-secrets", "occupied");

        var ex = TestAssert.Throws<InvalidOperationException>(() =>
                   SandboxProtectedPathsStore.WriteIfChanged(components, ["x"]));
        Assert.That(ex!.Message, Does.Contain("C1 protected-path manifest"));
        Assert.That(File.Exists(blockingFile), Is.True);
    }

    [Test]
    public void Compose_WritesProtectedPathsManifestForTheHelper()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The Windows sandbox composer is Windows-only.");

        using var home      = new TestTempDir();
        var       settings  = new WindowsSandboxSettings
        {
            Enabled              = true,
            SetupExecutablePath  = home.WriteBytes("setup.exe", [0x4d, 0x5a]),
            RunnerExecutablePath = home.WriteBytes("runner.exe", [0x4d, 0x5a]),
            SandboxHome          = Path.Combine(home.Root, "home")
        };
        home.CreateDirectory("home");
        using var workspace = new TestTempDir();
        var trustPath = Path.Combine(home.Root, "workspace-trust.json");

        var composed = WindowsSandboxComposer.Compose(settings, workspace.Root, null, null, null, [trustPath]);

        var manifestPath = SandboxProtectedPathsStore.ResolvePath(composed.Components);
        Assert.That(File.Exists(manifestPath), Is.True, manifestPath);
        var manifest = JsonSerializer.Deserialize<string[]>(File.ReadAllText(manifestPath),
                                                           WindowsSandboxJsonContext.Default.StringArray);
        Assert.That(manifest, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(manifest!.Any(path => string.Equals(path, composed.Components.SandboxHome,
                                                            StringComparison.OrdinalIgnoreCase)), Is.True, "home");
            Assert.That(manifest!.Any(path => string.Equals(path, composed.Components.CapabilitySidPath,
                                                            StringComparison.OrdinalIgnoreCase)), Is.True, "cap_sid");
            Assert.That(manifest!.Any(path => string.Equals(path, trustPath, StringComparison.OrdinalIgnoreCase)),
                        Is.True, "trust path");
        });
    }

    // ---------------------------------------------------------------- 装置

    /// <summary>空目标/代理环境字典，供身份串构造使用。 Empty target/proxy dictionaries for identity construction.</summary>
    private static IReadOnlyDictionary<string, string> EmptyEnv() => new Dictionary<string, string>();

    /// <summary>构造 home 与凭据目录并返回临时目录。 Builds a home with a credentials directory inside the temp root.</summary>
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

    /// <summary>构造带 deny-read 集的最小有效隔离策略。 Builds a minimal valid isolation policy with a deny-read set.</summary>
    private static SandboxIsolationPolicy Policy(TestTempDir dir, IReadOnlyList<string> denyReadPaths) => new()
    {
        Kind                = SandboxPolicyKind.WorkspaceWrite,
        WorkspaceRoot       = dir.Root,
        SpawnProfile        = new SandboxPermissionProfile
        {
            FileSystem = new SandboxFileSystemRestriction { Entries = [] },
            Network    = "restricted"
        },
        EffectiveReadRoots  = [],
        EffectiveWriteRoots = [dir.Root],
        DenyWritePaths      = [],
        TempWriteRoots      = [],
        DenyReadPaths       = denyReadPaths
    };

    /// <summary>构造带沙箱身份的执行策略。 Builds an execution policy carrying the sandbox identity.</summary>
    private static ProcessExecutionPolicy SandboxPolicy() => new()
    {
        Backend        = ProcessExecutionBackendKind.WindowsSandbox,
        DisplayName    = "windows-sandbox: workspace-write",
        PolicyIdentity = "backend=windows-sandbox\npolicyVersion=8\nkind=workspace-write"
    };

    /// <summary>以匿名对象序列化构造工具调用参数。 Builds tool-call arguments from an anonymous object.</summary>
    private static ChatToolCall Call(string functionName, object arguments) =>
        new(Guid.NewGuid().ToString("N"), functionName, JsonSerializer.Serialize(arguments));

    /// <summary>测试用活动快照来源：按构造返回固定快照。 Fixed-snapshot source for tests.</summary>
    private sealed class SnapshotSource(ActiveCapabilitySnapshot snapshot) : IActiveCapabilitySource
    {
        public ActiveCapabilitySnapshot Snapshot() => snapshot;
    }
}
