using TinyHarness.Cli;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Runtime;

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
}
