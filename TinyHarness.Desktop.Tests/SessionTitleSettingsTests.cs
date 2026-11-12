using NUnit.Framework;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Desktop.ViewModels;

namespace TinyHarness.Desktop.Tests;

[TestFixture]
public sealed class SessionTitleSettingsTests
{
    [Test]
    public async Task TitleAndReviewerModelsSaveIndependentlyInTheGeneralPanel()
    {
        var path = await CreateConfigAsync();
        var panel = new SettingsPanelViewModel(new GeneralSettingsSectionViewModel(path),
                                               new CatalogSettingsViewModel(path, () => Task.CompletedTask));
        await panel.OpenAsync();
        Assert.That(panel.HasError, Is.False, panel.ErrorText);
        panel.SessionTitles.UseCurrentModel = false;
        panel.SessionTitles.SelectedModel   = panel.SessionTitles.Models.Single(item => item.Model == "title");
        await panel.SessionTitles.WaitForAutoSaveAsync();
        var saved = await UserConfigStore.LoadAsync(path, default);
        Assert.That(saved.Settings!.SessionTitles!.Model, Is.EqualTo("title"));
        Assert.That(saved.Settings.Permissions!.ReviewerModel, Is.EqualTo("reviewer"));
        Assert.That(saved.Settings.Permissions.UseCurrentModel, Is.False);

        panel.Permissions.UseCurrentModel = true;
        await panel.Permissions.WaitForAutoSaveAsync();
        saved = await UserConfigStore.LoadAsync(path, default);
        Assert.That(saved.Settings!.Permissions!.UseCurrentModel, Is.True);
        Assert.That(saved.Settings.SessionTitles!.UseCurrentModel, Is.False);
        Assert.That(saved.Settings.SessionTitles.Model, Is.EqualTo("title"));
        Assert.That(panel.Permissions.HasAutoSaveError, Is.False, panel.Permissions.Notice);
        Assert.That(panel.SessionTitles.HasAutoSaveError, Is.False, panel.SessionTitles.Notice);

        panel.SessionTitles.UseCurrentModel = true;
        await panel.SessionTitles.WaitForAutoSaveAsync();
        await panel.OpenAsync();
        Assert.That(panel.SessionTitles.UseCurrentModel, Is.True);
        Assert.That(panel.SessionTitles.SelectedModel!.Model, Is.EqualTo("title"));
    }

    [Test]
    public async Task LoadingAndIncompleteFixedSelectionDoNotWrite()
    {
        var path   = await CreateConfigAsync();
        var before = await File.ReadAllTextAsync(path);
        var editor = new SessionTitleSettingsViewModel(path);
        await editor.LoadAsync();
        editor.UseCurrentModel = false;
        await editor.WaitForAutoSaveAsync();
        Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo(before));
        Assert.That(editor.Notice, Does.Contain("选择后自动保存"));
        Assert.That(editor.HasAutoSaveError, Is.False);
    }

    [Test]
    public async Task ConcurrentConfigChangesAreNotOverwrittenAndRetryKeepsTheDraft()
    {
        var path   = await CreateConfigAsync();
        var editor = new SessionTitleSettingsViewModel(path);
        await editor.LoadAsync();
        var current    = await UserConfigStore.LoadAsync(path, default);
        var concurrent = current with { Settings = current.Settings! with { MaxAgentSteps = 29 } };
        await UserConfigStore.SaveAsync(path, concurrent, default);
        editor.UseCurrentModel = false;
        editor.SelectedModel   = editor.Models.Single(item => item.Model == "title");
        await editor.WaitForAutoSaveAsync();
        Assert.That(editor.HasAutoSaveError, Is.True);
        Assert.That((await UserConfigStore.LoadAsync(path, default)).Settings!.SessionTitles, Is.Null);
        editor.AcceptBaseline(concurrent);
        await editor.SaveCommand.ExecuteAsync();
        var saved = await UserConfigStore.LoadAsync(path, default);
        Assert.That(saved.Settings!.MaxAgentSteps, Is.EqualTo(29));
        Assert.That(saved.Settings.SessionTitles!.Model, Is.EqualTo("title"));
    }

    private static async Task<string> CreateConfigAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), "tinyharness-title-settings-" + Guid.NewGuid().ToString("N"),
                                "config.json");
        await UserConfigStore.SaveAsync(path, new UserConfig
        {
            Profiles =
            [
                new UserProfile
                {
                    Name = "offline", Endpoint = "https://offline.invalid/v1",
                    Models =
                    [
                        new UserProfileModel { Id = "title", ContextWindowTokens    = 64000 },
                        new UserProfileModel { Id = "reviewer", ContextWindowTokens = 64000 }
                    ]
                }
            ],
            Settings = new UserConfigSettings
            {
                Permissions = new PermissionSettings
                {
                    UseCurrentModel = false, ReviewerProfile = "offline", ReviewerModel = "reviewer"
                }
            }
        }, default);
        return path;
    }
}
