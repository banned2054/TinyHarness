using TinyHarness.Cli;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;

namespace TinyHarness.Tests;

public sealed class SandboxCliCompositionTests
{
    [Test]
    public void ExternalSessionStore_AllowsDefaultLegacyDirectoryInsideWorkspace()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Windows sandbox composition requires Windows.");
        using var workspace = new TestTempDir();
        using var storage   = new TestTempDir();
        using var home      = new TestTempDir();
        var config = new TinyHarnessConfig
        {
            WorkspaceRoot    = workspace.Root,
            SessionDirectory = Path.Combine(workspace.Root, new TinyHarnessConfig().SessionDirectory)
        };
        var settings = new WindowsSandboxSettings
        {
            Enabled              = true,
            SetupExecutablePath  = home.WriteFile("setup.exe", "stub"),
            RunnerExecutablePath = home.WriteFile("runner.exe", "stub"),
            SandboxHome          = home.Root
        };

        var execution = SandboxCliComposition.TryCompose(settings, config.WorkspaceRoot,
                                                         new Dictionary<string, string>(), storage.Root);

        Assert.That(execution, Is.Not.Null);
        Assert.That(execution!.ExecutionPolicy.Backend, Is.EqualTo(ProcessExecutionBackendKind.WindowsSandbox));
        Assert.That(Directory.Exists(config.SessionDirectory), Is.False);
        Assert.That(Directory.GetFiles(storage.Root), Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SessionStoreInsideWritableRoot_IsRejectedBeforeComponentComposition(bool additionalRoot)
    {
        using var workspace = new TestTempDir();
        using var storage   = new TestTempDir();
        var       root      = additionalRoot ? storage.Root : workspace.Root;
        var settings = new WindowsSandboxSettings
        {
            Enabled              = true,
            AdditionalWriteRoots = additionalRoot ? [storage.Root] : []
        };

        var error = Assert.Throws<InvalidOperationException>(() =>
            SandboxCliComposition.TryCompose(settings, workspace.Root, new Dictionary<string, string>(),
                                              Path.Combine(root, "sessions")));

        Assert.That(error!.Message, Does.Contain("protected session store directory"));
        Assert.That(error.Message, Does.Contain(root));
        Assert.That(error.Message, Does.Contain("settings.sessionStoreDirectory"));
    }

    [Test]
    public void DisabledSandbox_DoesNotComposeOrValidateUnusedStorage()
    {
        using var workspace = new TestTempDir();

        Assert.That(SandboxCliComposition.TryCompose(new WindowsSandboxSettings(), workspace.Root,
                                                     new Dictionary<string, string>(), workspace.Root), Is.Null);
    }

    [Test]
    public void ExternalSessionStore_MissingComponentsStillFailClosed()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Windows sandbox composition requires Windows.");
        using var workspace = new TestTempDir();
        using var storage   = new TestTempDir();
        using var home      = new TestTempDir();
        var settings = new WindowsSandboxSettings
        {
            Enabled              = true,
            SetupExecutablePath  = Path.Combine(home.Root, "missing-setup.exe"),
            RunnerExecutablePath = home.WriteFile("runner.exe", "stub"),
            SandboxHome          = home.Root
        };

        var error = Assert.Throws<InvalidOperationException>(() =>
            SandboxCliComposition.TryCompose(settings, workspace.Root, new Dictionary<string, string>(), storage.Root));

        Assert.That(error!.Message, Does.Contain("setup executable was not found"));
        Assert.That(error.Message, Does.Contain("refusing to fall back to host execution"));
    }

    // ---- 常设信任组合门控（B4）：未确认关 OS 注入门，确认后按显式持久授权组合 ----

    [Test]
    public void StandingTrustUnconfirmed_ComposesTheGateClosedBaseline_WithoutAdditionalRoots()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Windows sandbox composition requires Windows.");
        using var workspace = new TestTempDir();
        using var storage   = new TestTempDir();
        using var home      = new TestTempDir();
        using var readRoot  = new TestTempDir();
        using var writeRoot = new TestTempDir();
        var settings = ExpansionSettings(home, readRoot, writeRoot);

        var execution = SandboxCliComposition.TryCompose(settings, workspace.Root,
                                                         new Dictionary<string, string>(), storage.Root,
                                                         standingTrust : StandingTrustState.Unconfirmed);

