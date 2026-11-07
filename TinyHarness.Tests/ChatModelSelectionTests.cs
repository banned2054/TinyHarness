using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using TinyHarness.Cli.Services;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;
using MicrosoftChatMessage = Microsoft.Extensions.AI.ChatMessage;
using TextContent = Microsoft.Extensions.AI.TextContent;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         验证会话内 /models 命令：目录展示、直接与菜单形式的原子应用、未声明强度的本地拒绝、
///         运行中仅查询，以及命令词识别不误伤相似前缀。
///     </para>
///     <para>
///         Verifies the in-session /models command: catalog display, atomic application through both the direct and
///         menu forms, local rejection of undeclared efforts, query-only behavior during runs, and command-word
///         recognition that ignores similar prefixes.
///     </para>
/// </summary>
public class ChatModelSelectionTests
{
    private const string SystemPrompt = "You are a test harness inspecting a workspace.";

    private static CancellationToken TestToken() => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    private static UserConfig Catalog() => new()
    {
        Profiles =
        [
            new UserProfile
            {
                Name     = "work",
                Endpoint = "https://one.example/v1",
                ChatApi  = ChatApiKind.ChatCompletions,
                Models =
                [
                    new UserProfileModel
                    {
                        Id = "big", DisplayName = "Big Model", ContextWindowTokens = 200_000,
                        ReasoningEfforts =
                            [ModelReasoningEffort.Low, ModelReasoningEffort.Medium, ModelReasoningEffort.High],
                        DefaultReasoningEffort = ModelReasoningEffort.Medium
                    },
                    new UserProfileModel { Id = "flash", ContextWindowTokens = 128_000 }
                ]
            },
            new UserProfile
            {
                Name     = "alt",
                Endpoint = "https://two.example/v1",
                ChatApi  = ChatApiKind.Responses,
                Models =
                [
                    new UserProfileModel
                    {
                        Id = "big", ContextWindowTokens = 64_000, ReasoningEfforts = [ModelReasoningEffort.Low]
                    }
                ]
            }
        ]
    };

    private static ChatSessionHost NewHost() =>
        new(Path.Combine(Path.GetTempPath(), "tinyharness-model-selection-tests"),
            new SessionModelIdentity("big", "https://one.example/v1", ChatApiKind.ChatCompletions),
            new AgentOptions { Model = "big", MaxAgentSteps = 8, DefaultToolTimeoutSeconds = 30 },
            new FakeChatClient(),
            new ToolRegistry([]),
            SystemPrompt);

    private static ChatModelSelection NewSelection(StringReader                         input, StringWriter output,
                                                   List<(string Profile, string Model)> created) =>
        new(input, output,
            _ => Task.FromResult(Catalog()),
            (profile, modelId, _) =>
            {
                created.Add((profile.Name, modelId));
                return Task.FromResult<IChatCompletionClient>(new FakeChatClient());
            });

    [Test]
    public async Task DraftUseAndEffortNeverCreateClientsAndListMarksOnlyTheChosenRoute()
    {
        var output    = new StringWriter();
        var created   = new List<(string Profile, string Model)>();
        var selection = NewSelection(new StringReader(string.Empty), output, created);
        await selection.HandleAsync(null, "/models use big --provider work --effort high", false, TestToken());
        await selection.HandleAsync(null, "/models effort low", false, TestToken());
        Assert.That(selection.DraftTarget!.Effort, Is.EqualTo(ModelReasoningEffort.Low));
        Assert.That(created, Is.Empty);
        await selection.HandleAsync(null, "/models list", false, TestToken());
        Assert.That(output.ToString(), Does.Contain("* work / big").And.Not.Contain("* alt / big"));
    }

    [Test]
    public async Task ListDistinguishesCredentialRoutesForTheSameIdentity()
    {
        var first   = Catalog().Profiles[0] with { ApiKeyEnvironmentVariable = "FIRST" };
        var second  = first with { Name = "second", ApiKeyEnvironmentVariable = "SECOND" };
        var catalog = new UserConfig { Profiles = [first, second] };
        var output  = new StringWriter();
        var selection = new ChatModelSelection(new StringReader(""), output, _ => Task.FromResult(catalog),
                                               (_, _, _) =>
                                                   Task.FromResult<IChatCompletionClient>(new FakeChatClient()));
        using var host = NewHost();
        await selection.HandleAsync(host, "/models use big --provider second", false, TestToken());
        await selection.HandleAsync(host, "/models list", false, TestToken());
        Assert.That(output.ToString(), Does.Contain("* second / big").And.Not.Contain("* work / big"));
        await selection.HandleAsync(host, "/models effort low", false, TestToken());
        Assert.That(host.Session.ModelSelection!.ProfileHint, Is.EqualTo("second"));
    }

