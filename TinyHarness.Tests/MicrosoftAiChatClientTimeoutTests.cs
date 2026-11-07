using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.ChatCompletions;
using ChatMessage = TinyHarness.Core.Models.ChatCompletions.ChatMessage;
using MicrosoftChatMessage = Microsoft.Extensions.AI.ChatMessage;
using TextContent = Microsoft.Extensions.AI.TextContent;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         MicrosoftAiChatClient 超时策略的离线测试：经 internal 接缝注入小期限与手动时钟，
///         验证流空闲保护、可选响应总期限、取消分类与响应完整性——全部无网络、无 key。
///         空闲计时始终走真实系统时钟（CancelAfter 无 TimeProvider 重载），相关用例使用
///         1 秒窗口加充裕外层守卫，不做精确毫秒断言。
///     </para>
///     <para>
///         Offline tests for the MicrosoftAiChatClient timeout policy: small limits and a manual
///         clock are injected through the internal seam to verify the stream idle guard, the
///         optional end-to-end response deadline, cancellation classification, and response
///         integrity — no network, no key. Idle timing always uses the real system clock
///         (CancelAfter has no TimeProvider overload), so those cases use a one-second window
///         under a generous outer guard without precise millisecond assertions.
///     </para>
/// </summary>
public class MicrosoftAiChatClientTimeoutTests
{
    private const string RequestModel = "mock-model";

    private static ChatCompletionRequest Request() => new()
    {
        Model    = RequestModel,
        Messages = [ChatMessage.User("probe")]
    };

    /// <summary>以 idle/response 显式期限构造 internal 接缝客户端。</summary>
    private static MicrosoftAiChatClient NewClient(
        IChatClient inner, int idleSeconds, int responseSeconds = 0,
        TimeProvider? timeProvider = null) =>
        new(inner, ChatApiKind.ChatCompletions, null, null,
            new ModelTimeouts
            {
                StreamIdleTimeoutSeconds = idleSeconds, ResponseTimeoutSeconds = responseSeconds
            },
            timeProvider);

    /// <summary>任务书 1：相邻 update 均在空闲窗口内，即使总时长跨窗也必须正常完成。</summary>
    [Test]
    public async Task ConsecutiveUpdatesWithinIdleWindow_CompleteAcrossLongerThanWindow()
    {
        // 10 个文本 update、间隔 150ms：总时长约 1.5 秒超过 1 秒窗口，但每次等待约 150ms。
        var updates = new List<ChatResponseUpdate>();
        for (var index = 0; index < 10; index++) updates.Add(TextUpdate($"chunk-{index}"));
        updates.Add(StopUpdate());
        using var client = NewClient(new DribbleSdkClient(updates, TimeSpan.FromMilliseconds(150)), idleSeconds : 1);

        var events = await CollectGuardedAsync(client);

        Assert.Multiple(() =>
        {
            Assert.That(events.Where(e => e.Kind == ChatStreamEventKind.ContentDelta).Select(e => e.ContentDelta),
                        Is.EqualTo(Enumerable.Range(0, 10).Select(i => $"chunk-{i}")));
            Assert.That(events.Single(e => e.Kind == ChatStreamEventKind.End).FinishReason, Is.EqualTo("stop"));
        });
    }

    /// <summary>任务书 2：首个 update 到来前挂起超过空闲窗口 → 空闲超时，底层令牌已被取消。</summary>
    [Test]
    public async Task FirstWaitExceedingIdleWindow_ThrowsIdleTimeoutAndCancelsUnderlying()
    {
        var gated  = new GatedSdkClient(gateBeforeFirstUpdate : true);
        using var client = NewClient(gated, idleSeconds : 1);

        var enumerator = client.CompleteAsync(Request(), CancellationToken.None).GetAsyncEnumerator();
        var (_, exception) = await MoveObservedGuarded(enumerator);

        Assert.Multiple(() =>
        {
            Assert.That(exception, Is.TypeOf<TimeoutException>());
            TestAssert.Contains("idle", exception!.Message, StringComparison.Ordinal);
            Assert.That(gated.ObservedToken.IsCancellationRequested, Is.True);
        });
    }

