namespace TinyHarness.Core.Models.Configuration;

/// <summary>
///     模型服务使用的 Chat API 协议种类。旧配置未声明该字段时缺省为
///     <see cref="ChatCompletions" />，不按模型名猜测协议，也不自动切换。
///     The Chat API protocol used by the model service. Legacy configurations that
///     do not declare this field default to <see cref="ChatCompletions" />; the kind
///     is never guessed from model names, nor switched automatically.
/// </summary>
public enum ChatApiKind
{
    /// <summary>
    ///     OpenAI 兼容的 Chat Completions 协议（/v1/chat/completions）。
    ///     The OpenAI-compatible Chat Completions protocol (/v1/chat/completions).
    /// </summary>
    ChatCompletions,

    /// <summary>
    ///     OpenAI Responses 协议（/v1/responses）。
    ///     The OpenAI Responses protocol (/v1/responses).
    /// </summary>
    Responses
}

/// <summary>
///     <see cref="ChatApiKind" /> 字符串值的解析与规范写出。配置加载、用户配置读写与 CLI 展示共用同一份
///     映射，两侧 JSON 格式不会漂移。
///     Parses and canonically writes <see cref="ChatApiKind" /> string values. The config loader, the user
///     config store, and CLI display share one mapping so the two JSON sides never drift.
/// </summary>
public static class ChatApiKindParser
{
    /// <summary>
    ///     Chat Completions 的规范 JSON 值。The canonical JSON value for Chat Completions.
    /// </summary>
    public const string ChatCompletionsValue = "chat-completions";

    /// <summary>
    ///     Responses 的规范 JSON 值。The canonical JSON value for Responses.
    /// </summary>
    public const string ResponsesValue = "responses";

    /// <summary>
    ///     大小写不敏感地解析协议名；接受规范 kebab-case 值与 camelCase 同义词 "chatCompletions"。
    ///     Parses a protocol name case-insensitively; accepts the canonical kebab-case value and the
    ///     camelCase synonym "chatCompletions".
    /// </summary>
    public static bool TryParse(string? text, out ChatApiKind value)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case ChatCompletionsValue :
            case "chatcompletions" :
                value = ChatApiKind.ChatCompletions;
                return true;
            case ResponsesValue :
                value = ChatApiKind.Responses;
                return true;
            default :
                value = ChatApiKind.ChatCompletions;
                return false;
        }
    }

    /// <summary>
    ///     写出规范 kebab-case 值，与读取端互逆。
    ///     Writes the canonical kebab-case value, the inverse of the reading side.
    /// </summary>
    public static string ToValueString(ChatApiKind value)
    {
        return value switch
        {
            ChatApiKind.ChatCompletions => ChatCompletionsValue,
            ChatApiKind.Responses => ResponsesValue,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown chat API kind.")
        };
    }
}
