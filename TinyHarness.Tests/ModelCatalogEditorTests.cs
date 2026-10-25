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

    private sealed class Credentials : ICredentialStore
    {
        public bool IsSupported             => true;
        public bool Fail { get; init; }
        public string? Target { get; private set; }
        public string? Read(string targetName) => throw new AssertionException("Saving must not read old secrets.");

        public void Save(string targetName, string secret)
        {
            Target = targetName;
            if (Fail) throw new IOException(secret);
        }

        public bool Delete(string targetName) => throw new AssertionException("Old secrets must not be deleted.");
    }
}
