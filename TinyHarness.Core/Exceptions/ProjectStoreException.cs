namespace TinyHarness.Core.Exceptions;

/// <summary>
///     <para>项目目录册的操作失败：路径非法、目录册损坏或版本不支持、超预算、获取保存锁超时、并发修改冲突或写入未可靠完成。</para>
///     <para>
///         A project-catalog failure: an invalid path, a damaged or unsupported catalog, a budget violation,
///         a save-lock timeout, a concurrent-edit conflict, or an unreliable write.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         异常消息携带目录册路径与原因上下文；出现本异常时不得把当前内存状态当作已保存成功，
///         也不得把损坏或版本不支持的目录册当作空目录册覆盖。
///     </para>
///     <para>
///         Messages carry the catalog path and cause context; when this exception is thrown the in-memory
///         state must not be treated as saved, and a damaged or unsupported catalog must never be overwritten
///         as if it were empty.
///     </para>
/// </remarks>
public sealed class ProjectStoreException : Exception
{
    /// <summary>
    ///     <para>创建携带原因说明的项目目录册异常。</para>
    ///     <para>Creates a project-store exception with the given reason.</para>
    /// </summary>
    public ProjectStoreException(string message) : base(message)
    {
    }

    /// <summary>
    ///     <para>创建携带原因说明与内部异常的项目目录册异常。</para>
    ///     <para>Creates a project-store exception with the given reason and inner exception.</para>
    /// </summary>
    public ProjectStoreException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
