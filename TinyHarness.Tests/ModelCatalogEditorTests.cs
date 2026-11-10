using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Runtime;

namespace TinyHarness.Tests;

public sealed class ModelCatalogEditorTests
{
    private static UserConfig Baseline() => new()
    {
        DefaultProfile = "work", Settings = new UserConfigSettings { MaxAgentSteps = 17 },
        Profiles =
        [
            new UserProfile
            {
                Name                   = "work", Endpoint = "https://old.invalid/v1", ChatApi = ChatApiKind.Responses,
                ApiKeyCredentialTarget = "old-target", DefaultModel = "a", Models =
                [
                    ModelCatalogEditor.Model("a", "A", 128000, "low,high", "high"),
                    ModelCatalogEditor.Model("b", "B", 64000, "", "default")
                ]
            }
        ]
    };

    [Test]
    public void ProviderEditsAndModelEditsPreserveSiblingsDefaultsAndSettings()
    {
        var baseline = Baseline();
        var candidate =
            ModelCatalogEditor.PutProvider(baseline,
                                           baseline.Profiles[0] with
                                           {
                                               Endpoint = "https://new.invalid/v1", Models = []
                                           }, true);
        Assert.That(candidate.Profiles[0].Models, Is.EqualTo(baseline.Profiles[0].Models));
        candidate = ModelCatalogEditor.PutModel(candidate, "work",
                                                ModelCatalogEditor.Model("a", "Renamed", 256000, "low,high", "low"),
                                                true);
        Assert.That(candidate.Profiles[0].Models[1], Is.EqualTo(baseline.Profiles[0].Models[1]));
        Assert.That(candidate.Profiles[0].DefaultModel, Is.EqualTo("a"));
        Assert.That(candidate.DefaultProfile, Is.EqualTo("work"));
        Assert.That(candidate.Settings, Is.EqualTo(baseline.Settings));
        Assert.Throws<InvalidOperationException>(() => ModelCatalogEditor.PutProvider(baseline, baseline.Profiles[0],
                                                          false));
        Assert.Throws<InvalidOperationException>(() => ModelCatalogEditor.PutModel(baseline, "work",
                                                          baseline.Profiles[0].Models[0], false));
    }

    [Test]
    public async Task ConflictKeepsOriginalReferenceAndReportsFreshCredentialTarget()
    {
        var path = Path.Combine(Path.GetTempPath(), "tinyharness-catalog-" + Guid.NewGuid().ToString("N"),
                                "user-config.json");
        var baseline = Baseline();
        await UserConfigStore.SaveAsync(path, baseline, CancellationToken.None);
        var concurrent = ModelCatalogEditor.PutModel(baseline, "work",
                                                     ModelCatalogEditor.Model("c", null, 32000, "", "default"), false);
        await UserConfigStore.SaveAsync(path, concurrent, CancellationToken.None);
        var credentials = new Credentials();
        var error = await Assert.ThrowsAsync<IOException>(() => ModelCatalogEditor.SaveAsync(path, baseline, baseline,
                                                                   CancellationToken.None,
                                                                   credentials, "candidate-secret", "work"));
        Assert.That(error!.Message, Does.Contain("New credential entry").And.Not.Contain("candidate-secret"));
        Assert.That(credentials.Target, Does.StartWith("TinyHarness:work:"));
        Assert.That(credentials.Target, Is.Not.EqualTo("old-target"));
        var saved = await UserConfigStore.LoadAsync(path, CancellationToken.None);
        Assert.That(saved.Profiles[0].ApiKeyCredentialTarget, Is.EqualTo("old-target"));
        Assert.That(saved.Profiles[0].Models.Select(item => item.Id), Is.EqualTo(new[] { "a", "b", "c" }));
    }

    [Test]
    public async Task CredentialFailureNeverWritesConfigOrExposesBackendSecret()
    {
        var path = Path.Combine(Path.GetTempPath(), "tinyharness-catalog-" + Guid.NewGuid().ToString("N"),
                                "user-config.json");
        var error = await Assert.ThrowsAsync<IOException>(() => ModelCatalogEditor.SaveAsync(path, new UserConfig(),
                                                                   Baseline(), CancellationToken.None,
                                                                   new Credentials { Fail = true }, "candidate-secret",
                                                                   "work"));
        Assert.That(error!.Message, Does.Not.Contain("candidate-secret"));
        Assert.That(File.Exists(path), Is.False);
        await Task.CompletedTask;
    }

    [Test]
    public void RemovingProviderRejectsDefaultAndMissingNames()
    {
        var baseline = Baseline();
        Assert.Throws<InvalidOperationException>(() => ModelCatalogEditor.RemoveProvider(baseline, "work"));
        Assert.Throws<InvalidOperationException>(() => ModelCatalogEditor.RemoveProvider(baseline, "missing"));

        var spare = baseline.Profiles[0] with
        {
            Name = "spare", ApiKeyCredentialTarget = string.Empty, DefaultModel = string.Empty
        };
        var candidate = ModelCatalogEditor.RemoveProvider(baseline with { Profiles = [.. baseline.Profiles, spare] },
                                                          "spare");
        Assert.That(candidate.Profiles.Select(item => item.Name), Is.EqualTo(new[] { "work" }));
        Assert.That(candidate.DefaultProfile, Is.EqualTo("work"));
        Assert.That(candidate.Settings, Is.EqualTo(baseline.Settings));
    }

