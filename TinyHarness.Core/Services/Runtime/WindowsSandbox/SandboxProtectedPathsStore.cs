using System.Text.Json;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     <para>
///         C1 可信名单文件（&lt;home&gt;\.sandbox-secrets\protected_paths.json）的应用侧维护：组合期
///         把组合器掌握的 C1 保护路径清单（权限系统实例路径、沙箱 home 与组件）以规范绝对路径数组
///         写入，helper 据此对授权豁免做"绝不触碰 C1"的第二道防线。目录由 provisioning 对沙箱组
///         永久 DENY，应用以真实用户写入；跨进程加锁合并已有保护路径并在内容变化时原子替换，
///         使共享同一 home 但使用各自安装目录的 CLI 与 Desktop 均保留对两端组件的保护。
///     </para>
///     <para>
///         Application-side maintenance of the C1 trusted-list file
///         (&lt;home&gt;\.sandbox-secrets\protected_paths.json): at composition the C1 protection paths
///         the composer knows (the permission-system instance paths, the sandbox home, and its
///         components) are merged under a cross-process lock and atomically replaced when changed so
///         both CLI and Desktop sharing one home keep both sets of component paths protected.
///     </para>
/// </summary>
public static class SandboxProtectedPathsStore
{
    private const int LockRetryCount   = 100;
    private const int LockRetryDelayMs = 50;
    private const int ReadRetryCount   = 5;
    private const int ReadRetryDelayMs = 25;

    /// <summary>protected_paths.json 的固定位置。The fixed location of protected_paths.json.</summary>
    public static string ResolvePath(WindowsSandboxComponents components) =>
        Path.Combine(components.CredentialsDirectory, "protected_paths.json");

