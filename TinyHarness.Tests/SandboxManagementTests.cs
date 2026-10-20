using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
///         sandbox 管理入口：sandbox status（严格只读）与 sandbox provision（显式机器级修改）的 parser、
///         CLI 命令与 Core provisioner 测试。全部离线：不启动真实 setup.exe、不触发 UAC、不修改机器
///         账户/ACL/注册表/防火墙；提权执行用 fake elevator 模拟。
///     </para>
///     <para>
///         Sandbox management-entry tests: the parser, the CLI command, and the Core
///         provisioner for sandbox status (strictly read-only) and sandbox
///         provision (the explicit machine-level mutation). Everything is offline:
///         no real setup.exe, no UAC, no machine account/ACL/registry/firewall
///         changes; elevation is simulated with a fake elevator.
///     </para>
/// </summary>
public sealed class SandboxManagementTests
{
    // ---- Parser ----

    [Test]
    public void Sandbox_BareVerb_IsAUsageError()
    {
        var error = TestAssert.Throws<CliUsageException>(() => CommandLine.Parse(["sandbox"]));

        TestAssert.Contains("status", error.Usage, StringComparison.Ordinal);
        TestAssert.Contains("provision", error.Usage, StringComparison.Ordinal);
    }

    [Test]
    public void Sandbox_UnknownSubcommand_IsAUsageError()
    {
        TestAssert.Throws<CliUsageException>(() => CommandLine.Parse(["sandbox", "deploy"]));
    }

    [Test]
    public void SandboxStatus_ParsesWithoutExtraOptions()
    {
        var options = CommandLine.Parse(["sandbox", "status"]);

        Assert.That(options.Kind, Is.EqualTo(CliCommandKind.Sandbox));
        Assert.That(options.Subcommand, Is.EqualTo("status"));
        Assert.That(options.AssumeYes, Is.False);
    }

    [Test]
    public void SandboxProvisionYes_Parses()
    {
        var options = CommandLine.Parse(["sandbox", "provision", "--yes"]);

        Assert.That(options.Kind, Is.EqualTo(CliCommandKind.Sandbox));
        Assert.That(options.Subcommand, Is.EqualTo("provision"));
        Assert.That(options.AssumeYes, Is.True);
    }

    [Test]
    public void SandboxProvision_WithoutYesStillParses()
    {
        var options = CommandLine.Parse(["sandbox", "provision"]);

        Assert.That(options.Kind, Is.EqualTo(CliCommandKind.Sandbox));
        Assert.That(options.Subcommand, Is.EqualTo("provision"));
        Assert.That(options.AssumeYes, Is.False);
    }

    [Test]
    public void SandboxStatusWithYes_IsAUsageError()
    {
        var error = TestAssert.Throws<CliUsageException>(() => CommandLine.Parse(["sandbox", "status", "--yes"]));

        TestAssert.Contains("sandbox status", error.Message, StringComparison.Ordinal);
    }

    [Test]
    public void SandboxHelpFlag_ReturnsTheSandboxHelpTopic()
    {
        var options = CommandLine.Parse(["sandbox", "--help"]);

        Assert.That(options.Kind, Is.EqualTo(CliCommandKind.Help));
        Assert.That(options.HelpTopic, Is.EqualTo("sandbox"));
    }

    // ---- status：严格只读 ----

    [Test]
    public async Task Status_UnconfiguredPrintsGuidanceAndExitsZero()
    {
        using var fixture = new CliFixture();

        var result = await SandboxCommand.ExecuteAsync(fixture.CreateContext(),
                                                       CommandLine.Parse(["sandbox", "status"]),
                                                       CancellationToken.None);

        Assert.That(result, Is.EqualTo(0));
        Assert.That(fixture.Io.Output, Does.Contain("未配置 windowsSandbox"));
        Assert.That(fixture.Io.Output, Does.Contain("settings.windowsSandbox"));
        Assert.That(fixture.Io.Output, Does.Contain("sandbox provision"));
    }

    [Test]
    public async Task Status_BlankSettingsAlsoCountAsUnconfigured()
    {
        using var fixture = new CliFixture();
        await UserConfigStore.SaveAsync(fixture.UserConfigPath, new UserConfig
        {
            Settings = new UserConfigSettings { WindowsSandbox = new WindowsSandboxSettings() }
        }, CancellationToken.None);

        var result = await SandboxCommand.ExecuteAsync(fixture.CreateContext(),
                                                       CommandLine.Parse(["sandbox", "status"]),
                                                       CancellationToken.None);

        Assert.That(result, Is.EqualTo(0));
        Assert.That(fixture.Io.Output, Does.Contain("未配置 windowsSandbox"));
    }

