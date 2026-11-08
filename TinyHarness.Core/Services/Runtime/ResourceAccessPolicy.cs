using TinyHarness.Core.Models.Permissions;
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
    ///     <para>
    ///         可信设置的附加只读根（工作区外）：读取属基线广泛读自动放行；写入仍需 Level 2
    ///         附加写能力逐次审批（只读根不接纳常设写入）。
    ///     </para>
    ///     <para>
    ///         Trusted additional read root (outside the workspace): reads pass at the broad baseline
    ///         automatically, while writes still need per-call approval with a Level 2 write capability
    ///         (a read-only root never accepts standing writes).
    ///     </para>
    /// </summary>
    AdditionalReadRoot,

    /// <summary>
    ///     <para>
    ///         常设信任 Confirmed 下的附加写根（工作区外）：写入按显式持久授权自动放行；
    ///         未确认时写入降为 <see cref="ResourceClass.UnconfirmedWriteRoot" /> 逐次审批。
    ///     </para>
    ///     <para>
    ///         An additional write root (outside the workspace) under Confirmed standing trust: writes pass
    ///         automatically as explicit persistent authorization; while unconfirmed, writes demote to
    ///         <see cref="ResourceClass.UnconfirmedWriteRoot" /> for per-call approval.
    ///     </para>
    /// </summary>
    AdditionalWriteRoot,

    /// <summary>
    ///     <para>
    ///         常设信任未确认（或策略未拿到确认状态）时的附加写根写入档：OS 基线不含写根，
    ///         写入逐次 Level 2 审批并经 grant 通道临时生效。
    ///     </para>
    ///     <para>
    ///         The additional-write-root write tier while standing trust is unconfirmed (or the policy lacks a
    ///         confirmed state): the OS baseline excludes write roots, so writes need per-call Level 2 approval
    ///         and take effect temporarily through the grant channel.
    ///     </para>
    /// </summary>
    UnconfirmedWriteRoot,

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
    ///     <para>
    ///         工作区外且不属于任何附加根或派生运行时写根的普通目标；读取属基线广泛读，
    ///         写入需要 Level 2 附加写能力。
    ///     </para>
    ///     <para>
    ///         An ordinary target outside the workspace and in no additional or derived runtime
    ///         root; reads are part of the broad baseline read, writes need a Level 2 write capability.
    ///     </para>
    /// </summary>
    OutsideWorkspace,

    /// <summary>
    ///     <para>
    ///         工作区外且命中已知敏感名单的位置（读保护敏感资源，名单式、不声称完整）：
    ///         读与写都需要 Level 2 逐次审批，即使读取已在广泛读基线内。
    ///     </para>
    ///     <para>
    ///         An out-of-workspace location matching the known sensitive list (read-protected
    ///         sensitive resource; list-based, never claimed complete): both reads and writes need
    ///         Level 2 approval even though reads are inside the broad baseline read.
    ///     </para>
    /// </summary>
    SensitiveLocation,

    /// <summary>
    ///     <para>
    ///         派生运行时写根（NuGet 缓存根、工具根与沙箱临时写根）：沙箱 OS 侧基线可写的
    ///         harness 基础设施，读与写均按 Level 1 自动放行以对齐两层边界。
    ///     </para>
    ///     <para>
    ///         Derived runtime write roots (the NuGet cache root, the tools root, and the sandbox
    ///         temp write root): harness infrastructure the sandbox OS already treats as writable
    ///         baseline; reads and writes are both allowed automatically at Level 1 so the two
    ///         boundaries stay aligned.
    ///     </para>
    /// </summary>
    SandboxRuntimeRoot
}

