using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Runtime;

namespace TinyHarness.Core.Services.Permissions;

/// <summary>
///     <para>
///         对不可变准备计划应用硬边界、临时授权与配置策略，给出 Allow、Ask 或 Deny。
///     </para>
///     <para>
///         Applies hard boundaries, temporary grants and configured policy to immutable prepared plans,
///         returning Allow, Ask or Deny.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         优先级为只读 worker 硬限制、工作区边界、显式拒绝、NeedsInspection 阻断、
///         一次性授权、能力与范围授权、配置命令规则、默认策略；未知能力默认询问。
///         这是应用层策略，不是 OS 沙箱；运行时另行强制文件系统边界。
///         Session 命名保留兼容用语，宿主须把临时授权状态限制为本次运行，不跨未来回合复活。
///     </para>
///     <para>
///         Priority is read-only worker restrictions, workspace boundaries, explicit denies,
///         NeedsInspection blocking, one-shot grants, capability-and-scope grants, configured command rules
///         and default policy; unknown capabilities default to Ask.
///         This is application policy, not an OS sandbox; the runtime enforces filesystem boundaries separately.
///         Session naming is retained for compatibility; the host must limit temporary grant state
///         to the current run rather than revive it across future turns.
///     </para>
/// </remarks>
public sealed class PermissionEngine
{
    private static readonly IReadOnlyDictionary<string, PermissionDecision> DefaultInsidePolicy =
        new Dictionary<string, PermissionDecision>(StringComparer.Ordinal)
        {
            ["filesystem.list"]   = PermissionDecision.Allow,
            ["filesystem.search"] = PermissionDecision.Allow,
            ["filesystem.read"]   = PermissionDecision.Allow,
            ["filesystem.write"]  = PermissionDecision.Ask
        };

    private readonly IReadOnlyList<ConfiguredCommandRule> _commandRules;

    private readonly bool                 _denyNonReadOnlyCapabilities;
    private readonly HashSet<string>      _oneShotApprovals = new(StringComparer.Ordinal);
    private readonly List<PermissionRule> _sessionDenies    = [];
    private readonly List<PermissionRule> _sessionGrants    = [];
    private readonly string               _workspaceRoot;


