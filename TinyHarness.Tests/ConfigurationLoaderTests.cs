using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.Configuration;

namespace TinyHarness.Tests;

public class ConfigurationLoaderTests
{
    [Test]
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

            Assert.That(config.Endpoint, Is.EqualTo("https://example.test/v1"));
            Assert.That(config.Model, Is.EqualTo("model-x"));
            Assert.That(config.ApiKeyCredentialTarget, Is.EqualTo("TinyHarness:model-x"));
            Assert.That(config.ContextWindowTokens, Is.EqualTo(64000));
            Assert.That(config.ReservedOutputTokens, Is.EqualTo(2000));
            Assert.That(config.MaxAgentSteps, Is.EqualTo(12));
            Assert.That(config.DefaultToolTimeoutSeconds, Is.EqualTo(90));
            Assert.That(config.CommandRules.Count, Is.EqualTo(2));
            var rule = config.CommandRules[0];
            Assert.That(rule.Executable, Is.EqualTo("dotnet"));
            Assert.That(rule.Arguments, Is.EqualTo(["test", "*"]));
            Assert.That(rule.WorkingDirectory, Is.EqualTo("sub"));
            var shellRule = config.CommandRules[1];
            Assert.That(shellRule.Mode, Is.EqualTo("shell"));
            Assert.That(shellRule.Shell, Is.EqualTo("powershell"));
            Assert.That(shellRule.Command, Is.EqualTo("git status | Out-String"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task MissingFile_KeepsDefaultsAndResolvesWorkspaceToCwd()
    {
        var config = await ConfigurationLoader.LoadAsync("does-not-exist.json", CancellationToken.None);

        Assert.That(config.ContextWindowTokens, Is.EqualTo(128_000));
        Assert.That(config.MaxAgentSteps, Is.EqualTo(40));
        Assert.That(config.DefaultToolTimeoutSeconds, Is.EqualTo(120));
        Assert.That(config.CommandRules, Is.Empty);
        Assert.That(config.WorkspaceRoot, Is.EqualTo(Path.GetFullPath(Environment.CurrentDirectory)));
    }

    [Test]
    public async Task IntField_AsString_IsParsed()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tinyharness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");
        try
        {
            await File.WriteAllTextAsync(path, """{ "maxAgentSteps": "5" }""");

            var config = await ConfigurationLoader.LoadAsync(path, CancellationToken.None);

            Assert.That(config.MaxAgentSteps, Is.EqualTo(5));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
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

            Assert.That(config.ChatApi, Is.EqualTo(ChatApiKind.Responses));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task ChatApi_Absent_DefaultsToChatCompletions()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tinyharness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");
        try
        {
            await File.WriteAllTextAsync(path, """{ "endpoint": "https://example.test/v1" }""");

            var config = await ConfigurationLoader.LoadAsync(path, CancellationToken.None);

            Assert.That(config.ChatApi, Is.EqualTo(ChatApiKind.ChatCompletions));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task ChatApi_InvalidValue_ThrowsWithKeyAndValue()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tinyharness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");
        try
        {
            await File.WriteAllTextAsync(path, """{ "chatApi": "grpc" }""");

            var error = await TestAssert.ThrowsAsync<InvalidDataException>(() =>
                                                                               ConfigurationLoader
                                                                                  .LoadAsync(path,
                                                                                            CancellationToken.None));

            Assert.That(error.Message, Does.Contain("chatApi"));
            Assert.That(error.Message, Does.Contain("grpc"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task ChatApi_NonStringValue_ThrowsMustBeAString()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tinyharness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");
        try
        {
            await File.WriteAllTextAsync(path, """{ "chatApi": 5 }""");

            var error = await TestAssert.ThrowsAsync<InvalidDataException>(() =>
                                                                               ConfigurationLoader
                                                                                  .LoadAsync(path,
                                                                                            CancellationToken.None));

            Assert.That(error.Message, Does.Contain("chatApi"));
            Assert.That(error.Message, Does.Contain("must be a string"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }


    [TestCase("responses", ChatApiKind.Responses)]
    [TestCase("RESPONSES", ChatApiKind.Responses)]
    [TestCase("chat-completions", ChatApiKind.ChatCompletions)]
    [TestCase("Chat-Completions", ChatApiKind.ChatCompletions)]
    [TestCase("chatCompletions", ChatApiKind.ChatCompletions)]
    public async Task ChatApi_AcceptsCaseAndCamelSynonyms(string jsonValue, ChatApiKind expected)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tinyharness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");
        try
        {
            await File.WriteAllTextAsync(path, $$"""{ "chatApi": "{{jsonValue}}" }""");

            var config = await ConfigurationLoader.LoadAsync(path, CancellationToken.None);

            Assert.That(config.ChatApi, Is.EqualTo(expected));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
