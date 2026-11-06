using System.Text.Json;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

namespace TinyHarness.Tests;

/// <summary>
///     <para>离线验证 TCP 环回的可信选择、版本拒绝和严格模式隔离；不运行真实组件或修改机器状态。</para>
///     <para>
///         Verifies trusted TCP-loopback selection, version rejection and strict-mode separation offline without real
///         components or machine changes.
///     </para>
/// </summary>
public class WindowsSandboxTcpLoopbackTests
{
    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task TrustedSettings_RoundTripBothEnvironmentBindingBranches(bool enabled, bool extras)
    {
        using var directory = new TestTempDir();
        var       path      = Path.Combine(directory.Root, "user-config.json");
        await UserConfigStore.SaveAsync(path, new UserConfig
        {
            Settings = new UserConfigSettings
            {
                WindowsSandbox = new WindowsSandboxSettings
                {
                    Enabled          = true,
                    AllowTcpLoopback = enabled,
                    ExtraEnvironment = extras ? new Dictionary<string, string> { ["TEST_ONLY"] = "value" } : null
                }
            }
        }, CancellationToken.None);
        var loaded = await UserConfigStore.LoadAsync(path, CancellationToken.None);
        Assert.That(loaded.Settings!.WindowsSandbox!.AllowTcpLoopback, Is.EqualTo(enabled));
        Assert.That(new WindowsSandboxSettings().AllowTcpLoopback, Is.False);
    }

    [TestCase("1")]
    [TestCase("\"true\"")]
    [TestCase("{}")]
    public async Task TrustedSettings_RejectsNonBooleanOptIn(string value)
    {
        using var directory = new TestTempDir();
        var path = directory.WriteFile("user-config.json",
                                       "{\"settings\":{\"windowsSandbox\":{\"allowTcpLoopback\":" + value + "}}}");
        await Assert.ThrowsAsync<InvalidDataException>(() => UserConfigStore.LoadAsync(path, CancellationToken.None));
    }

    [Test]
    public void Modes_UseSeparateAccountsAndDefaultHomes()
    {
        var strict   = Components("C:\\not-created", false);
        var loopback = strict with { AllowTcpLoopback = true };
        Assert.That(strict.OfflineUsername, Is.EqualTo("TinyHarnessOffline"));
        Assert.That(loopback.OfflineUsername, Is.EqualTo("TinyHarnessTcpOff"));
        Assert.That(loopback.OnlineUsername, Is.EqualTo("TinyHarnessTcpOn"));
        Assert.That(loopback.OfflineUsername.Length, Is.LessThanOrEqualTo(20));
        Assert.That(loopback.OnlineUsername.Length, Is.LessThanOrEqualTo(20));
        Assert.That(WindowsSandboxComponents.ResolveSandboxHome(null, true),
                    Is.EqualTo(WindowsSandboxComponents.DefaultTcpLoopbackSandboxHome));
        Assert.That(WindowsSandboxComponents.ResolveSandboxHome(null, true),
                    Is.Not.EqualTo(WindowsSandboxComponents.ResolveSandboxHome(null)));
    }