    /// <summary>
    ///     <para>任务书 3、10：部分 update 后中途静默 → 空闲超时；底层令牌被传递取消；已收集事件无 End。</para>
    /// </summary>
    [Test]
    public async Task MidStreamSilence_AbortsIdleNotifiesTokenAndYieldsNoEnd()
    {
        var gated  = new GatedSdkClient(gateBeforeFirstUpdate : false);
        using var client = NewClient(gated, idleSeconds : 1);

        var enumerator = client.CompleteAsync(Request(), CancellationToken.None).GetAsyncEnumerator();
        var (events, failure) = await DrainAsync(enumerator);

        Assert.Multiple(() =>
        {
            Assert.That(failure, Is.TypeOf<TimeoutException>());
            TestAssert.Contains("idle", failure!.Message, StringComparison.Ordinal);
            Assert.That(events.Count(e => e.Kind == ChatStreamEventKind.ContentDelta), Is.EqualTo(1));
            Assert.That(events.Any(e => e.Kind == ChatStreamEventKind.End), Is.False);
            Assert.That(gated.ObservedToken.IsCancellationRequested, Is.True);
        });
    }

    /// <summary>
    ///     <para>任务书 4（单元侧）：空 Contents、reasoning、usage 形态的 update 都算进展——
    ///     各形态交替供给使总时长跨过空闲窗口而不得误报。</para>
    /// </summary>
    [Test]
    public async Task NonTextUpdates_CountAsStreamProgressAcrossIdleWindow()
    {
        // 空内容、推理增量与 usage 各两个，加结尾 stop；间隔 150ms，总时长约 1.2 秒超过 1 秒窗口。
        // （反编译实证：只含 StreamingChatToolCallUpdate / usage-only 的 SDK update 同样逐条 yield。）
        List<ChatResponseUpdate> updates =
        [
            EmptyUpdate(), ReasoningUpdate("thinking-1"), UsageUpdate(),
            EmptyUpdate(), ReasoningUpdate("thinking-2"), UsageUpdate(),
            StopUpdate()
        ];
        using var client = NewClient(new DribbleSdkClient(updates, TimeSpan.FromMilliseconds(150)), idleSeconds : 1);

        var events = await CollectGuardedAsync(client);

        Assert.Multiple(() =>
        {
            Assert.That(events.Single(e => e.Kind == ChatStreamEventKind.End).FinishReason, Is.EqualTo("stop"));
            Assert.That(events.Where(e => e.Kind == ChatStreamEventKind.ReasoningDelta)
                             .Select(e => e.ReasoningDelta),
                        Is.EqualTo(new[] { "thinking-1", "thinking-2" }));
            Assert.That(events.Count(e => e.Kind == ChatStreamEventKind.ContentDelta), Is.Zero);
        });
    }

    /// <summary>
    ///     <para>
    ///         任务书 4（真实 SDK 侧）：经 MockSseServer 分块响应驱动真实 OpenAI SDK——纯工具参数
    ///         增量跨多个 SSE 事件（反编译实证：此类 update 会逐条 yield 且 Contents 为空），
    ///         跨越空闲窗口完成而不误伤；M.E.AI 适配器在流正常终止后把完整参数合成进单个
    ///         FunctionCallContent，客户端产出一条携带完整参数的 ToolCallDelta。
    ///     </para>
    /// </summary>
    [Test]
    public async Task ToolCallIncrementsOnRealSdk_CountAsProgressAndAssemble()
    {
        using var server = new MockSseServer();
        server.Start();
        // 每块间隔 250ms、共 6 块：总时长约 1.5 秒超过 idle=1s 窗口，每个 SSE 事件都是一次进展。
        // 参数按完整词元切分（{"path":"x" | }）：拼接后必须是合法 JSON，适配器解析失败会回退空参数。
        server.EnqueueChunked(
        [
            RoleDelta(), ToolCallHead("call_1", "read_file"), ToolArgumentsDelta("{\"path\":\"x\""),
            ToolArgumentsDelta("}"), FinishDelta("tool_calls"), "data: [DONE]\n\n"
        ], TimeSpan.FromMilliseconds(250));
        var client = ModelClientFactory.Create(RequestModel, server.BaseUrl, "test-key",
                                               timeouts : new ModelTimeouts { StreamIdleTimeoutSeconds = 1 });

        var events = await CollectGuardedAsync(client);

        var accumulator = new StreamAccumulator();
        foreach (var @event in events) accumulator.Append(@event);
        accumulator.Finish();

        Assert.Multiple(() =>
        {
            Assert.That(events.Count(e => e.Kind == ChatStreamEventKind.ToolCallDelta), Is.EqualTo(1));
            Assert.That(accumulator.ToolCalls.Count, Is.EqualTo(1));
            Assert.That(accumulator.ToolCalls[0].Id, Is.EqualTo("call_1"));
            Assert.That(accumulator.ToolCalls[0].FunctionName, Is.EqualTo("read_file"));
            Assert.That(accumulator.ToolCalls[0].ArgumentsJson, Is.EqualTo("""{"path":"x"}"""));
            Assert.That(events.Single(e => e.Kind == ChatStreamEventKind.End).FinishReason,
                        Is.EqualTo("tool_calls"));
            Assert.That(server.Requests.Count, Is.EqualTo(1));
        });
    }

