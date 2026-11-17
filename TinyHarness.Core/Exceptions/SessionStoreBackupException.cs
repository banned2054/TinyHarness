namespace TinyHarness.Core.Exceptions;

/// <summary>
///     <para>
///         会话业务库备份、校验或恢复的失败：备份来源缺失或损坏、版本不受支持、库身份与迁移标记不符、
///         恢复目标被占用或不可写。失败类别由 <see cref="Kind" /> 区分。
///     </para>
///     <para>
///         A business-database backup, verification, or restore failure: a missing or damaged backup source, an
///         unsupported version, a backup identity mismatching the migration marker, or an occupied or unwritable
///         restore target. <see cref="Kind" /> carries the failure category.
///     </para>
/// </summary>
public sealed class SessionStoreBackupException : Exception
{
    /// <summary>
    ///     <para>创建带失败类别的备份/恢复异常。</para>
    ///     <para>Creates a backup/restore exception carrying a failure category.</para>
    /// </summary>
    /// <param name="message">
    ///     <para>包含路径与原因的消息。</para>
    ///     <para>A message with the path and cause.</para>
    /// </param>
    /// <param name="kind">
    ///     <para>失败类别。</para>
    ///     <para>The failure category.</para>
    /// </param>
    /// <param name="innerException">
    ///     <para>底层原因；没有时为 null。</para>
    ///     <para>The underlying cause, or null.</para>
    /// </param>
    public SessionStoreBackupException(string message, SessionStoreFailureKind kind, Exception? innerException = null)
        : base(message, innerException) => Kind = kind;

    /// <summary>
    ///     <para>失败类别。</para>
    ///     <para>The failure category.</para>
    /// </summary>
    public SessionStoreFailureKind Kind { get; }
}
