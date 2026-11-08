using System.Text.Json;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     <para>
///         C1 可信名单文件（&lt;home&gt;\.sandbox-secrets\protected_paths.json）的应用侧维护：组合期
///         把组合器掌握的 C1 保护路径清单（权限系统实例路径、沙箱 home 与组件）以规范绝对路径数组
///         写入，helper 据此对授权豁免做"绝不触碰 C1"的第二道防线。目录由 provisioning 对沙箱组
///         永久 DENY，应用以真实用户写入；内容变化时才覆写。
///     </para>
///     <para>
///         Application-side maintenance of the C1 trusted-list file
///         (&lt;home&gt;\.sandbox-secrets\protected_paths.json): at composition the C1 protection paths
///         the composer knows (the permission-system instance paths, the sandbox home, and its
///         components) are written as a canonical absolute-path array so the helper can enforce a
///         second "never touch C1" line against authorization exemptions. Provisioning permanently
///         denies the directory to the sandbox group while the app writes as the real user; the file is
///         rewritten only when the content changes.
///     </para>
/// </summary>
public static class SandboxProtectedPathsStore
{
    /// <summary>protected_paths.json 的固定位置。The fixed location of protected_paths.json.</summary>
    public static string ResolvePath(WindowsSandboxComponents components) =>
        Path.Combine(components.CredentialsDirectory, "protected_paths.json");

    /// <summary>
    ///     <para>
    ///         组合期 C1 保护路径清单：权限系统实例路径（用户配置、共享信任文件与会话存储目录）
    ///         加上沙箱完整性路径（home、凭据目录、deny 簿记、cap_sid、执行组件目录与 users 文件），
    ///         规范化后去重保持顺序。路径全部来自组合期已校验的绝对路径。
    ///     </para>
    ///     <para>
    ///         The composition-time C1 protection manifest: the permission-system instance paths (the
    ///         user config, the shared trust file, and the session-store directory) plus the sandbox
    ///         integrity paths (the home, the credentials directory, deny bookkeeping, cap_sid, the
    ///         component executables, and the users file), canonicalized, deduplicated and
    ///         order-preserving. Every path is an absolute path already validated at composition.
    ///     </para>
    /// </summary>
    public static IReadOnlyList<string> ComposeManifest(
        WindowsSandboxComponents components, IReadOnlyList<string>? permissionSystemPaths)
    {
        ArgumentNullException.ThrowIfNull(components);
        var paths = new List<string>
        {
            components.SandboxHome,
            components.SetupExecutablePath,
            components.RunnerExecutablePath,
            components.UsersFilePath
        };
        paths.AddRange(components.ProtectedHomeSubpaths);
        if (permissionSystemPaths is not null) paths.AddRange(permissionSystemPaths);
        return SandboxIsolationPolicy.Deduplicate(paths.Select(Path.GetFullPath));
    }

    /// <summary>
    ///     <para>
    ///         写入 C1 清单（source-gen 序列化的规范绝对路径数组）；已有内容一致时不覆写。写入失败
    ///         抛出带上下文的异常——清单缺席会让授权豁免失去 C1 第二道防线，组合必须 fail closed。
    ///     </para>
    ///     <para>
    ///         Writes the C1 manifest (a source-generated canonical absolute-path array); an identical
    ///         existing file is left untouched. Write failures throw with context — a missing manifest
    ///         would strip the authorization exemptions of their C1 second line, so composition must
    ///         fail closed.
    ///     </para>
    /// </summary>
    public static void WriteIfChanged(WindowsSandboxComponents components, IReadOnlyList<string> protectedPaths)
    {
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(protectedPaths);

        var json = JsonSerializer.Serialize(protectedPaths.ToArray(),
                                            WindowsSandboxJsonContext.Default.StringArray);
        var path = ResolvePath(components);
        if (File.Exists(path))
            try
            {
                if (File.ReadAllText(path) == json) return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 读取失败按内容变化处理，走覆写路径并在写失败时抛出。
            }

        try
        {
            Directory.CreateDirectory(components.CredentialsDirectory);
            File.WriteAllText(path, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new
                InvalidOperationException($"Could not write the C1 protected-path manifest to '{path}'; the helper's " +
                                          "second defense line against authorization exemptions would be missing, refusing " +
                                          $"the composition (fail closed): {ex.Message}",
                                          ex);
        }
    }
}
