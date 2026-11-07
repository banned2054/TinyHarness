using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.Permissions;

namespace TinyHarness.Tests;

public class AiReviewerProtocolTests
{
    private static readonly ChatStreamEvent Ended = new() { Kind = ChatStreamEventKind.End, FinishReason = "stop" };

    private static ApprovalRequest Request() => new(
                                                    new ToolPreparation
                                                    {
                                                        ToolName = "shell",
                                                        CallId = "call_1",
                                                        Arguments = new JsonObject { ["command"] = "dotnet test" },
                                                        Capability = "process.execute",
                                                        Summary = "shell: dotnet test",
                                                        ExecutionPolicy = "backend=windows-sandbox:test",
                                                        RequiresNetworkAccess = false,
                                                        TargetPaths = [@"C:\ws"]
                                                    },
                                                    PermissionLevel.Level1,
                                                    [GrantScope.SingleOperation],
                                                    null,
                                                    null);

    private static ApprovalContext Context() => new(
    [
        new ApprovalContextItem(ApprovalInputSource.UserUtterance, "turn-1", "请运行测试并修复失败。"),
        new ApprovalContextItem(ApprovalInputSource.SystemNotice, ApprovalContextBuilder.WindowNoticeRecordId,
                                "〔窗口说明〕另有 3 条更早记录未包含在内。")
    ]);

    private static (AiReviewer Reviewer, ScriptedClient Client) Compose(params ChatStreamEvent[] events)
    {
        var client = new ScriptedClient(events);
        var reviewer =
            new AiReviewer(_ => Task.FromResult<(IChatCompletionClient, string)>((client, "reviewer-model")));
        return (reviewer, client);
    }

    private static AiReviewer ComposeUnavailable(Exception failure) =>
        new(_ => Task.FromException<(IChatCompletionClient, string)>(failure));

    private static async Task<AiReviewOutcome> ReviewAsync(AiReviewer reviewer, ScriptedClient client)
    {
        var outcome = await reviewer.ReviewAsync(Request(), Context(), CancellationToken.None);
        Assert.That(client.LastRequest, Is.Not.Null);
        return outcome;
    }

    private static ChatStreamEvent Text(string text) =>
        new() { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = text };

    private static string Protocol(string decision, string risk = "medium", string authorization = "high",
                                   string reason = "与目标直接相关") =>
        $$"""{"decision":"{{decision}}","riskLevel":"{{risk}}","userAuthorization":"{{authorization}}","reason":"{{reason}}"}""";

    [Test]
    public async Task ValidOutcomes_MapToAllThreeDecisionsAndDimensions()
    {
        foreach (var (decision, expected) in new[]
                 {
                     ("allow", AiReviewDecision.Allow),
                     ("deny", AiReviewDecision.Deny),
                     ("escalate", AiReviewDecision.EscalateToUser)
                 })
        {
            var (reviewer, client) = Compose(Text(Protocol(decision, "critical", "low", "理由")), Ended);
            var outcome = await ReviewAsync(reviewer, client);

            Assert.Multiple(() =>
            {
                Assert.That(outcome.Failure, Is.Null);
                Assert.That(outcome.Result!.Decision, Is.EqualTo(expected));
                Assert.That(outcome.Result.RiskLevel, Is.EqualTo(RiskLevel.Critical));
                Assert.That(outcome.Result.UserAuthorization, Is.EqualTo(UserAuthorization.Low));
                Assert.That(outcome.Result.Reason, Is.EqualTo("理由"));
            });
        }
    }

    [Test]
    public async Task ProtocolViolations_MapToInvalidResponse()
    {
        var violations = new[]
        {
            """{"decision":"allow","riskLevel":"medium","userAuthorization":"high"}""",
            Protocol("allow") + " extra",
            Protocol("maybe"),
            Protocol("allow", "extreme"),
            Protocol("allow", "medium", "certain"),
            """{"decision":"allow","riskLevel":"medium","userAuthorization":"high","reason":"  "}""",
            $$"""{"decision":"allow","riskLevel":"medium","userAuthorization":"high","reason":"{{new string('长', 2001)}}"}""",
            "not json"
        };

        foreach (var output in violations)
        {
            var (reviewer, client) = Compose(Text(output), Ended);
            var outcome = await ReviewAsync(reviewer, client);
            Assert.Multiple(() =>
            {
                Assert.That(outcome.Result, Is.Null, output);
                Assert.That(outcome.Failure!.Kind, Is.EqualTo(AiReviewFailureKind.InvalidResponse), output);
            });
        }
    }

