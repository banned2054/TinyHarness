using System.Text;
using System.Text.Json;
using TinyHarness.Cli.Commands;
using TinyHarness.Cli.Exceptions;
using TinyHarness.Cli.Models;
using TinyHarness.Cli.Services;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         network=enabled（常设在线）验收扩展的离线测试：CLI 解析（--network）、按模式的用例名集合、
///         在线用例判定（身份、代理环境反显、外网请求、宿主环回、凭据目录不可读）以及 restricted 模式下
///         显式负向对照的判定。后端全部为脚本化 fake——不启动真实 setup/runner、不做 refresh、
///         不触网、不修改机器状态；既有 restricted 矩阵的判定测试保持在原文件。
///     </para>
///     <para>
///         Offline tests of the standing-online (network=enabled) acceptance
///         extension: CLI parsing (--network), the per-mode case-name sets, the
///         online case verdicts (identity, proxy environment echo, external
///         request, host loopback, unreadable credentials directory), and the
///         restricted-mode explicit negative controls. Every backend is a scripted
///         fake — no real setup/runner launch, no refresh, no network, no machine
///         changes; the existing restricted matrix verdict tests stay in their file.
///     </para>
/// </summary>
public sealed class WindowsSandboxAcceptanceNetworkModeTests
{
    private const string NamedProxyHostPort = "127.0.0.1:47291";

    // ---- 命令行解析与用例名集合 ----

    [Test]
    public void SandboxVerify_NetworkDefaultsToRestricted()
    {
        var options = CommandLine.Parse(["sandbox", "verify"]);

        Assert.That(options.VerifyNetwork, Is.Null);
    }

    [Test]
    public void SandboxVerify_ParsesNetworkRestrictedAndEnabled()
    {
        Assert.That(CommandLine.Parse(["sandbox", "verify", "--network", "restricted"]).VerifyNetwork,
                    Is.EqualTo("restricted"));
        Assert.That(CommandLine.Parse(["sandbox", "verify", "--yes", "--network", "enabled"]).VerifyNetwork,
                    Is.EqualTo("enabled"));
    }

    [Test]
    public void SandboxVerify_NetworkWithoutValueOrUnknownValue_IsAUsageError()
    {
        TestAssert.Throws<CliUsageException>(() => CommandLine.Parse(["sandbox", "verify", "--network"]));
        TestAssert.Throws<CliUsageException>(() =>
                                                 CommandLine.Parse(["sandbox", "verify", "--network", "online"]));
    }

    [Test]
    public void SandboxVerifyWorker_ParsesParentResolvedNetworkMode()
    {
        var marker    = Path.Combine(Path.GetTempPath(), $"tinyharness-worker-parse-{Guid.NewGuid():N}.marker");
        var workspace = Path.Combine(Path.GetTempPath(), $"tinyharness-worker-parse-{Guid.NewGuid():N}");

        // 父进程显式传入解析出的验收网络模式；worker 解析同一取值域与校验。
        Assert.That(CommandLine.Parse(["sandbox", "verify-worker", "--marker", marker, "--hold-seconds", "30",
                                       "--workspace", workspace, "--network", "enabled"]).VerifyNetwork,
                    Is.EqualTo("enabled"));
        Assert.That(CommandLine.Parse(["sandbox", "verify-worker", "--marker", marker, "--hold-seconds", "30",
                                       "--workspace", workspace, "--network", "restricted"]).VerifyNetwork,
                    Is.EqualTo("restricted"));
        // 缺省（手工调用形态）为 null，按常设设置同路；未知取值是用法错误。
        Assert.That(CommandLine.Parse(["sandbox", "verify-worker", "--marker", marker, "--hold-seconds", "30",
                                       "--workspace", workspace]).VerifyNetwork, Is.Null);
        TestAssert.Throws<CliUsageException>(() => CommandLine.Parse(["sandbox", "verify-worker", "--marker", marker,
                                                                      "--hold-seconds", "30", "--workspace",
                                                                      workspace, "--network", "online"]));
    }

    [Test]
    public void CaseNameSets_RestrictedDefaultStaysSeventeen()
    {
        Assert.That(WindowsSandboxAcceptanceRunner.DefaultCaseNames(SandboxNetworkPolicy.Restricted),
                    Is.EqualTo(WindowsSandboxAcceptanceRunner.CaseNames));
        Assert.That(WindowsSandboxAcceptanceRunner.DefaultCaseNames(SandboxNetworkPolicy.Restricted), Has.Count.EqualTo(17));
        // restricted 只在显式 --case 下接受两个负向对照；enabled 全集为 8 个精简在线用例。
        Assert.That(WindowsSandboxAcceptanceRunner.SupportedCaseNames(SandboxNetworkPolicy.Restricted),
                    Has.Count.EqualTo(19));
        Assert.That(WindowsSandboxAcceptanceRunner.DefaultCaseNames(SandboxNetworkPolicy.Enabled),
                    Is.EqualTo(WindowsSandboxAcceptanceRunner.EnabledCaseNames));
        Assert.That(WindowsSandboxAcceptanceRunner.EnabledCaseNames, Has.Count.EqualTo(8));
    }

