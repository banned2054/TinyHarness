namespace TinyHarness.Desktop.Models;

/// <summary>一条已落盘的会话消息；Seq 是后端事件日志的连续序号。</summary>
/// <remarks>
///     Turn/Step 是后端给 assistant 消息与工具事件标注的轮次与步序号（user/message 不带）；
///     HasToolCalls 表示内容块里含 tool-call 块（这类消息是轮次提交，不是可见回复）；
///     Reasoning 是 reasoning 块拼接的思考文本（不算回复正文，与参考实现一致）；
///     IsInterrupted 表示这条消息是被用户取消的部分回复。
/// </remarks>
public sealed record ConversationMessage(
    long           Seq,
    string         Id,
    MessageRole    Role,
    string         Content,
    DateTimeOffset CreatedAt,
    long?          Turn          = null,
    bool           HasToolCalls  = false,
    long?          Step          = null,
    string?        Reasoning     = null,
    bool           IsInterrupted = false,
    string?        ModelLabel    = null) : ConversationEntry(Seq, CreatedAt);
