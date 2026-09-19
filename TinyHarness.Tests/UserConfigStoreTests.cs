using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.Configuration;

namespace TinyHarness.Tests;

public class UserConfigStoreTests
{
    [Fact]
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
                        DefaultModel              = "model-x",
                        Models =
                        [
                            new UserProfileModel { Id = "model-x", ContextWindowTokens = 128_000 },
                            new UserProfileModel { Id = "model-y", ContextWindowTokens = 200_000 },
                        ],
                    },
                ],
                Settings = new UserConfigSettings
                {
                    MaxAgentSteps             = 60,
                    ReservedOutputTokens      = 4_000,
                    CompactionThreshold       = 100_000,
                    DefaultToolTimeoutSeconds = 90,
                    SessionDirectory          = "custom/runs",
                },
            };

            await UserConfigStore.SaveAsync(path, config, CancellationToken.None);
            var loaded = await UserConfigStore.LoadAsync(path, CancellationToken.None);

            Assert.Equal("work", loaded.DefaultProfile);
            var profile = Assert.Single(loaded.Profiles);
            Assert.Equal("work", profile.Name);
            Assert.Equal("https://api.example.com/v1", profile.Endpoint);
            Assert.Equal("WORK_API_KEY", profile.ApiKeyEnvironmentVariable);
            Assert.Equal("TinyHarness:work", profile.ApiKeyCredentialTarget);
            Assert.Equal("model-x", profile.DefaultModel);
            Assert.Equal(2, profile.Models.Count);
            Assert.Equal(128_000, profile.Models[0].ContextWindowTokens);
            Assert.Equal(200_000, profile.Models[1].ContextWindowTokens);
            Assert.NotNull(loaded.Settings);
            Assert.Equal(60, loaded.Settings!.MaxAgentSteps);
            Assert.Equal(4_000, loaded.Settings.ReservedOutputTokens);
            Assert.Equal(100_000, loaded.Settings.CompactionThreshold);
            Assert.Equal(90, loaded.Settings.DefaultToolTimeoutSeconds);
            Assert.Equal("custom/runs", loaded.Settings.SessionDirectory);
        }
        finally
        {
            Directory.Delete(dir, recursive : true);
        }
    }

    [Fact]
    public async Task LoadAsync_MissingFile_ReturnsEmptyConfig()
    {
        var config = await UserConfigStore.LoadAsync("does-not-exist.json", CancellationToken.None);

        Assert.Null(config.DefaultProfile);
        Assert.Empty(config.Profiles);
        Assert.Null(config.Settings);
    }

    [Fact]
    public async Task LoadAsync_NonObjectRoot_ThrowsWithPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tinyharness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "user-config.json");
        try
        {
            await File.WriteAllTextAsync(path, "[1, 2]");

            var error =
                await Assert.ThrowsAsync<InvalidDataException>(() =>
                                                                   UserConfigStore
                                                                      .LoadAsync(path, CancellationToken.None));

            Assert.Contains("user-config.json", error.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive : true);
        }
    }

    [Theory]
    [InlineData("""{"profiles":[{"name":"a"},{"name":"a"}]}""", "duplicates profile name 'a'")]
    [InlineData("""{"profiles":[{"endpoint":"https://x"}]}""", "'profiles[0].name'")]
    [InlineData("""{"profiles":[{"name":"a","models":[{"contextWindowTokens":100}]}]}""", "'profiles[0].models[0].id'")]
    [InlineData("""{"profiles":[{"name":"a","models":[{"id":"m","contextWindowTokens":0}]}]}""", "positive integer")]
    [InlineData("""{"defaultProfile":42}""", "'defaultProfile'")]
    [InlineData("""{"settings":{"maxAgentSteps":-1}}""", "positive integer")]
    [InlineData("""{"settings":{"compactionThreshold":-5}}""", "non-negative integer")]
    [InlineData("""{"profiles":"nope"}""", "must be an array")]
    public async Task LoadAsync_MalformedValues_ThrowActionableDiagnostics(string json, string expectedFragment)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tinyharness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "user-config.json");
        try
        {
            await File.WriteAllTextAsync(path, json);

            var error =
                await Assert.ThrowsAsync<InvalidDataException>(() =>
                                                                   UserConfigStore
                                                                      .LoadAsync(path, CancellationToken.None));

            Assert.Contains(expectedFragment, error.Message);
            Assert.Contains(path, error.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive : true);
        }
    }

    [Fact]
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
                            Mode = "direct", Executable = "dotnet", Arguments = ["test"], WorkingDirectory = ".",
                        },
                    ],
                },
            };

            await UserConfigStore.SaveAsync(path, config, CancellationToken.None);
            var loaded = await UserConfigStore.LoadAsync(path, CancellationToken.None);

            var rule = Assert.Single(loaded.Settings!.CommandRules!);
            Assert.Equal("dotnet", rule.Executable);
            Assert.Equal(["test"], rule.Arguments);
        }
        finally
        {
            Directory.Delete(dir, recursive : true);
        }
    }
}
