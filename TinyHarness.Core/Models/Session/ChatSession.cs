namespace TinyHarness.Core.Models.Session;

/// <summary>
///     <para>连续会话的内存状态：稳定身份、固定工作区与模型身份、全部用户回合。</para>
///     <para>
///         In-memory state of a continuous session: stable identity, fixed workspace and model identity,
///         and all user turns.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         认证凭据由调用方按引用解析，不主动存入会话；对话内容仍可能含秘密。
///         不存储或恢复可执行授权；持久化与跨进程恢复由其他服务负责。
///     </para>
///     <para>
///         The caller resolves credentials by reference; they are not proactively stored, though conversation
///         content may contain secrets. Executable authorization is never stored or restored;
///         other services handle persistence and cross-process resume.
///     </para>
/// </remarks>
public sealed class ChatSession
{
    private readonly List<SessionTurn> _turns = [];

    public ChatSession(
        string id, string workspaceRoot, SessionModelIdentity model, DateTimeOffset? createdUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(model);
        Id            = id;
        WorkspaceRoot = Path.GetFullPath(workspaceRoot);
        Model         = model;
        CreatedUtc    = createdUtc ?? DateTimeOffset.UtcNow;
    }

    public string Id { get; }

    /// <summary>
    ///     <para>会话固定的工作区根绝对路径。</para>
    ///     <para>The session's fixed absolute workspace root.</para>
    /// </summary>
    public string WorkspaceRoot { get; }

    /// <summary>
    ///     <para>会话创建时固定的模型身份。</para>
    ///     <para>The model identity fixed at session creation.</para>
    /// </summary>
    public SessionModelIdentity Model { get; }

    public DateTimeOffset CreatedUtc { get; }

    /// <summary>
    ///     <para>用户最近一次确认 NeedsInspection 检查的时间；未确认时为 null，新的未知结果中断清空旧确认。</para>
    ///     <para>
    ///         When the user last acknowledged NeedsInspection; null before acknowledgement,
    ///         and cleared by a new unknown-result interruption.
    ///     </para>
    /// </summary>
    public DateTimeOffset? InspectionAcknowledgedUtc { get; internal set; }

    public string? PendingInspectionReason { get; internal set; }

    /// <summary>
    ///     <para>按提交顺序排列的用户回合。</para>
    ///     <para>User turns in submission order.</para>
    /// </summary>
    public IReadOnlyList<SessionTurn> Turns => _turns;

    internal void AddTurn(SessionTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        _turns.Add(turn);
    }
}