/// <summary>
///     <para>一次目标的分类结果：资源类别与是否命中已知敏感名单。</para>
///     <para>One target's classification: the resource class and whether the known sensitive list matched.</para>
/// </summary>
/// <remarks>
///     <para>
///         已知敏感名单只如实文档化、不声称完整；命中把工作区外的普通越界档升为
///         <see cref="ResourceClass.SensitiveLocation" />，其余类别不受命中影响，
///         供授权侧与审批展示区分"越界"与"已知凭据位置"。
///     </para>
///     <para>
///         The known sensitive list is documentation, not a complete inventory; a match
///         promotes the ordinary out-of-workspace class to
///         <see cref="ResourceClass.SensitiveLocation" />, while every other class is unaffected.
///         It lets the authorization side and approval UIs distinguish "outside workspace"
///         from "known credential location".
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
        ResourceClass.SensitiveLocation         => 4,
        ResourceClass.OutsideWorkspace          => 3,
        ResourceClass.UnconfirmedWriteRoot      => 3,
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
///         受保护元数据集、权限系统保护路径与派生运行时写根；分类复用既有的最终路径解析
///         （<see cref="SandboxFinalPathResolver" />）抗 8.3 短名与 junction/symlink 改向。
///         分类本身不授权：授权判定属于权限引擎。
///     </para>
///     <para>
///         The unified resource access policy shared by file tools and the shell. Inputs
///         are the workspace root, trusted additional read/write roots, the protected
///         metadata set, permission-system protected paths, and derived runtime write
///         roots; classification reuses the existing final-path resolution
///         (<see cref="SandboxFinalPathResolver" />) to resist 8.3 short names and
///         junction/symlink redirection. Classification never authorizes: granting
///         belongs to the permission engine.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         敏感名单只作用于权限系统资源（硬拒）与工作区外位置——后者命中即归
///         <see cref="ResourceClass.SensitiveLocation" /> 读保护档；工作区内部文件
///         按基线处理，不自动识别工作区内的秘密。权限系统保护根与运行时写根在构造期
///         解析最终落点，目标侧的词法与最终两种形态都会与两组形态比对。
///         常设信任状态（构造期注入）决定附加写根写入是显式持久授权还是逐次审批档。
///     </para>
///     <para>
///         The sensitive list applies only to permission-system resources (hard-denied)
///         and out-of-workspace locations — a hit there lands in the
///         <see cref="ResourceClass.SensitiveLocation" /> read-protection class; in-workspace
///         files stay baseline and secrets inside the workspace are not auto-identified.
///         Protected and runtime roots resolve their final landing at construction; the
///         target's lexical and final forms are matched against both form sets. The standing-trust
///         state (injected at construction) decides whether additional write-root writes run as
///         explicit persistent authorization or as the per-call approval tier.
///     </para>
/// </remarks>
public sealed class ResourceAccessPolicy : IResourceAccessPolicy
{
    private readonly IReadOnlyList<string> _additionalReadRoots;

    private readonly IReadOnlyList<string> _additionalWriteRoots;
    private readonly IReadOnlyList<string> _metadataRoots;
    private readonly IReadOnlyList<string> _permissionSystemFinalRoots;
    private readonly IReadOnlyList<string> _permissionSystemRoots;
    private readonly IReadOnlyList<string> _sandboxRuntimeFinalRoots;
    private readonly IReadOnlyList<string> _sandboxRuntimeRoots;
    private readonly StandingTrustState    _standingTrust;
    private readonly string                _workspaceRoot;

