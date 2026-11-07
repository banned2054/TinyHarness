using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

namespace TinyHarness.Core.Services.Runtime;

/// <summary>
///     <para>
///         统一资源访问策略的路径分类：文件工具与 Shell 共用同一套资源语义，
///         分类结果供授权侧判断所需能力等级，本身不授予任何权限。
///     </para>
///     <para>
///         Path classification of the unified resource access policy: file tools and
///         the shell share one resource vocabulary; the classification informs the
///         authorization side about the required capability level and grants nothing
///         by itself.
///     </para>
/// </summary>
public enum ResourceClass
{
    /// <summary>
    ///     <para>工作区基线资源：工作区内且未命中元数据/保护路径的目标。</para>
    ///     <para>Workspace baseline: inside the workspace and not metadata or protected.</para>
    /// </summary>
    BaselineWorkspace,

    /// <summary>
    ///     <para>可信设置的附加只读根（工作区外），读取需要 Level 2 附加读能力。</para>
    ///     <para>Trusted additional read root (outside the workspace); reads need a Level 2 read capability.</para>
    /// </summary>
    AdditionalReadRoot,

    /// <summary>
    ///     <para>可信设置的附加写根（工作区外），写入需要 Level 2 附加写能力。</para>
    ///     <para>Trusted additional write root (outside the workspace); writes need a Level 2 write capability.</para>
    /// </summary>
    AdditionalWriteRoot,

    /// <summary>
    ///     <para>受保护元数据（如 .git/.agents/.codex/.aws），写入需要 Level 2 元数据写 grant。</para>
    ///     <para>
    ///         Protected metadata (such as .git/.agents/.codex/.aws); writes need a
    ///         Level 2 metadata-write grant.
    ///     </para>
    /// </summary>
    ProtectedMetadata,

    /// <summary>
    ///     <para>权限系统保护路径（信任/配置/会话存储/沙箱组件/凭据）：一律硬拒，不可授权。</para>
    ///     <para>
    ///         Permission-system protected paths (trust/config/session store/sandbox
    ///         components/credentials): always hard-denied, never grantable.
    ///     </para>
    /// </summary>
    PermissionSystemProtected,

    /// <summary>
    ///     <para>工作区外且不属于任何附加根的目标；默认拒绝，需显式授权路径。</para>
    ///     <para>Outside the workspace and in no additional root; denied by default, needs an explicit path grant.</para>
    /// </summary>
    OutsideWorkspace
}

/// <summary>
///     <para>一次目标的分类结果：资源类别与是否命中已知敏感名单。</para>
///     <para>One target's classification: the resource class and whether the known sensitive list matched.</para>
/// </summary>
/// <remarks>
///     <para>
///         已知敏感名单只如实文档化、不声称完整；命中与否不改变 <see cref="Class" />，
///         供授权侧与审批展示区分"工作区外默认拒绝"与"已知凭据位置"。
///     </para>
///     <para>
///         The known sensitive list is documentation, not a complete inventory; a match
///         never changes <see cref="Class" />. It lets the authorization side and approval
///         UIs distinguish "outside workspace, denied by default" from "known credential location".
///     </para>
/// </remarks>
public sealed record ResourceClassification(ResourceClass Class, bool KnownSensitive)
{
    /// <summary>
    ///     <para>
    ///         类别严格度排序：多目标聚合时取最严格类别，使注记不会弱于任一目标。
    ///     </para>
    ///     <para>
    ///         Class strictness order: aggregating multiple targets keeps the strictest
    ///         class so the annotation is never weaker than any single target.
    ///     </para>
    /// </summary>
    internal static int Strictness(ResourceClass classification) => classification switch
    {
        ResourceClass.PermissionSystemProtected => 5,
        ResourceClass.ProtectedMetadata         => 4,
        ResourceClass.OutsideWorkspace          => 3,
        ResourceClass.AdditionalWriteRoot       => 2,
        ResourceClass.AdditionalReadRoot        => 1,
        _                                       => 0
    };

