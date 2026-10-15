using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Services.ChatCompletions;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         StreamAccumulator 对推理增量、推理条目边界与 usage/结束元数据累积的单元测试，
///         使用合成事件驱动。
///     </para>
///     <para>
///         Unit tests for StreamAccumulator's handling of reasoning deltas, reasoning
///         item boundaries, and usage/finish metadata, driven by synthetic events.
///     </para>
/// </summary>
public class StreamAccumulatorReasoningTests
{
    private static ChatStreamEvent ReasoningDelta(string text, string? itemId = null) => new()
    {
        Kind            = ChatStreamEventKind.ReasoningDelta,
        ReasoningDelta  = text,
        ReasoningItemId = itemId
    };

    private static ChatStreamEvent ReasoningItem(
        string? protectedData = null, string? itemId = null, string? text = null) => new()
    {
        Kind                   = ChatStreamEventKind.ReasoningItem,
        ReasoningProtectedData = protectedData,
        ReasoningItemId        = itemId,
        // 条目事件经 ReasoningDelta 字段携带条目完整文本。
        // item 事件经 ReasoningDelta 字段携带条目完整文本。
        ReasoningDelta = text
    };

    private static StreamAccumulator Accumulate(params ChatStreamEvent[] events)
    {
        var accumulator = new StreamAccumulator();
        foreach (var @event in events) accumulator.Append(@event);

        return accumulator;
    }

    [Test]
    public void ReasoningDeltas_AccumulateIntoOneEntryOnFinish()
    {
        var accumulator = Accumulate(ReasoningDelta("think "), ReasoningDelta("hard"),
                                     new ChatStreamEvent { Kind = ChatStreamEventKind.End });
        accumulator.Finish();

        var entry = TestAssert.Single(accumulator.Reasoning!);
        Assert.That(entry.Text, Is.EqualTo("think hard"));
        Assert.That(entry.ProtectedData, Is.Null);
        Assert.That(entry.ItemId, Is.Null);
    }

    [Test]
    public void ReasoningItemEvent_ClosesCurrentEntryWithItsIdentifiers()
    {
        // 条目事件终止的是"当前累积中"的条目：加密载荷与 item id 归属被关闭的条目，
        // 其后的增量进入新条目。
        // item 事件终结正在累积的条目：加密载荷与 item id 属于已闭合条目；后续增量
        // 进入新条目。
        var accumulator = Accumulate(ReasoningDelta("first "),
                                     ReasoningItem("opaque-1", "rs_1"),
                                     ReasoningDelta("second"),
                                     new ChatStreamEvent { Kind = ChatStreamEventKind.End });
        accumulator.Finish();

        Assert.That(accumulator.Reasoning!.Count, Is.EqualTo(2));
        Assert.That(accumulator.Reasoning[0].Text, Is.EqualTo("first "));
        Assert.That(accumulator.Reasoning[0].ProtectedData, Is.EqualTo("opaque-1"));
        Assert.That(accumulator.Reasoning[0].ItemId, Is.EqualTo("rs_1"));
        Assert.That(accumulator.Reasoning[1].Text, Is.EqualTo("second"));
        Assert.That(accumulator.Reasoning[1].ProtectedData, Is.Null);
        Assert.That(accumulator.Reasoning[1].ItemId, Is.Null);
    }

    [Test]
    public void ReasoningDeltas_WithDistinctItemIds_SplitIntoSeparateEntries()
    {
        // 多条目按 itemId 分界：无显式条目事件时，不同 item id 的增量各自成条目。
        // 条目按 item id 划分：没有显式 item 事件时，不同 id 的增量各自成条。
        var accumulator = Accumulate(ReasoningDelta("think ", "rs_1"),
                                     ReasoningDelta("hard", "rs_1"),
                                     ReasoningDelta("again", "rs_2"),
                                     new ChatStreamEvent { Kind = ChatStreamEventKind.End });
        accumulator.Finish();

        Assert.That(accumulator.Reasoning!.Count, Is.EqualTo(2));
        Assert.That(accumulator.Reasoning[0].Text, Is.EqualTo("think hard"));
        Assert.That(accumulator.Reasoning[0].ProtectedData, Is.Null);
        Assert.That(accumulator.Reasoning[0].ItemId, Is.EqualTo("rs_1"));
        Assert.That(accumulator.Reasoning[1].Text, Is.EqualTo("again"));
        Assert.That(accumulator.Reasoning[1].ItemId, Is.EqualTo("rs_2"));
    }

