namespace TinyHarness.Core.Runtime;

/// <summary>
/// 供只读文件工具使用的受限目录遍历器。结果排序稳定，并限制条目数、支持取消，
/// 同时跳过常见构建目录与版本控制目录。重解析点只列出但不跟随，避免越过工作区边界。
///
/// Recursive workspace walk used by the read-only file tools. Produces
/// deterministic (sorted) results with per-walk entry caps, cancellation and an
/// exclusion list for well-known build/version-control directories.
///
/// Rules:
/// <list type="bullet">
/// <item>An explicitly requested root is never filtered; exclusions apply to its
/// children.</item>
/// <item>Directories named .git/.hg/.svn/bin/obj/node_modules/.vs/.vscode/.idea
/// are omitted and never descended into.</item>
/// <item>Reparse points (symlinks/junctions) are listed but never followed, so a
/// mid-tree directory link cannot leak enumeration outside the workspace. A
/// file entry that is itself a link can still appear in the results, so callers
/// that open returned entries (search_text) re-verify the final resolved target
/// before reading. Explicitly requested roots are boundary-checked by the
/// caller in Execute.</item>
/// <item>The BCL directory enumeration is synchronous, so the cancellation token
/// is observed between entries.</item>
/// </list>
/// </summary>
internal static class DirectoryWalker
{
    private static readonly string[] ExcludedNames =
    [
        ".git", ".hg", ".svn", "bin", "obj", "node_modules", ".vs", ".vscode", ".idea",
    ];

    public sealed record WalkResult(IReadOnlyList<string> Entries, bool Truncated);

    /// <summary>
    /// 递归收集普通文件，供 search_text 使用。
    /// Recursively collects files only for search_text.
    /// </summary>
    public static WalkResult CollectFiles(string rootAbs, int cap, CancellationToken cancellationToken)
        => Collect(rootAbs, recursive: true, maxDepth: null, cap, includeDirectories: false, cancellationToken);

    /// <summary>
    /// 按递归与深度设置收集文件和目录，供 list_files 使用。
    /// Collects files and directories according to the recursion and depth settings for list_files.
    /// </summary>
    public static WalkResult CollectEntries(string rootAbs, bool recursive, int? maxDepth, int cap,
                                            CancellationToken cancellationToken)
        => Collect(rootAbs, recursive, maxDepth, cap, includeDirectories: true, cancellationToken);

    /// <summary>
    /// 执行实际遍历，在条目上限内返回排序结果，并标记是否因上限提前停止。
    /// Performs the bounded walk, returns sorted entries, and reports whether the cap stopped enumeration.
    /// </summary>
    private static WalkResult Collect(string rootAbs, bool recursive, int? maxDepth, int cap,
                                      bool includeDirectories, CancellationToken cancellationToken)
    {
        var entries   = new List<string>();
        var truncated = false;

        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((rootAbs, 0));

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (dir, depth) = stack.Pop();
            if (entries.Count >= cap)
            {
                truncated = true;
                break;
            }

            string[] children;
            try
            {
                children = Directory.GetFileSystemEntries(dir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue; // Unreadable directory: keep walking the rest.
            }

            Array.Sort(children, StringComparer.Ordinal);
            for (var i = children.Length - 1; i >= 0; i--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entries.Count >= cap)
                {
                    truncated = true;
                    break;
                }

                var child = children[i];
                var isDir = Directory.Exists(child);
                if (!isDir && !File.Exists(child))
                {
                    continue; // Vanished between enumeration and classification.
                }

                if (isDir)
                {
                    var name = Path.GetFileName(child);
                    if (ExcludedNames.Contains(name, StringComparer.Ordinal))
                    {
                        continue;
                    }

                    if (IsReparsePoint(child))
                    {
                        if (includeDirectories)
                        {
                            entries.Add(child);
                        }

                        continue; // Never descend through links/junctions.
                    }

                    if (recursive && (maxDepth is null || depth + 1 <= maxDepth))
                    {
                        stack.Push((child, depth + 1));
                    }
                }

                if (includeDirectories || !isDir)
                {
                    entries.Add(child);
                }
            }
        }

        entries.Sort(StringComparer.Ordinal);
        return new WalkResult(entries, truncated);
    }

    /// <summary>
    /// 判断路径是否为符号链接或 junction；无法读取属性时按重解析点处理以保持保守安全。
    /// Detects symlinks/junctions and conservatively treats unreadable entries as reparse points.
    /// </summary>
    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return true; // Treat unreadable entries as opaque; do not descend.
        }
    }
}
