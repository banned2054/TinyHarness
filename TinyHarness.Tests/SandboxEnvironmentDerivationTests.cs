using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         沙箱环境派生：NUGET_PACKAGES 默认指向 sandbox home 内的 nuget-cache 并作为写根，可信覆盖生效；
///         sandbox home 的 tools 目录进入写根并追加到 PATH；附加读根校验、去重且不得覆盖 sandbox home；
///         缺少网络字段的旧配置反序列化为 Restricted 兼容。
///     </para>
///     <para>
///         Sandbox environment derivation: NUGET_PACKAGES defaults to the
///         nuget-cache inside the sandbox home and becomes a write root, with the
///         trusted override winning; the sandbox home's tools directory joins the
///         write roots and is appended to PATH; additional read roots are
///         validated, deduplicated, and may never cover the sandbox home; old
///         configs without the network fields deserialize as the Restricted default.
///     </para>
/// </summary>
public class SandboxEnvironmentDerivationTests
{
    [Test]
    public void Compose_DefaultsNugetCacheInsideSandboxHomeAsWriteRoot()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new TestTempDir();
        using var workspace = new TestTempDir();
        var composed = WindowsSandboxComposer.Compose(TrustedSettings(home), workspace.Root);

        var expectedCache = Path.Combine(home.Root, "nuget-cache");
        Assert.That(composed.TargetEnvironment["NUGET_PACKAGES"], Is.EqualTo(expectedCache));
        Assert.That(composed.Policy.EffectiveWriteRoots, Does.Contain(expectedCache));
        var writeEntries = SpawnWritePaths(composed.Policy);
        Assert.That(writeEntries, Does.Contain(expectedCache));