    /// <summary>根据 sandboxHome 推导 protected_paths.json 路径。Resolves protected_paths.json from a sandbox home.</summary>
    public static string ResolvePathForHome(string sandboxHome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxHome);
        return Path.Combine(sandboxHome, ".sandbox-secrets", "protected_paths.json");
    }

    /// <summary>
    ///     <para>
    ///         严格只读地读取同一 sandbox home 下已登记的 C1 保护路径清单（供写根组合、grant 授权、
    ///         provisioning、refresh 复核与 ACL 准备同步保护共享 home 的另一端组件）；无副作用，
    ///         不创建锁文件或任何目录。写入侧采用临时文件原子替换，此处直接打开清单并对瞬时共享冲突
    ///         做有界短重试：仅当明确捕获文件或目录不存在时返回空数组，任何权限不足、I/O 错误或 JSON
    ///         损坏一律 fail closed 抛出，绝不通过 <see cref="File.Exists" /> 把访问失败误判为文件不存在。
    ///     </para>
    ///     <para>
    ///         Strictly reads the C1 protected-path manifest already registered under a sandbox home so
    ///         composition, grant authorization, provisioning, refresh revalidation, and ACL preparation
    ///         all protect peer installations sharing the home; strictly read-only with no side effects
    ///         (creates no lock file or directory). Because writers atomically replace via a temporary
    ///         file, this opens the manifest directly with bounded short retries on transient sharing
    ///         conflicts, returning empty only on explicit file-or-directory-not-found errors and failing
    ///         closed on any permission failure, I/O error, or malformed JSON instead of letting
    ///         <see cref="File.Exists" /> mask access errors as absence.
    ///     </para>
    /// </summary>
    public static IReadOnlyList<string> ReadRegisteredPathsForHome(string sandboxHome)
    {
        var path = ResolvePathForHome(sandboxHome);
        try
        {
            // 严格只读且无副作用：不创建 .lock 文件，也不预先调用 File.Exists（权限不足或 I/O 错误时
            // File.Exists 会返回 false）；直接打开清单读取，仅把 FileNotFoundException /
            // DirectoryNotFoundException 视作空清单，对原子替换窗口的瞬时共享冲突做有界短重试。
            for (var attempt = 1;; attempt++)
            {
                try
                {
                    return TryReadManifestStrict(path, out _, out var paths) ? paths : [];
                }
                catch (Exception ex) when (attempt < ReadRetryCount &&
                                           ex is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(ReadRetryDelayMs);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidOperationException(
                $"Could not read or parse the existing C1 protected-path manifest at '{path}'; refusing to " +
                $"discard or bypass previously registered protected paths (fail closed): {ex.Message}",
                ex);
        }
    }

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
            components.UsersFilePath,
            SandboxProvisionLeaseStore.ResolveDefaultLeaseDirectory()
        };
        paths.AddRange(components.ProtectedHomeSubpaths);
        if (permissionSystemPaths is not null) paths.AddRange(permissionSystemPaths);
        return SandboxIsolationPolicy.Deduplicate(paths.Select(Path.GetFullPath));
    }

    /// <summary>
    ///     <para>
    ///         跨进程加锁合并并原子写入 C1 清单（source-gen 序列化的规范绝对路径数组）：保留同一
    ///         home 下已登记的合法绝对保护路径并并入本次清单，合并后内容未变时不覆写。写入失败抛出
    ///         带上下文的异常——清单缺席会让授权豁免失去 C1 第二道防线，组合必须 fail closed。
    ///     </para>
    ///     <para>
    ///         Merges and atomically writes the C1 manifest under a cross-process lock (a source-generated
    ///         canonical absolute-path array): existing valid absolute entries recorded under the same home
    ///         are preserved alongside the new manifest, and an unchanged file is left untouched. Write
    ///         failures throw with context — a missing manifest would strip authorization exemptions of
    ///         their C1 second line, so composition must fail closed.
    ///     </para>
    /// </summary>
    public static void WriteIfChanged(WindowsSandboxComponents components, IReadOnlyList<string> protectedPaths)
    {
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(protectedPaths);

        var path     = ResolvePath(components);
        var lockPath = path + ".lock";
        try
        {
            Directory.CreateDirectory(components.CredentialsDirectory);
            using var lockStream = AcquireLockFile(lockPath);

            // 直接尝试打开读取，不先用 File.Exists：仅明确 FileNotFoundException / DirectoryNotFoundException
            // 按首次初始化处理；任何权限不足、I/O 错误或 JSON 损坏都保留原文件并 fail closed。
            var hasExisting = TryReadManifestStrict(path, out var existingContent, out var existingPaths);
            if (!hasExisting)
            {
                existingContent = null;
                existingPaths   = [];
            }

            var merged = MergePaths(existingPaths, protectedPaths);
            var json   = JsonSerializer.Serialize(merged, WindowsSandboxJsonContext.Default.StringArray);
            if (existingContent == json) return;

            var tempPath = Path.Combine(components.CredentialsDirectory,
                                        $"protected_paths.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, path, overwrite: true);
            }
            catch
            {
                TryDeleteTempFile(tempPath);
                throw;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new
                InvalidOperationException($"Could not update the C1 protected-path manifest at '{path}'; the helper's " +
                                          "second defense line against authorization exemptions would be missing or " +
                                          "previously registered protected paths could be lost, refusing the " +
                                          $"composition (fail closed): {ex.Message}",
                                          ex);
        }
    }

    private static bool TryReadManifestStrict(string path, out string? content, out IReadOnlyList<string> paths)
    {
        try
        {
            // 允许并发读与写入侧原子替换（File.Move overwrite），同时对 FileShare.None 独占锁继续报错。
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var reader = new StreamReader(stream);
            content = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            content = null;
            paths   = [];
            return false;
        }

        paths = ParseExistingPathsStrict(content, path);
        return true;
    }

    private static FileStream AcquireLockFile(string lockPath)
    {
        IOException? lastError = null;
        for (var attempt = 0; attempt < LockRetryCount; attempt++)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex)
            {
                lastError = ex;
                Thread.Sleep(LockRetryDelayMs);
            }
        }

        throw new IOException($"Timed out acquiring lock file '{lockPath}' for C1 protected paths.", lastError);
    }

    private static IReadOnlyList<string> ParseExistingPathsStrict(string json, string path)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new JsonException($"The existing protected-path manifest '{path}' is empty.");

        var parsed = JsonSerializer.Deserialize(json, WindowsSandboxJsonContext.Default.StringArray)
                  ?? throw new JsonException($"The existing protected-path manifest '{path}' did not deserialize to a string array.");

        var result = new List<string>(parsed.Length);
        foreach (var entry in parsed)
        {
            if (string.IsNullOrWhiteSpace(entry) || !Path.IsPathFullyQualified(entry.Trim()))
                throw new JsonException(
                    $"The existing protected-path manifest '{path}' contains an invalid non-absolute entry: '{entry}'.");

            result.Add(Path.GetFullPath(entry.Trim()));
        }

        return result;
    }

    private static string[] MergePaths(IReadOnlyList<string> existingPaths, IReadOnlyList<string> incomingPaths)
    {
        var combined = new List<string>(existingPaths.Count + incomingPaths.Count);
        combined.AddRange(existingPaths);
        combined.AddRange(incomingPaths.Select(Path.GetFullPath));
        return [.. SandboxIsolationPolicy.Deduplicate(combined)];
    }

    private static void TryDeleteTempFile(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时文件清理失败不掩盖原始原子写入异常。
        }
    }
}
