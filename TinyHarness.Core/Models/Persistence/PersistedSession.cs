using System.Security.Cryptography;
using System.Text;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Session;

namespace TinyHarness.Core.Models.Persistence;

/// <summary>
///     <para>版本化会话快照，保存固定身份、提交序号、脱敏检查视图及可续聊的协议内容。</para>
///     <para>
///         A versioned session snapshot containing fixed identity, commit sequence, redacted inspection
///         views and resumable protocol content.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         v3 的 <see cref="Conversation" /> 原样保存消息与压缩边界，可能含对话中的秘密；
///         认证凭据不主动注入，不保存可执行授权。受保护存储不代表加密，v1/v2 仅供只读检查。
///     </para>
///     <para>
///         In v3, <see cref="Conversation" /> preserves messages and compaction boundaries verbatim
///         and may contain conversation secrets. Credentials are not proactively injected,
///         and executable authorization is not stored. Protected storage does not imply encryption;
///         v1/v2 remain inspection-only.
///     </para>
/// </remarks>
public sealed record PersistedSession
{
    /// <summary>
    ///     <para>
    ///         当前格式版本；读取端拒绝未知版本。v1 仅有检查视图，v2 消息可能被脱敏，
    ///         v3 保真保存续聊协议内容。
    ///     </para>
    ///     <para>
    ///         The current format version; readers reject unknown versions. v1 carries inspection only,
    ///         v2 messages may be redacted, and v3 preserves resumable protocol content verbatim.
    ///     </para>
    /// </summary>
    public const int CurrentFormatVersion = 3;

    public int FormatVersion { get; init; } = CurrentFormatVersion;

    public string Id { get; init; } = string.Empty;

    public string WorkspaceRoot { get; init; } = string.Empty;

    public SessionModelIdentity? Model { get; init; }

    public DateTimeOffset CreatedUtc { get; init; }

    /// <summary>
    ///     <para>本快照最后一次成功原子替换的时间。</para>
    ///     <para>When this snapshot was last successfully atomically replaced.</para>
    /// </summary>
    public DateTimeOffset UpdatedUtc { get; init; }

    /// <summary>
    ///     <para>每次原子替换递增的提交序号，与审计中的同值记录对账；检查点也提交，因此不等同回合数。</para>
    ///     <para>
    ///         A commit sequence incremented on each atomic replacement and reconciled with the matching
    ///         audit record; checkpoints also commit, so it is not a turn count.
    ///     </para>
    /// </summary>
    public long CommitSequence { get; init; }

    /// <summary>
    ///     <para>用户最近一次确认 NeedsInspection 的时间；未确认或新的中断清空旧确认时为 null。</para>
    ///     <para>
    ///         When the user last acknowledged NeedsInspection; null before acknowledgement or when
    ///         a new interruption clears an earlier one.
    ///     </para>
    /// </summary>
    public DateTimeOffset? InspectionAcknowledgedUtc { get; init; }

    public string? PendingInspectionReason { get; init; }

    public IReadOnlyList<PersistedTurn> Turns { get; init; } = [];

    /// <summary>
    ///     <para>
    ///         消息级会话内容；仅 v3 保证协议字段不脱敏、不规范化，opaque 载荷逐条带完整性哈希。
    ///         检查命令不输出此节原文。
    ///     </para>
    ///     <para>
    ///         Message-level conversation content; only v3 guarantees unredacted, unnormalized protocol fields
    ///         with per-entry opaque integrity hashes. Inspection commands never print this section verbatim.
    ///     </para>
    /// </summary>
    public PersistedConversation? Conversation { get; init; }
}

/// <summary>
///     <para>可续聊的完整历史（含 system 消息）、结构化压缩状态与折叠边界。</para>
///     <para>
///         The resumable full history, including the system message, structured compaction state
///         and fold boundaries.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         v3 原样保存字段，恢复端校验后才发送模型。内容可能含秘密，不作为检查输出；
///         不保存批准或可执行计划，新调用仍须 Prepare / Authorize / Execute。
///     </para>
///     <para>
///         v3 preserves fields verbatim; restoration validates them before model requests.
///         Content may contain secrets and is not inspection output. Approvals and executable plans are not stored;
///         new calls still require Prepare / Authorize / Execute.
///     </para>
/// </remarks>
public sealed record PersistedConversation
{
    public IReadOnlyList<PersistedMessage> Messages { get; init; } = [];

