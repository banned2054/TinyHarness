namespace TinyHarness.Core.Models.Tools;

/// <summary>
///     <para>权限引擎 worker 模式与 NeedsInspection 阻断共用的只读能力分类。</para>
///     <para>
///         The read-only capability classification shared by permission-engine worker mode and
///         NeedsInspection blocking.
///     </para>
/// </summary>
/// <remarks>
///     <para>只读能力不修改工作区或启动进程；session.work 只记录会话陈述。其他能力（含未知能力）均按可能产生副作用处理。</para>
///     <para>
///         Read-only capabilities neither modify the workspace nor start processes; session.work records session claims
///         only.
///         all others, including unknown capabilities, are treated as potentially side-effecting.
///     </para>
/// </remarks>
public static class ToolCapabilities
{
    private static readonly HashSet<string> ReadOnlyCapabilities = new(StringComparer.Ordinal)
    {
        "filesystem.list",
        "filesystem.search",
        "filesystem.read",
        "session.work"
    };

    /// <summary>
    ///     <para>能力是否只读，即不写入工作区或执行进程。</para>
    ///     <para>Whether the capability is read-only: no workspace writes or process execution.</para>
    /// </summary>
    public static bool IsReadOnly(string capability) => ReadOnlyCapabilities.Contains(capability);
}
