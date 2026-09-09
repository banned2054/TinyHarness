using TinyHarness.Core.Runtime;
using TinyHarness.Core.Tools;

namespace TinyHarness.Core.Permissions;

/// <summary>
/// 会话范围的允许或拒绝规则，由能力名和规范化资源范围组成。目录范围覆盖后代路径；
/// 允许规则必须覆盖所有目标，拒绝规则命中任一目标即可阻止整个调用。
///
/// A session-scoped grant or deny: a capability bound to a normalized resource
/// scope and optional invocation constraint. An empty <see cref="ScopePaths"/> means "any scope". A rule matches a
/// grant must cover every target; a deny matches any covered target. Directory
/// scopes cover descendants, and file scopes cover that file.
/// </summary>
internal sealed record PermissionRule(string Capability, IReadOnlyList<string> ScopePaths, string? SessionConstraint)
{
    /// <summary>
    /// 判断准备计划是否落入本规则；<paramref name="deny"/> 决定采用“任一目标”还是“所有目标”语义。
    /// Tests whether a prepared plan falls under this rule, using any-target semantics for denies and
    /// all-target semantics for grants.
    /// </summary>
    public bool Matches(ToolPreparation preparation, bool deny = false)
    {
        if (!string.Equals(Capability, preparation.Capability, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.Equals(SessionConstraint, preparation.SessionConstraint, StringComparison.Ordinal))
        {
            return false;
        }

        if (ScopePaths.Count == 0)
        {
            return true; // Unscoped rule: applies to any resource for this capability.
        }

        if (preparation.TargetPaths.Count == 0)
        {
            return false;
        }

        bool IsCovered(string target) => ScopePaths.Any(scope => Workspace.IsInside(scope, target));

        // Grants must cover every target; a deny blocks the entire invocation
        // as soon as any target intersects its scope.
        return deny ? preparation.TargetPaths.Any(IsCovered) : preparation.TargetPaths.All(IsCovered);
    }
}
