using NUnit.Framework;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Desktop.ViewModels;

namespace TinyHarness.Desktop.Tests;

[TestFixture]
public sealed class PermissionSettingsAutoSaveTests
{
    [Test]
    public async Task LoadingAndSynchronizingCatalogDoNotWritePermissions()
    {
        var (path, baseline) = await CreateConfigAsync();
        var before = await File.ReadAllTextAsync(path);
        var editor = new PermissionSettingsViewModel(path);
        var saves  = 0;
        editor.ConfigSaved += _ => saves++;
        await editor.LoadAsync();
        editor.AcceptBaseline(baseline with { Profiles = [] });
        await editor.WaitForAutoSaveAsync();

        Assert.That(saves, Is.Zero);
        Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo(before));
    }

    [Test]
    public async Task DefaultModeAndReviewerSelectionsPersistWithoutSaveCommand()
    {
        var (path, _) = await CreateConfigAsync();
        var editor = new PermissionSettingsViewModel(path);
        await editor.LoadAsync();
        editor.DefaultMode = editor.Modes.Single(mode => mode.Mode == SessionApprovalMode.AIAutoReview);
        await editor.WaitForAutoSaveAsync();
        Assert.That((await UserConfigStore.LoadAsync(path, default)).Settings!.Permissions!.DefaultApprovalMode,
                    Is.EqualTo(SessionApprovalMode.AIAutoReview));

        editor.UseCurrentModel = false;
        await editor.WaitForAutoSaveAsync();
        Assert.That(editor.HasAutoSaveError, Is.False);
        Assert.That(editor.Notice, Does.Contain("选择后自动保存"));
        Assert.That((await UserConfigStore.LoadAsync(path, default)).Settings!.Permissions!.UseCurrentModel, Is.True);

        editor.ReviewerModel = editor.Models.Single(model => model.Model == "second");
        await editor.WaitForAutoSaveAsync();
        var saved = (await UserConfigStore.LoadAsync(path, default)).Settings!.Permissions!;
        Assert.That(saved.UseCurrentModel, Is.False);
        Assert.That(saved.ReviewerProfile, Is.EqualTo("offline"));
        Assert.That(saved.ReviewerModel, Is.EqualTo("second"));

        editor.UseCurrentModel = true;
        await editor.WaitForAutoSaveAsync();
        saved = (await UserConfigStore.LoadAsync(path, default)).Settings!.Permissions!;
        Assert.That(saved.UseCurrentModel, Is.True);
        Assert.That(saved.ReviewerModel, Is.EqualTo("second"));
    }

    [Test]
    public async Task ChangesDuringSaveAreSerializedAndLatestSelectionWins()
    {
        var (path, _) = await CreateConfigAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls   = 0;
        var editor = new PermissionSettingsViewModel(path, async () =>
        {
            if (Interlocked.Increment(ref calls) != 1) return;
            entered.SetResult();
            await release.Task;
        });
        await editor.LoadAsync();
        editor.DefaultMode = editor.Modes.Single(mode => mode.Mode == SessionApprovalMode.AIAutoReview);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        editor.DefaultMode     = editor.Modes.Single(mode => mode.Mode == SessionApprovalMode.HumanApproval);
        editor.UseCurrentModel = false;
        editor.ReviewerModel   = editor.Models.Single(model => model.Model == "second");
        release.SetResult();
        await editor.WaitForAutoSaveAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var saved = (await UserConfigStore.LoadAsync(path, default)).Settings!.Permissions!;
        Assert.That(saved.DefaultApprovalMode, Is.EqualTo(SessionApprovalMode.HumanApproval));
        Assert.That(saved.UseCurrentModel, Is.False);
        Assert.That(saved.ReviewerModel, Is.EqualTo("second"));
        Assert.That(editor.HasAutoSaveError, Is.False, editor.Notice);
    }

    [Test]
    public async Task ReopeningSettingsWaitsForPendingAutoSave()
    {
        var (path, _) = await CreateConfigAsync();
        var editor = new PermissionSettingsViewModel(path);
        await editor.LoadAsync();
        editor.DefaultMode = editor.Modes.Single(mode => mode.Mode == SessionApprovalMode.AIAutoReview);
        await editor.LoadAsync();
        Assert.That(editor.DefaultMode!.Mode, Is.EqualTo(SessionApprovalMode.AIAutoReview));
        Assert.That((await UserConfigStore.LoadAsync(path, default)).Settings!.Permissions!.DefaultApprovalMode,
                    Is.EqualTo(SessionApprovalMode.AIAutoReview));
    }

    [Test]
    public async Task ConflictKeepsDraftAndRetryPreservesOtherSavedSettings()
    {
        var (path, baseline) = await CreateConfigAsync();
        var editor = new PermissionSettingsViewModel(path);
        await editor.LoadAsync();
        var concurrent = baseline with { Settings = baseline.Settings! with { MaxAgentSteps = 29 } };
        await UserConfigStore.SaveAsync(path, concurrent, default);
        editor.DefaultMode = editor.Modes.Single(mode => mode.Mode == SessionApprovalMode.AIAutoReview);
        await editor.WaitForAutoSaveAsync();
        Assert.That(editor.HasAutoSaveError, Is.True);
        Assert.That(editor.Notice, Does.Contain("自动保存失败"));
        Assert.That(editor.DefaultMode!.Mode, Is.EqualTo(SessionApprovalMode.AIAutoReview));
        Assert.That((await UserConfigStore.LoadAsync(path, default)).Settings!.Permissions!.DefaultApprovalMode,
                    Is.EqualTo(SessionApprovalMode.HumanApproval));

        editor.AcceptBaseline(concurrent);
        await editor.SaveCommand.ExecuteAsync();
        var saved = await UserConfigStore.LoadAsync(path, default);
        Assert.That(editor.HasAutoSaveError, Is.False, editor.Notice);
        Assert.That(saved.Settings!.Permissions!.DefaultApprovalMode, Is.EqualTo(SessionApprovalMode.AIAutoReview));
        Assert.That(saved.Settings.MaxAgentSteps, Is.EqualTo(29));
    }

    [Test]
    public async Task AutomaticSaveDoesNotConfirmStandingTrustAndPreservesExplicitConfirmation()
    {
        var (path, _) = await CreateConfigAsync();
        var editor = new PermissionSettingsViewModel(path);
        await editor.LoadAsync();
        editor.DefaultMode = editor.Modes.Single(mode => mode.Mode == SessionApprovalMode.AIAutoReview);
        await editor.WaitForAutoSaveAsync();
        Assert.That(editor.StandingTrustPending, Is.True);
        Assert.That((await UserConfigStore.LoadAsync(path, default)).Settings!.Permissions!.StandingTrustConfirmation,
                    Is.Null);

        await editor.ConfirmStandingTrustAsync();
        var confirmation =
            (await UserConfigStore.LoadAsync(path, default)).Settings!.Permissions!.StandingTrustConfirmation;
        editor.DefaultMode = editor.Modes.Single(mode => mode.Mode == SessionApprovalMode.HumanApproval);
        await editor.WaitForAutoSaveAsync();
        Assert.That(editor.StandingTrustActive, Is.True);
        Assert.That((await UserConfigStore.LoadAsync(path, default)).Settings!.Permissions!.StandingTrustConfirmation,
                    Is.EqualTo(confirmation));

        await editor.RevokeStandingTrustAsync();
        editor.DefaultMode = editor.Modes.Single(mode => mode.Mode == SessionApprovalMode.AIAutoReview);
        await editor.WaitForAutoSaveAsync();
        var saved = await UserConfigStore.LoadAsync(path, default);
        Assert.That(saved.Settings!.Permissions!.StandingTrustConfirmation, Is.Null);
        Assert.That(saved.Settings.WindowsSandbox!.NetworkPolicy, Is.EqualTo(SandboxNetworkPolicy.Restricted));
        Assert.That(saved.Settings.WindowsSandbox.AdditionalWriteRoots, Is.Empty);
    }

    private static async Task<(string Path, UserConfig Baseline)> CreateConfigAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "tinyharness-permission-autosave-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "config.json");
        var baseline = new UserConfig
        {
            Profiles =
            [
                new UserProfile
                {
                    Name = "offline", Endpoint = "https://offline.invalid/v1",
                    Models =
                    [
                        new UserProfileModel { Id = "first", ContextWindowTokens  = 64000 },
                        new UserProfileModel { Id = "second", ContextWindowTokens = 128000 }
                    ]
                }
            ],
            Settings = new UserConfigSettings
            {
                Permissions = new PermissionSettings(),
                WindowsSandbox = new WindowsSandboxSettings
                {
                    Enabled              = true, NetworkPolicy = SandboxNetworkPolicy.Enabled,
                    AdditionalWriteRoots = [Path.Combine(root, "write-root")]
                }
            }
        };
        await UserConfigStore.SaveAsync(path, baseline, default);
        return (path, baseline);
    }
}
