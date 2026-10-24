using TinyHarness.Cli.Services;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;

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

        await selection.HandleAsync(host, "/models list", duringRun : false, TestToken());

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

        await selection.HandleAsync(host, "/models use flash --provider work", duringRun : false, TestToken());

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

        await selection.HandleAsync(host, "/models use big", duringRun : false, TestToken());

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

        await selection.HandleAsync(host, "/models use big --provider alt --effort high", duringRun : false,
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

        await selection.HandleAsync(host, "/models use big --provider work --effort medium", duringRun : false,
                                    TestToken());
        Assert.That(host.Session.ModelSelection!.ReasoningEffort, Is.EqualTo(ModelReasoningEffort.Medium));

        // 换到未声明默认强度的另一目标：不沿用 work/big 的 medium。
        await selection.HandleAsync(host, "/models use big --provider alt", duringRun : false, TestToken());
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

        await selection.HandleAsync(host, "/models", duringRun : false, TestToken());

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
        await selection.HandleAsync(host, "/models", duringRun : false, TestToken());
        Assert.That(output.ToString(), Does.Contain("cancelled"));
        Assert.That(created, Is.Empty);
        Assert.That(host.Session.ModelSelection!.Revision, Is.EqualTo(1));

        var invalidOutput    = new StringWriter();
        var invalidSelection = NewSelection(new StringReader("9\n"), invalidOutput, []);
        await invalidSelection.HandleAsync(host, "/models", duringRun : false, TestToken());
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

        await selection.HandleAsync(host, "/models", duringRun : false, TestToken());

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

        await selection.HandleAsync(host, "/models effort high", duringRun : false, TestToken());
        var raised = host.Session.ModelSelection!;
        Assert.That(raised.ReasoningEffort, Is.EqualTo(ModelReasoningEffort.High));
        Assert.That(raised.Model.ModelId, Is.EqualTo("big"));
        Assert.That(raised.Revision, Is.EqualTo(2));
        Assert.That(created, Is.Empty);

        await selection.HandleAsync(host, "/models effort default", duringRun : false, TestToken());
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
        await selection.HandleAsync(host, "/models use flash --provider work", duringRun : false, TestToken());
        output.GetStringBuilder().Clear();

        await selection.HandleAsync(host, "/models effort low", duringRun : false, TestToken());

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

        await selection.HandleAsync(host, "/models use flash --provider work", duringRun : true, TestToken());
        await selection.HandleAsync(host, "/models effort high", duringRun : true, TestToken());
        await selection.HandleAsync(host, "/models", duringRun : true, TestToken());
        output.GetStringBuilder().Clear();
        await selection.HandleAsync(host, "/models list", duringRun : true, TestToken());

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

        await selection.HandleAsync(host, "/models frobnicate", duringRun : false, TestToken());

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

        await selection.HandleAsync(host, "/models use big --provider work", duringRun : false, TestToken());
        var first = host.Session.ModelSelection!;
        await selection.HandleAsync(host, "/models use big --provider alt", duringRun : false, TestToken());
        var second = host.Session.ModelSelection!;

        // 同一模型三元组、不同凭据路由：开启新 epoch，隔离旧账户产生的 opaque 推理。
        Assert.That(second.Model, Is.EqualTo(first.Model));
        Assert.That(first.CredentialRoute, Is.EqualTo("work|env:WORK_KEY"));
        Assert.That(second.CredentialRoute, Is.EqualTo("alt|credential:alt-account"));
        Assert.That(second.ContinuationEpoch, Is.EqualTo(first.ContinuationEpoch + 1));
        Assert.That(second.MessageBoundary, Is.EqualTo(first.MessageBoundary));
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
}