    /// <summary>任务书 5：消费端两次读取之间处理 1.5 秒（超过 1 秒窗口）不得误报空闲。</summary>
    [Test]
    public async Task SlowConsumerDoesNotTripIdleGuard()
    {
        // 空闲计时只在每次等待期间武装：消费端处理时间不覆盖，停顿必须发生在 MoveNext 之间。
        var updates = new List<ChatResponseUpdate> { TextUpdate("before"), TextUpdate("after"), StopUpdate() };
        using var client = NewClient(new DribbleSdkClient(updates, TimeSpan.Zero), idleSeconds : 1);

        var enumerator = client.CompleteAsync(Request(), CancellationToken.None).GetAsyncEnumerator();
        var events     = new List<ChatStreamEvent>();

        Assert.That(await MoveGuarded(enumerator), Is.True);
        events.Add(enumerator.Current);
        await Task.Delay(TimeSpan.FromMilliseconds(1500));
        while (await MoveGuarded(enumerator)) events.Add(enumerator.Current);

        Assert.Multiple(() =>
        {
            Assert.That(events.Single(e => e.Kind == ChatStreamEventKind.End).FinishReason, Is.EqualTo("stop"));
            Assert.That(events.Count(e => e.Kind == ChatStreamEventKind.ContentDelta), Is.EqualTo(2));
        });
    }

    /// <summary>任务书 6：idle 关闭、响应总期限经手动时钟确定性到期 → TimeoutException。</summary>
    [Test]
    public async Task ResponseDeadlineExpires_DeterministicallyViaManualClock()
    {
        var provider = new ManualTimeoutProvider();
        var stepped  = new SteppedSdkClient(totalUpdates : 1);
        using var client = NewClient(stepped, idleSeconds : 0, responseSeconds : 5, provider);

        var enumerator = client.CompleteAsync(Request(), CancellationToken.None).GetAsyncEnumerator();

        stepped.ReleaseStep();
        Assert.That(await MoveGuarded(enumerator), Is.True); // ResponseStarted
        Assert.That(await MoveGuarded(enumerator), Is.True); // ContentDelta
        provider.Advance(TimeSpan.FromSeconds(6));
        var (_, exception) = await MoveObservedGuarded(enumerator);

        Assert.Multiple(() =>
        {
            Assert.That(exception, Is.TypeOf<TimeoutException>());
            TestAssert.Contains("response timeout", exception!.Message, StringComparison.Ordinal);
        });
    }

