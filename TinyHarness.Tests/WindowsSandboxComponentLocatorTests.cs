using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TinyHarness.Cli.Commands;
using TinyHarness.Cli.Services;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         沙箱随包组件定位、显式绝对覆盖、缺失诊断、保护清单跨进程合并、写根边界与离线命令的
///         综合测试；全部离线，不触发 UAC、不修改机器状态。
///     </para>
///     <para>
///         Comprehensive offline tests for bundled sandbox component resolution, explicit absolute
///         overrides, missing-file diagnostics, cross-process protected-path merging, write-root
///         boundaries, and isolated CLI commands; no UAC or machine-state mutation.
///     </para>
/// </summary>
public sealed partial class WindowsSandboxComponentLocatorTests
{
    [Test]
    public void Resolve_BlankOrOmittedSettingsUseBundledPathsPerInstallDirectory()
    {
        using var cliInstall     = new TestTempDir();
        using var desktopInstall = new TestTempDir();
        using var workspace      = new TestTempDir();

        // 同一份留空配置在不同安装目录下分别解析出各自安装包的组件与 probe。
        var shared = new WindowsSandboxSettings
        {
            SetupExecutablePath  = "   ",
            RunnerExecutablePath = string.Empty
        };
        var cliResolved     = WindowsSandboxComponentResolver.Resolve(shared, cliInstall.Root);
        var desktopResolved = WindowsSandboxComponentResolver.Resolve(shared, desktopInstall.Root);

        Assert.That(cliResolved.IsValid, Is.True);
        Assert.That(desktopResolved.IsValid, Is.True);
        Assert.That(cliResolved.Setup.Source, Is.EqualTo(WindowsSandboxComponentSource.Bundled));
        Assert.That(cliResolved.Runner.Source, Is.EqualTo(WindowsSandboxComponentSource.Bundled));
        Assert.That(cliResolved.Setup.CandidatePath,
                    Is.EqualTo(Path.Combine(cliInstall.Root, "sandbox",
                                            WindowsSandboxComponentResolver.SetupExecutableFileName)));
        Assert.That(cliResolved.Runner.CandidatePath,
                    Is.EqualTo(Path.Combine(cliInstall.Root, "sandbox",
                                            WindowsSandboxComponentResolver.RunnerExecutableFileName)));
        Assert.That(cliResolved.EffectiveProbePath,
                    Is.EqualTo(Path.Combine(cliInstall.Root, "sandbox",
                                            WindowsSandboxComponentResolver.DenyProbeExecutableFileName)));
        Assert.That(desktopResolved.Setup.CandidatePath,
                    Is.EqualTo(Path.Combine(desktopInstall.Root, "sandbox",
                                            WindowsSandboxComponentResolver.SetupExecutableFileName)));
        Assert.That(desktopResolved.Runner.CandidatePath,
                    Is.EqualTo(Path.Combine(desktopInstall.Root, "sandbox",
                                            WindowsSandboxComponentResolver.RunnerExecutableFileName)));
        Assert.That(cliResolved.Setup.CandidatePath, Is.Not.EqualTo(desktopResolved.Setup.CandidatePath));
        Assert.That(cliResolved.Setup.CandidatePath, Is.Not.EqualTo(workspace.Root));
    }

    [Test]
    public void Resolve_PartialOverrideKeepsOtherComponentBundledAndProbeFollowsEffectiveRunner()
    {
        using var appDir     = new TestTempDir();
        using var customFork = new TestTempDir();
        var customRunner = Path.Combine(customFork.Root, "custom-runner.exe");

        var settings = new WindowsSandboxSettings
        {
            SetupExecutablePath  = string.Empty,
            RunnerExecutablePath = $"  {customRunner}  "
        };

        var resolution = WindowsSandboxComponentResolver.Resolve(settings, appDir.Root);

        Assert.That(resolution.IsValid, Is.True);
        Assert.That(resolution.Setup.Source, Is.EqualTo(WindowsSandboxComponentSource.Bundled));
        Assert.That(resolution.Setup.CandidatePath,
                    Is.EqualTo(Path.Combine(appDir.Root, "sandbox",
                                            WindowsSandboxComponentResolver.SetupExecutableFileName)));
        Assert.That(resolution.Runner.Source, Is.EqualTo(WindowsSandboxComponentSource.Configured));
        Assert.That(resolution.Runner.CandidatePath, Is.EqualTo(customRunner));
        Assert.That(resolution.EffectiveProbePath,
                    Is.EqualTo(Path.Combine(customFork.Root,
                                            WindowsSandboxComponentResolver.DenyProbeExecutableFileName)));
    }

    [TestCase(@"sandbox\codex-windows-sandbox-setup.exe")]
    [TestCase(@"..\codex-windows-sandbox-setup.exe")]
    [TestCase(@"C:sandbox\codex-windows-sandbox-setup.exe")]
    [TestCase(@"\sandbox\codex-windows-sandbox-setup.exe")]
    public void Resolve_RejectsNonFullyQualifiedConfiguredPaths(string invalidPath)
    {
        if (!OperatingSystem.IsWindows()) return;

        using var appDir = new TestTempDir();
        var settings = new WindowsSandboxSettings
        {
            SetupExecutablePath  = invalidPath,
            RunnerExecutablePath = string.Empty
        };

        var resolution = WindowsSandboxComponentResolver.Resolve(settings, appDir.Root);

        Assert.That(resolution.Setup.IsValid, Is.False);
        Assert.That(resolution.Setup.Error, Does.Contain("setupExecutablePath"));
        Assert.That(resolution.Setup.CandidatePath, Is.Null);
        Assert.That(resolution.Runner.IsValid, Is.True);
    }

