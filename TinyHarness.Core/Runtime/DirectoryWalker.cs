namespace TinyHarness.Core.Runtime;

/// <summary>
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

    /// <summary>Files only (used by search_text).</summary>
    public static WalkResult CollectFiles(string rootAbs, int cap, CancellationToken cancellationToken)
        => Collect(rootAbs, recursive: true, maxDepth: null, cap, includeDirectories: false, cancellationToken);

    /// <summary>Files and directories (used by list_files).</summary>
    public static WalkResult CollectEntries(string rootAbs, bool recursive, int? maxDepth, int cap,
                                            CancellationToken cancellationToken)
        => Collect(rootAbs, recursive, maxDepth, cap, includeDirectories: true, cancellationToken);

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
