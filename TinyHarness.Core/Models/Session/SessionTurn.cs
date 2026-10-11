namespace TinyHarness.Core.Models.Session;

/// <summary>
///     一次用户输入及对此输入的完整 Agent 响应（PLAN §5）。回合可包含多次模型请求与工具
///     调用；对同一回合的每次执行尝试都记录为 <see cref="SessionRun" />，回合自身不可变。
///     One user input together with the complete agent response to it (PLAN §5). A turn
///     may contain several model requests and tool calls; every execution attempt for the
///     turn is recorded as a <see cref="SessionRun" /> while the turn itself stays immutable.
/// </summary>
public sealed class SessionTurn
{
    private readonly List<SessionRun> _runs = [];

    public SessionTurn(string id, string userInput, DateTimeOffset? createdUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(userInput);
        Id         = id;
        UserInput  = userInput;
        CreatedUtc = createdUtc ?? DateTimeOffset.UtcNow;
    }

    public string Id { get; }

    /// <summary>触发本回合的用户输入原文。The verbatim user input that started this turn.</summary>
    public string UserInput { get; }

    public DateTimeOffset CreatedUtc { get; }

    /// <summary>本回合的全部执行尝试，按提交顺序排列。Every execution attempt for this turn, in submission order.</summary>
    public IReadOnlyList<SessionRun> Runs => _runs;

    internal void AddRun(SessionRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        _runs.Add(run);
    }
}
