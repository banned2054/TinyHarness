namespace TinyHarness.Core.Exceptions;

/// <summary>
///     <para>
///         会话业务数据库的操作失败：库缺失却要求迁移、版本不受支持、文件损坏、内容不属于本程序或读写失败。
///         异常消息携带路径与原因上下文；业务库保存不可从 JSONL 重建的组织状态，失败时不得按空库继续，
///         也不得被当作会话日志或列表索引的失败。
///     </para>
///     <para>
///         A session business-database failure: migrating a missing database, an unsupported version, a damaged
///         file, content that does not belong to this program, or a failed read/write. Messages carry path and
///         cause context; the business database holds organization state that cannot be rebuilt from JSONL, so a
///         failure must never continue as an empty store and is never a session-log or list-index failure.
///     </para>
/// </summary>
public sealed class SessionStoreDatabaseException : Exception
{
    public SessionStoreDatabaseException(string message) : base(message)
    {
    }

    public SessionStoreDatabaseException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
