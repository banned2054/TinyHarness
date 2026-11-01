namespace TinyHarness.Core.Exceptions;

/// <summary>
///     <para>会话归档状态读取、校验或保存失败。</para>
///     <para>Session archive-state reading, validation, or persistence failed.</para>
/// </summary>
public sealed class SessionStoreArchiveException : Exception
{
    public SessionStoreArchiveException(string message) : base(message)
    {
    }

    public SessionStoreArchiveException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