    [Test]
    public async Task StreamViolations_MapToInvalidResponse()
    {
        // 工具调用增量。
        var (toolReviewer, toolClient) =
            Compose(new ChatStreamEvent { Kind = ChatStreamEventKind.ToolCallDelta }, Ended);
        var toolOutcome = await ReviewAsync(toolReviewer, toolClient);
        Assert.That(toolOutcome.Failure!.Kind, Is.EqualTo(AiReviewFailureKind.InvalidResponse));

        // 超长输出。
        var (longReviewer, longClient) = Compose(Text(new string('x', 4_097)), Ended);
        var longOutcome = await ReviewAsync(longReviewer, longClient);
        Assert.That(longOutcome.Failure!.Kind, Is.EqualTo(AiReviewFailureKind.InvalidResponse));

        // 非 stop 结束原因。
        var (finishReviewer, finishClient) = Compose(
                                                     Text(Protocol("allow")),
                                                     new ChatStreamEvent
                                                         { Kind = ChatStreamEventKind.End, FinishReason = "length" });
        var finishOutcome = await ReviewAsync(finishReviewer, finishClient);
        Assert.That(finishOutcome.Failure!.Kind, Is.EqualTo(AiReviewFailureKind.InvalidResponse));

        // 流没有结束事件。
        var (openReviewer, openClient) = Compose(Text(Protocol("allow")));
        var openOutcome = await ReviewAsync(openReviewer, openClient);
        Assert.That(openOutcome.Failure!.Kind, Is.EqualTo(AiReviewFailureKind.InvalidResponse));
    }

    [Test]
    public async Task TechnicalFailures_MapToTheirKinds()
    {
        var (timeoutReviewer, timeoutClient) = Compose(
                                                       new ChatStreamEvent
                                                       {
                                                           Kind         = ChatStreamEventKind.ContentDelta,
                                                           ContentDelta = "partial"
                                                       });
        timeoutClient.ThrowInStream = new TimeoutException("too slow");
        var timeoutOutcome = await ReviewAsync(timeoutReviewer, timeoutClient);
        Assert.That(timeoutOutcome.Failure!.Kind, Is.EqualTo(AiReviewFailureKind.Timeout));

        var unavailable        = ComposeUnavailable(new InvalidOperationException("reviewer model missing"));
        var unavailableOutcome = await unavailable.ReviewAsync(Request(), Context(), CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(unavailableOutcome.Failure!.Kind, Is.EqualTo(AiReviewFailureKind.ReviewUnavailable));
            Assert.That(unavailableOutcome.Failure.Detail, Does.Contain("reviewer model missing"));
        });
    }

    [Test]
    public void CallerCancellation_Propagates_AndIsNotConverted()
    {
        var (reviewer, _) = Compose(Text(Protocol("allow")), Ended);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAsync<OperationCanceledException>(() =>
                                                           reviewer.ReviewAsync(Request(), Context(),
                                                                                    cancellation.Token));
    }

    [Test]
    public async Task Payload_CarriesStructuredSources_FrozenPlan_AndAllowedScopes()
    {
        var (reviewer, client) = Compose(Text(Protocol("allow")), Ended);
        await ReviewAsync(reviewer, client);

        var payload        = JsonDocument.Parse(client.LastRequest!.Messages[^1].Content).RootElement;
        var contextEntries = payload.GetProperty("context");
        Assert.Multiple(() =>
        {
            Assert.That(contextEntries.GetArrayLength(), Is.EqualTo(2));
            Assert.That(contextEntries[0].GetProperty("source").GetString(), Is.EqualTo("userUtterance"));
            Assert.That(contextEntries[0].GetProperty("recordId").GetString(), Is.EqualTo("turn-1"));
            Assert.That(contextEntries[1].GetProperty("source").GetString(), Is.EqualTo("systemNotice"));
            Assert.That(payload.GetProperty("pendingOperation").GetProperty("capability").GetString(),
                        Is.EqualTo("process.execute"));
            Assert.That(payload.GetProperty("pendingOperation").GetProperty("level").GetString(),
                        Is.EqualTo("Level1"));
            Assert.That(payload.GetProperty("pendingOperation").GetProperty("allowedScopes")[0].GetString(),
                        Is.EqualTo("singleOperation"));
        });
        Assert.That(client.LastRequest!.Messages[0].Content, Does.Contain("deny"));
        Assert.That(client.LastRequest!.Messages[0].Content, Does.Contain("escalate"));
    }

    [Test]
    public async Task ReviewerClient_IsDisposedAfterEachReview()
    {
        var client   = new DisposalTrackingClient(Text(Protocol("allow")), Ended);
        var reviewer = new AiReviewer(_ => Task.FromResult<(IChatCompletionClient, string)>((client, "m")));

        await reviewer.ReviewAsync(Request(), Context(), CancellationToken.None);
        Assert.That(client.Disposed, Is.True);
    }

    /// <summary>脚本化聊天客户端：按脚本产出流事件并捕获请求；可注入流中异常。</summary>
    private sealed class ScriptedClient(params ChatStreamEvent[] events) : IChatCompletionClient
    {
        public ChatCompletionRequest? LastRequest { get; private set; }

        public Exception? ThrowInStream { get; set; }

        public async IAsyncEnumerable<ChatStreamEvent> CompleteAsync(
            ChatCompletionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            LastRequest = request;
            foreach (var @event in events)
            {
                await Task.Yield();
                if (ThrowInStream is { } failure) throw failure;
                yield return @event;
            }
        }
    }

    private sealed class DisposalTrackingClient(params ChatStreamEvent[] events) : IChatCompletionClient, IDisposable
    {
        public bool Disposed { get; private set; }

        public async IAsyncEnumerable<ChatStreamEvent> CompleteAsync(
            ChatCompletionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var @event in events)
            {
                await Task.Yield();
                yield return @event;
            }
        }

        public void Dispose() => Disposed = true;
    }
}
