namespace TinyHarness.Core.Models.Session;

/// <summary>
///     宿主事件信封（PLAN §6）：种类、顺序号、会话/回合/运行关联 ID、时间戳与负载。
///     事件只在内存中流转，本切片不定义任何序列化格式。
///     The host event envelope (PLAN §6): kind, sequence number, session/turn/run
///     correlation ids, timestamp and payload. Events flow in memory only; this slice
///     defines no serialization format.
/// </summary>
public sealed record HostEvent
{
    public required HostEventKind Kind { get; init; }

    /// <summary>
    ///     运行内递增的顺序号（每个运行从 1 开始），与 <see cref="RunId" /> 组合构成会话内
    ///     的全序；M12.1 中同一会话同一时刻只有一个活动运行，事件到达顺序即会话顺序。
    ///     Sequence increasing within one run (starting at 1); combined with
    ///     <see cref="RunId" /> it forms a total order inside the session. M12.1 keeps a
    ///     single active run per session, so arrival order equals session order.
    /// </summary>
    public long Sequence { get; init; }

    public string? SessionId { get; init; }

    public string? TurnId { get; init; }

    public string? RunId { get; init; }

    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>种类相关的负载对象；各成员的负载类型记录在 <see cref="HostEventKind" /> 上。The kind-specific payload; payload types are documented on <see cref="HostEventKind" />.</summary>
    public object? Payload { get; init; }
}
