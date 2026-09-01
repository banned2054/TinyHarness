using TinyHarness.Core.Configuration;

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
                                        "contextWindowTokens": 64000,
                                        "reservedOutputTokens": 2000,
                                        "maxAgentSteps": 12,
                                        "workspaceRoot": "sub"
                                      }
                                      """);

            var config = await ConfigurationLoader.LoadAsync(path, CancellationToken.None);

            Assert.Equal("https://example.test/v1", config.Endpoint);
            Assert.Equal("model-x", config.Model);
            Assert.Equal(64000, config.ContextWindowTokens);
            Assert.Equal(2000, config.ReservedOutputTokens);
            Assert.Equal(12, config.MaxAgentSteps);
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
}
