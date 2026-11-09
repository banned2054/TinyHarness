using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using TinyHarness.Cli.Services;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Agent;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         AI 审批独立期限的离线测试：审批期限经手动时钟确定性驱动，验证到期转 Timeout 失败、
///         路由为 Unavailable、不转人工、不写拒绝记忆；期限在每次审核开始解析并冻结，两次审核
///         之间随 provider 刷新；调用方取消仍原样传播。聊天响应总期限关闭不影响审批独立有界。
///     </para>
///     <para>
///         Offline tests for the independent AI approval deadline: the deadline is driven
///         deterministically by a manual clock, verifying expiry maps to a Timeout failure,
///         routes to Unavailable without human fallback or denial memory, refreshes between
///         reviews while staying frozen inside one review, and caller cancellation still
///         propagates verbatim. A disabled chat response deadline does not affect the
///         independent bounded approval.
///     </para>
/// </summary>
public class AiReviewerApprovalTimeoutTests
{
    /// <summary>审批期限经手动时钟确定性到期 → Timeout 失败（聊天 response=0 不影响）。</summary>
    [Test]
    public async Task ApprovalDeadlineExpiry_MapsToTimeoutFailure()
    {
        var provider = new MicrosoftAiChatClientTimeoutTests.ManualTimeoutProvider();
        var gated    = new GateReviewerClient();
        var reviewer = new AiReviewer(_ => Task.FromResult<(IChatCompletionClient, string)>((gated, "reviewer-model")),
                                      () => TimeSpan.FromSeconds(30), provider);

        var task = reviewer.ReviewAsync(Request(), Context(), CancellationToken.None);
        await gated.WaitStartedAsync(TimeSpan.FromSeconds(15));
        provider.Advance(TimeSpan.FromSeconds(31));
        var outcome = await AwaitWithGuard(task);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Failure, Is.Not.Null);
            Assert.That(outcome.Failure!.Kind, Is.EqualTo(AiReviewFailureKind.Timeout));
            TestAssert.Contains("approval deadline of 30 seconds", outcome.Failure.Detail, StringComparison.Ordinal);
        });
    }

    /// <summary>审批流持续有进展（update 持续供给）但期限耗尽仍失败。</summary>
    [Test]
    public async Task ApprovalDeadlineExpiryWithContinuedProgress_StillTimesOut()
    {
        var provider = new MicrosoftAiChatClientTimeoutTests.ManualTimeoutProvider();
        var endless  = new EndlessDeltaClient();
        var reviewer = new AiReviewer(_ => Task.FromResult<(IChatCompletionClient, string)>((endless, "reviewer-model")),
                                      () => TimeSpan.FromSeconds(30), provider);

        var task = reviewer.ReviewAsync(Request(), Context(), CancellationToken.None);
        await endless.WaitStartedAsync(TimeSpan.FromSeconds(15));
        provider.Advance(TimeSpan.FromSeconds(31));
        var outcome = await AwaitWithGuard(task);

        Assert.That(outcome.Failure!.Kind, Is.EqualTo(AiReviewFailureKind.Timeout));
    }

    /// <summary>
    ///     <para>审批期限到期 → 路由 Unavailable、不调用人工通道、不写拒绝记忆
    ///     （拒绝记忆只在 Denied 结论时由宿主记录；Unavailable 后评估仍为原分类，不出现
    ///     "Previously denied by the approver"）。</para>
    /// </summary>
    [Test]
    public async Task TimedOutReview_RoutesUnavailableWithoutHumanAndDenialMemory()
    {
        var human  = new FakeApprovalProvider();
        var engine = new PermissionEngine(Path.GetTempPath());
        engine.SetApprovalMode(SessionApprovalMode.AIAutoReview);
        using var host = Host(engine);
        var gated  = new GateReviewerClient();
        // 审批期限 250ms（真实时钟）：审核流挂起在门上，期限到即转 Unavailable。
        var routed = CliApprovalRouting.Create(human, () => host, engine,
                                               _ => Task.FromResult<(IChatCompletionClient, string)>((gated, "m")),
                                               () => TimeSpan.FromMilliseconds(250));

        var resolution = await AwaitWithGuard(routed.PromptAsync(Request(), CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(resolution.Kind, Is.EqualTo(ApprovalResolutionKind.Unavailable));
            TestAssert.Contains("timeout", resolution.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.That(human.Calls, Is.Zero);
            Assert.That(engine.Evaluate(Request().Preparation).Reason,
                        Does.Not.Contain("Previously denied by the approver"));
        });
    }

    /// <summary>
    ///     <para>迟到成功：流输出合法 allow 与 End 并正常收尾（不抛取消异常），但最后一次推进已
    ///     跨过审批期限——接受结果前必须显式判定期限，返回 Timeout 而不是 Allow。</para>
    /// </summary>
    [Test]
    public async Task ApprovalDeadlineExceededDuringStream_RejectsLateAllowResult()
    {
        var provider = new MicrosoftAiChatClientTimeoutTests.ManualTimeoutProvider();
        var late     = new LateSuccessClient(() => provider.Advance(TimeSpan.FromSeconds(31)));
        var reviewer = new AiReviewer(_ => Task.FromResult<(IChatCompletionClient, string)>((late, "reviewer-model")),
                                      () => TimeSpan.FromSeconds(30), provider);

        var outcome = await reviewer.ReviewAsync(Request(), Context(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Result, Is.Null);
            Assert.That(outcome.Failure, Is.Not.Null);
            Assert.That(outcome.Failure!.Kind, Is.EqualTo(AiReviewFailureKind.Timeout));
            TestAssert.Contains("approval deadline of 30 seconds", outcome.Failure.Detail, StringComparison.Ordinal);
        });
    }

    /// <summary>
    ///     <para>迟到创建：客户端创建正常返回（不抛取消异常），但创建期间已跨过审批期限——
    ///     使用客户端前必须转 Timeout，不得继续发送审核请求。</para>
    /// </summary>
    [Test]
    public async Task ApprovalDeadlineExceededDuringClientCreation_RejectsLateClient()
    {
        var provider = new MicrosoftAiChatClientTimeoutTests.ManualTimeoutProvider();
        var reviewer = new AiReviewer(_ =>
                                      {
                                          provider.Advance(TimeSpan.FromSeconds(31));
                                          return Task.FromResult<(IChatCompletionClient, string)>(
                                              (new LateSuccessClient(() => { }), "reviewer-model"));
                                      },
                                      () => TimeSpan.FromSeconds(30), provider);

        var outcome = await reviewer.ReviewAsync(Request(), Context(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Result, Is.Null);
            Assert.That(outcome.Failure!.Kind, Is.EqualTo(AiReviewFailureKind.Timeout));
        });
    }

    /// <summary>
    ///     <para>期限在两次审核之间刷新（缩短方向）：第一次审核按当时的 30 秒到期；provider
    ///     改为 10 秒后，第二次审核按新期限到期，失败详情携带新秒数。</para>
    ///     <para>
    ///         The deadline refreshes between reviews (shortening direction): the first review expires at the
    ///         then-current 30 seconds; after the provider changes to 10 seconds, the second review expires at
    ///         the new deadline and the failure detail carries the new value.
    ///     </para>
    /// </summary>
    [Test]
    public async Task DeadlineRefreshBetweenReviews_ShortenedDeadlineExpiresAtNewValue()
    {
        var provider = new MicrosoftAiChatClientTimeoutTests.ManualTimeoutProvider();
        var currentSeconds = 30;
        var gates = new Queue<GateReviewerClient>([new(), new()]);
        var reviewer = new AiReviewer(_ => Task.FromResult<(IChatCompletionClient, string)>((gates.Dequeue(),
                                                                                             "reviewer-model")),
                                      () => TimeSpan.FromSeconds(currentSeconds), provider);

        // 先取出门上实例再启动审核：工厂同步出队，Peek 必须在调用前捕获当次审核的客户端。
        var firstGate = gates.Peek();
        var first = reviewer.ReviewAsync(Request(), Context(), CancellationToken.None);
        await firstGate.WaitStartedAsync(TimeSpan.FromSeconds(15));
        provider.Advance(TimeSpan.FromSeconds(29));
        await AssertStillPendingAsync(first);
        provider.Advance(TimeSpan.FromSeconds(2));
        Assert.That((await AwaitWithGuard(first)).Failure!.Detail,
                    Does.Contain("approval deadline of 30 seconds"));

        currentSeconds = 10;
        var secondGate = gates.Peek();
        var second = reviewer.ReviewAsync(Request(), Context(), CancellationToken.None);
        await secondGate.WaitStartedAsync(TimeSpan.FromSeconds(15));
        provider.Advance(TimeSpan.FromSeconds(9));
        await AssertStillPendingAsync(second);
        provider.Advance(TimeSpan.FromSeconds(2));
        Assert.That((await AwaitWithGuard(second)).Failure!.Detail,
                    Does.Contain("approval deadline of 10 seconds"));
    }

    /// <summary>
    ///     <para>期限在两次审核之间刷新（延长方向）：第一次审核按 10 秒到期；provider 改为 60 秒
    ///     后，第二次审核越过旧期限点仍挂起，新期限点才到期。</para>
    ///     <para>
    ///         The deadline refreshes between reviews (extending direction): the first review expires at 10
    ///         seconds; after the provider changes to 60 seconds, the second review survives past the old
    ///         deadline point and only expires at the new one.
    ///     </para>
    /// </summary>
    [Test]
    public async Task DeadlineRefreshBetweenReviews_ExtendedDeadlineSurvivesOldDeadlinePoint()
    {
        var provider = new MicrosoftAiChatClientTimeoutTests.ManualTimeoutProvider();
        var currentSeconds = 10;
        var gates = new Queue<GateReviewerClient>([new(), new()]);
        var reviewer = new AiReviewer(_ => Task.FromResult<(IChatCompletionClient, string)>((gates.Dequeue(),
                                                                                             "reviewer-model")),
                                      () => TimeSpan.FromSeconds(currentSeconds), provider);

        var firstGate = gates.Peek();
        var first = reviewer.ReviewAsync(Request(), Context(), CancellationToken.None);
        await firstGate.WaitStartedAsync(TimeSpan.FromSeconds(15));
        provider.Advance(TimeSpan.FromSeconds(9));
        await AssertStillPendingAsync(first);
        provider.Advance(TimeSpan.FromSeconds(2));
        Assert.That((await AwaitWithGuard(first)).Failure!.Detail,
                    Does.Contain("approval deadline of 10 seconds"));

        currentSeconds = 60;
        var secondGate = gates.Peek();
        var second = reviewer.ReviewAsync(Request(), Context(), CancellationToken.None);
        await secondGate.WaitStartedAsync(TimeSpan.FromSeconds(15));
        provider.Advance(TimeSpan.FromSeconds(30));
        await AssertStillPendingAsync(second);
        provider.Advance(TimeSpan.FromSeconds(31));
        Assert.That((await AwaitWithGuard(second)).Failure!.Detail,
                    Does.Contain("approval deadline of 60 seconds"));
    }

    /// <summary>
    ///     <para>活动审核冻结（不延长）：provider 在审核 A 期间由 30 秒改为 300 秒，A 仍在原
    ///     30 秒点到期；随后的审核 B 按新值 300 秒到期，30 秒点不再触发。</para>
    ///     <para>
    ///         Active reviews stay frozen (no extension): with the provider changed from 30 to 300 seconds
    ///         mid-review, review A still expires at its original 30 seconds; the following review B expires
    ///         at the new 300 seconds and the 30-second point no longer fires.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ActiveReviewKeepsFrozenDeadline_EvenWhenProviderChangesMidReview()
    {
        var provider = new MicrosoftAiChatClientTimeoutTests.ManualTimeoutProvider();
        var currentSeconds = 30;
        var gates = new Queue<GateReviewerClient>([new(), new()]);
        var reviewer = new AiReviewer(_ => Task.FromResult<(IChatCompletionClient, string)>((gates.Dequeue(),
                                                                                             "reviewer-model")),
                                      () => TimeSpan.FromSeconds(currentSeconds), provider);

        var frozenGate = gates.Peek();
        var frozen = reviewer.ReviewAsync(Request(), Context(), CancellationToken.None);
        await frozenGate.WaitStartedAsync(TimeSpan.FromSeconds(15));
        currentSeconds = 300;
        provider.Advance(TimeSpan.FromSeconds(31));
        Assert.That((await AwaitWithGuard(frozen)).Failure!.Detail,
                    Does.Contain("approval deadline of 30 seconds"));

        var refreshedGate = gates.Peek();
        var refreshed = reviewer.ReviewAsync(Request(), Context(), CancellationToken.None);
        await refreshedGate.WaitStartedAsync(TimeSpan.FromSeconds(15));
        provider.Advance(TimeSpan.FromSeconds(30));
        await AssertStillPendingAsync(refreshed);
        provider.Advance(TimeSpan.FromSeconds(271));
        Assert.That((await AwaitWithGuard(refreshed)).Failure!.Detail,
                    Does.Contain("approval deadline of 300 seconds"));
    }

    /// <summary>
    ///     <para>活动审核冻结（不缩短）：provider 在审核期间由 300 秒改为 10 秒，已开始的审核
    ///     越过 10 秒点仍挂起，按开始时的 300 秒到期。</para>
    ///     <para>
    ///         Active reviews stay frozen (no shortening): with the provider changed from 300 to 10 seconds
    ///         mid-review, the already-started review survives past 10 seconds and expires at the 300 seconds
    ///         it started with.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ActiveReviewNotShortenedMidReview_OriginalDeadlineStillApplies()
    {
        var provider = new MicrosoftAiChatClientTimeoutTests.ManualTimeoutProvider();
        var currentSeconds = 300;
        var gated = new GateReviewerClient();
        var reviewer = new AiReviewer(_ => Task.FromResult<(IChatCompletionClient, string)>((gated, "reviewer-model")),
                                      () => TimeSpan.FromSeconds(currentSeconds), provider);

        var task = reviewer.ReviewAsync(Request(), Context(), CancellationToken.None);
        await gated.WaitStartedAsync(TimeSpan.FromSeconds(15));
        currentSeconds = 10;
        provider.Advance(TimeSpan.FromSeconds(11));
        await AssertStillPendingAsync(task);
        provider.Advance(TimeSpan.FromSeconds(290));
        Assert.That((await AwaitWithGuard(task)).Failure!.Detail,
                    Does.Contain("approval deadline of 300 seconds"));
    }

    /// <summary>
    ///     <para>审核中调用方取消优先于期限：先取消调用方再推时钟越过期限，ReviewAsync 仍原样
    ///     传播 OperationCanceledException，不落入 Timeout 失败分类。</para>
    ///     <para>
    ///         Caller cancellation during a review outranks the deadline: cancelling the caller first and then
    ///         advancing the clock past the deadline still propagates OperationCanceledException verbatim
    ///         instead of entering the Timeout failure category.
    ///     </para>
    /// </summary>
    [Test]
    public async Task CallerCancelDuringReview_PropagatesOverDeadline()
    {
        var provider = new MicrosoftAiChatClientTimeoutTests.ManualTimeoutProvider();
        var gated = new GateReviewerClient();
        var reviewer = new AiReviewer(_ => Task.FromResult<(IChatCompletionClient, string)>((gated, "reviewer-model")),
                                      () => TimeSpan.FromSeconds(30), provider);
        using var caller = new CancellationTokenSource();

        var task = reviewer.ReviewAsync(Request(), Context(), caller.Token);
        await gated.WaitStartedAsync(TimeSpan.FromSeconds(15));
        await caller.CancelAsync();
        provider.Advance(TimeSpan.FromSeconds(31));

        await Assert.CatchAsync<OperationCanceledException>(async () => await AwaitWithGuard(task));
    }

    /// <summary>
    ///     <para>provider 解析出非正数期限回落内置默认 300 秒：299 秒仍挂起，300 秒点到期，
    ///     失败详情携带默认秒数。</para>
    ///     <para>
    ///         A non-positive provider value falls back to the built-in 300-second default: still pending at
    ///         299 seconds, expiring at the 300-second point with the default value in the failure detail.
    ///     </para>
    /// </summary>
    [Test]
    public async Task NonPositiveDeadline_FallsBackToDefault300Seconds()
    {
        Assert.That(ModelTimeouts.Default.ApprovalTimeoutSeconds, Is.EqualTo(300));
        var provider = new MicrosoftAiChatClientTimeoutTests.ManualTimeoutProvider();
        var gated = new GateReviewerClient();
        var reviewer = new AiReviewer(_ => Task.FromResult<(IChatCompletionClient, string)>((gated, "reviewer-model")),
                                      () => TimeSpan.Zero, provider);

        var task = reviewer.ReviewAsync(Request(), Context(), CancellationToken.None);
        await gated.WaitStartedAsync(TimeSpan.FromSeconds(15));
        provider.Advance(TimeSpan.FromSeconds(299));
        await AssertStillPendingAsync(task);
        provider.Advance(TimeSpan.FromSeconds(2));
        Assert.That((await AwaitWithGuard(task)).Failure!.Detail,
                    Does.Contain("approval deadline of 300 seconds"));
    }

    /// <summary>
    ///     <para>provider 抛出非取消异常 → ReviewUnavailable，审核客户端工厂未被调用，
    ///     解析阶段不产生任何客户端资源。</para>
    ///     <para>
    ///         A provider throwing a non-cancellation exception yields ReviewUnavailable without invoking
    ///         the reviewer client factory — the resolution stage creates no client resources at all.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ThrowingDeadlineProvider_YieldsReviewUnavailableWithoutCreatingClient()
    {
        var provider = new MicrosoftAiChatClientTimeoutTests.ManualTimeoutProvider();
        var createCalls = 0;
        var reviewer = new AiReviewer(_ =>
                                      {
                                          createCalls++;
                                          return Task.FromResult<(IChatCompletionClient, string)>(
                                              (new LateSuccessClient(() => { }), "reviewer-model"));
                                      },
                                      () => throw new InvalidOperationException("config exploded"), provider);

        var outcome = await reviewer.ReviewAsync(Request(), Context(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Result, Is.Null);
            Assert.That(outcome.Failure!.Kind, Is.EqualTo(AiReviewFailureKind.ReviewUnavailable));
            TestAssert.Contains("could not be resolved", outcome.Failure.Detail, StringComparison.Ordinal);
            TestAssert.Contains("config exploded", outcome.Failure.Detail, StringComparison.Ordinal);
            Assert.That(createCalls, Is.Zero);
        });
    }

    /// <summary>
    ///     <para>真实授权链路：审核期限耗尽后 AgentLoop 返回 Unavailable——工具保持未执行、
    ///     不转人工、不写拒绝记忆，不可用原因回传给模型。</para>
    ///     <para>
    ///         The real authorization chain: once the review deadline expires the agent loop receives
    ///         Unavailable — the tool stays unexecuted with no human fallback and no denial memory, and the
    ///         unavailable reason returns to the model.
    ///     </para>
    /// </summary>
    [Test]
    public async Task TimedOutReview_AgentLoopKeepsToolUnexecutedWithoutDenialMemory()
    {
        using var dir     = new TestTempDir();
        var       outside = Path.Combine(Path.GetDirectoryName(dir.Root)!, $"outside-{Guid.NewGuid():N}.txt");
        var       client  = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("write_outside",
                                               new JsonObject { ["target"] = outside }.ToJsonString(), "p1"));
        client.Enqueue(FakeChatClient.Text("understood"));
        var human = new FakeApprovalProvider();
        var engine = new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root));
        engine.SetApprovalMode(SessionApprovalMode.AIAutoReview);
        using var host = Host(engine);
        var gated = new GateReviewerClient();
        var routed = CliApprovalRouting.Create(human, () => host, engine,
                                               _ => Task.FromResult<(IChatCompletionClient, string)>((gated, "m")),
                                               () => TimeSpan.FromMilliseconds(250));
        var tool = new OutsideWriteProbe();

        var loop = new AgentLoop(client, new ToolRegistry([tool]), LoopOptions(), engine, routed);
        var result = await loop.RunAsync("sys", "write outside", CancellationToken.None);

        var preparation = new ToolPreparation
        {
            ToolName    = "write_outside",
            CallId      = "p1",
            Arguments   = new JsonObject { ["target"] = outside },
            Capability  = "filesystem.write",
            Summary     = $"write {outside}",
            TargetPaths = [outside]
        };
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
            Assert.That(result.ToolExecutions, Is.EqualTo(0));
            Assert.That(tool.ExecuteCount, Is.EqualTo(0));
            Assert.That(File.Exists(outside), Is.False);
            Assert.That(human.Calls, Is.Zero);
            Assert.That(loop.History.Any(message => message.Role == ChatRole.Tool &&
                                                    message.Content.Contains("Approval unavailable; " +
                                                        "the operation was not executed", StringComparison.Ordinal)),
                        Is.True, "不可用原因应作为工具结果回传给模型。");
            Assert.That(engine.Evaluate(preparation).Reason,
                        Does.Not.Contain("Previously denied by the approver"));
        });
    }

    // ---- 请求与宿主构造 ------------------------------------------------------------------

    private static ApprovalRequest Request() => new(
                                                    new ToolPreparation
                                                    {
                                                        ToolName  = "shell",
                                                        CallId    = "call-1",
                                                        Arguments = new JsonObject { ["command"] = "dotnet test" },
                                                        Capability = "process.execute",
                                                        Summary    = "shell: dotnet test",
                                                        ExecutionPolicy = "backend=windows-sandbox:test",
                                                        TargetPaths     = [Path.GetTempPath()]
                                                    },
                                                    PermissionLevel.Level1,
                                                    [GrantScope.SingleOperation],
                                                    null,
                                                    null);

    private static ApprovalContext Context() => new(
    [
        new ApprovalContextItem(ApprovalInputSource.UserUtterance, "turn-1", "请运行测试并修复失败。")
    ]);

    private static AgentOptions LoopOptions() => new()
    {
        Model                     = "review-test",
        MaxAgentSteps             = 10,
        DefaultToolTimeoutSeconds = 30
    };

    private static ChatSessionHost Host(PermissionEngine engine)
    {
        var options  = new AgentOptions { Model = "review-test", MaxAgentSteps = 1, DefaultToolTimeoutSeconds = 1 };
        var identity = new SessionModelIdentity("review-test", "https://example.invalid", ChatApiKind.ChatCompletions);
        var session  = new ChatSession("review-session", Path.GetTempPath(), identity);
        return new ChatSessionHost(session, new ConversationContext(), options, null,
                                   new ToolRegistry([]), "system", engine);
    }

    /// <summary>守卫等待：15 秒内未完成即测试失败，避免实现缺陷退化为无限等待。</summary>
    private static async Task<T> AwaitWithGuard<T>(Task<T> task)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            return await task.WaitAsync(guard.Token);
        }
        catch (OperationCanceledException) when (guard.Token.IsCancellationRequested)
        {
            Assert.Fail("15 秒测试守卫超时：被测实现未按预期完成或抛出。");
            throw;
        }
    }

    /// <summary>
    ///     <para>确认任务仍挂起：期限未到时审核不得完成；短暂延迟只为让错误完成有机会暴露。</para>
    ///     <para>
    ///         Confirms the task is still pending: before the deadline a review must not complete; the short
    ///         delay only gives a wrongly-early completion a chance to surface.
    ///     </para>
    /// </summary>
    private static async Task AssertStillPendingAsync(Task task)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.That(task.IsCompleted, Is.False, "审批期限尚未到，审核不应完成。");
    }

    // ---- fake IChatCompletionClient ------------------------------------------------------

    /// <summary>门控审核客户端：记录启动信号后挂起在门上，直到审批令牌触发（期限到期或调用方取消）。</summary>
    private sealed class GateReviewerClient(string responseText = "allow") : IChatCompletionClient
    {
        private readonly TaskCompletionSource _gate    = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<ChatStreamEvent> CompleteAsync(
            ChatCompletionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            foreach (var item in FakeChatClient.Text(responseText))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }
        }

        public Task WaitStartedAsync(TimeSpan guard) =>
            _started.Task.WaitAsync(guard);
    }

    /// <summary>持续产出文本增量的审核客户端（永不结束），用于验证进展不豁免审批总期限。</summary>
    private sealed class EndlessDeltaClient : IChatCompletionClient
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<ChatStreamEvent> CompleteAsync(
            ChatCompletionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // 空文本增量：持续供给进展但不增长审核输出（4096 字符上限会先于期限触发 InvalidResponse）。
            // 每次 MoveNext 显式检查审批令牌：期限到期后立即中断，进展不豁免期限。
            _started.TrySetResult();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return new ChatStreamEvent { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = string.Empty };
            }
        }

        public Task WaitStartedAsync(TimeSpan guard) =>
            _started.Task.WaitAsync(guard);
    }

    /// <summary>
    ///     <para>
    ///         迟到成功客户端：输出合法 allow JSON 与 End 后正常收尾，全程不检查取消令牌；
    ///     在产出 End 前触发 <paramref name="advancePastDeadline" />，模拟“期限已到但底层正常返回”。
    ///     </para>
    /// </summary>
    private sealed class LateSuccessClient(Action advancePastDeadline) : IChatCompletionClient
    {
        public async IAsyncEnumerable<ChatStreamEvent> CompleteAsync(
            ChatCompletionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // 刻意不检查 cancellationToken：底层无视期限、正常收尾正是本场景的前提。
            yield return new ChatStreamEvent
            {
                Kind         = ChatStreamEventKind.ContentDelta,
                ContentDelta = """{"decision":"allow","riskLevel":"low","userAuthorization":"high","reason":"ok"}"""
            };
            advancePastDeadline();
            yield return new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason = "stop" };
        }
    }

    /// <summary>工作区外写入探针工具：记录执行次数，用于断言审批不可用时操作保持未执行。</summary>
    private sealed class OutsideWriteProbe : ITool
    {
        public int ExecuteCount { get; private set; }

        public ToolDefinition Definition { get; } = new()
        {
            Name = "write_outside", Description = "test", Parameters = new JsonObject { ["type"] = "object" }
        };

        public ToolPreparation Prepare(ChatToolCall call)
        {
            var target = JsonNode.Parse(call.ArgumentsJson)!["target"]!.GetValue<string>();
            return new ToolPreparation
            {
                ToolName    = Definition.Name,
                CallId      = call.Id,
                Arguments   = new JsonObject { ["target"] = target },
                Capability  = "filesystem.write",
                Summary     = $"write {target}",
                TargetPaths = [target]
            };
        }

        public Task<ToolResult> ExecuteAsync(ToolPreparation preparation, CancellationToken cancellationToken)
        {
            ExecuteCount++;
            return Task.FromResult(new ToolResult { Succeeded = true, Content = "written" });
        }
    }

    /// <summary>记录调用次数的人工审批通道 fake。</summary>
    private sealed class FakeApprovalProvider : IApprovalProvider
    {
        public int Calls { get; private set; }

        public Task<ApprovalResolution> PromptAsync(ApprovalRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(ApprovalResolution.Granted(GrantScope.SingleOperation));
        }
    }
}
