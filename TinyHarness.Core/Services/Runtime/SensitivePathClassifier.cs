namespace TinyHarness.Core.Services.Runtime;

/// <summary>
///     <para>
///         集中维护的已知敏感路径名单。名单只如实文档化常见凭据/密钥位置，不声称完整；
///         语义由调用方决定：权限系统资源一律硬拒，工作区外默认拒绝，工作区内部文件
///         按基线资源处理（本分类器不自动识别工作区内的秘密）。
///     </para>
///     <para>
///         The centrally maintained list of known sensitive paths. The list honestly
///         documents common credential/key locations and never claims completeness;
///         semantics belong to the caller: permission-system resources are hard-denied,
///         out-of-workspace targets are denied by default, and in-workspace files stay
///         baseline resources (this classifier does not auto-identify secrets inside
///         the workspace).
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         名单与 <see cref="WorkerReadPolicy" /> 的 worker 排除集同源但独立维护：
///         worker 排除是读取范围收缩，此处是分类事实；两者不应互相引用以免语义耦合。
///     </para>
///     <para>
///         The list mirrors the worker exclusion set of <see cref="WorkerReadPolicy" />
///         but is maintained independently: the worker list narrows a read scope, this
///         one states a classification fact; neither should reference the other to
///         avoid coupling their semantics.
///     </para>
/// </remarks>
public static class SensitivePathClassifier
{
    /// <summary>
    ///     <para>已知凭据/配置目录段：任一路径段命中即视为敏感。</para>
    ///     <para>Known credential/config directory segments: any matching path segment marks the path sensitive.</para>
    /// </summary>
    public static readonly IReadOnlyCollection<string> KnownSensitiveDirectoryNames =
        [".ssh", ".aws", ".docker", ".gnupg", ".kube", ".sandbox-secrets"];

    /// <summary>
    ///     <para>已知凭据与本地配置文件名（精确段匹配）。</para>
    ///     <para>Known credential and local config file names (exact segment match).</para>
    /// </summary>
    public static readonly IReadOnlyCollection<string> KnownSensitiveFileNames =
        [".env", "credentials", "credentials.json", ".git-credentials", ".netrc", ".npmrc", ".htpasswd",
         "secrets.json", "secrets.yaml", "secrets.yml", "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519",
         "tinyharness.json"];

    /// <summary>
    ///     <para>私钥/证书类扩展名。</para>
    ///     <para>Private-key and certificate file extensions.</para>
    /// </summary>
    public static readonly IReadOnlyCollection<string> KnownSensitiveExtensions =
        [".pem", ".key", ".p12", ".pfx", ".jks"];

    /// <summary>
    ///     <para>
    ///         判断路径是否命中已知敏感名单：目录段、文件名或扩展名任一匹配即为 true。
    ///         匹配大小写不敏感；仅做词法段匹配，不解析链接、不做 I/O。
    ///     </para>
    ///     <para>
    ///         Returns whether the path hits the known sensitive list: a directory segment,
    ///         file name, or extension match is sufficient. Matching is case-insensitive and
    ///         purely lexical — no link resolution and no I/O.
    ///     </para>
    /// </summary>
    public static bool IsKnownSensitivePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var segments = trimmed.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                                     StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return false;

        for (var index = 0; index < segments.Length - 1; index++)
            if (KnownSensitiveDirectoryNames.Contains(segments[index], StringComparer.OrdinalIgnoreCase))
                return true;

        var fileName = segments[^1];
        if (KnownSensitiveFileNames.Contains(fileName, StringComparer.OrdinalIgnoreCase)) return true;

        var extension = Path.GetExtension(fileName);
        return extension.Length > 0 &&
               KnownSensitiveExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }
}
