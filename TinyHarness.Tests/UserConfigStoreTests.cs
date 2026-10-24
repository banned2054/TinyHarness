using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.ChatCompletions.Configuration;

namespace TinyHarness.Tests;

public class UserConfigStoreTests
{
    [Test]
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
                        ChatApi                   = ChatApiKind.Responses,
                        DefaultModel              = "model-x",
                        Models =
                        [
                            new UserProfileModel { Id = "model-x", ContextWindowTokens = 128_000 },
                            new UserProfileModel { Id = "model-y", ContextWindowTokens = 200_000 }
                        ]
                    }
                ],
                Settings = new UserConfigSettings
                {
                    MaxAgentSteps             = 60,
                    ReservedOutputTokens      = 4_000,
                    CompactionThreshold       = 100_000,
                    DefaultToolTimeoutSeconds = 90,
                    SessionDirectory          = "custom/runs",
                    Worker = new UserWorkerSettings
                    {
                        WorkspaceRoot              = "C:/work/project",
                        RunTimeoutSeconds          = 180,
                        MaxAgentSteps              = 8,
                        DefaultToolTimeoutSeconds  = 25,
                        MaxTaskPackageCharacters   = 10_000,
                        MaxToolCalls               = 15,
                        MaxToolOutputCharacters    = 30_000,
                        MaxContextTokensPerRequest = 20_000,
                        MaxCumulativeContextTokens = 50_000,
                        MaxModelResponseCharacters = 16_000
                    }
                }
            };

            await UserConfigStore.SaveAsync(path, config, CancellationToken.None);
            var loaded = await UserConfigStore.LoadAsync(path, CancellationToken.None);

            Assert.That(loaded.DefaultProfile, Is.EqualTo("work"));
            var profile = TestAssert.Single(loaded.Profiles);
            Assert.That(profile.Name, Is.EqualTo("work"));
            Assert.That(profile.Endpoint, Is.EqualTo("https://api.example.com/v1"));
            Assert.That(profile.ApiKeyEnvironmentVariable, Is.EqualTo("WORK_API_KEY"));
            Assert.That(profile.ApiKeyCredentialTarget, Is.EqualTo("TinyHarness:work"));
            Assert.That(profile.ChatApi, Is.EqualTo(ChatApiKind.Responses));
            Assert.That(profile.DefaultModel, Is.EqualTo("model-x"));
            Assert.That(profile.Models.Count, Is.EqualTo(2));
            Assert.That(profile.Models[0].ContextWindowTokens, Is.EqualTo(128_000));
            Assert.That(profile.Models[1].ContextWindowTokens, Is.EqualTo(200_000));
            TestAssert.NotNull(loaded.Settings);
            Assert.That(loaded.Settings!.MaxAgentSteps, Is.EqualTo(60));
            Assert.That(loaded.Settings.ReservedOutputTokens, Is.EqualTo(4_000));
            Assert.That(loaded.Settings.CompactionThreshold, Is.EqualTo(100_000));
            Assert.That(loaded.Settings.DefaultToolTimeoutSeconds, Is.EqualTo(90));
            Assert.That(loaded.Settings.SessionDirectory, Is.EqualTo("custom/runs"));
            var worker = loaded.Settings.Worker!;
            Assert.That(worker.WorkspaceRoot, Is.EqualTo("C:/work/project"));
            Assert.That(worker.RunTimeoutSeconds, Is.EqualTo(180));
            Assert.That(worker.MaxAgentSteps, Is.EqualTo(8));
            Assert.That(worker.DefaultToolTimeoutSeconds, Is.EqualTo(25));
            Assert.That(worker.MaxTaskPackageCharacters, Is.EqualTo(10_000));
            Assert.That(worker.MaxToolCalls, Is.EqualTo(15));
            Assert.That(worker.MaxToolOutputCharacters, Is.EqualTo(30_000));
            Assert.That(worker.MaxContextTokensPerRequest, Is.EqualTo(20_000));
            Assert.That(worker.MaxCumulativeContextTokens, Is.EqualTo(50_000));
            Assert.That(worker.MaxModelResponseCharacters, Is.EqualTo(16_000));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task LoadAsync_MissingFile_ReturnsEmptyConfig()
    {
        var config = await UserConfigStore.LoadAsync("does-not-exist.json", CancellationToken.None);

        Assert.That(config.DefaultProfile, Is.Null);
        Assert.That(config.Profiles, Is.Empty);
        Assert.That(config.Settings, Is.Null);
    }

    [Test]
    public async Task LoadAsync_LegacyProfileWithoutChatApi_DefaultsToChatCompletions()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tinyharness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "user-config.json");
        try
        {
            await File.WriteAllTextAsync(path,
                                         """{"defaultProfile":"work","profiles":[{"name":"work","endpoint":"https://work.test/v1"}]}""");

            var loaded = await UserConfigStore.LoadAsync(path, CancellationToken.None);

            var profile = TestAssert.Single(loaded.Profiles);
            Assert.That(profile.ChatApi, Is.EqualTo(ChatApiKind.ChatCompletions));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task LoadAsync_NonObjectRoot_ThrowsWithPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tinyharness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "user-config.json");
        try
        {
            await File.WriteAllTextAsync(path, "[1, 2]");

            var error =
                await TestAssert.ThrowsAsync<InvalidDataException>(() =>
                                                                       UserConfigStore
                                                                          .LoadAsync(path, CancellationToken.None));

            Assert.That(error.Message, Does.Contain("user-config.json"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }


    [TestCase("""{"profiles":[{"name":"a"},{"name":"a"}]}""", "duplicates profile name 'a'")]
    [TestCase("""{"profiles":[{"endpoint":"https://x"}]}""", "'profiles[0].name'")]
    [TestCase("""{"profiles":[{"name":"a","models":[{"contextWindowTokens":100}]}]}""", "'profiles[0].models[0].id'")]
    [TestCase("""{"profiles":[{"name":"a","models":[{"id":"m","contextWindowTokens":0}]}]}""", "positive integer")]
    [TestCase("""{"profiles":[{"name":"a","chatApi":"grpc"}]}""", "'profiles[0].chatApi'")]
    [TestCase("""{"profiles":[{"name":"a","chatApi":"grpc"}]}""", "invalid value 'grpc'")]
    [TestCase("""{"profiles":[{"name":"a","chatApi":5}]}""", "must be a string")]
    [TestCase("""{"defaultProfile":42}""", "'defaultProfile'")]
    [TestCase("""{"settings":{"maxAgentSteps":-1}}""", "non-negative integer")]
    [TestCase("""{"settings":{"compactionThreshold":-5}}""", "non-negative integer")]
    [TestCase("""{"profiles":"nope"}""", "must be an array")]
    public async Task LoadAsync_MalformedValues_ThrowActionableDiagnostics(string json, string expectedFragment)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tinyharness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "user-config.json");
        try
        {
            await File.WriteAllTextAsync(path, json);

            var error =
                await TestAssert.ThrowsAsync<InvalidDataException>(() =>
                                                                       UserConfigStore
                                                                          .LoadAsync(path, CancellationToken.None));

            Assert.That(error.Message, Does.Contain(expectedFragment));
            Assert.That(error.Message, Does.Contain(path));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
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
                            Mode = "direct", Executable = "dotnet", Arguments = ["test"], WorkingDirectory = "."
                        }
                    ]
                }
            };

            await UserConfigStore.SaveAsync(path, config, CancellationToken.None);
            var loaded = await UserConfigStore.LoadAsync(path, CancellationToken.None);

            var rule = TestAssert.Single(loaded.Settings!.CommandRules!);
            Assert.That(rule.Executable, Is.EqualTo("dotnet"));
            Assert.That(rule.Arguments, Is.EqualTo(["test"]));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task SaveThenLoad_RoundTripsTheSessionStoreDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tinyharness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "user-config.json");
        try
        {
            var store = Path.Combine(dir, "sessions");
            await UserConfigStore.SaveAsync(path,
                                            new UserConfig
                                            {
                                                Settings = new UserConfigSettings { SessionStoreDirectory = store }
                                            },
                                            CancellationToken.None);

            var loaded = await UserConfigStore.LoadAsync(path, CancellationToken.None);

            Assert.That(loaded.Settings?.SessionStoreDirectory, Is.EqualTo(store));

            // 缺省字段往返为 null：应用默认受保护目录，而不是持久化空值。
            await UserConfigStore.SaveAsync(path, new UserConfig { Settings = new UserConfigSettings() },
                                            CancellationToken.None);
            var reloaded = await UserConfigStore.LoadAsync(path, CancellationToken.None);
            Assert.That(reloaded.Settings?.SessionStoreDirectory, Is.Null);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task SaveThenLoad_RoundTripsModelCatalogCapabilities()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tinyharness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "user-config.json");
        try
        {
            var config = new UserConfig
            {
                Profiles =
                [
                    new UserProfile
                    {
                        Name     = "work",
                        Endpoint = "https://api.example.com/v1",
                        Models =
                        [
                            new UserProfileModel
                            {
                                Id = "glm-5.3", DisplayName = "GLM-5.3", ContextWindowTokens = 200_000,
                                ReasoningEfforts =
                                    [ModelReasoningEffort.Low, ModelReasoningEffort.Medium, ModelReasoningEffort.High],
                                DefaultReasoningEffort = ModelReasoningEffort.Medium
                            },
                            new UserProfileModel { Id = "glm-5.3-flash", ContextWindowTokens = 128_000 }
                        ]
                    }
                ]
            };

            await UserConfigStore.SaveAsync(path, config, CancellationToken.None);
            var loaded = await UserConfigStore.LoadAsync(path, CancellationToken.None);

            var profile = TestAssert.Single(loaded.Profiles);
            Assert.That(profile.Models.Count, Is.EqualTo(2));
            var full = profile.Models[0];
            Assert.That(full.DisplayName, Is.EqualTo("GLM-5.3"));
            Assert.That(full.ReasoningEfforts,
                        Is.EqualTo(new[]
                        {
                            ModelReasoningEffort.Low, ModelReasoningEffort.Medium, ModelReasoningEffort.High
                        }));
            Assert.That(full.DefaultReasoningEffort, Is.EqualTo(ModelReasoningEffort.Medium));
            var bare = profile.Models[1];
            Assert.That(bare.DisplayName, Is.Null);
            Assert.That(bare.ReasoningEfforts, Is.Null);
            Assert.That(bare.DefaultReasoningEffort, Is.Null);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task LoadAsync_AcceptsOffSynonymAndCaseInsensitiveEfforts()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tinyharness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "user-config.json");
        try
        {
            await File.WriteAllTextAsync(path,
                                         """{"profiles":[{"name":"a","models":[{"id":"m","contextWindowTokens":1,"reasoningEfforts":["off","HIGH"],"defaultReasoningEffort":"high"}]}]}""");

            var loaded = await UserConfigStore.LoadAsync(path, CancellationToken.None);

            var model = TestAssert.Single(TestAssert.Single(loaded.Profiles).Models);
            Assert.That(model.ReasoningEfforts,
                        Is.EqualTo(new[] { ModelReasoningEffort.None, ModelReasoningEffort.High }));
            Assert.That(model.DefaultReasoningEffort, Is.EqualTo(ModelReasoningEffort.High));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [TestCase("""{"profiles":[{"name":"a","endpoint":"ftp://x","models":[{"id":"m","contextWindowTokens":1}]}]}""",
              "absolute http(s)")]
    [TestCase("""{"profiles":[{"name":"a","models":[{"id":"m","contextWindowTokens":1,"reasoningEfforts":"high"}]}]}""",
              "must be an array")]
    [TestCase("""{"profiles":[{"name":"a","models":[{"id":"m","contextWindowTokens":1,"reasoningEfforts":["turbo"]}]}]}""",
              "expected one of: none, low, medium, high, extra-high")]
    [TestCase("""{"profiles":[{"name":"a","models":[{"id":"m","contextWindowTokens":1,"reasoningEfforts":["low","LOW"]}]}]}""",
              "duplicates effort 'low'")]
    [TestCase("""{"profiles":[{"name":"a","models":[{"id":"m","contextWindowTokens":1,"defaultReasoningEffort":"turbo"}]}]}""",
              "invalid value 'turbo'")]
    [TestCase("""{"profiles":[{"name":"a","models":[{"id":"m","contextWindowTokens":1,"reasoningEfforts":["low"],"defaultReasoningEffort":"high"}]}]}""",
              "must be one of the model's declared reasoningEfforts")]
    [TestCase("""{"profiles":[{"name":"a","models":[{"id":"m","contextWindowTokens":1,"defaultReasoningEffort":"high"}]}]}""",
              "must be one of the model's declared reasoningEfforts")]
    public async Task LoadAsync_MalformedCatalogCapabilities_ThrowActionableDiagnostics(
        string json, string expectedFragment)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tinyharness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "user-config.json");
        try
        {
            await File.WriteAllTextAsync(path, json);

            var error =
                await TestAssert.ThrowsAsync<InvalidDataException>(() =>
                                                                       UserConfigStore
                                                                          .LoadAsync(path, CancellationToken.None));

            Assert.That(error.Message, Does.Contain(expectedFragment));
            Assert.That(error.Message, Does.Contain(path));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task SaveAsync_InvalidCandidate_IsRejectedWithoutTouchingDisk()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tinyharness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "user-config.json");
        try
        {
            var config = new UserConfig
            {
                Profiles =
                [
                    new UserProfile
                    {
                        Name = "a",
                        Models =
                        [
                            new UserProfileModel
                            {
                                Id = "m", ContextWindowTokens = 1, ReasoningEfforts = [ModelReasoningEffort.Low],
                                DefaultReasoningEffort = ModelReasoningEffort.High
                            }
                        ]
                    }
                ]
            };

            var error =
                await TestAssert.ThrowsAsync<InvalidDataException>(
                    () => UserConfigStore.SaveAsync(path, config, CancellationToken.None));

            Assert.That(error.Message, Does.Contain("defaultReasoningEffort"));
            Assert.That(File.Exists(path), Is.False);
            Assert.That(Directory.GetFiles(dir), Is.Empty);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task SaveAsync_StaleBaseline_RefusesAndKeepsNewerDiskContent()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tinyharness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "user-config.json");
        try
        {
            var baseline = ConfigWithModel("old");
            await UserConfigStore.SaveAsync(path, baseline, CancellationToken.None);
            await UserConfigStore.SaveAsync(path, ConfigWithModel("new"), CancellationToken.None);

            var error =
                await TestAssert.ThrowsAsync<InvalidDataException>(() =>
                                                                       UserConfigStore.SaveAsync(path,
                                                                           baseline with { DefaultProfile = "a" },
                                                                           baseline, CancellationToken.None));

            Assert.That(error.Message, Does.Contain("changed while it was being edited"));
            var loaded = await UserConfigStore.LoadAsync(path, CancellationToken.None);
            Assert.That(TestAssert.Single(TestAssert.Single(loaded.Profiles).Models).Id, Is.EqualTo("new"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task SaveAsync_MatchingBaseline_CommitsTheEdit()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tinyharness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "user-config.json");
        try
        {
            var baseline = ConfigWithModel("old");
            await UserConfigStore.SaveAsync(path, baseline, CancellationToken.None);

            // 非覆盖式追加：编辑后的候选保留原有模型并新增一项。
            var profile = TestAssert.Single(baseline.Profiles);
            var edited = baseline with
            {
                Profiles =
                [
                    profile with
                    {
                        Models =
                        [
                            .. profile.Models, new UserProfileModel { Id = "added", ContextWindowTokens = 2 }
                        ]
                    }
                ]
            };
            await UserConfigStore.SaveAsync(path, edited, baseline, CancellationToken.None);

            var loaded = await UserConfigStore.LoadAsync(path, CancellationToken.None);
            var models = TestAssert.Single(loaded.Profiles).Models;
            Assert.That(models.Select(model => model.Id).ToArray(), Is.EqualTo(["old", "added"]));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task SaveAsync_ConcurrentBaselineSavesCannotBothSucceed()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tinyharness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "user-config.json");
        try
        {
            var baseline = ConfigWithModel("old");
            await UserConfigStore.SaveAsync(path, baseline, CancellationToken.None);

            async Task<Exception?> TrySaveAsync(UserConfig candidate)
            {
                try
                {
                    await UserConfigStore.SaveAsync(path, candidate, baseline, CancellationToken.None);
                    return null;
                }
                catch (Exception ex)
                {
                    return ex;
                }
            }

            // 同基线并发保存：锁内串行，后到者复查基线时已见前者写入，必须明确失败而非静默覆盖。
            var errors = await Task.WhenAll(TrySaveAsync(ConfigWithModel("one")), TrySaveAsync(ConfigWithModel("two")));

            Assert.That(errors.Count(error => error is null), Is.EqualTo(1));
            var conflict = errors.Single(error => error is not null)!;
            Assert.That(conflict, Is.InstanceOf<InvalidDataException>());
            Assert.That(conflict.Message, Does.Contain("changed while it was being edited"));
            var loaded = await UserConfigStore.LoadAsync(path, CancellationToken.None);
            Assert.That(TestAssert.Single(TestAssert.Single(loaded.Profiles).Models).Id,
                        Is.EqualTo(errors[0] is null ? "one" : "two"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task SaveAsync_CancellationWhileWaitingForTheSaveLockStopsWaiting()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tinyharness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "user-config.json");
        try
        {
            // 预占保存锁并取消等待令牌：等待必须响应取消，而不是等满锁超时。
            await using var held = new FileStream(path + UserConfigStore.SaveLockSuffix, FileMode.OpenOrCreate,
                                                  FileAccess.ReadWrite, FileShare.None);
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

            try
            {
                await UserConfigStore.SaveAsync(path, ConfigWithModel("m"), cts.Token);
                Assert.Fail("Waiting for the save lock should honor cancellation.");
            }
            catch (OperationCanceledException)
            {
                // TaskCanceledException 也属于 OperationCanceledException，两种取消路径都接受。
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task SaveAsync_LeavesNoTemporaryFilesBehind()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tinyharness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "user-config.json");
        try
        {
            var config = ConfigWithModel("m");
            await UserConfigStore.SaveAsync(path, config, CancellationToken.None);
            await UserConfigStore.SaveAsync(path, config, config, CancellationToken.None);

            // 保存锁文件是持久的协调产物（与会话存储一致）；不得残留临时写入文件。
            Assert.That(Directory.GetFiles(dir),
                        Is.EquivalentTo([path, path + UserConfigStore.SaveLockSuffix]));
            var loaded = await UserConfigStore.LoadAsync(path, CancellationToken.None);
            Assert.That(TestAssert.Single(TestAssert.Single(loaded.Profiles).Models).Id, Is.EqualTo("m"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    ///     构造只有一个供应商、一个模型的最小合法配置。
    ///     Builds a minimal valid config with a single provider holding one model.
    /// </summary>
    private static UserConfig ConfigWithModel(string modelId) => new()
    {
        Profiles =
        [
            new UserProfile
            {
                Name     = "a",
                Endpoint = "https://api.example.com/v1",
                Models   = [new UserProfileModel { Id = modelId, ContextWindowTokens = 1 }]
            }
        ]
    };
}
