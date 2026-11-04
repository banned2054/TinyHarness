namespace TinyHarness.Core.Exceptions;

/// <summary>
///     <para>
///         会话列表索引的操作失败：数据库缺失时请求了读取、版本不受支持、文件损坏、不可写或锁等待耗尽。
///         异常消息携带路径与原因上下文；索引失败不得被当作权威日志失败，也不得阻塞或改写 JSONL。
///     </para>
///     <para>
///         A session-list-index failure: reading a missing database, an unsupported version, a damaged or unwritable
///         file, or exhausted lock waiting. Messages carry path and cause context; an index failure must never be
///         treated as a log failure, block the JSONL chain, or rewrite authoritative logs.
///     </para>
/// </summary>
public sealed class SessionIndexException : Exception
{
    public SessionIndexException(string message) : base(message)
    {
    }

    public SessionIndexException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
