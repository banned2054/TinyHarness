using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

namespace TinyHarness.Core.Services.Tools;

/// <summary>
///     <para>
///         工作区文件工具的稳定策略身份单一来源：四个文件工具的冻结计划都携带同一形态的身份串
///         （文件工具层 + 工作区规范根），授权登记表据此绑定与匹配 grant，授权事件审计据此填写
///         策略修订字段。工作区访问守卫在其后追加 workspace-access 修订段，二者共同构成失效矩阵。
///     </para>
///     <para>
///         The single source of the workspace file tools' stable policy identity: all four file tools' frozen
///         plans carry the same identity shape (the file-tool layer plus the canonical workspace root), so the
///         grant registry binds and matches grants against it and the grant-event audit fills its
///         policy-revision field from it. The workspace-access guard appends its workspace-access revision
///         section afterwards; together they form the invalidation matrix.
///     </para>
/// </summary>
internal static class WorkspaceFileToolPolicy
{
    /// <summary>
    ///     <para>
    ///         返回文件工具冻结计划的策略身份：层标识与规范化的工作区根；同一工作区稳定，
    ///         工作区变化即改变身份。
    ///     </para>
    ///     <para>
    ///         Returns the file tools' frozen-plan policy identity: the layer marker plus the canonical
    ///         workspace root; stable for one workspace and changed by a workspace change.
    ///     </para>
    /// </summary>
    public static string Identity(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        return "backend=workspace-file-tool\nworkspace=" +
               SandboxCapabilitySidStore.CanonicalRootKey(workspaceRoot);
    }
}
