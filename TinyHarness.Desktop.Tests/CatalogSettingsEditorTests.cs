using NUnit.Framework;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Desktop.ViewModels;

namespace TinyHarness.Desktop.Tests;

[TestFixture]
public sealed class CatalogSettingsEditorTests
{
    [TestCase("first")]
    [TestCase("second")]
    [TestCase("all")]
    public async Task RemovingModelsOnlyChangesDraftUntilSavedAndPreservesOtherConfiguration(string remove)
    {
        var path = Path.Combine(Path.GetTempPath(), "tinyharness-settings-test-" + Guid.NewGuid().ToString("N"),
                                "config.json");
        var profile = new UserProfile
        {
            Name = "test", Endpoint = "https://provider.invalid/v1", DefaultModel = "second",
            Models =
            [
                new UserProfileModel { Id = "first", DisplayName = "First model", ContextWindowTokens = 64000 },
                new UserProfileModel { Id = "second", ContextWindowTokens = 128000 }
            ]
        };
        var other = new UserProfile { Name = "other", Endpoint = "https://other.invalid/v1", Models = profile.Models };
        var baseline = new UserConfig
        {
            DefaultProfile = profile.Name, Profiles = [profile, other],
            Settings       = new UserConfigSettings { MaxAgentSteps = 17 }
        };
        await UserConfigStore.SaveAsync(path, baseline, CancellationToken.None);
        var before = await File.ReadAllTextAsync(path);
        var editor =
            CatalogProviderEditorViewModel.ForEdit(path, new Credentials(), baseline, profile, _ => { }, _ => { });
        foreach (var entry in editor.ModelEntries.Where(entry => remove == "all" || entry.Id == remove).ToArray())
            editor.RemoveModelCommand.Execute(entry);

        var retained = profile.Models.Where(model => remove != "all" && model.Id != remove).ToArray();
        Assert.That(editor.ModelEntries.Select(entry => entry.Id), Is.EqualTo(retained.Select(model => model.Id)));
        Assert.That(editor.HasModelEntries, Is.EqualTo(retained.Length > 0));
        Assert.That(editor.DefaultModelLabel, Is.EqualTo(remove == "first" ? "second" : "无默认"));
        Assert.That(editor.DefaultModelOptions.Select(option => option.Value),
                    Is.EqualTo(new[] { string.Empty }.Concat(retained.Select(model => model.Id))));
        Assert.That(editor.CanSave, Is.True);
        Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo(before));
        await editor.SaveAsync();

