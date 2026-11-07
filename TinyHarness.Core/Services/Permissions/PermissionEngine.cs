using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Runtime;

namespace TinyHarness.Core.Services.Permissions;

/// <summary>
///     <para>
///         对不可变准备计划应用固定决策链：硬限制、会话拒绝记忆、资源分类、基线放行、活动授权与审批路由，
///         给出 Allow、Ask 或 Deny，并携带执行分类与缺失能力等审核维度。
///     </para>
///     <para>
///         Applies the fixed decision chain — hard limits, session denial memory, resource classification,
///         baseline allowance, active grants and approval routing — to immutable prepared plans, returning
///         Allow, Ask or Deny with the execution level and missing capabilities as review dimensions.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         求值顺序固定：worker 与未信任工作区硬限制、NeedsInspection 阻断、系统保护资源、拒绝记忆、
///         资源分类（沙箱不可用拒绝、宿主申请 Level3、网络申请 Level2、文件越界与受保护元数据写入
///         Level2）、基线 Level1 放行、活动授权、审批。分类与级别是审核维度，不是授权来源；
///         授权只来自 Level1 基线或 <see cref="GrantRegistry" /> 中的活动 Grant。
///         这是应用层策略，不是 OS 沙箱；运行时另行强制文件系统边界。
///     </para>
///     <para>
///         The evaluation order is fixed: worker and untrusted-workspace hard limits, NeedsInspection blocking,
///         system-protected resources, denial memory, resource classification (sandbox-unavailable denial, host
///         request Level3, network request Level2, outside-workspace and protected-metadata writes Level2),
///         baseline Level1 allowance, active grants, approval. Levels are review dimensions, never sources of
///         authority; authorization comes only from the Level1 baseline or an active Grant in the
///         <see cref="GrantRegistry" />. This is application policy, not an OS sandbox; the runtime enforces
///         filesystem boundaries separately.
///     </para>
/// </remarks>
public sealed class PermissionEngine
{
    private const string AdditionalPathWrite = "additional-path-write";
    private const string AdditionalPathRead  = "additional-path-read";
    private const string MetadataWrite       = "metadata-write";

