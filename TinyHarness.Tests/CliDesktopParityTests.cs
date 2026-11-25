using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;
using TinyHarness.Cli.Services;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         两端一致（B7，对位测试设计 §3 CliDesktopToolParityTests）：两份 WorkspacePrompts 的 shell
///         描述逐字节一致（源级提取比对）；deny 与 read-root 计划两端同构——CLI 与 Desktop 生产规划
///         已统一切换至顶层读根与固定拒读规划（<see cref="WindowsSandboxComposer.PlanBaselineReadProtectionTopLevel" />），
///         测试以隔离的合成 profile 根与受控空簿记调用 <see cref="WindowsSandboxComposer.PlanBaselineReadProtectionTopLevelCore" />
///         验证：trust.json 存在时两端生成的 ReadRoots 与 DenyReadPaths 完全同构（含固定敏感目录、
///         受保护路径、ensureDirectory 与 cap_sid）；trust.json 不存在时 Desktop 侧遵守存在性分支，
///         既不加入也不物化 trust.json；CLI 源级静态断言确认不再调用旧扫描入口；离线 smoke 与生产的
///         差异显式声明（源级静态检查：声明标记+smoke 槽位 PermissionShellTool 形态）。
///     </para>
///     <para>
///         Two-ends consistency (B7, mirroring test design §3 CliDesktopToolParityTests): the two
///         WorkspacePrompts' shell descriptions are byte-identical (source-level extraction compare);
///         both CLI and Desktop production planners now use the top-level read-root and fixed
///         deny-read planner (<see cref="WindowsSandboxComposer.PlanBaselineReadProtectionTopLevel" />),
///         tested via <see cref="WindowsSandboxComposer.PlanBaselineReadProtectionTopLevelCore" /> against
///         an isolated synthetic profile root with controlled empty bookkeeping: when workspace-trust.json
///         exists both ends produce isomorphic ReadRoots and DenyReadPaths (including fixed sensitive
///         directories, protected paths, ensureDirectory, and cap_sid); when absent the Desktop side
///         keeps the existence branch, neither listing nor materializing workspace-trust.json; a
///         source-level check pins the CLI production call site away from the legacy scanner; and the
///         offline smoke declares its difference from production (source-level static check).
///     </para>
/// </summary>
public class CliDesktopParityTests
{
    private const string ShellBlockStart = "- shell：执行必要的构建、测试和其他命令。";

    private const string ShellBlockEnd = "executionScope=host 重新提交并等待审批。";

