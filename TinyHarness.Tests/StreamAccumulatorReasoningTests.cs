using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Services.ChatCompletions;

namespace TinyHarness.Tests;

/// <summary>
///     StreamAccumulator 对推理增量、推理条目边界与 usage/结束元数据累积的单元测试，
///     使用合成事件驱动（Responses 协议接入前先固化承载行为）。
///     Unit tests for StreamAccumulator's handling of reasoning deltas, reasoning
///     item boundaries, and usage/finish metadata, driven by synthetic events
///     (the carrying behavior is fixed before the Responses protocol lands).
/// </summary>
public class StreamAccumulatorReasoningTests
{
    private static ChatStreamEvent ReasoningDelta(string text, string? itemId = null)
    {
        return new ChatStreamEvent
        {
            Kind            = ChatStreamEventKind.ReasoningDelta,
            ReasoningDelta  = text,
            ReasoningItemId = itemId
        };
    }

    private static ChatStreamEvent ReasoningItem(string? protectedData = null, string? itemId = null,
                                                 string? text          = null)
    {
        return new ChatStreamEvent
        {
            Kind                   = ChatStreamEventKind.ReasoningItem,
            ReasoningProtectedData = protectedData,
            ReasoningItemId        = itemId,
            // 条目事件经 ReasoningDelta 字段携带条目完整文本。
            // Item events carry the entry's complete text via the ReasoningDelta field.
            ReasoningDelta = text
        };
    }

    private static StreamAccumulator Accumulate(params ChatStreamEvent[] events)
    {
        var accumulator = new StreamAccumulator();
        foreach (var @event in events) accumulator.Append(@event);

        return accumulator;
    }

    [Fact]
    public void ReasoningDeltas_AccumulateIntoOneEntryOnFinish()
    {
        var accumulator = Accumulate(ReasoningDelta("think "), ReasoningDelta("hard"),
                                     new ChatStreamEvent { Kind = ChatStreamEventKind.End });
        accumulator.Finish();

        var entry = Assert.Single(accumulator.Reasoning!);
        Assert.Equal("think hard", entry.Text);
        Assert.Null(entry.ProtectedData);
        Assert.Null(entry.ItemId);
    }

    [Fact]
    public void ReasoningItemEvent_ClosesCurrentEntryWithItsIdentifiers()
    {
        // 条目事件终止的是"当前累积中"的条目：加密载荷与 item id 归属被关闭的条目，
        // 其后的增量进入新条目。
        // The item event terminates the entry being accumulated: the encrypted
        // payload and item id belong to the closed entry; later deltas go to a
        // new one.
        var accumulator = Accumulate(
                                     ReasoningDelta("first "),
                                     ReasoningItem("opaque-1", "rs_1"),
                                     ReasoningDelta("second"),
                                     new ChatStreamEvent { Kind = ChatStreamEventKind.End });
        accumulator.Finish();

        Assert.Equal(2, accumulator.Reasoning!.Count);
        Assert.Equal("first ", accumulator.Reasoning[0].Text);
        Assert.Equal("opaque-1", accumulator.Reasoning[0].ProtectedData);
        Assert.Equal("rs_1", accumulator.Reasoning[0].ItemId);
        Assert.Equal("second", accumulator.Reasoning[1].Text);
        Assert.Null(accumulator.Reasoning[1].ProtectedData);
        Assert.Null(accumulator.Reasoning[1].ItemId);
    }

    [Fact]
    public void ReasoningDeltas_WithDistinctItemIds_SplitIntoSeparateEntries()
    {
        // 多条目按 itemId 分界：无显式条目事件时，不同 item id 的增量各自成条目。
        // Items are delimited by item id: without explicit item events, deltas
        // with distinct ids each form their own entry.
        var accumulator = Accumulate(
                                     ReasoningDelta("think ", "rs_1"),
                                     ReasoningDelta("hard", "rs_1"),
                                     ReasoningDelta("again", "rs_2"),
                                     new ChatStreamEvent { Kind = ChatStreamEventKind.End });
        accumulator.Finish();

        Assert.Equal(2, accumulator.Reasoning!.Count);
        Assert.Equal("think hard", accumulator.Reasoning[0].Text);
        Assert.Null(accumulator.Reasoning[0].ProtectedData);
        Assert.Equal("rs_1", accumulator.Reasoning[0].ItemId);
        Assert.Equal("again", accumulator.Reasoning[1].Text);
        Assert.Equal("rs_2", accumulator.Reasoning[1].ItemId);
    }