    /// <summary>
    ///     <para>返回两者中更严格的类别，用于多目标计划的聚合注记。</para>
    ///     <para>Returns the stricter of the two classes, used for multi-target plan annotations.</para>
    /// </summary>
    internal static ResourceClass Strictest(ResourceClass left, ResourceClass right) =>
        Strictness(left) >= Strictness(right) ? left : right;
}

/// <summary>
///     <para>
///         文件工具与 Shell 共用的统一资源访问策略。输入为工作区根、可信附加读写根、
///         受保护元数据集与权限系统保护路径；分类复用既有的最终路径解析
///         （<see cref="SandboxFinalPathResolver" />）抗 8.3 短名与 junction/symlink 改向。
///         分类本身不授权：授权判定属于权限引擎。
///     </para>
///     <para>
///         The unified resource access policy shared by file tools and the shell. Inputs
///         are the workspace root, trusted additional read/write roots, the protected
///         metadata set, and permission-system protected paths; classification reuses the
///         existing final-path resolution (<see cref="SandboxFinalPathResolver" />) to
///         resist 8.3 short names and junction/symlink redirection. Classification never
///         authorizes: granting belongs to the permission engine.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         敏感名单只作用于权限系统资源（硬拒）与工作区外默认拒绝；工作区内部文件
///         按基线处理，不自动识别工作区内的秘密。权限系统保护根在构造期解析最终落点，
///         目标侧的词法与最终两种形态都会与两组形态比对。
///     </para>
///     <para>
///         The sensitive list applies only to permission-system resources (hard-denied)
///         and out-of-workspace default denial; in-workspace files stay baseline and
///         secrets inside the workspace are not auto-identified. Protected roots resolve
///         their final landing at construction; the target's lexical and final forms are
///         matched against both form sets.
///     </para>
/// </remarks>
public sealed class ResourceAccessPolicy : IResourceAccessPolicy
{
    private readonly IReadOnlyList<string> _additionalReadRoots;

    private readonly IReadOnlyList<string>                  _additionalWriteRoots;
    private readonly IReadOnlyList<string>                  _metadataRoots;
    private readonly IReadOnlyList<string>                  _permissionSystemRoots;
    private readonly IReadOnlyList<string>                  _permissionSystemFinalRoots;
    private readonly string                                 _workspaceRoot;

    /// <summary>
    ///     <para>
    ///         构造策略；根路径全部规范化为绝对路径。受保护元数据缺省为工作区下的
    ///         .git/.agents/.codex/.aws，也可显式传入。
    ///     </para>
    ///     <para>
    ///     Builds the policy; all roots are normalized to absolute paths. Protected
    ///     metadata defaults to .git/.agents/.codex/.aws under the workspace root and
    ///     may be overridden explicitly.
    ///     </para>
    /// </summary>
    public ResourceAccessPolicy(string workspaceRoot,
                                IEnumerable<string>? additionalReadRoots      = null,
                                IEnumerable<string>? additionalWriteRoots     = null,
                                IEnumerable<string>? protectedMetadataRoots   = null,
                                IEnumerable<string>? permissionSystemRoots    = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _workspaceRoot = Path.GetFullPath(workspaceRoot);

        _additionalReadRoots  = NormalizeRoots(additionalReadRoots);
        _additionalWriteRoots = NormalizeRoots(additionalWriteRoots);
        _metadataRoots = protectedMetadataRoots is null
            ? SandboxIsolationPolicy.ProtectedMetadataSubpaths
                                  .Select(name => Path.Combine(_workspaceRoot, name))
                                  .ToList()
            : NormalizeRoots(protectedMetadataRoots);
        _permissionSystemRoots      = NormalizeRoots(permissionSystemRoots);
        _permissionSystemFinalRoots = _permissionSystemRoots
                                     .Select(root => SandboxFinalPathResolver.TryGetFinalPath(root))
                                     .OfType<string>()
                                     .ToList();
    }

