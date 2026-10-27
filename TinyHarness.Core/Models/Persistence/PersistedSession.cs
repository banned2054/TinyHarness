using System.Security.Cryptography;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Agent;
using System.Text;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.ProjectInstructions;
using TinyHarness.Core.Models.Session;

namespace TinyHarness.Core.Models.Persistence;

/// <summary>
///     <para>由会话日志重建的本地投影，保存身份、提交序号、运行事实及可续聊协议原文。</para>
///     <para>
///         A local session-log projection containing identity, commit sequence, run facts and verbatim resumable
///         protocol content.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         <see cref="Conversation" /> 原样保存消息与压缩边界，可能含对话中的秘密；
///         认证凭据不主动注入，不保存可执行授权。受保护存储不代表加密。
///     </para>
///     <para>
///         <see cref="Conversation" /> preserves messages and compaction boundaries verbatim
///         and may contain conversation secrets. Credentials are not proactively injected,
///         and executable authorization is not stored. Protected storage does not imply encryption.
///     </para>
/// </remarks>
public sealed record PersistedSession
{
    public string Id { get; init; } = string.Empty;

    public string WorkspaceRoot { get; init; } = string.Empty;

    /// <summary>
    ///     <para>日志 project-binding 记录的项目归属；未绑定的旧日志为 null，不支持转移。</para>
    ///     <para>Project ownership from the log's project-binding record; null for unbound legacy logs and never transferred.</para>
    /// </summary>
    public string? ProjectId { get; init; }

    public SessionModelIdentity? Model { get; init; }

    public IReadOnlyList<SessionModelSelection> ModelSelections { get; init; } = [];
    public SessionModelSelection?               ModelSelection  => ModelSelections.LastOrDefault();

    public DateTimeOffset CreatedUtc { get; init; }

    /// <summary>
    ///     <para>日志最近一次成功提交的时间。</para>
    ///     <para>When the log was last successfully committed.</para>
    /// </summary>
    public DateTimeOffset UpdatedUtc { get; init; }

    /// <summary>
    ///     <para>
    ///         最近一次非归属维护批次的提交时间；纯 project-binding 追加不推进本值，归属初始化不伪造新对话活动。
    ///     </para>
    ///     <para>
    ///         When the last non-ownership-maintenance batch committed; a pure project-binding append never
    ///         advances it, so ownership initialization fabricates no new conversation activity.
    ///     </para>
    /// </summary>
    public DateTimeOffset LastActivityUtc { get; init; }

    /// <summary>
    ///     <para>日志最近一次成功提交的批次序号；检查点也提交，因此不等同回合数。</para>
    ///     <para>
    ///         The last successfully committed log batch sequence; checkpoints also commit, so it is not a turn count.
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

    public IReadOnlyList<RunAmendment>    Amendments { get; init; } = [];
    public IReadOnlyList<SessionWorkItem> WorkItems  { get; init; } = [];

    /// <summary>
    ///     <para>
    ///         消息级会话内容；保证协议字段不脱敏、不规范化，opaque 载荷逐条带完整性哈希。
    ///         检查命令不输出此节原文。
    ///     </para>
    ///     <para>
    ///         Message-level conversation content; preserves unredacted, unnormalized protocol fields
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
///         原样保存字段，恢复端校验后才发送模型。内容可能含秘密，不作为检查输出；
///         不保存批准或可执行计划，新调用仍须 Prepare / Authorize / Execute。
///     </para>
///     <para>
///         Preserves fields verbatim; restoration validates them before model requests.
///         Content may contain secrets and is not inspection output. Approvals and executable plans are not stored;
///         new calls still require Prepare / Authorize / Execute.
///     </para>
/// </remarks>
public sealed record PersistedConversation
{
    public long?                           CheckpointModelSelectionRevision { get; init; }
    public IReadOnlyList<PersistedMessage> Messages                         { get; init; } = [];

    public IReadOnlyList<ProjectInstructionRecord> ProjectInstructionRecords { get; init; } = [];

    /// <summary>
    ///     <para>已提交的交接摘要和原话来源；不脱敏，未压缩时为 null。</para>
    ///     <para>The committed handoff and original-user sources; unredacted and null before compaction.</para>
    /// </summary>
    public CompactionState? State { get; init; }

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
///     <para>用户回合的本地视图，保留输入原文及全部运行尝试。</para>
///     <para>A local turn view preserving user input and all run attempts.</para>
/// </summary>
public sealed record PersistedTurn
{
    public string Id { get; init; } = string.Empty;

    public string UserInput { get; init; } = string.Empty;

    public DateTimeOffset CreatedUtc { get; init; }

    public IReadOnlyList<PersistedRun> Runs { get; init; } = [];
}

/// <summary>
///     <para>运行的本地视图，保留回答原文及执行事实；错误诊断继续脱敏。</para>
///     <para>
///         A local run view preserving answers and execution facts while keeping error diagnostics redacted.
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
    public long?              ModelSelectionRevision { get; init; }
    public RunBudgetSnapshot? Budget                 { get; init; }

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
    ///     <para>从执行记录推导的未知副作用标记；尾部中断且缺少记录时，恢复端保守阻断。</para>
    ///     <para>
    ///         An unknown-side-effect marker derived from execution records; restoration conservatively
    ///         blocks interrupted tails without records.
    ///     </para>
    /// </summary>
    public bool? NeedsInspection { get; init; }

    /// <summary>
    ///     <para>工具调用的执行意图与确认事实；没有调用时可为 null，不含可重放计划或授权。</para>
    ///     <para>
    ///         Execution intent and confirmation facts for requested tool calls; may be null when no
    ///         calls exist and carry no replayable plan or authorization.
    ///     </para>
    /// </summary>
    public IReadOnlyList<PersistedToolCallRecord>? ToolCalls { get; init; }
}

/// <summary>
///     <para>工具调用的执行意图与确认记录，区分未开始、已确认与未知结果；不可重放执行或授权。</para>
///     <para>
///         A tool-call record of execution intent and confirmation, distinguishing
///         not started, confirmed and unknown results; never replays execution or authorization.
///     </para>
/// </summary>
public sealed record PersistedToolCallRecord
{
    public string CallId { get; init; } = string.Empty;

    /// <summary>
    ///     <para>未脱敏的本次运行调用序号；旧日志缺失时按 CallId 关联。</para>
    ///     <para>The unredacted per-run invocation ordinal; older logs without it correlate by CallId.</para>
    /// </summary>
    public string? InvocationId { get; init; }

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
    ///     <para>工具准备时的一行摘要原文。</para>
    ///     <para>The original one-line summary from tool preparation.</para>
    /// </summary>
    public string Summary { get; init; } = string.Empty;

    public string? PreparationError { get; init; }

    /// <summary>
    ///     <para>工具已知未开始的原因；与准备错误及授权结论分别保存。</para>
    ///     <para>
    ///         The reason a tool is known not to have started, stored separately from preparation errors and permission
    ///         outcomes.
    ///     </para>
    /// </summary>
    public string? NotStartedReason { get; init; }

    /// <summary>
    ///     <para>用于本地查看的授权结论；尚未授权时为 null，不可恢复为授权。</para>
    ///     <para>The local authorization outcome; null before authorization and never restored as a grant.</para>
    /// </summary>
    public string? Approval { get; init; }

    /// <summary>
    ///     <para><see cref="Models.Agent.ToolCallExecutionState" /> 的名称；恢复端拒绝未知值。</para>
    ///     <para>The <see cref="Models.Agent.ToolCallExecutionState" /> name; restoration rejects unknown values.</para>
    /// </summary>
    public string State { get; init; } = string.Empty;
}