    [Test]
    public async Task Status_DetectsMissingReleasePackageFileViaBuildInfoAndFailsEvenWhenDisabled()
    {
        using var packageDir = new TestTempDir();
        using var configDir  = new TestTempDir();
        using var workDir    = new TestTempDir();
        WriteBundledComponents(packageDir.Root, includeSetup : true, includeRunner : false, includeProbe : true);
        WriteBuildInfo(packageDir.Root);

        var io = new RecordingConsole();
        var context = new CommandContext
        {
            Io               = io,
            Credentials      = new MemoryCredentialStore(),
            UserConfigPath   = Path.Combine(configDir.Root, UserConfigStore.FileName),
            WorkingDirectory = workDir.Root,
            AppBaseDirectory = packageDir.Root
        };

        var exitCode = await SandboxCommand.ExecuteAsync(context, CommandLine.Parse(["sandbox", "status"]),
                                                         CancellationToken.None);

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(io.Output, Does.Contain("Release package is missing bundled Windows sandbox runner executable"));
        Assert.That(io.Output, Does.Contain(Path.Combine(packageDir.Root, "sandbox",
                                                         WindowsSandboxComponentResolver.RunnerExecutableFileName)));
        Assert.That(io.Output, Does.Contain("当前安装包默认"));
    }

    [Test]
    public async Task Status_BundledPresentDisabledReturnsZeroAndResolvesAllThreeBundledComponents()
    {
        using var packageDir = new TestTempDir();
        using var configDir  = new TestTempDir();
        using var workDir    = new TestTempDir();
        WriteBundledComponents(packageDir.Root, includeSetup : true, includeRunner : true, includeProbe : true);
        WriteBuildInfo(packageDir.Root);

        var io = new RecordingConsole();
        var context = new CommandContext
        {
            Io               = io,
            Credentials      = new MemoryCredentialStore(),
            UserConfigPath   = Path.Combine(configDir.Root, UserConfigStore.FileName),
            WorkingDirectory = workDir.Root,
            AppBaseDirectory = packageDir.Root
        };

        var exitCode = await SandboxCommand.ExecuteAsync(context, CommandLine.Parse(["sandbox", "status"]),
                                                         CancellationToken.None);

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(io.Output, Does.Contain(Path.Combine(packageDir.Root, "sandbox",
                                                         WindowsSandboxComponentResolver.SetupExecutableFileName)));
        Assert.That(io.Output, Does.Contain(Path.Combine(packageDir.Root, "sandbox",
                                                         WindowsSandboxComponentResolver.RunnerExecutableFileName)));
        Assert.That(io.Output, Does.Contain(Path.Combine(packageDir.Root, "sandbox",
                                                         WindowsSandboxComponentResolver.DenyProbeExecutableFileName)));
        Assert.That(io.Output, Does.Contain("当前安装包默认"));
        Assert.That(io.Output, Does.Contain("enabled"));
    }

    [Test]
    public async Task Provision_AllowsMissingWindowsSandboxSectionWithBundledComponents()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var packageDir = new TestTempDir();
        using var configDir  = new TestTempDir();
        using var workDir    = new TestTempDir();
        using var home       = new SandboxTestHome();
        WriteBundledComponents(packageDir.Root, includeSetup : true, includeRunner : true, includeProbe : true);

        // 仅设置 sandboxHome（无路径覆盖，也无 enabled=true），验证随包组件可直接走通 provision 计划与模拟提权。
        var configPath = Path.Combine(configDir.Root, UserConfigStore.FileName);
        await UserConfigStore.SaveAsync(configPath, new UserConfig
        {
            Settings = new UserConfigSettings
            {
                WindowsSandbox = new WindowsSandboxSettings { SandboxHome = home.Components.SandboxHome }
            }
        }, CancellationToken.None);

        var io       = new RecordingConsole();
        var elevator = new StubElevator { Result = SandboxSetupElevationResult.Completed0 };
        var context = new CommandContext
        {
            Io               = io,
            Credentials      = new MemoryCredentialStore(),
            UserConfigPath   = configPath,
            WorkingDirectory = workDir.Root,
            AppBaseDirectory = packageDir.Root
        };

        var exitCode = await SandboxCommand.ExecuteAsync(context, CommandLine.Parse(["sandbox", "provision", "--yes"]),
                                                         CancellationToken.None,
                                                         new WindowsSandboxProvisioner(elevator));