    /// <summary>
    ///     <para>任务书 7：默认无响应总期限——手动时钟无计时器可建，流在虚拟时长累计远超旧
    ///     5 分钟窗口的情况下正常完成。工厂的 NetworkTimeout 语义为每次网络操作保护而非总期限
    ///     （源码依据：ModelClientFactory.PerOperationTimeout 只按空闲窗口换算单操作上限），
    ///     该语义属真实 SDK 传输行为、无法在进程内 fake 中观测，此处以源码依据说明、不强测。</para>
    /// </summary>
    [Test]
    public async Task DefaultResponseTimeoutDisabled_NoDeadlineTimerAndNoEndToEndLimit()
    {
        Assert.That(ModelTimeouts.Default.ResponseTimeoutSeconds, Is.Zero);

        var provider = new ManualTimeoutProvider();
        var stepped  = new SteppedSdkClient(totalUpdates : 4);
        // idle=0 + response=0：全期限关闭，进程内不应创建任何到期计时器。
        using var client = NewClient(stepped, idleSeconds : 0, responseSeconds : 0, provider);

        var enumerator = client.CompleteAsync(Request(), CancellationToken.None).GetAsyncEnumerator();
        var events     = new List<ChatStreamEvent>();
        for (var step = 0; step < 4; step++)
        {
            stepped.ReleaseStep();
            Assert.That(await MoveGuarded(enumerator), Is.True);
            events.Add(enumerator.Current);
            // 每步之间虚拟推进 10 分钟：累计 50 分钟远超旧 5 分钟硬期限而流不受影响。
            provider.Advance(TimeSpan.FromMinutes(10));
        }

        stepped.ReleaseStep();
        while (await MoveGuarded(enumerator)) events.Add(enumerator.Current);

        Assert.Multiple(() =>
        {
            // 响应总期限关闭时不得创建任何计时器。
            Assert.That(provider.TimerCount, Is.Zero);
            Assert.That(events.Count(e => e.Kind == ChatStreamEventKind.ContentDelta), Is.EqualTo(4));
            Assert.That(events.Single(e => e.Kind == ChatStreamEventKind.End).FinishReason, Is.EqualTo("stop"));
        });
    }

    /// <summary>任务书 9a：调用方取消原样传播为 OperationCanceledException，不得转为 TimeoutException。</summary>
    [Test]
    public async Task CallerCancellation_PropagatesAsOperationCanceled()
    {
        // fake 先启动并捕获底层令牌，再取消调用方：确保取消发生在流等待期间。
        var gated  = new GatedSdkClient(gateBeforeFirstUpdate : false);
        using var client = NewClient(gated, idleSeconds : 1);
        using var caller = new CancellationTokenSource();

        var enumerator = client.CompleteAsync(Request(), caller.Token).GetAsyncEnumerator();
        Assert.That(await MoveGuarded(enumerator), Is.True); // ResponseStarted（fake 已启动）
        Assert.That(await MoveGuarded(enumerator), Is.True); // ContentDelta("first")
        await caller.CancelAsync();
        var (_, exception) = await MoveObservedGuarded(enumerator);

        Assert.Multiple(() =>
        {
            Assert.That(exception, Is.InstanceOf<OperationCanceledException>());
            Assert.That(exception, Is.Not.InstanceOf<TimeoutException>());
            Assert.That(gated.ObservedToken.IsCancellationRequested, Is.True);
        });
    }

    /// <summary>
    ///     <para>任务书 9b：fake 注入的非调用方取消（模拟 SCM 每操作超时残余）转 TimeoutException
    ///     且底层消息原样透传（期限未到期、空闲未触发时走透传分类）。</para>
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task InjectedNonCallerCancellation_PassthroughAsTimeoutWithOriginalMessage(
        bool asTaskCancellation)
    {
        const string originalMessage = "scm per-operation read timed out";
        var failing = new FaultingSdkClient(asTaskCancellation
            ? new TaskCanceledException(originalMessage)
            : new OperationCanceledException(originalMessage));
        using var client = NewClient(failing, idleSeconds : 0);

        var exception = await CollectFailureAsync(client);

        Assert.Multiple(() =>
        {
            Assert.That(exception, Is.TypeOf<TimeoutException>());
            Assert.That(exception!.Message, Is.EqualTo(originalMessage));
        });
    }

