using TinyHarness.Core.Configuration;

namespace TinyHarness.Tests;

public class ConfigResolverTests
{
    [Fact]
    public async Task ExplicitPathMissing_ThrowsUsageErrorWithoutFallingBack()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tinyharness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var missing = Path.Combine(dir, "missing.json");

            var error = await Assert.ThrowsAsync<ConfigException>(() => ConfigResolver.ResolveAsync(missing,
                                                                      CancellationToken.None,
                                                                      workingDirectory : dir,
                                                                      userConfigPath :
                                                                      Path.Combine(dir, "user-config.json")));

            Assert.True(error.IsUsageError);
            Assert.Contains(missing, error.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive : true);
        }
    }

    [Fact]
    public async Task ExplicitPath_UsesThatFileAndIgnoresEverythingElse()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tinyharness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var explicitPath = Path.Combine(dir, "live.json");
            await File.WriteAllTextAsync(explicitPath,
                                         """{"endpoint":"https://explicit.test/v1","model":"m-explicit","maxAgentSteps":7}""");

            var resolution = await ConfigResolver.ResolveAsync(explicitPath, CancellationToken.None,
                                                               workingDirectory : dir,
                                                               userConfigPath : Path.Combine(dir, "user-config.json"));

            Assert.Equal(ConfigSourceKind.ExplicitFile, resolution.Source);
            Assert.Equal(Path.GetFullPath(explicitPath), resolution.SourcePath);
            Assert.Equal("https://explicit.test/v1", resolution.Config.Endpoint);
            Assert.Equal("m-explicit", resolution.Config.Model);
            Assert.Equal(7, resolution.Config.MaxAgentSteps);
            Assert.Null(resolution.ProfileName);
        }
        finally
        {
            Directory.Delete(dir, recursive : true);
        }
    }

    [Fact]
    public async Task ProjectFileInWorkingDirectory_TakesPrecedenceOverUserConfig()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tinyharness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "tinyharness.json"),
                                         """{"endpoint":"https://project.test/v1","model":"m-project"}""");
            var userPath = Path.Combine(dir, "user-config.json");
            await File.WriteAllTextAsync(userPath, """{"defaultProfile":"work"}""");

            var resolution = await ConfigResolver.ResolveAsync(null, CancellationToken.None,
                                                               workingDirectory : dir,
                                                               userConfigPath : userPath);

            Assert.Equal(ConfigSourceKind.ProjectFile, resolution.Source);
            Assert.Equal(Path.Combine(dir, "tinyharness.json"), resolution.SourcePath);
            Assert.Equal("https://project.test/v1", resolution.Config.Endpoint);
            Assert.True(resolution.ProjectConfigExists);
            Assert.True(resolution.UserConfigExists);
        }
        finally
        {
            Directory.Delete(dir, recursive : true);
        }
    }

    [Fact]
    public async Task UserConfig_MapsDefaultProfileModelAndSettings()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tinyharness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var userPath = Path.Combine(dir, "user-config.json");
            await File.WriteAllTextAsync(userPath, """
                                         {
                                           "defaultProfile": "work",
                                           "profiles": [
                                             {
                                               "name": "work",
                                               "endpoint": "https://work.test/v1",
                                               "apiKeyCredentialTarget": "TinyHarness:work",
                                               "defaultModel": "model-x",
                                               "models": [
                                                 {"id": "model-x", "contextWindowTokens": 64000}
                                               ]
                                             }
                                           ],
                                           "settings": {"maxAgentSteps": 12, "sessionDirectory": "custom/runs"}
                                         }
                                         """);

            var resolution = await ConfigResolver.ResolveAsync(null, CancellationToken.None,
                                                               workingDirectory : dir,
                                                               userConfigPath : userPath);

            Assert.Equal(ConfigSourceKind.UserConfig, resolution.Source);
            Assert.Equal(userPath, resolution.SourcePath);
            Assert.Equal("work", resolution.ProfileName);
            Assert.Equal("model-x", resolution.ModelName);
            Assert.Equal("https://work.test/v1", resolution.Config.Endpoint);
            Assert.Equal("model-x", resolution.Config.Model);
            Assert.Equal(64_000, resolution.Config.ContextWindowTokens);
            Assert.Equal("TinyHarness:work", resolution.Config.ApiKeyCredentialTarget);
            Assert.Equal(12, resolution.Config.MaxAgentSteps);
            Assert.Equal(8_000, resolution.Config.ReservedOutputTokens); // setting absent -> built-in default
            Assert.True(Path.IsPathRooted(resolution.Config.SessionDirectory));
        }
        finally
        {
            Directory.Delete(dir, recursive : true);
        }
    }

    [Fact]
    public async Task UserConfigWithNoDefaultProfile_KeepsEmptyConfigForDisplay()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tinyharness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var userPath = Path.Combine(dir, "user-config.json");
            await File.WriteAllTextAsync(userPath, """{"profiles":[]}""");

            var resolution = await ConfigResolver.ResolveAsync(null, CancellationToken.None,
                                                               workingDirectory : dir,
                                                               userConfigPath : userPath);

            Assert.Equal(ConfigSourceKind.UserConfig, resolution.Source);
            Assert.Null(resolution.ProfileName);
            Assert.Empty(resolution.Config.Endpoint);
            Assert.Empty(resolution.Config.Model);
        }
        finally
        {
            Directory.Delete(dir, recursive : true);
        }
    }

    [Fact]
    public async Task UserConfigWithMissingDefaultProfile_ThrowsActionableError()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tinyharness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var userPath = Path.Combine(dir, "user-config.json");
            await File.WriteAllTextAsync(userPath, """{"defaultProfile":"ghost","profiles":[]}""");

            var error =
                await Assert.ThrowsAsync<ConfigException>(() => ConfigResolver.ResolveAsync(null,
                                                              CancellationToken.None,
                                                              workingDirectory : dir,
                                                              userConfigPath : userPath));

            Assert.False(error.IsUsageError);
            Assert.Contains("ghost", error.Message);
            Assert.Contains("provider use", error.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive : true);
        }
    }

    [Fact]
    public async Task UserConfigWithUnknownDefaultModel_ThrowsActionableError()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tinyharness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var userPath = Path.Combine(dir, "user-config.json");
            await File.WriteAllTextAsync(userPath, """
                                         {
                                           "defaultProfile": "work",
                                           "profiles": [
                                             {"name": "work", "defaultModel": "ghost-model", "models": []}
                                           ]
                                         }
                                         """);

            var error =
                await Assert.ThrowsAsync<ConfigException>(() => ConfigResolver.ResolveAsync(null,
                                                              CancellationToken.None,
                                                              workingDirectory : dir,
                                                              userConfigPath : userPath));

            Assert.False(error.IsUsageError);
            Assert.Contains("ghost-model", error.Message);
            Assert.Contains("model add", error.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive : true);
        }
    }

    [Fact]
    public async Task NoFilesAtAll_ReturnsDefaultsAndLookupPaths()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tinyharness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var userPath = Path.Combine(dir, "user-config.json");

            var resolution = await ConfigResolver.ResolveAsync(null, CancellationToken.None,
                                                               workingDirectory : dir,
                                                               userConfigPath : userPath);

            Assert.Equal(ConfigSourceKind.Defaults, resolution.Source);
            Assert.Null(resolution.SourcePath);
            Assert.False(resolution.ProjectConfigExists);
            Assert.False(resolution.UserConfigExists);
            Assert.Equal(128_000, resolution.Config.ContextWindowTokens);
            Assert.Equal(Path.Combine(dir, "tinyharness.json"), resolution.ProjectConfigPath);
        }
        finally
        {
            Directory.Delete(dir, recursive : true);
        }
    }
}