        // 相同设置重复组合得到稳定身份；派生缓存进入环境哈希。
        var again = WindowsSandboxComposer.Compose(TrustedSettings(home), workspace.Root);
        Assert.That(again.ExecutionPolicy.PolicyIdentity, Is.EqualTo(composed.ExecutionPolicy.PolicyIdentity));
    }

    [Test]
    public void Compose_ExtraEnvironmentNugetPackagesOverrideWins()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new TestTempDir();
        using var workspace = new TestTempDir();
        var overrideCache = Path.Combine(home.Root, "custom-nuget-cache");
        var settings = TrustedSettings(home) with
        {
            ExtraEnvironment = new Dictionary<string, string> { ["NUGET_PACKAGES"] = overrideCache }
        };

        var composed = WindowsSandboxComposer.Compose(settings, workspace.Root);

        Assert.That(composed.TargetEnvironment["NUGET_PACKAGES"], Is.EqualTo(overrideCache));
        // 覆盖值替代默认缓存路径成为派生写根，否则 restore 会写入只读目录。
        Assert.That(composed.Policy.EffectiveWriteRoots, Does.Contain(overrideCache));
        Assert.That(composed.Policy.EffectiveWriteRoots,
                    Does.Not.Contain(Path.Combine(home.Root, "nuget-cache")));
    }

    [Test]
    public void Compose_AppendsSandboxToolsDirectoryToPathAndWriteRoots()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new TestTempDir();
        using var workspace = new TestTempDir();
        var composed = WindowsSandboxComposer.Compose(TrustedSettings(home), workspace.Root);

        var toolsDirectory = Path.Combine(home.Root, "tools");
        Assert.That(composed.Policy.EffectiveWriteRoots, Does.Contain(toolsDirectory));
        var path = composed.TargetEnvironment["PATH"];
        Assert.That(path == toolsDirectory || path.EndsWith(";" + toolsDirectory, StringComparison.Ordinal),
                    Is.True, $"PATH must append the sandbox tools directory: '{path}'.");
    }

    [Test]
    public void Resolver_ValidatesAndDeduplicatesAdditionalReadRoots()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var dir   = new TestTempDir();
        using var extra = new TestTempDir();
        using var home  = new TestTempDir();
        var environment = new Dictionary<string, string> { ["TEMP"] = dir.Root };

        Assert.Throws<ArgumentException>(() => SandboxPolicyResolver.Resolve(
            SandboxPolicyKind.WorkspaceWrite, dir.Root, environment,
            additionalReadRoots : ["relative-read-root"], sandboxHome : home.Root));

        var policy = SandboxPolicyResolver.Resolve(SandboxPolicyKind.WorkspaceWrite, dir.Root, environment,
                                                   additionalReadRoots :
                                                   [extra.Root, extra.Root.ToUpperInvariant()],
                                                   sandboxHome : home.Root);
        Assert.That(policy.EffectiveReadRoots, Is.EqualTo([extra.Root]));
        var readPaths = SpawnReadPaths(policy);
        Assert.That(readPaths, Does.Contain(extra.Root));
        Assert.That(readPaths.Count(path =>
                                        string.Equals(path, extra.Root, StringComparison.OrdinalIgnoreCase)),
                    Is.EqualTo(1), "Duplicate read roots must collapse into one entry.");
        // 读根只授予读取；不得混入有效写根。
        Assert.That(policy.EffectiveWriteRoots, Does.Not.Contain(extra.Root));
    }

    [Test]
    public void Resolver_RejectsReadRootContainingSandboxHome()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var dir  = new TestTempDir();
        using var home = new TestTempDir();
        var       env  = new Dictionary<string, string> { ["TEMP"] = dir.Root };

        // 读根包含或等于 sandbox home 会把 DPAPI 凭据暴露给沙箱账户组；必须拒绝。
        Assert.Throws<ArgumentException>(() => SandboxPolicyResolver.Resolve(
            SandboxPolicyKind.WorkspaceWrite, dir.Root, env,
            additionalReadRoots : [home.Root], sandboxHome : home.Root));
    }

    [Test]
    public void Resolver_AdditionalReadRootsFlowThroughAndBindIdentity()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new TestTempDir();
        using var workspace = new TestTempDir();
        using var extra     = new TestTempDir();
        var settings = TrustedSettings(home);

        var withoutReadRoots = WindowsSandboxComposer.Compose(settings, workspace.Root);
        var withReadRoots = WindowsSandboxComposer.Compose(settings with { AdditionalReadRoots = [extra.Root] },
                                                           workspace.Root);

        Assert.That(withReadRoots.Policy.EffectiveReadRoots, Is.EqualTo([extra.Root]));
        Assert.That(SpawnReadPaths(withReadRoots.Policy), Does.Contain(extra.Root));
        Assert.That(withReadRoots.ExecutionPolicy.PolicyIdentity,
                    Is.Not.EqualTo(withoutReadRoots.ExecutionPolicy.PolicyIdentity));
        TestAssert.Contains("readRoots=", withReadRoots.ExecutionPolicy.PolicyIdentity, StringComparison.Ordinal);
    }

    [Test]
    public async Task UserConfigStore_OldShapeWithoutNetworkFields_BindsRestrictedDefaults()
    {
        using var dir  = new TestTempDir();
        var       path = Path.Combine(dir.Root, "user-config.json");
        await File.WriteAllTextAsync(path,
                                     """
                                     {"settings":{"windowsSandbox":{"enabled":true,
                                     "setupExecutablePath":"C:\\sb\\setup.exe",
                                     "runnerExecutablePath":"C:\\sb\\runner.exe",
                                     "sandboxHome":"C:\\sb-home","policy":"workspace-write"}}}
                                     """);

        var loaded = await UserConfigStore.LoadAsync(path, CancellationToken.None);
        var sandbox = TestAssert.NotNull(loaded.Settings!.WindowsSandbox!);

        Assert.That(sandbox.NetworkPolicy, Is.EqualTo(SandboxNetworkPolicy.Restricted));
        Assert.That(sandbox.Proxy.Mode, Is.EqualTo(SandboxProxyMode.FollowAppDefault));
        Assert.That(sandbox.AdditionalReadRoots, Is.Empty);
    }

    [Test]
    public async Task UserConfigStore_RoundTripsNetworkPolicyProxyAndReadRoots()
    {
        using var dir  = new TestTempDir();
        var       path = Path.Combine(dir.Root, "user-config.json");
        var config = new UserConfig
        {
            Settings = new UserConfigSettings
            {
                WindowsSandbox = new WindowsSandboxSettings
                {
                    Enabled              = true,
                    SetupExecutablePath  = "C:\\sb\\setup.exe",
                    RunnerExecutablePath = "C:\\sb\\runner.exe",
                    SandboxHome          = "C:\\sb-home",
                    NetworkPolicy        = SandboxNetworkPolicy.Enabled,
                    Proxy                = new SandboxProxyPolicy
                    {
                        Mode      = SandboxProxyMode.Named,
                        ProxyName = "corp-proxy"
                    },
                    AdditionalReadRoots  = ["C:\\data\\reference"]
                }
            }
        };

        await UserConfigStore.SaveAsync(path, config, CancellationToken.None);
        var loaded = await UserConfigStore.LoadAsync(path, CancellationToken.None);
        var sandbox = TestAssert.NotNull(loaded.Settings!.WindowsSandbox!);

        Assert.That(sandbox.NetworkPolicy, Is.EqualTo(SandboxNetworkPolicy.Enabled));
        Assert.That(sandbox.Proxy.Mode, Is.EqualTo(SandboxProxyMode.Named));
        Assert.That(sandbox.Proxy.ProxyName, Is.EqualTo("corp-proxy"));
        Assert.That(sandbox.AdditionalReadRoots, Is.EqualTo(["C:\\data\\reference"]));
    }

    [Test]
    public void Resolver_RejectsWriteRootTouchingCredentialsDirectory()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var dir  = new TestTempDir();
        using var home = new TestTempDir();
        var       env  = new Dictionary<string, string> { ["TEMP"] = dir.Root };
        var secrets = Path.Combine(home.Root, ".sandbox-secrets");

        // 写根等于凭据目录或位于其下（或反过来覆盖它）都会暴露 DPAPI 凭据；两个方向都拒绝。
        Assert.Throws<ArgumentException>(() => SandboxPolicyResolver.Resolve(
            SandboxPolicyKind.WorkspaceWrite, dir.Root, env,
            additionalWriteRoots : [secrets], sandboxHome : home.Root));
        Assert.Throws<ArgumentException>(() => SandboxPolicyResolver.Resolve(
            SandboxPolicyKind.WorkspaceWrite, dir.Root, env,
            additionalWriteRoots : [Path.Combine(secrets, "nested")], sandboxHome : home.Root));

        // 与凭据目录无关的写根不受影响；sandboxHome 缺省时（测试形态）不启用该校验。
        using var extra = new TestTempDir();
        var policy = SandboxPolicyResolver.Resolve(SandboxPolicyKind.WorkspaceWrite, dir.Root, env,
                                                   additionalWriteRoots : [extra.Root], sandboxHome : home.Root);
        Assert.That(policy.EffectiveWriteRoots, Does.Contain(extra.Root));
    }

    [Test]
    public void Composer_RejectsWriteRootTouchingCredentialsDirectory()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new TestTempDir();
        using var workspace = new TestTempDir();
        var       settings  = TrustedSettings(home);
        var       secrets   = Path.Combine(home.Root, ".sandbox-secrets");

        // 用户声明写根经 Planner 的规范化接受凭据重叠校验。
        Assert.Throws<ArgumentException>(() => WindowsSandboxComposer.Compose(
            settings with { AdditionalWriteRoots = [secrets] }, workspace.Root));

        // 可信 extraEnvironment 的 NUGET_PACKAGES 覆盖派生的缓存写根，同样必须经过该校验。
        Assert.Throws<ArgumentException>(() => WindowsSandboxComposer.Compose(
            settings with { ExtraEnvironment = new Dictionary<string, string> { ["NUGET_PACKAGES"] = secrets } },
            workspace.Root));
    }

    [Test]
    public void Resolver_RejectsTempRootTouchingCredentialsDirectory()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var dir  = new TestTempDir();
        using var home = new TestTempDir();
        var       secrets = Path.Combine(home.Root, ".sandbox-secrets");

        // temp 根等于凭据目录（可信 tempRoot 指向它）会把它变成沙箱可写面，必须拒绝；
        // 文案钉住"凭据目录绝不可作为沙箱可写根"。
        var equal = Assert.Throws<ArgumentException>(() => SandboxPolicyResolver.Resolve(
            SandboxPolicyKind.WorkspaceWrite, dir.Root,
            new Dictionary<string, string> { ["TEMP"] = secrets, ["TMP"] = secrets },
            sandboxHome : home.Root));
        TestAssert.Contains("must never be a sandbox writable root", equal!.Message, StringComparison.Ordinal);

        // temp 根落入凭据目录内部同样拒绝（任一方向重叠都算）。
        Assert.Throws<ArgumentException>(() => SandboxPolicyResolver.Resolve(
            SandboxPolicyKind.WorkspaceWrite, dir.Root,
            new Dictionary<string, string> { ["TEMP"] = Path.Combine(secrets, "nested") },
            sandboxHome : home.Root));

        // 与凭据目录无关的默认 temp 根不受影响；sandboxHome 缺省时（测试形态）不启用该校验。
        var policy = SandboxPolicyResolver.Resolve(SandboxPolicyKind.WorkspaceWrite, dir.Root,
                                                   new Dictionary<string, string>
                                                   {
                                                       ["TEMP"] = Path.Combine(home.Root, "tmp")
                                                   },
                                                   sandboxHome : home.Root);
        Assert.That(policy.TempWriteRoots, Does.Contain(Path.Combine(home.Root, "tmp")));
    }

    [Test]
    public void Composer_RejectsTempRootTouchingCredentialsDirectory()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new TestTempDir();
        using var workspace = new TestTempDir();
        var       settings  = TrustedSettings(home);
        var       secrets   = Path.Combine(home.Root, ".sandbox-secrets");

        // 可信 tempRoot 指向凭据目录：组合（经 Planner→Resolver 的单一拦截点）必须 fail closed，
        // 绝不回退宿主执行。
        var rejected = Assert.Throws<ArgumentException>(() => WindowsSandboxComposer.Compose(
            settings with { SandboxTempRoot = secrets }, workspace.Root));
        TestAssert.Contains("credentials directory", rejected!.Message, StringComparison.Ordinal);

        // 落入凭据目录内部的 tempRoot 同样拒绝。
        Assert.Throws<ArgumentException>(() => WindowsSandboxComposer.Compose(
            settings with { SandboxTempRoot = Path.Combine(secrets, "tmp") }, workspace.Root));

        // 经可信 extraEnvironment 注入的 TEMP/TMP 走同一条拦截，不因覆盖默认 TEMP 而绕过。
        Assert.Throws<ArgumentException>(() => WindowsSandboxComposer.Compose(
            settings with { ExtraEnvironment = new Dictionary<string, string> { ["TMP"] = secrets } },
            workspace.Root));
    }

    /// <summary>
    ///     <para>
    ///         最终路径解析的直测：存在的目录解析为同一落点，缺失路径返回 null（调用方回落词法校验）。
    ///         不布置 junction/8.3 实例：junction 需要 mklink 外部工具、8.3 名生成依赖卷设置，
    ///         在任意测试机上不可靠；解析链本身由 GetFinalPathNameByHandleW 保证。
    ///     </para>
    ///     <para>
    ///         A direct test of final-path resolution: an existing directory resolves
    ///         to the same location, and a missing path returns null (callers fall back
    ///         to lexical validation). No junction/8.3 fixtures: junctions need the
    ///         external mklink tool and 8.3 generation depends on per-volume settings,
    ///         neither reliable on arbitrary test machines; the resolution chain itself
    ///         is guaranteed by GetFinalPathNameByHandleW.
    ///     </para>
    /// </summary>
    [Test]
    public void FinalPathResolver_ResolvesExistingDirectoryAndReturnsNullForMissingPath()
    {
        using var dir = new TestTempDir();
        if (!OperatingSystem.IsWindows())
        {
            Assert.That(SandboxFinalPathResolver.TryGetFinalPath(dir.Root), Is.Null);
            return;
        }

        var final = SandboxFinalPathResolver.TryGetFinalPath(dir.Root);
        Assert.That(final, Is.Not.Null);
        Assert.That(string.Equals(Path.GetFullPath(final!), dir.Root, StringComparison.OrdinalIgnoreCase),
                    Is.True, $"Final path '{final}' must identify the directory '{dir.Root}'.");

        Assert.That(SandboxFinalPathResolver.TryGetFinalPath(Path.Combine(dir.Root, "does-not-exist")),
                    Is.Null);
    }

    // ---- 工具 ----

    /// <summary>
    ///     构造组件齐全的可信设置：setup/runner 为占位文件，Compose 的存在性校验可通过。
    ///     Builds trusted settings with present components: setup/runner are stub
    ///     files so the composer's existence checks pass.
    /// </summary>
    private static WindowsSandboxSettings TrustedSettings(TestTempDir home) => new()
    {
        Enabled              = true,
        SetupExecutablePath  = home.WriteBytes("setup.exe", [0x4d, 0x5a]),
        RunnerExecutablePath = home.WriteBytes("runner.exe", [0x4d, 0x5a]),
        SandboxHome          = home.Root
    };

    private static IReadOnlyList<string> SpawnWritePaths(SandboxIsolationPolicy policy) =>
        policy.SpawnProfile.FileSystem.Entries
              .Where(entry => entry is { Access: "write", Path.ExplicitPath: not null })
              .Select(entry => entry.Path.ExplicitPath!)
              .ToList();

    private static IReadOnlyList<string> SpawnReadPaths(SandboxIsolationPolicy policy) =>
        policy.SpawnProfile.FileSystem.Entries
              .Where(entry => entry is { Access: "read", Path.ExplicitPath: not null })
              .Select(entry => entry.Path.ExplicitPath!)
              .ToList();
}