    [Test]
    public async Task Status_ReadyHomeExitsZeroWithOkSummary()
    {
        using var home    = new SandboxTestHome();
        using var fixture = new CliFixture();
        await UserConfigStore.SaveAsync(fixture.UserConfigPath, SandboxConfig(home, true), CancellationToken.None);

        var result = await SandboxCommand.ExecuteAsync(fixture.CreateContext(),
                                                       CommandLine.Parse(["sandbox", "status"]),
                                                       CancellationToken.None);

        Assert.That(result, Is.EqualTo(0));
        Assert.That(fixture.Io.Output, Does.Contain("[ok]"));
        Assert.That(fixture.Io.Output,
                    Does.Contain($"setup v{WindowsSandboxComponents.SetupVersion} / ipc v{WindowsSandboxComponents.IpcVersion}"));
        Assert.That(fixture.Io.Output, Does.Contain(home.Components.SetupExecutablePath));
    }

    [Test]
    public async Task Status_BrokenMarkerExitsOneWithFailLines()
    {
        using var home = new SandboxTestHome();
        await File.WriteAllTextAsync(home.Components.MarkerPath, "{\"version\":4}");
        using var fixture = new CliFixture();
        await UserConfigStore.SaveAsync(fixture.UserConfigPath, SandboxConfig(home), CancellationToken.None);

        var result = await SandboxCommand.ExecuteAsync(fixture.CreateContext(),
                                                       CommandLine.Parse(["sandbox", "status"]),
                                                       CancellationToken.None);

        Assert.That(result, Is.EqualTo(1));
        Assert.That(fixture.Io.Output, Does.Contain("[fail]"));
        Assert.That(fixture.Io.Output, Does.Contain("4"));
    }

    [Test]
    public async Task Status_MissingMarkerAndComponentsReportEachProblem()
    {
        using var home = new SandboxTestHome();
        File.Delete(home.Components.MarkerPath);
        File.Delete(home.Components.RunnerExecutablePath);
        using var fixture = new CliFixture();
        await UserConfigStore.SaveAsync(fixture.UserConfigPath, SandboxConfig(home), CancellationToken.None);

        var result = await SandboxCommand.ExecuteAsync(fixture.CreateContext(),
                                                       CommandLine.Parse(["sandbox", "status"]),
                                                       CancellationToken.None);

        Assert.That(result, Is.EqualTo(1));
        Assert.That(fixture.Io.Output, Does.Contain("Runner executable not found"));
        Assert.That(fixture.Io.Output, Does.Contain("not provisioned"));
    }

    [Test]
    public async Task Status_RelativePathsAreFailLinesNotCrashes()
    {
        using var fixture = new CliFixture();
        await UserConfigStore.SaveAsync(fixture.UserConfigPath, new UserConfig
        {
            Settings = new UserConfigSettings
            {
                WindowsSandbox = new WindowsSandboxSettings
                {
                    Enabled              = true,
                    SetupExecutablePath  = "relative\\setup.exe",
                    RunnerExecutablePath = "C:\\sb\\runner.exe",
                    SandboxHome          = "relative\\home"
                }
            }
        }, CancellationToken.None);

        var result = await SandboxCommand.ExecuteAsync(fixture.CreateContext(),
                                                       CommandLine.Parse(["sandbox", "status"]),
                                                       CancellationToken.None);

        Assert.That(result, Is.EqualTo(1));
        Assert.That(fixture.Io.Output, Does.Contain("setupExecutablePath"));
        Assert.That(fixture.Io.Output, Does.Contain("sandboxHome"));
    }

    [Test]
    public async Task Status_ChangesNoFileOrDirectory()
    {
        using var home    = new SandboxTestHome();
        using var fixture = new CliFixture();
        await UserConfigStore.SaveAsync(fixture.UserConfigPath, SandboxConfig(home, true), CancellationToken.None);
        var beforeHome    = Snapshot(home.Components.SandboxHome);
        var beforeHomeDir = Snapshot(Path.GetDirectoryName(fixture.UserConfigPath)!);

        var result = await SandboxCommand.ExecuteAsync(fixture.CreateContext(),
                                                       CommandLine.Parse(["sandbox", "status"]),
                                                       CancellationToken.None);

        Assert.That(result, Is.EqualTo(0));
        Assert.That(Snapshot(home.Components.SandboxHome), Is.EqualTo(beforeHome));
        Assert.That(Snapshot(Path.GetDirectoryName(fixture.UserConfigPath)!), Is.EqualTo(beforeHomeDir));
    }

