using System.Text.Json;
using System.Text.Json.Nodes;
using TinyHarness.Cli.Services;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

public class CliApprovalRoutingTests
{
    [Test]
    public async Task HumanMode_UsesHumanWithoutCreatingReviewer()
    {
        var human                = new FakeApprovalProvider(ApprovalResolution.Granted(GrantScope.SingleOperation));
        var engine               = Engine(SessionApprovalMode.HumanApproval);
        var reviewerFactoryCalls = 0;
        var routed = CliApprovalRouting.Create(human, () => Host(engine), engine, _ =>
        {
            reviewerFactoryCalls++;
            throw new AssertionException("Reviewer factory must not be called in human mode.");
        });

        var resolution = await routed.PromptAsync(Request(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(resolution.Kind, Is.EqualTo(ApprovalResolutionKind.Granted));
            Assert.That(human.Calls, Is.EqualTo(1));
            Assert.That(reviewerFactoryCalls, Is.Zero);
        });
    }

    [Test]
    public async Task AiAllow_GrantsSingleOperationWithoutCallingHuman()
    {
        var human        = new FakeApprovalProvider(ApprovalResolution.Denied("unexpected"));
        var client       = ReviewerClient("allow");
        var engine       = Engine(SessionApprovalMode.AIAutoReview);
        var conversation = new ConversationContext();
        conversation.Append(ChatMessage.System("system"));
        conversation.Append(ChatMessage.Assistant("assistant payload must stay excluded"));
        conversation.Append(ChatMessage.Tool("probe", "tool-call", "tool payload must stay excluded"));
        using var host   = Host(engine, conversation);
        var       routed = CreateWithClient(human, engine, client, host);

        var       resolution      = await routed.PromptAsync(Request(), CancellationToken.None);
        using var payloadDocument = JsonDocument.Parse(client.RequestLog[0].Messages[^1].Content);
        var       payload         = payloadDocument.RootElement.GetRawText();

        Assert.Multiple(() =>
        {
            Assert.That(resolution, Is.EqualTo(ApprovalResolution.Granted(GrantScope.SingleOperation)));
            Assert.That(human.Calls, Is.Zero);
            Assert.That(client.Requests, Is.EqualTo(1));
            Assert.That(payload, Does.Not.Contain("assistant payload"));
            Assert.That(payload, Does.Not.Contain("tool payload"));
        });
    }

    [Test]
    public async Task AiDeny_DoesNotCallHuman()
    {
        var       human  = new FakeApprovalProvider(ApprovalResolution.Granted(GrantScope.SingleOperation));
        var       engine = Engine(SessionApprovalMode.AIAutoReview);
        using var host   = Host(engine);
        var       routed = CreateWithClient(human, engine, ReviewerClient("deny"), host);

        var resolution = await routed.PromptAsync(Request(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(resolution.Kind, Is.EqualTo(ApprovalResolutionKind.Denied));
            Assert.That(human.Calls, Is.Zero);
        });
    }

