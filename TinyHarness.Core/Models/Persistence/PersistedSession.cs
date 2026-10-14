using System.Security.Cryptography;
using System.Text;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Session;

namespace TinyHarness.Core.Models.Persistence;

/// <summary>
///     受保护会话存储中的版本化会话快照（M13.1 起，PLAN §7.1）：会话身份、固定工作区与模型身份、
///     单调提交序号和全部用户回合的脱敏检查视图。v3 的 <see cref="Conversation" /> 原样保存
///     消息级协议内容与压缩边界，可能包含用户粘贴或模型返回的秘密；认证凭据不自动注入，
///     不携带可执行授权。v1/v2 旧快照仅保持只读检查。
///     The versioned session snapshot in the protected session store (since
///     M13.1, PLAN §7.1): session identity, the fixed workspace and model
///     identity, a monotonic commit sequence, and an inspection view of every
///     user turn, redacted for inspection. In v3, <see cref="Conversation" />
///     preserves the message-level protocol content and compaction boundaries
///     verbatim, which can include secrets pasted by the user or returned by the
///     model. Authentication credentials are not injected, and executable
///     authorization is never stored. Older v1/v2 snapshots stay inspection-only.
/// </summary>
public sealed record PersistedSession
{
    /// <summary>
    ///     当前格式版本；读取端只接受已知版本，未知版本按错误拒绝而不是猜测解释。
    ///     v1 只有检查视图；v2 的消息内容可能被脱敏；v3 保真保存协议内容用于续聊。
    ///     The current format version; readers accept known versions only and
    ///     reject unknown ones instead of guessing. v1 carries inspection only;
    ///     v2 may have redacted messages; v3 preserves protocol content for resume.
    /// </summary>
    public const int CurrentFormatVersion = 3;

    public int FormatVersion { get; init; } = CurrentFormatVersion;

    public string Id { get; init; } = string.Empty;

    public string WorkspaceRoot { get; init; } = string.Empty;

    public SessionModelIdentity? Model { get; init; }

    public DateTimeOffset CreatedUtc { get; init; }

    /// <summary>本快照最后一次成功原子替换的时间。When this snapshot was last atomically replaced.</summary>
    public DateTimeOffset UpdatedUtc { get; init; }

    /// <summary>
    ///     单调递增的提交序号：每次原子替换加一，并在审计文件中记录同值提交；读取端用它检测
    ///     快照与审计是否一致（PLAN §7.1）。
    ///     The monotonically increasing commit sequence: every atomic replacement
    ///     increments it and appends an audit record with the same value; readers
    ///     use it to detect snapshot/audit divergence (PLAN §7.1).
    /// </summary>
    public long CommitSequence { get; init; }

    public IReadOnlyList<PersistedTurn> Turns { get; init; } = [];

    /// <summary>
    ///     消息级会话内容（v2 起）；只有 v3 保证写入端不脱敏或规范化协议字段，可用于续聊。
    ///     opaque 载荷逐条带完整性哈希。检查命令不输出此节的原始内容（PLAN §7.2）。
    ///     Message-level conversation content (since v2); only v3 guarantees that
    ///     the writer neither redacts nor normalizes protocol fields for resume.
    ///     Opaque payloads carry per-entry integrity hashes. Inspection commands
    ///     never print the raw contents of this section (PLAN §7.2).
    /// </summary>
    public PersistedConversation? Conversation { get; init; }
}

/// <summary>
///     会话的可续聊对话内容（M13.2，PLAN §7.2）：完整本地历史（含 system 消息）、结构化压缩
///     状态与折叠边界。v3 写入端原样保存字段；恢复端重建 <c>ConversationContext</c> 并在
///     重新发送给模型前完成完整性检查。此节可能含秘密，不作为检查输出；没有批准或可执行
///     计划，恢复后的新工具调用仍重新 Prepare / Authorize / Execute。
///     The resumable conversation content of a session (M13.2, PLAN §7.2): the
///     complete local history (system message included), the structured
///     compaction state and the fold boundaries. The v3 writer preserves fields
///     verbatim; the resume side rebuilds the context and checks integrity before
///     sending it to the model. This section may contain secrets and is not an
///     inspection output. Approvals and executable plans are never stored; new
///     tool calls still pass Prepare / Authorize / Execute after resume.
/// </summary>
public sealed record PersistedConversation
{
    public IReadOnlyList<PersistedMessage> Messages { get; init; } = [];

    /// <summary>
    ///     压缩后的结构化状态 JSON（v3 不再脱敏）；未发生过压缩时为 null。
    ///     The compacted structured-state JSON (not redacted in v3); null when no compaction ever happened.
    /// </summary>
    public string? StateJson { get; init; }

    public bool HasState { get; init; }

    /// <summary>折叠区起点（原始历史中的消息下标）；从未折叠时为 -1。The fold-region start index into the raw history; -1 while never folded.</summary>
    public int FoldStartMessageIndex { get; init; } = -1;

