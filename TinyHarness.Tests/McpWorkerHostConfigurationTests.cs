using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Mcp;
using TinyHarness.Core.Services.Runtime;

namespace TinyHarness.Tests;

public class McpWorkerHostConfigurationTests
{
    [Test]
    public async Task ResolveAsync_UsesOnlyUserProfileAndWorkerSettings_NotProjectConfig()
    {
        using var dir       = new TestTempDir();
        var       workspace = Path.Combine(dir.Root, "target");
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
                    Name                   = "glm",
                    Endpoint               = "https://glm.example/v1",
                    ApiKeyCredentialTarget = "tinyharness:test-glm",
                    ChatApi                = ChatApiKind.Responses,
                    DefaultModel           = "glm-flash",
                    Models                 = [new UserProfileModel { Id = "glm-flash", ContextWindowTokens = 32_000 }]
                }
            ],
            Settings = new UserConfigSettings
            {
                CommandRules =
                [
                    new CommandRule
                        { Mode = "direct", Executable = "dangerous", Arguments = [], WorkingDirectory = "." }
                ],
                Worker = new UserWorkerSettings
                {
                    WorkspaceRoot              = "target",
                    RunTimeoutSeconds          = 150,
                    MaxAgentSteps              = 4,
                    DefaultToolTimeoutSeconds  = 15,
                    MaxTaskPackageCharacters   = 9_000,
                    MaxToolCalls               = 10,
                    MaxToolOutputCharacters    = 19_000,
                    MaxContextTokensPerRequest = 20_000,
                    MaxCumulativeContextTokens = 30_000,
                    MaxModelResponseCharacters = 11_000
                }
            }
        }, CancellationToken.None);

        var resolved = await McpWorkerHostConfiguration.ResolveAsync(CancellationToken.None, userConfigPath,
                                                                     dir.Root, new FakeCredentialStore());

        Assert.That(resolved.Model, Is.EqualTo("glm-flash"));
        Assert.That(resolved.Endpoint, Is.EqualTo("https://glm.example/v1"));
        Assert.That(resolved.ChatApi, Is.EqualTo(ChatApiKind.Responses));
        Assert.That(resolved.EndpointType, Is.EqualTo("HTTPS"));
        Assert.That(resolved.ApiKey, Is.EqualTo("test-api-key"));
        Assert.That(resolved.WorkspaceRoot, Is.EqualTo(workspace));
        Assert.That(resolved.ExecutionOptions.Model, Is.EqualTo("glm-flash"));
        Assert.That(resolved.ExecutionOptions.RunTimeout.TotalSeconds, Is.EqualTo(150));
        Assert.That(resolved.ExecutionOptions.MaxAgentSteps, Is.EqualTo(4));
        Assert.That(resolved.ExecutionOptions.MaxTaskPackageCharacters, Is.EqualTo(9_000));
        Assert.That(resolved.ExecutionOptions.MaxToolCalls, Is.EqualTo(10));
        Assert.That(resolved.ExecutionOptions.MaxToolOutputCharacters, Is.EqualTo(19_000));
        Assert.That(resolved.ExecutionOptions.MaxContextTokensPerRequest, Is.EqualTo(20_000));
        Assert.That(resolved.ExecutionOptions.MaxCumulativeContextTokens, Is.EqualTo(30_000));
        Assert.That(resolved.ExecutionOptions.MaxModelResponseCharacters, Is.EqualTo(11_000));
    }

    [Test]
    public async Task ResolveAsync_RejectsWorkerBudgetAboveStableCeiling()
    {
        using var dir       = new TestTempDir();
        var       workspace = Path.Combine(dir.Root, "workspace");
        Directory.CreateDirectory(workspace);
        var path = Path.Combine(dir.Root, "user-config.json");
        await UserConfigStore.SaveAsync(path, new UserConfig
        {
            DefaultProfile = "glm",
            Profiles =
            [
                new UserProfile
                {
                    Name         = "glm", Endpoint = "https://glm.example/v1", ApiKeyEnvironmentVariable = "TEST_KEY",
                    DefaultModel = "m", Models     = [new UserProfileModel { Id = "m", ContextWindowTokens = 16_000 }]
                }
            ],
            Settings = new UserConfigSettings
            {
                Worker = new UserWorkerSettings { WorkspaceRoot = workspace, MaxAgentSteps = 25 }
            }
        }, CancellationToken.None);

        var error = await TestAssert.ThrowsAsync<ConfigException>(() =>
                                                                      McpWorkerHostConfiguration
                                                                         .ResolveAsync(CancellationToken.None, path,
                                                                                   dir.Root,
                                                                                   new FakeCredentialStore()));

        TestAssert.Contains("settings.worker.maxAgentSteps", error.Message, StringComparison.Ordinal);
    }

    private sealed class FakeCredentialStore : ICredentialStore
    {
        public bool IsSupported => true;

        public void Save(string targetName, string secret)
        {
        }

        public string? Read(string targetName) => "test-api-key";

        public bool Delete(string targetName) => false;
    }
}
