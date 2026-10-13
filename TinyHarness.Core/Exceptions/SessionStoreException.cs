namespace TinyHarness.Core.Exceptions;

/// <summary>
///     受保护会话存储的操作失败：目录不满足保护要求、会话被其他进程独占、快照损坏、未知格式版本、
///     审计与快照不一致或写入未可靠完成。异常消息携带路径与原因上下文；出现本异常时不得把
///     当前内存状态当作已保存成功（PLAN §7.1）。
///     A protected-session-store failure: an unprotected directory, a session
///     held by another process, a damaged snapshot, an unknown format version,
///     snapshot/audit divergence, or an unreliable save. Messages carry path and
///     cause context; when this exception is thrown the in-memory state must not
///     be treated as saved (PLAN §7.1).
/// </summary>
public sealed class SessionStoreException : Exception
{
    public SessionStoreException(string message) : base(message)
    {
    }

    public SessionStoreException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