        Assert.That(exitCode, Is.EqualTo(0), io.Output);
        var request = TestAssert.Single(elevator.Requests);
        Assert.That(request.Components.SetupExecutablePath,
                    Is.EqualTo(Path.Combine(packageDir.Root, "sandbox",
                                            WindowsSandboxComponentResolver.SetupExecutableFileName)));
        Assert.That(request.Components.RunnerExecutablePath,
                    Is.EqualTo(Path.Combine(packageDir.Root, "sandbox",
                                            WindowsSandboxComponentResolver.RunnerExecutableFileName)));
        Assert.That(io.Output, Does.Contain("当前安装包默认"));
    }

    [Test]
    public async Task Provision_RejectsMissingRunnerAndWriteRootOverlapBeforeElevation()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var packageDir = new TestTempDir();
        using var configDir  = new TestTempDir();
        using var workDir    = new TestTempDir();
        WriteBundledComponents(packageDir.Root, includeSetup : true, includeRunner : false, includeProbe : false);

        var elevator = new StubElevator();
        var io       = new RecordingConsole();
        var context = new CommandContext
        {
            Io               = io,
            Credentials      = new MemoryCredentialStore(),
            UserConfigPath   = Path.Combine(configDir.Root, UserConfigStore.FileName),
            WorkingDirectory = workDir.Root,
            AppBaseDirectory = packageDir.Root
        };

        var missingRunnerExit = await SandboxCommand.ExecuteAsync(context,
                                                                  CommandLine.Parse(["sandbox", "provision", "--yes"]),
                                                                  CancellationToken.None,
                                                                  new WindowsSandboxProvisioner(elevator));

        Assert.That(missingRunnerExit, Is.EqualTo(1));
        Assert.That(elevator.Requests, Is.Empty);
        Assert.That(io.Output, Does.Contain("runner executable"));

        // 补齐 runner，但把工作区设成包含安装目录的父目录：必须拒绝提权。
        WriteBundledComponents(packageDir.Root, includeSetup : true, includeRunner : true, includeProbe : false);
        var overlapIo = new RecordingConsole();
        var overlapContext = new CommandContext
        {
            Io               = overlapIo,
            Credentials      = new MemoryCredentialStore(),
            UserConfigPath   = Path.Combine(configDir.Root, UserConfigStore.FileName),
            WorkingDirectory = packageDir.Root,
            AppBaseDirectory = packageDir.Root
        };

        var overlapExit = await SandboxCommand.ExecuteAsync(overlapContext,
                                                            CommandLine.Parse(["sandbox", "provision", "--yes"]),
                                                            CancellationToken.None,
                                                            new WindowsSandboxProvisioner(elevator));

        Assert.That(overlapExit, Is.EqualTo(1));
        Assert.That(elevator.Requests, Is.Empty);
        Assert.That(overlapIo.Output, Does.Contain("overlaps the writable root"));
    }

    [Test]
    public async Task Provisioner_RefusesElevationWhenSetupExecutableIsReplacedAfterPlanning()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var packageDir = new TestTempDir();
        using var workDir    = new TestTempDir();
        using var home       = new SandboxTestHome();
        WriteBundledComponents(packageDir.Root, includeSetup : true, includeRunner : true, includeProbe : false);

        var elevator    = new StubElevator();
        var provisioner = new WindowsSandboxProvisioner(elevator);
        var settings    = new WindowsSandboxSettings { SandboxHome = home.Components.SandboxHome };
        var plan        = provisioner.BuildPlan(settings, workDir.Root, "TestUser", packageDir.Root);

        // 规划完成后删除并重建 setup 文件（不同文件索引），提权前复核必须拦截。
        var setupPath = plan.Components.SetupExecutablePath;
        File.Delete(setupPath);
        File.WriteAllText(setupPath, "replaced-setup");

        var error = await TestAssert.ThrowsAsync<InvalidOperationException>(
            () => provisioner.ProvisionAsync(plan, CancellationToken.None));

        Assert.That(error.Message, Does.Contain("changed identity after planning"));
        Assert.That(elevator.Requests, Is.Empty);
    }

    [Test]
    public void ValidateExecutableForUse_RejectsBundledComponentEscapingViaJunctionOrSymlink()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var packageDir  = new TestTempDir();
        using var externalDir = new TestTempDir();
        using var workDir     = new TestTempDir();
        // 普通用户权限即可在 Windows 上创建目录联接点（junction）：把 sandbox 目录重定向到
        // 应用目录之外的目录，验证随包子树边界校验拒绝逃逸。
        var redirectedSandbox = Path.Combine(externalDir.Root, "redirected-sandbox");
        Directory.CreateDirectory(redirectedSandbox);
        File.WriteAllText(Path.Combine(redirectedSandbox, WindowsSandboxComponentResolver.SetupExecutableFileName),
                          "stub-setup");
        var sandboxJunction = Path.Combine(packageDir.Root, "sandbox");
        var mklink = new ProcessStartInfo("cmd.exe")
        {
            Arguments              = $"/d /c mklink /J \"{sandboxJunction}\" \"{redirectedSandbox}\"",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };
        using (var proc = Process.Start(mklink)!)
        {
            proc.WaitForExit();
            Assert.That(proc.ExitCode, Is.EqualTo(0), proc.StandardError.ReadToEnd());
        }

        try
        {
            var resolution = WindowsSandboxComponentResolver.Resolve(new WindowsSandboxSettings(), packageDir.Root);
            var error = TestAssert.Throws<InvalidOperationException>(() =>
                WindowsSandboxComponentResolver.ValidateExecutableForUse(resolution.Setup, packageDir.Root,
                                                                         [workDir.Root]));

            Assert.That(error.Message, Does.Contain("resolves outside the application's bundled sandbox directory"));
        }
        finally
        {
            // 先移除联接点本身，再由 TestTempDir 回收目录，避免递归删联接目标。
            if (Directory.Exists(sandboxJunction)) Directory.Delete(sandboxJunction, false);
        }

        // 同时验证显式配置路径经 junction 落入工作区写根时，也会被最终路径写根互斥拦截。
        var workspaceLinkTarget = Path.Combine(workDir.Root, "inside-workspace");
        Directory.CreateDirectory(workspaceLinkTarget);
        File.WriteAllText(Path.Combine(workspaceLinkTarget, WindowsSandboxComponentResolver.SetupExecutableFileName),
                          "stub-setup");
        var externalJunction = Path.Combine(externalDir.Root, "link-to-workspace");
        var mklinkWriteRoot = new ProcessStartInfo("cmd.exe")
        {
            Arguments              = $"/d /c mklink /J \"{externalJunction}\" \"{workspaceLinkTarget}\"",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };
        using (var proc = Process.Start(mklinkWriteRoot)!)
        {
            proc.WaitForExit();
            Assert.That(proc.ExitCode, Is.EqualTo(0), proc.StandardError.ReadToEnd());
        }

        try
        {
            var configuredSettings = new WindowsSandboxSettings
            {
                SetupExecutablePath = Path.Combine(externalJunction,
                                                   WindowsSandboxComponentResolver.SetupExecutableFileName)
            };
            var resolution = WindowsSandboxComponentResolver.Resolve(configuredSettings, packageDir.Root);
            var error = TestAssert.Throws<InvalidOperationException>(() =>
                WindowsSandboxComponentResolver.ValidateExecutableForUse(resolution.Setup, packageDir.Root,
                                                                         [workDir.Root]));

            Assert.That(error.Message, Does.Contain("overlaps the writable root"));
        }
        finally
        {
            if (Directory.Exists(externalJunction)) Directory.Delete(externalJunction, false);
        }
    }

    [Test]
    public void Composer_ResolvesDifferentBundledComponentsAcrossTwoInstallDirsAndMergesProtectedPaths()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var cliInstall     = new TestTempDir();
        using var desktopInstall = new TestTempDir();
        using var workspace      = new TestTempDir();
        using var home           = new SandboxTestHome();
        WriteBundledComponents(cliInstall.Root, includeSetup : true, includeRunner : true, includeProbe : true);
        WriteBundledComponents(desktopInstall.Root, includeSetup : true, includeRunner : true, includeProbe : true);

        var sharedSettings = new WindowsSandboxSettings
        {
            Enabled     = true,
            SandboxHome = home.Components.SandboxHome
        };

        var cliExecution = WindowsSandboxComposer.Compose(sharedSettings, workspace.Root,
                                                          appBaseDirectory : cliInstall.Root);
        var desktopExecution = WindowsSandboxComposer.Compose(sharedSettings, workspace.Root,
                                                              appBaseDirectory : desktopInstall.Root);

        Assert.That(cliExecution.Components.SetupExecutablePath,
                    Is.EqualTo(Path.Combine(cliInstall.Root, "sandbox",
                                            WindowsSandboxComponentResolver.SetupExecutableFileName)));
        Assert.That(desktopExecution.Components.SetupExecutablePath,
                    Is.EqualTo(Path.Combine(desktopInstall.Root, "sandbox",
                                            WindowsSandboxComponentResolver.SetupExecutableFileName)));

        // 两次交替组合后，protected_paths.json 必须同时保留 CLI 与 Desktop 两套随包组件路径。
        var protectedPathsFile = SandboxProtectedPathsStore.ResolvePath(cliExecution.Components);
        var stored = JsonSerializer.Deserialize<string[]>(File.ReadAllText(protectedPathsFile));
        Assert.That(stored, Is.Not.Null);
        Assert.That(stored!, Does.Contain(cliExecution.Components.SetupExecutablePath));
        Assert.That(stored!, Does.Contain(cliExecution.Components.RunnerExecutablePath));
        Assert.That(stored!, Does.Contain(desktopExecution.Components.SetupExecutablePath));
        Assert.That(stored!, Does.Contain(desktopExecution.Components.RunnerExecutablePath));
    }

    [Test]
    public async Task ProtectedPathsStore_ConcurrentWritesAcrossTwoInstallsPreserveBothComponentSets()
    {
        using var home    = new SandboxTestHome();
        using var cliDir  = new TestTempDir();
        using var deskDir = new TestTempDir();
        var cliComponents = home.Components with
        {
            SetupExecutablePath  = Path.Combine(cliDir.Root, "sandbox", "codex-windows-sandbox-setup.exe"),
            RunnerExecutablePath = Path.Combine(cliDir.Root, "sandbox", "codex-command-runner.exe")
        };
        var desktopComponents = home.Components with
        {
            SetupExecutablePath  = Path.Combine(deskDir.Root, "sandbox", "codex-windows-sandbox-setup.exe"),
            RunnerExecutablePath = Path.Combine(deskDir.Root, "sandbox", "codex-command-runner.exe")
        };

        var cliManifest     = SandboxProtectedPathsStore.ComposeManifest(cliComponents, null);
        var desktopManifest = SandboxProtectedPathsStore.ComposeManifest(desktopComponents, null);

        var tasks = Enumerable.Range(0, 16)
                              .Select(index => Task.Run(() =>
                              {
                                  var target = index % 2 == 0 ? cliComponents : desktopComponents;
                                  var manifest = index % 2 == 0 ? cliManifest : desktopManifest;
                                  SandboxProtectedPathsStore.WriteIfChanged(target, manifest);
                              }))
                              .ToArray();
        await Task.WhenAll(tasks);

        var protectedPathsFile = SandboxProtectedPathsStore.ResolvePath(cliComponents);
        var stored = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(protectedPathsFile));
        Assert.That(stored, Is.Not.Null);
        Assert.That(stored!, Does.Contain(cliComponents.SetupExecutablePath));
        Assert.That(stored!, Does.Contain(cliComponents.RunnerExecutablePath));
        Assert.That(stored!, Does.Contain(desktopComponents.SetupExecutablePath));
        Assert.That(stored!, Does.Contain(desktopComponents.RunnerExecutablePath));
    }

    [Test]
    public void Fingerprint_IncludesComponentSourceSoSwitchingBetweenBundledAndSameConfiguredPathInvalidates()
    {
        using var appDir     = new TestTempDir();
        using var customDir  = new TestTempDir();
        using var workspace  = new TestTempDir();
        using var configDir  = new TestTempDir();
        var userConfigPath   = Path.Combine(configDir.Root, UserConfigStore.FileName);
        var bundledSettings  = new WindowsSandboxSettings { Enabled = true };
        var explicitSamePath = new WindowsSandboxSettings
        {
            Enabled = true,
            SetupExecutablePath = Path.Combine(appDir.Root, "sandbox",
                                               WindowsSandboxComponentResolver.SetupExecutableFileName),
            RunnerExecutablePath = Path.Combine(appDir.Root, "sandbox",
                                                WindowsSandboxComponentResolver.RunnerExecutableFileName)
        };
        var overriddenSettings = new WindowsSandboxSettings
        {
            Enabled             = true,
            SetupExecutablePath = Path.Combine(customDir.Root, "custom-setup.exe")
        };

        var bundledFp = PermissionCompositionFingerprint.Compute(bundledSettings, null, null, workspace.Root, false,
                                                                 [userConfigPath], null, appDir.Root);
        var samePathConfiguredFp = PermissionCompositionFingerprint.Compute(explicitSamePath, null, null,
                                                                            workspace.Root, false, [userConfigPath],
                                                                            null, appDir.Root);
        var overriddenFp = PermissionCompositionFingerprint.Compute(overriddenSettings, null, null, workspace.Root,
                                                                    false, [userConfigPath], null, appDir.Root);

        // 随包模式会强制额外的 sandbox 子树边界检查，即使路径相同也必须与显式配置产出不同指纹。
        Assert.That(samePathConfiguredFp, Is.Not.EqualTo(bundledFp));
        Assert.That(overriddenFp, Is.Not.EqualTo(bundledFp));
        Assert.That(overriddenFp, Is.Not.EqualTo(samePathConfiguredFp));
    }

    [Test]
    public void ProcessWorkspaceAclGrant_ValidatePreparationScopeHandlesBlankComponentPathsAndPeerProtection()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var cliInstall     = new TestTempDir();
        using var desktopInstall = new TestTempDir();
        using var workspace      = new TestTempDir();
        using var home           = new SandboxTestHome();
        WriteBundledComponents(cliInstall.Root, includeSetup : true, includeRunner : true, includeProbe : false);
        WriteBundledComponents(desktopInstall.Root, includeSetup : true, includeRunner : true, includeProbe : false);

        var blankSettings = new WindowsSandboxSettings
        {
            Enabled     = true,
            SandboxHome = home.Components.SandboxHome
        };
        // 先由 Desktop 组合登记 Desktop 的随包组件到共享 protected_paths.json。
        WindowsSandboxComposer.Compose(blankSettings, workspace.Root, appBaseDirectory : desktopInstall.Root);

        // 留空路径在正常独立工作区下必须通过 ValidatePreparationScope（绝不能对空串调 Path.GetFullPath）。
        var grant = new Core.Services.Runtime.ProcessWorkspaceAclGrant([], [workspace.Root], blankSettings,
                                                                       cliInstall.Root);
        var workspaceIdentity = WorkspaceDirectoryIdentity.Capture(workspace.Root);
        Assert.DoesNotThrow(() => grant.ValidatePreparationScope(workspaceIdentity));

        // 若把已登记的 Desktop 安装目录作为准备目标，必须被共享 home 的保护路径拒绝。
        var desktopIdentity = WorkspaceDirectoryIdentity.Capture(desktopInstall.Root);
        var peerGrant = new Core.Services.Runtime.ProcessWorkspaceAclGrant([], [desktopInstall.Root], blankSettings,
                                                                           cliInstall.Root);
        TestAssert.Throws<InvalidDataException>(() => peerGrant.ValidatePreparationScope(desktopIdentity));
    }

    [Test]
    public void ComposerAndProvisioner_RejectComponentsInsideEffectiveTempWriteRoots()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var packageDir = new TestTempDir();
        using var workspace  = new TestTempDir();
        using var home       = new SandboxTestHome();
        WriteBundledComponents(packageDir.Root, includeSetup : true, includeRunner : true, includeProbe : false);

        // 通过 extraEnvironment.TEMP 把临时写根指向包含随包组件的目录：组合器与 provisioner 都必须拒绝。
        var tempOverrideSettings = new WindowsSandboxSettings
        {
            Enabled          = true,
            SandboxHome      = home.Components.SandboxHome,
            ExtraEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["TEMP"] = packageDir.Root
            }
        };

        var composeError = TestAssert.Throws<InvalidOperationException>(() =>
            WindowsSandboxComposer.Compose(tempOverrideSettings, workspace.Root, appBaseDirectory : packageDir.Root));
        Assert.That(composeError.Message, Does.Contain("writable root"));

        var provisioner = new WindowsSandboxProvisioner(new StubElevator());
        var provisionError = TestAssert.Throws<InvalidOperationException>(() =>
            provisioner.BuildPlan(tempOverrideSettings, workspace.Root, "TestUser", packageDir.Root));
        Assert.That(provisionError.Message, Does.Contain("writable root"));
    }

    [Test]
    public void ComposerAndGrantScope_ProtectPeerInstallComponentsRegisteredInSharedHome()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var cliInstall     = new TestTempDir();
        using var desktopInstall = new TestTempDir();
        using var workspace      = new TestTempDir();
        using var home           = new SandboxTestHome();
        WriteBundledComponents(cliInstall.Root, includeSetup : true, includeRunner : true, includeProbe : false);
        WriteBundledComponents(desktopInstall.Root, includeSetup : true, includeRunner : true, includeProbe : false);

        var sharedSettings = new WindowsSandboxSettings
        {
            Enabled     = true,
            SandboxHome = home.Components.SandboxHome
        };

        // Desktop 先组合，把 Desktop 安装目录下的组件写入共享 home 的 protected_paths.json。
        WindowsSandboxComposer.Compose(sharedSettings, workspace.Root, appBaseDirectory : desktopInstall.Root);

        // CLI 随后试图把 Desktop 安装目录作为工作区组合：必须拒绝。
        var composeError = TestAssert.Throws<InvalidOperationException>(() =>
            WindowsSandboxComposer.Compose(sharedSettings, desktopInstall.Root, appBaseDirectory : cliInstall.Root));
        Assert.That(composeError.Message, Does.Contain("shared-home registered protected path"));

        // CLI 在正常工程组合后，试图经 grant 把 Desktop 安装目录加入写根：同样必须拒绝。
        var cliExec = WindowsSandboxComposer.Compose(sharedSettings, workspace.Root, appBaseDirectory : cliInstall.Root);
        var grantError = TestAssert.Throws<InvalidOperationException>(() =>
            WindowsSandboxComposer.ResolveGrantScope(cliExec.Policy, cliExec.Components,
                                                     grantReadRoots : null,
                                                     grantWriteRoots : [desktopInstall.Root],
                                                     grantMetadataWritePaths : null));
        Assert.That(grantError.Message, Does.Contain("shared-home registered protected path"));
    }

    [Test]
    public void ProtectedPathsStore_CorruptedExistingManifestFailsClosedWithoutOverwriting()
    {
        using var home = new SandboxTestHome();
        Directory.CreateDirectory(home.Components.CredentialsDirectory);
        var manifestPath = SandboxProtectedPathsStore.ResolvePath(home.Components);
        const string corrupted = "{not-valid-json";
        File.WriteAllText(manifestPath, corrupted);

        var incoming = SandboxProtectedPathsStore.ComposeManifest(home.Components, null);
        var writeError = TestAssert.Throws<InvalidOperationException>(() =>
            SandboxProtectedPathsStore.WriteIfChanged(home.Components, incoming));
        Assert.That(writeError.Message, Does.Contain("fail closed"));
        Assert.That(File.ReadAllText(manifestPath), Is.EqualTo(corrupted));

        var readError = TestAssert.Throws<InvalidOperationException>(() =>
            SandboxProtectedPathsStore.ReadRegisteredPathsForHome(home.Components.SandboxHome));
        Assert.That(readError.Message, Does.Contain("fail closed"));
    }

    [TestCase("[]")]
    [TestCase("null")]
    [TestCase("123")]
    [TestCase("\"text\"")]
    public void InspectComponent_NonObjectBuildInfoNeverBreaksExistingOrMissingDiagnostics(string buildInfoContent)
    {
        using var packageDir = new TestTempDir();
        using var workspace  = new TestTempDir();
        WriteBundledComponents(packageDir.Root, includeSetup : true, includeRunner : false, includeProbe : false);
        File.WriteAllText(Path.Combine(packageDir.Root, WindowsSandboxComponentResolver.BuildInfoFileName),
                          buildInfoContent);

        var resolution = WindowsSandboxComponentResolver.Resolve(new WindowsSandboxSettings(), packageDir.Root);

        // 存在的 setup 组件完全不受畸形 build-info.json 影响。
        var setupInspection = WindowsSandboxComponentResolver.InspectComponent(resolution.Setup, packageDir.Root);
        Assert.That(setupInspection.IsUsable, Is.True);
        Assert.DoesNotThrow(() =>
            WindowsSandboxComponentResolver.ValidateExecutableForUse(resolution.Setup, packageDir.Root,
                                                                     [workspace.Root]));

        // 缺失的 runner 组件安全回退为通用缺失诊断，不抛 InvalidOperationException。
        var runnerInspection = WindowsSandboxComponentResolver.InspectComponent(resolution.Runner, packageDir.Root);
        Assert.That(runnerInspection.Exists, Is.False);
        Assert.That(runnerInspection.IsReleasePackageBundled, Is.False);
        Assert.That(runnerInspection.Error, Does.Contain("current application directory is missing"));
    }

    [Test]
    public async Task Provisioner_RejectsPeerRegisteredComponentsAtBuildPlanAndBeforeElevation()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var cliInstall     = new TestTempDir();
        using var desktopInstall = new TestTempDir();
        using var workspace      = new TestTempDir();
        using var home           = new SandboxTestHome();
        WriteBundledComponents(cliInstall.Root, includeSetup : true, includeRunner : true, includeProbe : false);
        WriteBundledComponents(desktopInstall.Root, includeSetup : true, includeRunner : true, includeProbe : false);

        var sharedSettings = new WindowsSandboxSettings
        {
            Enabled     = true,
            SandboxHome = home.Components.SandboxHome
        };

        // 场景 A：Desktop 已把其安装目录登记到共享 home，CLI 以 Desktop 目录为工作区构造 provision 计划必须被拒。
        WindowsSandboxComposer.Compose(sharedSettings, workspace.Root, appBaseDirectory : desktopInstall.Root);
        var elevator    = new StubElevator();
        var provisioner = new WindowsSandboxProvisioner(elevator);
        var planError = TestAssert.Throws<InvalidOperationException>(() =>
            provisioner.BuildPlan(sharedSettings, desktopInstall.Root, "TestUser", cliInstall.Root));
        Assert.That(planError.Message, Does.Contain("shared-home registered protected path"));

        // 场景 B：CLI 先完成计划构造，确认期间另一端把位于该工作区子目录的新安装登记进共享清单；提权前必须拦截。
        var nestedPeerInstall = Path.Combine(workspace.Root, "later-peer-install");
        WriteBundledComponents(nestedPeerInstall, includeSetup : true, includeRunner : true, includeProbe : false);
        var plan = provisioner.BuildPlan(sharedSettings, workspace.Root, "TestUser", cliInstall.Root);

        var peerComponents = home.Components with
        {
            SetupExecutablePath  = Path.Combine(nestedPeerInstall, "sandbox",
                                                WindowsSandboxComponentResolver.SetupExecutableFileName),
            RunnerExecutablePath = Path.Combine(nestedPeerInstall, "sandbox",
                                                WindowsSandboxComponentResolver.RunnerExecutableFileName)
        };
        SandboxProtectedPathsStore.WriteIfChanged(peerComponents,
                                                  SandboxProtectedPathsStore.ComposeManifest(peerComponents, null));

        var elevateError = await TestAssert.ThrowsAsync<InvalidOperationException>(
            () => provisioner.ProvisionAsync(plan, CancellationToken.None));
        Assert.That(elevateError.Message, Does.Contain("shared-home registered protected path"));
        Assert.That(elevator.Requests, Is.Empty);
    }

    [Test]
    public async Task Backend_RefreshRevalidatesWriteRootsAgainstNewlyRegisteredPeerComponents()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var cliInstall = new TestTempDir();
        using var workspace  = new TestTempDir();
        using var home       = new SandboxTestHome();
        WriteBundledComponents(cliInstall.Root, includeSetup : true, includeRunner : true, includeProbe : false);

        var sharedSettings = new WindowsSandboxSettings
        {
            Enabled     = true,
            SandboxHome = home.Components.SandboxHome
        };

        // CLI 先正常组合并冻结会话策略。
        var composed = WindowsSandboxComposer.Compose(sharedSettings, workspace.Root,
                                                      appBaseDirectory : cliInstall.Root);
        var invoker  = new StubSetupInvoker();
        var launcher = new StubRunnerLauncher();
        var backend = new WindowsSandboxBackend(composed.Components, composed.Policy, composed.TargetEnvironment,
                                                setupInvoker : invoker,
                                                runnerLauncher : launcher,
                                                validateWriteAcls : (_, _) => Task.CompletedTask);

        // 组合完成后，另一端把位于 CLI 工作区子树内的新组件登记到同一共享 home。
        var laterPeerDir = Path.Combine(workspace.Root, "peer-desktop");
        WriteBundledComponents(laterPeerDir, includeSetup : true, includeRunner : true, includeProbe : false);
        var peerComponents = composed.Components with
        {
            SetupExecutablePath  = Path.Combine(laterPeerDir, "sandbox",
                                                WindowsSandboxComponentResolver.SetupExecutableFileName),
            RunnerExecutablePath = Path.Combine(laterPeerDir, "sandbox",
                                                WindowsSandboxComponentResolver.RunnerExecutableFileName)
        };
        SandboxProtectedPathsStore.WriteIfChanged(peerComponents,
                                                  SandboxProtectedPathsStore.ComposeManifest(peerComponents, null));

        // 已有会话再次发起普通 Shell 执行时，必须在 RefreshAsync 之前复核并拦截。
        var error = await TestAssert.ThrowsAsync<ProcessExecutionStartException>(() =>
            backend.ExecuteAsync(new PreparedProcessExecution("cmd.exe", ["/c", "echo hi"], workspace.Root, 30, false,
                                                              null),
                                 new RecordingOutputCapture(),
                                 new RecordingOutputCapture(),
                                 CancellationToken.None));

        Assert.That(error.Message, Does.Contain("shared-home registered protected path"));
        Assert.That(invoker.RefreshPayloads, Is.Empty);
        Assert.That(launcher.CallCount, Is.Zero);
    }

    [Test]
    public void ProtectedPathsStore_NonNotFoundAccessFailureFailsClosedInsteadOfTreatingAsMissing()
    {
        using var home = new SandboxTestHome();
        Directory.CreateDirectory(home.Components.CredentialsDirectory);
        var manifestPath = SandboxProtectedPathsStore.ResolvePath(home.Components);

        // 把清单路径建为目录：File.Exists 会返回 false，但直接打开文件流会抛 UnauthorizedAccessException / IOException。
        Directory.CreateDirectory(manifestPath);
        try
        {
            var readError = TestAssert.Throws<InvalidOperationException>(() =>
                SandboxProtectedPathsStore.ReadRegisteredPathsForHome(home.Components.SandboxHome));
            Assert.That(readError.Message, Does.Contain("fail closed"));

            var incoming = SandboxProtectedPathsStore.ComposeManifest(home.Components, null);
            var writeError = TestAssert.Throws<InvalidOperationException>(() =>
                SandboxProtectedPathsStore.WriteIfChanged(home.Components, incoming));
            Assert.That(writeError.Message, Does.Contain("fail closed"));
            Assert.That(Directory.Exists(manifestPath), Is.True);
        }
        finally
        {
            if (Directory.Exists(manifestPath)) Directory.Delete(manifestPath, true);
        }

        // 文件存在但被独占锁定（共享冲突 I/O 错误）：读写均必须 fail closed，绝不能当成空清单覆盖。
        const string validJson = """["C:\\Existing\\Protected\\component.exe"]""";
        File.WriteAllText(manifestPath, validJson);
        using (new FileStream(manifestPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var lockedReadError = TestAssert.Throws<InvalidOperationException>(() =>
                SandboxProtectedPathsStore.ReadRegisteredPathsForHome(home.Components.SandboxHome));
            Assert.That(lockedReadError.Message, Does.Contain("fail closed"));

            var incoming = SandboxProtectedPathsStore.ComposeManifest(home.Components, null);
            var lockedWriteError = TestAssert.Throws<InvalidOperationException>(() =>
                SandboxProtectedPathsStore.WriteIfChanged(home.Components, incoming));
            Assert.That(lockedWriteError.Message, Does.Contain("fail closed"));
        }

        Assert.That(File.ReadAllText(manifestPath), Is.EqualTo(validJson));
    }

    [Test]
    public async Task CliSubprocess_StatusFromThirdCwdUsesIsolatedUserConfigWithoutLeakingHostEnv()
    {
        using var configDir = new TestTempDir();
        using var thirdCwd  = new TestTempDir();
        using var tempDir   = new TestTempDir();
        var configPath = Path.Combine(configDir.Root, UserConfigStore.FileName);
        await UserConfigStore.SaveAsync(configPath, new UserConfig
        {
            Settings = new UserConfigSettings
            {
                WindowsSandbox = new WindowsSandboxSettings { Enabled = false }
            }
        }, CancellationToken.None);

        var cliDll = Path.Combine(AppContext.BaseDirectory, "TinyHarness.dll");
        Assert.That(File.Exists(cliDll), Is.True, $"Expected CLI assembly at '{cliDll}'.");

        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory       = thirdCwd.Root,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(cliDll);
        startInfo.ArgumentList.Add("sandbox");
        startInfo.ArgumentList.Add("status");
        startInfo.Environment[UserConfigStore.DirectoryOverrideVariable] = configDir.Root;
        startInfo.Environment["TEMP"]                                    = tempDir.Root;
        startInfo.Environment["TMP"]                                     = tempDir.Root;

        using var process = Process.Start(startInfo)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.That(process.ExitCode, Is.EqualTo(0), stdout + "\n" + stderr);
        Assert.That(stdout, Does.Contain(configPath));
        Assert.That(stdout, Does.Contain(Path.Combine(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),
                                                      "sandbox",
                                                      WindowsSandboxComponentResolver.SetupExecutableFileName)));
    }

    private static void WriteBundledComponents(
        string appRoot, bool includeSetup, bool includeRunner, bool includeProbe)
    {
        var sandboxDir = Path.Combine(appRoot, "sandbox");
        Directory.CreateDirectory(sandboxDir);
        if (includeSetup)
            File.WriteAllText(Path.Combine(sandboxDir, WindowsSandboxComponentResolver.SetupExecutableFileName),
                              "stub-setup");
        if (includeRunner)
            File.WriteAllText(Path.Combine(sandboxDir, WindowsSandboxComponentResolver.RunnerExecutableFileName),
                              "stub-runner");
        if (includeProbe)
            File.WriteAllText(Path.Combine(sandboxDir, WindowsSandboxComponentResolver.DenyProbeExecutableFileName),
                              "stub-probe");
    }

    private static void WriteBuildInfo(string appRoot)
    {
        const string json =
            """
            {
              "version": "0.0.0-test",
              "edition": "CLI",
              "files": {
                "TinyHarness.exe": "0000",
                "sandbox/codex-windows-sandbox-setup.exe": "1111",
                "sandbox/codex-command-runner.exe": "2222",
                "sandbox/codex-windows-managed-deny-probe.exe": "3333"
              }
            }
            """;
        File.WriteAllText(Path.Combine(appRoot, WindowsSandboxComponentResolver.BuildInfoFileName), json);
    }

    private sealed class StubElevator : ISandboxSetupElevator
    {
        public List<(WindowsSandboxComponents Components, string Base64Payload)> Requests { get; } = [];
        public SandboxSetupElevationResult Result { get; set; } = SandboxSetupElevationResult.Completed0;
        public Action? OnElevate { get; set; }

        public Task<SandboxSetupElevationResult> ElevateAsync(
            WindowsSandboxComponents components, string base64Payload, CancellationToken cancellationToken)
        {
            Requests.Add((components, base64Payload));
            OnElevate?.Invoke();
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingConsole : ICliConsole
    {
        private readonly StringBuilder _output = new();
        public           bool          IsInteractive => false;
        public           string        Output        => _output.ToString();

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

        public Task<string?> ReadLineAsync(CancellationToken cancellationToken)       => Task.FromResult<string?>(null);
        public Task<string?> ReadHiddenLineAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class MemoryCredentialStore : ICredentialStore
    {
        public bool    IsSupported                             => false;
        public void    Save(string targetName, string secret)  { }
        public string? Read(string targetName)                 => null;
        public bool    Delete(string targetName)               => false;
    }

    private sealed class StubSetupInvoker : ISandboxSetupInvoker
    {
        public List<SandboxSetupPayload> RefreshPayloads { get; } = [];

        public Task RefreshAsync(SandboxSetupPayload payload, CancellationToken cancellationToken)
        {
            RefreshPayloads.Add(payload);
            return Task.CompletedTask;
        }
    }

    private sealed class StubRunnerLauncher : ISandboxRunnerLauncher
    {
        public int CallCount { get; private set; }

        public Task<SandboxRunnerConnection> LaunchAsync(
            RunnerLaunchRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException("StubRunnerLauncher should never be called in this test.");
        }
    }
}
