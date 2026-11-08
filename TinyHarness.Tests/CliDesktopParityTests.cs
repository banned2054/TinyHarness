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
///         描述逐字节一致（源级提取比对）；deny 计划两端同构——trust.json 存在时 Desktop 与 CLI 的
///         条目集合一致（10 条=trust+用户配置+会话存储+5 C2 目录段+.codex+cap_sid），不存在时
///         Desktop 按存在性分支不加入也不物化（9 条；B6-0 后 CLI 生产组合的 plan 调用已移入信任
///         激活回调，trust 缺失只发生在 Desktop 分支）；离线 smoke 与生产的差异显式声明（源级静态
///         检查：声明标记+smoke 槽位 PermissionShellTool 形态）。CLI 组合态 PermissionShellTool 注册
///         与未组合回退由真实入口测试（RS-10 与未信任 run）功能性覆盖，不在此重复。
///     </para>
///     <para>
///         Two-ends consistency (B7, mirroring test design §3 CliDesktopToolParityTests): the two
///         WorkspacePrompts' shell descriptions are byte-identical (source-level extraction compare); the
///         deny plans are isomorphic — with workspace-trust.json present the Desktop set matches the CLI
///         set (10 entries), and without it the Desktop keeps the existence branch, neither listing nor
///         materializing the file (9 entries; after B6-0 the CLI production plan call runs inside the
///         trust-activation callback, so a missing trust store only reaches the Desktop branch); the
///         offline smoke declares its difference from production (source-level static check). The CLI
///         composed PermissionShellTool registration and the bare fallback are covered functionally by
///         the real-entry tests (RS-10 and the untrusted run), not duplicated here.
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

    // ---- deny 计划两端同构（trust.json 存在性分支） ----

    [Test]
    public async Task DenyPlans_AreIsomorphic_WhenTheSharedTrustStoreExists()
    {
        // 存在性断言在夹具存续期内完成（临时目录在计划构建返回后即清理，assert 放在构建期）。
        var (cliPlan, desktopPlan, trustExisted) = await BuildCliAndDesktopPlansAsync();

        Assert.Multiple(() =>
        {
            Assert.That(trustExisted, Is.True);
            Assert.That(cliPlan, Is.Not.Null);
            Assert.That(desktopPlan, Is.Not.Null);
            Assert.That(Normalized(desktopPlan!), Is.EqualTo(Normalized(cliPlan!)),
                        "trust.json 存在时两端 deny 集必须同构（B7 超集）。");
            Assert.That(desktopPlan!.DenyReadPaths, Has.Count.EqualTo(10),
                        "典型构成：trust+配置+会话存储+5 C2 目录段+.codex+cap_sid=10。");
        });
    }

    [Test]
    public async Task DenyPlans_TrustStoreAbsent_DesktopFollowsTheExistenceBranch()
    {
        // B6-0 之后 CLI 生产组合的 plan 调用已移入信任激活回调（trust.json 必然已存在），
        // "trust 缺失"只发生在 Desktop 的存在性分支：缺文件不加条目、也不物化 trust.json。
        var (desktopPlan, desktopTrustListed) = await BuildDesktopPlanWithoutTrustAsync();

        Assert.Multiple(() =>
        {
            Assert.That(desktopPlan, Is.Not.Null);
            Assert.That(desktopTrustListed, Is.False,
                        "trust.json 不存在时 Desktop 不加入 trust 条目（存在性分支）。");
            Assert.That(desktopPlan!.DenyReadPaths, Has.Count.EqualTo(9),
                        "典型构成：配置+会话存储+5 C2 目录段+.codex+cap_sid=9。");
            Assert.That(desktopPlan.DenyReadPaths.Any(path => path.Contains("workspace-trust",
                                                                            StringComparison.OrdinalIgnoreCase)),
                        Is.False);
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

    private static string[] Normalized(SandboxBaselineReadProtection plan) =>
        plan.DenyReadPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();

    private static async Task<(SandboxBaselineReadProtection? CliPlan, SandboxBaselineReadProtection? DesktopPlan,
        bool TrustExisted)> BuildCliAndDesktopPlansAsync()
    {
        bool                           trustExisted;
        SandboxBaselineReadProtection? cliPlan;
        SandboxBaselineReadProtection? desktopPlan;
        using var                      configDir = new TestTempDir();
        using var                      workspace = new TestTempDir();
        using var                      storage   = new TestTempDir();
        using var                      home      = new TestTempDir();
        {
            var          userConfigPath = Path.Combine(configDir.Root, UserConfigStore.FileName);
            var          trustPath      = Path.Combine(configDir.Root, "workspace-trust.json");
            const string configBody     = "{\"settings\":{\"sessionStoreDirectory\":\"placeholder\"}}";
            await File.WriteAllTextAsync(userConfigPath, configBody);
            // 与 CLI/Desktop 相同的共享 trust.json：存在性分支的真实文件前提。
            var trustStore = new WorkspaceTrustStore(trustPath, [workspace.Root]);
            await trustStore.SaveAsync(WorkspaceDirectoryIdentity.Capture(workspace.Root), true,
                                       CancellationToken.None);
            trustExisted = File.Exists(trustPath);
            var settings = SandboxStubSettings(home);

            // CLI 生产输入（Program.cs 组合态）：受保护路径=trust+用户配置，ensureDirectory=会话存储。
            cliPlan = WindowsSandboxComposer.PlanBaselineReadProtection(
                                                                        [trustPath, userConfigPath], storage.Root,
                                                                        settings, static _ => []);
            // Desktop 生产输入（DesktopSessionFactory 组合态）：用户配置恒入、trust 按存在性分支。
            var desktopSecrets = new List<string> { userConfigPath };
            if (File.Exists(trustPath)) desktopSecrets.Add(trustPath);
            desktopPlan = WindowsSandboxComposer.PlanBaselineReadProtection(
                                                                            desktopSecrets, storage.Root, settings,
                                                                            static _ => []);
        }

        return (cliPlan, desktopPlan, trustExisted);
    }

    private static async Task<(SandboxBaselineReadProtection? DesktopPlan, bool TrustListed)>
        BuildDesktopPlanWithoutTrustAsync()
    {
        SandboxBaselineReadProtection? desktopPlan;
        var                            trustListed = false;
        using var                      configDir   = new TestTempDir();
        using var                      workspace   = new TestTempDir();
        using var                      storage     = new TestTempDir();
        using var                      home        = new TestTempDir();
        {
            var          userConfigPath = Path.Combine(configDir.Root, UserConfigStore.FileName);
            var          trustPath      = Path.Combine(configDir.Root, "workspace-trust.json");
            const string configBody     = "{\"settings\":{\"sessionStoreDirectory\":\"placeholder\"}}";
            await File.WriteAllTextAsync(userConfigPath, configBody);
            var settings = SandboxStubSettings(home);

            var desktopSecrets = new List<string> { userConfigPath };
            if (File.Exists(trustPath)) desktopSecrets.Add(trustPath);
            desktopPlan = WindowsSandboxComposer.PlanBaselineReadProtection(
                                                                            desktopSecrets, storage.Root, settings,
                                                                            static _ => []);
            trustListed = desktopPlan?.DenyReadPaths.Any(path => string.Equals(path, trustPath,
                                                                               StringComparison.OrdinalIgnoreCase)) ==
                          true;
        }

        return (desktopPlan, trustListed);
    }

    private static WindowsSandboxSettings SandboxStubSettings(TestTempDir home) => new()
    {
        Enabled              = true,
        SetupExecutablePath  = home.WriteFile("setup.exe", "stub"),
        RunnerExecutablePath = home.WriteFile("runner.exe", "stub"),
        SandboxHome          = home.Root
    };
}
