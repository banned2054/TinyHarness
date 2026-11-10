using NUnit.Framework;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Desktop.ViewModels;

namespace TinyHarness.Desktop.Tests;

[TestFixture]
public sealed class CatalogProviderDeletionTests
{
    [Test]
    public async Task RequestingProviderDeletionOpensMessageBoxWithoutWriting()
    {
        var path     = NewConfigPath();
        var keep     = new UserProfile { Name          = "keep", Endpoint    = "https://keep.invalid/v1" };
        var remove   = new UserProfile { Name          = "remove", Endpoint  = "https://remove.invalid/v1" };
        var baseline = new UserConfig { DefaultProfile = keep.Name, Profiles = [keep, remove] };
        await UserConfigStore.SaveAsync(path, baseline, CancellationToken.None);
        var settings = new CatalogSettingsViewModel(path, () => Task.CompletedTask, new Credentials());
        await settings.LoadAsync();
        var row     = settings.Providers.Single(item => item.Name == remove.Name);
        var changed = new List<string?>();
        settings.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        row.RequestDeleteCommand.Execute(null);
        Assert.That(settings.IsDeleteConfirmationOpen, Is.True);
        Assert.That(settings.IsSettingsContentEnabled, Is.False);
        Assert.That(changed, Does.Contain(nameof(settings.IsSettingsContentEnabled)));
        Assert.That(settings.DeleteConfirmationTitle, Is.EqualTo("删除供应商"));
        Assert.That(settings.DeleteConfirmationName, Is.EqualTo("remove"));
        Assert.That(settings.DeleteConfirmationMessage, Does.Contain("API 密钥"));
        Assert.That((await UserConfigStore.LoadAsync(path, CancellationToken.None)).Profiles.Count,
                    Is.EqualTo(2), "requesting deletion must not write the config");

        settings.CancelDeleteConfirmationCommand.Execute(null);
        Assert.That(settings.IsDeleteConfirmationOpen, Is.False);
        Assert.That(settings.IsSettingsContentEnabled, Is.True);
        Assert.That((await UserConfigStore.LoadAsync(path, CancellationToken.None)).Profiles.Count, Is.EqualTo(2));
    }

    [Test]
    public async Task ConfirmingProviderDeletionSavesAndRefreshes()
    {
        var path     = NewConfigPath();
        var keep     = new UserProfile { Name          = "keep", Endpoint    = "https://keep.invalid/v1" };
        var remove   = new UserProfile { Name          = "remove", Endpoint  = "https://remove.invalid/v1" };
        var baseline = new UserConfig { DefaultProfile = keep.Name, Profiles = [keep, remove] };
        await UserConfigStore.SaveAsync(path, baseline, CancellationToken.None);
        var credentials = new Credentials();
        var refreshed   = 0;
        var settings = new CatalogSettingsViewModel(path, () =>
        {
            refreshed++;
            return Task.CompletedTask;
        }, credentials);
        await settings.LoadAsync();
        var row = settings.Providers.Single(item => item.Name == remove.Name);

        row.RequestDeleteCommand.Execute(null);
        await settings.ConfirmDeleteConfirmationCommand.ExecuteAsync();

        Assert.That(settings.ConfirmDeleteConfirmationCommand.LastError, Is.Null);
        Assert.That(settings.IsDeleteConfirmationOpen, Is.False);
        Assert.That(settings.Providers.Select(item => item.Name), Is.EqualTo(new[] { keep.Name }),
                    settings.SectionNotice);
        Assert.That(settings.SectionNotice, Does.Contain("已删除"));
        Assert.That(refreshed, Is.EqualTo(1));
        Assert.That(credentials.Deleted, Is.Empty);
        var saved = await UserConfigStore.LoadAsync(path, CancellationToken.None);
        Assert.That(saved.Profiles.Select(item => item.Name), Is.EqualTo(new[] { keep.Name }));
        Assert.That(saved.DefaultProfile, Is.EqualTo(keep.Name));
    }

    [Test]
    public async Task DefaultProviderRejectsDeletionConfirmation()
    {
        var path     = NewConfigPath();
        var keep     = new UserProfile { Name          = "keep", Endpoint    = "https://keep.invalid/v1" };
        var remove   = new UserProfile { Name          = "remove", Endpoint  = "https://remove.invalid/v1" };
        var baseline = new UserConfig { DefaultProfile = keep.Name, Profiles = [keep, remove] };
        await UserConfigStore.SaveAsync(path, baseline, CancellationToken.None);
        var settings = new CatalogSettingsViewModel(path, () => Task.CompletedTask, new Credentials());
        await settings.LoadAsync();
        var row = settings.Providers.Single(item => item.Name == keep.Name);

        row.RequestDeleteCommand.Execute(null);

        Assert.That(settings.IsDeleteConfirmationOpen, Is.False);
        Assert.That(settings.SectionNotice, Does.Contain("默认供应商"));
        Assert.That((await UserConfigStore.LoadAsync(path, CancellationToken.None)).Profiles.Count, Is.EqualTo(2));
    }

    private static string NewConfigPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tinyharness-provider-delete-" +
                                                         Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "user.json");
    }

    private sealed class Credentials : ICredentialStore
    {
        public List<string> Deleted     { get; } = [];
        public bool         IsSupported => true;

        public string? Read(string targetName) => throw new AssertionException("Deletion must not read secrets.");

        public void Save(string targetName, string secret) =>
            throw new AssertionException("Deletion must not save secrets.");

        public bool Delete(string targetName)
        {
            Deleted.Add(targetName);
            return true;
        }
    }
}
