using NUnit.Framework;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Desktop.ViewModels;

namespace TinyHarness.Desktop.Tests;

[TestFixture]
public sealed class NetworkAndSandboxAutoSaveTests
{
    [Test]
    public async Task LoadingAndBaselineSynchronizationDoNotWrite()
    {
        var (path, baseline) = await CreateConfigAsync();
        var before  = await File.ReadAllTextAsync(path);
        var network = new NetworkSettingsViewModel(path);
        var sandbox = new WindowsSandboxSettingsViewModel(path);
        var saves   = 0;
        network.ConfigSaved += _ => saves++;
        sandbox.ConfigSaved += _ => saves++;
        await network.LoadAsync();
        await sandbox.LoadAsync();
        network.AcceptBaseline(baseline);
        sandbox.AcceptBaseline(baseline);
        await network.WaitForAutoSaveAsync();
        await sandbox.WaitForAutoSaveAsync();
        Assert.That(saves, Is.Zero);
        Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo(before));
    }

    [Test]
    public async Task RebuildingSandboxProxyChoicesKeepsSelectionAndDoesNotWrite()
    {
        var (path, baseline) = await CreateConfigAsync();
        baseline = baseline with
        {
            Settings = baseline.Settings! with
            {
                WindowsSandbox = baseline.Settings.WindowsSandbox! with
                {
                    Proxy = new SandboxProxyPolicy { Mode = SandboxProxyMode.Named, ProxyName = "Second proxy" }
                }
            }
        };
        await UserConfigStore.SaveAsync(path, baseline, default);
        var before  = await File.ReadAllTextAsync(path);
        var sandbox = new WindowsSandboxSettingsViewModel(path);
        sandbox.ProxyChoices.CollectionChanged += (_, _) => sandbox.SelectedProxy = null;
        await sandbox.LoadAsync();
        sandbox.AcceptBaseline(baseline);
        await sandbox.WaitForAutoSaveAsync();
        Assert.That(sandbox.SelectedProxy?.Id, Is.EqualTo("Second proxy"));
        Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo(before));
    }

    [Test]
    public async Task DefaultNetworkAndProxySelectionsPersistWithoutSaveCommand()
    {
        var (path, _) = await CreateConfigAsync();
        var network = new NetworkSettingsViewModel(path);
        await network.LoadAsync();
        network.SelectedDefaultMode  = network.DefaultModeOptions.Single(option => option.Value == "proxy");
        network.SelectedDefaultProxy = network.ProxyChoices.Single(option => option.Id          == "second");
        await network.WaitForAutoSaveAsync();
        var saved = await UserConfigStore.LoadAsync(path, default);
        Assert.That(saved.Settings!.Network!.DefaultRoute.Mode, Is.EqualTo(NetworkMode.Proxy));
        Assert.That(saved.Settings.Network.DefaultRoute.ProxyId, Is.EqualTo("second"));
        Assert.That(saved.Settings.Network.Proxies.Count, Is.EqualTo(2));
        network.SelectedDefaultMode = network.DefaultModeOptions.Single(option => option.Value == "direct");
        await network.WaitForAutoSaveAsync();
        Assert.That((await UserConfigStore.LoadAsync(path, default)).Settings!.Network!.DefaultRoute.Mode,
                    Is.EqualTo(NetworkMode.Direct));
        Assert.That(network.HasDefaultRouteSaveError, Is.False, network.Notice);
    }

    [Test]
    public async Task DefaultProxyModeWaitsForAValidProxyAndCanSwitchBack()
    {
        var (path, baseline) = await CreateConfigAsync();
        await UserConfigStore.SaveAsync(path, baseline with
        {
            Settings = baseline.Settings! with { Network = new NetworkSettings() }
        }, default);
        var before  = await File.ReadAllTextAsync(path);
        var network = new NetworkSettingsViewModel(path);
        await network.LoadAsync();
        network.SelectedDefaultMode = network.DefaultModeOptions.Single(option => option.Value == "proxy");
        await network.WaitForAutoSaveAsync();
        Assert.That(network.Notice, Does.Contain("请选择"));
        Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo(before));
        network.SelectedDefaultMode = network.DefaultModeOptions.Single(option => option.Value == "direct");
        await network.WaitForAutoSaveAsync();
        Assert.That((await UserConfigStore.LoadAsync(path, default)).Settings!.Network!.DefaultRoute.Mode,
                    Is.EqualTo(NetworkMode.Direct));
    }

    [Test]
    public async Task SandboxSwitchesPathsAndEnvironmentPersistWithoutConfirmingStandingTrust()
    {
        var (path, _) = await CreateConfigAsync();
        var sandbox = new WindowsSandboxSettingsViewModel(path);
        await sandbox.LoadAsync();
        var root = Path.GetDirectoryName(path)!;
        sandbox.Enabled = true;
        sandbox.SelectedNetworkPolicy =
            sandbox.NetworkPolicyOptions.Single(option => option.Policy == SandboxNetworkPolicy.Enabled);
        sandbox.SelectedProxyMode = sandbox.ProxyModeOptions.Single(option => option.Mode == SandboxProxyMode.Named);
        sandbox.SelectedProxy     = sandbox.ProxyChoices.Single(choice => choice.Id       == "Second proxy");
        sandbox.ReadRootsText     = Path.Combine(root, "read-root");
        sandbox.WriteRootsText    = Path.Combine(root, "write-root");
        sandbox.EnvironmentText   = "EXAMPLE=first\nEXAMPLE=final";
        await sandbox.WaitForAutoSaveAsync();
        var saved    = await UserConfigStore.LoadAsync(path, default);
        var settings = saved.Settings!.WindowsSandbox!;
        Assert.That(sandbox.HasAutoSaveError, Is.False, sandbox.Notice);
        Assert.That(settings.Enabled, Is.True);
        Assert.That(settings.AllowTcpLoopback, Is.False);
        Assert.That(settings.NetworkPolicy, Is.EqualTo(SandboxNetworkPolicy.Enabled));
        Assert.That(settings.Proxy.ProxyName, Is.EqualTo("Second proxy"));
        Assert.That(settings.AdditionalReadRoots, Is.EqualTo(new[] { Path.Combine(root, "read-root") }));
        Assert.That(settings.AdditionalWriteRoots, Is.EqualTo(new[] { Path.Combine(root, "write-root") }));
        Assert.That(settings.ExtraEnvironment!["EXAMPLE"], Is.EqualTo("final"));
        Assert.That(saved.Settings.Permissions!.StandingTrustConfirmation, Is.Null);
    }

    [Test]
    public async Task InvalidSandboxTextKeepsStoredSettingsAndSavesAfterCorrection()
    {
        var (path, _) = await CreateConfigAsync();
        var before  = await File.ReadAllTextAsync(path);
        var sandbox = new WindowsSandboxSettingsViewModel(path);
        await sandbox.LoadAsync();
        sandbox.ReadRootsText   = "relative-path";
        sandbox.EnvironmentText = "unfinished";
        await sandbox.WaitForAutoSaveAsync();
        Assert.That(sandbox.HasAutoSaveError, Is.True);
        Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo(before));
        sandbox.ReadRootsText   = Path.Combine(Path.GetDirectoryName(path)!, "read-root");
        sandbox.EnvironmentText = "EXAMPLE=complete";
        await sandbox.WaitForAutoSaveAsync();
        Assert.That(sandbox.HasAutoSaveError, Is.False, sandbox.Notice);
        Assert.That((await UserConfigStore.LoadAsync(path, default)).Settings!.WindowsSandbox!.ExtraEnvironment!
                    ["EXAMPLE"],
                    Is.EqualTo("complete"));
    }

    [Test]
    public async Task SavingNetworkDoesNotDiscardPendingSandboxText()
    {
        var (path, _) = await CreateConfigAsync();
        var network = new NetworkSettingsViewModel(path);
        var sandbox = new WindowsSandboxSettingsViewModel(path);
        var panel = new SettingsPanelViewModel(new GeneralSettingsSectionViewModel(path),
                                               new CatalogSettingsViewModel(path, () => Task.CompletedTask),
                                               network : network, windowsSandbox : sandbox);
        await panel.OpenAsync();
        sandbox.EnvironmentText     = "EXAMPLE=pending";
        network.SelectedDefaultMode = network.DefaultModeOptions.Single(option => option.Value == "direct");
        await network.WaitForAutoSaveAsync();
        Assert.That(sandbox.EnvironmentText, Is.EqualTo("EXAMPLE=pending"));
        await sandbox.WaitForAutoSaveAsync();
        var saved = await UserConfigStore.LoadAsync(path, default);
        Assert.That(sandbox.HasAutoSaveError, Is.False, sandbox.Notice);
        Assert.That(saved.Settings!.Network!.DefaultRoute.Mode, Is.EqualTo(NetworkMode.Direct));
        Assert.That(saved.Settings.WindowsSandbox!.ExtraEnvironment!["EXAMPLE"], Is.EqualTo("pending"));
    }

    [Test]
    public async Task ChangingNetworkWhileEarlierSaveRefreshesKeepsLatestSelection()
    {
        var (path, _) = await CreateConfigAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls   = 0;
        var network = new NetworkSettingsViewModel(path, async () =>
        {
            if (Interlocked.Increment(ref calls) != 1) return;
            entered.SetResult();
            await release.Task;
        });
        await network.LoadAsync();
        network.SelectedDefaultMode = network.DefaultModeOptions.Single(option => option.Value == "proxy");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        network.SelectedDefaultMode = network.DefaultModeOptions.Single(option => option.Value == "direct");
        release.SetResult();
        await network.WaitForAutoSaveAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(network.SelectedDefaultMode!.Value, Is.EqualTo("direct"));
        Assert.That((await UserConfigStore.LoadAsync(path, default)).Settings!.Network!.DefaultRoute.Mode,
                    Is.EqualTo(NetworkMode.Direct));
        Assert.That(network.HasDefaultRouteSaveError, Is.False, network.Notice);
    }

    [Test]
    public async Task ConflictingNetworkSaveRetainsSelectionAndCanRetryWithUpdatedBaseline()
    {
        var (path, baseline) = await CreateConfigAsync();
        var network = new NetworkSettingsViewModel(path);
        await network.LoadAsync();
        var concurrent = baseline with { Settings = baseline.Settings! with { MaxAgentSteps = 21 } };
        await UserConfigStore.SaveAsync(path, concurrent, default);
        network.SelectedDefaultMode = network.DefaultModeOptions.Single(option => option.Value == "direct");
        await network.WaitForAutoSaveAsync();
        Assert.That(network.HasDefaultRouteSaveError, Is.True);
        network.AcceptBaseline(concurrent);
        Assert.That(network.SelectedDefaultMode!.Value, Is.EqualTo("direct"));
        await network.SaveDefaultRouteCommand.ExecuteAsync();
        var saved = await UserConfigStore.LoadAsync(path, default);
        Assert.That(network.HasDefaultRouteSaveError, Is.False, network.Notice);
        Assert.That(saved.Settings!.MaxAgentSteps, Is.EqualTo(21));
        Assert.That(saved.Settings.Network!.DefaultRoute.Mode, Is.EqualTo(NetworkMode.Direct));
    }

    [Test]
    public async Task SandboxComponentPathsSupportCustomOverridesValidationAndResetToBundledDefaults()
    {
        var (path, _) = await CreateConfigAsync();
        var appDir    = Path.Combine(Path.GetDirectoryName(path)!, "desktop-app");
        var customDir = Path.Combine(Path.GetDirectoryName(path)!, "custom-fork");
        Directory.CreateDirectory(Path.Combine(appDir, "sandbox"));
        Directory.CreateDirectory(customDir);
        var bundledSetup  = Path.Combine(appDir, "sandbox", "codex-windows-sandbox-setup.exe");
        var bundledRunner = Path.Combine(appDir, "sandbox", "codex-command-runner.exe");
        var customSetup   = Path.Combine(customDir, "codex-windows-sandbox-setup.exe");
        var customRunner  = Path.Combine(customDir, "codex-command-runner.exe");
        await File.WriteAllTextAsync(bundledSetup, "stub");
        await File.WriteAllTextAsync(bundledRunner, "stub");
        await File.WriteAllTextAsync(customSetup, "stub");
        await File.WriteAllTextAsync(customRunner, "stub");

        var sandbox = new WindowsSandboxSettingsViewModel(path, appBaseDirectory : appDir);
        await sandbox.LoadAsync();

        Assert.That(sandbox.HasCustomComponentPaths, Is.False);
        Assert.That(sandbox.ResetComponentPathsCommand.CanExecute(null), Is.False);
        Assert.That(sandbox.SetupStatusSummaryText, Does.Contain("当前安装包默认"));
        Assert.That(sandbox.SetupStatusSummaryText, Does.Contain("已找到"));
        Assert.That(sandbox.SetupEffectivePathText, Does.Contain(bundledSetup));
        Assert.That(sandbox.RunnerEffectivePathText, Does.Contain(bundledRunner));

        // 填写非法相对路径时拒绝落盘，保留原有配置。
        var beforeInvalid = await File.ReadAllTextAsync(path);
        sandbox.SetupExecutablePathText = @"relative\setup.exe";
        await sandbox.WaitForAutoSaveAsync();
        Assert.That(sandbox.HasAutoSaveError, Is.True);
        Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo(beforeInvalid));

        // 填写合法绝对路径覆盖，自动保存写入配置并更新来源展示。
        sandbox.SetupExecutablePathText  = customSetup;
        sandbox.RunnerExecutablePathText = customRunner;
        await sandbox.WaitForAutoSaveAsync();
        Assert.That(sandbox.HasAutoSaveError, Is.False, sandbox.Notice);
        Assert.That(sandbox.HasCustomComponentPaths, Is.True);
        Assert.That(sandbox.ResetComponentPathsCommand.CanExecute(null), Is.True);
        Assert.That(sandbox.SetupStatusSummaryText, Does.Contain("自定义路径"));
        Assert.That(sandbox.SetupEffectivePathText, Does.Contain(customSetup));

        var savedWithCustom = await UserConfigStore.LoadAsync(path, default);
        Assert.That(savedWithCustom.Settings!.WindowsSandbox!.SetupExecutablePath, Is.EqualTo(customSetup));
        Assert.That(savedWithCustom.Settings.WindowsSandbox.RunnerExecutablePath, Is.EqualTo(customRunner));

        // 点击恢复默认：清空两个输入框并自动保存，配置文件不再写出两个路径键。
        sandbox.ResetComponentPathsCommand.Execute(null);
        await sandbox.WaitForAutoSaveAsync();
        Assert.That(sandbox.HasAutoSaveError, Is.False, sandbox.Notice);
        Assert.That(sandbox.HasCustomComponentPaths, Is.False);
        Assert.That(sandbox.SetupExecutablePathText, Is.Empty);
        Assert.That(sandbox.RunnerExecutablePathText, Is.Empty);
        Assert.That(sandbox.SetupStatusSummaryText, Does.Contain("当前安装包默认"));
        Assert.That(sandbox.SetupEffectivePathText, Does.Contain(bundledSetup));

        var savedReset = await UserConfigStore.LoadAsync(path, default);
        Assert.That(savedReset.Settings?.WindowsSandbox?.SetupExecutablePath ?? string.Empty, Is.Empty);
        Assert.That(savedReset.Settings?.WindowsSandbox?.RunnerExecutablePath ?? string.Empty, Is.Empty);
        var rawJson = await File.ReadAllTextAsync(path);
        Assert.That(rawJson, Does.Not.Contain("setupExecutablePath"));
        Assert.That(rawJson, Does.Not.Contain("runnerExecutablePath"));
    }

    private static async Task<(string Path, UserConfig Baseline)> CreateConfigAsync()
    {
        var root = Path.Combine(Path.GetTempPath(),
                                "tinyharness-network-sandbox-autosave-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "config.json");
        var baseline = new UserConfig
        {
            Settings = new UserConfigSettings
            {
                Permissions    = new PermissionSettings(),
                WindowsSandbox = new WindowsSandboxSettings(),
                Network = new NetworkSettings
                {
                    Proxies =
                    [
                        new ProxyDefinition { Id = "first", Name  = "First proxy", Host  = "127.0.0.1", Port = 7890 },
                        new ProxyDefinition { Id = "second", Name = "Second proxy", Host = "127.0.0.1", Port = 7891 }
                    ]
                }
            }
        };
        await UserConfigStore.SaveAsync(path, baseline, default);
        return (path, baseline);
    }
}
