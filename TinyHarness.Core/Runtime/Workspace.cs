namespace TinyHarness.Core.Runtime;

/// <summary>
/// 为文件工具提供相对工作区根目录的路径解析与边界检查。Prepare 阶段做纯词法检查，
/// Execute 阶段再解析符号链接与 junction，防止两阶段之间目标变化后逃逸工作区。
///
/// Root-relative path resolution for file tools. Every path a model
/// passes to a tool is resolved against the configured workspace root and must
/// stay inside it.
///
/// Two checks exist because they run at different times:
/// <list type="bullet">
/// <item><see cref="ResolveInside"/> is a pure, lexical check used in Prepare:
/// normalized absolute path, no ".." or outside absolute path.</item>
/// <item><see cref="EnsureFinalTargetInside"/> runs in Execute, right before the
/// file/directory is touched, because a symlink or junction inside the workspace
/// may have been (re)created between Prepare and Execute and can point outside
/// the root.</item>
/// </list>
/// </summary>
public sealed class Workspace
{
    public string Root { get; }

    /// <summary>
    /// 验证并保存规范化的绝对工作区根目录。
    /// Validates and stores the normalized absolute workspace root.
    /// </summary>
    public Workspace(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("A workspace root is required.", nameof(root));
        }

        Root = Path.GetFullPath(root);
    }

    /// <summary>
    /// 将用户路径解析为工作区内的规范化绝对路径；拒绝“..”或外部绝对路径造成的词法逃逸。
    ///
    /// Resolves a user-supplied path to a normalized absolute path that stays
    /// inside the workspace root. Throws <see cref="InvalidDataException"/> on
    /// lexical escapes ("..", an outside absolute path).
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

        if (!IsInside(Root, full))
        {
            throw new InvalidDataException(
                $"Argument '{argumentName}': path '{path}' escapes the workspace root '{Root}'.");
        }

        return full;
    }

    /// <summary>
    /// 在真正访问前解析目标及各级祖先链接，确认最终位置仍位于工作区内。
    ///
    /// Verifies at execution time that the final target of <paramref name="path"/>
    /// stays inside the workspace root. Checks the entry itself (when it is a
    /// symlink/junction) and every ancestor directory, because a junction can sit
    /// on any level of the path (e.g. root/link/secret.txt where 'link' points
    /// outside). Throws <see cref="InvalidDataException"/> when a link escapes.
    /// </summary>
    public void EnsureFinalTargetInside(string path, bool isDirectory, string what)
    {
        var current     = Path.GetFullPath(path);
        var currentIsDir = isDirectory;
        if (!IsInside(Root, current))
        {
            throw new InvalidDataException(
                $"{what} '{path}' is outside the workspace root '{Root}'.");
        }

        while (IsInside(Root, current))
        {
            var resolved = ResolveLinkTarget(current, currentIsDir);
            if (!IsInside(Root, resolved))
            {
                throw new InvalidDataException(
                    $"{what} '{path}' resolves outside the workspace root ('{resolved}').");
            }

            var parent = Path.GetDirectoryName(current);
            if (parent is null)
            {
                break;
            }

            current      = parent;
            currentIsDir = true;
        }
    }

    /// <summary>
    /// 把工作区内绝对路径转换为使用正斜杠的稳定显示路径。
    ///
    /// Converts an absolute path inside the root to a workspace-relative display
    /// path using forward slashes (stable for the model and for logs).
    /// </summary>
    public string ToDisplay(string absolutePath)
    {
        var relative = Path.GetRelativePath(Root, absolutePath);
        var display  = relative == string.Empty ? "." : relative.Replace('\\', '/');
        return display;
    }

    /// <summary>
    /// 按当前操作系统的大小写规则判断候选路径是否等于根目录或位于其后代目录中。
    /// Tests whether a candidate equals or descends from a root using the current OS path-comparison rules.
    /// </summary>
    public static bool IsInside(string root, string candidate)
    {
        var rootFull = Path.GetFullPath(root);
        var candFull = Path.GetFullPath(candidate);

        if (string.Equals(rootFull, candFull, Comparison))
        {
            return true;
        }

        return candFull.StartsWith(rootFull.EndsWith(Path.DirectorySeparatorChar)
                                       ? rootFull
                                       : rootFull + Path.DirectorySeparatorChar, Comparison);
    }

    private static StringComparison Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// 解析符号链接或 junction 链至最终绝对路径，并用固定循环上限防止链接环。
    ///
    /// Resolves symlink/junction chains to the final absolute path. Returns the
    /// input unchanged when it is not a link. A bounded loop guards against link
    /// cycles.
    /// </summary>
    private static string ResolveLinkTarget(string path, bool isDirectory)
    {
        var current = path;
        for (var i = 0; i < 64; i++)
        {
            FileSystemInfo info = isDirectory ? new DirectoryInfo(current) : new FileInfo(current);
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is null)
            {
                return current;
            }

            current = target.FullName;
        }

        throw new InvalidDataException($"Too many link levels resolving '{path}'.");
    }
}
