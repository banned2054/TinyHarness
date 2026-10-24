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

    public ChatSession(
        string id, string workspaceRoot, SessionModelIdentity model, DateTimeOffset? createdUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(model);
        Id            = id;
        WorkspaceRoot = Path.GetFullPath(workspaceRoot);
        InitialModel  = model;
        CreatedUtc    = createdUtc ?? DateTimeOffset.UtcNow;
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
