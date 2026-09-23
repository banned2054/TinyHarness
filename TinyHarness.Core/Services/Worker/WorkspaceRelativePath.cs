namespace TinyHarness.Core.Services.Worker;

/// <summary>
///     判断路径字符串是否具备“工作区相对路径提示”的形状，跨宿主平台行为一致：拒绝 Windows
///     盘符前缀（C:\foo、C:/foo、C:foo）、UNC 前缀（\\host\share、//host/share）以及任一风格
///     的根化路径（/foo、\foo）。只做字符串判断，不解析路径、不访问文件；Path.IsPathRooted
///     仅作为当前平台额外形态的兜底，不作为 Windows 形状的依据。
///     Decides whether a path string has the shape of a workspace-relative hint, with identical behavior
///     on every host platform: Windows drive prefixes (C:\foo, C:/foo, C:foo), UNC prefixes
///     (\\host\share, //host/share) and rooted paths of any style (/foo, \foo) are rejected. Pure string
///     checks — no path resolution, no file access; Path.IsPathRooted only acts as a catch-all for
///     platform-specific shapes and is never the basis for rejecting Windows shapes.
/// </summary>
internal static class WorkspaceRelativePath
{
    /// <summary>
    ///     调用方必须保证传入非空字符串；空白检查由各校验器在形状判断前完成。
    ///     Callers must pass a non-empty string; blank checks happen in the validators before this shape check.
    /// </summary>
    internal static bool IsAcceptableShape(string path)
    {
        return !HasWindowsDrivePrefix(path) && !HasUncPrefix(path) && !IsRootedOnAnyPlatform(path);
    }

    private static bool HasWindowsDrivePrefix(string path)
    {
        return path is [_, ':', ..] && char.IsAsciiLetter(path[0]);
    }

    private static bool HasUncPrefix(string path)
    {
        return path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);
    }

    private static bool IsRootedOnAnyPlatform(string path)
    {
        return path[0] is '/' or '\\' || Path.IsPathRooted(path);
    }
}