    [Test]
    public async Task Status_NonWindowsNotesThePlatformLimitation()
    {
        if (OperatingSystem.IsWindows()) return;

        using var home    = new SandboxTestHome();
        using var fixture = new CliFixture();
        await UserConfigStore.SaveAsync(fixture.UserConfigPath, SandboxConfig(home, true), CancellationToken.None);

        var result = await SandboxCommand.ExecuteAsync(fixture.CreateContext(),
                                                       CommandLine.Parse(["sandbox", "status"]),
                                                       CancellationToken.None);

        Assert.That(fixture.Io.Output, Does.Contain("不是 Windows"));
    }

    // ---- provision：确认门与校验都在提权之前 ----

    [Test]
    public async Task Provision_NonInteractiveWithoutYes_RefusesWithoutElevation()
    {
        using var home     = new SandboxTestHome();
        using var fixture  = new CliFixture();
        var       elevator = new FakeSetupElevator();
        await UserConfigStore.SaveAsync(fixture.UserConfigPath, SandboxConfig(home), CancellationToken.None);

        var result = await SandboxCommand.ExecuteAsync(fixture.CreateContext(),
                                                       CommandLine.Parse(["sandbox", "provision"]),
                                                       CancellationToken.None,
                                                       new WindowsSandboxProvisioner(elevator));

        Assert.That(result, Is.EqualTo(1));
        Assert.That(elevator.Requests, Is.Empty);
        Assert.That(fixture.Io.Output, Does.Contain("--yes"));
        Assert.That(File.Exists(Path.Combine(home.Components.SandboxDirectory, "tinyharness-provision.jsonl")),
                    Is.False);
    }

    [Test]
    public async Task Provision_MissingSetupExecutable_FailsWithoutElevation()
    {
        using var home = new SandboxTestHome();
        File.Delete(home.Components.SetupExecutablePath);
        using var fixture  = new CliFixture();
        var       elevator = new FakeSetupElevator();
        await UserConfigStore.SaveAsync(fixture.UserConfigPath, SandboxConfig(home), CancellationToken.None);

        var result = await SandboxCommand.ExecuteAsync(fixture.CreateContext(),
                                                       CommandLine.Parse(["sandbox", "provision", "--yes"]),
                                                       CancellationToken.None,
                                                       new WindowsSandboxProvisioner(elevator));

        Assert.That(result, Is.EqualTo(1));
        Assert.That(elevator.Requests, Is.Empty);
        Assert.That(fixture.Io.Output, Does.Contain("setup 可执行文件不存在"));
    }

    [Test]
    public async Task Provision_MissingRequiredSettings_FailsWithClearError()
    {
        using var fixture  = new CliFixture();
        var       elevator = new FakeSetupElevator();
        await UserConfigStore.SaveAsync(fixture.UserConfigPath, new UserConfig
        {
            Settings = new UserConfigSettings { WindowsSandbox = new WindowsSandboxSettings { Enabled = true } }
        }, CancellationToken.None);

        var result = await SandboxCommand.ExecuteAsync(fixture.CreateContext(),
                                                       CommandLine.Parse(["sandbox", "provision", "--yes"]),
                                                       CancellationToken.None,
                                                       new WindowsSandboxProvisioner(elevator));

        Assert.That(result, Is.EqualTo(1));
        Assert.That(elevator.Requests, Is.Empty);
        Assert.That(fixture.Io.Output, Does.Contain("setupExecutablePath"));
    }

    [Test]
    public async Task Provision_OversizedPayloadIsRejectedWithoutElevation()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home     = new SandboxTestHome();
        using var fixture  = new CliFixture();
        var       elevator = new FakeSetupElevator();
        // 足够多的不同绝对写根，把 base64 载荷推过 UAC 位置参数上限（该通道无环境变量分段）。
        var roots = Enumerable.Range(0, 1500)
                              .Select(i => $"C:\\tinyharness-test-roots\\package-cache-{i:D5}\\long-segment")
                              .ToArray();
        await UserConfigStore.SaveAsync(fixture.UserConfigPath,
                                        new UserConfig
                                        {
                                            Settings = new UserConfigSettings
                                            {
                                                WindowsSandbox = SandboxSettings(home) with
                                                {
                                                    AdditionalWriteRoots = roots
                                                }
                                            }
                                        },
                                        CancellationToken.None);

