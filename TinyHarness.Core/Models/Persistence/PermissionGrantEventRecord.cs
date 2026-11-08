namespace TinyHarness.Core.Models.Persistence;

/// <summary>
///     <para>
///         授权事件必需记录（permission-grant-event/1）：授权生命周期（授予/消费/撤销/到期/失效）
///         与可信授权变更（拒绝记忆解除）的追加型审计事实。旧程序按未知控制记录拒读，
///         符合仓库必需记录哲学；恢复不重建任何可执行授权。
///     </para>
///     <para>
///         The required permission-grant-event/1 record: an append-only audit fact of the grant lifecycle
///         (granted, consumed, revoked, expired, invalidated) and trusted authorization changes (denial-memory
///         releases). Older programs reject it as an unknown control record, matching the repository's required-
///         record philosophy; recovery never rebuilds executable authority from it.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         Targets 与 Reason 由写入方按会话日志脱敏惯例处理后落盘；授权变更事件的
///         <see cref="GrantId" /> 为空串且 <see cref="Scope" /> 为 <c>none</c>。
///     </para>
///     <para>
///         Targets and Reason are redacted by the writer under the session-log convention; an
///         authorization-changed event carries an empty <see cref="GrantId" /> and the
///         <c>none</c> <see cref="Scope" />.
///     </para>
/// </remarks>
public sealed record PermissionGrantEventRecord
{
    /// <summary>当前记录协议标识。The current record schema identifier.</summary>
    public const string CurrentSchema = "tinyharness.permission-grant-event/1";

    /// <summary>授权事件的稳定事件名。The stable event names of the grant lifecycle.</summary>
    public static readonly IReadOnlyList<string> KnownEvents =
        ["granted", "consumed", "revoked", "expired", "invalidated", "authorization-changed"];

    /// <summary>协议标识；必须等于 <see cref="CurrentSchema" />。The schema; must equal <see cref="CurrentSchema" />.</summary>
    public required string Schema { get; init; }

    /// <summary>
    ///     <para>授权标识；授权变更事件为空串。</para>
    ///     <para>The grant id; empty for authorization-changed events.</para>
    /// </summary>
    public required string GrantId { get; init; }

    /// <summary>
    ///     <para>事件名：granted/consumed/revoked/expired/invalidated/authorization-changed。</para>
    ///     <para>The event: granted, consumed, revoked, expired, invalidated, or authorization-changed.</para>
    /// </summary>
    public required string Event { get; init; }

    /// <summary>
    ///     <para>授权范围（single-operation/turn/session）；授权变更事件为 none。</para>
    ///     <para>The grant scope (single-operation, turn, or session); none for authorization-changed events.</para>
    /// </summary>
    public required string Scope { get; init; }

    /// <summary>
    ///     <para>授权能力（如 filesystem.write）；授权变更事件为 authorization。</para>
    ///     <para>The granted capability (for example filesystem.write); authorization for authorization-changed events.</para>
    /// </summary>
    public required string Capability { get; init; }

    /// <summary>
    ///     <para>授权目标摘要（已按会话日志惯例脱敏）；授权变更事件为空列表。</para>
    ///     <para>The grant target summary (redacted per the session-log convention); empty for authorization-changed events.</para>
    /// </summary>
    public required IReadOnlyList<string> Targets { get; init; }

    /// <summary>
    ///     <para>授权绑定的策略身份修订（完整身份串）；授权变更事件为空串。</para>
    ///     <para>
    ///         The policy-identity revision the grant binds (the full identity string); empty for authorization-changed
    ///         events.
    ///     </para>
    /// </summary>
    public required string PolicyIdentityRevision { get; init; }

    /// <summary>
    ///     <para>可选原因（如可信授权变更的具体触发）；生命周期事件可缺省。</para>
    ///     <para>An optional reason (such as the concrete trusted-authorization trigger); lifecycle events may omit it.</para>
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>事件发生时间（UTC）。When the event occurred (UTC).</summary>
    public required DateTimeOffset Utc { get; init; }

    /// <summary>
    ///     <para>校验记录结构与取值：协议一致、事件与范围在已知集合内、授权变更事件以外的字段约束。</para>
    ///     <para>
    ///         Validates structure and values: schema agreement, known event and scope sets, and the field
    ///         constraints that distinguish authorization-changed events from lifecycle events.
    ///     </para>
    /// </summary>
    public void Validate()
    {
        if (!string.Equals(Schema, CurrentSchema, StringComparison.Ordinal))
            throw new InvalidDataException($"Permission grant event schema must be '{CurrentSchema}'.");
        if (!KnownEvents.Contains(Event, StringComparer.Ordinal))
            throw new InvalidDataException($"Permission grant event has an unknown event '{Event}'.");
        if (string.IsNullOrWhiteSpace(Capability))
            throw new InvalidDataException("Permission grant event must carry a capability.");
        if (string.IsNullOrWhiteSpace(PolicyIdentityRevision) && Event != "authorization-changed")
            throw new
                InvalidDataException("A grant lifecycle event must carry the bound policy-identity revision.");
        if (Event == "authorization-changed")
        {
            if (GrantId.Length > 0 || Scope != "none" || Targets.Count > 0)
                throw new
                    InvalidDataException("An authorization-changed event carries no grant id, scope none, and no targets.");
            if (string.IsNullOrWhiteSpace(Reason))
                throw new InvalidDataException("An authorization-changed event must carry its release reason.");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(GrantId))
                throw new InvalidDataException("A grant lifecycle event must carry the grant id.");
            if (Scope is not ("single-operation" or "turn" or "session"))
                throw new InvalidDataException($"Permission grant event has an unknown scope '{Scope}'.");
        }

        if (Utc == default) throw new InvalidDataException("Permission grant event must carry its occurrence time.");
    }
}