    /// <summary>
    ///     <para>
    ///         构造策略；根路径全部规范化为绝对路径。受保护元数据缺省为工作区下的
    ///         .git/.agents/.codex/.aws，也可显式传入。
    ///     </para>
    ///     <para>
    ///         Builds the policy; all roots are normalized to absolute paths. Protected
    ///         metadata defaults to .git/.agents/.codex/.aws under the workspace root and
    ///         may be overridden explicitly.
    ///     </para>
    /// </summary>
    /// <param name="sandboxRuntimeRoots">
    ///     <para>
    ///         派生运行时写根（NuGet 缓存根、工具根与沙箱临时写根）；其中目标按 Level 1
    ///         基线放行，与沙箱 OS 侧可写范围对齐。缺省为空。
    ///     </para>
    ///     <para>
    ///         Derived runtime write roots (the NuGet cache root, the tools root, and the sandbox
    ///         temp write root); targets inside them pass at the Level 1 baseline, aligning with
    ///         the sandbox's OS-writable scope. Empty by default.
    ///     </para>
    /// </param>
    /// <param name="standingTrust">
    ///     <para>
    ///         常设信任三态：<see cref="StandingTrustState.Confirmed" /> 时附加写根写入按显式持久
    ///         授权放行；其余状态（缺省 <see cref="StandingTrustState.None" />，fail-closed）把附加写根
    ///         写入降为 <see cref="ResourceClass.UnconfirmedWriteRoot" /> 逐次审批。
    ///     </para>
    ///     <para>
    ///         The three-state standing trust: under <see cref="StandingTrustState.Confirmed" /> additional
    ///         write-root writes pass as explicit persistent authorization; every other state (the default
    ///         <see cref="StandingTrustState.None" />, fail-closed) demotes them to
    ///         <see cref="ResourceClass.UnconfirmedWriteRoot" /> for per-call approval.
    ///     </para>
    /// </param>
    public ResourceAccessPolicy(string               workspaceRoot,
                                IEnumerable<string>? additionalReadRoots    = null,
                                IEnumerable<string>? additionalWriteRoots   = null,
                                IEnumerable<string>? protectedMetadataRoots = null,
                                IEnumerable<string>? permissionSystemRoots  = null,
                                IEnumerable<string>? sandboxRuntimeRoots    = null,
                                StandingTrustState   standingTrust          = StandingTrustState.None)
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
        _permissionSystemRoots = NormalizeRoots(permissionSystemRoots);
        _permissionSystemFinalRoots = _permissionSystemRoots
                                     .Select(root => SandboxFinalPathResolver.TryGetFinalPath(root))
                                     .OfType<string>()
                                     .ToList();
        _sandboxRuntimeRoots = NormalizeRoots(sandboxRuntimeRoots);
        _sandboxRuntimeFinalRoots = _sandboxRuntimeRoots
                                   .Select(root => SandboxFinalPathResolver.TryGetFinalPath(root))
                                   .OfType<string>()
                                   .ToList();
        _standingTrust = standingTrust;
    }

    /// <summary>
    ///     <para>
    ///         权限引擎注入点（<see cref="IResourceAccessPolicy" />）：按能力与目标路径返回资源分类。
    ///         capability 参与读写判定：只读能力对受保护元数据按基线读处理（元数据 grant 只约束写），
    ///         其余类别直通。接口契约要求不抛出：无效输入或解析失败返回 null，由引擎按需审批 fail-closed。
    ///     </para>
    ///     <para>
    ///         The permission-engine injection point (<see cref="IResourceAccessPolicy" />): returns the
    ///         resource classification per capability and target path. The capability participates in the
    ///         read/write decision: read-only capabilities treat protected metadata as a baseline read (the
    ///         metadata grant constrains writes only), and unconfirmed standing trust demotes additional
    ///         write-root writes to <see cref="ResourceClass.UnconfirmedWriteRoot" />. The interface contract
    ///         forbids throwing: invalid input or resolution failures return null, which the engine fails
    ///         closed to approval.
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

            // 常设信任未确认时附加写根的写入降为逐次审批档；读方向维持基线广泛读不降档。
            if (classification.Class == ResourceClass.AdditionalWriteRoot &&
                !ToolCapabilities.IsReadOnly(capability)                  &&
                _standingTrust is not StandingTrustState.Confirmed)
                return classification with { Class = ResourceClass.UnconfirmedWriteRoot };

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
    ///         权限引擎注入点（<see cref="IResourceAccessPolicy" />）：判断一个路径（通常是 shell
    ///         申请的写根）与权限系统保护根的双向重叠。词法根集与构造期最终形态根集都参与比对，
    ///         路径侧同样解析词法与最终两种形态；任一方向的包含或相等即命中。接口契约要求不抛出：
    ///         无保护根信息或输入无效返回 false，由引擎的分类兜底处理。
    ///     </para>
    ///     <para>
    ///         The permission-engine injection point (<see cref="IResourceAccessPolicy" />): decides the
    ///         bidirectional overlap of a path (usually a shell-requested write root) with the
    ///         permission-system protected roots. Both the lexical root set and the final-form root set
    ///         resolved at construction participate, the path side resolves its lexical and final forms too,
    ///         and containment or equality in either direction hits. The interface contract forbids throwing:
    ///         without protected roots or for invalid input it returns false and the engine's classification
    ///         fallback takes over.
    ///     </para>
    /// </summary>
    bool IResourceAccessPolicy.OverlapsPermissionSystemRoots(string path)
    {
        // 接口契约：判定不得抛出；无效输入按不重叠处理，分类兜底仍会拦截保护目标本身。
        if (string.IsNullOrWhiteSpace(path)) return false;

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var final = SandboxFinalPathResolver.TryGetFinalPath(full) ?? full;
        return OverlapsAnyRoot(_permissionSystemRoots, full, final) ||
               OverlapsAnyRoot(_permissionSystemFinalRoots, full, final);
    }

    /// <summary>
    ///     <para>
    ///         对一个绝对目标分类。词法形态与最终落点（展开链接与 8.3 短名）都参与比对；
    ///         优先级为权限系统保护、受保护元数据、工作区基线、派生运行时写根、附加写根、
    ///         附加读根、工作区外（敏感命中归读保护档）。解析失败回落词法路径继续分类，
    ///         绝不跳过检查。
    ///     </para>
    ///     <para>
    ///         Classifies one absolute target. Both the lexical form and the final landing
    ///         (links and 8.3 short names expanded) participate; priority is
    ///         permission-system protected, protected metadata, workspace baseline, derived
    ///         runtime write root, additional write root, additional read root, and outside
    ///         workspace (sensitive hits land in the read-protection class). Resolution
    ///         failures fall back to the lexical path and never skip the check.
    ///     </para>
    ///     <para>
    ///         本重载不带能力方向，不执行常设信任降档；附加写根写入的
    ///         <see cref="ResourceClass.UnconfirmedWriteRoot" /> 降档只发生在按能力分类的
    ///         引擎注入点。
    ///     </para>
    ///     <para>
    ///         This overload takes no capability direction and never applies the standing-trust demotion;
    ///         the <see cref="ResourceClass.UnconfirmedWriteRoot" /> demotion of additional write-root writes
    ///         happens only at the capability-aware engine injection point.
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
        if (IsInsideAny(_permissionSystemRoots, full)  || IsInsideAny(_permissionSystemFinalRoots, full) ||
            IsInsideAny(_permissionSystemRoots, final) || IsInsideAny(_permissionSystemFinalRoots, final))
            return WithSensitive(ResourceClass.PermissionSystemProtected, full, final);

        // 2) 工作区成员（词法与最终都在界内）：元数据命中取元数据类，否则基线。
        //    词法在界内但最终逃逸时落到下方的运行时根/附加根/外部判定，由最终形态决定类别。
        if (Workspace.IsInside(_workspaceRoot, full) && Workspace.IsInside(_workspaceRoot, final))
            return WithSensitive(IsInsideAny(_metadataRoots, full) || IsInsideAny(_metadataRoots, final)
                                     ? ResourceClass.ProtectedMetadata
                                     : ResourceClass.BaselineWorkspace,
                                 full, final);

        // 3) 派生运行时写根：沙箱 OS 侧基线可写的基础设施，读写均按基线放行以对齐两层边界。
        if (IsInsideAny(_sandboxRuntimeRoots, full)  || IsInsideAny(_sandboxRuntimeFinalRoots, full) ||
            IsInsideAny(_sandboxRuntimeRoots, final) || IsInsideAny(_sandboxRuntimeFinalRoots, final))
            return WithSensitive(ResourceClass.SandboxRuntimeRoot, full, final);

        // 4) 附加根：写根优先于读根（请求能力更强的类别，避免弱化）。
        if (IsInsideAny(_additionalWriteRoots, full) || IsInsideAny(_additionalWriteRoots, final))
            return WithSensitive(ResourceClass.AdditionalWriteRoot, full, final);

        if (IsInsideAny(_additionalReadRoots, full) || IsInsideAny(_additionalReadRoots, final))
            return WithSensitive(ResourceClass.AdditionalReadRoot, full, final);

        // 5) 工作区外兜底：命中已知敏感名单的越界位置单独成档（读保护敏感资源），
        //    其余为普通越界（读取属基线广泛读，写入仍需审批）。
        var classification = WithSensitive(ResourceClass.OutsideWorkspace, full, final);
        return classification.KnownSensitive
            ? classification with { Class = ResourceClass.SensitiveLocation }
            : classification;
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
    ///     <para>
    ///         执行期递归入口的 C2 覆盖判定：候选路径命中已知敏感名单（工作区外、且不属于任何
    ///         附加根或派生运行时根），而本次调用准备的严格档不是敏感档时返回 true——审批边界
    ///         从未覆盖该敏感后代，普通父目录的基线广泛读不得隐式开放它。准备的严格档是敏感档
    ///         （搜索根/列举根本身走了 L2 审批）时返回 false，已批准范围不被收缩；候选等于显式
    ///         目标时引擎已按同一分类判定，同样不被收缩。分类失败按需审批方向处理（返回 true）。
    ///     </para>
    ///     <para>
    ///         The execute-time C2 coverage verdict for recursive entries: returns true when the
    ///         candidate hits the known sensitive list (outside the workspace and in no additional
    ///         or derived runtime root) while the invocation's strictest prepared class is not the
    ///         sensitive one — the approval boundary never covered that sensitive descendant, and a
    ///         normal parent's baseline broad read must not open it implicitly. When the prepared
    ///         strictest class is the sensitive one (the search or list root itself went through L2
    ///         approval) it returns false so an approved scope is never narrowed; a candidate equal
    ///         to an explicit target was already judged by the engine under the same classification
    ///         and is likewise never narrowed. Classification failures lean toward approval (true).
    ///     </para>
    /// </summary>
    public bool RequiresApprovalForRecursiveRead(string                  candidate,
                                                 ResourceClassification? preparationClassification)
    {
        // 准备档即敏感档：审批显式覆盖了敏感根的递归范围，后代随批准范围执行。
        if (preparationClassification is { Class: ResourceClass.SensitiveLocation }) return false;

        try
        {
            return Classify(candidate).Class == ResourceClass.SensitiveLocation;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
        {
            // 无法分类的候选按需要审批处理，绝不落入基线广泛读。
            return true;
        }
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
    ///     <para>候选路径的词法与最终形态对一组根任一方向重叠即命中（根侧形态由集合本身承载）。</para>
    ///     <para>
    ///         A hit when either form (lexical or final) of the candidate overlaps any root of the set in
    ///         either direction (the root's form is carried by the set itself).
    ///     </para>
    /// </summary>
    private static bool OverlapsAnyRoot(IReadOnlyList<string> roots, string full, string final)
    {
        foreach (var root in roots)
            if (Workspace.IsInside(root, full)  || Workspace.IsInside(full, root) ||
                Workspace.IsInside(root, final) || Workspace.IsInside(final, root))
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
