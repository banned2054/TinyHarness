namespace TinyHarness.Core.Models.Session;

/// <summary>
///     一次连续会话的最小身份与状态（PLAN §5）：固定工作区、稳定会话 ID、模型身份引用和
///     全部用户回合。会话不包含密钥或可执行授权；持久化与跨进程恢复不在本类型职责内。
///     The minimal identity and state of one continuous session (PLAN §5): a fixed
///     workspace, a stable session id, the referenced model identity and every user turn.
///     A session carries no secrets and no executable authorization; persistence and
///     cross-process resume are outside this type's responsibility.
/// </summary>
public sealed class ChatSession
{
    private readonly List<SessionTurn> _turns = [];

    public ChatSession(string id, string workspaceRoot, SessionModelIdentity model,
                       DateTimeOffset? createdUtc = null)
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

    /// <summary>会话固定的工作区根（绝对路径）。The session's fixed workspace root (absolute path).</summary>
    public string WorkspaceRoot { get; }

    /// <summary>会话创建时固定的模型身份。The model identity fixed at session creation.</summary>
    public SessionModelIdentity Model { get; }

    public DateTimeOffset CreatedUtc { get; }

    /// <summary>已提交的用户回合，按提交顺序排列。Every submitted user turn, in submission order.</summary>
    public IReadOnlyList<SessionTurn> Turns => _turns;

    internal void AddTurn(SessionTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        _turns.Add(turn);
    }
}
