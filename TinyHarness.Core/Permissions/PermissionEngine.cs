using System.Security.Cryptography;
using System.Text;
using TinyHarness.Core.Runtime;
using TinyHarness.Core.Tools;

namespace TinyHarness.Core.Permissions;

/// <summary>
/// 应用层权限引擎。依据工作区硬边界、会话规则、一次性授权和默认策略，
/// 对已准备的工具调用给出允许、询问或拒绝结论。它属于策略与审批层，不是 OS 沙箱。
///
/// Application-layer permission engine. It decides, for each prepared
/// tool invocation, whether to allow, ask or deny based on the current policy and
/// the session's recorded approvals.
///
/// Decision priority, most authoritative first:
/// <list type="number">
/// <item>hard deny — any target outside the workspace root;</item>
/// <item>session deny — an explicit host rule recorded for a capability + scope;</item>
/// <item>one-shot approval — a user "allow once" for this exact invocation (spent
/// the first time the covered invocation executes, see
/// <see cref="TryConsumeOnce"/>);</item>
/// <item>session grant — a user "allow session" for a capability + scope;</item>
/// <item>default policy — read-only capabilities inside the workspace allow,
/// write capabilities ask;</item>
/// <item>ask — anything not otherwise allowed.</item>
/// </list>
///
/// This is policy/approval, not an OS sandbox; the runtime enforces the actual
/// filesystem boundary separately.
/// </summary>
public sealed class PermissionEngine
{
    private static readonly IReadOnlyDictionary<string, PermissionDecision> DefaultInsidePolicy =
        new Dictionary<string, PermissionDecision>(StringComparer.Ordinal)
        {
            ["filesystem.list"]   = PermissionDecision.Allow,
            ["filesystem.search"] = PermissionDecision.Allow,
            ["filesystem.read"]   = PermissionDecision.Allow,
            ["filesystem.write"]  = PermissionDecision.Ask,
        };

    private readonly string _workspaceRoot;
    private readonly List<PermissionRule> _sessionGrants = [];
    private readonly List<PermissionRule> _sessionDenies = [];
    private readonly HashSet<string> _oneShotApprovals = new(StringComparer.Ordinal);

    /// <summary>
    /// 为指定工作区建立独立的会话权限状态。
    /// Creates isolated session permission state for the specified workspace.
    /// </summary>
    public PermissionEngine(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            throw new ArgumentException("A workspace root is required.", nameof(workspaceRoot));
        }

        _workspaceRoot = Path.GetFullPath(workspaceRoot);
    }

    /// <summary>
    /// 只读地计算当前调用的权限结论；重复调用不会消费一次性授权或改变会话状态。
    ///
    /// Decides how to treat a prepared invocation without mutating state. Safe to
    /// call repeatedly; the loop may re-run a decision after each state change.
    /// </summary>
    public PermissionDecision Decide(ToolPreparation preparation)
    {
        // 1. Hard deny: a target outside the workspace is never allowed, even if
        // the tool failed to reject it at Prepare time (defense in depth).
        if (preparation.TargetPaths.Any(target => !Workspace.IsInside(_workspaceRoot, target)))
        {
            return PermissionDecision.Deny;
        }

        // 2. Explicit/session deny wins over any earlier approval.
        if (_sessionDenies.Any(rule => rule.Matches(preparation, deny : true)))
        {
            return PermissionDecision.Deny;
        }

        // 3. One-shot approval for this exact invocation.
        if (_oneShotApprovals.Contains(Fingerprint(preparation)))
        {
            return PermissionDecision.Allow;
        }

        // 4. Session grant for this capability + scope.
        if (_sessionGrants.Any(rule => rule.Matches(preparation)))
        {
            return PermissionDecision.Allow;
        }

        // 5/6. Default policy, then ask.
        return DefaultInsidePolicy.TryGetValue(preparation.Capability, out var decision)
            ? decision
            : PermissionDecision.Ask;
    }

    /// <summary>
    /// 记录对该完整调用指纹的一次性预授权，须在实际执行时由
    /// <see cref="TryConsumeOnce"/> 消费。
    ///
    /// Records an "allow once" approval for this exact invocation. Use only for
    /// pre-authorization, i.e. when the caller grants before a later
    /// <see cref="Decide"/>/execution cycle; an approval that is followed
    /// immediately by the execution it covers should not be persisted at all.
    /// The caller must spend the approval via <see cref="TryConsumeOnce"/> when
    /// the covered invocation executes, otherwise it would approve every later
    /// identical invocation for the rest of the session.
    /// </summary>
    public void GrantOnce(ToolPreparation preparation)
        => _oneShotApprovals.Add(Fingerprint(preparation));

    /// <summary>
    /// 消费完全匹配的一次性授权并返回是否成功消费；没有对应授权时不改变状态。
    ///
    /// Spends a one-shot approval for this exact invocation when one is present,
    /// and reports whether one was spent. The Agent Loop calls this as an
    /// approved execution commits, so an "allow once" covers exactly one attempt
    /// and an identical later invocation must be approved again. A no-op when
    /// the execution was approved through another mechanism (session grant,
    /// default policy), because no one-shot entry exists for it.
    /// </summary>
    public bool TryConsumeOnce(ToolPreparation preparation)
        => _oneShotApprovals.Remove(Fingerprint(preparation));

    /// <summary>
    /// 记录覆盖该能力与资源范围的会话级允许规则。
    /// Records a session-wide allow rule for this capability and resource scope.
    /// </summary>
    public void GrantSession(ToolPreparation preparation)
        => _sessionGrants.Add(ToRule(preparation));

    /// <summary>
    /// 记录覆盖该能力与资源范围的会话级拒绝规则。
    /// Records a session-wide deny rule for this capability and resource scope.
    /// </summary>
    public void DenySession(ToolPreparation preparation)
        => _sessionDenies.Add(ToRule(preparation));

    /// <summary>
    /// 从不可变准备计划提取能力和规范化目标，生成会话规则。
    /// Creates a session rule from a prepared plan's capability and normalized targets.
    /// </summary>
    private static PermissionRule ToRule(ToolPreparation preparation)
        => new(preparation.Capability, preparation.TargetPaths.ToArray());

    /// <summary>
    /// 对工具名、能力、排序后的目标路径和规范化参数生成稳定 SHA-256 指纹，
    /// 保证单次授权只匹配完全相同的调用。
    ///
    /// A stable fingerprint of the full prepared invocation: tool name, capability,
    /// normalized target paths and normalized arguments. "Allow once" matches only
    /// the exact same call, never a re-interpretation of similar input.
    /// </summary>
    private static string Fingerprint(ToolPreparation preparation)
    {
        var canonical =
            preparation.ToolName + '\n' +
            preparation.Capability + '\n' +
            string.Join('\n', preparation.TargetPaths.OrderBy(x => x, StringComparer.Ordinal)) + '\n' +
            preparation.Arguments.ToJsonString();

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
