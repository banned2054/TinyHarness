using System.Text.Json;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
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
