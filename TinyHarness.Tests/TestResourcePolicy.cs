using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Runtime;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         测试用六值资源分类策略：工作区内为基线，.git/.agents/.codex/.aws 为受保护元数据，
///         附加根与系统保护路径不启用，工作区外为越界；KnownSensitive 恒 false。
///     </para>
///     <para>
///         A test six-value classification policy: inside the workspace is baseline, .git/.agents/.codex/.aws are
///         protected metadata, additional roots and system-protected paths are disabled, and outside-workspace is
///         OutsideWorkspace; KnownSensitive is always false.
///     </para>
/// </summary>
internal sealed class TestResourcePolicy(string workspaceRoot) : IResourceAccessPolicy
{
    public ResourceClassification? Classify(string capability, string targetPath)
    {
        if (!Workspace.IsInside(workspaceRoot, targetPath))
            return new ResourceClassification(ResourceClass.OutsideWorkspace, false);

        var metadata = new[] { ".git", ".agents", ".codex", ".aws" }
                      .Any(name => Workspace.IsInside(Path.Combine(workspaceRoot, name), targetPath));
        return metadata
            ? new ResourceClassification(ResourceClass.ProtectedMetadata, false)
            : new ResourceClassification(ResourceClass.BaselineWorkspace, false);
    }

    // 测试策略不携带权限系统保护根：如实返回不重叠，由分类侧的六值矩阵兜底。
    public bool OverlapsPermissionSystemRoots(string path) => false;
}