    [Test]
    public async Task ManagementMenuDoesNotApplyASelectionAndIsNeverOpenedDuringRun()
    {
        var selection = NewSelection(new StringReader("4\n"), new StringWriter(), []);
        var calls     = 0;
        selection.ManageCatalog = _ =>
        {
            calls++;
            return Task.CompletedTask;
        };
        using var host = NewHost();
        await selection.HandleAsync(host, "/models", true, TestToken());
        Assert.That(calls, Is.Zero);
        await selection.HandleAsync(host, "/models", false, TestToken());
        Assert.That(calls, Is.EqualTo(1));
        Assert.That(host.Session.Turns, Is.Empty);
    }

    [Test]
    public async Task StaleEffortSelectionCannotChangeANewerRevision()
    {
        using var host     = NewHost();
        var       revision = host.Session.ModelSelection!.Revision;
        await host.ChangeReasoningEffortAsync(ModelReasoningEffort.High, TestToken());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
                                                                host.ChangeReasoningEffortAsync(ModelReasoningEffort
                                                                       .Low, TestToken(),
                                                                    expectedRevision : revision));
        Assert.That(host.Session.ModelSelection!.ReasoningEffort, Is.EqualTo(ModelReasoningEffort.High));
    }

    [TestCase("/models", true)]
    [TestCase("  /models  ", true)]
    [TestCase("/models list", true)]
    [TestCase("/models use big --provider work", true)]
    [TestCase("/modelsXYZ", false)]
    [TestCase("/model", false)]
    [TestCase("models", false)]
    public void IsModelsCommand_MatchesExactCommandWordOnly(string input, bool expected) =>
        Assert.That(ChatModelSelection.IsModelsCommand(input), Is.EqualTo(expected));

    [Test]
    public async Task List_MarksCurrentModelAndShowsCapabilities()
    {
        using var host      = NewHost();
        var       output    = new StringWriter();
        var       selection = NewSelection(new StringReader(string.Empty), output, []);

        await selection.HandleAsync(host, "/models list", false, TestToken());

        var text = output.ToString();
        Assert.That(text, Does.Contain("* work / big (Big Model)"));
        Assert.That(text, Does.Contain("efforts: low, medium, high"));
        Assert.That(text, Does.Contain("effort: default (service decides)"));
        Assert.That(text, Does.Contain("alt / big"));
    }

    [Test]
    public async Task Use_AppliesTargetAtomicallyWithoutFabricatingTurns()
    {
        using var host      = NewHost();
        var       output    = new StringWriter();
        var       created   = new List<(string Profile, string Model)>();
        var       selection = NewSelection(new StringReader(string.Empty), output, created);

        await selection.HandleAsync(host, "/models use flash --provider work", false, TestToken());

        var current = host.Session.ModelSelection!;
        Assert.That(created, Is.EqualTo([("work", "flash")]));
        Assert.That(current.Model.ModelId, Is.EqualTo("flash"));
        Assert.That(current.Model.Endpoint, Is.EqualTo("https://one.example/v1"));
        Assert.That(current.ReasoningEffort, Is.Null);
        Assert.That(current.Context!.ContextWindowTokens, Is.EqualTo(128_000));
        Assert.That(current.Revision, Is.EqualTo(2));
        Assert.That(host.Session.Turns, Is.Empty);
        Assert.That(output.ToString(), Does.Contain("model      : work / flash"));
    }

    [Test]
    public async Task Use_AmbiguousModelIdRequiresProvider()
    {
        using var host      = NewHost();
        var       output    = new StringWriter();
        var       created   = new List<(string Profile, string Model)>();
        var       selection = NewSelection(new StringReader(string.Empty), output, created);

        await selection.HandleAsync(host, "/models use big", false, TestToken());

        var text = output.ToString();
        Assert.That(text, Does.Contain("multiple profiles"));
        Assert.That(text, Does.Contain("work/big"));
        Assert.That(text, Does.Contain("alt/big"));
        Assert.That(created, Is.Empty);
        Assert.That(host.Session.ModelSelection!.Revision, Is.EqualTo(1));
    }

    [Test]
    public async Task Use_EffortOutsideDeclaredSetIsRejectedLocally()
    {
        using var host      = NewHost();
        var       output    = new StringWriter();
        var       created   = new List<(string Profile, string Model)>();
        var       selection = NewSelection(new StringReader(string.Empty), output, created);

        await selection.HandleAsync(host, "/models use big --provider alt --effort high", false,
                                    TestToken());

        Assert.That(output.ToString(), Does.Contain("does not declare effort 'high'"));
        Assert.That(created, Is.Empty);
        Assert.That(host.Session.ModelSelection!.Revision, Is.EqualTo(1));
    }

    [Test]
    public async Task Use_OmittedEffortUsesTargetDefaultWithoutCarryover()
    {
        using var host      = NewHost();
        var       output    = new StringWriter();
        var       created   = new List<(string Profile, string Model)>();
        var       selection = NewSelection(new StringReader(string.Empty), output, created);

        await selection.HandleAsync(host, "/models use big --provider work --effort medium", false,
                                    TestToken());
        Assert.That(host.Session.ModelSelection!.ReasoningEffort, Is.EqualTo(ModelReasoningEffort.Medium));

        // 换到未声明默认强度的另一目标：不沿用 work/big 的 medium。
        await selection.HandleAsync(host, "/models use big --provider alt", false, TestToken());
        var current = host.Session.ModelSelection!;
        Assert.That(current.Model.Endpoint, Is.EqualTo("https://two.example/v1"));
        Assert.That(current.ReasoningEffort, Is.Null);
        Assert.That(current.Context!.ContextWindowTokens, Is.EqualTo(64_000));
        Assert.That(current.Revision, Is.EqualTo(3));
    }

    [Test]
    public async Task Menu_AppliesModelAndEffortInOneAtomicStep()
    {
        using var host      = NewHost();
        var       output    = new StringWriter();
        var       created   = new List<(string Profile, string Model)>();
        var       selection = NewSelection(new StringReader("1\n4\n"), output, created);

        await selection.HandleAsync(host, "/models", false, TestToken());

        var current = host.Session.ModelSelection!;
        Assert.That(created, Is.EqualTo([("work", "big")]));
        Assert.That(current.Model.ModelId, Is.EqualTo("big"));
        Assert.That(current.ReasoningEffort, Is.EqualTo(ModelReasoningEffort.High));
        Assert.That(current.Revision, Is.EqualTo(2));
    }

    [Test]
    public async Task Menu_CancelOrInvalidChoiceNeverAppliesPartially()
    {
        using var host      = NewHost();
        var       output    = new StringWriter();
        var       created   = new List<(string Profile, string Model)>();
        var       selection = NewSelection(new StringReader("1\n/cancel\n"), output, created);
        await selection.HandleAsync(host, "/models", false, TestToken());
        Assert.That(output.ToString(), Does.Contain("cancelled"));
        Assert.That(created, Is.Empty);
        Assert.That(host.Session.ModelSelection!.Revision, Is.EqualTo(1));

        var invalidOutput    = new StringWriter();
        var invalidSelection = NewSelection(new StringReader("9\n"), invalidOutput, []);
        await invalidSelection.HandleAsync(host, "/models", false, TestToken());
        Assert.That(invalidOutput.ToString(), Does.Contain("Invalid choice"));
        Assert.That(host.Session.ModelSelection!.Revision, Is.EqualTo(1));
    }

    [Test]
    public async Task Menu_UndeclaredEffortsSkipTheSecondStep()
    {
        using var host      = NewHost();
        var       output    = new StringWriter();
        var       created   = new List<(string Profile, string Model)>();
        var       selection = NewSelection(new StringReader("2\n"), output, created);

        await selection.HandleAsync(host, "/models", false, TestToken());

        var current = host.Session.ModelSelection!;
        Assert.That(created, Is.EqualTo([("work", "flash")]));
        Assert.That(output.ToString(), Does.Contain("declares no reasoning efforts"));
        Assert.That(current.Model.ModelId, Is.EqualTo("flash"));
        Assert.That(current.ReasoningEffort, Is.Null);
    }

    [Test]
    public async Task Effort_ChangesOnlyEffortWithoutBuildingAClient()
    {
        using var host      = NewHost();
        var       output    = new StringWriter();
        var       created   = new List<(string Profile, string Model)>();
        var       selection = NewSelection(new StringReader(string.Empty), output, created);

        await selection.HandleAsync(host, "/models effort high", false, TestToken());
        var raised = host.Session.ModelSelection!;
        Assert.That(raised.ReasoningEffort, Is.EqualTo(ModelReasoningEffort.High));
        Assert.That(raised.Model.ModelId, Is.EqualTo("big"));
        Assert.That(raised.Revision, Is.EqualTo(2));
        Assert.That(created, Is.Empty);

        await selection.HandleAsync(host, "/models effort default", false, TestToken());
        var cleared = host.Session.ModelSelection!;
        Assert.That(cleared.ReasoningEffort, Is.Null);
        Assert.That(cleared.Revision, Is.EqualTo(3));
    }

    [Test]
    public async Task Effort_RejectedWhenModelDeclaresNoCapability()
    {
        using var host      = NewHost();
        var       output    = new StringWriter();
        var       created   = new List<(string Profile, string Model)>();
        var       selection = NewSelection(new StringReader(string.Empty), output, created);
        await selection.HandleAsync(host, "/models use flash --provider work", false, TestToken());
        output.GetStringBuilder().Clear();

        await selection.HandleAsync(host, "/models effort low", false, TestToken());

        Assert.That(output.ToString(), Does.Contain("does not declare effort 'low'"));
        Assert.That(host.Session.ModelSelection!.ReasoningEffort, Is.Null);
        Assert.That(host.Session.ModelSelection.Revision, Is.EqualTo(2));
    }

    [Test]
    public async Task DuringRun_OnlyQueriesAreAllowed()
    {
        using var host      = NewHost();
        var       output    = new StringWriter();
        var       created   = new List<(string Profile, string Model)>();
        var       selection = NewSelection(new StringReader(string.Empty), output, created);

        await selection.HandleAsync(host, "/models use flash --provider work", true, TestToken());
        await selection.HandleAsync(host, "/models effort high", true, TestToken());
        await selection.HandleAsync(host, "/models", true, TestToken());
        output.GetStringBuilder().Clear();
        await selection.HandleAsync(host, "/models list", true, TestToken());

        var text = output.ToString();
        Assert.That(text, Does.Contain("* work / big"));
        Assert.That(created, Is.Empty);
        Assert.That(host.Session.ModelSelection!.Revision, Is.EqualTo(1));
        Assert.That(text, Does.Not.Contains("* work / flash"));
    }

    [Test]
    public async Task UnknownSubcommand_ReportsUsageInsteadOfModelText()
    {
        using var host      = NewHost();
        var       output    = new StringWriter();
        var       selection = NewSelection(new StringReader(string.Empty), output, []);

        await selection.HandleAsync(host, "/models frobnicate", false, TestToken());

        var text = output.ToString();
        Assert.That(text, Does.Contain("Usage: /models"));
        Assert.That(text, Does.Contain("not sent to the model"));
        Assert.That(host.Session.ModelSelection!.Revision, Is.EqualTo(1));
    }

    [Test]
    public async Task Use_SameModelIdentityAcrossProfilesOpensNewEpochByRoute()
    {
        // 两个 profile 共用同一模型三元组，只有凭据引用不同：这是路由隔离必须覆盖的场景。
        var catalog = new UserConfig
        {
            Profiles =
            [
                new UserProfile
                {
                    Name                      = "work",
                    Endpoint                  = "https://one.example/v1",
                    ChatApi                   = ChatApiKind.ChatCompletions,
                    ApiKeyEnvironmentVariable = "WORK_KEY",
                    Models                    = [new UserProfileModel { Id = "big", ContextWindowTokens = 200_000 }]
                },
                new UserProfile
                {
                    Name                   = "alt",
                    Endpoint               = "https://one.example/v1",
                    ChatApi                = ChatApiKind.ChatCompletions,
                    ApiKeyCredentialTarget = "alt-account",
                    Models                 = [new UserProfileModel { Id = "big", ContextWindowTokens = 200_000 }]
                }
            ]
        };
        using var host   = NewHost();
        var       output = new StringWriter();
        var selection = new ChatModelSelection(new StringReader(string.Empty), output,
                                               _ => Task.FromResult(catalog),
                                               (_, _, _) =>
                                                   Task.FromResult<IChatCompletionClient>(new FakeChatClient()));

        await selection.HandleAsync(host, "/models use big --provider work", false, TestToken());
        var first = host.Session.ModelSelection!;
        await selection.HandleAsync(host, "/models use big --provider alt", false, TestToken());
        var second = host.Session.ModelSelection!;

        // 同一模型三元组、不同凭据路由：开启新 epoch，隔离旧账户产生的 opaque 推理。
        Assert.That(second.Model, Is.EqualTo(first.Model));
        Assert.That(first.CredentialRoute, Is.EqualTo("work|env:WORK_KEY"));
        Assert.That(second.CredentialRoute, Is.EqualTo("alt|credential:alt-account"));
        Assert.That(second.ContinuationEpoch, Is.EqualTo(first.ContinuationEpoch + 1));
        Assert.That(second.MessageBoundary, Is.EqualTo(first.MessageBoundary));
    }

    [Test]
    public async Task InheritedNetworkRouteRefreshesBeforeNextRequestAndKeepsContinuity()
    {
        var catalog = Catalog();
        var clients = new List<FakeChatClient>();
        var output  = new StringWriter();
        var selection = new ChatModelSelection(new StringReader(string.Empty), output,
                                               _ => Task.FromResult(catalog),
                                               (_, _, _) =>
                                               {
                                                   var client = new FakeChatClient();
                                                   clients.Add(client);
                                                   return Task.FromResult<IChatCompletionClient>(client);
                                               });
        using var host = NewHost();

        await selection.HandleAsync(host, "/models use big --provider work", false, TestToken());
        await selection.ValidateBeforeSendAsync(host, TestToken());
        var systemSelection = host.Session.ModelSelection!;
        Assert.That(systemSelection.NetworkRouteIdentity, Is.EqualTo("system"));

        var proxy = new ProxyDefinition
        {
            Id       = "local",
            Name     = "本地代理",
            Protocol = ProxyProtocol.Http,
            Host     = "127.0.0.1",
            Port     = 8080
        };
        catalog = catalog with
        {
            Settings = new UserConfigSettings
            {
                Network = new NetworkSettings
                {
                    DefaultRoute = new NetworkRoute { Mode = NetworkMode.Proxy, ProxyId = proxy.Id },
                    Proxies      = [proxy]
                }
            }
        };

        await selection.ValidateBeforeSendAsync(host, TestToken());

        var expectedIdentity = NetworkRouteResolver.BindingIdentity(
                                                                    NetworkRouteResolver.Resolve(catalog.Settings
                                                                       .Network, catalog.Profiles[0].Network));
        var proxySelection = host.Session.ModelSelection!;
        Assert.That(proxySelection.NetworkRouteIdentity, Is.EqualTo(expectedIdentity));
        Assert.That(proxySelection.Revision, Is.EqualTo(systemSelection.Revision + 1));
        Assert.That(proxySelection.ContinuationEpoch, Is.EqualTo(systemSelection.ContinuationEpoch));
        Assert.That(clients, Has.Count.EqualTo(3));
        Assert.That(output.ToString(), Does.Contain("network    : applied 代理 本地代理"));
    }

    /// <summary>
    ///     <para>
    ///         绑定标签 = 客户端实际策略的回归：工厂闭包在目录加载后、应用前读到漂移后的"当前"
    ///         策略并构造带新策略的真实 MicrosoftAiChatClient，宿主必须替换客户端（同模型也不
    ///         再按 no-op 复用），双向（默认 ↔ 30s）生效；新实例实际接管后续请求，旧实例恰好
    ///         释放一次；策略未漂移时同模型重选保持 no-op，冗余候选被宿主释放一次。
    ///     </para>
    ///     <para>
    ///         Regression for "binding tag = the client's actual policy": the factory closure reads the
    ///         drifted "current" policy after the catalog loads but before the apply and builds a real
    ///         MicrosoftAiChatClient carrying it, so the host must replace the client (never a same-model
    ///         no-op reuse) in both directions (default ↔ 30s); the new instance actually takes over
    ///         later requests and the old one is released exactly once; with no drift a same-model
    ///         reselection stays a no-op and the redundant candidate is released once by the host.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ReselectingSameModelAdoptsRebuiltClientWhenItsTimeoutPolicyDrifts()
    {
        var catalog = Catalog();
        var built   = new List<BuiltClient>();
        var output  = new StringWriter();
        // 工厂读"当前"策略；漂移在目录加载后、客户端构造前生效，模拟配置文件在菜单等待期间的同步点。
        var currentPolicy  = ModelTimeouts.Default;
        var pendingDrifts  = new Queue<ModelTimeouts>();
        var selection = new ChatModelSelection(new StringReader(string.Empty), output,
                                               _ =>
                                               {
                                                   if (pendingDrifts.Count > 0) currentPolicy = pendingDrifts.Dequeue();
                                                   return Task.FromResult(catalog);
                                               },
                                               (_, _, _) =>
                                               {
                                                   var client = NewPolicyClient(currentPolicy);
                                                   built.Add(client);
                                                   return Task.FromResult<IChatCompletionClient>(client.Wrapper);
                                               });
        using var host = NewHost();

        await selection.HandleAsync(host, "/models use big --provider work", false, TestToken());
        var initial = host.Session.ModelSelection!;
        Assert.Multiple(() =>
        {
            Assert.That(built, Has.Count.EqualTo(1));
            Assert.That(host.EffectiveModelTimeouts, Is.EqualTo(ModelTimeouts.Default));
            Assert.That(initial.Revision, Is.EqualTo(2));
        });

        // 漂移方向一（默认 → 30s）：同模型重选必须采纳携带新策略的重建客户端，而不是按 no-op 丢弃。
        pendingDrifts.Enqueue(new ModelTimeouts { ResponseTimeoutSeconds = 30 });
        await selection.HandleAsync(host, "/models use big --provider work", false, TestToken());
        var widened = host.Session.ModelSelection!;
        Assert.Multiple(() =>
        {
            Assert.That(built, Has.Count.EqualTo(2));
            Assert.That(widened.Revision, Is.EqualTo(initial.Revision + 1));
            Assert.That(widened.ContinuationEpoch, Is.EqualTo(initial.ContinuationEpoch));
            Assert.That(widened.MessageBoundary, Is.EqualTo(initial.MessageBoundary));
            Assert.That(host.EffectiveModelTimeouts, Is.EqualTo(new ModelTimeouts { ResponseTimeoutSeconds = 30 }));
            Assert.That(host.EffectiveModelTimeouts, Is.EqualTo(built[1].Wrapper.EffectiveTimeouts));
            Assert.That(built[0].Probe.Count, Is.EqualTo(1), "策略漂移后旧客户端应随绑定替换恰好释放一次");
            Assert.That(built[1].Probe.Count, Is.EqualTo(0));
        });

        // 新实例在绑定中：宿主发送的真实请求落在重建客户端上，被替换实例不再收到请求。
        await host.SubmitTurnAsync("probe after widening", TestToken());
        Assert.Multiple(() =>
        {
            Assert.That(built[1].Inner.Requests, Is.EqualTo(1));
            Assert.That(built[0].Inner.Requests, Is.Zero);
        });

        // 漂移方向二（30s → 默认）：旧期限不得继续生效，同样必须替换。
        pendingDrifts.Enqueue(ModelTimeouts.Default);
        await selection.HandleAsync(host, "/models use big --provider work", false, TestToken());
        var reverted = host.Session.ModelSelection!;
        Assert.Multiple(() =>
        {
            Assert.That(built, Has.Count.EqualTo(3));
            Assert.That(reverted.Revision, Is.EqualTo(widened.Revision + 1));
            Assert.That(host.EffectiveModelTimeouts, Is.EqualTo(ModelTimeouts.Default));
            Assert.That(built[1].Probe.Count, Is.EqualTo(1), "反向漂移后上一实例也应恰好释放一次");
            Assert.That(built[2].Probe.Count, Is.EqualTo(0));
        });

        await host.SubmitTurnAsync("probe after reverting", TestToken());
        Assert.That(built[2].Inner.Requests, Is.EqualTo(1));

        // 策略未漂移：同模型重选保持 no-op 复用——CLI 仍构造候选客户端，宿主释放冗余实例一次，
        // 不产生修订，后续请求仍落在在用实例上。
        await selection.HandleAsync(host, "/models use big --provider work", false, TestToken());
        await host.SubmitTurnAsync("probe after noop", TestToken());
        Assert.Multiple(() =>
        {
            Assert.That(built, Has.Count.EqualTo(4));
            Assert.That(host.Session.ModelSelection!.Revision, Is.EqualTo(reverted.Revision));
            Assert.That(host.EffectiveModelTimeouts, Is.EqualTo(ModelTimeouts.Default));
            Assert.That(built[3].Probe.Count, Is.EqualTo(1), "no-op 候选的冗余客户端由宿主释放一次");
            Assert.That(built[3].Inner.Requests, Is.Zero);
            Assert.That(built[2].Inner.Requests, Is.EqualTo(2), "在用客户端应继续承载请求");
        });
    }

    [Test]
    public void CredentialRoute_DescribeChangesOnlyWithProfileOrCredentialReferences()
    {
        var route = CredentialRoute.Describe("work", "WORK_KEY", null);
        Assert.That(route, Is.EqualTo("work|env:WORK_KEY"));
        Assert.That(CredentialRoute.Describe("work", "WORK_KEY", null), Is.EqualTo(route));
        Assert.That(CredentialRoute.Describe("alt", "WORK_KEY", null), Is.Not.EqualTo(route));
        Assert.That(CredentialRoute.Describe("work", "OTHER_KEY", null), Is.Not.EqualTo(route));
        Assert.That(CredentialRoute.Describe("work", null, "alt-account"), Is.Not.EqualTo(route));
        Assert.That(CredentialRoute.Describe("work", null, null), Is.EqualTo("work|unconfigured"));
        Assert.That(CredentialRoute.Describe(null, "WORK_KEY", null), Is.EqualTo("|env:WORK_KEY"));
    }

    [Test]
    public async Task ConsoleIdlePath_RoutesModelsCommandWithoutCreatingTurns()
    {
        using var host         = NewHost();
        var       output       = new StringWriter();
        var       created      = new List<(string Profile, string Model)>();
        var       selection    = NewSelection(new StringReader(string.Empty), output, created);
        using var cancellation = new ChatCancellation(false);
        using var console = new ChatSessionConsole(host,
                                                   new StringReader("/models use flash --provider work\n"),
                                                   output, cancellation);
        console.ModelSelection = selection;

        Assert.That(await console.RunAsync(), Is.Zero);
        Assert.That(created, Is.EqualTo([("work", "flash")]));
        Assert.That(host.Session.ModelSelection!.Model.ModelId, Is.EqualTo("flash"));
        Assert.That(host.Session.Turns, Is.Empty);
    }

    // ---- 真实客户端载体（绑定标签 = EffectiveTimeouts 契约） -----------------------------

    /// <summary>记录一次工厂构造：包装客户端、脚本化底层与释放探针，供替换与 no-op 断言使用。</summary>
    private sealed record BuiltClient(MicrosoftAiChatClient Wrapper, ScriptedSdkClient Inner, DisposeProbe Probe);

    /// <summary>经 internal 接缝以显式策略构造真实 MicrosoftAiChatClient，ownedResource 注入释放探针。</summary>
    private static BuiltClient NewPolicyClient(ModelTimeouts timeouts)
    {
        var inner = new ScriptedSdkClient();
        var probe = new DisposeProbe();
        return new BuiltClient(new MicrosoftAiChatClient(inner, ChatApiKind.ChatCompletions, probe, null,
                                                         timeouts, null), inner, probe);
    }

    /// <summary>释放探针：计数 ownedResource 的释放次数，验证"恰好释放一次"。</summary>
    private sealed class DisposeProbe : IDisposable
    {
        public int Count { get; private set; }

        public void Dispose() => Count++;
    }

    /// <summary>脚本化 M.E.AI 底层客户端：每次流请求立即产出一段文本与 stop 收尾，并统计请求数。</summary>
    private sealed class ScriptedSdkClient : IChatClient
    {
        public int Requests { get; private set; }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<MicrosoftChatMessage> messages, ChatOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests++;
            await Task.Yield();
            yield return new ChatResponseUpdate(null, new List<AIContent> { new TextContent("scripted answer") });
            yield return new ChatResponseUpdate(null, new List<AIContent>())
            {
                FinishReason = ChatFinishReason.Stop
            };
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<MicrosoftChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken) =>
            throw new NotSupportedException("This fake only supports streaming.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
