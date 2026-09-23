namespace TinyHarness.Core.Services.Runtime;

/// <summary>
/// 一次 worker run 专用的只读访问策略，由 WorkerRunner 在每次 run 时从经过校验的请求
/// 与可信配置构造，不跨 run 共享。两层约束：
/// <list type="number">
/// <item>focus 根：请求提供 focusPaths 时，每个条目解析为一个工作区相对允许根，访问只允许
/// 落在其中某个根内；比较基于 Workspace 解析后的绝对路径与分隔符边界
/// （<see cref="Workspace.IsInside"/>），绝不做字符串前缀比较。focusPaths 为空时允许整个
/// 工作区。focus 只能缩小范围，永远不能扩大工作区或越过已有链接逃逸检查。</item>
/// <item>敏感排除：平台无关、保守的文件名/目录段规则，始终生效（与 focus 无关）；
/// 直接路径与枚举结果同等适用。</item>
/// </list>
/// 词法检查（<see cref="EnsureLexicalAccessAllowed"/>）在工具 Prepare 阶段使用，不做 I/O；
/// 最终检查（<see cref="EnsureFinalAccessAllowed"/>）在 Execute 阶段使用，会解析链接链后
/// 对最终目标复核，作为已有 <see cref="Workspace.EnsureFinalTargetInside"/> 之外的附加边界，
/// 不替代也不绕过它。
///
/// The read-only access policy of a single worker run, built by the WorkerRunner from the validated
/// request and trusted configuration and never shared across runs. Two layers: focus roots (when the
/// request supplies focusPaths, each entry resolves to a workspace-relative allowed root and access
/// must fall inside one of them, compared on Workspace-resolved absolute paths and separator
/// boundaries — never string prefixes; an empty list means the whole workspace) and sensitive-path
/// exclusions (platform-agnostic, conservative file-name/directory-segment rules that always apply,
/// to explicit paths and enumeration results alike). Lexical checks run in Prepare without I/O; the
/// final check runs in Execute over the resolved link chain as an additional boundary on top of —
/// never instead of — the existing workspace link checks.
/// </summary>
public sealed class WorkerReadPolicy
{
    /// <summary>
    /// 常见凭据/密钥目录：任一路径段命中即整棵排除，枚举不下降、直接访问被拒。
    /// Common credential/key directories: any matching path segment excludes the whole subtree.
    /// </summary>
    private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ssh", ".aws", ".docker", ".gnupg", ".kube",
    };

    /// <summary>
    /// 常见凭据与配置文件名（精确段匹配，含目标仓库可能存在的 tinyharness.json）。
    /// Common credential/config file names (exact segment match), including a target repo's tinyharness.json.
    /// </summary>
    private static readonly HashSet<string> ExcludedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".env",
        "credentials", "credentials.json",
        ".git-credentials", ".netrc", ".npmrc", ".htpasswd",
        "secrets.json", "secrets.yaml", "secrets.yml",
        "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519",
        "tinyharness.json",
    };

    /// <summary>
    /// 私钥/证书类扩展名。
    /// Private-key/certificate file extensions.
    /// </summary>
    private static readonly string[] ExcludedExtensions = [".pem", ".key", ".p12", ".pfx", ".jks"];

    /// <summary>
    /// 单段链接链的解析层数上限；耗尽即按无法验证处理（fail closed），绝不把未验证
    /// 路径当作安全放行。
    /// Per-segment link resolution depth cap; exhausting it means "unverifiable" and denies access
    /// — an unverified path is never treated as safe.
    /// </summary>
    private const int MaxLinkLevels = 64;

    private readonly Workspace _workspace;

    /// <summary>解析后的绝对 focus 根；空列表表示整个工作区。 Absolute focus roots; empty means the whole workspace.</summary>
    private readonly IReadOnlyList<string> _focusRoots;

    private WorkerReadPolicy(Workspace workspace, IReadOnlyList<string> focusRoots)
    {
        _workspace  = workspace;
        _focusRoots = focusRoots;
    }

    /// <summary>
    /// 从已通过 <c>WorkerRequestValidator</c> 的请求构造本次 run 的策略；focusPaths 在此
    /// 解析为绝对允许根，非法或越界条目会失败（校验器已先行拦截常见形状问题）。
    ///
    /// Builds the per-run policy from an already validated request. Focus paths resolve to absolute
    /// allowed roots here; invalid or escaping entries fail (the validator rejects common shape
    /// problems earlier).
    /// </summary>
    public static WorkerReadPolicy Create(Workspace workspace, IReadOnlyList<string>? focusPaths)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        var roots = new List<string>();
        if (focusPaths is { Count: > 0 })
        {
            foreach (var focusPath in focusPaths)
            {
                roots.Add(workspace.ResolveInside(focusPath, "focusPaths"));
            }
        }

        return new WorkerReadPolicy(workspace, roots);
    }

    /// <summary>
    /// Prepare 阶段的纯词法检查：不触碰文件系统，路径必须是策略允许的形状。
    /// Pure lexical check for Prepare: no filesystem access; the path must be an allowed shape.
    /// </summary>
    public void EnsureLexicalAccessAllowed(string absolutePath)
    {
        if (!IsLexicallyAllowed(absolutePath))
        {
            throw ExplainDenial(absolutePath);
        }
    }

    /// <summary>
    /// Execute 阶段的最终检查：先做词法检查，再从工作区根起逐段解析链接（含祖先级链接，
    /// 例如 focus 内的 junction 指向工作区内其他目录），并对最终落点复核 focus 与敏感排除。
    /// Final Execute check: lexical first, then links are resolved segment by segment from the
    /// workspace root — including ancestor-level links, e.g. a junction inside the focus pointing at
    /// another workspace directory — and the final landing path is re-checked against focus and
    /// sensitive exclusions.
    /// </summary>
    public void EnsureFinalAccessAllowed(string absolutePath)
    {
        EnsureLexicalAccessAllowed(absolutePath);

        var finalPath = ResolveFinalPath(absolutePath);
        if (!Workspace.IsInside(_workspace.Root, finalPath) || !IsLexicallyAllowed(finalPath))
        {
            throw new
                InvalidDataException($"'{_workspace.ToDisplay(absolutePath)}' resolves to '{_workspace.ToDisplay(finalPath)}', " +
                                     "which is outside the worker read scope.");
        }
    }

    /// <summary>
    /// 枚举条目的完整检查：先词法判定；是重解析点（symlink/junction）时解析最终落点，
    /// 落点在 focus 外、敏感路径或工作区外则该条目整体排除（名称不出现在结果里）。
    /// 解析失败一律 fail closed。目录重解析点永远不会被遍历器下钻，这里只决定
    /// 它的名称是否允许出现。
    ///
    /// Full check for enumeration entries: lexical first; when the entry is a reparse point
    /// (symlink/junction) its resolved landing path is checked too — a landing outside the focus, a
    /// sensitive path or the workspace excludes the entry entirely, so its name never appears.
    /// Resolution failures always fail closed. Directory reparse points are never descended into by
    /// the walker; this check only decides whether their name may appear.
    /// </summary>
    public bool IsEntryAllowed(string absolutePath)
    {
        if (!IsLexicallyAllowed(absolutePath))
        {
            return false;
        }

        if (!IsReparsePoint(absolutePath))
        {
            return true;
        }

        try
        {
            var finalPath = ResolveFinalPath(absolutePath);
            return Workspace.IsInside(_workspace.Root, finalPath) && IsLexicallyAllowed(finalPath);
        }
        catch (InvalidDataException)
        {
            return false; // 无法验证即排除（fail closed）。
        }
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private bool IsLexicallyAllowed(string absolutePath)
    {
        return IsInsideFocus(absolutePath) && !IsSensitive(absolutePath);
    }

    private InvalidDataException ExplainDenial(string absolutePath)
    {
        // 错误只描述策略类别与请求路径本身，绝不读取或暗示文件内容。
        // The error names the policy category and the requested path only; content is never read or hinted at.
        return !IsInsideFocus(absolutePath)
            ? new
                InvalidDataException($"Argument is outside the worker's focused read scope: '{_workspace.ToDisplay(absolutePath)}'.")
            : new
                InvalidDataException($"Argument is excluded by the worker read policy: '{_workspace.ToDisplay(absolutePath)}'.");
    }

    private bool IsInsideFocus(string absolutePath)
    {
        if (_focusRoots.Count == 0)
        {
            return true;
        }

        return _focusRoots.Any(root => Workspace.IsInside(root, absolutePath));
    }

    private bool IsSensitive(string absolutePath)
    {
        var relative = Path.GetRelativePath(_workspace.Root, absolutePath);
        var segments = relative.Split('/', '\\');

        foreach (var segment in segments)
        {
            if (segment.Length > 0 && ExcludedDirectoryNames.Contains(segment))
            {
                return true;
            }
        }

        var name = segments[^1];
        if (name.Length == 0)
        {
            return false; // The workspace root itself.
        }

        if (ExcludedFileNames.Contains(name))
        {
            return true;
        }

        // .env 及其变体（.env.local、.env.production 等）。
        // .env and its variants (.env.local, .env.production, ...).
        if (name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return ExcludedExtensions.Any(extension => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 从工作区根起逐段解析真实路径：每段拼接后解析该级的链接链（含中间目录与入口本身的
    /// junction/symlink），链接跳转后把剩余原始段拼到新位置继续。只解析工作区根以下的段
    /// （根本身是可信配置）；解析失败按拒绝处理，链接环用固定上限截断。
    /// Workspace.EnsureFinalTargetInside 仍先行验证原路径的链接不逃逸工作区；这里在其之上
    /// 提供策略所需的最终落点，不替代也不重复其语义。
    ///
    /// Resolves the true path segment by segment starting from the workspace root: each segment is
    /// appended and its link chain (intermediate directories and the entry itself) resolved, and the
    /// remaining original segments continue from the new location. Only segments below the workspace
    /// root are resolved (the root itself is trusted configuration); verification failures deny
    /// access and link cycles are bounded. Workspace.EnsureFinalTargetInside still validates first
    /// that the original path's links never escape the workspace; this provides the final landing
    /// path the policy needs on top of that, without replacing its semantics.
    /// </summary>
    private string ResolveFinalPath(string absolutePath)
    {
        var relative = Path.GetRelativePath(_workspace.Root, absolutePath);
        if (relative is "." or "")
        {
            return absolutePath;
        }

        var current = _workspace.Root;
        foreach (var segment in relative.Split('/', '\\'))
        {
            if (segment.Length == 0)
            {
                continue;
            }

            current = Path.Combine(current, segment);
            var level = 0;
            while (true)
            {
                if (level >= MaxLinkLevels)
                {
                    // 上限耗尽说明仍有未解析的链接：按无法验证处理，绝不当作安全路径。
                    // A cap exhaustion means an unresolved link remains: treat as unverifiable,
                    // never as a safe path.
                    throw new
                        InvalidDataException($"Too many link levels resolving '{_workspace.ToDisplay(absolutePath)}'.");
                }

                level++;
                FileSystemInfo info = File.Exists(current)
                    ? new FileInfo(current)
                    : new DirectoryInfo(current);
                FileSystemInfo? target;
                try
                {
                    target = info.ResolveLinkTarget(returnFinalTarget : false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 无法确认链接目标时按拒绝处理，保持保守。
                    // Deny when the link target cannot be verified; stay conservative.
                    throw new
                        InvalidDataException($"Cannot verify the link target of '{_workspace.ToDisplay(absolutePath)}'.",
                                             ex);
                }

                if (target is null)
                {
                    break;
                }

                current = target.FullName;
            }
        }

        return Path.GetFullPath(current);
    }
}