    [Test]
    public void Policy_OptInBindsIdentityAndVersionWithoutWideningFileSystemRoots()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home      = new TestTempDir();
        using var workspace = new TestTempDir();
        var settings = new WindowsSandboxSettings
        {
            Enabled              = true, SandboxHome = home.Root,
            SetupExecutablePath  = home.WriteFile("setup.exe", "stub"),
            RunnerExecutablePath = home.WriteFile("runner.exe", "stub")
        };
        var strict   = WindowsSandboxComposer.Compose(settings, workspace.Root);
        var loopback = WindowsSandboxComposer.Compose(settings with { AllowTcpLoopback = true }, workspace.Root);
        Assert.That(loopback.Policy.EffectiveWriteRoots, Is.EqualTo(strict.Policy.EffectiveWriteRoots));
        Assert.That(loopback.Policy.DenyWritePaths, Is.EqualTo(strict.Policy.DenyWritePaths));
        Assert.That(loopback.Policy.SpawnProfile.Network, Is.EqualTo("restricted"));
        Assert.That(loopback.ExecutionPolicy.PolicyIdentity, Is.Not.EqualTo(strict.ExecutionPolicy.PolicyIdentity));
        Assert.That(loopback.ExecutionPolicy.PolicyIdentity, Does.Contain("setupVersion=6\n"));
        Assert.That(loopback.ExecutionPolicy.PolicyIdentity, Does.Contain("tcpLoopback=allowed\n"));
        Assert.That(loopback.ExecutionPolicy.DisplayName, Does.Contain("host services included"));
        var payload = loopback.Policy.CreateSetupPayload(loopback.Components, workspace.Root, "test-user", true);
        Assert.That(payload.Version, Is.EqualTo(6u));
        Assert.That(payload.AllowTcpLoopback, Is.True);
        Assert.That(payload.AllowLocalBinding, Is.False, "TCP opt-in must not allow UDP binding.");
        Assert.That(payload.ProxyPorts, Is.Empty);
        Assert.That(payload.OfflineUsername, Is.EqualTo("TinyHarnessTcpOff"));
        var encoded = JsonSerializer.SerializeToElement(payload, WindowsSandboxJsonContext.Default.SandboxSetupPayload);
        Assert.That(encoded.GetProperty("allow_tcp_loopback").GetBoolean(), Is.True);
        Assert.That(strict.Policy.CreateSetupPayload(strict.Components, workspace.Root, "test-user", true).Version,
                    Is.EqualTo(5u));
        Assert.Throws<InvalidOperationException>(() => loopback.Policy.CreateSetupPayload(strict.Components,
                                                          workspace.Root, "test-user", true));
    }

    [TestCase(5, false, false)]
    [TestCase(6, false, false)]
    [TestCase(6, true, true)]
    public async Task Readiness_RequiresVersionAndModeAndAccountPair(int version, bool mode, bool ready)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home       = new SandboxTestHome();
        var       components = home.Components with { AllowTcpLoopback = true };
        await WriteMarkerAsync(components, version, mode);
        await WriteUsersAsync(components);
        var result = await new WindowsSandboxStateInspector(components).InspectAsync();
        Assert.That(result.Ready, Is.EqualTo(ready), string.Join("; ", result.Problems));
        if (ready)
        {
            await WriteMarkerAsync(components, 6, true, "TinyHarnessOffline");
            Assert.That((await new WindowsSandboxStateInspector(components).InspectAsync()).Ready, Is.False);
        }
    }

    [Test]
    public void Provision_RejectsStrictHomeBeforeAnyElevation()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        var settings = new WindowsSandboxSettings
        {
            AllowTcpLoopback     = true, SandboxHome = home.Components.SandboxHome,
            SetupExecutablePath  = home.Components.SetupExecutablePath,
            RunnerExecutablePath = home.Components.RunnerExecutablePath
        };
        Assert.Throws<InvalidOperationException>(() =>
                                                     new WindowsSandboxProvisioner(new NeverElevate())
                                                        .BuildPlan(settings, workspace.Root, "test-user"));
    }

    [Test]
    public void Provision_MalformedMarkerFailsWithContextBeforeElevation()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        File.WriteAllText(home.Components.MarkerPath, "{invalid");
        var error = Assert.Throws<InvalidOperationException>(() => new WindowsSandboxProvisioner(new NeverElevate())
                                                                .BuildPlan(
                                                                           new WindowsSandboxSettings
                                                                           {
                                                                               AllowTcpLoopback = true,
                                                                               SandboxHome =
                                                                                   home.Components.SandboxHome,
                                                                               SetupExecutablePath =
                                                                                   home.Components.SetupExecutablePath,
                                                                               RunnerExecutablePath =
                                                                                   home.Components.RunnerExecutablePath
                                                                           }, workspace.Root, "test-user"));
        Assert.That(error!.Message, Does.Contain(home.Components.SandboxHome).And.Contain("no elevation"));
    }

    [Test]
    public async Task Provision_RejectsForeignCredentialsEvenWithoutMarker()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        File.Delete(home.Components.MarkerPath);
        var before = await File.ReadAllBytesAsync(home.Components.UsersFilePath);
        Assert.Throws<InvalidOperationException>(() => new WindowsSandboxProvisioner(new NeverElevate()).BuildPlan(
                                                      new WindowsSandboxSettings
                                                      {
                                                          AllowTcpLoopback     = true,
                                                          SandboxHome          = home.Components.SandboxHome,
                                                          SetupExecutablePath  = home.Components.SetupExecutablePath,
                                                          RunnerExecutablePath = home.Components.RunnerExecutablePath
                                                      }, workspace.Root, "test-user"));
        Assert.That(await File.ReadAllBytesAsync(home.Components.UsersFilePath), Is.EqualTo(before));
    }

    [Test]
    public void Provision_NewModePlanDoesNotCreateHomeOrRotateCredentials()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TestTempDir();
        using var workspace = new TestTempDir();
        var       home      = Path.Combine(directory.Root, "not-created");
        var plan = new WindowsSandboxProvisioner(new NeverElevate()).BuildPlan(new WindowsSandboxSettings
        {
            AllowTcpLoopback     = true, SandboxHome = home,
            SetupExecutablePath  = directory.WriteFile("setup.exe", "stub"),
            RunnerExecutablePath = directory.WriteFile("runner.exe", "stub")
        }, workspace.Root, "test-user");
        Assert.That(plan.Payload.Version, Is.EqualTo(6u));
        Assert.That(plan.Payload.AllowTcpLoopback, Is.True);
        Assert.That(plan.Payload.OfflineUsername, Is.EqualTo("TinyHarnessTcpOff"));
        Assert.That(plan.Payload.OnlineUsername, Is.EqualTo("TinyHarnessTcpOn"));
        Assert.That(Directory.Exists(home), Is.False);
    }

    [Test]
    public async Task Backend_TcpLoopbackWithOnlineGrant_FailsClosedBeforeAnyLaunch()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var home       = new SandboxTestHome();
        var       components = home.Components with { AllowTcpLoopback = true };
        // 环回 home 需要 v6 marker 与 TcpOff/TcpOn 账户对才满足就绪检查（离线腿依赖）。
        await WriteMarkerAsync(components, 6, true);
        await WriteUsersAsync(components);
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["TEMP"] = home.Components.SandboxHome
        };
        var policy = SandboxPolicyResolver.Resolve(SandboxPolicyKind.WorkspaceWrite,
                                                   home.Components.SandboxHome, environment,
                                                   allowTcpLoopback : true);
        var launcher = new FakeSandboxRunnerLauncher
        {
            RunnerBody = (_, _, _) => throw new InvalidOperationException("The guard must fail closed before launch.")
        };
        var backend = new WindowsSandboxBackend(components, policy, environment, null, new FakeSetupInvoker(),
                                                new FakeCredentialSource(), desktopFactory : new FakeDesktopFactory(),
                                                runnerLauncher : launcher, limits : BackendTestLimits(),
                                                allowNullDeviceAccess : _ => { },
                                                validateWriteAcls : (_, _) => Task.CompletedTask);
        var plan = new PreparedProcessExecution("cmd.exe", ["/c", "whoami"], home.Components.SandboxHome, 30, false, null)
        {
            NetworkAccessEnabled = true
        };

        // 逐次在线授予在 TCP 环回模式下与组合器的常设互斥守卫同语义：在预检、refresh 与
        // 启动之前 fail closed，绝不以在线账户身份启动 runner 或创建桌面。拒绝按启动期校验
        // 抛 ProcessExecutionStartException（InvalidOperationException 的子类）。
        await TestAssert.ThrowsAsync<ProcessExecutionStartException>(() => backend.ExecuteAsync(
            plan, new RecordingOutputCapture(), new RecordingOutputCapture(), CancellationToken.None));

        Assert.That(launcher.LastRequest, Is.Null);

        // 未申请网络的同一命令不受影响：凭据源、桌面与 runner 均为 fake，整条链路离线完成。
        var offlinePlan = plan with { NetworkAccessEnabled = false };
        var backendOffline = new WindowsSandboxBackend(components, policy, environment, null, new FakeSetupInvoker(),
                                                       new FakeCredentialSource(),
                                                       desktopFactory : new FakeDesktopFactory(),
                                                       runnerLauncher : new FakeSandboxRunnerLauncher
                                                       {
                                                           RunnerBody = FakeRunnerBody
                                                       },
                                                       limits : BackendTestLimits(),
                                                       allowNullDeviceAccess : _ => { },
                                                       validateWriteAcls : (_, _) => Task.CompletedTask);
        var result = await backendOffline.ExecuteAsync(offlinePlan, new RecordingOutputCapture(),
                                                       new RecordingOutputCapture(), CancellationToken.None);
        Assert.That(result.ExitCode, Is.EqualTo(0));
    }

    private static async Task FakeRunnerBody(Stream downstream, Stream upstream, CancellationToken end)
    {
        await RunnerFrameCodec.ReadFrameAsync(downstream, end);
        await RunnerFrameCodec.WriteFrameAsync(upstream, "spawn_ready",
                                               new RunnerSpawnReady { ProcessId = 7 }, end);
        await RunnerFrameCodec.WriteFrameAsync(upstream, "exit",
                                               new RunnerExitPayload { ExitCode = 0, TimedOut = false }, end);
    }

    private static WindowsSandboxBackendLimits BackendTestLimits() => new()
    {
        RefreshTimeout          = TimeSpan.FromSeconds(5),
        SpawnReadyTimeout       = TimeSpan.FromSeconds(3),
        CommunicationGrace      = TimeSpan.FromMilliseconds(300),
        TerminationDrainTimeout = TimeSpan.FromMilliseconds(500),
        RunnerExitTimeout       = TimeSpan.FromMilliseconds(500),
        TerminationKillGrace    = TimeSpan.FromSeconds(1),
        TerminateWriteTimeout   = TimeSpan.FromSeconds(1)
    };

    private static WindowsSandboxComponents Components(string home, bool mode) => new()
    {
        SetupExecutablePath = Path.Combine(home, "setup.exe"), RunnerExecutablePath = Path.Combine(home, "runner.exe"),
        SandboxHome         = home, AllowTcpLoopback                                = mode
    };

    private static Task WriteMarkerAsync(WindowsSandboxComponents components, int version, bool mode,
                                         string?                  offline = null) =>
        File.WriteAllTextAsync(components.MarkerPath, JsonSerializer.Serialize(new SandboxSetupMarker
        {
            Version         = (uint)version, AllowTcpLoopback                       = mode,
            OfflineUsername = offline ?? components.OfflineUsername, OnlineUsername = components.OnlineUsername
        }, WindowsSandboxJsonContext.Default.SandboxSetupMarker));

    private static Task WriteUsersAsync(WindowsSandboxComponents components) =>
        File.WriteAllTextAsync(components.UsersFilePath, JsonSerializer.Serialize(new SandboxUsersFile
        {
            Version = 5,
            Offline = new SandboxUserCredential
                { Username = components.OfflineUsername, ProtectedPassword = "test-only" },
            Online = new SandboxUserCredential { Username = components.OnlineUsername, ProtectedPassword = "test-only" }
        }, WindowsSandboxJsonContext.Default.SandboxUsersFile));

    private sealed class NeverElevate : ISandboxSetupElevator
    {
        public Task<SandboxSetupElevationResult> ElevateAsync(WindowsSandboxComponents components, string base64Payload,
                                                              CancellationToken        cancellationToken) =>
            throw new InvalidOperationException("Offline regression must not elevate.");
    }
}
