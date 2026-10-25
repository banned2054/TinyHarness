using NUnit.Framework;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Desktop.ViewModels;

namespace TinyHarness.Desktop.Tests;

[TestFixture]
public sealed class CatalogSettingsEditorTests
{
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
        public bool IsSupported => true;
        public string? SavedTarget { get; private set; }
        public string? SavedSecret { get; private set; }

        public string? Read(string targetName) => throw new AssertionException("Editing must not read stored secrets.");

        public void Save(string targetName, string secret)
        {
            SavedTarget = targetName;
            SavedSecret = secret;
        }

        public bool Delete(string targetName) => throw new AssertionException("Editing must not delete credentials.");
    }
}
