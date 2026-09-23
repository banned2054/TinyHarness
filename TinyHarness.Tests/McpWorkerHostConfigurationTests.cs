using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Worker;
using TinyHarness.Core.Services.Configuration;
using TinyHarness.Core.Services.Mcp;
using TinyHarness.Core.Services.Runtime;

namespace TinyHarness.Tests;

public class McpWorkerHostConfigurationTests
{
    [Fact]
    public async Task ResolveAsync_UsesOnlyUserProfileAndWorkerSettings_NotProjectConfig()
    {
        using var dir = new TestTempDir();
        var workspace = Path.Combine(dir.Root, "target");
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(Path.Combine(dir.Root, "tinyharness.json"),
                                     """{"endpoint":"https://untrusted-project.example/v1","model":"project-model","workspaceRoot":"."}""");
        var userConfigPath = Path.Combine(dir.Root, "user-config.json");
        await UserConfigStore.SaveAsync(userConfigPath, new UserConfig
        {
            DefaultProfile = "glm",
            Profiles =
            [
                new UserProfile
                {
                    Name = "glm",
                    Endpoint = "https://glm.example/v1",
                    ApiKeyCredentialTarget = "tinyharness:test-glm",
                    DefaultModel = "glm-flash",
                    Models = [new UserProfileModel { Id = "glm-flash", ContextWindowTokens = 32_000 }],
                },
            ],
            Settings = new UserConfigSettings
            {
                CommandRules = [new CommandRule { Mode = "direct", Executable = "dangerous", Arguments = [], WorkingDirectory = "." }],
                Worker = new UserWorkerSettings
                {
                    WorkspaceRoot = "target",
                    RunTimeoutSeconds = 150,
                    MaxAgentSteps = 4,
                    DefaultToolTimeoutSeconds = 15,
                    MaxTaskPackageCharacters = 9_000,
                    MaxToolCalls = 10,
                    MaxToolOutputCharacters = 19_000,
                    MaxContextTokensPerRequest = 20_000,
                    MaxCumulativeContextTokens = 30_000,
                    MaxModelResponseCharacters = 11_000,
                },
            },
        }, CancellationToken.None);

        var resolved = await McpWorkerHostConfiguration.ResolveAsync(CancellationToken.None, userConfigPath,
                                                                       dir.Root, new FakeCredentialStore());

        Assert.Equal("glm-flash", resolved.Model);
        Assert.Equal("https://glm.example/v1", resolved.Endpoint);
        Assert.Equal("HTTPS", resolved.EndpointType);
        Assert.Equal("test-api-key", resolved.ApiKey);
        Assert.Equal(workspace, resolved.WorkspaceRoot);
        Assert.Equal("glm-flash", resolved.ExecutionOptions.Model);
        Assert.Equal(150, resolved.ExecutionOptions.RunTimeout.TotalSeconds);
        Assert.Equal(4, resolved.ExecutionOptions.MaxAgentSteps);
        Assert.Equal(9_000, resolved.ExecutionOptions.MaxTaskPackageCharacters);
        Assert.Equal(10, resolved.ExecutionOptions.MaxToolCalls);
        Assert.Equal(19_000, resolved.ExecutionOptions.MaxToolOutputCharacters);
        Assert.Equal(20_000, resolved.ExecutionOptions.MaxContextTokensPerRequest);
        Assert.Equal(30_000, resolved.ExecutionOptions.MaxCumulativeContextTokens);
        Assert.Equal(11_000, resolved.ExecutionOptions.MaxModelResponseCharacters);
    }

    [Fact]
    public async Task ResolveAsync_RejectsWorkerBudgetAboveStableCeiling()
    {
        using var dir = new TestTempDir();
        var workspace = Path.Combine(dir.Root, "workspace");
        Directory.CreateDirectory(workspace);
        var path = Path.Combine(dir.Root, "user-config.json");
        await UserConfigStore.SaveAsync(path, new UserConfig
        {
            DefaultProfile = "glm",
            Profiles =
            [
                new UserProfile
                {
                    Name = "glm", Endpoint = "https://glm.example/v1", ApiKeyEnvironmentVariable = "TEST_KEY",
                    DefaultModel = "m", Models = [new UserProfileModel { Id = "m", ContextWindowTokens = 16_000 }],
                },
            ],
            Settings = new UserConfigSettings
            {
                Worker = new UserWorkerSettings { WorkspaceRoot = workspace, MaxAgentSteps = 25 },
            },
        }, CancellationToken.None);

        var error = await Assert.ThrowsAsync<ConfigException>(() =>
            McpWorkerHostConfiguration.ResolveAsync(CancellationToken.None, path, dir.Root,
                                                    new FakeCredentialStore()));

        Assert.Contains("settings.worker.maxAgentSteps", error.Message, StringComparison.Ordinal);
    }

    private sealed class FakeCredentialStore : ICredentialStore
    {
        public bool IsSupported => true;
        public void Save(string targetName, string secret) { }
        public string? Read(string targetName) => "test-api-key";
        public bool Delete(string targetName) => false;
    }
}