    // ---- 运行器：enabled 全集与逐例判定 ----

    [Test]
    public async Task EnabledMatrix_WithScriptedBackend_AllCasesPassAndReportIsWritten()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        // deny-probe 用例期望探针与 runner 同目录（SandboxTestHome 的组件都在 home 根下）。
        await File.WriteAllTextAsync(Path.Combine(home.Components.SandboxHome, "codex-windows-managed-deny-probe.exe"),
                                     "MZ");
        var script = OnlineScript(home.Components.OnlineUsername, FullCmdAnswer(workspace.Root), CurlOutcome(0, "200"));
        var runner = new WindowsSandboxAcceptanceRunner(
            new ScriptedBackendFactory(home.Components, workspace.Root, script).Invoke,
            workspace.Root, (marker, _) => new NoopWorker(marker), limits : TestLimits(),
            networkPolicy : SandboxNetworkPolicy.Enabled);

        var report = await runner.RunAsync(cancellationToken : CancellationToken.None);

        Assert.That(report.Cases.Select(result => result.Name).ToList(),
                    Is.EqualTo(WindowsSandboxAcceptanceRunner.EnabledCaseNames));
        Assert.That(report.Cases, Has.All.Property("Verdict").EqualTo(SandboxAcceptanceVerdict.Pass));
        Assert.That(report.Cases.Single(result => result.Name == "identity-whoami").ActualSummary,
                    Does.Contain(home.Components.OnlineUsername));
        // 在线模式的局限清单要说明本报告是 enabled 用例集。
        Assert.That(report.Limitations, Has.Some.Contains("network=enabled"));
        var reportFiles = Directory.GetFiles(home.Components.SandboxDirectory, "tinyharness-verify-*.json");
        Assert.That(reportFiles, Is.Not.Empty);
    }

    [Test]
    public async Task EnabledIdentity_WhoamiWithoutOnlineAccount_Fails()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        // 回显 offline 账户名：enabled 模式期望 online 账户，必须判 FAIL。
        var script = OnlineScript(home.Components.OfflineUsername, OnlineEcho, CurlOutcome(0, "200"));
        var report = await RunEnabledCaseAsync(home, workspace, "identity-whoami", script);

        Assert.That(VerdictOf(report, "identity-whoami"), Is.EqualTo(SandboxAcceptanceVerdict.Fail));
    }

    [Test]
    public async Task EnvProxyEcho_Enabled_JudgesProxyInjection()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        var passing = await RunEnabledCaseAsync(home, workspace, "env-proxy-echo",
                                                OnlineScript(home.Components.OnlineUsername, OnlineEcho,
                                                             CurlOutcome(0, "200")));
        Assert.That(VerdictOf(passing, "env-proxy-echo"), Is.EqualTo(SandboxAcceptanceVerdict.Pass));

        var wrongNoProxy = await RunEnabledCaseAsync(home, workspace, "env-proxy-echo",
                                                     OnlineScript(home.Components.OnlineUsername,
                                                                  Echo("HTTP_PROXY=http://127.0.0.1:47291",
                                                                       "HTTPS_PROXY=http://127.0.0.1:47291",
                                                                       "NO_PROXY=localhost"),
                                                                  CurlOutcome(0, "200")));
        Assert.That(VerdictOf(wrongNoProxy, "env-proxy-echo"), Is.EqualTo(SandboxAcceptanceVerdict.Fail));
        TestAssert.Contains("NO_PROXY", CaseDetail(wrongNoProxy, "env-proxy-echo")!, StringComparison.Ordinal);

        // 显示串脱敏：报告不得携带完整代理 URL（scheme），host:port 允许出现。
        Assert.That(ActualOf(passing, "env-proxy-echo"), Does.Not.Contain("http://"));
        Assert.That(ActualOf(passing, "env-proxy-echo"), Does.Contain(NamedProxyHostPort));
    }

    [Test]
    public async Task EnvProxyEcho_Restricted_JudgesAbsence()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        var passing = await RunRestrictedCaseAsync(home, workspace, "env-proxy-echo",
                                                   OnlineScript(home.Components.OfflineUsername, EmptyEcho,
                                                                CurlOutcome(7, "000")));
        Assert.That(VerdictOf(passing, "env-proxy-echo"), Is.EqualTo(SandboxAcceptanceVerdict.Pass));

        var leaking = await RunRestrictedCaseAsync(home, workspace, "env-proxy-echo",
                                                   OnlineScript(home.Components.OfflineUsername,
                                                                Echo("HTTP_PROXY=http://127.0.0.1:47291"),
                                                                CurlOutcome(7, "000")));
        Assert.That(VerdictOf(leaking, "env-proxy-echo"), Is.EqualTo(SandboxAcceptanceVerdict.Fail));
        Assert.That(ActualOf(leaking, "env-proxy-echo"), Does.Contain("HTTP_PROXY=<set:"));
    }

    [Test]
    public async Task NetworkExternal_OnlineRequires2xx()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        var passing = await RunEnabledCaseAsync(home, workspace, "network-external",
                                                OnlineScript(home.Components.OnlineUsername, OnlineEcho,
                                                             CurlOutcome(0, "200")));
        Assert.That(VerdictOf(passing, "network-external"), Is.EqualTo(SandboxAcceptanceVerdict.Pass));

        var failed = await RunEnabledCaseAsync(home, workspace, "network-external",
                                               OnlineScript(home.Components.OnlineUsername, OnlineEcho,
                                                            CurlOutcome(0, "000")));
        Assert.That(VerdictOf(failed, "network-external"), Is.EqualTo(SandboxAcceptanceVerdict.Fail));

        var refused = await RunEnabledCaseAsync(home, workspace, "network-external",
                                                OnlineScript(home.Components.OnlineUsername, OnlineEcho,
                                                             CurlOutcome(7, "000")));
        Assert.That(VerdictOf(refused, "network-external"), Is.EqualTo(SandboxAcceptanceVerdict.Fail));
    }

    [Test]
    public async Task NetworkExternal_RestrictedRequiresFailure()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        var blocked = await RunRestrictedCaseAsync(home, workspace, "network-external",
                                                   OnlineScript(home.Components.OfflineUsername, EmptyEcho,
                                                                CurlOutcome(7, "000")));
        Assert.That(VerdictOf(blocked, "network-external"), Is.EqualTo(SandboxAcceptanceVerdict.Pass));

        var leaked = await RunRestrictedCaseAsync(home, workspace, "network-external",
                                                  OnlineScript(home.Components.OfflineUsername, EmptyEcho,
                                                               CurlOutcome(0, "200")));
        Assert.That(VerdictOf(leaked, "network-external"), Is.EqualTo(SandboxAcceptanceVerdict.Fail));
    }

    [Test]
    public async Task NetworkLoopbackHost_JudgesAnyHttpReplyAndRefusal()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        var reached = await RunEnabledCaseAsync(home, workspace, "network-loopback-host",
                                                OnlineScript(home.Components.OnlineUsername, OnlineEcho,
                                                             CurlOutcome(0, "400")));
        Assert.That(VerdictOf(reached, "network-loopback-host"), Is.EqualTo(SandboxAcceptanceVerdict.Pass));

        var refused = await RunEnabledCaseAsync(home, workspace, "network-loopback-host",
                                                OnlineScript(home.Components.OnlineUsername, OnlineEcho,
                                                             CurlOutcome(7, "000")));
        Assert.That(VerdictOf(refused, "network-loopback-host"), Is.EqualTo(SandboxAcceptanceVerdict.Fail));
        TestAssert.Contains("环回", CaseDetail(refused, "network-loopback-host")!, StringComparison.Ordinal);
    }

    [Test]
    public async Task NetworkLoopbackHost_WithoutLoopbackTarget_IsIndeterminate()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        var limits = TestLimits(loopbackTarget : null);
        var runner = new WindowsSandboxAcceptanceRunner(
            new ScriptedBackendFactory(home.Components, workspace.Root,
                                       OnlineScript(home.Components.OnlineUsername, OnlineEcho,
                                                    CurlOutcome(0, "400"))).Invoke,
            workspace.Root, (marker, _) => new NoopWorker(marker), limits : limits,
            networkPolicy : SandboxNetworkPolicy.Enabled);

        var report = await runner.RunAsync(["network-loopback-host"], CancellationToken.None);

        Assert.That(VerdictOf(report, "network-loopback-host"), Is.EqualTo(SandboxAcceptanceVerdict.Indeterminate));
    }

    [Test]
    public async Task CredentialsDirUnreadable_JudgesDeniedListing()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        // dir 分派：对验收工作区根（正对照）退出 0，对凭据目录按脚本回答拒绝。
        Func<PreparedProcessExecution, ScriptedOutcome> cmdAnswer(Func<PreparedProcessExecution, ScriptedOutcome>
                                                                      deniedAnswer) =>
            Cmd => Cmd.Arguments[^1].Contains(workspace.Root, StringComparison.OrdinalIgnoreCase)
                ? new ScriptedOutcome(0, StdoutChunks : [$" {workspace.Root} 的目录\n"])
                : deniedAnswer(Cmd);

        var denied = await RunEnabledCaseAsync(home, workspace, "credentials-dir-unreadable",
                                               OnlineScript(home.Components.OnlineUsername,
                                                            cmdAnswer(_ => new ScriptedOutcome(1,
                                                                   StderrChunks : ["Access is denied.\n"])),
                                                            CurlOutcome(0, "200")));
        Assert.That(VerdictOf(denied, "credentials-dir-unreadable"), Is.EqualTo(SandboxAcceptanceVerdict.Pass));

        // 拒绝文本经 runner 的 UTF-8 解码常为乱码（GBK 代码页）；判定不得依赖文本，非零退出即拒绝。
        var mojibake = await RunEnabledCaseAsync(home, workspace, "credentials-dir-unreadable",
                                                 OnlineScript(home.Components.OnlineUsername,
                                                              cmdAnswer(_ => new ScriptedOutcome(1,
                                                                     StderrChunks :
                                                                     ["\u00be\u00dc\u00be\u00f8\u00b7\u00c3\u00ce\u00ca.\n"])),
                                                              CurlOutcome(0, "200")));
        Assert.That(VerdictOf(mojibake, "credentials-dir-unreadable"), Is.EqualTo(SandboxAcceptanceVerdict.Pass));

        var leaked = await RunEnabledCaseAsync(home, workspace, "credentials-dir-unreadable",
                                               OnlineScript(home.Components.OnlineUsername,
                                                            cmdAnswer(_ => new ScriptedOutcome(0,
                                                                   StdoutChunks :
                                                                   ["sandbox_users.json\n"])),
                                                            CurlOutcome(0, "200")));
        Assert.That(VerdictOf(leaked, "credentials-dir-unreadable"), Is.EqualTo(SandboxAcceptanceVerdict.Fail));
    }

    [Test]
    public async Task CredentialsDirUnreadable_FailsWhenReadableControlFails()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        // 正对照失败（必然可读的验收工作区根也退出 1）说明是 shell/执行环境异常，
        // 即使凭据 dir 同样非零退出也必须记 FAIL，防止 provision 回归时空洞通过。
        var report = await RunEnabledCaseAsync(home, workspace, "credentials-dir-unreadable",
                                               OnlineScript(home.Components.OnlineUsername,
                                                            _ => new ScriptedOutcome(1,
                                                                   StderrChunks : ["Access is denied.\n"]),
                                                            CurlOutcome(0, "200")));

        Assert.That(VerdictOf(report, "credentials-dir-unreadable"), Is.EqualTo(SandboxAcceptanceVerdict.Fail));
        TestAssert.Contains("正对照", CaseDetail(report, "credentials-dir-unreadable") ?? string.Empty,
                            StringComparison.Ordinal);
    }

    [Test]
    public async Task EnabledFilter_RejectsRestrictedOnlyCases()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        var runner = new WindowsSandboxAcceptanceRunner(
            new ScriptedBackendFactory(home.Components, workspace.Root,
                                       OnlineScript(home.Components.OnlineUsername, OnlineEcho,
                                                    CurlOutcome(0, "200"))).Invoke,
            workspace.Root, (marker, _) => new NoopWorker(marker), limits : TestLimits(),
            networkPolicy : SandboxNetworkPolicy.Enabled);

        await TestAssert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(["network-denied"],
                                                                              CancellationToken.None));
    }

    // ---- CLI：--network enabled 端到端（组合守卫确认门之前的真实组合 + 注入工厂执行） ----

    [Test]
    public async Task CliVerify_NetworkEnabled_RunsOnlineSetAndWritesReport()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        using var fixture   = new CliFixture();
        await File.WriteAllTextAsync(Path.Combine(home.Components.SandboxHome, "codex-windows-managed-deny-probe.exe"),
                                     "MZ");
        await UserConfigStore.SaveAsync(fixture.UserConfigPath, EnabledConfig(home), CancellationToken.None);
        var factory = new ScriptedBackendFactory(home.Components, workspace.Root,
                                                 OnlineScript(home.Components.OnlineUsername,
                                                              FullCmdAnswer(workspace.Root),
                                                              CurlOutcome(0, "200")));

        var result = await SandboxVerifyCommand.ExecuteAsync(
                                                         fixture.CreateContext(),
                                                         CommandLine.Parse([
                                                             "sandbox", "verify", "--yes", "--network", "enabled",
                                                             "--workspace", workspace.Root
                                                         ]),
                                                         CancellationToken.None, factory.Invoke,
                                                         (marker, _) => new NoopWorker(marker));

        Assert.That(result, Is.EqualTo(0));
        Assert.That(fixture.Io.Output, Does.Contain("network  : enabled"));
        Assert.That(fixture.Io.Output, Does.Contain(home.Components.OnlineUsername));
        Assert.That(fixture.Io.Output, Does.Contain("PASS 8"));
        var reportFiles = Directory.GetFiles(home.Components.SandboxDirectory, "tinyharness-verify-*.json");
        Assert.That(reportFiles, Is.Not.Empty);
    }

    [Test]
    public async Task CliVerify_NetworkRestricted_AcceptsOptInNegativeControls()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        using var fixture   = new CliFixture();
        await UserConfigStore.SaveAsync(fixture.UserConfigPath, RestrictedConfig(home), CancellationToken.None);
        var factory = new ScriptedBackendFactory(home.Components, workspace.Root,
                                                 OnlineScript(home.Components.OfflineUsername, EmptyEcho,
                                                              CurlOutcome(7, "000")));

        var result = await SandboxVerifyCommand.ExecuteAsync(
                                                         fixture.CreateContext(),
                                                         CommandLine.Parse([
                                                             "sandbox", "verify", "--yes",
                                                             "--case", "env-proxy-echo,network-external",
                                                             "--workspace", workspace.Root
                                                         ]),
                                                         CancellationToken.None, factory.Invoke,
                                                         (marker, _) => new NoopWorker(marker));

        Assert.That(result, Is.EqualTo(0));
        Assert.That(fixture.Io.Output, Does.Contain("PASS 2"));
        Assert.That(fixture.Io.Output, Does.Contain("env-proxy-echo"));
        Assert.That(fixture.Io.Output, Does.Contain("network-external"));
    }

    [Test]
    public async Task CliVerify_NamedProxyUnderRestricted_ComposesPreviewWithoutProxyMissingFailure()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        using var fixture   = new CliFixture();
        // 常设 restricted + Named 代理：预览组合与生产 run 同源传入 NetworkSettings，
        // 不得因验收路径缺配置而误报"代理不存在"。
        await UserConfigStore.SaveAsync(fixture.UserConfigPath, RestrictedNamedProxyConfig(home),
                                        CancellationToken.None);
        var factory = new ScriptedBackendFactory(home.Components, workspace.Root,
                                                 OnlineScript(home.Components.OfflineUsername, EmptyEcho,
                                                              CurlOutcome(7, "000")));

        var result = await SandboxVerifyCommand.ExecuteAsync(
                                                         fixture.CreateContext(),
                                                         CommandLine.Parse([
                                                             "sandbox", "verify", "--yes",
                                                             "--case", "env-proxy-echo,network-external",
                                                             "--workspace", workspace.Root
                                                         ]),
                                                         CancellationToken.None, factory.Invoke,
                                                         (marker, _) => new NoopWorker(marker));

        Assert.That(result, Is.EqualTo(0));
        Assert.That(fixture.Io.Output, Does.Contain("PASS 2"));
        TestAssert.DoesNotContain("does not exist in the network settings", fixture.Io.Output,
                                  StringComparison.Ordinal);
    }

    [Test]
    public void ComposeCase_NamedProxyUnderRestricted_RequiresSameSourceNetworkSettings()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        var settings = SandboxSettings(home) with
        {
            Proxy = new SandboxProxyPolicy { Mode = SandboxProxyMode.Named, ProxyName = "local" }
        };
        var networkSettings = new NetworkSettings
        {
            Proxies =
            [
                new ProxyDefinition
                {
                    Id = "local", Name = "local", Protocol = ProxyProtocol.Http, Host = "127.0.0.1", Port = 47291
                }
            ]
        };

        // 与生产同源传入 NetworkSettings：restricted 下 Named 代理同样可解析，组合成功且保持
        // 离线形态（代理环境不进入目标环境）。
        var execution = SandboxVerifyCommand.ComposeCase(settings, SandboxPolicyKind.WorkspaceWrite, null,
                                                         workspace.Root,
                                                         new Dictionary<string, string>(StringComparer.Ordinal),
                                                         SandboxNetworkPolicy.Restricted, networkSettings);
        Assert.That(execution.Policy.NetworkEnabled, Is.False);
        Assert.That(execution.TargetEnvironment.ContainsKey("HTTP_PROXY"), Is.False);
        Assert.That(execution.TargetEnvironment.ContainsKey("ALL_PROXY"), Is.False);

        // 同源 NetworkSettings 缺失（历史缺陷形态）会让组合误判代理不存在而 fail closed。
        Assert.Throws<InvalidOperationException>(() => SandboxVerifyCommand.ComposeCase(
            settings, SandboxPolicyKind.WorkspaceWrite, null, workspace.Root,
            new Dictionary<string, string>(StringComparer.Ordinal), SandboxNetworkPolicy.Restricted, null));
    }

    [Test]
    public void ComposeCase_PassesNetworkPolicyAndNamedProxySettingsThrough()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        var settings = SandboxSettings(home) with
        {
            NetworkPolicy = SandboxNetworkPolicy.Enabled,
            Proxy         = new SandboxProxyPolicy { Mode = SandboxProxyMode.Named, ProxyName = "local" }
        };
        var networkSettings = new NetworkSettings
        {
            Proxies =
            [
                new ProxyDefinition
                {
                    Id = "local", Name = "local", Protocol = ProxyProtocol.Http, Host = "127.0.0.1", Port = 47291
                }
            ]
        };

        var execution = SandboxVerifyCommand.ComposeCase(settings, SandboxPolicyKind.WorkspaceWrite, null,
                                                         workspace.Root,
                                                         new Dictionary<string, string>(StringComparer.Ordinal),
                                                         SandboxNetworkPolicy.Enabled, networkSettings);

        // 克隆后的设置与 Network 配置必须同时到达组合器：在线策略决定代理注入，
        // 命名代理配置提供地址；缺任一都会让组合 fail closed 或丢失代理环境。
        Assert.That(execution.Policy.NetworkEnabled, Is.True);
        Assert.That(execution.TargetEnvironment["HTTP_PROXY"], Is.EqualTo("http://127.0.0.1:47291"));
        Assert.That(execution.TargetEnvironment["HTTPS_PROXY"], Is.EqualTo("http://127.0.0.1:47291"));
        Assert.That(execution.TargetEnvironment["NO_PROXY"], Is.EqualTo("localhost,127.0.0.1,::1"));
    }

    [Test]
    public void HelpText_SandboxDisclosesActualExternalProbeEndpoint()
    {
        // 帮助文本必须引用验收限额的实际默认探测端点，不得漂移到未实现的目标。
        TestAssert.Contains(WindowsSandboxAcceptanceLimits.Default.OnlineExternalProbeUrl, HelpText.Sandbox,
                            StringComparison.Ordinal);
        TestAssert.DoesNotContain("api.github.com", HelpText.Sandbox, StringComparison.Ordinal);
    }

    // ---- fixture 与脚本 ----

    private static async Task<WindowsSandboxAcceptanceReport> RunEnabledCaseAsync(
        SandboxTestHome                                                          home, TestTempDir workspace,
        string                                                                   caseName,
        Func<PreparedProcessExecution, CancellationToken, Task<ScriptedOutcome>> script)
    {
        var runner = new WindowsSandboxAcceptanceRunner(
            new ScriptedBackendFactory(home.Components, workspace.Root, script).Invoke,
            workspace.Root, (marker, _) => new NoopWorker(marker), limits : TestLimits(),
            networkPolicy : SandboxNetworkPolicy.Enabled);
        return await runner.RunAsync([caseName], CancellationToken.None);
    }

    private static async Task<WindowsSandboxAcceptanceReport> RunRestrictedCaseAsync(
        SandboxTestHome                                                          home, TestTempDir workspace,
        string                                                                   caseName,
        Func<PreparedProcessExecution, CancellationToken, Task<ScriptedOutcome>> script)
    {
        var runner = new WindowsSandboxAcceptanceRunner(
            new ScriptedBackendFactory(home.Components, workspace.Root, script).Invoke,
            workspace.Root, (marker, _) => new NoopWorker(marker), limits : TestLimits());
        return await runner.RunAsync([caseName], CancellationToken.None);
    }

    private static WindowsSandboxAcceptanceLimits TestLimits(string? loopbackTarget = NamedProxyHostPort) => new()
    {
        OverallDeadline           = TimeSpan.FromSeconds(60),
        ProcessScanTimeout        = TimeSpan.FromSeconds(2),
        ProcessScanInterval       = TimeSpan.FromMilliseconds(20),
        MarkerWaitTimeout         = TimeSpan.FromSeconds(1),
        TerminateCancelDelay      = TimeSpan.FromMilliseconds(60),
        WorkerHoldSeconds         = 10,
        LargeOutputTargetCharacters = 60_000,
        ModelOutputCharacterLimit   = 4_096,
        OnlineProxyHostPort       = NamedProxyHostPort,
        HostLoopbackProbeTarget   = loopbackTarget
    };

    /// <summary>
    ///     <para>
    ///         在线用例的满答脚本：whoami 回显指定账户、cmd 按 set/dir/copy 语义应答、curl 按
    ///         预设退出码与 http_code 应答、deny-probe 退出 0。未识别的可执行文件按退出 1 处理。
    ///     </para>
    ///     <para>
    ///         The answering script for the online cases: whoami echoes the given
    ///         account, cmd answers per set/dir/copy semantics, curl answers with the
    ///         preset exit code and http_code, and deny-probe exits 0. Unknown
    ///         executables answer with exit 1.
    ///     </para>
    /// </summary>
    private static Func<PreparedProcessExecution, CancellationToken, Task<ScriptedOutcome>> OnlineScript(
        string                                                                   account,
        Func<PreparedProcessExecution, ScriptedOutcome>                          cmdAnswer,
        Func<PreparedProcessExecution, ScriptedOutcome>                          curlAnswer) =>
        (plan, _) =>
        {
            switch (Path.GetFileName(plan.Executable))
            {
                case "whoami.exe" :
                    return Task.FromResult(new ScriptedOutcome(0, StdoutChunks : [$"test-machine\\{account}\n"]));
                case "cmd.exe" :
                    return Task.FromResult(cmdAnswer(plan));
                case "curl.exe" :
                    return Task.FromResult(curlAnswer(plan));
                case "codex-windows-managed-deny-probe.exe" :
                    return Task.FromResult(new ScriptedOutcome(0));
                default :
                    return Task.FromResult(new ScriptedOutcome(1));
            }
        };

    private static Func<PreparedProcessExecution, ScriptedOutcome> Echo(params string[] lines) => _ =>
        new ScriptedOutcome(0, StdoutChunks : lines.Select(line => line + "\n").ToArray());

    /// <summary>在线代理环境的标准反显：HTTP/HTTPS 同值并命中命名代理，NO_PROXY 为常量。</summary>
    private static ScriptedOutcome OnlineEcho(PreparedProcessExecution plan) =>
        Echo("HTTP_PROXY=http://127.0.0.1:47291",
             "HTTPS_PROXY=http://127.0.0.1:47291",
             "NO_PROXY=localhost,127.0.0.1,::1")(plan);

    private static ScriptedOutcome EmptyEcho(PreparedProcessExecution plan) => new(0, StdoutChunks : ["\n"]);

    /// <summary>
    ///     <para>
    ///         全集用的 cmd 分派工厂：set 反显代理、对验收工作区根的 dir 正对照退出 0、
    ///         其余 dir 拒绝访问、copy 写拒绝；其余退出 1。
    ///     </para>
    ///     <para>
    ///         The cmd dispatch factory for the full set: set echoes the proxy, dir on
    ///         the acceptance workspace root is the exit-0 positive control, any other
    ///         dir answers access denied, copy answers write-denied; everything else exits 1.
    ///     </para>
    /// </summary>
    private static Func<PreparedProcessExecution, ScriptedOutcome> FullCmdAnswer(string workspaceRoot) => plan =>
    {
        var command = plan.Arguments.Count > 0 ? plan.Arguments[^1] : string.Empty;
        if (command.StartsWith("set ", StringComparison.Ordinal)) return OnlineEcho(plan);

        if (command.StartsWith("dir ", StringComparison.Ordinal))
            return command.Contains(workspaceRoot, StringComparison.OrdinalIgnoreCase)
                ? new ScriptedOutcome(0, StdoutChunks : [$" {workspaceRoot} 的目录\n"])
                : new ScriptedOutcome(1, StderrChunks : ["Access is denied.\n"]);

        if (command.StartsWith("copy ", StringComparison.Ordinal)) return new ScriptedOutcome(1);

        return new ScriptedOutcome(1);
    };

    private static Func<PreparedProcessExecution, ScriptedOutcome> CurlOutcome(int exitCode, string httpCode) => _ =>
        new ScriptedOutcome(exitCode, StdoutChunks : exitCode == 0 ? [httpCode] : []);

    private static SandboxAcceptanceVerdict VerdictOf(WindowsSandboxAcceptanceReport report, string caseName) =>
        report.Cases.Single(result => result.Name == caseName).Verdict;

    private static string? ActualOf(WindowsSandboxAcceptanceReport report, string caseName) =>
        report.Cases.Single(result => result.Name == caseName).ActualSummary;

    private static string? CaseDetail(WindowsSandboxAcceptanceReport report, string caseName) =>
        report.Cases.Single(result => result.Name == caseName).Detail;

    private static UserConfig EnabledConfig(SandboxTestHome home) => new()
    {
        Settings = new UserConfigSettings
        {
            WindowsSandbox = new WindowsSandboxSettings
            {
                Enabled              = true,
                AllowTcpLoopback     = false,
                NetworkPolicy        = SandboxNetworkPolicy.Enabled,
                SetupExecutablePath  = home.Components.SetupExecutablePath,
                RunnerExecutablePath = home.Components.RunnerExecutablePath,
                SandboxHome          = home.Components.SandboxHome,
                Proxy                = new SandboxProxyPolicy { Mode = SandboxProxyMode.Named, ProxyName = "local" }
            },
            Network = new NetworkSettings
            {
                Proxies =
                [
                    new ProxyDefinition
                    {
                        Id = "local", Name = "local", Protocol = ProxyProtocol.Http, Host = "127.0.0.1", Port = 47291
                    }
                ]
            }
        }
    };

    private static UserConfig RestrictedConfig(SandboxTestHome home) => new()
    {
        Settings = new UserConfigSettings { WindowsSandbox = SandboxSettings(home) }
    };

    /// <summary>
    ///     <para>常设 restricted + Named 代理的隔离配置：Network 同源携带被引用的代理定义。</para>
    ///     <para>
    ///         An isolated config standing restricted with a Named proxy: Network
    ///         carries the referenced proxy definition from the same source.
    ///     </para>
    /// </summary>
    private static UserConfig RestrictedNamedProxyConfig(SandboxTestHome home) => new()
    {
        Settings = new UserConfigSettings
        {
            WindowsSandbox = SandboxSettings(home) with
            {
                Proxy = new SandboxProxyPolicy { Mode = SandboxProxyMode.Named, ProxyName = "local" }
            },
            Network = new NetworkSettings
            {
                Proxies =
                [
                    new ProxyDefinition
                    {
                        Id = "local", Name = "local", Protocol = ProxyProtocol.Http, Host = "127.0.0.1", Port = 47291
                    }
                ]
            }
        }
    };

    private static WindowsSandboxSettings SandboxSettings(SandboxTestHome home) => new()
    {
        Enabled              = true,
        SetupExecutablePath  = home.Components.SetupExecutablePath,
        RunnerExecutablePath = home.Components.RunnerExecutablePath,
        SandboxHome          = home.Components.SandboxHome
    };

    private sealed record ScriptedOutcome(
        int?                   ExitCode     = null,
        bool                   TimedOut     = false,
        IReadOnlyList<string>? StdoutChunks = null,
        IReadOnlyList<string>? StderrChunks = null);

    /// <summary>
    ///     <para>脚本化 fake 后端：把预设结果原样呈给运行器，不做任何真实进程操作。</para>
    ///     <para>A scripted fake backend: hands preset results to the runner without any real process activity.</para>
    /// </summary>
    private sealed class ScriptedBackend(
        Func<PreparedProcessExecution, CancellationToken, Task<ScriptedOutcome>> script) : IProcessExecutionBackend
    {
        public async Task<ProcessExecutionResult> ExecuteAsync(PreparedProcessExecution execution,
                                                               IProcessOutputCapture    stdoutCapture,
                                                               IProcessOutputCapture    stderrCapture,
                                                               CancellationToken        cancellationToken)
        {
            var outcome = await script(execution, cancellationToken).ConfigureAwait(false);
            foreach (var chunk in outcome.StdoutChunks ?? [])
                await stdoutCapture.AppendAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);

            await stdoutCapture.AppendAsync(ReadOnlyMemory<char>.Empty, cancellationToken).ConfigureAwait(false);
            foreach (var chunk in outcome.StderrChunks ?? [])
                await stderrCapture.AppendAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);

            await stderrCapture.AppendAsync(ReadOnlyMemory<char>.Empty, cancellationToken).ConfigureAwait(false);
            return new ProcessExecutionResult(outcome.ExitCode, outcome.TimedOut, null);
        }
    }

    /// <summary>
    ///     <para>
    ///         录制式工厂 fake：用真实的 SandboxPolicyResolver 对验收工作区解析策略，使读根/写根
    ///         判定与生产语义一致；组合守卫本身由 CLI 端到端测试覆盖。
    ///     </para>
    ///     <para>
    ///         A recording factory fake: the policy is resolved with the real
    ///         SandboxPolicyResolver against the acceptance workspace so verdicts
    ///         match production semantics; the composition guard itself is covered by
    ///         the CLI end-to-end tests.
    ///     </para>
    /// </summary>
    private sealed class ScriptedBackendFactory(
        WindowsSandboxComponents                                                 components,
        string                                                                   workspaceRoot,
        Func<PreparedProcessExecution, CancellationToken, Task<ScriptedOutcome>> script)
    {
        public WindowsSandboxExecution Invoke(
            SandboxPolicyKind kind, IReadOnlyDictionary<string, string>? extraEnvironment)
        {
            var tempRoot = Path.Combine(components.SandboxHome, "tmp");
            var targetEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["TEMP"] = tempRoot,
                ["TMP"]  = tempRoot
            };
            var policy = SandboxPolicyResolver.Resolve(kind, workspaceRoot, targetEnvironment,
                                                       allowTcpLoopback : components.AllowTcpLoopback);
            return new WindowsSandboxExecution(components, policy, targetEnvironment,
                                               ProcessExecutionPolicy.Host(), new ScriptedBackend(script));
        }
    }

    /// <summary>fake verify-worker：立即写 marker；enabled 矩阵不含 host-exit 用例，仅满足构造签名。A fake verify-worker: writes the marker at once; the enabled matrix has no host-exit case, this only satisfies the constructor signature.</summary>
    private sealed class NoopWorker : ISandboxVerifyWorkerProcess
    {
        public NoopWorker(string markerPath) => File.WriteAllText(markerPath, DateTimeOffset.UtcNow.ToString("O"));

        public void Kill()
        {
        }
    }

    private sealed class CliFixture : IDisposable
    {
        public CliFixture()
        {
            Root          = Path.Combine(Path.GetTempPath(), $"tinyharness-netmode-{Guid.NewGuid():N}");
            UserConfigPath = Path.Combine(Path.GetTempPath(), $"tinyharness-netmode-config-{Guid.NewGuid():N}",
                                          "user-config.json");
            Io = new RecordingConsole();
        }

        public string          Root          { get; }
        public string          UserConfigPath { get; }
        public RecordingConsole Io           { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);

            var configDirectory = Path.GetDirectoryName(UserConfigPath);
            if (Directory.Exists(configDirectory)) Directory.Delete(configDirectory, true);
        }

        public CommandContext CreateContext() => new()
        {
            Io               = Io,
            Credentials      = new FakeCredentialStore(),
            UserConfigPath   = UserConfigPath,
            WorkingDirectory = Root
        };
    }

    private sealed class RecordingConsole : ICliConsole
    {
        private readonly StringBuilder _output = new();

        public string Output => _output.ToString();

        public bool IsInteractive => false;

        public Task WriteAsync(string text, CancellationToken cancellationToken)
        {
            _output.Append(text);
            return Task.CompletedTask;
        }

        public Task WriteLineAsync(string text, CancellationToken cancellationToken)
        {
            _output.AppendLine(text);
            return Task.CompletedTask;
        }

        public Task WriteErrorLineAsync(string text, CancellationToken cancellationToken)
        {
            _output.AppendLine(text);
            return Task.CompletedTask;
        }

        public Task<string?> ReadLineAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<string?> ReadHiddenLineAsync(CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);
    }

    private sealed class FakeCredentialStore : ICredentialStore
    {
        public bool IsSupported => false;

        public void Save(string targetName, string secret)
        {
        }

        public string? Read(string targetName) => null;

        public bool Delete(string targetName) => false;
    }
}