        // Unconfirmed 的生效配置回落 restricted 并清空附加读写根：OS 基线不携带任何附加根。
        Assert.Multiple(() =>
        {
            Assert.That(execution, Is.Not.Null);
            Assert.That(execution!.Policy.NetworkEnabled, Is.False);
            Assert.That(execution.Policy.EffectiveWriteRoots, Does.Not.Contain(writeRoot.Root));
            Assert.That(execution.Policy.EffectiveReadRoots, Does.Not.Contain(readRoot.Root));
            // M2 payload 面：附加读根不进入 setup payload 的 read_roots。
            var payload = execution.Policy.CreateSetupPayload(new WindowsSandboxComponents
                                                  {
                                                      SetupExecutablePath  = settings.SetupExecutablePath,
                                                      RunnerExecutablePath = settings.RunnerExecutablePath,
                                                      SandboxHome          = settings.SandboxHome
                                                  }, workspace.Root, "test", false);
            Assert.That(payload.ReadRoots.Any(path => path.StartsWith(readRoot.Root,
                StringComparison.OrdinalIgnoreCase)), Is.False,
                "Unconfirmed 下附加读根不得注入 payload read_roots。");
            TestAssert.DoesNotContain("network=enabled", execution.ExecutionPolicy.PolicyIdentity,
                                      StringComparison.Ordinal);
            TestAssert.Contains("network=restricted", execution.ExecutionPolicy.PolicyIdentity,
                                StringComparison.Ordinal);
        });
    }

    [Test]
    public void StandingTrustConfirmed_ComposesExplicitPersistentTrust_WithAdditionalRoots()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Windows sandbox composition requires Windows.");
        using var workspace = new TestTempDir();
        using var storage   = new TestTempDir();
        using var home      = new TestTempDir();
        using var readRoot  = new TestTempDir();
        using var writeRoot = new TestTempDir();
        var settings = ExpansionSettings(home, readRoot, writeRoot);

        var execution = SandboxCliComposition.TryCompose(settings, workspace.Root,
                                                         new Dictionary<string, string>(), storage.Root,
                                                         standingTrust : StandingTrustState.Confirmed);

        // Confirmed 沿用原配置组合：在线网络与附加读写根按显式持久授权进入 payload 侧策略。
        Assert.Multiple(() =>
        {
            Assert.That(execution, Is.Not.Null);
            Assert.That(execution!.Policy.NetworkEnabled, Is.True);
            Assert.That(execution.Policy.EffectiveWriteRoots, Does.Contain(writeRoot.Root));
            Assert.That(execution.Policy.EffectiveReadRoots, Does.Contain(readRoot.Root));
            // M2 payload 面：附加读根随 Confirmed 注入 setup payload 的 read_roots。
            var payload = execution.Policy.CreateSetupPayload(new WindowsSandboxComponents
                                                  {
                                                      SetupExecutablePath  = settings.SetupExecutablePath,
                                                      RunnerExecutablePath = settings.RunnerExecutablePath,
                                                      SandboxHome          = settings.SandboxHome
                                                  }, workspace.Root, "test", false);
            Assert.That(payload.ReadRoots.Any(path => path.StartsWith(readRoot.Root,
                StringComparison.OrdinalIgnoreCase)), Is.True,
                "Confirmed 下附加读根必须注入 payload read_roots（profile 外真实可读）。");
            TestAssert.Contains("network=enabled", execution.ExecutionPolicy.PolicyIdentity,
                                StringComparison.Ordinal);
        });
    }

    [Test]
    public void StandingTrustUnconfirmed_SessionStoreProtection_StillChecksTheOriginalWriteRoots()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Windows sandbox composition requires Windows.");
        using var workspace = new TestTempDir();
        using var writeRoot = new TestTempDir();
        var settings = new WindowsSandboxSettings
        {
            Enabled              = true,
            AdditionalWriteRoots = [writeRoot.Root]
        };

        // 未确认写根虽不进 OS 基线，仍可经 grant 通道临时生效：存储保护必须按原始写根集合拒绝。
        var error = Assert.Throws<InvalidOperationException>(() =>
            SandboxCliComposition.TryCompose(settings, workspace.Root, new Dictionary<string, string>(),
                                              Path.Combine(writeRoot.Root, "sessions"),
                                              standingTrust : StandingTrustState.Unconfirmed));

        Assert.That(error!.Message, Does.Contain("protected session store directory"));
    }

    private static WindowsSandboxSettings ExpansionSettings(TestTempDir home, TestTempDir readRoot,
                                                            TestTempDir writeRoot) =>
        new()
        {
            Enabled              = true,
            SetupExecutablePath  = home.WriteFile("setup.exe", "stub"),
            RunnerExecutablePath = home.WriteFile("runner.exe", "stub"),
            SandboxHome          = home.Root,
            NetworkPolicy        = SandboxNetworkPolicy.Enabled,
            AdditionalReadRoots  = [readRoot.Root],
            AdditionalWriteRoots = [writeRoot.Root]
        };
}