    /// <summary>
    ///     <para>压缩后的结构化状态 JSON；v3 不脱敏，未发生压缩时为 null。</para>
    ///     <para>The compacted structured-state JSON; unredacted in v3 and null before any compaction.</para>
    /// </summary>
    public string? StateJson { get; init; }

    public bool HasState { get; init; }

    /// <summary>
    ///     <para>折叠区起点在原始历史中的消息下标；从未折叠时为 -1。</para>
    ///     <para>The fold-region start index in raw history; -1 before any folding.</para>
    /// </summary>
    public int FoldStartMessageIndex { get; init; } = -1;

    /// <summary>
    ///     <para>折叠区终点下标（不含）；从未折叠时为 0。</para>
    ///     <para>The exclusive fold-region end index; 0 before any folding.</para>
    /// </summary>
    public int FoldEndMessageIndex { get; init; }

    /// <summary>
    ///     <para>v2 opaque 载荷曾被脱敏的兼容标记，恢复端据此拒绝续聊；v3 不脱敏协议内容，写入 false。</para>
    ///     <para>
    ///         A compatibility marker for redacted v2 opaque payloads, used to refuse resume;
    ///         v3 preserves protocol content unredacted and writes false.
    ///     </para>
    /// </summary>
    public bool OpaqueRedacted { get; init; }
}

/// <summary>
///     <para>持久化对话消息，保留角色、文本、工具调用与结果关联、推理条目。</para>
///     <para>
///         A persisted conversation message preserving role, text, tool calls and result linkage,
///         and reasoning entries.
///     </para>
/// </summary>
public sealed record PersistedMessage
{
    /// <summary>
    ///     <para><see cref="ChatRole" /> 的名称；恢复端拒绝未知值。</para>
    ///     <para>The <see cref="ChatRole" /> name; restoration rejects unknown values.</para>
    /// </summary>
    public string Role { get; init; } = string.Empty;

    public string Content { get; init; } = string.Empty;

    public IReadOnlyList<PersistedToolCall>? ToolCalls { get; init; }

    public string? ToolCallId { get; init; }

    public string? Name { get; init; }

    public IReadOnlyList<PersistedReasoning>? Reasoning { get; init; }
}

/// <summary>
///     <para>assistant 请求的工具调用，恢复时原样续传协议内容，不重放执行。</para>
///     <para>
///         An assistant-requested tool call, preserved verbatim for protocol continuation,
///         never for execution replay.
///     </para>
/// </summary>
public sealed record PersistedToolCall(string Id, string Name, string Arguments);

/// <summary>
///     <para>原样保存的推理摘要、opaque 载荷（encrypted_content 与 item id）及完整性哈希。</para>
///     <para>
///         Verbatim reasoning summary and opaque payloads (encrypted_content and item id),
///         with their integrity hash.
///     </para>
/// </summary>
/// <remarks>
///     <para>哈希覆盖落盘值；恢复端重算不符时拒绝续聊，不伪造被改变或无法验证的原值。</para>
///     <para>
///         The hash covers stored values; restoration refuses resume on a mismatch and never forges
///         original values for changed or unverifiable payloads.
///     </para>
/// </remarks>
public sealed record PersistedReasoning
{
    public string? Text { get; init; }

    public string? ProtectedData { get; init; }

    public string? ItemId { get; init; }

    public string OpaqueHash { get; init; } = string.Empty;

    /// <summary>
    ///     <para>计算写入端与恢复端共用的 opaque 载荷 SHA-256 完整性哈希；分隔符避免拼接歧义。</para>
    ///     <para>
    ///         Computes the opaque payload SHA-256 integrity hash shared by writing and restoration;
    ///         a separator avoids concatenation ambiguity.
    ///     </para>
    /// </summary>
    public static string ComputeOpaqueHash(string? protectedData, string? itemId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8
                                                    .GetBytes($"{protectedData ?? string.Empty}\u001F{itemId ?? string.Empty}")));
}

/// <summary>
///     <para>用户回合的脱敏检查视图，包含输入与全部运行尝试的记录。</para>
///     <para>A redacted inspection view of a user turn, containing its input and records of all run attempts.</para>
/// </summary>
public sealed record PersistedTurn
{
    public string Id { get; init; } = string.Empty;

    public string UserInput { get; init; } = string.Empty;