    /// <summary>
    ///     <para>
    ///         为指定工作区建立独立权限状态。
    ///     </para>
    ///     <para>
    ///         Creates isolated permission state for the specified workspace.
    ///     </para>
    /// </summary>
    public PermissionEngine(
        string workspaceRoot, IEnumerable<CommandRule>? commandRules = null, bool denyNonReadOnlyCapabilities = false)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new ArgumentException("A workspace root is required.", nameof(workspaceRoot));

        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _commandRules = (commandRules ?? []).Select(rule => ConfiguredCommandRule.Create(rule, _workspaceRoot))
                                            .ToArray();
        _denyNonReadOnlyCapabilities = denyNonReadOnlyCapabilities;
    }

    /// <summary>
    ///     <para>
    ///         当前 NeedsInspection 非只读阻断原因；未阻断时为 null。
    ///     </para>
    ///     <para>
    ///         The current NeedsInspection reason for blocking non-read-only capabilities; null when unblocked.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         仅由宿主依据中断事实设置，并在用户确认检查后清除；模型输出不能建立或解除阻断。
    ///     </para>
    ///     <para>
    ///         Only the host sets it from interruption facts and clears it after user inspection confirmation;
    ///         model output cannot create or remove the block.
    ///     </para>
    /// </remarks>
    public string? NonReadOnlyBlockReason { get; private set; }

    /// <summary>
    ///     <para>
    ///         只读地计算当前调用的权限结论；重复调用不会消费一次性授权或改变会话状态。
    ///     </para>
    ///     <para>
    ///         Decides how to treat a prepared invocation without mutating state. Safe to
    ///         call repeatedly; the loop may re-run a decision after each state change.
    ///     </para>
    /// </summary>
    public PermissionDecision Decide(ToolPreparation preparation)
    {
        // 只读 worker 限制是硬拒绝，未知能力也不能被 grant 或命令规则放行；普通 CLI 不启用此限制。
        if (_denyNonReadOnlyCapabilities && !ToolCapabilities.IsReadOnly(preparation.Capability))
            return PermissionDecision.Deny;

        // 即使 Prepare 漏检，越界目标仍须拒绝。
        if (preparation.TargetPaths.Any(target => !Workspace.IsInside(_workspaceRoot, target)))
            return PermissionDecision.Deny;

        if (_sessionDenies.Any(rule => rule.Matches(preparation, true))) return PermissionDecision.Deny;

        // NeedsInspection 优先于所有授权与命令规则；仅由宿主在用户确认检查后解除。
        if (NonReadOnlyBlock(preparation) is not null) return PermissionDecision.Deny;

        if (_oneShotApprovals.Contains(Fingerprint(preparation))) return PermissionDecision.Allow;

        if (_sessionGrants.Any(rule => rule.Matches(preparation))) return PermissionDecision.Allow;

        // 命令免审批必须精确匹配模式与规范化 cwd；direct 逐参数匹配，shell 精确匹配完整命令。
        if (string.Equals(preparation.Capability, "process.execute", StringComparison.Ordinal) &&
            _commandRules.Any(rule => rule.Matches(preparation)))
            return PermissionDecision.Allow;

        return DefaultInsidePolicy.GetValueOrDefault(preparation.Capability, PermissionDecision.Ask);
    }

    /// <summary>
    ///     <para>
    ///         记录绑定完整调用指纹的一次性预授权。
    ///     </para>
    ///     <para>
    ///         Records a one-shot preauthorization bound to the complete invocation fingerprint.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         仅供先授权、后 Decide 的调用使用；在授权阶段用 TryConsumeOnce 预留消费，
    ///         不得等到执行后才消费，否则同轮相同调用可重复获批。
    ///         当前调用的交互批准无需存入引擎，避免误批后续调用。
    ///     </para>
    ///     <para>
    ///         Use only when authorization precedes Decide; reserve it with TryConsumeOnce during authorization,
    ///         not after execution, so identical calls in one round cannot reuse the grant.
    ///         Do not store an interactive approval for the current call, which could approve a later call accidentally.
    ///     </para>
    /// </remarks>
    public void GrantOnce(ToolPreparation preparation)
    {
        _oneShotApprovals.Add(Fingerprint(preparation));
    }

    /// <summary>
    ///     <para>
    ///         消费完全匹配的一次性预授权并返回是否成功；不存在时不改变状态。
    ///     </para>
    ///     <para>
    ///         Consumes a matching one-shot preauthorization, returning whether it was spent;
    ///         does not change state if absent.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Agent Loop 在授权阶段消费，使一次批准只覆盖一次尝试；未用一次性授权批准时为无操作。
    ///     </para>
    ///     <para>
    ///         Agent Loop consumes it during authorization so one approval covers one attempt;
    ///         this is a no-op for approvals made without a one-shot grant.
    ///     </para>
    /// </remarks>
    public bool TryConsumeOnce(ToolPreparation preparation) => _oneShotApprovals.Remove(Fingerprint(preparation));

    /// <summary>
    ///     <para>
    ///         记录绑定能力、资源范围与执行策略身份的临时允许规则。
    ///     </para>
    ///     <para>
    ///         Records a temporary allow rule bound to capability, resource scope and execution-policy identity.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         SessionGrantConstraint 绑定命令及策略；授权不能跨后端、隔离策略或环境复用。
    ///         宿主限定状态为本次运行，Session 命名不表示长期会话授权。
    ///     </para>
    ///     <para>
    ///         SessionGrantConstraint binds command and policy; grants cannot cross backends, isolation policies
    ///         or environments. The host limits state to the current run; Session naming does not grant future turns.
    ///     </para>
    /// </remarks>
    public void GrantSession(ToolPreparation preparation)
    {
        _sessionGrants.Add(ToRule(preparation, false));
    }

    /// <summary>
    ///     <para>
    ///         记录绑定能力、资源范围与命令身份的显式拒绝规则。
    ///     </para>
    ///     <para>
    ///         Records an explicit deny rule bound to capability, resource scope and command identity.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         拒绝不绑定执行策略，避免切换后端使其失效。
    ///     </para>
    ///     <para>
    ///         Denials do not bind execution policy, so switching backends cannot invalidate them.
    ///     </para>
    /// </remarks>
    public void DenySession(ToolPreparation preparation)
    {
        _sessionDenies.Add(ToRule(preparation, true));
    }

    /// <summary>
    ///     <para>
    ///         清理本次运行内的临时允许状态；保留显式拒绝和 NeedsInspection 阻断。
    ///     </para>
    ///     <para>
    ///         Clears temporary allow state for the current run while retaining explicit denials
    ///         and the NeedsInspection block.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         用于项目指令版本变化等使旧计划失效的安全边界。一次性预授权和运行期 allow 一并失效；
    ///         deny 与阻断不因规则刷新被移除，后续计划必须重新授权。
    ///     </para>
    ///     <para>
    ///         Used at safety boundaries such as a project-instruction revision invalidating an old plan.
    ///         One-shot and run-scoped allows both lapse; denials and blocking survive, and later plans
    ///         must be authorized again.
    ///     </para>
    /// </remarks>
    public void ClearTemporaryAllows()
    {
        _oneShotApprovals.Clear();
        _sessionGrants.Clear();
    }

    /// <summary>
    ///     <para>
    ///         设置或清除非只读阻断，任何授权或命令规则均不得绕过。
    ///     </para>
    ///     <para>
    ///         Sets or clears non-read-only blocking, which no grant or command rule may bypass.
    ///     </para>
    /// </summary>
    /// <param name="reason">
    ///     <para>
    ///         null 或空白表示清除；宿主仅在用户明确确认检查后清除。
    ///         非空时写入、进程执行及未知能力均拒绝。
    ///     </para>
    ///     <para>
    ///         Null or whitespace clears the block; the host clears it only after explicit user inspection confirmation.
    ///         A nonempty reason blocks writes, process execution and unknown capabilities.
    ///     </para>
    /// </param>
    public void SetNonReadOnlyBlock(string? reason) =>
        NonReadOnlyBlockReason = string.IsNullOrWhiteSpace(reason) ? null : reason;

    /// <summary>
    ///     <para>
    ///         返回本次调用被 NeedsInspection 阻断的原因；未被阻断时为 null。主循环用它把阻断
    ///         原因带回给模型，而不是让它猜测拒绝理由。
    ///     </para>
    ///     <para>
    ///         Returns the NeedsInspection-block reason applying to this invocation, or
    ///         null when it is not blocked. The agent loop uses it to carry the reason
    ///         back to the model instead of leaving it guessing.
    ///     </para>
    /// </summary>
    public string? NonReadOnlyBlock(ToolPreparation preparation) =>
        NonReadOnlyBlockReason is null || ToolCapabilities.IsReadOnly(preparation.Capability)
            ? null
            : NonReadOnlyBlockReason;

    /// <summary>
    ///     <para>
    ///         从不可变准备计划提取能力和规范化目标，生成会话规则；允许规则绑定授权约束，
    ///         拒绝规则绑定命令约束。
    ///     </para>
    ///     <para>
    ///         Creates a session rule from a prepared plan's capability and normalized
    ///         targets; grants bind the grant constraint, denies the command constraint.
    ///     </para>
    /// </summary>
    private static PermissionRule ToRule(ToolPreparation preparation, bool deny) => new(
                                                                                        preparation.Capability,
                                                                                        preparation.TargetPaths.ToArray(),
                                                                                        deny ? preparation.SessionConstraint : GrantConstraint(preparation));

    /// <summary>
    ///     <para>
    ///         会话授权与一次性授权使用的约束：优先执行策略绑定的授权约束，缺省回落到命令约束。
    ///     </para>
    ///     <para>
    ///         The constraint used by session grants and one-shot approvals: the
    ///         policy-bound grant constraint when present, otherwise the command constraint.
    ///     </para>
    /// </summary>
    private static string? GrantConstraint(ToolPreparation preparation) =>
        preparation.SessionGrantConstraint ?? preparation.SessionConstraint;

    /// <summary>
    ///     <para>
    ///         对完整准备调用生成稳定 SHA-256 指纹，限制一次性授权精确匹配。
    ///     </para>
    ///     <para>
    ///         Generates a stable SHA-256 fingerprint of the complete prepared invocation
    ///         for exact one-shot approval matching.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         指纹包含工具名、能力、排序目标路径、授权约束与规范化参数；后端或策略变化不得命中旧授权。
    ///     </para>
    ///     <para>
    ///         Includes tool name, capability, sorted target paths, grant constraint and normalized arguments;
    ///         backend or policy changes must not match old approvals.
    ///     </para>
    /// </remarks>
    private static string Fingerprint(ToolPreparation preparation)
    {
        var canonical =
            preparation.ToolName                                                               + '\n' +
            preparation.Capability                                                             + '\n' +
            string.Join('\n', preparation.TargetPaths.OrderBy(x => x, StringComparer.Ordinal)) + '\n' +
            GrantConstraint(preparation)                                                       + '\n' +
            preparation.Arguments.ToJsonString();

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>
    ///     <para>
    ///         已规范化的命令允许规则，区分 direct 参数模式与 shell 精确命令。
    ///     </para>
    ///     <para>
    ///         A normalized command allow rule distinguishing direct argument patterns from exact shell commands.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         direct 仅在单个参数内支持 * 与 ?；shell 精确匹配 flavor、完整命令与 cwd，不接受通配符。
    ///     </para>
    ///     <para>
    ///         Direct mode supports * and ? only within individual arguments; shell mode matches flavor,
    ///         full command and cwd exactly, without wildcards.
    ///     </para>
    /// </remarks>
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
                throw new InvalidDataException("Command rule mode must be 'direct' or 'shell'.");

            switch (rule.Mode)
            {
                case "direct" when string.IsNullOrWhiteSpace(rule.Executable) :
                    throw new InvalidDataException("A direct command rule executable must be a non-empty string.");
                case "direct" when !string.IsNullOrEmpty(rule.Shell) || !string.IsNullOrEmpty(rule.Command) :
                    throw new InvalidDataException("A direct command rule cannot contain shell or command fields.");
                case "shell" when
                    string.IsNullOrWhiteSpace(rule.Shell) || string.IsNullOrWhiteSpace(rule.Command) :
                    throw new InvalidDataException("A shell command rule requires a shell flavor and exact command.");
                case "shell" when !string.IsNullOrEmpty(rule.Executable) || rule.Arguments.Count != 0 :
                    throw new
                        InvalidDataException("A shell command rule cannot contain executable or arguments fields.");
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
            var args       = preparation.Arguments;
            var mode       = args["mode"]?.GetValue<string>() ?? "direct";
            var executable = args["executable"]?.GetValue<string>();
            var directory  = args["workingDirectory"]?.GetValue<string>();
            var arguments  = args["arguments"] as JsonArray;
            if (!string.Equals(Mode, mode, StringComparison.Ordinal) || directory is null ||
                !string.Equals(WorkingDirectory, directory, ExecutableComparison))
                return false;

            if (Mode == "shell")
            {
                var shell   = args["shell"]?.GetValue<string>();
                var command = args["command"]?.GetValue<string>();
                return string.Equals(Shell, shell, StringComparison.Ordinal) &&
                       string.Equals(Command, command, StringComparison.Ordinal);
            }

            if (executable is null                                           || arguments is null ||
                !string.Equals(Executable, executable, ExecutableComparison) ||
                arguments.Count != ArgumentPatterns.Count)
                return false;

            for (var i = 0; i < arguments.Count; i++)
            {
                var argument = arguments[i]?.GetValue<string>();
                if (argument is null || !WildcardMatch(ArgumentPatterns[i], argument)) return false;
            }

            return true;
        }

        private static string NormalizeExecutable(string executable, string workingDirectory, Workspace workspace)
        {
            var pathLike = Path.IsPathRooted(executable) || executable.Contains(Path.DirectorySeparatorChar) ||
                           executable.Contains(Path.AltDirectorySeparatorChar);
            if (!pathLike) return executable;

            var full = Path.GetFullPath(Path.IsPathRooted(executable)
                                            ? executable
                                            : Path.Combine(workingDirectory, executable));
            if (!Path.IsPathRooted(executable) && !Workspace.IsInside(workspace.Root, full))
                throw new InvalidDataException("A command rule's relative executable escapes the workspace root.");

            return full;
        }

        private static bool WildcardMatch(string pattern, string value)
        {
            var patternIndex = 0;
            var valueIndex   = 0;
            var starIndex    = -1;
            var starValue    = -1;
            while (valueIndex < value.Length)
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

            while (patternIndex < pattern.Length && pattern[patternIndex] == '*') patternIndex++;

            return patternIndex == pattern.Length;
        }
    }
}
