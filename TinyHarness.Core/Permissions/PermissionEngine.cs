using System.Security.Cryptography;
using System.Text;
using TinyHarness.Core.Configuration;
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
/// <item>configured command rule — a mode-specific direct-command pattern or exact shell-command/cwd match;</item>
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

    private readonly string                               _workspaceRoot;
    private readonly IReadOnlyList<ConfiguredCommandRule> _commandRules;
    private readonly List<PermissionRule>                 _sessionGrants    = [];
    private readonly List<PermissionRule>                 _sessionDenies    = [];
    private readonly HashSet<string>                      _oneShotApprovals = new(StringComparer.Ordinal);

    /// <summary>
    /// 为指定工作区建立独立的会话权限状态。
    /// Creates isolated session permission state for the specified workspace.
    /// </summary>
    public PermissionEngine(string workspaceRoot, IEnumerable<CommandRule>? commandRules = null)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            throw new ArgumentException("A workspace root is required.", nameof(workspaceRoot));
        }

        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _commandRules = (commandRules ?? []).Select(rule => ConfiguredCommandRule.Create(rule, _workspaceRoot))
                                            .ToArray();
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

        // Precisely configured process rules are the only commands that bypass
        // interactive approval. They match executable, each argument pattern,
        // and one exact normalized working directory.
        if (string.Equals(preparation.Capability, "process.execute", StringComparison.Ordinal) &&
            _commandRules.Any(rule => rule.Matches(preparation)))
        {
            return PermissionDecision.Allow;
        }

        // 6/7. Default policy, then ask.
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
        => new(preparation.Capability, preparation.TargetPaths.ToArray(), preparation.SessionConstraint);

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
            preparation.ToolName                                                               + '\n' +
            preparation.Capability                                                             + '\n' +
            string.Join('\n', preparation.TargetPaths.OrderBy(x => x, StringComparer.Ordinal)) + '\n' +
            preparation.SessionConstraint                                                      + '\n' +
            preparation.Arguments.ToJsonString();

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>
    /// 已规范化的命令允许规则。direct 参数 pattern 只在单个参数内支持 '*' 与 '?'；shell
    /// 规则则精确匹配 flavor、完整命令文本和工作目录，不接受 wildcard。
    /// </summary>
    private sealed record ConfiguredCommandRule(
        string                Mode,
        string                Executable,
        IReadOnlyList<string> ArgumentPatterns,
        string                Shell,
        string                Command,
        string                WorkingDirectory)
    {
        private static readonly StringComparison ExecutableComparison =
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        public static ConfiguredCommandRule Create(CommandRule rule, string workspaceRoot)
        {
            ArgumentNullException.ThrowIfNull(rule);
            if (rule.Mode is not "direct" and not "shell")
            {
                throw new InvalidDataException("Command rule mode must be 'direct' or 'shell'.");
            }

            if (rule.Mode == "direct" && string.IsNullOrWhiteSpace(rule.Executable))
            {
                throw new InvalidDataException("A direct command rule executable must be a non-empty string.");
            }

            if (rule.Mode == "direct" && (!string.IsNullOrEmpty(rule.Shell) || !string.IsNullOrEmpty(rule.Command)))
            {
                throw new InvalidDataException("A direct command rule cannot contain shell or command fields.");
            }

            if (rule.Mode == "shell" && (string.IsNullOrWhiteSpace(rule.Shell) || string.IsNullOrWhiteSpace(rule.Command)))
            {
                throw new InvalidDataException("A shell command rule requires a shell flavor and exact command.");
            }

            if (rule.Mode == "shell" && (!string.IsNullOrEmpty(rule.Executable) || rule.Arguments.Count != 0))
            {
                throw new InvalidDataException("A shell command rule cannot contain executable or arguments fields.");
            }

            var workspace = new Workspace(workspaceRoot);
            var directory =
                workspace.ResolveInside(string.IsNullOrWhiteSpace(rule.WorkingDirectory) ? "." : rule.WorkingDirectory,
                                        "commandRules.workingDirectory");
            var executable = rule.Mode == "direct"
                ? NormalizeExecutable(rule.Executable.Trim(), directory, workspace)
                : string.Empty;
            return new ConfiguredCommandRule(rule.Mode, executable, rule.Arguments.ToArray(), rule.Shell,
                                             rule.Command, directory);
        }

        public bool Matches(ToolPreparation preparation)
        {
            var args = preparation.Arguments;
            var mode = args["mode"]?.GetValue<string>() ?? "direct";
            var executable = args["executable"]?.GetValue<string>();
            var directory = args["workingDirectory"]?.GetValue<string>();
            var arguments = args["arguments"] as System.Text.Json.Nodes.JsonArray;
            if (!string.Equals(Mode, mode, StringComparison.Ordinal) || directory is null ||
                !string.Equals(WorkingDirectory, directory, ExecutableComparison))
            {
                return false;
            }

            if (Mode == "shell")
            {
                var shell   = args["shell"]?.GetValue<string>();
                var command = args["command"]?.GetValue<string>();
                return string.Equals(Shell, shell, StringComparison.Ordinal) &&
                       string.Equals(Command, command, StringComparison.Ordinal);
            }

            if (executable is null || arguments is null ||
                !string.Equals(Executable, executable, ExecutableComparison) ||
                arguments.Count != ArgumentPatterns.Count)
            {
                return false;
            }

            for (var i = 0; i < arguments.Count; i++)
            {
                var argument = arguments[i]?.GetValue<string>();
                if (argument is null || !WildcardMatch(ArgumentPatterns[i], argument))
                {
                    return false;
                }
            }

            return true;
        }

        private static string NormalizeExecutable(string executable, string workingDirectory, Workspace workspace)
        {
            var pathLike = Path.IsPathRooted(executable) || executable.Contains(Path.DirectorySeparatorChar) ||
                           executable.Contains(Path.AltDirectorySeparatorChar);
            if (!pathLike)
            {
                return executable;
            }

            var full = Path.GetFullPath(Path.IsPathRooted(executable)
                                            ? executable
                                            : Path.Combine(workingDirectory, executable));
            if (!Path.IsPathRooted(executable) && !Workspace.IsInside(workspace.Root, full))
            {
                throw new InvalidDataException("A command rule's relative executable escapes the workspace root.");
            }

            return full;
        }

        private static bool WildcardMatch(string pattern, string value)
        {
            var patternIndex = 0;
            var valueIndex   = 0;
            var starIndex    = -1;
            var starValue    = -1;
            while (valueIndex < value.Length)
            {
                if (patternIndex < pattern.Length &&
                    (pattern[patternIndex] == '?' || pattern[patternIndex] == value[valueIndex]))
                {
                    patternIndex++;
                    valueIndex++;
                }
                else if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
                {
                    starIndex = patternIndex++;
                    starValue = valueIndex;
                }
                else if (starIndex >= 0)
                {
                    patternIndex = starIndex + 1;
                    valueIndex   = ++starValue;
                }
                else
                {
                    return false;
                }
            }

            while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                patternIndex++;
            }

            return patternIndex == pattern.Length;
        }
    }
}