    [Test]
    public async Task DeletingProviderWritesConfigAndCleansOnlyItsGeneratedCredential()
    {
        var path = Path.Combine(Path.GetTempPath(), "tinyharness-catalog-" + Guid.NewGuid().ToString("N"),
                                "user-config.json");
        var main = new UserProfile { Name = "main", Endpoint = "https://main.invalid/v1" };
        var keep = new UserProfile
        {
            Name                   = "keep", Endpoint = "https://keep.invalid/v1",
            ApiKeyCredentialTarget = "user-managed-credential",
            Models                 = [new UserProfileModel { Id = "keep-model", ContextWindowTokens = 32_000 }]
        };
        var remove = new UserProfile
        {
            Name                   = "remove", Endpoint = "https://remove.invalid/v1",
            ApiKeyCredentialTarget = "TinyHarness:remove:generated"
        };
        var baseline = new UserConfig { DefaultProfile = "main", Profiles = [main, keep, remove] };
        await UserConfigStore.SaveAsync(path, baseline, CancellationToken.None);

        var credentials = new DeletingCredentials();
        var leftover = await ModelCatalogEditor.DeleteProviderAsync(path, baseline, "remove", CancellationToken.None,
                                                                    credentials);

        Assert.That(leftover, Is.False);
        Assert.That(credentials.Deleted, Is.EqualTo(new[] { "TinyHarness:remove:generated" }));
        var saved = await UserConfigStore.LoadAsync(path, CancellationToken.None);
        Assert.That(saved.Profiles.Select(item => item.Name), Is.EqualTo(new[] { "main", "keep" }));
        Assert.That(saved.DefaultProfile, Is.EqualTo("main"));

        Assert.That(await ModelCatalogEditor.DeleteProviderAsync(path, saved, "keep", CancellationToken.None,
                                                                 credentials),
                    Is.False);
        Assert.That(credentials.Deleted, Is.EqualTo(new[] { "TinyHarness:remove:generated" }),
                    "user-supplied credential targets must not be deleted with the provider");
    }

    [Test]
    public async Task DeletingProviderPreservesSharedGeneratedCredentialIgnoringTargetCase()
    {
        var path = Path.Combine(Path.GetTempPath(), "tinyharness-catalog-" + Guid.NewGuid().ToString("N"),
                                "user-config.json");
        var target = "TinyHarness:remove:generated";
        var baseline = new UserConfig
        {
            DefaultProfile = "main",
            Profiles =
            [
                new UserProfile { Name = "main", Endpoint = "https://main.invalid/v1" },
                new UserProfile
                {
                    Name                   = "keep", Endpoint = "https://keep.invalid/v1",
                    ApiKeyCredentialTarget = target.ToUpperInvariant()
                },
                new UserProfile
                {
                    Name = "remove", Endpoint = "https://remove.invalid/v1", ApiKeyCredentialTarget = target
                }
            ]
        };
        await UserConfigStore.SaveAsync(path, baseline, CancellationToken.None);
        var credentials = new DeletingCredentials();

        var leftover = await ModelCatalogEditor.DeleteProviderAsync(path, baseline, "remove", CancellationToken.None,
                                                                    credentials);

        Assert.That(leftover, Is.False, "a live shared credential is retained, not reported as failed cleanup");
        Assert.That(credentials.Deleted, Is.Empty);
        var saved = await UserConfigStore.LoadAsync(path, CancellationToken.None);
        Assert.That(saved.Profiles.Single(item => item.Name == "keep").ApiKeyCredentialTarget,
                    Is.EqualTo(target.ToUpperInvariant()));
    }

    [Test]
    public async Task CredentialCleanupFailureLeavesConfigDeletedAndReportsResidual()
    {
        var path = Path.Combine(Path.GetTempPath(), "tinyharness-catalog-" + Guid.NewGuid().ToString("N"),
                                "user-config.json");
        var remove = new UserProfile
        {
            Name                   = "remove", Endpoint = "https://remove.invalid/v1",
            ApiKeyCredentialTarget = "TinyHarness:remove:generated"
        };
        var baseline = new UserConfig { Profiles = [remove] };
        await UserConfigStore.SaveAsync(path, baseline, CancellationToken.None);

        var leftover = await ModelCatalogEditor.DeleteProviderAsync(path, baseline, "remove", CancellationToken.None,
                                                                    new DeletingCredentials { FailDelete = true });

        Assert.That(leftover, Is.True);
        var saved = await UserConfigStore.LoadAsync(path, CancellationToken.None);
        Assert.That(saved.Profiles, Is.Empty);
    }

    private sealed class Credentials : ICredentialStore
    {
        public bool    Fail                    { get; init; }
        public string? Target                  { get; private set; }
        public bool    IsSupported             => true;
        public string? Read(string targetName) => throw new AssertionException("Saving must not read old secrets.");

        public void Save(string targetName, string secret)
        {
            Target = targetName;
            if (Fail) throw new IOException(secret);
        }

        public bool Delete(string targetName) => throw new AssertionException("Old secrets must not be deleted.");
    }

    private sealed class DeletingCredentials : ICredentialStore
    {
        public bool         FailDelete  { get; init; }
        public List<string> Deleted     { get; } = [];
        public bool         IsSupported => true;

        public string? Read(string targetName) => throw new AssertionException("Deletion must not read secrets.");

        public void Save(string targetName, string secret) =>
            throw new AssertionException("Deletion must not save secrets.");

        public bool Delete(string targetName)
        {
            Deleted.Add(targetName);
            if (FailDelete) throw new IOException("credential backend unavailable");
            return true;
        }
    }
}
