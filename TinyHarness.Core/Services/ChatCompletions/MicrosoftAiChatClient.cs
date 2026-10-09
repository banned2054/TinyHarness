using Microsoft.Extensions.AI;
using OpenAI.Responses;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatRole = Microsoft.Extensions.AI.ChatRole;
using ChatToolCall = TinyHarness.Core.Models.ChatCompletions.ChatToolCall;
using InternalChatRole = TinyHarness.Core.Models.ChatCompletions.ChatRole;

namespace TinyHarness.Core.Services.ChatCompletions;

/// <summary>
///     基于 Microsoft.Extensions.AI <see cref="IChatClient" /> 的流式客户端，覆盖 Chat Completions 与
///     Responses 两种协议。它把项目内部请求映射为 M.E.AI 消息与 <see cref="ChatOptions" />，再将文本、
///     工具调用增量、推理内容与流元数据转换回内部事件。协议适配的验证探针见 artifacts/protocol-compat。
///     Streaming client backed by a Microsoft.Extensions.AI <see cref="IChatClient" />, covering both
///     the Chat Completions and the Responses protocol. It maps internal requests to M.E.AI
///     messages and <see cref="ChatOptions" />, and converts text, tool-call deltas, reasoning
///     content, and stream metadata back into internal events. See artifacts/protocol-compat
///     for the protocol compatibility probes.
/// </summary>
public sealed class MicrosoftAiChatClient : IChatCompletionClient, IDisposable
{
    /// <summary>
    ///     M.E.AI.OpenAI Responses 适配器存放 reasoning item id 的 AdditionalProperties 键，
    ///     回传时由其按该键重建 {type:"reasoning", id, encrypted_content} 条目。出处：
    ///     artifacts/protocol-compat/source/OpenAIResponsesChatClient.cs 的 ReasoningItemIdKey。
    ///     The AdditionalProperties key under which the M.E.AI.OpenAI Responses adapter
    ///     stashes the reasoning item id and later rebuilds the
    ///     {type:"reasoning", id, encrypted_content} item on replay. Source:
    ///     ReasoningItemIdKey in artifacts/protocol-compat/source/OpenAIResponsesChatClient.cs.
    /// </summary>
    private const string ReasoningItemIdKey = "reasoningItemId";

    private readonly ChatApiKind _api;

    private readonly IChatClient _inner;

