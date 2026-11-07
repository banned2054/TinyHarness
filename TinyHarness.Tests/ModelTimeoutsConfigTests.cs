using System.Text.Json.Nodes;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Mcp;
using TinyHarness.Core.Services.Runtime;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         ModelTimeouts 的校验、用户配置绑定与启动透传离线测试：Validate 边界值、严格绑定
///         （缺省节、宿主默认值预填、未知字段与非法类型拒绝）、保存-加载往返与写侧顺序、
///         McpWorkerHostConfiguration.ResolveAsync 透传。
///     </para>
///     <para>
///         Offline tests for ModelTimeouts validation, user-config binding and startup
///         pass-through: Validate boundaries, strict binding (missing section, host-default
///         backfill, unknown field and wrong type rejection), save-load round trips with write
///         ordering, and ResolveAsync pass-through.
///     </para>
/// </summary>
public class ModelTimeoutsConfigTests
{
    // ---- Validate 单元用例 ---------------------------------------------------------------

    /// <summary>任务书 12：边界值合法（idle/response 允许 0 与上限；approval 下限 1）。</summary>
    [TestCase(0, 0, 1)]
    [TestCase(300, 0, 300)]
    [TestCase(4_294_966, 4_294_966, 4_294_966)]
    public void Validate_AcceptsBoundaryValues(int idle, int response, int approval)
    {
        Assert.DoesNotThrow(() => new ModelTimeouts
        {
            StreamIdleTimeoutSeconds = idle, ResponseTimeoutSeconds = response, ApprovalTimeoutSeconds = approval
        }.Validate());
    }