    /// <summary>任务书 10（响应总期限变体）：期限中断同样不产生 End 事件。</summary>
    [Test]
    public async Task ResponseDeadlineInterruption_YieldsNoEndEvent()
    {
        var provider = new ManualTimeoutProvider();
        var stepped  = new SteppedSdkClient(totalUpdates : 1);
        using var client = NewClient(stepped, idleSeconds : 0, responseSeconds : 5, provider);

        var enumerator = client.CompleteAsync(Request(), CancellationToken.None).GetAsyncEnumerator();
        var events     = new List<ChatStreamEvent>();

        stepped.ReleaseStep();
        Assert.That(await MoveGuarded(enumerator), Is.True); // ResponseStarted
        events.Add(enumerator.Current);
        Assert.That(await MoveGuarded(enumerator), Is.True); // ContentDelta
        events.Add(enumerator.Current);
        provider.Advance(TimeSpan.FromMinutes(6));
        var (_, exception) = await MoveObservedGuarded(enumerator);

        Assert.Multiple(() =>
        {
            Assert.That(exception, Is.TypeOf<TimeoutException>());
            TestAssert.Contains("response timeout", exception!.Message, StringComparison.Ordinal);
            Assert.That(events.Any(e => e.Kind == ChatStreamEventKind.End), Is.False);
        });
    }

    /// <summary>
    ///     <para>任务书 11a：消费端提前退出枚举 → 底层枚举器被释放、DisposeAsync 有界完成、无未观察异常。</para>
    /// </summary>
    [Test]
    public async Task ConsumerDisposalEarly_ReleasesEnumeratorWithoutHanging()
    {
        var gated  = new GatedSdkClient(gateBeforeFirstUpdate : false);
        using var client = NewClient(gated, idleSeconds : 1);
        using var caller = new CancellationTokenSource();

        var enumerator = client.CompleteAsync(Request(), caller.Token).GetAsyncEnumerator();
        Assert.That(await MoveGuarded(enumerator), Is.True); // ResponseStarted
        Assert.That(await MoveGuarded(enumerator), Is.True); // ContentDelta("first")

        // fake 此刻挂起在门上：取消调用方使挂起等待随中止令牌解出，释放路径有界完成。
        await caller.CancelAsync();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await enumerator.DisposeAsync().AsTask().WaitAsync(guard.Token);

        Assert.Multiple(() =>
        {
            Assert.That(gated.EnumeratorDisposed, Is.True);
            Assert.That(gated.ObservedToken.IsCancellationRequested, Is.True);
        });
    }

    /// <summary>
    ///     <para>任务书 11b：update 与空闲到期同刻竞态——窗口边界附近的延迟扰动下，结果只能是
    ///     成功消费或干净的空闲 TimeoutException，不崩溃、单次请求、不重复读取。</para>
    /// </summary>
    [TestCase(850)]
    [TestCase(1000)]
    [TestCase(1150)]
    public async Task UpdateRacingIdleExpiry_EitherCompletesOrTimesOutCleanly(int delayMilliseconds)
    {
        var updates = new List<ChatResponseUpdate> { TextUpdate("race"), StopUpdate() };
        var dribble = new DribbleSdkClient(updates, TimeSpan.FromMilliseconds(delayMilliseconds));
        using var client = NewClient(dribble, idleSeconds : 1);

        var enumerator = client.CompleteAsync(Request(), CancellationToken.None).GetAsyncEnumerator();
        var events     = new List<ChatStreamEvent>();
        Exception? failure = null;
        while (true)
        {
            var (moved, caught) = await MoveObservedGuarded(enumerator);
            if (caught is not null)
            {
                // 中断路径只允许干净的空闲超时。
                TestAssert.Contains("idle", caught.Message, StringComparison.Ordinal);
                failure = caught;
                break;
            }

            if (!moved) break;
            events.Add(enumerator.Current);
            if (events.Any(e => e.Kind == ChatStreamEventKind.End)) break;
        }

        Assert.Multiple(() =>
        {
            Assert.That(failure, Is.Null.Or.InstanceOf<TimeoutException>());
            Assert.That(dribble.Requests, Is.EqualTo(1));
            Assert.That(events.Count(e => e.Kind == ChatStreamEventKind.ContentDelta),
                        Is.LessThanOrEqualTo(1));
        });
    }

    // ---- SSE 帧构造（Chat Completions wire 形态，与真实 SDK 兼容） -----------------------

