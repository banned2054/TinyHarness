using System.Runtime.CompilerServices;
using TinyHarness.Core.ChatCompletions;

namespace TinyHarness.Tests;

/// <summary>
/// Scripted fake model client. Each request is answered from a queue of prebuilt
/// event sequences, letting tests drive the Agent loop without a network or key.
/// </summary>
internal sealed class FakeChatClient : IChatCompletionClient
{
    private readonly Queue<IReadOnlyList<ChatStreamEvent>> _responses = new();

    private readonly List<ChatCompletionRequest> _requestLog = [];

    public int Requests { get; private set; }

    public string? LastRequestModel { get; private set; }

    /// <summary>
    /// 每次 <see cref="CompleteAsync"/> 收到的请求快照（按调用顺序），供测试断言请求形状。
    /// Every request observed by <see cref="CompleteAsync"/>, in call order, for shape assertions.
    /// </summary>
    public IReadOnlyList<ChatCompletionRequest> RequestLog => _requestLog;

    /// <summary>
    /// Optional callback invoked after the first event is yielded, so a test can
    /// cancel the token mid-stream to exercise cancellation during enumeration.
    /// </summary>
    public Action? CancelMidStream { get; set; }

    /// <summary>A scripted completion made of one or more content fragments.</summary>
    public static IReadOnlyList<ChatStreamEvent> Text(params string[] chunks) => chunks
       .Select(c => new ChatStreamEvent { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = c })
       .Append(new ChatStreamEvent { Kind = ChatStreamEventKind.End }).ToArray();

    /// <summary>A scripted completion containing a single tool call.</summary>
    public static IReadOnlyList<ChatStreamEvent> ToolCall(string name, string argumentsJson, string? id = "call_1")
    {
        const int half   = 2;
        var       events = new List<ChatStreamEvent>();

        if (argumentsJson.Length > half)
        {
            // Split the JSON across deltas to exercise fragment assembly.
            var mid = argumentsJson.Length / 2;
            events.Add(new ChatStreamEvent
            {
                Kind                   = ChatStreamEventKind.ToolCallDelta,
                ToolCallIndex          = 0,
                ToolCallId             = id,
                ToolCallFunctionName   = name,
                ToolCallArgumentsDelta = argumentsJson[..mid],
            });
            events.Add(new ChatStreamEvent
            {
                Kind                   = ChatStreamEventKind.ToolCallDelta,
                ToolCallIndex          = 0,
                ToolCallArgumentsDelta = argumentsJson[mid..],
            });
        }
        else
        {
            events.Add(new ChatStreamEvent
            {
                Kind                   = ChatStreamEventKind.ToolCallDelta,
                ToolCallIndex          = 0,
                ToolCallId             = id,
                ToolCallFunctionName   = name,
                ToolCallArgumentsDelta = argumentsJson,
            });
        }

        events.Add(new ChatStreamEvent { Kind = ChatStreamEventKind.End });
        return events;
    }

    /// <summary>脚本化一个包含多个工具调用的 assistant 回合。</summary>
    public static IReadOnlyList<ChatStreamEvent> ToolCalls(string name, int count, string idPrefix,
                                                           string argumentsJson = "{}")
    {
        var events = new List<ChatStreamEvent>(count + 1);
        for (var index = 1; index <= count; index++)
        {
            events.Add(new ChatStreamEvent
            {
                Kind                   = ChatStreamEventKind.ToolCallDelta,
                ToolCallIndex          = index - 1,
                ToolCallId             = $"{idPrefix}_{index}",
                ToolCallFunctionName   = name,
                ToolCallArgumentsDelta = argumentsJson,
            });
        }

        events.Add(new ChatStreamEvent { Kind = ChatStreamEventKind.End });
        return events;
    }

    public void Enqueue(IReadOnlyList<ChatStreamEvent> response) => _responses.Enqueue(response);

    public async IAsyncEnumerable<ChatStreamEvent> CompleteAsync(ChatCompletionRequest request,
                                                                 [EnumeratorCancellation]
                                                                 CancellationToken cancellationToken)
    {
        Requests++;
        LastRequestModel = request.Model;
        _requestLog.Add(request);

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException("No scripted response for the request.");
        }

        var first = true;
        foreach (var item in _responses.Dequeue())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
            if (first)
            {
                first = false;
                CancelMidStream?.Invoke();
            }

            await Task.Yield();
        }
    }
}
