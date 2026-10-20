using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.ChatCompletions.Configuration;

namespace TinyHarness.Tests;

public class ConfigResolverTests
{
    [Test]
    public async Task ExplicitPathMissing_ThrowsUsageErrorWithoutFallingBack()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tinyharness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var missing = Path.Combine(dir, "missing.json");

            var error = await TestAssert.ThrowsAsync<ConfigException>(() => ConfigResolver.ResolveAsync(missing,
                                                                               CancellationToken.None,
                                                                               dir,
                                                                               Path.Combine(dir, "user-config.json")));

            Assert.That(error.IsUsageError, Is.True);
            Assert.That(error.Message, Does.Contain(missing));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
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
                                                               dir,
                                                               Path.Combine(dir, "user-config.json"));

            Assert.That(resolution.Source, Is.EqualTo(ConfigSourceKind.ExplicitFile));
            Assert.That(resolution.SourcePath, Is.EqualTo(Path.GetFullPath(explicitPath)));
            Assert.That(resolution.Config.Endpoint, Is.EqualTo("https://explicit.test/v1"));
            Assert.That(resolution.Config.Model, Is.EqualTo("m-explicit"));
            Assert.That(resolution.Config.MaxAgentSteps, Is.EqualTo(7));
            Assert.That(resolution.ProfileName, Is.Null);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
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
                                                               dir,
                                                               userPath);

            Assert.That(resolution.Source, Is.EqualTo(ConfigSourceKind.ProjectFile));
            Assert.That(resolution.SourcePath, Is.EqualTo(Path.Combine(dir, "tinyharness.json")));
            Assert.That(resolution.Config.Endpoint, Is.EqualTo("https://project.test/v1"));
            Assert.That(resolution.ProjectConfigExists, Is.True);
            Assert.That(resolution.UserConfigExists, Is.True);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
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
                                               "chatApi": "responses",
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
                                                               dir,
                                                               userPath);

            Assert.That(resolution.Source, Is.EqualTo(ConfigSourceKind.UserConfig));
            Assert.That(resolution.SourcePath, Is.EqualTo(userPath));
            Assert.That(resolution.ProfileName, Is.EqualTo("work"));
            Assert.That(resolution.ModelName, Is.EqualTo("model-x"));
            Assert.That(resolution.Config.Endpoint, Is.EqualTo("https://work.test/v1"));
            Assert.That(resolution.Config.ChatApi, Is.EqualTo(ChatApiKind.Responses));
            Assert.That(resolution.Config.Model, Is.EqualTo("model-x"));
            Assert.That(resolution.Config.ContextWindowTokens, Is.EqualTo(64_000));
            Assert.That(resolution.Config.ApiKeyCredentialTarget, Is.EqualTo("TinyHarness:work"));
            Assert.That(resolution.Config.MaxAgentSteps, Is.EqualTo(12));
            Assert.That(resolution.Config.ReservedOutputTokens,
                        Is.EqualTo(8_000)); // 未配置该设置 -> 使用内置默认值
            Assert.That(Path.IsPathRooted(resolution.Config.SessionDirectory), Is.True);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task UserConfigWithNoDefaultProfile_KeepsEmptyConfigForDisplay()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tinyharness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var userPath = Path.Combine(dir, "user-config.json");
            await File.WriteAllTextAsync(userPath, """{"profiles":[]}""");

            var resolution = await ConfigResolver.ResolveAsync(null, CancellationToken.None,
                                                               dir,
                                                               userPath);

            Assert.That(resolution.Source, Is.EqualTo(ConfigSourceKind.UserConfig));
            Assert.That(resolution.ProfileName, Is.Null);
            Assert.That(resolution.Config.Endpoint, Is.Empty);
            Assert.That(resolution.Config.Model, Is.Empty);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task UserConfig_DefaultProfileWithoutChatApi_DefaultsToChatCompletions()
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
                                             {"name": "work", "endpoint": "https://work.test/v1"}
                                           ]
                                         }
                                         """);

            var resolution = await ConfigResolver.ResolveAsync(null, CancellationToken.None,
                                                               dir,
                                                               userPath);

            Assert.That(resolution.Config.ChatApi, Is.EqualTo(ChatApiKind.ChatCompletions));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task UserConfigWithMissingDefaultProfile_ThrowsActionableError()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tinyharness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var userPath = Path.Combine(dir, "user-config.json");
            await File.WriteAllTextAsync(userPath, """{"defaultProfile":"ghost","profiles":[]}""");

            var error =
                await TestAssert.ThrowsAsync<ConfigException>(() => ConfigResolver.ResolveAsync(null,
                                                                       CancellationToken.None,
                                                                       dir,
                                                                       userPath));

            Assert.That(error.IsUsageError, Is.False);
            Assert.That(error.Message, Does.Contain("ghost"));
            Assert.That(error.Message, Does.Contain("provider use"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
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
                await TestAssert.ThrowsAsync<ConfigException>(() => ConfigResolver.ResolveAsync(null,
                                                                       CancellationToken.None,
                                                                       dir,
                                                                       userPath));

            Assert.That(error.IsUsageError, Is.False);
            Assert.That(error.Message, Does.Contain("ghost-model"));
            Assert.That(error.Message, Does.Contain("model add"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task NoFilesAtAll_ReturnsDefaultsAndLookupPaths()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tinyharness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var userPath = Path.Combine(dir, "user-config.json");

            var resolution = await ConfigResolver.ResolveAsync(null, CancellationToken.None,
                                                               dir,
                                                               userPath);

            Assert.That(resolution.Source, Is.EqualTo(ConfigSourceKind.Defaults));
            Assert.That(resolution.SourcePath, Is.Null);
            Assert.That(resolution.ProjectConfigExists, Is.False);
            Assert.That(resolution.UserConfigExists, Is.False);
            Assert.That(resolution.Config.ContextWindowTokens, Is.EqualTo(128_000));
            Assert.That(resolution.ProjectConfigPath, Is.EqualTo(Path.Combine(dir, "tinyharness.json")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
