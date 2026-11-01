namespace TinyHarness.Core.Exceptions;

/// <summary>
///     <para>会话存储置顶状态读取、校验或写入失败；失败时不得把内存状态当作已保存。</para>
///     <para>A session-store pin-state read, validation, or write failure; failed state must never be treated as saved.</para>
/// </summary>
public sealed class SessionStorePinException : Exception
{
    /// <summary>
    ///     <para>创建置顶状态异常。</para>
    ///     <para>Creates a pin-state exception.</para>
    /// </summary>
    public SessionStorePinException(string message) : base(message)
    {
    }

    /// <summary>
    ///     <para>创建包含底层原因的置顶状态异常。</para>
    ///     <para>Creates a pin-state exception with its underlying cause.</para>
    /// </summary>
    public SessionStorePinException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
