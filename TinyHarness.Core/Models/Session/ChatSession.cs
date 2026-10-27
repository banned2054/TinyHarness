using TinyHarness.Core.Models.Persistence;

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
public sealed partial class ChatSession
{
    private readonly List<SessionTurn>           _turns           = [];
    private readonly List<SessionModelSelection> _modelSelections = [];

    /// <summary>
    ///     <para>创建固定执行目录的未绑定会话，不读写持久化数据。</para>
    ///     <para>Creates an unbound session with a fixed execution directory without accessing persistence.</para>
    /// </summary>
    public ChatSession(
        string id, string workspaceRoot, SessionModelIdentity model, DateTimeOffset? createdUtc = null)
        : this(id, workspaceRoot, model, createdUtc, null)
    {
    }

    /// <summary>
    ///     <para>创建带可选初始项目归属的会话；持久化由调用方提交，归属不授予执行权限。</para>
    ///     <para>Creates a session with optional initial project ownership; the caller persists it, and ownership grants no execution permissions.</para>
    /// </summary>
    public ChatSession(
        string id, string workspaceRoot, SessionModelIdentity model, DateTimeOffset? createdUtc,
        string? projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(model);
        if (projectId is not null && !SessionLogProjectBinding.IsValidProjectId(projectId))
            throw new ArgumentException("A project id must contain exactly 32 lowercase hexadecimal characters.",
                                        nameof(projectId));
        Id            = id;
        WorkspaceRoot = Path.GetFullPath(workspaceRoot);
        InitialModel  = model;
        CreatedUtc    = createdUtc ?? DateTimeOffset.UtcNow;
        ProjectId     = projectId;
    }

    public string Id { get; }

    /// <summary>
    ///     <para>会话固定的工作区根绝对路径。</para>
    ///     <para>The session's fixed absolute workspace root.</para>
    /// </summary>
    public string WorkspaceRoot { get; }

    /// <summary>
    ///     <para>当前选择的模型身份；未采用选择记录的旧会话沿用初始模型。</para>
    ///     <para>The selected model identity; legacy sessions without selections retain their initial model.</para>
    /// </summary>
    public SessionModelIdentity Model => ModelSelection?.Model ?? InitialModel;

    public SessionModelIdentity                 InitialModel    { get; }
    public SessionModelSelection?               ModelSelection  => _modelSelections.LastOrDefault();
    public IReadOnlyList<SessionModelSelection> ModelSelections => _modelSelections.AsReadOnly();

    /// <summary>
    ///     <para>
    ///         会话的项目归属；旧会话未绑定时为 null，绑定后只能保持或幂等重申同值，不支持转移。
    ///     </para>
    ///     <para>
    ///         The session's project ownership; null for unbound legacy sessions, and once bound it can only
    ///         stay or idempotently restate the same value — ownership is never transferred.
    ///     </para>
    /// </summary>
    public string? ProjectId { get; private set; }

    /// <summary>
    ///     <para>
    ///         绑定项目归属：同值重复绑定幂等，绑定到不同项目被拒绝。仅在归属记录可靠提交到会话日志后调用，
    ///         使内存会话与日志镜像一致；未提交归属就绑定会使后续提交被 writer 的一致性检查拒绝。
    ///     </para>
    ///     <para>
    ///         Binds project ownership: rebinding the same value is idempotent while binding to a different
    ///         project is rejected. Call only after the ownership record is durably committed to the session log
    ///         so the in-memory session mirrors the journal; binding before that commit makes later commits fail
    ///         the writer's consistency check.
    ///     </para>
    /// </summary>
    /// <exception cref="ArgumentException">
    ///     <para>项目 ID 形态非法。</para>
    ///     <para>The project id is malformed.</para>
    /// </exception>
    /// <exception cref="InvalidOperationException">
    ///     <para>已绑定到其他项目时尝试改绑。</para>
    ///     <para>Attempting to rebind a session already owned by a different project.</para>
    /// </exception>
    public void BindProject(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        if (!SessionLogProjectBinding.IsValidProjectId(projectId))
            throw new ArgumentException("A project id must contain exactly 32 lowercase hexadecimal characters.",
                                        nameof(projectId));
        if (ProjectId is not null && !string.Equals(ProjectId, projectId, StringComparison.Ordinal))
            throw new
                InvalidOperationException($"Session '{Id}' is already bound to project '{ProjectId}'; project ownership is never transferred to '{projectId}'.");
        ProjectId = projectId;
    }

    internal void RecordModelSelection(SessionModelSelection selection)
    {
        selection.Validate();
        var previousRevision = ModelSelection?.Revision ?? 0;
        if (selection.Revision != previousRevision + 1)
            throw new InvalidDataException("Model selection revisions must be contiguous.");
        // /2 记录显式携带前驱；与修订递增推导不一致即拒绝，不静默修补。
        if (!selection.IsLegacySchema && selection.PreviousRevision != previousRevision)
            throw new InvalidDataException("Model selection predecessors must continue the recorded chain.");
        _modelSelections.Add(selection);
    }

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
