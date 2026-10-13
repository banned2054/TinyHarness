namespace TinyHarness.Core.Models.Persistence;

/// <summary>
///     受保护会话存储的追加审计行（M13.1，PLAN §7.1）：与快照的原子替换一一对应的提交记录。
///     读取端用最大已提交序号与快照的 <see cref="PersistedSession.CommitSequence" /> 对账，
///     不一致即报告上下文错误而不是静默展示可疑状态。
///     One appended audit line of the protected session store (M13.1, PLAN §7.1):
///     a commit record paired with each atomic snapshot replacement. Readers
///     reconcile the maximum committed sequence against the snapshot's
///     <see cref="PersistedSession.CommitSequence" /> and report a contextual
///     error on divergence instead of silently showing a suspect state.
/// </summary>
public sealed record SessionStoreAuditRecord
{
    /// <summary>成功原子替换快照后的提交记录。The commit record appended after a successful atomic snapshot replacement.</summary>
    public const string CommittedKind = "session.committed";

    public string SessionId { get; init; } = string.Empty;

    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;

    public string Kind { get; init; } = string.Empty;

    /// <summary>本次提交对应的快照提交序号。The snapshot commit sequence this record confirms.</summary>
    public long Sequence { get; init; }
}
