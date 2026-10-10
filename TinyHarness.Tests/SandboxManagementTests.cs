using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TinyHarness.Cli.Commands;
using TinyHarness.Cli.Exceptions;
using TinyHarness.Cli.Models;
using TinyHarness.Cli.Services;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.Configuration;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

namespace TinyHarness.Tests;

/// <summary>
///     M11.4 管理入口：sandbox status（严格只读）与 sandbox provision（显式机器级修改）的 parser、
///     CLI 命令与 Core provisioner 测试。全部离线：不启动真实 setup.exe、不触发 UAC、不修改机器
///     账户/ACL/注册表/防火墙；提权执行用 fake elevator 模拟。
///     M11.4 management entry tests: the parser, the CLI command, and the Core
///     provisioner for sandbox status (strictly read-only) and sandbox
///     provision (the explicit machine-level mutation). Everything is offline:
///     no real setup.exe, no UAC, no machine account/ACL/registry/firewall
///     changes; elevation is simulated with a fake elevator.
/// </summary>
public sealed class SandboxManagementTests
{
    // ---- Parser ----

    [Fact]
    public void Sandbox_BareVerb_IsAUsageError()
    {
        var error = Assert.Throws<CliUsageException>(() => CommandLine.Parse(["sandbox"]));

        Assert.Contains("status", error.Usage, StringComparison.Ordinal);
        Assert.Contains("provision", error.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public void Sandbox_UnknownSubcommand_IsAUsageError()
    {
        Assert.Throws<CliUsageException>(() => CommandLine.Parse(["sandbox", "deploy"]));
    }

    [Fact]
    public void SandboxStatus_ParsesWithoutExtraOptions()
    {
        var options = CommandLine.Parse(["sandbox", "status"]);

        Assert.Equal(CliCommandKind.Sandbox, options.Kind);
        Assert.Equal("status", options.Subcommand);
        Assert.False(options.AssumeYes);
    }

    [Fact]
    public void SandboxProvisionYes_Parses()
    {
        var options = CommandLine.Parse(["sandbox", "provision", "--yes"]);

        Assert.Equal(CliCommandKind.Sandbox, options.Kind);
        Assert.Equal("provision", options.Subcommand);
        Assert.True(options.AssumeYes);
    }

    [Fact]
    public void SandboxProvision_WithoutYesStillParses()
    {
        var options = CommandLine.Parse(["sandbox", "provision"]);

        Assert.Equal(CliCommandKind.Sandbox, options.Kind);
        Assert.Equal("provision", options.Subcommand);
        Assert.False(options.AssumeYes);
    }

    [Fact]
    public void SandboxStatusWithYes_IsAUsageError()
    {
        var error = Assert.Throws<CliUsageException>(() => CommandLine.Parse(["sandbox", "status", "--yes"]));

        Assert.Contains("sandbox status", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SandboxHelpFlag_ReturnsTheSandboxHelpTopic()
    {
        var options = CommandLine.Parse(["sandbox", "--help"]);

        Assert.Equal(CliCommandKind.Help, options.Kind);
        Assert.Equal("sandbox", options.HelpTopic);
    }

    // ---- status：严格只读 ----

    [Fact]
    public async Task Status_UnconfiguredPrintsGuidanceAndExitsZero()
    {
        using var fixture = new CliFixture();

        var result = await SandboxCommand.ExecuteAsync(fixture.CreateContext(),
                                                       CommandLine.Parse(["sandbox", "status"]),
                                                       CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Contains("未配置 windowsSandbox", fixture.Io.Output);
        Assert.Contains("settings.windowsSandbox", fixture.Io.Output);
        Assert.Contains("sandbox provision", fixture.Io.Output);
    }

    [Fact]
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

        Assert.Equal(0, result);
        Assert.Contains("未配置 windowsSandbox", fixture.Io.Output);
    }

    [Fact]
    public async Task Status_ReadyHomeExitsZeroWithOkSummary()
    {
        using var home = new SandboxTestHome();
        using var fixture = new CliFixture();
        await UserConfigStore.SaveAsync(fixture.UserConfigPath, SandboxConfig(home, enabled : true), CancellationToken.None);

        var result = await SandboxCommand.ExecuteAsync(fixture.CreateContext(),
                                                       CommandLine.Parse(["sandbox", "status"]),
                                                       CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Contains("[ok]", fixture.Io.Output);
        Assert.Contains($"setup v{WindowsSandboxComponents.SetupVersion} / ipc v{WindowsSandboxComponents.IpcVersion}",
                        fixture.Io.Output);
        Assert.Contains(home.Components.SetupExecutablePath, fixture.Io.Output);
    }

    [Fact]
    public async Task Status_BrokenMarkerExitsOneWithFailLines()
    {
        using var home = new SandboxTestHome();
        await File.WriteAllTextAsync(home.Components.MarkerPath, "{\"version\":4}");
        using var fixture = new CliFixture();
        await UserConfigStore.SaveAsync(fixture.UserConfigPath, SandboxConfig(home), CancellationToken.None);

        var result = await SandboxCommand.ExecuteAsync(fixture.CreateContext(),
                                                       CommandLine.Parse(["sandbox", "status"]),
                                                       CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Contains("[fail]", fixture.Io.Output);
        Assert.Contains("4", fixture.Io.Output);
    }

    [Fact]
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

        Assert.Equal(1, result);
        Assert.Contains("Runner executable not found", fixture.Io.Output);
        Assert.Contains("not provisioned", fixture.Io.Output);
    }

    [Fact]
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

        Assert.Equal(1, result);
        Assert.Contains("setupExecutablePath", fixture.Io.Output);
        Assert.Contains("sandboxHome", fixture.Io.Output);
    }

    [Fact]
    public async Task Status_ChangesNoFileOrDirectory()
    {
        using var home     = new SandboxTestHome();
        using var fixture  = new CliFixture();
        await UserConfigStore.SaveAsync(fixture.UserConfigPath, SandboxConfig(home, enabled : true), CancellationToken.None);
        var beforeHome    = Snapshot(home.Components.SandboxHome);
        var beforeHomeDir = Snapshot(Path.GetDirectoryName(fixture.UserConfigPath)!);

        var result = await SandboxCommand.ExecuteAsync(fixture.CreateContext(),
                                                       CommandLine.Parse(["sandbox", "status"]),
                                                       CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Equal(beforeHome, Snapshot(home.Components.SandboxHome));
        Assert.Equal(beforeHomeDir, Snapshot(Path.GetDirectoryName(fixture.UserConfigPath)!));
    }

    [Fact]
    public async Task Status_NonWindowsNotesThePlatformLimitation()
    {
        if (OperatingSystem.IsWindows()) return;

        using var home    = new SandboxTestHome();
        using var fixture = new CliFixture();
        await UserConfigStore.SaveAsync(fixture.UserConfigPath, SandboxConfig(home, enabled : true), CancellationToken.None);

        var result = await SandboxCommand.ExecuteAsync(fixture.CreateContext(),
                                                       CommandLine.Parse(["sandbox", "status"]),
                                                       CancellationToken.None);

        Assert.Contains("不是 Windows", fixture.Io.Output);
    }

    // ---- provision：确认门与校验都在提权之前 ----

    [Fact]
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

        Assert.Equal(1, result);
        Assert.Empty(elevator.Requests);
        Assert.Contains("--yes", fixture.Io.Output);
        Assert.False(File.Exists(Path.Combine(home.Components.SandboxDirectory, "tinyharness-provision.jsonl")));
    }

    [Fact]
    public async Task Provision_MissingSetupExecutable_FailsWithoutElevation()
    {
        using var home     = new SandboxTestHome();
        File.Delete(home.Components.SetupExecutablePath);
        using var fixture  = new CliFixture();
        var       elevator = new FakeSetupElevator();
        await UserConfigStore.SaveAsync(fixture.UserConfigPath, SandboxConfig(home), CancellationToken.None);

        var result = await SandboxCommand.ExecuteAsync(fixture.CreateContext(),
                                                       CommandLine.Parse(["sandbox", "provision", "--yes"]),
                                                       CancellationToken.None,
                                                       new WindowsSandboxProvisioner(elevator));

        Assert.Equal(1, result);
        Assert.Empty(elevator.Requests);
        Assert.Contains("setup 可执行文件不存在", fixture.Io.Output);
    }

    [Fact]
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

        Assert.Equal(1, result);
        Assert.Empty(elevator.Requests);
        Assert.Contains("setupExecutablePath", fixture.Io.Output);
    }

    [Fact]
    public async Task Provision_OversizedPayloadIsRejectedWithoutElevation()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home = new SandboxTestHome();
        using var fixture  = new CliFixture();
        var       elevator = new FakeSetupElevator();
        // Enough distinct absolute write roots to push the base64 payload past
        // the UAC positional-argument limit (no env chunk channel there).
        var roots = Enumerable.Range(0, 1500)
                              .Select(i => $"C:\\tinyharness-test-roots\\package-cache-{i:D5}\\long-segment")
                              .ToArray();
        await UserConfigStore.SaveAsync(fixture.UserConfigPath,
                                        new UserConfig
                                        {
                                            Settings = new UserConfigSettings
                                            {
                                                WindowsSandbox = SandboxSettings(home) with
                                                                { AdditionalWriteRoots = roots }
                                            }
                                        },
                                        CancellationToken.None);

        var result = await SandboxCommand.ExecuteAsync(fixture.CreateContext(),
                                                       CommandLine.Parse(["sandbox", "provision", "--yes"]),
                                                       CancellationToken.None,
                                                       new WindowsSandboxProvisioner(elevator));

        Assert.Equal(1, result);
        Assert.Empty(elevator.Requests);
        Assert.Contains("additionalWriteRoots", fixture.Io.Output);
    }

    [Fact]
    public async Task Provision_SuccessfulElevationReportsReadyAndAppendsAudit()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home     = new SandboxTestHome();
        using var fixture  = new CliFixture();
        var       elevator = new FakeSetupElevator { Result = SandboxSetupElevationResult.Completed(0) };
        await UserConfigStore.SaveAsync(fixture.UserConfigPath, SandboxConfig(home, enabled : true), CancellationToken.None);

        var result = await SandboxCommand.ExecuteAsync(fixture.CreateContext(),
                                                       CommandLine.Parse(["sandbox", "provision", "--yes"]),
                                                       CancellationToken.None,
                                                       new WindowsSandboxProvisioner(elevator));

        Assert.Equal(0, result);
        var request = Assert.Single(elevator.Requests);
        Assert.Equal(home.Components.SetupExecutablePath, request.Components.SetupExecutablePath);
        Assert.Equal(request.Components.SandboxHome, request.Components.SandboxHome);
        Assert.Contains("机器级状态", fixture.Io.Output);
        Assert.Contains("TinyHarnessUsers", fixture.Io.Output);
        Assert.Contains("没有卸载或回滚", fixture.Io.Output);
        Assert.Contains("[ok]", fixture.Io.Output);

        var auditPath = Path.Combine(home.Components.SandboxDirectory, "tinyharness-provision.jsonl");
        Assert.True(File.Exists(auditPath));
        var record = await ParseSingleAuditRecordAsync(auditPath);
        Assert.Equal("completed", record["outcome"]!.GetValue<string>());
        Assert.Equal(0, record["exitCode"]!.GetValue<int>());
        Assert.Equal("full", record["mode"]!.GetValue<string>());
        Assert.False(record["refreshOnly"]!.GetValue<bool>());
        Assert.Matches("^[0-9A-F]{64}$", record["payloadSha256"]!.GetValue<string>());
    }

    [Fact]
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

        Assert.Equal(1, result);
        Assert.Contains("退出码 1", fixture.Io.Output);
        Assert.Contains("create_accounts_failed", fixture.Io.Output);
        Assert.Contains("net user error 5", fixture.Io.Output);

        var auditPath = Path.Combine(home.Components.SandboxDirectory, "tinyharness-provision.jsonl");
        Assert.True(File.Exists(auditPath));
        var record = await ParseSingleAuditRecordAsync(auditPath);
        Assert.Equal("failed", record["outcome"]!.GetValue<string>());
        Assert.Equal(1, record["exitCode"]!.GetValue<int>());
        Assert.Contains("create_accounts_failed", record["error"]!.GetValue<string>());
    }