    private static readonly IReadOnlySet<string> KnownCapabilities =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "filesystem.list", "filesystem.search", "filesystem.read", "filesystem.write",
            "process.execute", "session.work"
        };

    private static readonly IReadOnlyList<string> HostExecutionMissing = ["host-execution"];
    private static readonly IReadOnlyList<string> NetworkAccessMissing = ["network-access"];
    private readonly        TimeProvider          _clock;
    private readonly        HashSet<string>       _deniedFingerprints = new(StringComparer.Ordinal);
    private readonly        List<PermissionRule>  _deniedRules        = [];

    private readonly bool                                           _denyNonReadOnlyCapabilities;
    private readonly Func<ToolPreparation, FrozenCapabilityScope?>? _frozenScopeReader;
    private readonly bool                                           _networkStandingEnabled;
    private readonly IResourceAccessPolicy                          _resourcePolicy;
    private readonly bool                                           _sandboxExecutionAvailable;
    private readonly Func<bool>?                                    _workspaceReadOnly;
    private readonly string                                         _workspaceRoot;
    private          long                                           _authorizationRevision;

    /// <summary>
    ///     <para>
    ///         为指定工作区建立独立权限状态；授权登记表缺省内部新建，也可注入共享实例。
    ///     </para>
    ///     <para>
    ///         Creates isolated permission state for the workspace; the grant registry defaults to a private
    ///         instance and can be shared by injection.
    ///     </para>
    /// </summary>
    /// <param name="workspaceReadOnly">
    ///     <para>返回 true 时非只读能力一律硬拒绝，优先于所有授权。</para>
    ///     <para>When true, non-read-only capabilities are hard-denied ahead of every grant.</para>
    /// </param>
    /// <param name="sandboxNetworkStandingEnabled">
    ///     <para>
    ///         沙箱常设网络信任（可信用户设置的 Enabled 档）。缺省 false：每个网络申请都逐次审批；
    ///         true 时网络申请不再单独强制审批，随调用的文件分类路由。
    ///     </para>
    ///     <para>
    ///         Standing sandbox network trust (the trusted settings' Enabled tier). Default false: every network
    ///         request is approved per call; true stops network requests from forcing their own approval.
    ///     </para>
    /// </param>
    /// <param name="sandboxExecutionAvailable">
    ///     <para>
    ///         沙箱是否可用（组合期由执行侧暴露）。false 时所有 process.execute 一律拒绝（含宿主申请），
    ///         文件工具不受影响；fail closed，绝不回退宿主执行。
    ///     </para>
    ///     <para>
    ///         Whether the sandbox is available (exposed by composition). False denies every process.execute,
    ///         including host requests, while file tools keep working; fail closed, never falling back to host.
    ///     </para>
    /// </param>
    /// <param name="resourcePolicy">
    ///     <para>
    ///         执行侧注入的六值资源分类策略（<see cref="IResourceAccessPolicy" />）；缺省 null 时
    ///         所有目标按未分类 fail-closed 处理（一律审批）。
    ///     </para>
    ///     <para>
    ///         The six-value resource-classification policy injected by the execution side
    ///         (<see cref="IResourceAccessPolicy" />); when null every target fails closed to approval
    ///         as unclassified.
    ///     </para>
    /// </param>
    public PermissionEngine(string                                         workspaceRoot,
                            bool                                           denyNonReadOnlyCapabilities   = false,
                            Func<bool>?                                    workspaceReadOnly             = null,
                            bool                                           sandboxNetworkStandingEnabled = false,
                            bool                                           sandboxExecutionAvailable     = true,
                            IResourceAccessPolicy?                         resourcePolicy                = null,
                            GrantRegistry?                                 grantRegistry                 = null,
                            TimeProvider?                                  clock                         = null,
                            Func<ToolPreparation, FrozenCapabilityScope?>? frozenScopeReader             = null)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new ArgumentException("A workspace root is required.", nameof(workspaceRoot));

        _workspaceRoot               = Path.GetFullPath(workspaceRoot);
        _denyNonReadOnlyCapabilities = denyNonReadOnlyCapabilities;
        _workspaceReadOnly           = workspaceReadOnly;
        _networkStandingEnabled      = sandboxNetworkStandingEnabled;
        _sandboxExecutionAvailable   = sandboxExecutionAvailable;
        _resourcePolicy              = resourcePolicy ?? FailClosedResourcePolicy.Instance;
        Grants                       = grantRegistry  ?? new GrantRegistry(clock);
        _clock                       = clock          ?? TimeProvider.System;
        _frozenScopeReader           = frozenScopeReader;
    }

    internal long AuthorizationRevision => Interlocked.Read(ref _authorizationRevision);

    /// <summary>
    ///     <para>本引擎使用的授权登记表；宿主通过它撤销授权并读取授权事件。</para>
    ///     <para>The grant registry used by this engine; hosts revoke grants and read grant events through it.</para>
    /// </summary>
    public GrantRegistry Grants { get; }

    /// <summary>
    ///     <para>
    ///         当前 NeedsInspection 非只读阻断原因；未阻断时为 null。仅由宿主依据中断事实设置，
    ///         并在用户确认检查后清除；模型输出不能建立或解除阻断。
    ///     </para>
    ///     <para>
    ///         The current NeedsInspection reason blocking non-read-only capabilities; null when unblocked.
    ///         Only the host sets it from interruption facts and clears it after user inspection; model output
    ///         can neither create nor remove the block.
    ///     </para>
    /// </summary>
    public string? NonReadOnlyBlockReason { get; private set; }

    /// <summary>
    ///     <para>当前审批模式；缺省人工审批。切换模式撤销全部活动授权，拒绝记忆保留。</para>
    ///     <para>
    ///         The current approval mode; human approval by default. Switching modes revokes every active grant
    ///         while denial memory persists.
    ///     </para>
    /// </summary>
    public SessionApprovalMode ApprovalMode => SelectedApprovalMode ?? SessionApprovalMode.HumanApproval;

    /// <summary>
    ///     <para>
    ///         宿主显式选择的审批模式；null 表示尚未有任何选择（传统宿主或未迁移会话），
    ///         此时按人工审批执行且不向模型注入审批说明。
    ///     </para>
    ///     <para>
    ///         The approval mode explicitly selected by the host; null means no selection yet (legacy hosts or
    ///         unmigrated sessions), which runs as human approval without injecting the approval notice.
    ///     </para>
    /// </summary>
    public SessionApprovalMode? SelectedApprovalMode { get; private set; }

    /// <summary>
    ///     <para>在宿主空闲边界切换审批模式并撤销全部活动授权；硬限制与拒绝记忆仍然有效。</para>
    ///     <para>
    ///         Switches the approval mode at the host's idle boundary and revokes every active grant; hard
    ///         limits and denial memory persist.
    ///     </para>
    /// </summary>
    public void SetApprovalMode(SessionApprovalMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (SelectedApprovalMode == mode) return;
        RevokeGrants();
        SelectedApprovalMode = mode;
    }

    /// <summary>
    ///     <para>
    ///         只读地计算当前调用的权限结论；重复调用不消费授权、不改变会话状态。
    ///         授权阶段的消费走 <see cref="ConsumeGrant" />。
    ///     </para>
    ///     <para>
    ///         Read-only verdict for the invocation: repeated calls consume nothing and mutate nothing.
    ///         Authorization-time consumption goes through <see cref="ConsumeGrant" />.
    ///     </para>
    /// </summary>
    public PermissionEvaluation Evaluate(ToolPreparation preparation)
    {
        // 硬限制：worker 与未信任工作区优先于一切授权。
        if ((_denyNonReadOnlyCapabilities || _workspaceReadOnly?.Invoke() == true) &&
            !ToolCapabilities.IsReadOnly(preparation.Capability))
            return PermissionEvaluation.Deny("Non-read-only capabilities are hard-denied for this workspace.");

        // NeedsInspection：仅由宿主在用户确认检查后解除，任何授权不得绕过。
        if (NonReadOnlyBlock(preparation) is { } block)
            return PermissionEvaluation.Deny($"Blocked pending inspection of an interrupted run ({block}).");

        // 拒绝记忆：人审或 AI 拒绝过的同一操作不再弹卡、不再询问。
        if (IsDenied(preparation))
            return PermissionEvaluation.Deny($"Previously denied by the approver: {preparation.Summary}");

        var classification = Classify(preparation);
        if (classification.Denied is { } denied) return denied;
        if (classification.Level == PermissionLevel.Level1)
            return PermissionEvaluation.Allow(PermissionLevel.Level1, "Sandbox baseline operation.");

        // Level2/Level3 先尝试活动授权，再进入审批。
        if (Grants.Peek(preparation, _clock.GetUtcNow()) is { } grant)
            return PermissionEvaluation.Allow(classification.Level,
                                              $"Authorized by grant {grant.GrantId} ({grant.Scope}).");

        return PermissionEvaluation.Ask(classification.Level,
                                        classification.AskReason ??
                                        "Approval required for this operation.",
                                        classification.Missing);
    }

    /// <summary>
    ///     <para>
    ///         授权阶段的授权复用：原子匹配活动授权；SingleOperation 授权在匹配的同一锁内消费，
    ///         Turn 与 Session 授权原样返回。无匹配返回 null。
    ///     </para>
    ///     <para>
    ///         Grant reuse at authorization time: matches an active grant atomically; a SingleOperation grant
    ///         is consumed under the same lock that matched it, and Turn or Session grants are returned for
    ///         reuse. Returns null without a match.
    ///     </para>
    /// </summary>
    public Grant? ConsumeGrant(ToolPreparation preparation) => Grants.Consume(preparation, _clock.GetUtcNow());

    /// <summary>
    ///     <para>
    ///         复核硬限制、拒绝记忆、资源分类与当前活动授权；
    ///         任一失效即拒绝执行，调用方不得自动重放。
    ///     </para>
    ///     <para>
    ///         Rechecks hard limits, denial memory, resource classification and active grants;
    ///         any lapse denies execution without an automatic replay.
    ///     </para>
    /// </summary>
    public PermissionEvaluation Revalidate(ToolPreparation           preparation,
                                           ActiveCapabilitySnapshot? currentSnapshot = null) =>
        RevalidateCore(preparation, currentSnapshot, null);

    /// <summary>
    ///     <para>消费一次派发凭据并复核其计划、授权修订及活动授权。</para>
    ///     <para>Consumes one dispatch credential and rechecks its plan, authorization revision and active grant.</para>
    /// </summary>
    internal PermissionEvaluation RevalidateForDispatch(
        ToolPreparation preparation, ActiveCapabilitySnapshot? currentSnapshot, DispatchAuthorization? authorization)
    {
        if (authorization is null || !authorization.TryUse(this, preparation, AuthorizationRevision))
            return PermissionEvaluation.Deny("Authorization invalidated before dispatch; re-issue the call.");
        return RevalidateCore(preparation, currentSnapshot, authorization);
    }

    /// <summary>
    ///     <para>为已完成审批的冻结计划建立仅可派发一次的内部凭据。</para>
    ///     <para>Creates an internal, single-dispatch credential for an approved frozen plan.</para>
    /// </summary>
    internal DispatchAuthorization CreateDispatchAuthorization(
        ToolPreparation preparation, long revision, string? grantId = null, bool singleOperationApproved = false,
        DateTimeOffset? expiresUtc = null) =>
        new(this, preparation, revision, grantId, singleOperationApproved, expiresUtc);

    /// <summary>
    ///     <para>复核共同的安全限制、冻结范围以及基线或显式授权。</para>
    ///     <para>Rechecks shared safety restrictions, frozen scope, and baseline or explicit authority.</para>
    /// </summary>
    private PermissionEvaluation RevalidateCore(
        ToolPreparation preparation, ActiveCapabilitySnapshot? currentSnapshot, DispatchAuthorization? authorization)
    {
        // 授权后仍可能变化的安全状态：worker/只读工作区、NeedsInspection 与拒绝记忆。
        if ((_denyNonReadOnlyCapabilities || _workspaceReadOnly?.Invoke() == true) &&
            !ToolCapabilities.IsReadOnly(preparation.Capability))
            return PermissionEvaluation.Deny("Non-read-only capabilities are hard-denied for this workspace.");
        if (NonReadOnlyBlock(preparation) is { } block)
            return PermissionEvaluation.Deny($"Blocked pending inspection of an interrupted run ({block}).");
        if (IsDenied(preparation))
            return PermissionEvaluation.Deny($"Previously denied by the approver: {preparation.Summary}");

        var classification = Classify(preparation);
        if (classification.Denied is { } denied) return denied;

        // 授权凭据复核：匹配中的活动授权即存活（约束含策略身份与工作区修订，失效即不再匹配）。
        var grant = Grants.Peek(preparation, _clock.GetUtcNow());

        // 新增和撤销能力都使旧计划失效，防止冻结计划继续携带已经撤销的权限。
        if (currentSnapshot is { } current)
        {
            var frozen = _frozenScopeReader?.Invoke(preparation);
            if (frozen is null                                                       ||
                !IsSubset(current.AdditionalReadRoots, frozen.AdditionalReadRoots)   ||
                !IsSubset(current.AdditionalWriteRoots, frozen.AdditionalWriteRoots) ||
                !IsSubset(current.MetadataWritePaths, frozen.MetadataWritePaths)     ||
                !IsSubset(frozen.AdditionalReadRoots, current.AdditionalReadRoots)   ||
                !IsSubset(frozen.AdditionalWriteRoots, current.AdditionalWriteRoots) ||
                !IsSubset(frozen.MetadataWritePaths, current.MetadataWritePaths))
                return PermissionEvaluation.Deny(
                                                 "Authorization invalidated: capability scope changed; re-issue the call.",
                                                 classification.Level);
        }

        if (authorization?.GrantId is { } expectedGrantId && grant?.GrantId != expectedGrantId)
            return PermissionEvaluation.Deny("Authorization revoked or expired before dispatch.", classification.Level);
        if (classification.Level != PermissionLevel.Level1 &&
            grant is null                                  && authorization?.SingleOperationApproved != true)
            return PermissionEvaluation.Deny("No valid authorization remains for this operation.",
                                             classification.Level);

        return PermissionEvaluation.Allow(classification.Level,
                                          grant is { } active
                                              ? $"Revalidated against grant {active.GrantId}."
                                              : "Revalidated before dispatch.");
    }

    /// <summary>
    ///     <para>判断候选路径是否逐项包含在冻结范围内（序数比较，空集恒为子集）。</para>
    ///     <para>Whether every candidate path is contained in the frozen scope (ordinal; empty is always a subset).</para>
    /// </summary>
    private static bool IsSubset(IReadOnlyList<string> candidates, IReadOnlyList<string> frozen)
    {
        foreach (var candidate in candidates)
            if (!frozen.Contains(candidate, StringComparer.Ordinal))
                return false;
        return true;
    }

    /// <summary>
    ///     <para>
    ///         记录一次审批拒绝：绑定完整调用指纹与命令身份，使同一操作不再弹卡或询问。
    ///         指纹覆盖参数，命令约束跨执行后端有效；跨工具的语义等价不做声称。
    ///     </para>
    ///     <para>
    ///         Records one approval denial: bound to the full invocation fingerprint and the command identity so
    ///         the same operation never prompts again. The fingerprint covers arguments; the command constraint
    ///         survives backend switches; no cross-tool equivalence is claimed.
    ///     </para>
    /// </summary>
    public void RecordDenial(ToolPreparation preparation)
    {
        _deniedFingerprints.Add(Fingerprint(preparation));
        _deniedRules.Add(new PermissionRule(preparation.Capability, preparation.TargetPaths.ToArray(),
                                            preparation.SessionConstraint));
    }

    /// <summary>
    ///     <para>设置或清除非只读阻断，任何授权或命令规则均不得绕过。</para>
    ///     <para>Sets or clears non-read-only blocking, which no grant or command rule may bypass.</para>
    /// </summary>
    /// <param name="reason">
    ///     <para>null 或空白表示清除；宿主仅在用户明确确认检查后清除。</para>
    ///     <para>Null or whitespace clears; the host clears only after explicit user inspection confirmation.</para>
    /// </param>
    public void SetNonReadOnlyBlock(string? reason) =>
        NonReadOnlyBlockReason = string.IsNullOrWhiteSpace(reason) ? null : reason;

    /// <summary>
    ///     <para>返回本次调用被 NeedsInspection 阻断的原因；未被阻断时为 null。</para>
    ///     <para>The NeedsInspection reason applying to this invocation, or null when unblocked.</para>
    /// </summary>
    public string? NonReadOnlyBlock(ToolPreparation preparation) =>
        NonReadOnlyBlockReason is null || ToolCapabilities.IsReadOnly(preparation.Capability)
            ? null
            : NonReadOnlyBlockReason;

    /// <summary>
    ///     <para>撤销全部活动授权；拒绝记忆与 NeedsInspection 阻断保留。</para>
    ///     <para>Revokes every active grant; denial memory and the NeedsInspection block persist.</para>
    /// </summary>
    public void RevokeGrants()
    {
        Interlocked.Increment(ref _authorizationRevision);
        Grants.RevokeAll();
    }

    /// <summary>
    ///     <para>把全部活动 Turn 授权标记为到期（运行终态调用）；Session 授权与拒绝记忆保留。</para>
    ///     <para>
    ///         Marks every active Turn grant expired (called at run end); Session grants and denial memory
    ///         persist.
    ///     </para>
    /// </summary>
    public void ExpireTurnGrants() => Grants.ExpireTurnGrants();

    /// <summary>
    ///     <para>判断调用是否命中拒绝记忆：指纹精确命中或拒绝规则按命令身份与范围命中。</para>
    ///     <para>
    ///         Whether the invocation hits denial memory: an exact fingerprint hit or a denial rule matched by
    ///         command identity and scope.
    ///     </para>
    /// </summary>
    private bool IsDenied(ToolPreparation preparation) =>
        _deniedFingerprints.Contains(Fingerprint(preparation)) ||
        _deniedRules.Any(rule => rule.Matches(preparation, true));

    /// <summary>分类一次冻结调用：拒绝结论、执行级别、缺失能力与审批原因。</summary>
    private (PermissionEvaluation? Denied, PermissionLevel Level, string? AskReason,
        IReadOnlyList<string> Missing) Classify(ToolPreparation preparation)
    {
        if (preparation.Capability == "process.execute")
        {
            // 沙箱不可用即整条命令拒绝，宿主申请同样拒绝，绝不回退宿主执行；不可用不是缺失能力，不入 Missing。
            if (!_sandboxExecutionAvailable)
                return (PermissionEvaluation.Deny("Sandbox unavailable; process execution is fail-closed."),
                        PermissionLevel.None, null, Array.Empty<string>());

            // Host 判定只看冻结计划上的显式后端申请；执行策略串仅作身份与授权绑定。
            if (preparation.RequestedBackend == ProcessExecutionBackendKind.Host)
                return (null, PermissionLevel.Level3,
                        "Host execution has no OS isolation; approve this single operation to run it.",
                        HostExecutionMissing);

            if (preparation.RequiresNetworkAccess && !_networkStandingEnabled)
                return (null, PermissionLevel.Level2,
                        "The command requests sandbox network access (full network through the user's proxy); " +
                        "approve this operation to run it.",
                        NetworkAccessMissing);

            return (null, PermissionLevel.Level1, null, Array.Empty<string>());
        }

        // 未知能力 fail closed：一律审批，不按文件路径分类放行。
        if (!KnownCapabilities.Contains(preparation.Capability))
            return (null, PermissionLevel.None,
                    $"Unknown capability '{preparation.Capability}'; approval is required.",
                    [preparation.Capability]);

        // 非只读能力未声明任何目标：无法验证范围，fail closed 一律审批。
        if (preparation.TargetPaths.Count == 0 && !ToolCapabilities.IsReadOnly(preparation.Capability))
            return (null, PermissionLevel.None,
                    "The invocation declares no resource targets; approval is required.",
                    [preparation.Capability]);

        var level     = PermissionLevel.Level1;
        var missing   = new List<string>();
        var askReason = (string?)null;
        var readClass = ToolCapabilities.IsReadOnly(preparation.Capability);
        foreach (var target in preparation.TargetPaths)
        {
            // 未分类（策略未注入或返回 null）按需审批 fail-closed。
            var classification = _resourcePolicy.Classify(preparation.Capability, target);
            if (classification is null)
            {
                level     = PermissionLevel.None;
                askReason = "The invocation targets an unclassified resource; approval is required.";
                continue;
            }

            switch (classification.Class)
            {
                // 系统保护路径一律硬拒；执行侧 Prepare 已先行硬拒，这里兜底。
                case ResourceClass.PermissionSystemProtected :
                    return (PermissionEvaluation.Deny(
                                                      $"The invocation touches a permission-system-protected resource: {target}"),
                            PermissionLevel.None, null, Array.Empty<string>());
                case ResourceClass.OutsideWorkspace :
                    level = PermissionLevel.Level2;
                    // 契约 D2：读与写分开申请与展示，审批文本不得把读显示为写。
                    askReason = readClass
                        ? "The invocation reads outside the workspace; approval is required."
                        : "The invocation writes outside the workspace; approval is required.";
                    missing.Add(readClass ? AdditionalPathRead : AdditionalPathWrite);
                    break;
                case ResourceClass.ProtectedMetadata :
                    // 元数据读取（如 git status 的 .git 访问）属基线只读；写入升为 Level2。
                    if (!readClass)
                    {
                        level     = PermissionLevel.Level2;
                        askReason = "The invocation writes protected metadata; approval is required.";
                        missing.Add(MetadataWrite);
                    }

                    break;
                case ResourceClass.AdditionalReadRoot when !readClass :
                    // 附加只读根不接纳写入；写入按附加能力审批。
                    level     = PermissionLevel.Level2;
                    askReason = "The invocation writes an additional read root; approval is required.";
                    missing.Add(AdditionalPathWrite);
                    break;
                case ResourceClass.BaselineWorkspace :
                case ResourceClass.AdditionalReadRoot :
                case ResourceClass.AdditionalWriteRoot :
                default :
                    break;
            }

            if (classification.KnownSensitive && askReason is not null)
                askReason += " (known sensitive location)";
        }

        if (preparation.RequiresNetworkAccess && !_networkStandingEnabled)
        {
            level = PermissionLevel.Level2;
            missing.Add("network-access");
            askReason = "The invocation requests sandbox network access; approval is required.";
        }

        return (null, level, askReason, missing.Distinct(StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    ///     <para>对完整准备调用生成稳定 SHA-256 指纹，供拒绝记忆精确匹配。</para>
    ///     <para>Generates a stable SHA-256 fingerprint of the complete invocation for exact denial matching.</para>
    /// </summary>
    private static string Fingerprint(ToolPreparation preparation)
    {
        var canonical =
            preparation.ToolName                                                               + '\n' +
            preparation.Capability                                                             + '\n' +
            string.Join('\n', preparation.TargetPaths.OrderBy(x => x, StringComparer.Ordinal)) + '\n' +
            (preparation.SessionGrantConstraint ?? preparation.SessionConstraint)              + '\n' +
            preparation.Arguments.ToJsonString();

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>
    ///     <para>绑定当前引擎、冻结计划及授权修订的一次性派发凭据。</para>
    ///     <para>A single-dispatch credential bound to one engine, frozen plan and authorization revision.</para>
    /// </summary>
    internal sealed class DispatchAuthorization(
        PermissionEngine engine,
        ToolPreparation  preparation,
        long             revision,
        string?          grantId,
        bool             singleOperationApproved,
        DateTimeOffset?  expiresUtc)
    {
        private  int     _used;
        internal string? GrantId                 { get; } = grantId;
        internal bool    SingleOperationApproved { get; } = singleOperationApproved;

        internal bool TryUse(PermissionEngine currentEngine, ToolPreparation currentPreparation,
                             long currentRevision) =>
            ReferenceEquals(engine, currentEngine) && ReferenceEquals(preparation, currentPreparation) &&
            revision == currentRevision && (expiresUtc is null || expiresUtc > engine._clock.GetUtcNow()) &&
            Interlocked.Exchange(ref _used, 1) == 0;
    }

    /// <summary>
    ///     <para>未注入分类策略时的 fail-closed 空实现：所有目标一律视为未分类，走审批。</para>
    ///     <para>
    ///         The fail-closed stand-in used without an injected policy: every target counts as unclassified and
    ///         requires approval.
    ///     </para>
    /// </summary>
    private sealed class FailClosedResourcePolicy : IResourceAccessPolicy
    {
        public static readonly FailClosedResourcePolicy Instance = new();

        public ResourceClassification? Classify(string capability, string targetPath) => null;
    }
}
