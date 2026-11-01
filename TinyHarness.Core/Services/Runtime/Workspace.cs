namespace TinyHarness.Core.Services.Runtime;

/// <summary>
///     <para>为文件工具解析路径并复查链接。默认限制在工作区；启用外部路径时，调用方必须先审批具体目标。</para>
///     <para>
///         Resolves file-tool paths and rechecks links. Confined by default; external paths require the caller to
///         authorize each concrete target.
///     </para>
///     <para>准备和执行分别进行检查。</para>
///     <para>Preparation and execution perform separate checks.</para>
///     <list type="bullet">
///         <item>
///             <see cref="ResolveInside" /> is a pure, lexical check used in Prepare:
///             normalized absolute path, confined unless external paths are explicitly enabled.
///         </item>
///         <item>
///             <see cref="EnsureFinalTargetInside" /> runs in Execute, right before the
///             file/directory is touched, because a symlink or junction inside the workspace
///             may have been (re)created between Prepare and Execute and can point outside
///             the root.
///         </item>
///     </list>
/// </summary>
public sealed class Workspace
{
    /// <summary>
    ///     <para>保存工作区根及外部路径解析开关；开关本身不授予访问权限。</para>
    ///     <para>Stores the workspace root and external-path resolution option; the option itself grants no access.</para>
    /// </summary>
    public Workspace(string root, bool allowExternalPaths = false)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("A workspace root is required.", nameof(root));

        Root               = Path.GetFullPath(root);
        AllowExternalPaths = allowExternalPaths;
    }

    public string Root { get; }

    /// <summary>
    ///     <para>是否允许准备外部目标；实际执行仍需权限引擎批准。</para>
    ///     <para>Whether external targets can be prepared; execution still requires permission-engine approval.</para>
    /// </summary>
    public bool AllowExternalPaths { get; }

    private static StringComparison Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    ///     <para>解析规范化绝对路径；未启用外部路径时拒绝词法越界，不读取文件或授予权限。</para>
    ///     <para>
    ///         Resolves an absolute path without reading files or granting access; rejects lexical escapes unless external
    ///         paths are enabled.
    ///     </para>
    /// </summary>
    public string ResolveInside(string path, string argumentName)
    {
        string full;
        try
        {
            // Relative model paths are relative to the workspace root, not the
            // process current directory.
            var combined = Path.IsPathRooted(path) ? path : Path.Combine(Root, path);
            full = Path.GetFullPath(combined);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException($"Argument '{argumentName}': '{path}' is not a valid path.", ex);
        }

        if (!AllowExternalPaths && !IsInside(Root, full))
            throw new
                InvalidDataException($"Argument '{argumentName}': path '{path}' escapes the workspace root '{Root}'.");

        return full;
    }

    /// <summary>
    ///     <para>访问前复查链接：内部目标不得逃逸工作区，外部目标不得通过链接改向其他位置。</para>
    ///     <para>
    ///         Rechecks links before access: internal targets cannot escape the workspace, and external targets cannot
    ///         redirect elsewhere.
    ///     </para>
    /// </summary>
    public void EnsureFinalTargetInside(string path, bool isDirectory, string what)
    {
        var current      = Path.GetFullPath(path);
        var currentIsDir = isDirectory;
        if (AllowExternalPaths && !IsInside(Root, current))
        {
            // 外部路径仍绑定审批时的词法目标；拒绝链接改向，不让批准范围暗中改变。
            while (current is not null)
            {
                var parent = Path.GetDirectoryName(current);
                if (parent is null) break;
                if (!string.Equals(current, ResolveLinkTarget(current, currentIsDir), Comparison))
                    throw new InvalidDataException($"{what} '{path}' contains a linked external path.");
                current      = parent;
                currentIsDir = true;
            }

            return;
        }

        if (!IsInside(Root, current))
            throw new InvalidDataException($"{what} '{path}' is outside the workspace root '{Root}'.");

        while (IsInside(Root, current))
        {
            var resolved = ResolveLinkTarget(current, currentIsDir);
            if (!IsInside(Root, resolved))
                throw new InvalidDataException($"{what} '{path}' resolves outside the workspace root ('{resolved}').");

            var parent = Path.GetDirectoryName(current);
            if (parent is null) break;

            current      = parent;
            currentIsDir = true;
        }
    }

    /// <summary>
    ///     把工作区内绝对路径转换为使用正斜杠的稳定显示路径。
    ///     Converts an absolute path inside the root to a workspace-relative display
    ///     path using forward slashes (stable for the model and for logs).
    /// </summary>
    public string ToDisplay(string absolutePath)
    {
        var relative = Path.GetRelativePath(Root, absolutePath);
        var display  = relative == string.Empty ? "." : relative.Replace('\\', '/');
        return display;
    }

    /// <summary>
    ///     按当前操作系统的大小写规则判断候选路径是否等于根目录或位于其后代目录中。
    ///     Tests whether a candidate equals or descends from a root using the current OS path-comparison rules.
    /// </summary>
    public static bool IsInside(string root, string candidate)
    {
        var rootFull = Path.GetFullPath(root);
        var candFull = Path.GetFullPath(candidate);

        if (string.Equals(rootFull, candFull, Comparison)) return true;

        return candFull.StartsWith(rootFull.EndsWith(Path.DirectorySeparatorChar)
                                       ? rootFull
                                       : rootFull + Path.DirectorySeparatorChar, Comparison);
    }

    /// <summary>
    ///     解析符号链接或 junction 链至最终绝对路径，并用固定循环上限防止链接环。
    ///     Resolves symlink/junction chains to the final absolute path. Returns the
    ///     input unchanged when it is not a link. A bounded loop guards against link
    ///     cycles.
    /// </summary>
    private static string ResolveLinkTarget(string path, bool isDirectory)
    {
        var current = path;
        for (var i = 0; i < 64; i++)
        {
            FileSystemInfo info   = isDirectory ? new DirectoryInfo(current) : new FileInfo(current);
            var            target = info.ResolveLinkTarget(true);
            if (target is null) return current;

            current = target.FullName;
        }

        throw new InvalidDataException($"Too many link levels resolving '{path}'.");
    }
}