    [Fact]
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

        Assert.Equal(1, result);
        Assert.Contains("拒绝了提权", fixture.Io.Output);
        var record = await ParseSingleAuditRecordAsync(Path.Combine(home.Components.SandboxDirectory,
                                                                        "tinyharness-provision.jsonl"));
        Assert.Equal("declined", record["outcome"]!.GetValue<string>());
    }

    [Fact]
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

        Assert.Equal(1, result);
        Assert.Contains("状态未知", fixture.Io.Output);
        Assert.Contains("sandbox status", fixture.Io.Output);
        var record = await ParseSingleAuditRecordAsync(Path.Combine(home.Components.SandboxDirectory,
                                                                        "tinyharness-provision.jsonl"));
        Assert.Equal("not-finished", record["outcome"]!.GetValue<string>());
    }

    [Fact]
    public async Task Provision_InteractiveConfirmationCancelsCleanly()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home    = new SandboxTestHome();
        using var fixture = new CliFixture(["n"], interactive : true);
        var elevator = new FakeSetupElevator();
        await UserConfigStore.SaveAsync(fixture.UserConfigPath, SandboxConfig(home), CancellationToken.None);

        var result = await SandboxCommand.ExecuteAsync(fixture.CreateContext(),
                                                       CommandLine.Parse(["sandbox", "provision"]),
                                                       CancellationToken.None,
                                                       new WindowsSandboxProvisioner(elevator));

        Assert.Equal(0, result);
        Assert.Empty(elevator.Requests);
        Assert.Contains("已取消", fixture.Io.Output);
    }

    // ---- Core：provisioner payload 与审计 ----

    [Fact]
    public void Provisioner_BuildPlanDerivesPayloadFromSharedPolicySemantics()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        var settings = SandboxSettings(home);
        var provisioner = new WindowsSandboxProvisioner(new FakeSetupElevator());

        var plan = provisioner.BuildPlan(settings, workspace.Root, "TestUser");

        Assert.Equal(5u, plan.Payload.Version);
        Assert.False(plan.Payload.RefreshOnly);
        Assert.Equal("full", plan.Payload.Mode);
        Assert.Empty(plan.Payload.ProxyPorts);
        Assert.Equal("TestUser", plan.Payload.RealUser);
        Assert.Equal(home.Components.SandboxHome, plan.Payload.SandboxHome);
        Assert.Equal(workspace.Root, plan.Payload.CommandWorkingDirectory);
        Assert.Equal(WindowsSandboxComponents.DefaultOfflineUsername, plan.Payload.OfflineUsername);
        Assert.Equal(WindowsSandboxComponents.DefaultOnlineUsername, plan.Payload.OnlineUsername);

        // Same derivation the composer/policy uses: workspace-write grants the
        // workspace plus the sandbox temp root; read roots cover workspace and
        // temp; metadata subdirectories stay deny-write protected.
        var expectedTemp = Path.Combine(home.Components.SandboxHome, "tmp");
        Assert.Equal([workspace.Root, expectedTemp], plan.Payload.ReadRoots);
        Assert.Equal([workspace.Root, expectedTemp], plan.Payload.WriteRoots);
        Assert.Equal(SandboxIsolationPolicy.ProtectedMetadataSubpaths
                                 .Select(subpath => Path.Combine(workspace.Root, subpath))
                                 .ToArray(),
                     plan.Payload.DenyWritePaths);

        Assert.Equal(expectedTemp, plan.TempRoot);
        Assert.False(Directory.Exists(expectedTemp), "BuildPlan must not create the temp root");
        Assert.True(plan.Base64Payload.Length <= ProcessSandboxSetupInvoker.PayloadArgumentCharacterLimit);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(plan.PayloadJson)), plan.PayloadSha256);
        Assert.Equal(Path.Combine(home.Components.SandboxDirectory, "tinyharness-provision.jsonl"), plan.AuditPath);
    }

    [Fact]
    public void Provisioner_BlankHomeUsesFixedDefaultAndForkAccountNames()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        var settings = SandboxSettings(home) with { SandboxHome = "" };

        var plan = new WindowsSandboxProvisioner(new FakeSetupElevator()).BuildPlan(settings, workspace.Root, "TestUser");

        Assert.Equal(WindowsSandboxComponents.DefaultSandboxHome, plan.Payload.SandboxHome);
        Assert.Equal(WindowsSandboxComponents.DefaultSandboxHome, plan.Components.SandboxHome);
        Assert.Equal("TinyHarnessOffline", plan.Payload.OfflineUsername);
        Assert.Equal("TinyHarnessOnline", plan.Payload.OnlineUsername);
        // The fixed default home must stay outside every workspace write root
        // or composition would reject it as tamperable.
        Assert.False(Workspace.IsInside(workspace.Root, plan.Components.SandboxHome));
    }

    [Fact]
    public void Provisioner_RefusesHomeCarryingLegacyCodexMarkerOrCredentials()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        File.WriteAllText(home.Components.MarkerPath,
                          """{"version":5,"offline_username":"CodexSandboxOffline","online_username":"CodexSandboxOnline","created_at":"2026-10-01T00:00:00Z"}""");

        var markerError = Assert.Throws<InvalidOperationException>(() =>
                                                                           new WindowsSandboxProvisioner(new FakeSetupElevator())
                                                                              .BuildPlan(SandboxSettings(home), workspace.Root, "TestUser"));
        Assert.Contains("legacy Codex account 'CodexSandboxOffline'", markerError.Message, StringComparison.Ordinal);
        Assert.Contains("setup marker", markerError.Message, StringComparison.Ordinal);

        File.WriteAllText(home.Components.MarkerPath,
                          """{"version":5,"offline_username":"TinyHarnessOffline","online_username":"TinyHarnessOnline","created_at":"2026-10-01T00:00:00Z"}""");
        File.WriteAllText(home.Components.UsersFilePath,
                          """{"version":5,"offline":{"username":"TinyHarnessOffline","password":"QUJDRA=="},"online":{"username":"CodexSandboxOnline","password":"QUJDRA=="}}""");

        var credentialsError = Assert.Throws<InvalidOperationException>(() =>
                                                                                new WindowsSandboxProvisioner(new FakeSetupElevator())
                                                                                   .BuildPlan(SandboxSettings(home), workspace.Root, "TestUser"));
        Assert.Contains("account credentials", credentialsError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Provisioner_PayloadTooLargeThrowsBeforeAnyElevation()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        var roots = Enumerable.Range(0, 1500)
                              .Select(i => $"C:\\tinyharness-test-roots\\package-cache-{i:D5}\\long-segment")
                              .ToArray();
        var settings = SandboxSettings(home) with { AdditionalWriteRoots = roots };

        var error = Assert.Throws<InvalidOperationException>(() =>
                                                                    new WindowsSandboxProvisioner(new FakeSetupElevator())
                                                                       .BuildPlan(settings, workspace.Root, "TestUser"));

        Assert.Contains("additionalWriteRoots", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Provisioner_RejectsRelativeAdditionalWriteRoots()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        var settings = SandboxSettings(home) with { AdditionalWriteRoots = ["relative-root"] };

        Assert.Throws<ArgumentException>(() =>
                                                new WindowsSandboxProvisioner(new FakeSetupElevator())
                                                   .BuildPlan(settings, workspace.Root, "TestUser"));
    }

    [Fact]
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

        Assert.Equal(SandboxProvisionOutcome.NotFinished, result.Outcome);
        var record = await ParseSingleAuditRecordAsync(plan.AuditPath);
        Assert.Equal("not-finished", record["outcome"]!.GetValue<string>());
    }

    [Fact]
    public void SandboxProvisionAuditRecord_RoundTripsThroughSourceGeneration()
    {
        var record = new SandboxProvisionAuditRecord
        {
            TimestampUtc = "2026-10-10T00:00:00Z",
            Outcome      = "failed",
            ExitCode     = 1,
            Mode         = "full",
            RefreshOnly  = false,
            PayloadSha256 = "ABCDEF",
            Error        = "create_accounts_failed: net user error 5"
        };

        var json = JsonSerializer.Serialize(record, WindowsSandboxJsonContext.Default.SandboxProvisionAuditRecord);
        var back = JsonSerializer.Deserialize(json, WindowsSandboxJsonContext.Default.SandboxProvisionAuditRecord);

        Assert.Equal(record, back);
        Assert.Contains("\"outcome\":\"failed\"", json, StringComparison.Ordinal);
        Assert.Contains("\"payloadSha256\":\"ABCDEF\"", json, StringComparison.Ordinal);
        // The full payload is never part of the audit line.
        Assert.DoesNotContain("read_roots", json, StringComparison.Ordinal);
    }

    // ---- Core：执行路径未 provision 时 fail closed 且不调 setup ----

    [Fact]
    public async Task Backend_FailsClosedWhenNotProvisionedWithoutCallingSetup()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home = new SandboxTestHome();
        File.Delete(home.Components.MarkerPath);
        var invoker  = new FakeSetupInvoker();
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

        var exception = await Assert.ThrowsAsync<ProcessExecutionStartException>(() => backend.ExecuteAsync(
                                                                                    new PreparedProcessExecution(
                                                                                        "cmd.exe", ["/c", "whoami"],
                                                                                        home.Components.SandboxHome, 30,
                                                                                        false, null),
                                                                                    new RecordingOutputCapture(),
                                                                                    new RecordingOutputCapture(),
                                                                                    CancellationToken.None));

        Assert.Contains("not ready", exception.Message, StringComparison.Ordinal);
        Assert.Empty(invoker.RefreshPayloads);
        Assert.Null(launcher.LastRequest);
    }

    // ---- fixture 与 fake ----

    private static WindowsSandboxSettings SandboxSettings(SandboxTestHome home, bool enabled = false)
    {
        return new WindowsSandboxSettings
        {
            Enabled              = enabled,
            SetupExecutablePath  = home.Components.SetupExecutablePath,
            RunnerExecutablePath = home.Components.RunnerExecutablePath,
            SandboxHome          = home.Components.SandboxHome
        };
    }

    private static UserConfig SandboxConfig(SandboxTestHome home, bool enabled = false)
    {
        return new UserConfig { Settings = new UserConfigSettings { WindowsSandbox = SandboxSettings(home, enabled) } };
    }

    private static async Task<System.Text.Json.Nodes.JsonObject> ParseSingleAuditRecordAsync(string auditPath)
    {
        var lines  = await File.ReadAllLinesAsync(auditPath);
        var record = Assert.Single(lines, line => !string.IsNullOrWhiteSpace(line));
        return System.Text.Json.Nodes.JsonNode.Parse(record)!.AsObject();
    }

    /// <summary>
    ///     递归快照目录树（相对路径 + 类型 + 长度），用于只读性断言。
    ///     A recursive directory snapshot (relative paths + kind + length) for read-only assertions.
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
    ///     录制式 fake 提权器：记录每次请求并返回脚本化结果，绝不启动进程或触发 UAC。
    ///     A recording fake elevator: it records requests and returns the
    ///     scripted result without ever starting a process or raising UAC.
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
        private readonly FakeCliConsole _io = new(input);

        public CliFixture(IReadOnlyList<string> input, bool interactive) : this(input)
        {
            _io.Interactive = interactive;
        }

        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"tinyharness-sandbox-{Guid.NewGuid():N}");

        public string UserConfigPath { get; } = Path.Combine(Path.GetTempPath(),
                                                             $"tinyharness-sandbox-config-{Guid.NewGuid():N}",
                                                             "user-config.json");

        public FakeCliConsole Io => _io;

        public CommandContext CreateContext()
        {
            return new CommandContext
            {
                Io               = _io,
                Credentials      = new FakeCredentialStore(),
                UserConfigPath   = UserConfigPath,
                WorkingDirectory = Root
            };
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);

            var configDirectory = Path.GetDirectoryName(UserConfigPath);
            if (Directory.Exists(configDirectory)) Directory.Delete(configDirectory, true);
        }
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

        public Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(_input.Count > 0 ? _input.Dequeue() : null);
        }

        public Task<string?> ReadHiddenLineAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(_input.Count > 0 ? _input.Dequeue() : null);
        }
    }

    private sealed class FakeCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, string> _entries = new(StringComparer.Ordinal);

        public bool IsSupported => false;

        public void Save(string targetName, string secret)
        {
            _entries[targetName] = secret;
        }

        public string? Read(string targetName)
        {
            return _entries.GetValueOrDefault(targetName);
        }

        public bool Delete(string targetName)
        {
            return _entries.Remove(targetName);
        }
    }
}