    [Fact]
    public void ItemEventText_DuplicatingStreamedDeltas_IsKeptOnce()
    {
        // done 事件的完整文本与已流出增量重复时，以条目为单位只保留一份完整文本。
        // When the item event's complete text duplicates the streamed deltas,
        // exactly one copy of the complete text is kept per entry.
        var accumulator = Accumulate(
                                     ReasoningDelta("think ", "rs_1"),
                                     ReasoningDelta("hard", "rs_1"),
                                     ReasoningItem("opaque", "rs_1", "think hard"),
                                     new ChatStreamEvent { Kind = ChatStreamEventKind.End });
        accumulator.Finish();

        var entry = Assert.Single(accumulator.Reasoning!);
        Assert.Equal("think hard", entry.Text);
        Assert.Equal("opaque", entry.ProtectedData);
        Assert.Equal("rs_1", entry.ItemId);
    }

    [Fact]
    public void ItemEventWithNeitherTextNorIdentifiers_ProducesNoEntry()
    {
        // 空文本且无任何不透明标识的条目事件不产出条目。
        // An item event with neither text nor any opaque identifier produces no entry.
        var accumulator = Accumulate(
                                     ReasoningItem(),
                                     new ChatStreamEvent { Kind = ChatStreamEventKind.End });
        accumulator.Finish();

        Assert.Null(accumulator.Reasoning);
    }

    [Fact]
    public void ReasoningItemWithoutText_IsStillCarriedForContextReplay()
    {
        // 仅携带不透明标识的条目也必须保留：续接推理上下文依赖它们原样回传。
        // Entries carrying only opaque identifiers are kept too: continuing the
        // reasoning context depends on replaying them verbatim.
        var accumulator = Accumulate(
                                     ReasoningItem("opaque", "rs_1"),
                                     new ChatStreamEvent { Kind = ChatStreamEventKind.End });
        accumulator.Finish();

        var entry = Assert.Single(accumulator.Reasoning!);
        Assert.Null(entry.Text);
        Assert.Equal("opaque", entry.ProtectedData);
        Assert.Equal("rs_1", entry.ItemId);
    }

    [Fact]
    public void WithoutReasoningEvents_ReasoningStaysNullAndContentIsUntouched()
    {
        var accumulator = Accumulate(
                                     new ChatStreamEvent
                                         { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = "answer" },
                                     new ChatStreamEvent { Kind = ChatStreamEventKind.End });
        accumulator.Finish();

        Assert.Null(accumulator.Reasoning);
        Assert.Equal("answer", accumulator.Content);
    }

    [Fact]
    public void UsageAndEndEvents_FillTokenCountsAndFinishReason_FirstValueWins()
    {
        var accumulator = Accumulate(
                                     new ChatStreamEvent
                                         { Kind = ChatStreamEventKind.Usage, InputTokens = 10, OutputTokens = 4 },
                                     new ChatStreamEvent
                                     {
                                         Kind         = ChatStreamEventKind.End,
                                         InputTokens  = 99,
                                         OutputTokens = 5,
                                         FinishReason = "stop"
                                     });

        Assert.Equal(10, accumulator.InputTokens);
        Assert.Equal(4, accumulator.OutputTokens);
        Assert.Equal("stop", accumulator.FinishReason);
    }

    [Fact]
    public void MetadataAlone_LeavesNullableCountersNull()
    {
        var accumulator = Accumulate(
                                     new ChatStreamEvent
                                         { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = "hi" },
                                     new ChatStreamEvent { Kind = ChatStreamEventKind.End });

        accumulator.Finish();

        Assert.Null(accumulator.InputTokens);
        Assert.Null(accumulator.OutputTokens);
        Assert.Null(accumulator.FinishReason);
    }

    [Fact]
    public void FinishStillValidatesToolCallsAlongsideReasoning()
    {
        // 向后兼容：推理字段不改变 Finish 对工具调用名称与参数 JSON 的校验。
        // Backward compatibility: reasoning fields do not change Finish's
        // validation of tool-call names and argument JSON.
        var accumulator = Accumulate(
                                     ReasoningDelta("thinking"),
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

        var call = Assert.Single(accumulator.ToolCalls);
        Assert.Equal("read_file", call.FunctionName);
        Assert.Equal("thinking", Assert.Single(accumulator.Reasoning!).Text);
    }
}
