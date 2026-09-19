using System.Text;
using TinyHarness.Cli.Commands;
using TinyHarness.Cli.Services;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.Configuration;
using TinyHarness.Core.Services.Runtime;

namespace TinyHarness.Tests;

public sealed class CliCommandExecutionTests
{
    [Fact]
    public async Task ConfigShow_ProjectFileDisplaysTheEffectiveModel()
    {
        using var fixture = new CliFixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "tinyharness.json"), $$"""
                                     {
                                       "endpoint": "https://project.example/v1",
                                       "model": "project-model",
                                       "contextWindowTokens": 64000,
                                       "workspaceRoot": "{{fixture.Root.Replace("\\", "\\\\")}}"
                                     }
                                     """);

        var context = fixture.CreateContext();
        var result = await ConfigCommand.ExecuteAsync(
                                                      context,
                                                      CommandLine.Parse(["config", "show"]),
                                                      CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Contains("model           : project-model (64,000 tokens context window)", fixture.Io.Output);
        Assert.DoesNotContain("model           : (not set)", fixture.Io.Output);
        Assert.Contains("source          : project file", fixture.Io.Output);
    }

    [Fact]
    public async Task Init_CreatesDefaultProfileAndModel()
    {
        using var fixture = new CliFixture(input :
        [
            "work",
            "https://api.example/v1",
            "1",
            "WORK_API_KEY",
            "model-x",
            "64000",
        ]);

        var result = await InitCommand.ExecuteAsync(fixture.CreateContext(), CancellationToken.None);
        var config = await UserConfigStore.LoadAsync(fixture.UserConfigPath, CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Equal("work", config.DefaultProfile);
        var profile = Assert.Single(config.Profiles);
        Assert.Equal("https://api.example/v1", profile.Endpoint);
        Assert.Equal("WORK_API_KEY", profile.ApiKeyEnvironmentVariable);
        Assert.Equal("model-x", profile.DefaultModel);
        Assert.Equal(64000, Assert.Single(profile.Models).ContextWindowTokens);
        Assert.Contains("Saved user config", fixture.Io.Output);
    }

    [Fact]
    public async Task ProviderAddAndUse_UpdatesTheSelectedProfile()
    {
        using var fixture = new CliFixture(input :
        [
            "https://team.example/v1",
            "3",
            "team-model",
            "32000",
            "y",
        ]);
        await UserConfigStore.SaveAsync(fixture.UserConfigPath, new UserConfig
        {
            DefaultProfile = "work",
            Profiles =
            [
                new UserProfile
                {
                    Name         = "work",
                    Endpoint     = "https://work.example/v1",
                    DefaultModel = "work-model",
                    Models       = [new UserProfileModel { Id = "work-model", ContextWindowTokens = 64000 }],
                },
            ],
        }, CancellationToken.None);

        var addResult = await ProviderCommand.ExecuteAsync(fixture.CreateContext(),
                                                           CommandLine.Parse(["provider", "add", "team"]),
                                                           CancellationToken.None);
        var useResult = await ProviderCommand.ExecuteAsync(fixture.CreateContext(),
                                                           CommandLine.Parse(["provider", "use", "work"]),
                                                           CancellationToken.None);
        var listResult = await ProviderCommand.ExecuteAsync(fixture.CreateContext(),
                                                            CommandLine.Parse(["provider", "list"]),
                                                            CancellationToken.None);
        var config = await UserConfigStore.LoadAsync(fixture.UserConfigPath, CancellationToken.None);

        Assert.Equal(0, addResult);
        Assert.Equal(0, useResult);
        Assert.Equal(0, listResult);
        Assert.Equal("work", config.DefaultProfile);
        Assert.Equal(["work", "team"], config.Profiles.Select(p => p.Name));
        Assert.Contains("Saved profile 'team'.", fixture.Io.Output);
        Assert.Contains("Default profile is now 'work'", fixture.Io.Output);
        Assert.Contains("* work", fixture.Io.Output);
    }

    [Fact]
    public async Task ConfigSet_UpdatesOnlyTheUserSettings()
    {
        using var fixture           = new CliFixture();
        var       projectConfigPath = Path.Combine(fixture.Root, "tinyharness.json");
        const string projectConfigJson = """
            {
              "endpoint": "https://project.example/v1",
              "model": "project-model",
              "maxAgentSteps": 12
            }
            """;
        await File.WriteAllTextAsync(projectConfigPath, projectConfigJson);
        await UserConfigStore.SaveAsync(fixture.UserConfigPath, new UserConfig
        {
            DefaultProfile = "work",
            Profiles =
            [
                new UserProfile
                {
                    Name         = "work",
                    Endpoint     = "https://work.example/v1",
                    DefaultModel = "work-model",
                    Models       = [new UserProfileModel { Id = "work-model", ContextWindowTokens = 64000 }],
                },
            ],
        }, CancellationToken.None);

        var result = await ConfigCommand.ExecuteAsync(fixture.CreateContext(),
                                                      CommandLine.Parse(["config", "set", "maxAgentSteps", "60"]),
                                                      CancellationToken.None);
        var config = await UserConfigStore.LoadAsync(fixture.UserConfigPath, CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Equal(60, config.Settings!.MaxAgentSteps);
        Assert.Equal("https://work.example/v1", config.Profiles[0].Endpoint);
        Assert.Equal(projectConfigJson, await File.ReadAllTextAsync(projectConfigPath));
        Assert.Contains("maxAgentSteps: (built-in default) -> 60", fixture.Io.Output);
    }

    [Fact]
    public async Task AuthSet_StoresOnlyTheEnvironmentVariableReference()
    {
        using var fixture = new CliFixture();
        await UserConfigStore.SaveAsync(fixture.UserConfigPath, ProfileConfig("work"), CancellationToken.None);

        var result = await AuthCommand.ExecuteAsync(fixture.CreateContext(),
                                                    CommandLine.Parse(["auth", "set", "work", "--env", "WORK_API_KEY"]),
                                                    CancellationToken.None);
        var config = await UserConfigStore.LoadAsync(fixture.UserConfigPath, CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Equal("WORK_API_KEY", Assert.Single(config.Profiles).ApiKeyEnvironmentVariable);
        Assert.Empty(config.Profiles[0].ApiKeyCredentialTarget);
        Assert.Contains("env 'WORK_API_KEY'", fixture.Io.Output);
    }

    [Fact]
    public async Task ModelAddUseAndList_UpdatesTheProfileModelSelection()
    {
        using var fixture = new CliFixture();
        await UserConfigStore.SaveAsync(fixture.UserConfigPath, ProfileConfig("work"), CancellationToken.None);

        var addResult = await ModelCommand.ExecuteAsync(fixture.CreateContext(),
                                                        CommandLine.Parse([
                                                            "model", "add", "model-y", "--context-window", "200000"
                                                        ]),
                                                        CancellationToken.None);
        var useResult = await ModelCommand.ExecuteAsync(fixture.CreateContext(),
                                                        CommandLine.Parse(["model", "use", "model-y"]),
                                                        CancellationToken.None);
        var listResult = await ModelCommand.ExecuteAsync(fixture.CreateContext(), CommandLine.Parse(["model", "list"]),
                                                         CancellationToken.None);
        var config = await UserConfigStore.LoadAsync(fixture.UserConfigPath, CancellationToken.None);

        Assert.Equal(0, addResult);
        Assert.Equal(0, useResult);
        Assert.Equal(0, listResult);
        var profile = Assert.Single(config.Profiles);
        Assert.Equal("model-y", profile.DefaultModel);
        Assert.Equal(200000, profile.Models.Single(model => model.Id == "model-y").ContextWindowTokens);
        Assert.Contains("default model: model-y", fixture.Io.Output);
    }

    [Fact]
    public async Task Doctor_DefaultsToOfflineChecks()
    {
        using var fixture          = new CliFixture();
        var       sessionDirectory = Path.Combine(fixture.Root, "sessions");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "tinyharness.json"), $$"""
                                     {
                                       "endpoint": "https://api.example/v1",
                                       "model": "model-x",
                                       "workspaceRoot": "{{fixture.Root.Replace("\\", "\\\\")}}",
                                       "sessionDirectory": "{{sessionDirectory.Replace("\\", "\\\\")}}"
                                     }
                                     """);

        var result =
            await DoctorCommand.ExecuteAsync(fixture.CreateContext(), CommandLine.Parse(["doctor"]),
                                             CancellationToken.None);

        Assert.Equal(0, result);
        Assert.True(Directory.Exists(sessionDirectory));
        Assert.Contains("[ok]   endpoint https://api.example/v1", fixture.Io.Output);
        Assert.Contains("[ok]   model model-x", fixture.Io.Output);
        Assert.Contains("offline mode; pass --connect", fixture.Io.Output);
        Assert.DoesNotContain("--connect will send one real model request", fixture.Io.Output);
    }

    private static UserConfig ProfileConfig(string name) => new()
    {
        DefaultProfile = name,
        Profiles =
        [
            new UserProfile
            {
                Name         = name,
                Endpoint     = "https://api.example/v1",
                DefaultModel = "model-x",
                Models       = [new UserProfileModel { Id = "model-x", ContextWindowTokens = 64000 }],
            },
        ],
    };

    private sealed class CliFixture : IDisposable
    {
        public CliFixture(IReadOnlyList<string>? input = null)
        {
            Root = Path.Combine(Path.GetTempPath(), $"tinyharness-cli-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            UserConfigPath = Path.Combine(Root, "user-config.json");
            Io             = new FakeCliConsole(input);
            Credentials    = new FakeCredentialStore();
        }

        public string Root { get; }

        public string UserConfigPath { get; }

        public FakeCliConsole Io { get; }

        public FakeCredentialStore Credentials { get; }

        public CommandContext CreateContext() => new()
        {
            Io               = Io,
            Credentials      = Credentials,
            UserConfigPath   = UserConfigPath,
            WorkingDirectory = Root,
        };

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive : true);
            }
        }
    }

    private sealed class FakeCliConsole(IReadOnlyList<string>? input) : ICliConsole
    {
        private readonly Queue<string?> _input  = new(input ?? []);
        private readonly StringBuilder  _output = new();
        private readonly StringBuilder  _error  = new();

        public bool IsInteractive => false;

        public string Output => _output.ToString();

        public string Error => _error.ToString();

        public Task WriteAsync(string text, CancellationToken cancellationToken)
        {
            _output.Append(text);
            return Task.CompletedTask;
        }

        public Task WriteLineAsync(string text, CancellationToken cancellationToken)
        {
            _output.AppendLine(text);
            return Task.CompletedTask;
        }

        public Task WriteErrorLineAsync(string text, CancellationToken cancellationToken)
        {
            _error.AppendLine(text);
            return Task.CompletedTask;
        }

        public Task<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_input.Count > 0 ? _input.Dequeue() : null);

        public Task<string?> ReadHiddenLineAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_input.Count > 0 ? _input.Dequeue() : null);
    }

    private sealed class FakeCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, string> _entries = new(StringComparer.Ordinal);

        public bool IsSupported => false;

        public void Save(string targetName, string secret) => _entries[targetName] = secret;

        public string? Read(string targetName) => _entries.GetValueOrDefault(targetName);

        public bool Delete(string targetName) => _entries.Remove(targetName);
    }
}
