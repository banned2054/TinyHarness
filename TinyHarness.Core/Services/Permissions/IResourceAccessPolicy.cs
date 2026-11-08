using TinyHarness.Core.Services.Runtime;

namespace TinyHarness.Core.Services.Permissions;

/// <summary>
///     <para>
///         执行侧注入的资源分类接口：按能力与目标路径消费 Services/Runtime 的
///         <see cref="ResourceClassification" />（含 KnownSensitive 聚合注记）。分类驱动执行级别，
///         不直接产生授权；实现由执行侧组合期注入。
///     </para>
///     <para>
///         The resource-classification interface injected by the execution side: it consumes Services/Runtime's
///         <see cref="ResourceClassification" /> (with the KnownSensitive aggregate annotation) per
///         capability and target path. Classification drives the execution level and never grants authority;
///         implementations are injected at composition time.
///     </para>
/// </summary>
public interface IResourceAccessPolicy
{
    /// <summary>
    ///     <para>
    ///         返回一个目标路径在指定能力下的资源分类；null 表示未分类，引擎按需审批 fail-closed 处理。
    ///         实现必须纯函数化，不得读取可变会话状态，不得抛出异常。
    ///     </para>
    ///     <para>
    ///         Returns the resource class of one target path under a capability; null means unclassified and the
    ///         engine fails closed to approval. Implementations must be pure, read no mutable session state, and
    ///         never throw.
    ///     </para>
    /// </summary>
    ResourceClassification? Classify(string capability, string targetPath);

    /// <summary>
    ///     <para>
    ///         判断一个路径（通常是 shell 申请的写根）是否与权限系统保护资源重叠：路径落入任一保护根
    ///         或覆盖任一保护根都算命中，词法与最终形态都参与比对。实现必须纯函数化，不得抛出异常；
    ///         无保护根信息或输入无效时返回 false，由引擎的分类兜底处理。
    ///     </para>
    ///     <para>
    ///         Whether a path (usually a shell-requested write root) overlaps permission-system-protected
    ///         resources: the path falling inside any protected root or covering any protected root both count,
    ///         with lexical and final forms compared. Implementations must be pure and never throw; without
    ///         protected-root knowledge or for invalid input they return false and the engine's classification
    ///         fallback takes over.
    ///     </para>
    /// </summary>
    bool OverlapsPermissionSystemRoots(string path);
}
