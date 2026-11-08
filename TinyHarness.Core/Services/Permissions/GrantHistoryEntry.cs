using System.Security.Cryptography;
using System.Text;
using TinyHarness.Core.Models.Permissions;

namespace TinyHarness.Core.Services.Permissions;

/// <summary>
///     <para>
///         授权历史条目单一来源：把一条 Grant 描述为 Reviewer 授权上下文与审批展示共用的摘要行，
///         覆盖范围、能力、状态、目标摘要、约束、有效期与策略修订，超长字段预算内截断。
///         CLI 与 Desktop 两侧注入同构，不各自拼装。
///     </para>
///     <para>
///         The single source of grant-history entries: it describes one grant as a summary line shared by the
///         reviewer authorization context and approval display, covering scope, capability, state, target
///         summary, constraint, expiry, and policy revision, with long fields truncated within budget. CLI and
///         Desktop inject the same shape instead of assembling their own.
///     </para>
/// </summary>
public static class GrantHistoryEntry
{
    private const int MaxTargets              = 6;
    private const int MaxTargetCharacters     = 120;
    private const int MaxConstraintCharacters = 160;
    private const int MaxPolicyCharacters     = 96;

    /// <summary>
    ///     <para>
    ///         描述一条授权：scope/capability/state 之后依次附目标摘要、约束（截断）、有效期与策略
    ///         修订（SHA-256 前 16 个十六进制字符）；无范围授权目标显示 (unscoped)。
    ///     </para>
    ///     <para>
    ///         Describes one grant: after scope, capability, and state it appends the target summary, the
    ///         truncated constraint, the expiry, and the policy revision (the first 16 hex characters of its
    ///         SHA-256); an unscoped grant shows (unscoped) targets.
    ///     </para>
    /// </summary>
    public static string Describe(Grant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var targets = grant.Targets.Count == 0
            ? "(unscoped)"
            : string.Join(", ", grant.Targets.Take(MaxTargets)
                                     .Select(target => Truncate(target, MaxTargetCharacters))) +
              (grant.Targets.Count > MaxTargets ? $" (+{grant.Targets.Count - MaxTargets} more)" : string.Empty);
        return "grant "                                                                                +
               $"{grant.Scope.ToString().ToLowerInvariant()} {grant.Capability} state={grant.State}; " +
               $"targets=[{targets}]; "                                                                +
               $"constraint={Truncate(grant.Constraint ?? "(none)", MaxConstraintCharacters)}; "       +
               $"expires={grant.ExpiresUtc?.ToString("yyyy-MM-dd HH:mm 'UTC'") ?? "(never)"}; "        +
               $"policyRevision={PolicyRevision(grant.PolicyIdentity)}";
    }

    /// <summary>
    ///     <para>策略修订取身份串 SHA-256 的前 16 个十六进制字符；缺失显示 (none)。</para>
    ///     <para>
    ///         The policy revision is the first 16 hex characters of the identity string's SHA-256; missing
    ///         identities show (none).
    ///     </para>
    /// </summary>
    private static string PolicyRevision(string? policyIdentity)
    {
        if (string.IsNullOrEmpty(policyIdentity)) return "(none)";

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(policyIdentity)))[..16];
    }

    /// <summary>预算内截断，超长以省略号标记。Truncates within budget, marking the cut with an ellipsis.</summary>
    private static string Truncate(string text, int maximumCharacters)
    {
        if (text.Length <= maximumCharacters) return text;

        return string.Concat(text.AsSpan(0, maximumCharacters - 1), "…");
    }
}
