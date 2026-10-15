using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.Configuration;

namespace TinyHarness.Tests;

public class UserConfigStoreTests
{
    [Test]
    public async Task SaveThenLoad_RoundTripsProfilesSettingsAndDefault()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tinyharness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "nested", "user-config.json");
        try
        {
            var config = new UserConfig
            {
                DefaultProfile = "work",
                Profiles =
                [
                    new UserProfile
                    {
                        Name                      = "work",
                        Endpoint                  = "https://api.example.com/v1",
                        ApiKeyEnvironmentVariable = "WORK_API_KEY",
                        ApiKeyCredentialTarget    = "TinyHarness:work",
                        ChatApi                   = ChatApiKind.Responses,
                        DefaultModel              = "model-x",
                        Models =
                        [
                            new UserProfileModel { Id = "model-x", ContextWindowTokens = 128_000 },
                            new UserProfileModel { Id = "model-y", ContextWindowTokens = 200_000 }
                        ]
                    }
                ],
                Settings = new UserConfigSettings
                {
                    MaxAgentSteps             = 60,
                    ReservedOutputTokens      = 4_000,
                    CompactionThreshold       = 100_000,
                    DefaultToolTimeoutSeconds = 90,
                    SessionDirectory          = "custom/runs",
                    Worker = new UserWorkerSettings
                    {
                        WorkspaceRoot              = "C:/work/project",
                        RunTimeoutSeconds          = 180,
                        MaxAgentSteps              = 8,
                        DefaultToolTimeoutSeconds  = 25,
                        MaxTaskPackageCharacters   = 10_000,
                        MaxToolCalls               = 15,
                        MaxToolOutputCharacters    = 30_000,
                        MaxContextTokensPerRequest = 20_000,
                        MaxCumulativeContextTokens = 50_000,
                        MaxModelResponseCharacters = 16_000
                    }
                }
            };

            await UserConfigStore.SaveAsync(path, config, CancellationToken.None);
            var loaded = await UserConfigStore.LoadAsync(path, CancellationToken.None);

            Assert.That(loaded.DefaultProfile, Is.EqualTo("work"));
            var profile = TestAssert.Single(loaded.Profiles);
            Assert.That(profile.Name, Is.EqualTo("work"));
            Assert.That(profile.Endpoint, Is.EqualTo("https://api.example.com/v1"));
            Assert.That(profile.ApiKeyEnvironmentVariable, Is.EqualTo("WORK_API_KEY"));
            Assert.That(profile.ApiKeyCredentialTarget, Is.EqualTo("TinyHarness:work"));
            Assert.That(profile.ChatApi, Is.EqualTo(ChatApiKind.Responses));
            Assert.That(profile.DefaultModel, Is.EqualTo("model-x"));
            Assert.That(profile.Models.Count, Is.EqualTo(2));
            Assert.That(profile.Models[0].ContextWindowTokens, Is.EqualTo(128_000));
            Assert.That(profile.Models[1].ContextWindowTokens, Is.EqualTo(200_000));
            TestAssert.NotNull(loaded.Settings);
            Assert.That(loaded.Settings!.MaxAgentSteps, Is.EqualTo(60));
            Assert.That(loaded.Settings.ReservedOutputTokens, Is.EqualTo(4_000));
            Assert.That(loaded.Settings.CompactionThreshold, Is.EqualTo(100_000));
            Assert.That(loaded.Settings.DefaultToolTimeoutSeconds, Is.EqualTo(90));
            Assert.That(loaded.Settings.SessionDirectory, Is.EqualTo("custom/runs"));
            var worker = loaded.Settings.Worker!;
            Assert.That(worker.WorkspaceRoot, Is.EqualTo("C:/work/project"));
            Assert.That(worker.RunTimeoutSeconds, Is.EqualTo(180));
            Assert.That(worker.MaxAgentSteps, Is.EqualTo(8));
            Assert.That(worker.DefaultToolTimeoutSeconds, Is.EqualTo(25));
            Assert.That(worker.MaxTaskPackageCharacters, Is.EqualTo(10_000));
            Assert.That(worker.MaxToolCalls, Is.EqualTo(15));
            Assert.That(worker.MaxToolOutputCharacters, Is.EqualTo(30_000));
            Assert.That(worker.MaxContextTokensPerRequest, Is.EqualTo(20_000));
            Assert.That(worker.MaxCumulativeContextTokens, Is.EqualTo(50_000));
            Assert.That(worker.MaxModelResponseCharacters, Is.EqualTo(16_000));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task LoadAsync_MissingFile_ReturnsEmptyConfig()
    {
        var config = await UserConfigStore.LoadAsync("does-not-exist.json", CancellationToken.None);

        Assert.That(config.DefaultProfile, Is.Null);
        Assert.That(config.Profiles, Is.Empty);
        Assert.That(config.Settings, Is.Null);
    }

    [Test]
    public async Task LoadAsync_LegacyProfileWithoutChatApi_DefaultsToChatCompletions()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tinyharness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "user-config.json");
        try
        {
            await File.WriteAllTextAsync(path,
                                         """{"defaultProfile":"work","profiles":[{"name":"work","endpoint":"https://work.test/v1"}]}""");

            var loaded = await UserConfigStore.LoadAsync(path, CancellationToken.None);

            var profile = TestAssert.Single(loaded.Profiles);
            Assert.That(profile.ChatApi, Is.EqualTo(ChatApiKind.ChatCompletions));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task LoadAsync_NonObjectRoot_ThrowsWithPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tinyharness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "user-config.json");
        try
        {
            await File.WriteAllTextAsync(path, "[1, 2]");

            var error =
                await TestAssert.ThrowsAsync<InvalidDataException>(() =>
                                                                       UserConfigStore
                                                                          .LoadAsync(path, CancellationToken.None));

            Assert.That(error.Message, Does.Contain("user-config.json"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }


    [TestCase("""{"profiles":[{"name":"a"},{"name":"a"}]}""", "duplicates profile name 'a'")]
    [TestCase("""{"profiles":[{"endpoint":"https://x"}]}""", "'profiles[0].name'")]
    [TestCase("""{"profiles":[{"name":"a","models":[{"contextWindowTokens":100}]}]}""", "'profiles[0].models[0].id'")]
    [TestCase("""{"profiles":[{"name":"a","models":[{"id":"m","contextWindowTokens":0}]}]}""", "positive integer")]
    [TestCase("""{"profiles":[{"name":"a","chatApi":"grpc"}]}""", "'profiles[0].chatApi'")]
    [TestCase("""{"profiles":[{"name":"a","chatApi":"grpc"}]}""", "invalid value 'grpc'")]
    [TestCase("""{"profiles":[{"name":"a","chatApi":5}]}""", "must be a string")]
    [TestCase("""{"defaultProfile":42}""", "'defaultProfile'")]
    [TestCase("""{"settings":{"maxAgentSteps":-1}}""", "positive integer")]
    [TestCase("""{"settings":{"compactionThreshold":-5}}""", "non-negative integer")]
    [TestCase("""{"profiles":"nope"}""", "must be an array")]
    public async Task LoadAsync_MalformedValues_ThrowActionableDiagnostics(string json, string expectedFragment)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tinyharness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "user-config.json");
        try
        {
            await File.WriteAllTextAsync(path, json);

            var error =
                await TestAssert.ThrowsAsync<InvalidDataException>(() =>
                                                                       UserConfigStore
                                                                          .LoadAsync(path, CancellationToken.None));

            Assert.That(error.Message, Does.Contain(expectedFragment));
            Assert.That(error.Message, Does.Contain(path));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task SaveAsync_CommandRulesRoundTrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tinyharness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "user-config.json");
        try
        {
            var config = new UserConfig
            {
                Settings = new UserConfigSettings
                {
                    CommandRules =
                    [
                        new CommandRule
                        {
                            Mode = "direct", Executable = "dotnet", Arguments = ["test"], WorkingDirectory = "."
                        }
                    ]
                }
            };

            await UserConfigStore.SaveAsync(path, config, CancellationToken.None);
            var loaded = await UserConfigStore.LoadAsync(path, CancellationToken.None);

            var rule = TestAssert.Single(loaded.Settings!.CommandRules!);
            Assert.That(rule.Executable, Is.EqualTo("dotnet"));
            Assert.That(rule.Arguments, Is.EqualTo(["test"]));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task SaveThenLoad_RoundTripsTheSessionStoreDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tinyharness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "user-config.json");
        try
        {
            var store = Path.Combine(dir, "sessions");
            await UserConfigStore.SaveAsync(path,
                                            new UserConfig
                                            {
                                                Settings = new UserConfigSettings { SessionStoreDirectory = store }
                                            },
                                            CancellationToken.None);

            var loaded = await UserConfigStore.LoadAsync(path, CancellationToken.None);

            Assert.That(loaded.Settings?.SessionStoreDirectory, Is.EqualTo(store));

            // 缺省字段往返为 null：应用默认受保护目录，而不是持久化空值。
            await UserConfigStore.SaveAsync(path, new UserConfig { Settings = new UserConfigSettings() },
                                            CancellationToken.None);
            var reloaded = await UserConfigStore.LoadAsync(path, CancellationToken.None);
            Assert.That(reloaded.Settings?.SessionStoreDirectory, Is.Null);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
