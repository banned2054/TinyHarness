using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.Configuration;

namespace TinyHarness.Tests;

public class ConfigurationLoaderTests
{
    [Fact]
    public async Task LoadsValuesFromJsonFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tinyharness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");
        try
        {
            await File.WriteAllTextAsync(path, """
                                         {
                                           "endpoint": "https://example.test/v1",
                                           "model": "model-x",
                                           "apiKeyCredentialTarget": "TinyHarness:model-x",
                                           "contextWindowTokens": 64000,
                                           "reservedOutputTokens": 2000,
                                           "maxAgentSteps": 12,
                                           "defaultToolTimeoutSeconds": 90,
                                           "commandRules": [
                                              {
                                                "executable": "dotnet",
                                                "arguments": ["test", "*"],
                                                "workingDirectory": "sub"
                                              },
                                              {
                                                "mode": "shell",
                                                "shell": "powershell",
                                                "command": "git status | Out-String",
                                                "workingDirectory": "sub"
                                              }
                                           ],
                                           "workspaceRoot": "sub"
                                         }
                                         """);

            var config = await ConfigurationLoader.LoadAsync(path, CancellationToken.None);

            Assert.Equal("https://example.test/v1", config.Endpoint);
            Assert.Equal("model-x", config.Model);
            Assert.Equal("TinyHarness:model-x", config.ApiKeyCredentialTarget);
            Assert.Equal(64000, config.ContextWindowTokens);
            Assert.Equal(2000, config.ReservedOutputTokens);
            Assert.Equal(12, config.MaxAgentSteps);
            Assert.Equal(90, config.DefaultToolTimeoutSeconds);
            Assert.Equal(2, config.CommandRules.Count);
            var rule = config.CommandRules[0];
            Assert.Equal("dotnet", rule.Executable);
            Assert.Equal(["test", "*"], rule.Arguments);
            Assert.Equal("sub", rule.WorkingDirectory);
            var shellRule = config.CommandRules[1];
            Assert.Equal("shell", shellRule.Mode);
            Assert.Equal("powershell", shellRule.Shell);
            Assert.Equal("git status | Out-String", shellRule.Command);
        }
        finally
        {
            Directory.Delete(dir, recursive : true);
        }
    }

    [Fact]
    public async Task MissingFile_KeepsDefaultsAndResolvesWorkspaceToCwd()
    {
        var config = await ConfigurationLoader.LoadAsync("does-not-exist.json", CancellationToken.None);

        Assert.Equal(128_000, config.ContextWindowTokens);
        Assert.Equal(40, config.MaxAgentSteps);
        Assert.Equal(120, config.DefaultToolTimeoutSeconds);
        Assert.Empty(config.CommandRules);
        Assert.Equal(Path.GetFullPath(Environment.CurrentDirectory), config.WorkspaceRoot);
    }

    [Fact]
    public async Task IntField_AsString_IsParsed()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tinyharness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");
        try
        {
            await File.WriteAllTextAsync(path, """{ "maxAgentSteps": "5" }""");

            var config = await ConfigurationLoader.LoadAsync(path, CancellationToken.None);

            Assert.Equal(5, config.MaxAgentSteps);
        }
        finally
        {
            Directory.Delete(dir, recursive : true);
        }
    }

    [Fact]
    public async Task ChatApi_ExplicitResponses_IsApplied()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tinyharness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");
        try
        {
            await File.WriteAllTextAsync(path,
                                         """{ "endpoint": "https://example.test/v1", "chatApi": "responses" }""");

            var config = await ConfigurationLoader.LoadAsync(path, CancellationToken.None);

            Assert.Equal(ChatApiKind.Responses, config.ChatApi);
        }
        finally
        {
            Directory.Delete(dir, recursive : true);
        }
    }

    [Fact]
    public async Task ChatApi_Absent_DefaultsToChatCompletions()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tinyharness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");
        try
        {
            await File.WriteAllTextAsync(path, """{ "endpoint": "https://example.test/v1" }""");

            var config = await ConfigurationLoader.LoadAsync(path, CancellationToken.None);

            Assert.Equal(ChatApiKind.ChatCompletions, config.ChatApi);
        }
        finally
        {
            Directory.Delete(dir, recursive : true);
        }
    }

    [Fact]
    public async Task ChatApi_InvalidValue_ThrowsWithKeyAndValue()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tinyharness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");
        try
        {
            await File.WriteAllTextAsync(path, """{ "chatApi": "grpc" }""");

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                                                                           ConfigurationLoader
                                                                              .LoadAsync(path,
                                                                                         CancellationToken.None));

            Assert.Contains("chatApi", error.Message);
            Assert.Contains("grpc", error.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive : true);
        }
    }

    [Fact]
    public async Task ChatApi_NonStringValue_ThrowsMustBeAString()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tinyharness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");
        try
        {
            await File.WriteAllTextAsync(path, """{ "chatApi": 5 }""");

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                                                                           ConfigurationLoader
                                                                              .LoadAsync(path,
                                                                                         CancellationToken.None));

            Assert.Contains("chatApi", error.Message);
            Assert.Contains("must be a string", error.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive : true);
        }
    }

    [Theory]
    [InlineData("responses", ChatApiKind.Responses)]
    [InlineData("RESPONSES", ChatApiKind.Responses)]
    [InlineData("chat-completions", ChatApiKind.ChatCompletions)]
    [InlineData("Chat-Completions", ChatApiKind.ChatCompletions)]
    [InlineData("chatCompletions", ChatApiKind.ChatCompletions)]
    public async Task ChatApi_AcceptsCaseAndCamelSynonyms(string jsonValue, ChatApiKind expected)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tinyharness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");
        try
        {
            await File.WriteAllTextAsync(path, $$"""{ "chatApi": "{{jsonValue}}" }""");

            var config = await ConfigurationLoader.LoadAsync(path, CancellationToken.None);

            Assert.Equal(expected, config.ChatApi);
        }
        finally
        {
            Directory.Delete(dir, recursive : true);
        }
    }
}