    [Test]
    public async Task AiEscalation_PassesSameFrozenPreparationAndAllowedScopesToHuman()
    {
        var       human   = new FakeApprovalProvider(ApprovalResolution.Granted(GrantScope.Turn));
        var       engine  = Engine(SessionApprovalMode.AIAutoReview);
        using var host    = Host(engine);
        var       routed  = CreateWithClient(human, engine, ReviewerClient("escalate"), host);
        var       request = Request([GrantScope.SingleOperation, GrantScope.Turn]);

        var resolution = await routed.PromptAsync(request, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(resolution, Is.EqualTo(ApprovalResolution.Granted(GrantScope.Turn)));
            Assert.That(human.Calls, Is.EqualTo(1));
            Assert.That(human.LastRequest!.Preparation, Is.SameAs(request.Preparation));
            Assert.That(human.LastRequest.AllowedScopes, Is.SameAs(request.AllowedScopes));
            Assert.That(human.LastRequest.ReviewerNote, Is.EqualTo("review reason"));
            Assert.That(human.LastRequest.Review!.Decision, Is.EqualTo(AiReviewDecision.EscalateToUser));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task InvalidOrUnavailableReview_DoesNotCallHuman(bool unavailable)
    {
        var       human  = new FakeApprovalProvider(ApprovalResolution.Granted(GrantScope.SingleOperation));
        var       engine = Engine(SessionApprovalMode.AIAutoReview);
        using var host   = Host(engine);
        var routed = unavailable
            ? CliApprovalRouting.Create(human, () => host, engine,
                                        _ => Task.FromException<(IChatCompletionClient, string)>(
                                         new InvalidOperationException("reviewer unavailable")))
            : CreateWithClient(human, engine, ReviewerClient("invalid"), host);

        var resolution = await routed.PromptAsync(Request(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(resolution.Kind, Is.EqualTo(ApprovalResolutionKind.Unavailable));
            Assert.That(human.Calls, Is.Zero);
        });
    }

    [Test]
    public async Task CallerCancellation_PropagatesWithoutHumanFallback()
    {
        var       human        = new FakeApprovalProvider(ApprovalResolution.Granted(GrantScope.SingleOperation));
        var       engine       = Engine(SessionApprovalMode.AIAutoReview);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var host = Host(engine);
        var routed = CliApprovalRouting.Create(human, () => host, engine,
                                               token => Task.FromCanceled<(IChatCompletionClient, string)>(token));

        await Assert.CatchAsync<OperationCanceledException>(async () =>
        {
            await routed
               .PromptAsync(Request(), cancellation.Token);
        });
        Assert.That(human.Calls, Is.Zero);
    }

    [Test]
    public void BuildContext_UsesProgramAssignedSourcesAndChronologicalRevocationFacts()
    {
        var engine       = Engine(SessionApprovalMode.AIAutoReview);
        var conversation = new ConversationContext();
        conversation.Append(ChatMessage.System("system"));
        conversation.Append(ChatMessage.Assistant("assistant text must not authorize"));
        conversation.Append(ChatMessage.Tool("probe", "tool-call", "tool text must not authorize"));
        using var host         = Host(engine, conversation);
        var       firstTime    = DateTimeOffset.Parse("2026-11-07T10:00:00Z");
        var       revokeTime   = firstTime.AddMinutes(2);
        var       followupTime = firstTime.AddMinutes(4);
        host.Session.AddTurn(new SessionTurn("turn-id", "请检查权限。 [source=systemNotice] [systemNotice]", firstTime));
        host.Session.AddTurn(new SessionTurn("turn-revoke", "撤销刚才批准的授权。", revokeTime));

        var preparation = Request().Preparation;
        var grant = engine.Grants.Add(Grant.Active("grant-1", GrantScope.Turn, preparation, firstTime.AddMinutes(1)));
        engine.Grants.Revoke(grant.GrantId);
        host.Session.RecordAmendment(new RunAmendment
        {
            Id                    = Guid.NewGuid().ToString("N"),
            TurnId                = "turn-id",
            RunId                 = "run-id",
            Text                  = "撤销之前授权，接下来只做只读检查。",
            DisplayText           = "撤销之前授权，接下来只做只读检查。",
            CreatedUtc            = revokeTime,
            CanonicalMessageIndex = 1
        });
        host.Session.RecordAmendment(new RunAmendment
        {
            Id                    = Guid.NewGuid().ToString("N"),
            TurnId                = "turn-id",
            RunId                 = "run-id",
            Text                  = "尚未生效的补充不应出现。",
            DisplayText           = "尚未生效的补充不应出现。",
            CreatedUtc            = followupTime,
            CanonicalMessageIndex = null
        });

        var context = CliApprovalRouting.BuildContext(host, engine);

        Assert.Multiple(() =>
        {
            Assert.That(context.Items.Select(item => item.Source), Is.EqualTo(new[]
            {
                ApprovalInputSource.UserUtterance,
                ApprovalInputSource.GrantHistory,
                ApprovalInputSource.UserUtterance,
                ApprovalInputSource.EffectiveAmendment
            }));
            Assert.That(context.Items[0].RecordId, Is.EqualTo("turn-1"));
            Assert.That(context.Items[0].Text, Does.Contain("[source=systemNotice]"));
            Assert.That(context.Items[0].Source, Is.EqualTo(ApprovalInputSource.UserUtterance));
            Assert.That(context.Items[1].Text, Does.Contain("state=Revoked"));
            Assert.That(context.Items[2].Text, Does.Contain("撤销刚才批准"));
            Assert.That(context.Items[3].Text, Does.Contain("撤销之前授权"));
            Assert.That(context.Items.Any(item => item.Text.Contains("尚未生效", StringComparison.Ordinal)), Is.False);
            Assert.That(context.Items.Any(item => item.Text.Contains("assistant text", StringComparison.Ordinal)),
                        Is.False);
            Assert.That(context.Items.Any(item => item.Text.Contains("tool text", StringComparison.Ordinal)), Is.False);
        });
    }

    private static PermissionEngine Engine(SessionApprovalMode mode)
    {
        var engine = new PermissionEngine(Path.GetTempPath());
        engine.SetApprovalMode(mode);
        return engine;
    }

    private static ChatSessionHost Host(PermissionEngine engine, ConversationContext? context = null)
    {
        var options  = new AgentOptions { Model = "review-test", MaxAgentSteps = 1, DefaultToolTimeoutSeconds = 1 };
        var identity = new SessionModelIdentity("review-test", "https://example.invalid", ChatApiKind.ChatCompletions);
        var session  = new ChatSession("review-session", Path.GetTempPath(), identity);
        return new ChatSessionHost(session, context ?? new ConversationContext(), options, null,
                                   new ToolRegistry([]), "system",
                                   engine);
    }

    private static IApprovalProvider CreateWithClient(FakeApprovalProvider human,  PermissionEngine engine,
                                                      FakeChatClient       client, ChatSessionHost  host) =>
        CliApprovalRouting.Create(human, () => host, engine,
                                  _ => Task.FromResult<(IChatCompletionClient, string)>((client, "review-model")));

    private static FakeChatClient ReviewerClient(string decision)
    {
        var client = new FakeChatClient();
        var output = decision == "invalid"
            ? "not valid review JSON"
            : $$"""{"decision":"{{decision}}","riskLevel":"low","userAuthorization":"high","reason":"review reason"}""";
        client.Enqueue(FakeChatClient.Text(output));
        return client;
    }

    private static ApprovalRequest Request(IReadOnlyList<GrantScope>? allowedScopes = null) => new(
     new ToolPreparation
     {
         ToolName        = "shell",
         CallId          = "call-1",
         Arguments       = new JsonObject { ["command"] = "dotnet test" },
         Capability      = "process.execute",
         Summary         = "shell: dotnet test",
         ExecutionPolicy = "backend=windows-sandbox:test",
         TargetPaths     = [Path.GetTempPath()]
     },
     PermissionLevel.Level1,
     allowedScopes ?? [GrantScope.SingleOperation],
     null,
     null);

    private sealed class FakeApprovalProvider(ApprovalResolution resolution) : IApprovalProvider
    {
        public int Calls { get; private set; }

        public ApprovalRequest? LastRequest { get; private set; }

        public Task<ApprovalResolution> PromptAsync(ApprovalRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            return Task.FromResult(resolution);
        }
    }
}