    /// <summary>任务书 12：越界值按属性名抛 ArgumentOutOfRangeException。</summary>
    [TestCase("StreamIdleTimeoutSeconds", -1, 0, 300)]
    [TestCase("StreamIdleTimeoutSeconds", 4_294_967, 0, 300)]
    [TestCase("ResponseTimeoutSeconds", 300, -1, 300)]
    [TestCase("ResponseTimeoutSeconds", 300, 4_294_967, 300)]
    [TestCase("ApprovalTimeoutSeconds", 300, 0, 0)]
    [TestCase("ApprovalTimeoutSeconds", 300, 0, 4_294_967)]
    public void Validate_RejectsOutOfRangeValues(string paramName, int idle, int response, int approval)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => new ModelTimeouts
        {
            StreamIdleTimeoutSeconds = idle, ResponseTimeoutSeconds = response, ApprovalTimeoutSeconds = approval
        }.Validate());
        Assert.That(exception!.ParamName, Is.EqualTo(paramName));
    }

    // ---- BindModelTimeouts 严格绑定 ------------------------------------------------------

    /// <summary>任务书 12：settings.modelTimeouts 节缺失 → null，沿用内置默认值。</summary>
    [Test]
    public async Task MissingSection_BindsNull()
    {
        var path = WriteConfig("""{"settings":{"maxAgentSteps":7}}""");

        var timeouts = (await UserConfigStore.LoadAsync(path, default)).Settings!.ModelTimeouts;

        Assert.That(timeouts, Is.Null);
    }

    /// <summary>任务书 12：显式全值按 camelCase 解析。</summary>
    [Test]
    public async Task ExplicitSection_BindsAllValues()
    {
        var path = WriteConfig(
            """{"settings":{"modelTimeouts":{"streamIdleTimeoutSeconds":45,"responseTimeoutSeconds":90,"approvalTimeoutSeconds":120}}}""");

        var timeouts = (await UserConfigStore.LoadAsync(path, default)).Settings!.ModelTimeouts;

        Assert.That(timeouts, Is.EqualTo(new ModelTimeouts
        {
            StreamIdleTimeoutSeconds = 45, ResponseTimeoutSeconds = 90, ApprovalTimeoutSeconds = 120
        }));
    }

    /// <summary>任务书 12：部分节缺省的字段由宿主默认值补齐（预填后叠加 supplied 键）。</summary>
    [Test]
    public async Task PartialSection_FillsHostDefaults()
    {
        var path = WriteConfig("""{"settings":{"modelTimeouts":{"streamIdleTimeoutSeconds":60}}}""");

        var timeouts = (await UserConfigStore.LoadAsync(path, default)).Settings!.ModelTimeouts;

        Assert.That(timeouts, Is.EqualTo(new ModelTimeouts
        {
            StreamIdleTimeoutSeconds = 60, ResponseTimeoutSeconds = 0, ApprovalTimeoutSeconds = 300
        }));
    }

    /// <summary>任务书 12：idle/response 显式 0（关闭）合法；approval 0 非法并包装为 InvalidDataException。</summary>
    [Test]
    public async Task ExplicitZeros_IdleAndResponseValid_ApprovalRejected()
    {
        var validPath = WriteConfig(
            """{"settings":{"modelTimeouts":{"streamIdleTimeoutSeconds":0,"responseTimeoutSeconds":0}}}""");

        var timeouts = (await UserConfigStore.LoadAsync(validPath, default)).Settings!.ModelTimeouts;

        Assert.That(timeouts, Is.EqualTo(new ModelTimeouts
        {
            StreamIdleTimeoutSeconds = 0, ResponseTimeoutSeconds = 0, ApprovalTimeoutSeconds = 300
        }));

        var invalidPath = WriteConfig(
            """{"settings":{"modelTimeouts":{"approvalTimeoutSeconds":0}}}""");
        var error = await TestAssert.ThrowsAsync<InvalidDataException>(async () =>
                        await UserConfigStore.LoadAsync(invalidPath, default));
        AssertErrorMessage(error, invalidPath);
    }

    /// <summary>任务书 12：负数、超上限、非法类型、未知字段、非对象节均拒绝，消息指向 settings.modelTimeouts。</summary>
    [TestCase("""{"streamIdleTimeoutSeconds":-1}""")]
    [TestCase("""{"responseTimeoutSeconds":-5}""")]
    [TestCase("""{"approvalTimeoutSeconds":-1}""")]
    [TestCase("""{"streamIdleTimeoutSeconds":4294967}""")]
    [TestCase("""{"responseTimeoutSeconds":4294967}""")]
    [TestCase("""{"approvalTimeoutSeconds":4294967}""")]
    [TestCase("""{"streamIdleTimeoutSeconds":"60"}""")]
    [TestCase("""{"responseTimeoutSeconds":{"seconds":60}}""")]
    [TestCase("""{"typo":1}""")]
    [TestCase("""null""")]
    [TestCase("""[]""")]
    public async Task InvalidSection_IsRejectedWithWrappedMessage(string sectionJson)
    {
        // sectionJson 含连续右花括号，不能用内插原始字符串拼接。
        var path = WriteConfig("{\"settings\":{\"modelTimeouts\":" + sectionJson + "}}");

        var error = await TestAssert.ThrowsAsync<InvalidDataException>(async () =>
                        await UserConfigStore.LoadAsync(path, default));

        AssertErrorMessage(error, path);
    }

    // ---- 保存与加载往返 ------------------------------------------------------------------

    /// <summary>任务书 12：Save→Load 保留 modelTimeouts 节；写侧位置紧跟 runBudget；null 时整节不写出。</summary>
    [Test]
    public async Task SaveAndLoad_RoundTripsModelTimeouts_AfterRunBudget()
    {
        var path = Path.Combine(Path.GetTempPath(), $"model-timeouts-roundtrip-{Guid.NewGuid():N}.json");
        var timeouts = new ModelTimeouts
        {
            StreamIdleTimeoutSeconds = 30, ResponseTimeoutSeconds = 600, ApprovalTimeoutSeconds = 45
        };
        await UserConfigStore.SaveAsync(path,
                                        new UserConfig
                                        {
                                            Settings = new UserConfigSettings
                                            {
                                                RunBudget = new RunBudgetLimits { MaxModelRequests = 3 },
                                                ModelTimeouts = timeouts
                                            }
                                        },
                                        default);
        var saved = await File.ReadAllTextAsync(path);
        var loaded = (await UserConfigStore.LoadAsync(path, default)).Settings!.ModelTimeouts;

        Assert.Multiple(() =>
        {
            Assert.That(loaded, Is.EqualTo(timeouts));
            Assert.That(saved.IndexOf("\"runBudget\"", StringComparison.Ordinal),
                        Is.LessThan(saved.IndexOf("\"modelTimeouts\"", StringComparison.Ordinal)));
        });

        var nullPath = Path.Combine(Path.GetTempPath(), $"model-timeouts-null-{Guid.NewGuid():N}.json");
        await UserConfigStore.SaveAsync(nullPath,
                                        new UserConfig { Settings = new UserConfigSettings { MaxAgentSteps = 2 } },
                                        default);
        var nullSaved = await File.ReadAllTextAsync(nullPath);
        Assert.That(nullSaved.Contains("modelTimeouts", StringComparison.Ordinal), Is.False);
    }

    // ---- 启动透传 -----------------------------------------------------------------------

    /// <summary>任务书 12：ResolveAsync 把 settings.modelTimeouts 透传给 worker 宿主配置。</summary>
    [Test]
    public async Task ResolveAsync_PassesModelTimeoutsToWorkerHost()
    {
        using var dir = new TestTempDir();
        var userConfigPath = await WriteProfileConfig(dir.Root,
            new ModelTimeouts { StreamIdleTimeoutSeconds = 90, ResponseTimeoutSeconds = 0, ApprovalTimeoutSeconds = 60 });

        var resolved = await McpWorkerHostConfiguration.ResolveAsync(CancellationToken.None, userConfigPath,
                                                                     dir.Root, new FakeCredentialStore());

        Assert.That(resolved.ModelTimeouts, Is.EqualTo(new ModelTimeouts
        {
            StreamIdleTimeoutSeconds = 90, ResponseTimeoutSeconds = 0, ApprovalTimeoutSeconds = 60
        }));
    }

    /// <summary>任务书 12：配置无 modelTimeouts 节时 ResolveAsync 沿用内置默认值。</summary>
    [Test]
    public async Task ResolveAsync_WithoutSection_UsesDefaultTimeouts()
    {
        using var dir = new TestTempDir();
        var userConfigPath = await WriteProfileConfig(dir.Root, null);

        var resolved = await McpWorkerHostConfiguration.ResolveAsync(CancellationToken.None, userConfigPath,
                                                                     dir.Root, new FakeCredentialStore());

        Assert.That(resolved.ModelTimeouts, Is.EqualTo(ModelTimeouts.Default));
    }

    // ---- 辅助 ---------------------------------------------------------------------------

    private static string WriteConfig(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"model-timeouts-config-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    /// <summary>写一份最小可用 profile 配置，可选携带 settings.modelTimeouts（camelCase JSON）。</summary>
    private static async Task<string> WriteProfileConfig(string root, ModelTimeouts? timeouts)
    {
        var path = Path.Combine(root, "user-config.json");
        var settings = new JsonObject();
        if (timeouts is not null)
            settings["modelTimeouts"] = new JsonObject
            {
                ["streamIdleTimeoutSeconds"]  = timeouts.StreamIdleTimeoutSeconds,
                ["responseTimeoutSeconds"]    = timeouts.ResponseTimeoutSeconds,
                ["approvalTimeoutSeconds"]    = timeouts.ApprovalTimeoutSeconds
            };
        var config = new JsonObject
        {
            ["defaultProfile"] = "glm",
            ["profiles"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"]  = "glm",
                    ["endpoint"] = "https://glm.example/v1",
                    ["apiKeyCredentialTarget"] = "tinyharness:test-glm",
                    ["chatApi"] = "chat-completions",
                    ["defaultModel"] = "glm-flash",
                    ["models"] = new JsonArray
                    {
                        new JsonObject { ["id"] = "glm-flash", ["contextWindowTokens"] = 32_000 }
                    }
                }
            },
            ["settings"] = settings
        };
        await File.WriteAllTextAsync(path, config.ToJsonString());
        return path;
    }

    private static void AssertErrorMessage(Exception? error, string path)
    {
        Assert.That(error, Is.Not.Null);
        TestAssert.Contains("settings.modelTimeouts", error!.Message, StringComparison.Ordinal);
        TestAssert.Contains(path, error.Message, StringComparison.Ordinal);
    }

    /// <summary>固定返回测试密钥的凭据存储 fake。</summary>
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
