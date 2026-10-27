using TinyHarness.Core.Exceptions;

namespace TinyHarness.Core.Models.Project;

/// <summary>
///     <para>项目关联目录条目；路径已规范化为绝对路径，是将来多目录扩展的最小单元。</para>
///     <para>
///         An associated project directory entry; the path is a normalized absolute path and the smallest unit for
///         future multi-root expansion.
///     </para>
/// </summary>
public sealed record ProjectRoot(string Path);

/// <summary>
///     <para>项目目录的规范化与比较：转绝对路径、统一尾部分隔符，并按当前平台路径大小写规则做词法精确匹配。</para>
///     <para>
///         Normalization and comparison for project roots: to an absolute path with unified trailing
///         separators, and lexical exact matching under the current platform's path case rules.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         匹配只做词法等价，不解析符号链接或 junction，也不推断文件系统物理等价；
///         物理身份、信任与授权由运行时的目录捕获与权限体系负责。
///     </para>
///     <para>
///         Matching stays lexical: no symlink or junction resolution, no filesystem physical-equivalence
///         inference. Physical identity, trust, and authorization remain the runtime's directory-capture
///         and permission systems' responsibility.
///     </para>
/// </remarks>
public static class ProjectRoots
{
    /// <summary>
    ///     <para>当前平台的目录路径比较规则；Windows 大小写不敏感，其他平台区分大小写。</para>
    ///     <para>The current platform's path comparison; case-insensitive on Windows, case-sensitive elsewhere.</para>
    /// </summary>
    internal static StringComparison Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    ///     <para>把目录转换为规范化绝对路径：完整解析并去掉尾部多余分隔符，卷根保留一个分隔符。</para>
    ///     <para>
    ///         Converts a directory to the normalized absolute form: fully resolved with trailing separators
    ///         removed, keeping one separator on volume roots.
    ///     </para>
    /// </summary>
    /// <exception cref="ProjectStoreException">
    ///     <para>路径为空或无法解析为绝对路径。</para>
    ///     <para>The path is empty or cannot be resolved to an absolute path.</para>
    /// </exception>
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ProjectStoreException($"Project root '{path}' is not a valid path: {ex.Message}", ex);
        }

        // 尾部分隔符不参与身份；卷根（如 C:\）与 Unix 根（/）保留原样以便辨认。
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (trimmed.Length == 0) return full;
        if (trimmed[^1] == ':') return trimmed + Path.DirectorySeparatorChar;
        return trimmed;
    }

    /// <summary>
    ///     <para>按当前平台规则比较两个已规范化目录是否为同一目录的等价词法路径。</para>
    ///     <para>Compares two normalized roots as equivalent lexical paths of the same directory under platform rules.</para>
    /// </summary>
    public static bool AreEqual(string left, string right) => string.Equals(left, right, Comparison);

    /// <summary>
    ///     <para>为新项目取名：默认取目录末级名称；卷根等没有可用末级名称时退回完整路径。</para>
    ///     <para>
    ///         Names a new project: the final directory segment by default, or the full path when a volume
    ///         root leaves no usable segment.
    ///     </para>
    /// </summary>
    public static string DefaultDisplayName(string normalizedRoot)
    {
        var name = Path.GetFileName(normalizedRoot);
        return string.IsNullOrWhiteSpace(name) ? normalizedRoot : name;
    }
}