    /// <summary>
    ///     <para>
    ///         权限引擎注入点（<see cref="IResourceAccessPolicy" />）：按能力与目标路径返回六值分类。
    ///         capability 参与读写判定：只读能力对受保护元数据按基线读处理（元数据 grant 只约束写），
    ///         其余类别直通。接口契约要求不抛出：无效输入或解析失败返回 null，由引擎按需审批 fail-closed。
    ///     </para>
    ///     <para>
    ///         The permission-engine injection point (<see cref="IResourceAccessPolicy" />): returns the
    ///         six-value classification per capability and target path. The capability participates in the
    ///         read/write decision: read-only capabilities treat protected metadata as a baseline read (the
    ///         metadata grant constrains writes only), and every other class passes through unchanged. The
    ///         interface contract forbids throwing: invalid input or resolution failures return null, which
    ///         the engine fails closed to approval.
    ///     </para>
    /// </summary>
    ResourceClassification? IResourceAccessPolicy.Classify(string capability, string targetPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(capability) || string.IsNullOrWhiteSpace(targetPath)) return null;

            var classification = Classify(targetPath);
            if (classification.Class == ResourceClass.ProtectedMetadata && ToolCapabilities.IsReadOnly(capability))
                return classification with { Class = ResourceClass.BaselineWorkspace };

