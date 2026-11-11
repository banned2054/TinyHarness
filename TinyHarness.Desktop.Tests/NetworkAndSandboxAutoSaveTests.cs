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
        sandbox.Enabled          = true;
        sandbox.AllowTcpLoopback = true;
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
        Assert.That(settings.AllowTcpLoopback, Is.True);
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