    private static string RepoRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                                                                    "..", "..", "..", ".."));

    private static string CliPromptsSource =>
        File.ReadAllText(Path.Combine(RepoRoot, "TinyHarness.Cli", "Prompts", "WorkspacePrompts.cs"));

    private static string DesktopPromptsSource =>
        File.ReadAllText(Path.Combine(RepoRoot, "TinyHarness.Desktop.Runtime", "Prompts",
                                      "WorkspacePrompts.cs"));

    // ---- 两份 WorkspacePrompts 的 shell 描述逐字节一致 ----

    [Test]
    public void WorkspacePrompts_ShellDescriptions_AreByteIdenticalAcrossBothEnds()
    {
        var cliBlock     = ExtractBlock(CliPromptsSource);
        var desktopBlock = ExtractBlock(DesktopPromptsSource);

        Assert.Multiple(() =>
        {
            Assert.That(cliBlock, Is.Not.Empty, "CLI 源中必须能提取 shell 描述块。");
            Assert.That(desktopBlock, Is.Not.Empty, "Desktop 源中必须能提取 shell 描述块。");
            Assert.That(desktopBlock, Is.EqualTo(cliBlock),
                        "两份 WorkspacePrompts 的 shell 描述必须逐字节一致（B7-4）。");
        });
    }

    private static string ExtractBlock(string source)
    {
        var start = source.IndexOf(ShellBlockStart, StringComparison.Ordinal);
        var end   = source.IndexOf(ShellBlockEnd, StringComparison.Ordinal);
        if (start < 0 || end < 0 || end < start) return string.Empty;

        return source[start..(end + ShellBlockEnd.Length)];
    }

    // ---- 顶层读根与 deny 计划两端同构（trust.json 存在性分支） ----

    [Test]
    public async Task DenyPlans_AreIsomorphic_WhenTheSharedTrustStoreExists()
    {
        // 存在性断言在夹具存续期内完成（临时目录在计划构建返回后即清理，assert 放在构建期）。
        var result = await BuildCliAndDesktopPlansAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.TrustExisted, Is.True);
            Assert.That(result.CliPlan, Is.Not.Null);
            Assert.That(result.DesktopPlan, Is.Not.Null);
            Assert.That(Normalized(result.DesktopPlan!.ReadRoots),
                        Is.EqualTo(Normalized(result.CliPlan!.ReadRoots)),
                        "trust.json 存在时两端 ReadRoots 必须同构。");
            Assert.That(Normalized(result.DesktopPlan.DenyReadPaths),
                        Is.EqualTo(Normalized(result.CliPlan.DenyReadPaths)),
                        "trust.json 存在时两端 DenyReadPaths 必须同构（B7 超集）。");
            Assert.That(result.DesktopPlan.ReadRoots, Has.Count.EqualTo(3),
                        "合成 profile 顶层普通子项：config+docs+sandbox-home=3（sessions 已入 deny 故排除）。");
            Assert.That(result.DesktopPlan.ReadRoots,
                        Does.Contain(result.ConfigDir).And.Contain(result.DocsDir).And.Contain(result.HomeDir));
            Assert.That(result.DesktopPlan.DenyReadPaths, Has.Count.EqualTo(10),
                        "典型构成：trust+配置+会话存储+5 固定敏感目录+.codex+cap_sid=10。");
            Assert.That(result.DesktopPlan.DenyReadPaths,
                        Does.Contain(result.TrustPath)
                            .And.Contain(result.UserConfigPath)
                            .And.Contain(result.StorageRoot)
                            .And.Contain(Path.Combine(result.HomeDir, "cap_sid"))
                            .And.Contain(Path.Combine(result.ProfileRoot, ".ssh"))
                            .And.Contain(Path.Combine(result.ProfileRoot, ".codex")));
        });
    }

    [Test]
    public async Task DenyPlans_TrustStoreAbsent_DesktopFollowsTheExistenceBranch()
    {
        // B6-0 之后 CLI 生产组合的 plan 调用已移入信任激活回调（trust.json 必然已存在），
        // "trust 缺失"只发生在 Desktop 的存在性分支：缺文件不加条目、也不物化 trust.json。
        var (desktopPlan, desktopTrustListed, trustMaterialized) = await BuildDesktopPlanWithoutTrustAsync();

        Assert.Multiple(() =>
        {
            Assert.That(desktopPlan, Is.Not.Null);
            Assert.That(desktopTrustListed, Is.False,
                        "trust.json 不存在时 Desktop 不加入 trust 条目（存在性分支）。");
            Assert.That(trustMaterialized, Is.False,
                        "trust.json 不存在时 Desktop 不得物化 workspace-trust.json。");
            Assert.That(desktopPlan!.ReadRoots, Has.Count.EqualTo(3));
            Assert.That(desktopPlan.DenyReadPaths, Has.Count.EqualTo(9),
                        "典型构成：配置+会话存储+5 固定敏感目录+.codex+cap_sid=9。");
            Assert.That(desktopPlan.DenyReadPaths.Any(path => path.Contains("workspace-trust",
                                                                            StringComparison.OrdinalIgnoreCase)),
                        Is.False);
        });
    }

    // ---- CLI 生产基线读保护入口切换至顶层规划（源级静态断言） ----

    [Test]
    public void CliProduction_UsesTopLevelReadProtectionPlanner_WithoutLegacyFullScanCall()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot, "TinyHarness.Cli", "Program.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("WindowsSandboxComposer.PlanBaselineReadProtectionTopLevel("),
                        "CLI 生产组合必须统一使用顶层读根规划入口。");
            Assert.That(source, Does.Not.Contain("WindowsSandboxComposer.PlanBaselineReadProtection("),
                        "CLI 不得保留旧的全盘递归扫描入口调用。");
        });
    }

    // ---- 离线 smoke 差异声明静态检查 ----

    [Test]
    public void Smoke_DeclaresItsDifferenceFromProduction_AndUsesThePermissionShellShape()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot, "TinyHarness.Cli", "Program.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("离线 smoke 与生产的显式差异（B7 声明）"),
                        "smoke 必须显式声明与生产的差异（B7-3）。");
            Assert.That(source, Does.Contain("不证明机器沙箱可用"),
                        "声明必须如实说明 smoke 不证明机器沙箱可用。");
            var declarationIndex = source.IndexOf("离线 smoke 与生产的显式差异（B7 声明）", StringComparison.Ordinal);
            var smokeComposition = source[declarationIndex..];
            Assert.That(smokeComposition.Contains("new PermissionShellTool(", StringComparison.Ordinal),
                        Is.True, "smoke 的 shell 槽位同样注册 PermissionShellTool 形态（同构路由）。");
        });
    }

    // ---- M2 常设信任确认摘要读根行（CLI 源级结构化断言） ----

    [Test]
    public void CliStandingTrustSummary_DeclaresReadRootsInjectionAndRevocation()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot, "TinyHarness.Cli", "Program.cs"));

        Assert.Multiple(() =>
        {
            // M2：CLI 确认摘要必须列出附加读根并声明注入/撤销语义（与 Desktop
            // BuildStandingTrustSummary 同语义，见 DesktopStandingTrustSettingsTests）。
            Assert.That(source, Does.Contain("\"  read roots : \""),
                        "CLI 确认摘要必须包含 read roots 行。");
            Assert.That(source, Does.Contain("injected with the confirmed authorization " +
                                             "(outside-profile roots become OS-readable)"),
                        "read roots 行必须声明确认后注入与 profile 外可读语义。");
            Assert.That(source, Does.Contain("read roots : \" + FormatPaths(sandbox.AdditionalReadRoots)"),
                        "read roots 行必须列出附加读根清单。");
            // Desktop 侧同语义的读根行与撤销语义（源级）。
            var desktopViewModel = File.ReadAllText(Path.Combine(
                                                                 RepoRoot, "TinyHarness.Desktop", "ViewModels",
                                                                 "PermissionSettingsViewModel.cs"));
            Assert.That(desktopViewModel, Does.Contain("附加读根："));
            Assert.That(desktopViewModel, Does.Contain("注入沙箱 OS 读面"));
            Assert.That(desktopViewModel, Does.Contain("读根不再注入"));
        });
    }

    // ---- 工具 ----

    private static string[] Normalized(IReadOnlyList<string> paths) =>
        paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();

    private sealed record ParityPlansResult(
        SandboxBaselineReadProtection? CliPlan,
        SandboxBaselineReadProtection? DesktopPlan,
        bool                           TrustExisted,
        string                         ProfileRoot,
        string                         ConfigDir,
        string                         DocsDir,
        string                         HomeDir,
        string                         StorageRoot,
        string                         UserConfigPath,
        string                         TrustPath);

    private static async Task<ParityPlansResult> BuildCliAndDesktopPlansAsync()
    {
        bool                           trustExisted;
        SandboxBaselineReadProtection? cliPlan;
        SandboxBaselineReadProtection? desktopPlan;
        string                         profileRootPath;
        string                         configDir;
        string                         docsDir;
        string                         homeDir;
        string                         storageRoot;
        string                         userConfigPath;
        string                         trustPath;
        using var                      profileRoot = new TestTempDir();
        using var                      workspace   = new TestTempDir();
        {
            profileRootPath = profileRoot.Root;
            configDir       = profileRoot.CreateDirectory("config");
            docsDir         = profileRoot.CreateDirectory("docs");
            homeDir         = profileRoot.CreateDirectory("sandbox-home");
            storageRoot     = Path.Combine(profileRoot.Root, "sessions");
            userConfigPath  = Path.Combine(configDir, UserConfigStore.FileName);
            trustPath       = Path.Combine(configDir, "workspace-trust.json");
            const string configBody = "{\"settings\":{\"sessionStoreDirectory\":\"placeholder\"}}";
            await File.WriteAllTextAsync(userConfigPath, configBody);
            // 与 CLI/Desktop 相同的共享 trust.json：存在性分支的真实文件前提。
            var trustStore = new WorkspaceTrustStore(trustPath, [workspace.Root]);
            await trustStore.SaveAsync(WorkspaceDirectoryIdentity.Capture(workspace.Root), true,
                                       CancellationToken.None);
            trustExisted = File.Exists(trustPath);
            var settings = SandboxStubSettings(profileRoot, homeDir);

            // CLI 生产输入（Program.cs 组合态）：受保护路径=trust+用户配置，ensureDirectory=会话存储；
            // 使用隔离的合成 profile 根与受控空簿记，避免受本机真实 UserProfile 或簿记文件影响。
            cliPlan =
                WindowsSandboxComposer.PlanBaselineReadProtectionTopLevelCore(profileRoot.Root,
                                                                             [trustPath, userConfigPath],
                                                                             storageRoot,
                                                                             settings,
                                                                             CancellationToken.None,
                                                                             readDenyBookkeeping : static _ => null);
            // Desktop 侧受保护路径组合（与 DesktopSessionFactory 的 baselineProtectedPaths 同形）：
            // 用户配置恒入、trust 按存在性分支，统一走顶层读根规划器。
            var desktopSecrets = new List<string> { userConfigPath };
            if (File.Exists(trustPath)) desktopSecrets.Add(trustPath);
            desktopPlan =
                WindowsSandboxComposer.PlanBaselineReadProtectionTopLevelCore(profileRoot.Root,
                                                                             desktopSecrets,
                                                                             storageRoot,
                                                                             settings,
                                                                             CancellationToken.None,
                                                                             readDenyBookkeeping : static _ => null);
        }

        return new ParityPlansResult(cliPlan, desktopPlan, trustExisted, profileRootPath, configDir, docsDir,
                                     homeDir, storageRoot, userConfigPath, trustPath);
    }

    private static async Task<(SandboxBaselineReadProtection? DesktopPlan, bool TrustListed, bool TrustMaterialized)>
        BuildDesktopPlanWithoutTrustAsync()
    {
        SandboxBaselineReadProtection? desktopPlan;
        bool                           trustListed;
        bool                           trustMaterialized;
        using var                      profileRoot = new TestTempDir();
        {
            var configDir   = profileRoot.CreateDirectory("config");
            _               = profileRoot.CreateDirectory("docs");
            var homeDir     = profileRoot.CreateDirectory("sandbox-home");
            var storageRoot = Path.Combine(profileRoot.Root, "sessions");
            var userConfigPath = Path.Combine(configDir, UserConfigStore.FileName);
            var trustPath      = Path.Combine(configDir, "workspace-trust.json");
            const string configBody = "{\"settings\":{\"sessionStoreDirectory\":\"placeholder\"}}";
            await File.WriteAllTextAsync(userConfigPath, configBody);
            var settings = SandboxStubSettings(profileRoot, homeDir);

            var desktopSecrets = new List<string> { userConfigPath };
            if (File.Exists(trustPath)) desktopSecrets.Add(trustPath);
            desktopPlan =
                WindowsSandboxComposer.PlanBaselineReadProtectionTopLevelCore(profileRoot.Root,
                                                                             desktopSecrets,
                                                                             storageRoot,
                                                                             settings,
                                                                             CancellationToken.None,
                                                                             readDenyBookkeeping : static _ => null);
            trustListed = desktopPlan.DenyReadPaths.Any(path => string.Equals(path, trustPath,
                                                                              StringComparison.OrdinalIgnoreCase));
            trustMaterialized = File.Exists(trustPath);
        }

        return (desktopPlan, trustListed, trustMaterialized);
    }

    private static WindowsSandboxSettings SandboxStubSettings(TestTempDir profileRoot, string homeDir) => new()
    {
        Enabled              = true,
        SetupExecutablePath  = profileRoot.WriteFile("sandbox-home/setup.exe", "stub"),
        RunnerExecutablePath = profileRoot.WriteFile("sandbox-home/runner.exe", "stub"),
        SandboxHome          = homeDir
    };
}