            return classification;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
        {
            // 接口契约：分类不得抛出；无法分类交由引擎按需审批 fail-closed 处理。
            return null;
        }
    }

    /// <summary>
    ///     <para>
    ///         对一个绝对目标分类。词法形态与最终落点（展开链接与 8.3 短名）都参与比对；
    ///         优先级为权限系统保护、受保护元数据、工作区基线、附加写根、附加读根、工作区外。
    ///         解析失败回落词法路径继续分类，绝不跳过检查。
    ///     </para>
    ///     <para>
    ///         Classifies one absolute target. Both the lexical form and the final landing
    ///         (links and 8.3 short names expanded) participate; priority is
    ///         permission-system protected, protected metadata, workspace baseline,
    ///         additional write root, additional read root, outside workspace. Resolution
    ///         failures fall back to the lexical path and never skip the check.
    ///     </para>
    /// </summary>
    public ResourceClassification Classify(string target)
    {
        ArgumentNullException.ThrowIfNull(target);

        string full;
        try
        {
            full = Path.GetFullPath(target);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException($"Resource target '{target}' is not a valid path.", ex);
        }

        var final = SandboxFinalPathResolver.TryGetFinalPath(full) ?? full;

        // 1) 权限系统保护：目标任一形态落入保护根（词法或最终）即硬拒类。
        if (IsInsideAny(_permissionSystemRoots, full) || IsInsideAny(_permissionSystemFinalRoots, full) ||
            IsInsideAny(_permissionSystemRoots, final) || IsInsideAny(_permissionSystemFinalRoots, final))
            return WithSensitive(ResourceClass.PermissionSystemProtected, full, final);

        // 2) 工作区成员（词法与最终都在界内）：元数据命中取元数据类，否则基线。
        //    词法在界内但最终逃逸时落到底部的附加根/外部判定，由最终形态决定类别。
        if (Workspace.IsInside(_workspaceRoot, full) && Workspace.IsInside(_workspaceRoot, final))
            return WithSensitive(IsInsideAny(_metadataRoots, full) || IsInsideAny(_metadataRoots, final)
                                     ? ResourceClass.ProtectedMetadata
                                     : ResourceClass.BaselineWorkspace,
                                 full, final);

        // 3) 附加根：写根优先于读根（请求能力更强的类别，避免弱化）。
        if (IsInsideAny(_additionalWriteRoots, full) || IsInsideAny(_additionalWriteRoots, final))
            return WithSensitive(ResourceClass.AdditionalWriteRoot, full, final);

        if (IsInsideAny(_additionalReadRoots, full) || IsInsideAny(_additionalReadRoots, final))
            return WithSensitive(ResourceClass.AdditionalReadRoot, full, final);

        return WithSensitive(ResourceClass.OutsideWorkspace, full, final);
    }

    /// <summary>
    ///     <para>
    ///         Prepare 期的分类入口：权限系统保护路径直接拒绝（抛 <see cref="InvalidDataException" />），
    ///         其余类别原样返回注记。分类结果不决定授权，授权由权限引擎计算。
    ///     </para>
    ///     <para>
    ///         The Prepare-side entry: permission-system protected paths are rejected
    ///         immediately (throwing <see cref="InvalidDataException" />); every other class
    ///         is returned as the annotation unchanged. The annotation never decides
    ///         authorization; the permission engine does.
    ///     </para>
    /// </summary>
    public ResourceClassification Evaluate(string target, string argumentName)
    {
        var classification = Classify(target);
        if (classification.Class == ResourceClass.PermissionSystemProtected)
            throw new InvalidDataException($"Argument '{argumentName}': '{target}' is protected by the permission " +
                                           "system and cannot be accessed by tools.");

        return classification;
    }

    /// <summary>
    ///     <para>
    ///         Execute 期的复核：目标的最终落点（新文件则最近存在祖先的最终落点）不得落入
    ///         权限系统保护路径；命中即抛出，防止 Prepare 与 Execute 之间新增的链接改向。
    ///     </para>
    ///     <para>
    ///         The Execute-side re-check: the target's final landing (or, for a not-yet-existing
    ///         file, the final landing of its nearest existing ancestor) must not fall inside a
    ///         permission-system protected path; a hit throws, guarding against links created
    ///         between Prepare and Execute.
    ///     </para>
    /// </summary>
    public void EnsureNotPermissionProtected(string target, string what)
    {
        var classification = Classify(target);
        if (classification.Class == ResourceClass.PermissionSystemProtected)
            throw new InvalidDataException($"{what} '{target}' resolves into a permission-system protected path; " +
                                           "access is refused.");

        if (File.Exists(target) || Directory.Exists(target)) return;

        // 新文件叶子尚不存在：解析最近存在的祖先，其最终落点同样不得进入保护路径。
        var ancestor = Path.GetDirectoryName(Path.GetFullPath(target));
        while (ancestor is not null && !Directory.Exists(ancestor))
            ancestor = Path.GetDirectoryName(ancestor);

        if (ancestor is null) return;

        var ancestorFinal = SandboxFinalPathResolver.TryGetFinalPath(ancestor) ?? ancestor;
        if (IsInsideAny(_permissionSystemRoots, ancestorFinal) ||
            IsInsideAny(_permissionSystemFinalRoots, ancestorFinal))
            throw new InvalidDataException($"{what} '{target}' would be created inside a permission-system protected " +
                                           $"path through '{ancestorFinal}'; access is refused.");
    }

    /// <summary>
    ///     <para>把分类结果与敏感标记组合成注记。</para>
    ///     <para>Combines a class with the sensitive-list verdict into the annotation.</para>
    /// </summary>
    private ResourceClassification WithSensitive(ResourceClass classification, string full, string final) =>
        new(classification, SensitivePathClassifier.IsKnownSensitivePath(full) ||
                            SensitivePathClassifier.IsKnownSensitivePath(final));

    private bool IsInsideAny(IEnumerable<string> roots, string candidate)
    {
        foreach (var root in roots)
            if (Workspace.IsInside(root, candidate))
                return true;

        return false;
    }

    /// <summary>
    ///     <para>规范化根列表：绝对化、去重（大小写不敏感）、保持顺序；空输入返回空列表。</para>
    ///     <para>
    ///         Normalizes a root list: absolutized, deduplicated case-insensitively,
    ///         order-preserving; empty input yields an empty list.
    ///     </para>
    /// </summary>
    private static IReadOnlyList<string> NormalizeRoots(IEnumerable<string>? roots)
    {
        if (roots is null) return [];

        var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<string>();
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;

            var full = Path.GetFullPath(root);
            if (seen.Add(full)) results.Add(full);
        }

        return results;
    }
}