    /// <summary>折叠区终点（不含）；从未折叠时为 0。The fold-region end index (exclusive); 0 while never folded.</summary>
    public int FoldEndMessageIndex { get; init; }

    /// <summary>
    ///     旧 v2 写入端脱敏改动 opaque 载荷时的标记，保留用于只读兼容与恢复拒绝。
    ///     v3 不再脱敏协议内容，写入时保持 false。
    ///     The legacy v2 marker for opaque payloads changed by redaction, retained
    ///     for read-only compatibility and resume refusal. The v3 writer does not
    ///     redact protocol content and leaves this false.
    /// </summary>
    public bool OpaqueRedacted { get; init; }
}

/// <summary>
///     一条持久化的对话消息：角色、原样文本、工具调用（组）、工具结果关联与推理条目。
///     One persisted conversation message: role, verbatim text, requested tool
///     calls, tool-result linkage and reasoning entries.
/// </summary>
public sealed record PersistedMessage
{
    /// <summary><see cref="ChatRole" /> 的名称；恢复端拒绝未知值。The <see cref="ChatRole" /> name; the resume side rejects unknown values.</summary>
    public string Role { get; init; } = string.Empty;

    public string Content { get; init; } = string.Empty;

    public IReadOnlyList<PersistedToolCall>? ToolCalls { get; init; }

    public string? ToolCallId { get; init; }

    public string? Name { get; init; }

    public IReadOnlyList<PersistedReasoning>? Reasoning { get; init; }
}

/// <summary>
///     assistant 消息请求的一次工具调用，恢复端原样续传的协议形态，不用于重放执行。
///     One tool call requested by an assistant message, preserved for protocol
///     replay by the resume side, never for replaying execution.
/// </summary>
public sealed record PersistedToolCall(string Id, string Name, string Arguments);

/// <summary>
///     一条持久化的推理内容：摘要文本（原样）与服务端 opaque 载荷（encrypted_content 与 item id，
///     原样保留）加上其完整性哈希。哈希覆盖落盘值，恢复端重算比对，不一致即拒绝无损续聊
///     （PLAN §7.2：被改变或不能验证完整性的 opaque 内容不得伪造原值续传）。
///     One persisted reasoning entry: the verbatim summary text, the
///     server-side opaque payloads (encrypted_content and item id, kept
///     verbatim) and their integrity hash. The hash covers the stored values;
///     the resume side recomputes and compares, refusing lossless resume on any
///     mismatch (PLAN §7.2: opaque content that was changed or cannot be
///     verified must never be replayed with a forged original).
/// </summary>
public sealed record PersistedReasoning
{
    public string? Text { get; init; }

    public string? ProtectedData { get; init; }

    public string? ItemId { get; init; }

    public string OpaqueHash { get; init; } = string.Empty;

    /// <summary>
    ///     计算 opaque 载荷的 SHA-256 完整性哈希（写入端与恢复端共用同一实现；分隔符避免
    ///     字段拼接歧义）。
    ///     Computes the SHA-256 integrity hash of the opaque payloads (one shared
    ///     implementation for the writer and the resume side; the separator
    ///     avoids concatenation ambiguity).
    /// </summary>
    public static string ComputeOpaqueHash(string? protectedData, string? itemId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8
                                                    .GetBytes($"{protectedData ?? string.Empty}\u001F{itemId ?? string.Empty}")));
}

/// <summary>
///     一个用户回合的持久化检查视图：输入原文（脱敏后）与该回合全部执行尝试的终态摘要。
///     The persisted inspection view of one user turn: the (redacted) input and
///     the terminal summaries of every run attempted for it.
/// </summary>
public sealed record PersistedTurn
{
    public string Id { get; init; } = string.Empty;

    public string UserInput { get; init; } = string.Empty;

    public DateTimeOffset CreatedUtc { get; init; }

    public IReadOnlyList<PersistedRun> Runs { get; init; } = [];
}

/// <summary>
///     一次运行的终态摘要（脱敏后的检查视图）：状态、步数、工具数、usage 与最终回答/错误文本。
///     不含消息级协议内容，也不能用于重放执行。
///     The terminal summary of one run (a redacted inspection view): status,
///     steps, tools, usage and the final/error text. It carries no
///     message-level protocol content and cannot replay execution.
/// </summary>
public sealed record PersistedRun
{
    public string Id { get; init; } = string.Empty;

    public DateTimeOffset StartedUtc { get; init; }

    /// <summary>终态写入时间；尚未结束的运行为 null。When the terminal result was recorded; null while the run is unfinished.</summary>
    public DateTimeOffset? FinishedUtc { get; init; }

    /// <summary>
    ///     <see cref="Models.Agent.AgentStatus" /> 的名称；运行未结束时为 null。The <see cref="Models.Agent.AgentStatus" /> name;
    ///     null while unfinished.
    /// </summary>
    public string? Status { get; init; }

    public string? FinalMessage { get; init; }

    public string? Error { get; init; }

    public int Steps { get; init; }

    public int ToolExecutions { get; init; }

    public long? InputTokens { get; init; }

    public long? OutputTokens { get; init; }
}