    /// <summary>
    ///     包装一个已配置好的 M.E.AI 客户端；本实例负责其释放。<paramref name="api" /> 决定协议感知
    ///     行为（Responses 无服务端会话状态、reasoning 注入/回传），缺省 Chat Completions。
    ///     Wraps a configured M.E.AI client; this instance owns its disposal.
    ///     <paramref name="api" /> selects the protocol-aware behavior (stateless
    ///     Responses requests, reasoning injection/replay); it defaults to Chat Completions.
    /// </summary>
    public MicrosoftAiChatClient(IChatClient inner, ChatApiKind api = ChatApiKind.ChatCompletions)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _api   = api;
    }

    /// <summary>
    ///     将内部请求转换为 M.E.AI 输入，并把 M.E.AI 的文本与工具调用增量映射为项目流事件。
    ///     Converts the internal request to M.E.AI input and maps M.E.AI text/tool
    ///     deltas back to project stream events.
    /// </summary>
    public async IAsyncEnumerable<ChatStreamEvent> CompleteAsync(ChatCompletionRequest request,
                                                                 [EnumeratorCancellation]
                                                                 CancellationToken cancellationToken)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        var messages = ToMicrosoftMessages(request);
        var options  = ToChatOptions(request);

        // The M.E.AI/OpenAI stack surfaces HTTP errors and malformed streams as
        // ClientModelException/ClientResultException whose message already
        // carries the HTTP status. They intentionally propagate to the Agent
        // Loop boundary, which converts them into a Failed result with that
        // context; nothing is swallowed here. (A C# iterator cannot yield
        // inside a try-with-catch.)
        var callIndexes   = new Dictionary<string, int>(StringComparer.Ordinal);
        var nextCallIndex = 0;

        // usage 与结束原因按流内增量捕获，不再为流末聚合而驻留全部 update：结束原因取
        // 最后一个非空值；usage 以 update.Contents 中的 UsageContent 计数累加——
        // ChatResponseUpdate 没有 Usage 属性，聚合语义与此前 ToChatResponse 路径一致
        //（探针验证：多段 usage 计数相加）。
        //
        // Usage and the finish reason are captured incrementally in-stream instead
        // of holding every update for an end-of-stream aggregation: the finish
        // reason keeps the last non-null value; usage adds the counts of the
        // UsageContent entries inside update.Contents — ChatResponseUpdate has
        // no Usage property — with the same semantics as the previous
        // ToChatResponse path (probe-verified: multiple usage chunks add up).
        long?             inputTokens  = null;
        long?             outputTokens = null;
        ChatFinishReason? finishReason = null;

        await foreach (var update in _inner
                                    .GetStreamingResponseAsync(messages, options, cancellationToken)
                                    .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (update.FinishReason is { } reason) finishReason = reason;

            foreach (var content in update.Contents)
                switch (content)
                {
                    // usage 以 UsageContent 形式随流到达，仅累加非空计数后落在 End 事件上。
                    // Usage arrives in-stream as a UsageContent; only non-null counts
                    // accumulate and land on the End event.
                    case UsageContent usage :
                        if (usage.Details.InputTokenCount is { } input) inputTokens = (inputTokens ?? 0) + input;

                        if (usage.Details.OutputTokenCount is { } output) outputTokens = (outputTokens ?? 0) + output;

                        break;

                    // Only plain text maps onto ContentDelta; content kinds beyond
                    // the MVP protocol (audio, images) are, like the previous SDK
                    // client, not surfaced here. Reasoning is carried by the
                    // Responses protocol events below.
                    case TextContent text when !string.IsNullOrEmpty(text.Text) :
                        yield return new ChatStreamEvent
                        {
                            Kind         = ChatStreamEventKind.ContentDelta,
                            ContentDelta = text.Text
                        };
                        break;

                    // Responses reasoning: streaming deltas carry item-scoped text
                    // without ProtectedData; the item-done update carries the
                    // encrypted payload (and, per the adapter source, a null or
                    // full text). Both shapes map onto the reasoning events.
                    case TextReasoningContent reasoning when reasoning.ProtectedData is not null :
                        yield return new ChatStreamEvent
                        {
                            Kind                   = ChatStreamEventKind.ReasoningItem,
                            ReasoningProtectedData = reasoning.ProtectedData,
                            ReasoningItemId        = ReasoningItemIdOf(reasoning),
                            // 在 ReasoningItem 事件里该字段承载条目的完整文本，可能与已流出的
                            // 增量重复；累加器以条目为界去重。
                            // On a ReasoningItem event this field carries the item's
                            // complete text, possibly duplicating already streamed
                            // deltas; the accumulator dedups per item boundary.
                            ReasoningDelta = reasoning.Text
                        };
                        break;

                    case TextReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text) :
                        yield return new ChatStreamEvent
                        {
                            Kind            = ChatStreamEventKind.ReasoningDelta,
                            ReasoningDelta  = reasoning.Text,
                            ReasoningItemId = ReasoningItemIdOf(reasoning)
                        };
                        break;

                    case FunctionCallContent call :
                        // The M.E.AI adapter assembles wire fragments itself and
                        // emits each call once, complete; the arguments are
                        // therefore serialized as one full fragment, which the
                        // accumulator's per-index merge handles natively.
                        yield return new ChatStreamEvent
                        {
                            Kind                   = ChatStreamEventKind.ToolCallDelta,
                            ToolCallIndex          = IndexForCall(callIndexes, call.CallId, ref nextCallIndex),
                            ToolCallId             = string.IsNullOrEmpty(call.CallId) ? null : call.CallId,
                            ToolCallFunctionName   = string.IsNullOrEmpty(call.Name) ? null : call.Name,
                            ToolCallArgumentsDelta = SerializeArguments(call.CallId, call.Arguments)
                        };
                        break;
                }
        }

        // 流结束：把增量捕获的 usage 与结束原因放到唯一的 End 事件上；usage 缺失时保持 null。
        // Stream end: the incrementally captured usage and finish reason land on the
        // single End event; a missing usage stays null.
        yield return new ChatStreamEvent
        {
            Kind         = ChatStreamEventKind.End,
            FinishReason = finishReason?.ToString(),
            InputTokens  = inputTokens is { } inputTokenCount ? checked((int)inputTokenCount) : null,
            OutputTokens = outputTokens is { } outputTokenCount ? checked((int)outputTokenCount) : null
        };
    }

    /// <summary>
    ///     M.E.AI 客户端实现 IDisposable；<see cref="IChatCompletionClient" /> 本身无释放语义，
    ///     持有完整生命周期的调用方（如测试）应通过具体类型释放。
    ///     The M.E.AI client implements IDisposable; <see cref="IChatCompletionClient" /> itself
    ///     carries no dispose semantics, so owners of the full lifetime (e.g. tests)
    ///     dispose through the concrete type.
    /// </summary>
    public void Dispose()
    {
        _inner.Dispose();
    }

    /// <summary>
    ///     为每个工具调用分配稳定索引；M.E.AI 内容不含 wire 索引，同一非空 CallId 复用同一索引。
    ///     缺失（null 或空串）的 CallId 不注册字典，因此分配新索引时推进独立计数器
    ///     <paramref name="nextIndex" /> 而不是读取字典数量，否则多个无 id 的调用拿到相同索引，
    ///     累加器按索引合并时会把它们的参数 JSON 叠成非法文本。
    ///     Assigns a stable index per tool call. M.E.AI content carries no wire
    ///     index, so the same non-empty CallId reuses the same index. A missing
    ///     (null or empty) CallId registers nothing, so each allocation advances
    ///     the independent counter <paramref name="nextIndex" /> instead of reading
    ///     the dictionary size; otherwise id-less calls would all share one index
    ///     and the accumulator's index-based merge would fold their argument JSON
    ///     into invalid text.
    /// </summary>
    private static int IndexForCall(Dictionary<string, int> callIndexes, string? callId, ref int nextIndex)
    {
        if (!string.IsNullOrEmpty(callId) && callIndexes.TryGetValue(callId, out var index)) return index;

        index = nextIndex++;
        if (!string.IsNullOrEmpty(callId)) callIndexes.Add(callId, index);

        return index;
    }

    /// <summary>
    ///     从 <see cref="TextReasoningContent.AdditionalProperties" /> 提取 reasoning item id。
    ///     值形态照抄 M.E.AI.OpenAI Responses 适配器的读取方式（反编译 L1562-1566）：进程内为
    ///     string，历史经序列化再水化后为 JsonElement(string)。
    ///     Extracts the reasoning item id from
    ///     <see cref="TextReasoningContent.AdditionalProperties" />. The value shape
    ///     mirrors the M.E.AI.OpenAI Responses adapter's own reader (decompiled
    ///     L1562-1566): a string in-process, or a JsonElement(string) after the
    ///     history has been serialized and rehydrated.
    /// </summary>
    private static string? ReasoningItemIdOf(TextReasoningContent reasoning)
    {
        if (reasoning.AdditionalProperties?.TryGetValue(ReasoningItemIdKey, out var value) is not true) return null;

        return value as string
            ?? (value is JsonElement { ValueKind: JsonValueKind.String } element ? element.GetString() : null);
    }

    /// <summary>
    ///     把项目消息转换为 M.E.AI 消息。assistant 消息的文本与工具调用以多内容形式同时保留；
    ///     tool 消息映射为 <see cref="FunctionResultContent" />。Responses 协议下，assistant 消息携带的
    ///     推理条目以 <see cref="TextReasoningContent" /> 形式插到其余内容之前，供适配器原样回传。
    ///     Translates protocol messages into M.E.AI messages. An assistant
    ///     message's text and tool calls are preserved together as multiple
    ///     contents; tool messages map to <see cref="FunctionResultContent" />.
    ///     Under the Responses protocol, an assistant message's reasoning entries
    ///     are injected as <see cref="TextReasoningContent" /> ahead of all other
    ///     contents so the adapter replays them verbatim.
    /// </summary>
    private IList<ChatMessage> ToMicrosoftMessages(ChatCompletionRequest request)
    {
        var result = new List<ChatMessage>(request.Messages.Count);

        foreach (var message in request.Messages)
            switch (message.Role)
            {
                case InternalChatRole.System :
                    result.Add(new ChatMessage(ChatRole.System, message.Content));
                    break;

                case InternalChatRole.User :
                    result.Add(new ChatMessage(ChatRole.User, message.Content));
                    break;

                case InternalChatRole.Assistant :
                    var contents = new List<AIContent>();

                    // Responses 专用：推理条目按到达顺序置于最前。Chat Completions 不注入——
                    // 未知内容在该路径无验证，避免破坏既有行为。
                    // Responses only: reasoning entries come first in arrival
                    // order. Chat Completions does not inject them — unverified
                    // content on that path would risk breaking existing behavior.
                    if (_api == ChatApiKind.Responses && message.Reasoning is { Count: > 0 })
                        foreach (var entry in message.Reasoning)
                        {
                            var reasoning = new TextReasoningContent(entry.Text)
                            {
                                ProtectedData = entry.ProtectedData
                            };

                            if (entry.ItemId is not null)
                                // reasoningItemId 是 M.E.AI.OpenAI Responses 适配器的既定往返
                                // 契约键（ReasoningItemIdKey），回传时按它重建 reasoning 条目 id。
                                // reasoningItemId is the M.E.AI.OpenAI Responses adapter's
                                // established round-trip contract key (ReasoningItemIdKey);
                                // replay rebuilds the reasoning item id from it.
                                reasoning.AdditionalProperties =
                                    new AdditionalPropertiesDictionary { [ReasoningItemIdKey] = entry.ItemId };

                            contents.Add(reasoning);
                        }

                    if (!string.IsNullOrEmpty(message.Content)) contents.Add(new TextContent(message.Content));

                    if (message.ToolCalls is { Count: > 0 })
                        foreach (var call in message.ToolCalls)
                            contents.Add(ToFunctionCall(call));

                    result.Add(new ChatMessage(ChatRole.Assistant, contents));
                    break;

                case InternalChatRole.Tool :
                    if (string.IsNullOrEmpty(message.ToolCallId))
                        throw new InvalidDataException("A tool message must reference a tool call id.");

                    result.Add(new ChatMessage(ChatRole.Tool,
                                               [new FunctionResultContent(message.ToolCallId, message.Content)]));
                    break;

                default :
                    throw new InvalidOperationException($"Unsupported chat role '{message.Role}'.");
            }

        return result;
    }

    /// <summary>
    ///     将内部工具调用转换为 M.E.AI 函数调用内容；参数 JSON 解析为 JsonElement 值的字典，
    ///     与适配器入站方向的值形态一致（离线探针已验证该形态可安全回传并经 NativeAOT 发布）。
    ///     Converts an internal tool call into an M.E.AI function-call content. The
    ///     arguments JSON is parsed into a dictionary of JsonElement values, matching
    ///     the inbound value shape (the offline probes verified this shape round-trips
    ///     and survives NativeAOT publishing).
    /// </summary>
    private static FunctionCallContent ToFunctionCall(ChatToolCall call)
    {
        return new FunctionCallContent(call.Id, call.FunctionName, ToArgumentDictionary(call.ArgumentsJson));
    }

    private static IDictionary<string, object?> ToArgumentDictionary(string argumentsJson)
    {
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(argumentsJson)) return arguments;

        using var document = JsonDocument.Parse(argumentsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException(
                                           $"Tool call arguments must be a JSON object, got '{document.RootElement.ValueKind}'.");

        foreach (var property in document.RootElement.EnumerateObject())
            arguments[property.Name] = property.Value.Clone();

        return arguments;
    }

    /// <summary>
    ///     把工具定义映射为 <see cref="ChatOptions" /> 的声明式工具；Chat Completions 无工具时返回 null。
    ///     Responses 即使无工具也创建 ChatOptions，并通过 RawRepresentationFactory 固定
    ///     <c>StoredOutputEnabled = false</c>：服务端不存储输出、不依赖 previous_response_id 会话状态，
    ///     全部上下文（含 reasoning）由本地历史逐轮回传（PLAN §21；探针 ProviderProbe.cs）。
    ///     Maps tool definitions to declaration-only tools on <see cref="ChatOptions" />;
    ///     Chat Completions returns null when the request carries no tools. Responses
    ///     always creates a ChatOptions and pins <c>StoredOutputEnabled = false</c> via
    ///     RawRepresentationFactory: no server-side stored output and no
    ///     previous_response_id session state — every round replays the full local
    ///     history, reasoning included (PLAN §21; the ProviderProbe.cs probe).
    /// </summary>
    private ChatOptions? ToChatOptions(ChatCompletionRequest request)
    {
        ChatOptions? options;
        if (_api == ChatApiKind.Responses)
        {
            // OPENAI001（CreateResponseOptions 属实验 API）在此最小范围关闭：PLAN §21 已离线
            // 验证该请求构造路径并经 win-x64 NativeAOT 发布确认零 trim/AOT 警告。
            // OPENAI001 (experimental CreateResponseOptions) is suppressed for this
            // minimal scope only: PLAN §21 verified this request construction
            // offline and confirmed zero trim/AOT warnings in a win-x64
            // NativeAOT publish.
#pragma warning disable OPENAI001
            options = new ChatOptions
            {
                RawRepresentationFactory = _ => new CreateResponseOptions { StoredOutputEnabled = false }
            };
#pragma warning restore OPENAI001
        }
        else if (request.Tools is { Count: > 0 })
        {
            options = new ChatOptions();
        }
        else
        {
            return null;
        }

        if (request.Tools is not { Count: > 0 }) return options;

        options.Tools ??= [];
        foreach (var tool in request.Tools)
        {
            // The schema lives as a JsonObject; parse-and-clone yields the
            // JsonElement the declaration factory expects without reflection.
            using var document = JsonDocument.Parse(tool.Parameters.ToJsonString());
            options.Tools.Add(AIFunctionFactory.CreateDeclaration(tool.Name, tool.Description,
                                                                  document.RootElement.Clone()));
        }

        return options;
    }

    /// <summary>
    ///     以 JsonNode 组装工具参数的完整 JSON 字符串。禁止用 JsonSerializer 序列化
    ///     <see cref="FunctionCallContent.Arguments" /> 字典：其值为 object（实际是 JsonElement），
    ///     在禁用反射 JSON 的 NativeAOT 产物中不可依赖运行时类型解析。
    ///     Assembles the full argument JSON with JsonNodes. The arguments dictionary
    ///     must never be serialized via JsonSerializer: its values are object-typed
    ///     (JsonElement in practice), and runtime type resolution is unavailable in
    ///     reflection-disabled NativeAOT artifacts.
    /// </summary>
    private static string SerializeArguments(string? callId, IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0) return "{}";

        var json                                            = new JsonObject();
        foreach (var (name, value) in arguments) json[name] = ToJsonNode(callId, name, value);

        return json.ToJsonString();
    }

    /// <summary>
    ///     JsonElement 值按原始文本重新解析为 JsonNode；其余仅接受 <see cref="JsonValue" /> 可覆盖的
    ///     标量，超出该范围抛出带上下文的 <see cref="InvalidDataException" />。
    ///     JsonElement values are re-parsed from their raw text into JsonNodes;
    ///     everything else is accepted only when a <see cref="JsonValue" /> overload
    ///     covers the scalar, otherwise an <see cref="InvalidDataException" /> with
    ///     context is thrown.
    /// </summary>
    private static JsonNode? ToJsonNode(string? callId, string name, object? value)
    {
        return value switch
        {
            null => null,
            JsonElement { ValueKind: JsonValueKind.Undefined or JsonValueKind.Null } => null,
            JsonElement element => JsonNode.Parse(element.GetRawText()),
            string text => JsonValue.Create(text),
            bool boolean => JsonValue.Create(boolean),
            char character => JsonValue.Create(character),
            byte byteValue => JsonValue.Create(byteValue),
            sbyte sbyteValue => JsonValue.Create(sbyteValue),
            short shortValue => JsonValue.Create(shortValue),
            ushort ushortValue => JsonValue.Create(ushortValue),
            int intValue => JsonValue.Create(intValue),
            uint uintValue => JsonValue.Create(uintValue),
            long longValue => JsonValue.Create(longValue),
            ulong ulongValue => JsonValue.Create(ulongValue),
            float floatValue => JsonValue.Create(floatValue),
            double doubleValue => JsonValue.Create(doubleValue),
            decimal decimalValue => JsonValue.Create(decimalValue),
            DateTime dateTime => JsonValue.Create(dateTime),
            DateTimeOffset dateTimeOffset => JsonValue.Create(dateTimeOffset),
            Guid guid => JsonValue.Create(guid),
            _ => throw new InvalidDataException(
                                                $"Tool call '{callId ?? "?"}' argument '{name}' has unsupported type '{value.GetType().FullName}'.")
        };
    }
}