    private static string Chunk(string deltaJson, string finishReason = "null") =>
        "data: {\"id\":\"chatcmpl-mock\",\"object\":\"chat.completion.chunk\",\"created\":1700000000," +
        $"\"model\":\"mock\",\"choices\":[{{\"index\":0,\"delta\":{deltaJson},\"finish_reason\":{finishReason}}}]}}\n\n";

    private static string RoleDelta() => Chunk("""{"role":"assistant","content":""}""");

    private static string ToolCallHead(string id, string name) =>
        Chunk($"{{\"tool_calls\":[{{\"index\":0,\"id\":\"{id}\",\"type\":\"function\"," +
              $"\"function\":{{\"name\":\"{name}\",\"arguments\":\"\"}}}}]}}");

    private static string ToolArgumentsDelta(string rawFragment) =>
        Chunk($"{{\"tool_calls\":[{{\"index\":0,\"function\":{{\"arguments\":{Json(rawFragment)}}}}}]}}");

    private static string FinishDelta(string finishReason) => Chunk("{}", $"\"{finishReason}\"");

    private static string Json(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    // ---- update 构造 -------------------------------------------------------------------

    private static ChatResponseUpdate TextUpdate(string text) =>
        new(null, new List<AIContent> { new TextContent(text) });

    private static ChatResponseUpdate EmptyUpdate() => new(null, new List<AIContent>());

    private static ChatResponseUpdate ReasoningUpdate(string text) =>
        new(null, new List<AIContent> { new TextReasoningContent(text) });

    private static ChatResponseUpdate UsageUpdate() =>
        new(null, new List<AIContent> { new UsageContent(new UsageDetails()) });

    private static ChatResponseUpdate StopUpdate() =>
        new(null, new List<AIContent>()) { FinishReason = ChatFinishReason.Stop };

    // ---- 守卫辅助 ----------------------------------------------------------------------

    /// <summary>消费整条流，断言期间无异常并返回全部事件（守卫 15 秒）。</summary>
    private static async Task<List<ChatStreamEvent>> CollectGuardedAsync(IChatCompletionClient client)
    {
        var enumerator = client.CompleteAsync(Request(), CancellationToken.None).GetAsyncEnumerator();
        var (events, failure) = await DrainAsync(enumerator);
        Assert.Multiple(() =>
        {
            Assert.That(failure, Is.Null);
            Assert.That(events.Any(e => e.Kind == ChatStreamEventKind.End), Is.True);
        });
        return events;
    }

    /// <summary>消费整条流并返回首个异常实例；null 表示正常完成（守卫 15 秒）。</summary>
    private static async Task<Exception?> CollectFailureAsync(IChatCompletionClient client)
    {
        var enumerator = client.CompleteAsync(Request(), CancellationToken.None).GetAsyncEnumerator();
        var (_, failure) = await DrainAsync(enumerator);
        return failure;
    }

    /// <summary>守卫推进一次枚举并返回结果：Moved=true 表示产出事件，false 表示流正常结束或抛出；
    /// Failure 为等待中捕获的异常（null 表示未抛出），守卫超时即测试失败。</summary>
    private static async Task<(bool Moved, Exception? Failure)> MoveObservedGuarded(
        IAsyncEnumerator<ChatStreamEvent> enumerator)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var moved = await enumerator.MoveNextAsync().AsTask().WaitAsync(guard.Token).ConfigureAwait(false);
            return (moved, null);
        }
        catch (Exception exception) when (!guard.Token.IsCancellationRequested)
        {
            return (false, exception);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("15 秒测试守卫超时：被测实现未按预期完成或抛出。");
            return (false, null);
        }
    }

    /// <summary>守卫推进一次枚举；返回 false 表示流正常结束，守卫超时即测试失败。</summary>
    private static async Task<bool> MoveGuarded(IAsyncEnumerator<ChatStreamEvent> enumerator)
    {
        var (moved, failure) = await MoveObservedGuarded(enumerator);
        Assert.That(failure, Is.Null);
        return moved;
    }

    /// <summary>逐条推进直到流结束或抛出，返回已收集事件与首个异常（流正常结束时为 null）。</summary>
    private static async Task<(List<ChatStreamEvent> Events, Exception? Failure)> DrainAsync(
        IAsyncEnumerator<ChatStreamEvent> enumerator)
    {
        var events  = new List<ChatStreamEvent>();
        Exception? failure = null;
        while (failure is null)
        {
            var (moved, caught) = await MoveObservedGuarded(enumerator);
            if (caught is not null)
            {
                failure = caught;
                break;
            }

            if (!moved) break;
            events.Add(enumerator.Current);
        }

        return (events, failure);
    }

    // ---- fake M.E.AI IChatClient ---------------------------------------------------------

    /// <summary>按固定间隔逐条产出预置 update 的迟交流 fake；间隔发生在 MoveNext 等待期间，可跨空闲窗口探测进展。</summary>
    private sealed class DribbleSdkClient(IReadOnlyList<ChatResponseUpdate> updates, TimeSpan gap) : IChatClient
    {
        public int Requests { get; private set; }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<MicrosoftChatMessage> messages, ChatOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests++;
            foreach (var update in updates)
            {
                if (gap > TimeSpan.Zero) await Task.Delay(gap, cancellationToken).ConfigureAwait(false);
                yield return update;
            }
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<MicrosoftChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken) =>
            throw new NotSupportedException("This fake only supports streaming.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>
    ///     <para>门控 fake：在首个 update 之前或之后挂起于 TCS 门上，供空闲与取消用例驱动；
    ///     记录观察到的取消令牌与底层枚举器释放状态。客户端经返回的 IAsyncEnumerable 调用
    ///     GetAsyncEnumerator，因此释放探针必须包在该返回值上。</para>
    /// </summary>
    private sealed class GatedSdkClient(bool gateBeforeFirstUpdate) : IChatClient
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken ObservedToken { get; private set; }

        public bool EnumeratorDisposed { get; private set; }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<MicrosoftChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken) =>
            new ProbeEnumerable(this, cancellationToken);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<MicrosoftChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken) =>
            throw new NotSupportedException("This fake only supports streaming.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }

        /// <summary>记录令牌并挂起于门上；门打开或流中止令牌触发后解出。</summary>
        private async IAsyncEnumerable<ChatResponseUpdate> Stream(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ObservedToken = cancellationToken;
            if (gateBeforeFirstUpdate)
            {
                // 空闲探测点：门打开或流中止令牌触发前一直挂起。
                await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                yield return TextUpdate("first");
            }
            else
            {
                yield return TextUpdate("first");
                await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                yield return TextUpdate("after-gate");
            }
        }

        private sealed class ProbeEnumerable(GatedSdkClient owner, CancellationToken token) : IAsyncEnumerable<ChatResponseUpdate>
        {
            public IAsyncEnumerator<ChatResponseUpdate> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            {
                var inner = owner.Stream(token).GetAsyncEnumerator(token);
                return new DisposalProbeEnumerator(inner, owner);
            }
        }

        private sealed class DisposalProbeEnumerator(
            IAsyncEnumerator<ChatResponseUpdate> inner, GatedSdkClient owner) : IAsyncEnumerator<ChatResponseUpdate>
        {
            public ChatResponseUpdate Current => inner.Current;

            public ValueTask<bool> MoveNextAsync() => inner.MoveNextAsync();

            public async ValueTask DisposeAsync()
            {
                // 释放状态在等待底层解出前置位：取消解出路径可能吞掉后续标记时机。
                owner.EnumeratorDisposed = true;
                await inner.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    ///     <para>步进 fake：每次 MoveNext 挂起在一个待放行的门上，测试放行一个门即产出下一条
    ///     update；全部文本 update 产出后给出带 FinishReason 的收尾 update 并结束流。供手动时钟
    ///     用例确定性地控制"持续供给 update"的节奏。放行先于挂起时记为额度，由下一个门继承。</para>
    /// </summary>
    private sealed class SteppedSdkClient(int totalUpdates) : IChatClient
    {
        private readonly Queue<TaskCompletionSource> _pendingSteps = new();
        private readonly Lock _lock = new();
        private int _yielded;
        private int _releasedCredits;

        /// <summary>放行一个门：优先完成最早挂起的门，否则记为额度供下一个创建的门直接完成。</summary>
        public void ReleaseStep()
        {
            TaskCompletionSource? step;
            lock (_lock)
            {
                if (_pendingSteps.Count > 0)
                {
                    step = _pendingSteps.Dequeue();
                }
                else
                {
                    _releasedCredits++;
                    step = null;
                }
            }

            step?.TrySetResult();
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<MicrosoftChatMessage> messages, ChatOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            while (true)
            {
                TaskCompletionSource step;
                lock (_lock)
                {
                    if (_releasedCredits > 0)
                    {
                        // 消费预放行额度：门直接置为完成态。
                        _releasedCredits--;
                        step = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        step.TrySetResult();
                    }
                    else
                    {
                        _pendingSteps.Enqueue(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                        step = _pendingSteps.Dequeue();
                    }
                }

                await step.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                _yielded++;
                if (_yielded <= totalUpdates)
                {
                    yield return TextUpdate($"step-{_yielded}");
                }
                else
                {
                    yield return StopUpdate();
                    yield break;
                }
            }
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<MicrosoftChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken) =>
            throw new NotSupportedException("This fake only supports streaming.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>在首个等待中抛出注入异常的 fake；用于模拟 SCM 每操作超时残余等非调用方取消。</summary>
    private sealed class FaultingSdkClient(Exception thrown) : IChatClient
    {
        public CancellationToken ObservedToken { get; private set; }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<MicrosoftChatMessage> messages, ChatOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // 先产出一条 update（满足 async-iterator 语法），下一次等待中抛出注入异常。
            ObservedToken = cancellationToken;
            yield return TextUpdate("pre");
            await Task.Yield();
            throw thrown;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<MicrosoftChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken) =>
            throw new NotSupportedException("This fake only supports streaming.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>
    ///     <para>手动时钟：计时器到期由 Advance 按虚拟时间确定性触发（参照 RunBudgetTests 的
    ///     ElapsedTimeProvider 手写惯例，不引入 NuGet 包）。响应总期限经
    ///     new CancellationTokenSource(TimeSpan, TimeProvider) 使用该时钟。</para>
    /// </summary>
    internal sealed class ManualTimeoutProvider : TimeProvider
    {
        private readonly Lock _lock = new();
        private readonly List<Entry> _timers = [];
        private long _nowTicks;

        private sealed class Entry(ManualTimer timer, TimerCallback callback, object? state)
        {
            public ManualTimer Timer { get; } = timer;

            public TimerCallback Callback { get; } = callback;

            public object? State { get; } = state;

            public long DueTicks;
            public bool Fired;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_lock)
            {
                var entry = new Entry(new ManualTimer(this), callback, state)
                {
                    DueTicks = _nowTicks + dueTime.Ticks
                };
                _timers.Add(entry);
                return entry.Timer;
            }
        }

        public int TimerCount
        {
            get
            {
                lock (_lock) return _timers.Count;
            }
        }

        /// <summary>推进虚拟时钟并同步触发所有到期的计时器回调。</summary>
        public void Advance(TimeSpan delta)
        {
            List<Entry> due;
            lock (_lock)
            {
                _nowTicks += delta.Ticks;
                due = _timers.Where(t => !t.Fired && t.DueTicks <= _nowTicks).ToList();
                due.ForEach(t => t.Fired = true);
            }

            foreach (var entry in due) entry.Callback(entry.State);
        }

        private bool Reschedule(ManualTimer timer, TimeSpan dueTime)
        {
            lock (_lock)
            {
                var entry = _timers.SingleOrDefault(t => t.Timer == timer);
                if (entry is null) return false;
                if (dueTime.Ticks < 0)
                {
                    // -1 表示解除计时器（CTS 解除到期源的用法）。
                    _timers.Remove(entry);
                    return true;
                }

                entry.Fired    = false;
                entry.DueTicks = _nowTicks + dueTime.Ticks;
                return true;
            }
        }

        private void Remove(ManualTimer timer)
        {
            lock (_lock)
            {
                var entry = _timers.SingleOrDefault(t => t.Timer == timer);
                if (entry is not null) _timers.Remove(entry);
            }
        }

        private sealed class ManualTimer(ManualTimeoutProvider owner) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => owner.Reschedule(this, dueTime);

            public void Dispose() => owner.Remove(this);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