    public DateTimeOffset CreatedUtc { get; init; }

    public IReadOnlyList<PersistedRun> Runs { get; init; } = [];
}

/// <summary>
///     <para>运行的脱敏检查视图，包含状态、计数、usage、回答或错误，以及可选的工具记录和检查标记。</para>
///     <para>
///         A redacted run inspection view containing status, counts, usage, answer or error,
///         and optional tool records and inspection markers.
///     </para>
/// </summary>
/// <remarks>
///     <para>执行意图检查点可保存未结束的运行，不补造终态；此视图不含消息级协议内容，不可重放执行。</para>
///     <para>
///         Execution-intent checkpoints may store unfinished runs without fabricating terminal results;
///         this view carries no message-level protocol content and cannot replay execution.
///     </para>
/// </remarks>
public sealed record PersistedRun
{
    public string Id { get; init; } = string.Empty;

    public DateTimeOffset StartedUtc { get; init; }

    /// <summary>
    ///     <para>终态写入时间；运行未结束时为 null。</para>
    ///     <para>When the terminal result was recorded; null while unfinished.</para>
    /// </summary>
    public DateTimeOffset? FinishedUtc { get; init; }

    /// <summary>
    ///     <para><see cref="Models.Agent.AgentStatus" /> 的名称；运行未结束时为 null。</para>
    ///     <para>The <see cref="Models.Agent.AgentStatus" /> name; null while unfinished.</para>
    /// </summary>
    public string? Status { get; init; }

    public string? FinalMessage { get; init; }

    public string? Error { get; init; }

    public int Steps { get; init; }

    public int ToolExecutions { get; init; }

    public long? InputTokens { get; init; }

    public long? OutputTokens { get; init; }

    /// <summary>
    ///     <para>从执行记录推导的未知副作用标记；旧 v3 快照缺少记录且尾部中断时，恢复端保守阻断。</para>
    ///     <para>
    ///         An unknown-side-effect marker derived from execution records; restoration conservatively
    ///         blocks older v3 snapshots with an interrupted tail and no records.
    ///     </para>
    /// </summary>
    public bool? NeedsInspection { get; init; }

    /// <summary>
    ///     <para>请求的工具调用的脱敏执行意图与确认事实；旧快照可为 null，不含可重放计划或授权。</para>
    ///     <para>
    ///         Redacted execution intent and confirmation facts for requested tool calls; may be null in
    ///         older snapshots and carry no replayable plan or authorization.
    ///     </para>
    /// </summary>
    public IReadOnlyList<PersistedToolCallRecord>? ToolCalls { get; init; }
}

/// <summary>
///     <para>工具调用的脱敏执行意图与确认检查记录，区分未开始、已确认与未知结果；不可重放执行或授权。</para>
///     <para>
///         A redacted tool-call inspection record of execution intent and confirmation, distinguishing
///         not started, confirmed and unknown results; never replays execution or authorization.
///     </para>
/// </summary>
public sealed record PersistedToolCallRecord
{
    public string CallId { get; init; } = string.Empty;

    public string ToolName { get; init; } = string.Empty;

    /// <summary>
    ///     <para>规范化能力名；准备失败时为 null。</para>
    ///     <para>The normalized capability; null when preparation failed.</para>
    /// </summary>
    public string? Capability { get; init; }

    /// <summary>
    ///     <para>能力是否可能产生副作用（非只读）。</para>
    ///     <para>Whether the capability can produce side effects (non-read-only).</para>
    /// </summary>
    public bool SideEffects { get; init; }

    /// <summary>
    ///     <para>按已知密钥规则脱敏的一行摘要。</para>
    ///     <para>The one-line summary redacted for known secrets.</para>
    /// </summary>
    public string Summary { get; init; } = string.Empty;

    public string? PreparationError { get; init; }

    /// <summary>
    ///     <para>脱敏后的授权结论；尚未授权时为 null。</para>
    ///     <para>The redacted authorization outcome; null before authorization.</para>
    /// </summary>
    public string? Approval { get; init; }

    /// <summary>
    ///     <para><see cref="Models.Agent.ToolCallExecutionState" /> 的名称；恢复端拒绝未知值。</para>
    ///     <para>The <see cref="Models.Agent.ToolCallExecutionState" /> name; restoration rejects unknown values.</para>
    /// </summary>
    public string State { get; init; } = string.Empty;
}
