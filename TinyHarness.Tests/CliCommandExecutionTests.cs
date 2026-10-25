using System.Text;
using TinyHarness.Cli.Commands;
using TinyHarness.Cli.Services;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Runtime;

namespace TinyHarness.Tests;

public sealed class CliCommandExecutionTests
{
    [Test]
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
        var result =
            await ConfigCommand.ExecuteAsync(context, CommandLine.Parse(["config", "show"]), CancellationToken.None);

        Assert.That(result, Is.EqualTo(0));
        Assert.That(fixture.Io.Output, Does.Contain("model           : project-model (64,000 tokens context window)"));
        Assert.That(fixture.Io.Output, Does.Not.Contain("model           : (not set)"));
        Assert.That(fixture.Io.Output, Does.Contain("source          : project file"));
    }

    [Test]
    public async Task Init_CreatesDefaultProfileAndModel()
    {
        using var fixture = new CliFixture([
            "work",
            "https://api.example/v1",
            "1",
            "WORK_API_KEY",
            "model-x",
            "64000"
        ]);

        var result = await InitCommand.ExecuteAsync(fixture.CreateContext(), CancellationToken.None);
        var config = await UserConfigStore.LoadAsync(fixture.UserConfigPath, CancellationToken.None);

        Assert.That(result, Is.EqualTo(0));
        Assert.That(config.DefaultProfile, Is.EqualTo("work"));
        var profile = TestAssert.Single(config.Profiles);
        Assert.That(profile.Endpoint, Is.EqualTo("https://api.example/v1"));
        Assert.That(profile.ApiKeyEnvironmentVariable, Is.EqualTo("WORK_API_KEY"));
        Assert.That(profile.DefaultModel, Is.EqualTo("model-x"));
        Assert.That(TestAssert.Single(profile.Models).ContextWindowTokens, Is.EqualTo(64000));
        Assert.That(fixture.Io.Output, Does.Contain("Saved user config"));
    }

    [Test]
    public async Task ProviderAddAndUse_UpdatesTheSelectedProfile()
    {
        using var fixture = new CliFixture([
            "https://team.example/v1",
            "chat-completions",
            "none",
            "y"
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
                    Models       = [new UserProfileModel { Id = "work-model", ContextWindowTokens = 64000 }]
                }
            ]
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

        Assert.That(addResult, Is.EqualTo(0));
        Assert.That(useResult, Is.EqualTo(0));
        Assert.That(listResult, Is.EqualTo(0));
        Assert.That(config.DefaultProfile, Is.EqualTo("work"));
        Assert.That(config.Profiles.Select(p => p.Name), Is.EqualTo(["work", "team"]));
        Assert.That(fixture.Io.Output, Does.Contain("Provider saved"));
        Assert.That(fixture.Io.Output, Does.Contain("Default profile is now 'work'"));
        Assert.That(fixture.Io.Output, Does.Contain("* work"));
    }

    [Test]
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
                    Models       = [new UserProfileModel { Id = "work-model", ContextWindowTokens = 64000 }]
                }
            ]
        }, CancellationToken.None);

        var result = await ConfigCommand.ExecuteAsync(fixture.CreateContext(),
                                                      CommandLine.Parse(["config", "set", "maxAgentSteps", "60"]),
                                                      CancellationToken.None);
        var config = await UserConfigStore.LoadAsync(fixture.UserConfigPath, CancellationToken.None);

        Assert.That(result, Is.EqualTo(0));
        Assert.That(config.Settings!.MaxAgentSteps, Is.EqualTo(60));
        Assert.That(config.Profiles[0].Endpoint, Is.EqualTo("https://work.example/v1"));
        Assert.That(await File.ReadAllTextAsync(projectConfigPath), Is.EqualTo(projectConfigJson));
        Assert.That(fixture.Io.Output, Does.Contain("maxAgentSteps: (built-in default) -> 60"));
    }

    [Test]
    public async Task ConfigSet_ZeroStepsDisablesLimitAndNegativeStepsPreserveConfiguration()
    {
        using var fixture = new CliFixture();
        var       context = fixture.CreateContext();
        var result = await ConfigCommand.ExecuteAsync(context,
                                                      CommandLine.Parse(["config", "set", "maxAgentSteps", "0"]),
                                                      CancellationToken.None);
        Assert.That(result, Is.Zero);
        var saved = await UserConfigStore.LoadAsync(fixture.UserConfigPath, CancellationToken.None);
        Assert.That(saved.Settings!.MaxAgentSteps, Is.Zero);
        var before = await File.ReadAllTextAsync(fixture.UserConfigPath);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
                                                                ConfigCommand.ExecuteAsync(context,
                                                                    CommandLine.Parse([
                                                                        "config", "set", "maxAgentSteps", "-1"
                                                                    ]),
                                                                    CancellationToken.None));
        Assert.That(await File.ReadAllTextAsync(fixture.UserConfigPath), Is.EqualTo(before));
    }

    [Test]
    public async Task AuthSet_StoresOnlyTheEnvironmentVariableReference()
    {
        using var fixture = new CliFixture();
        await UserConfigStore.SaveAsync(fixture.UserConfigPath, ProfileConfig("work"), CancellationToken.None);

        var result = await AuthCommand.ExecuteAsync(fixture.CreateContext(),
                                                    CommandLine.Parse(["auth", "set", "work", "--env", "WORK_API_KEY"]),
                                                    CancellationToken.None);
        var config = await UserConfigStore.LoadAsync(fixture.UserConfigPath, CancellationToken.None);

        Assert.That(result, Is.EqualTo(0));
        Assert.That(TestAssert.Single(config.Profiles).ApiKeyEnvironmentVariable, Is.EqualTo("WORK_API_KEY"));
        Assert.That(config.Profiles[0].ApiKeyCredentialTarget, Is.Empty);
        Assert.That(fixture.Io.Output, Does.Contain("env 'WORK_API_KEY'"));
    }

    [Test]
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

        Assert.That(addResult, Is.EqualTo(0));
        Assert.That(useResult, Is.EqualTo(0));
        Assert.That(listResult, Is.EqualTo(0));
        var profile = TestAssert.Single(config.Profiles);
        Assert.That(profile.DefaultModel, Is.EqualTo("model-y"));
        Assert.That(profile.Models.Single(model => model.Id == "model-y").ContextWindowTokens, Is.EqualTo(200000));
        Assert.That(fixture.Io.Output, Does.Contain("default model: model-y"));
    }

    [Test]
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

        Assert.That(result, Is.EqualTo(0));
        Assert.That(Directory.Exists(sessionDirectory), Is.True);
        Assert.That(fixture.Io.Output, Does.Contain("[ok]   endpoint https://api.example/v1"));
        Assert.That(fixture.Io.Output, Does.Contain("[ok]   model model-x"));
        Assert.That(fixture.Io.Output, Does.Contain("offline mode; pass --connect"));
        Assert.That(fixture.Io.Output, Does.Not.Contain("--connect will send one real model request"));
    }

    [Test]
    public async Task ExplicitProviderAndModelEditsPreserveUneditedConfiguration()
    {
        using var fixture = new CliFixture([
            "https://new.example/v1", "responses", "keep", "y",
            "Model X", "96000", "low,high", "high", "n", "y"
        ]);
        var original = ProfileConfig("work") with { Settings = new UserConfigSettings { MaxAgentSteps = 13 } };
        await UserConfigStore.SaveAsync(fixture.UserConfigPath, original, CancellationToken.None);
        await ProviderCommand.ExecuteAsync(fixture.CreateContext(), CommandLine.Parse(["provider", "edit", "work"]),
                                           CancellationToken.None);
        await ModelCommand.ExecuteAsync(fixture.CreateContext(),
                                        CommandLine.Parse(["model", "edit", "model-x", "--provider", "work"]),
                                        CancellationToken.None);
        var saved = await UserConfigStore.LoadAsync(fixture.UserConfigPath, CancellationToken.None);
        Assert.That(saved.Profiles[0].Models[0].DisplayName, Is.EqualTo("Model X"));
        Assert.That(saved.Profiles[0].Models[0].ContextWindowTokens, Is.EqualTo(96000));
        Assert.That(saved.Profiles[0].Models[0].DefaultReasoningEffort, Is.EqualTo(Core.Models.ChatCompletions.ModelReasoningEffort.High));
        Assert.That(saved.Profiles[0].DefaultModel, Is.EqualTo("model-x"));
        Assert.That(saved.Settings, Is.EqualTo(original.Settings));
    }

    [Test]
    public async Task ModelAddSupportsDisplayAndEffortCapabilities()
    {
        using var fixture = new CliFixture();
        await UserConfigStore.SaveAsync(fixture.UserConfigPath, ProfileConfig("work"), CancellationToken.None);
        await ModelCommand.ExecuteAsync(fixture.CreateContext(), CommandLine.Parse([
            "model", "add", "flash", "--context-window", "64000",
            "--display-name", "Flash", "--efforts", "low,high",
            "--default-effort", "low"
        ]), CancellationToken.None);
        var saved = await UserConfigStore.LoadAsync(fixture.UserConfigPath, CancellationToken.None);
        Assert.That(saved.Profiles[0].Models[1].DisplayName, Is.EqualTo("Flash"));
        Assert.That(saved.Profiles[0].Models[1].ReasoningEfforts, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task InitIncompleteFormDoesNotWriteCredential()
    {
        using var fixture = new CliFixture(["work", "https://api.example/v1", "2", "secret-before-eof"]);
        fixture.Credentials.IsSupported = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => InitCommand.ExecuteAsync(fixture.CreateContext(),
                                                                CancellationToken.None));
        Assert.That(fixture.Credentials.Saves, Is.Zero);
        Assert.That(File.Exists(fixture.UserConfigPath), Is.False);
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
                Models       = [new UserProfileModel { Id = "model-x", ContextWindowTokens = 64000 }]
            }
        ]
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

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }

        public CommandContext CreateContext() => new()
        {
            Io               = Io,
            Credentials      = Credentials,
            UserConfigPath   = UserConfigPath,
            WorkingDirectory = Root
        };
    }

    private sealed class FakeCliConsole(IReadOnlyList<string>? input) : ICliConsole
    {
        private readonly StringBuilder  _error  = new();
        private readonly Queue<string?> _input  = new(input ?? []);
        private readonly StringBuilder  _output = new();

        public string Output => _output.ToString();

        public string Error => _error.ToString();

        public bool IsInteractive => false;

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
        public bool IsSupported { get; set; }
        public int Saves { get; private set; }

        public void Save(string targetName, string secret)
        {
            Saves++;
            _entries[targetName] = secret;
        }

        public string? Read(string targetName) => _entries.GetValueOrDefault(targetName);

        public bool Delete(string targetName) => _entries.Remove(targetName);
    }
}