    [Test]
    public void ItemEventText_DuplicatingStreamedDeltas_IsKeptOnce()
    {
        // done 事件的完整文本与已流出增量重复时，以条目为单位只保留一份完整文本。
        // item 事件的完整文本与流式增量重复时，每条只保留一份完整文本。
        var accumulator = Accumulate(ReasoningDelta("think ", "rs_1"),
                                     ReasoningDelta("hard", "rs_1"),
                                     ReasoningItem("opaque", "rs_1", "think hard"),
                                     new ChatStreamEvent { Kind = ChatStreamEventKind.End });
        accumulator.Finish();

        var entry = TestAssert.Single(accumulator.Reasoning!);
        Assert.That(entry.Text, Is.EqualTo("think hard"));
        Assert.That(entry.ProtectedData, Is.EqualTo("opaque"));
        Assert.That(entry.ItemId, Is.EqualTo("rs_1"));
    }

    [Test]
    public void ItemEventWithNeitherTextNorIdentifiers_ProducesNoEntry()
    {
        // 空文本且无任何不透明标识的条目事件不产出条目。
        // 既无文本也无 opaque 标识的 item 事件不产生条目。
        var accumulator = Accumulate(ReasoningItem(),
                                     new ChatStreamEvent { Kind = ChatStreamEventKind.End });
        accumulator.Finish();

        Assert.That(accumulator.Reasoning, Is.Null);
    }

    [Test]
    public void ReasoningItemWithoutText_IsStillCarriedForContextReplay()
    {
        // 仅携带不透明标识的条目也必须保留：续接推理上下文依赖它们原样回传。
        // 只携带 opaque 标识的条目同样保留：延续 reasoning 上下文依赖原样回放它们。
        var accumulator = Accumulate(ReasoningItem("opaque", "rs_1"),
                                     new ChatStreamEvent { Kind = ChatStreamEventKind.End });
        accumulator.Finish();

        var entry = TestAssert.Single(accumulator.Reasoning!);
        Assert.That(entry.Text, Is.Null);
        Assert.That(entry.ProtectedData, Is.EqualTo("opaque"));
        Assert.That(entry.ItemId, Is.EqualTo("rs_1"));
    }

    [Test]
    public void WithoutReasoningEvents_ReasoningStaysNullAndContentIsUntouched()
    {
        var accumulator = Accumulate(new ChatStreamEvent
                                         { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = "answer" },
                                     new ChatStreamEvent { Kind = ChatStreamEventKind.End });
        accumulator.Finish();

        Assert.That(accumulator.Reasoning, Is.Null);
        Assert.That(accumulator.Content, Is.EqualTo("answer"));
    }

    [Test]
    public void UsageAndEndEvents_FillTokenCountsAndFinishReason_FirstValueWins()
    {
        var accumulator = Accumulate(new ChatStreamEvent
                                         { Kind = ChatStreamEventKind.Usage, InputTokens = 10, OutputTokens = 4 },
                                     new ChatStreamEvent
                                     {
                                         Kind         = ChatStreamEventKind.End,
                                         InputTokens  = 99,
                                         OutputTokens = 5,
                                         FinishReason = "stop"
                                     });

        Assert.That(accumulator.InputTokens, Is.EqualTo(10));
        Assert.That(accumulator.OutputTokens, Is.EqualTo(4));
        Assert.That(accumulator.FinishReason, Is.EqualTo("stop"));
    }

    [Test]
    public void MetadataAlone_LeavesNullableCountersNull()
    {
        var accumulator = Accumulate(new ChatStreamEvent
                                         { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = "hi" },
                                     new ChatStreamEvent { Kind = ChatStreamEventKind.End });

        accumulator.Finish();

        Assert.That(accumulator.InputTokens, Is.Null);
        Assert.That(accumulator.OutputTokens, Is.Null);
        Assert.That(accumulator.FinishReason, Is.Null);
    }

    [Test]
    public void FinishStillValidatesToolCallsAlongsideReasoning()
    {
        // 向后兼容：推理字段不改变 Finish 对工具调用名称与参数 JSON 的校验。
        // reasoning 字段不改变 Finish 对工具调用名称与参数 JSON 的校验。
        var accumulator = Accumulate(ReasoningDelta("thinking"),
                                     new ChatStreamEvent
                                     {
                                         Kind                   = ChatStreamEventKind.ToolCallDelta,
                                         ToolCallIndex          = 0,
                                         ToolCallId             = "call_1",
                                         ToolCallFunctionName   = "read_file",
                                         ToolCallArgumentsDelta = """{"path":"a"}"""
                                     },
                                     new ChatStreamEvent { Kind = ChatStreamEventKind.End });
        accumulator.Finish();

        var call = TestAssert.Single(accumulator.ToolCalls);
        Assert.That(call.FunctionName, Is.EqualTo("read_file"));
        Assert.That(TestAssert.Single(accumulator.Reasoning!).Text, Is.EqualTo("thinking"));
    }
}