        var saved = await UserConfigStore.LoadAsync(path, CancellationToken.None);
        Assert.That(saved.Profiles[0].Models, Is.EqualTo(retained));
        Assert.That(saved.Profiles[0].DefaultModel, Is.EqualTo(remove == "first" ? "second" : string.Empty));
        Assert.That(saved.Profiles[0].Endpoint, Is.EqualTo(profile.Endpoint));
        Assert.That(saved.Profiles[1].Models, Is.EqualTo(other.Models));
        Assert.That(saved.DefaultProfile, Is.EqualTo(baseline.DefaultProfile));
        Assert.That(saved.Settings, Is.EqualTo(baseline.Settings));
    }

    [Test]
    public async Task RemovingUnfinishedModelRestoresValidDraftAndCancellingRemovalKeepsStoredModels()
    {
        var path = Path.Combine(Path.GetTempPath(), "tinyharness-settings-test-" + Guid.NewGuid().ToString("N"),
                                "config.json");
        var profile = new UserProfile
        {
            Name   = "test", Endpoint = "https://provider.invalid/v1", DefaultModel = "existing",
            Models = [new UserProfileModel { Id = "existing", ContextWindowTokens = 64000 }]
        };
        var baseline = new UserConfig { DefaultProfile = profile.Name, Profiles = [profile] };
        await UserConfigStore.SaveAsync(path, baseline, CancellationToken.None);
        var before   = await File.ReadAllTextAsync(path);
        var settings = new CatalogSettingsViewModel(path, () => Task.CompletedTask, new Credentials());
        await settings.LoadAsync();
        settings.Providers[0].EditCommand.Execute(null);
        var editor = settings.Editor!;
        editor.AddModelCommand.Execute(null);
        Assert.That(editor.HasValidationError, Is.True);
        editor.RemoveModelCommand.Execute(editor.ModelEntries.Last());
        Assert.That(editor.HasValidationError, Is.False);
        Assert.That(editor.IsDirty, Is.False);
        editor.RemoveModelCommand.Execute(editor.ModelEntries.Single());
        Assert.That(editor.HasModelEntries, Is.False);
        editor.CancelCommand.Execute(null);
        Assert.That(settings.Editor, Is.Null);
        Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo(before));
        settings.Providers[0].EditCommand.Execute(null);
        Assert.That(settings.Editor!.ModelEntries.Single().Id, Is.EqualTo("existing"));
        Assert.That(settings.Editor.DefaultModelLabel, Is.EqualTo("existing"));
    }

    [Test]
    public async Task ReaddingModelRemovedByTrashButtonRestoresEditedParameters()
    {
        var profile = new UserProfile
        {
            Name   = "test", Endpoint = "https://provider.invalid/v1",
            Models = [new UserProfileModel { Id = "existing", ContextWindowTokens = 64000 }]
        };
        var baseline = new UserConfig { Profiles = [profile] };
        var editor = CatalogProviderEditorViewModel.ForEdit("unused.json", new Credentials(), baseline, profile,
                                                            _ => { }, _ => { });
        var entry = editor.ModelEntries.Single();
        entry.DisplayName = "Edited model";
        entry.WindowDraft = "96K";
        editor.RemoveModelCommand.Execute(entry);
        editor.FetchModels = (_, _, _) => Task.FromResult<IReadOnlyList<string>>(["existing"]);
        await editor.FetchModelsCommand.ExecuteAsync();
        editor.FilteredDiscoveredModels.Single().IsSelected = true;
        editor.SaveModelSelectionCommand.Execute(null);
        Assert.That(editor.ModelEntries.Single(), Is.SameAs(entry));
        Assert.That(entry.DisplayName, Is.EqualTo("Edited model"));
        Assert.That(entry.WindowDraft, Is.EqualTo("96K"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task AddingProviderPreservesExistingProvidersAfterEditingOrDeletingLastProvider(bool deleteLast)
    {
        var path = Path.Combine(Path.GetTempPath(), "tinyharness-settings-test-" + Guid.NewGuid().ToString("N"),
                                "config.json");
        var profiles = new[] { "glm", "deepseek", "openai" }.Select(name => new UserProfile
        {
            Name   = name, Endpoint = $"https://{name}.invalid/v1", DefaultModel = "existing-model",
            Models = [new UserProfileModel { Id = "existing-model", ContextWindowTokens = 64000 }]
        }).ToArray();
        var baseline = new UserConfig { DefaultProfile = profiles[0].Name, Profiles = profiles };
        await UserConfigStore.SaveAsync(path, baseline, CancellationToken.None);
        var settings = new CatalogSettingsViewModel(path, () => Task.CompletedTask, new Credentials());
        await settings.LoadAsync();
        if (deleteLast)
        {
            settings.Providers.Last().RequestDeleteCommand.Execute(null);
            await settings.ConfirmDeleteConfirmationCommand.ExecuteAsync();
            Assert.That(settings.ConfirmDeleteConfirmationCommand.LastError, Is.Null);
        }

        settings.Providers.Last().EditCommand.Execute(null);
        settings.Editor!.Endpoint = "https://discarded.invalid/v1";

        settings.OpenAddCommand.Execute(null);

        Assert.That(settings.IsAddOpen, Is.True);
        Assert.That(settings.Providers.All(row => !row.IsEditing && !row.HasEditor), Is.True);
        var editor = settings.Editor!;
        Assert.That(editor.IsCreate, Is.True);
        Assert.That(editor.Name, Is.Empty);
        Assert.That(editor.Endpoint, Is.Empty);
        Assert.That(editor.ModelEntries, Is.Empty);
        editor.Name     = "new-provider";
        editor.Endpoint = "https://new-provider.invalid/v1";
        await editor.SaveAsync();

        var saved    = await UserConfigStore.LoadAsync(path, CancellationToken.None);
        var retained = deleteLast ? profiles[..^1] : profiles;
        Assert.That(saved.DefaultProfile, Is.EqualTo(baseline.DefaultProfile));
        Assert.That(saved.Profiles.Select(profile => profile.Name),
                    Is.EqualTo(retained.Select(profile => profile.Name).Append("new-provider")));
        foreach (var profile in retained)
        {
            var actual = saved.Profiles.Single(item => item.Name == profile.Name);
            Assert.That(actual.Endpoint, Is.EqualTo(profile.Endpoint));
            Assert.That(actual.DefaultModel, Is.EqualTo(profile.DefaultModel));
            Assert.That(actual.Models, Is.EqualTo(profile.Models));
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task SavingCollapsedEditorPreservesModelsAndOnlyReplacesCredentialsWhenKeyIsEntered(bool replaceKey)
    {
        var path = Path.Combine(Path.GetTempPath(), "tinyharness-settings-test-" + Guid.NewGuid().ToString("N"),
                                "config.json");
        var profile = new UserProfile
        {
            Name         = "deepseek", Endpoint = "https://deepseek.invalid/v1", ApiKeyCredentialTarget = "existing",
            DefaultModel = "second", Models =
            [
                new UserProfileModel { Id = "first", ContextWindowTokens = 128000 },
                new UserProfileModel { Id = "second", DisplayName        = "Second model", ContextWindowTokens = 64000 }
            ]
        };
        var baseline = new UserConfig { DefaultProfile = profile.Name, Profiles = [profile] };
        await UserConfigStore.SaveAsync(path, baseline, CancellationToken.None);
        var credentials = new Credentials();
        var saved       = false;
        var editor = CatalogProviderEditorViewModel.ForEdit(path, credentials, baseline, profile,
                                                            _ => saved = true, _ => { });
        editor.ToggleCustomCommand.Execute(null);
        Assert.That(editor.IsCustomExpanded, Is.False);
        Assert.That(editor.IsDirty, Is.False);
        Assert.That(editor.Secret, Is.Empty);
        if (replaceKey) editor.Secret = "offline-replacement-key";
        else editor.Endpoint          = "https://updated.invalid/v1";
        await editor.SaveAsync();

        Assert.That(saved, Is.True);
        Assert.That(editor.Secret, Is.Empty);
        var result = await UserConfigStore.LoadAsync(path, CancellationToken.None);
        Assert.That(result.DefaultProfile, Is.EqualTo(baseline.DefaultProfile));
        Assert.That(result.Profiles[0].DefaultModel, Is.EqualTo(profile.DefaultModel));
        Assert.That(result.Profiles[0].Models, Is.EqualTo(profile.Models));
        if (replaceKey)
        {
            Assert.That(result.Profiles[0].ApiKeyCredentialTarget, Is.Not.EqualTo("existing"));
            Assert.That(credentials.SavedTarget, Is.EqualTo(result.Profiles[0].ApiKeyCredentialTarget));
            Assert.That(credentials.SavedSecret, Is.EqualTo("offline-replacement-key"));
            Assert.That(await File.ReadAllTextAsync(path), Does.Not.Contain("offline-replacement-key"));
        }
        else
        {
            Assert.That(result.Profiles[0].ApiKeyCredentialTarget, Is.EqualTo("existing"));
            Assert.That(credentials.SavedTarget, Is.Null);
        }
    }

    private sealed class Credentials : ICredentialStore
    {
        public string? SavedTarget { get; private set; }
        public string? SavedSecret { get; private set; }
        public bool    IsSupported => true;

        public string? Read(string targetName) => throw new AssertionException("Editing must not read stored secrets.");

        public void Save(string targetName, string secret)
        {
            SavedTarget = targetName;
            SavedSecret = secret;
        }

        public bool Delete(string targetName) => throw new AssertionException("Editing must not delete credentials.");
    }
}
