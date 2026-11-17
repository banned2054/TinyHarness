namespace TinyHarness.Core.Exceptions;

/// <summary>
///     <para>
///         会话存储迁移或权威切换的失败：标记损坏、源在导入期间发生变化、迁移被占用、库身份不符、
///         迁移未完成却要求生产读写等。消息携带路径与不含凭据的原因，失败类别由
///         <see cref="Kind" /> 区分，调用方不按文案分类。
///     </para>
///     <para>
///         A session-store migration or authority-switching failure: a damaged marker, sources changing during the
///         import, a held migration lease, an identity mismatch, or production access before migration completes.
///         Messages carry the path and a credential-free reason, and <see cref="Kind" /> carries the category so
///         callers never classify by message text.
///     </para>
/// </summary>
public sealed class SessionStoreMigrationException : Exception
{
    /// <summary>
    ///     <para>创建带失败类别的迁移异常。</para>
    ///     <para>Creates a migration exception carrying a failure category.</para>
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
    public SessionStoreMigrationException(string message, SessionStoreFailureKind kind, Exception? innerException = null)
        : base(message, innerException) => Kind = kind;

    /// <summary>
    ///     <para>失败类别。</para>
    ///     <para>The failure category.</para>
    /// </summary>
    public SessionStoreFailureKind Kind { get; }
}