        var result = await SandboxCommand.ExecuteAsync(fixture.CreateContext(),
                                                       CommandLine.Parse(["sandbox", "provision", "--yes"]),
                                                       CancellationToken.None,
                                                       new WindowsSandboxProvisioner(elevator));

        Assert.That(result, Is.EqualTo(1));
        Assert.That(elevator.Requests, Is.Empty);
        Assert.That(fixture.Io.Output, Does.Contain("additionalWriteRoots"));
    }

    [Test]
    public async Task Provision_SuccessfulElevationReportsReadyAndAppendsAudit()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home     = new SandboxTestHome();
        using var fixture  = new CliFixture();
        var       elevator = new FakeSetupElevator { Result = SandboxSetupElevationResult.Completed(0) };
        await UserConfigStore.SaveAsync(fixture.UserConfigPath, SandboxConfig(home, true), CancellationToken.None);

        var result = await SandboxCommand.ExecuteAsync(fixture.CreateContext(),
                                                       CommandLine.Parse(["sandbox", "provision", "--yes"]),
                                                       CancellationToken.None,
                                                       new WindowsSandboxProvisioner(elevator));

        Assert.That(result, Is.EqualTo(0));
        var request = TestAssert.Single(elevator.Requests);
        Assert.That(request.Components.SetupExecutablePath, Is.EqualTo(home.Components.SetupExecutablePath));
        Assert.That(request.Components.SandboxHome, Is.EqualTo(request.Components.SandboxHome));
        Assert.That(fixture.Io.Output, Does.Contain("机器级状态"));
        Assert.That(fixture.Io.Output, Does.Contain("TinyHarnessUsers"));
        Assert.That(fixture.Io.Output, Does.Contain("没有卸载或回滚"));
        Assert.That(fixture.Io.Output, Does.Contain("[ok]"));

        var auditPath = Path.Combine(home.Components.SandboxDirectory, "tinyharness-provision.jsonl");
        Assert.That(File.Exists(auditPath), Is.True);
        var record = await ParseSingleAuditRecordAsync(auditPath);
        Assert.That(record["outcome"]!.GetValue<string>(), Is.EqualTo("completed"));
        Assert.That(record["exitCode"]!.GetValue<int>(), Is.EqualTo(0));
        Assert.That(record["mode"]!.GetValue<string>(), Is.EqualTo("full"));
        Assert.That(record["refreshOnly"]!.GetValue<bool>(), Is.False);
        Assert.That(record["payloadSha256"]!.GetValue<string>(), Does.Match("^[0-9A-F]{64}$"));
    }

    [Test]
    public async Task Provision_HelperFailureReportsSetupErrorAndAuditsFailure()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home = new SandboxTestHome();
        await File.WriteAllTextAsync(home.Components.SetupErrorPath,
                                     """{"code":"create_accounts_failed","message":"net user error 5"}""");
        using var fixture  = new CliFixture();
        var       elevator = new FakeSetupElevator { Result = SandboxSetupElevationResult.Completed(1) };
        await UserConfigStore.SaveAsync(fixture.UserConfigPath, SandboxConfig(home), CancellationToken.None);

        var result = await SandboxCommand.ExecuteAsync(fixture.CreateContext(),
                                                       CommandLine.Parse(["sandbox", "provision", "--yes"]),
                                                       CancellationToken.None,
                                                       new WindowsSandboxProvisioner(elevator));

        Assert.That(result, Is.EqualTo(1));
        Assert.That(fixture.Io.Output, Does.Contain("退出码 1"));
        Assert.That(fixture.Io.Output, Does.Contain("create_accounts_failed"));
        Assert.That(fixture.Io.Output, Does.Contain("net user error 5"));

        var auditPath = Path.Combine(home.Components.SandboxDirectory, "tinyharness-provision.jsonl");
        Assert.That(File.Exists(auditPath), Is.True);
        var record = await ParseSingleAuditRecordAsync(auditPath);
        Assert.That(record["outcome"]!.GetValue<string>(), Is.EqualTo("failed"));
        Assert.That(record["exitCode"]!.GetValue<int>(), Is.EqualTo(1));
        Assert.That(record["error"]!.GetValue<string>(), Does.Contain("create_accounts_failed"));
    }

    [Test]
    public async Task Provision_UacDeclineReportsUserRefusalAndAudits()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home     = new SandboxTestHome();
        using var fixture  = new CliFixture();
        var       elevator = new FakeSetupElevator { Result = SandboxSetupElevationResult.Declined };
        await UserConfigStore.SaveAsync(fixture.UserConfigPath, SandboxConfig(home), CancellationToken.None);

        var result = await SandboxCommand.ExecuteAsync(fixture.CreateContext(),
                                                       CommandLine.Parse(["sandbox", "provision", "--yes"]),
                                                       CancellationToken.None,
                                                       new WindowsSandboxProvisioner(elevator));

        Assert.That(result, Is.EqualTo(1));
        Assert.That(fixture.Io.Output, Does.Contain("拒绝了提权"));
        var record = await ParseSingleAuditRecordAsync(Path.Combine(home.Components.SandboxDirectory,
                                                                    "tinyharness-provision.jsonl"));
        Assert.That(record["outcome"]!.GetValue<string>(), Is.EqualTo("declined"));
    }

    [Test]
    public async Task Provision_ElevationTimeoutReportsUnknownStateAndAudits()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home     = new SandboxTestHome();
        using var fixture  = new CliFixture();
        var       elevator = new FakeSetupElevator { Result = SandboxSetupElevationResult.TimedOut };
        await UserConfigStore.SaveAsync(fixture.UserConfigPath, SandboxConfig(home), CancellationToken.None);

        var result = await SandboxCommand.ExecuteAsync(fixture.CreateContext(),
                                                       CommandLine.Parse(["sandbox", "provision", "--yes"]),
                                                       CancellationToken.None,
                                                       new WindowsSandboxProvisioner(elevator));

        Assert.That(result, Is.EqualTo(1));
        Assert.That(fixture.Io.Output, Does.Contain("状态未知"));
        Assert.That(fixture.Io.Output, Does.Contain("sandbox status"));
        var record = await ParseSingleAuditRecordAsync(Path.Combine(home.Components.SandboxDirectory,
                                                                    "tinyharness-provision.jsonl"));
        Assert.That(record["outcome"]!.GetValue<string>(), Is.EqualTo("not-finished"));
    }

    [Test]
    public async Task Provision_InteractiveConfirmationCancelsCleanly()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home     = new SandboxTestHome();
        using var fixture  = new CliFixture(["n"], true);
        var       elevator = new FakeSetupElevator();
        await UserConfigStore.SaveAsync(fixture.UserConfigPath, SandboxConfig(home), CancellationToken.None);

        var result = await SandboxCommand.ExecuteAsync(fixture.CreateContext(),
                                                       CommandLine.Parse(["sandbox", "provision"]),
                                                       CancellationToken.None,
                                                       new WindowsSandboxProvisioner(elevator));

        Assert.That(result, Is.EqualTo(0));
        Assert.That(elevator.Requests, Is.Empty);
        Assert.That(fixture.Io.Output, Does.Contain("已取消"));
    }

    // ---- Core：provisioner payload 与审计 ----

    [Test]
    public void Provisioner_BuildPlanDerivesPayloadFromSharedPolicySemantics()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home        = new SandboxTestHome();
        using var workspace   = new TestTempDir();
        var       settings    = SandboxSettings(home);
        var       provisioner = new WindowsSandboxProvisioner(new FakeSetupElevator());

        var plan = provisioner.BuildPlan(settings, workspace.Root, "TestUser");

        Assert.That(plan.Payload.Version, Is.EqualTo(5u));
        Assert.That(plan.Payload.RefreshOnly, Is.False);
        Assert.That(plan.Payload.Mode, Is.EqualTo("full"));
        Assert.That(plan.Payload.ProxyPorts, Is.Empty);
        Assert.That(plan.Payload.RealUser, Is.EqualTo("TestUser"));
        Assert.That(plan.Payload.SandboxHome, Is.EqualTo(home.Components.SandboxHome));
        Assert.That(plan.Payload.CommandWorkingDirectory, Is.EqualTo(workspace.Root));
        Assert.That(plan.Payload.OfflineUsername, Is.EqualTo(WindowsSandboxComponents.DefaultOfflineUsername));
        Assert.That(plan.Payload.OnlineUsername, Is.EqualTo(WindowsSandboxComponents.DefaultOnlineUsername));

        // 与 composer/policy 相同的推导：workspace-write 授予工作区加沙箱临时根；读根覆盖
        // 工作区与临时根；元数据子目录保持 deny-write 保护。
        var expectedTemp = Path.Combine(home.Components.SandboxHome, "tmp");
        Assert.That(plan.Payload.ReadRoots, Is.EqualTo([workspace.Root, expectedTemp]));
        Assert.That(plan.Payload.WriteRoots, Is.EqualTo([workspace.Root, expectedTemp]));
        Assert.That(plan.Payload.DenyWritePaths, Is.Empty,
                    "Missing metadata paths must not be materialized by the setup helper.");

        Assert.That(plan.TempRoot, Is.EqualTo(expectedTemp));
        Assert.That(Directory.Exists(expectedTemp), Is.False, "BuildPlan must not create the temp root");
        Assert.That(plan.Base64Payload.Length <= ProcessSandboxSetupInvoker.PayloadArgumentCharacterLimit, Is.True);
        Assert.That(plan.PayloadSha256, Is.EqualTo(Convert.ToHexString(SHA256.HashData(plan.PayloadJson))));
        Assert.That(plan.AuditPath,
                    Is.EqualTo(Path.Combine(home.Components.SandboxDirectory, "tinyharness-provision.jsonl")));
    }

    [Test]
    public void Provisioner_BlankHomeUsesFixedDefaultAndForkAccountNames()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        var       settings  = SandboxSettings(home) with { SandboxHome = "" };

        var plan =
            new WindowsSandboxProvisioner(new FakeSetupElevator()).BuildPlan(settings, workspace.Root, "TestUser");

        Assert.That(plan.Payload.SandboxHome, Is.EqualTo(WindowsSandboxComponents.DefaultSandboxHome));
        Assert.That(plan.Components.SandboxHome, Is.EqualTo(WindowsSandboxComponents.DefaultSandboxHome));
        Assert.That(plan.Payload.OfflineUsername, Is.EqualTo("TinyHarnessOffline"));
        Assert.That(plan.Payload.OnlineUsername, Is.EqualTo("TinyHarnessOnline"));
        // 固定默认 home 必须位于所有工作区写根之外，否则组合会因其可被篡改而拒绝。
        Assert.That(Workspace.IsInside(workspace.Root, plan.Components.SandboxHome), Is.False);
    }

    [Test]
    public void Provisioner_RefusesHomeCarryingLegacyCodexMarkerOrCredentials()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        File.WriteAllText(home.Components.MarkerPath,
                          """{"version":5,"offline_username":"CodexSandboxOffline","online_username":"CodexSandboxOnline","created_at":"2026-10-01T00:00:00Z"}""");

        var markerError = TestAssert.Throws<InvalidOperationException>(() =>
                                                                           new
                                                                                   WindowsSandboxProvisioner(new
                                                                                                FakeSetupElevator())
                                                                              .BuildPlan(SandboxSettings(home),
                                                                                        workspace.Root, "TestUser"));
        TestAssert.Contains("legacy Codex account 'CodexSandboxOffline'", markerError.Message,
                            StringComparison.Ordinal);
        TestAssert.Contains("setup marker", markerError.Message, StringComparison.Ordinal);

        File.WriteAllText(home.Components.MarkerPath,
                          """{"version":5,"offline_username":"TinyHarnessOffline","online_username":"TinyHarnessOnline","created_at":"2026-10-01T00:00:00Z"}""");
        File.WriteAllText(home.Components.UsersFilePath,
                          """{"version":5,"offline":{"username":"TinyHarnessOffline","password":"QUJDRA=="},"online":{"username":"CodexSandboxOnline","password":"QUJDRA=="}}""");

        var credentialsError = TestAssert.Throws<InvalidOperationException>(() =>
                                                                                new
                                                                                        WindowsSandboxProvisioner(new
                                                                                                     FakeSetupElevator())
                                                                                   .BuildPlan(SandboxSettings(home),
                                                                                             workspace.Root,
                                                                                             "TestUser"));
        TestAssert.Contains("account credentials", credentialsError.Message, StringComparison.Ordinal);
    }

    [Test]
    public void Provisioner_PayloadTooLargeThrowsBeforeAnyElevation()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        var roots = Enumerable.Range(0, 1500)
                              .Select(i => $"C:\\tinyharness-test-roots\\package-cache-{i:D5}\\long-segment")
                              .ToArray();
        var settings = SandboxSettings(home) with { AdditionalWriteRoots = roots };

        var error = TestAssert.Throws<InvalidOperationException>(() =>
                                                                     new
                                                                             WindowsSandboxProvisioner(new
                                                                                          FakeSetupElevator())
                                                                        .BuildPlan(settings, workspace.Root,
                                                                                  "TestUser"));

        TestAssert.Contains("additionalWriteRoots", error.Message, StringComparison.Ordinal);
    }

    [Test]
    public void Provisioner_RejectsRelativeAdditionalWriteRoots()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        var       settings  = SandboxSettings(home) with { AdditionalWriteRoots = ["relative-root"] };

        TestAssert.Throws<ArgumentException>(() =>
                                                 new WindowsSandboxProvisioner(new FakeSetupElevator())
                                                    .BuildPlan(settings, workspace.Root, "TestUser"));
    }

    [Test]
    public async Task Provisioner_CancelledWaitStillWritesNotFinishedAudit()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        var provisioner = new WindowsSandboxProvisioner(new FakeSetupElevator
                                                            { Result = SandboxSetupElevationResult.Cancelled });
        var plan = provisioner.BuildPlan(SandboxSettings(home), workspace.Root, "TestUser");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await provisioner.ProvisionAsync(plan, cts.Token);

        Assert.That(result.Outcome, Is.EqualTo(SandboxProvisionOutcome.NotFinished));
        var record = await ParseSingleAuditRecordAsync(plan.AuditPath);
        Assert.That(record["outcome"]!.GetValue<string>(), Is.EqualTo("not-finished"));
    }

    [Test]
    public void SandboxProvisionAuditRecord_RoundTripsThroughSourceGeneration()
    {
        var record = new SandboxProvisionAuditRecord
        {
            TimestampUtc  = "2026-10-10T00:00:00Z",
            Outcome       = "failed",
            ExitCode      = 1,
            Mode          = "full",
            RefreshOnly   = false,
            PayloadSha256 = "ABCDEF",
            Error         = "create_accounts_failed: net user error 5"
        };

        var json = JsonSerializer.Serialize(record, WindowsSandboxJsonContext.Default.SandboxProvisionAuditRecord);
        var back = JsonSerializer.Deserialize(json, WindowsSandboxJsonContext.Default.SandboxProvisionAuditRecord);

        Assert.That(back, Is.EqualTo(record));
        TestAssert.Contains("\"outcome\":\"failed\"", json, StringComparison.Ordinal);
        TestAssert.Contains("\"payloadSha256\":\"ABCDEF\"", json, StringComparison.Ordinal);
        // 完整载荷绝不进入审计行。
        TestAssert.DoesNotContain("read_roots", json, StringComparison.Ordinal);
    }

    // ---- Core：执行路径未 provision 时 fail closed 且不调 setup ----

    [Test]
    public async Task Backend_FailsClosedWhenNotProvisionedWithoutCallingSetup()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home = new SandboxTestHome();
        File.Delete(home.Components.MarkerPath);
        var invoker = new FakeSetupInvoker();
        var launcher = new FakeSandboxRunnerLauncher
        {
            RunnerBody = (_, _, _) => Task.CompletedTask
        };
        var targetEnvironment = new Dictionary<string, string> { ["TEMP"] = home.Components.SandboxHome };
        var policy = SandboxPolicyResolver.Resolve(SandboxPolicyKind.WorkspaceWrite, home.Components.SandboxHome,
                                                   targetEnvironment);
        var backend = new WindowsSandboxBackend(home.Components, policy, targetEnvironment,
                                                setupInvoker : invoker, credentialSource : new FakeCredentialSource(),
                                                desktopFactory : new FakeDesktopFactory(),
                                                runnerLauncher : launcher,
                                                allowNullDeviceAccess : _ => { });

        var exception = await TestAssert.ThrowsAsync<ProcessExecutionStartException>(() => backend.ExecuteAsync(
                      new PreparedProcessExecution(
                                                   "cmd.exe", ["/c", "whoami"],
                                                   home.Components.SandboxHome, 30,
                                                   false, null),
                      new RecordingOutputCapture(),
                      new RecordingOutputCapture(),
                      CancellationToken.None));

        TestAssert.Contains("not ready", exception.Message, StringComparison.Ordinal);
        Assert.That(invoker.RefreshPayloads, Is.Empty);
        Assert.That(launcher.LastRequest, Is.Null);
    }

    // ---- fixture 与 fake ----

    private static WindowsSandboxSettings SandboxSettings(SandboxTestHome home, bool enabled = false) => new()
    {
        Enabled              = enabled,
        SetupExecutablePath  = home.Components.SetupExecutablePath,
        RunnerExecutablePath = home.Components.RunnerExecutablePath,
        SandboxHome          = home.Components.SandboxHome
    };

    private static UserConfig SandboxConfig(SandboxTestHome home, bool enabled = false) => new()
        { Settings = new UserConfigSettings { WindowsSandbox = SandboxSettings(home, enabled) } };

    private static async Task<JsonObject> ParseSingleAuditRecordAsync(string auditPath)
    {
        var lines  = await File.ReadAllLinesAsync(auditPath);
        var record = TestAssert.Single(lines, line => !string.IsNullOrWhiteSpace(line));
        return JsonNode.Parse(record)!.AsObject();
    }

    /// <summary>
    ///     <para>递归快照目录树（相对路径 + 类型 + 长度），用于只读性断言。</para>
    ///     <para>A recursive directory snapshot (relative paths + kind + length) for read-only assertions.</para>
    /// </summary>
    private static Dictionary<string, string> Snapshot(string root)
    {
        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            snapshot[Path.GetRelativePath(root, directory)] = "dir";

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            snapshot[Path.GetRelativePath(root, file)] = $"file:{new FileInfo(file).Length}";

        return snapshot;
    }

    /// <summary>
    ///     <para>录制式 fake 提权器：记录每次请求并返回脚本化结果，绝不启动进程或触发 UAC。</para>
    ///     <para>
    ///         A recording fake elevator: it records requests and returns the
    ///         scripted result without ever starting a process or raising UAC.
    ///     </para>
    /// </summary>
    private sealed class FakeSetupElevator : ISandboxSetupElevator
    {
        public List<(WindowsSandboxComponents Components, string Base64Payload)> Requests { get; } = [];

        public SandboxSetupElevationResult Result { get; set; } = SandboxSetupElevationResult.Completed0;

        public Task<SandboxSetupElevationResult> ElevateAsync(WindowsSandboxComponents components,
                                                              string                   base64Payload,
                                                              CancellationToken        cancellationToken)
        {
            Requests.Add((components, base64Payload));
            return Task.FromResult(Result);
        }
    }

    private sealed class CliFixture(IReadOnlyList<string>? input = null) : IDisposable
    {
        public CliFixture(IReadOnlyList<string> input, bool interactive) : this(input) => Io.Interactive = interactive;

        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"tinyharness-sandbox-{Guid.NewGuid():N}");

        public string UserConfigPath { get; } = Path.Combine(Path.GetTempPath(),
                                                             $"tinyharness-sandbox-config-{Guid.NewGuid():N}",
                                                             "user-config.json");

        public FakeCliConsole Io { get; } = new(input);

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

    private sealed class FakeCliConsole(IReadOnlyList<string>? input) : ICliConsole
    {
        private readonly StringBuilder  _error  = new();
        private readonly Queue<string?> _input  = new(input ?? []);
        private readonly StringBuilder  _output = new();

        public bool Interactive { get; set; }

        public string Output => _output.ToString();

        public string Error => _error.ToString();

        public bool IsInteractive => Interactive;

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
            _error.AppendLine(text);
            return Task.CompletedTask;
        }

        public Task<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_input.Count > 0 ? _input.Dequeue() : null);

        public Task<string?> ReadHiddenLineAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_input.Count > 0 ? _input.Dequeue() : null);
    }

    private sealed class FakeCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, string> _entries = new(StringComparer.Ordinal);

        public bool IsSupported => false;

        public void Save(string targetName, string secret)
        {
            _entries[targetName] = secret;
        }

        public string? Read(string targetName) => _entries.GetValueOrDefault(targetName);

        public bool Delete(string targetName) => _entries.Remove(targetName);
    }
}
