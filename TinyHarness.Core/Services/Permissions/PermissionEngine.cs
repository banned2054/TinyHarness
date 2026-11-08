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
///         资源分类（沙箱不可用拒绝、宿主申请 Level3、网络申请 Level2——仅"常设网络已启用且常设信任
///         Confirmed"时随显式持久授权放行、shell 写根申请先做 C1 双向重叠硬拒再按 Level2 逐次审批、
///         已知敏感位置读写与越界写、未确认附加写根写入、受保护元数据写入 Level2，其余文件读写属
///         基线 Level1）、基线放行、活动授权、审批。分类与级别是审核维度，不是授权来源；授权只来自
///         Level1 基线或 <see cref="GrantRegistry" /> 中的活动 Grant。这是应用层策略，不是 OS 沙箱；
///         运行时另行强制文件系统边界。
///     </para>
///     <para>
///         The evaluation order is fixed: worker and untrusted-workspace hard limits, NeedsInspection blocking,
///         system-protected resources, denial memory, resource classification (sandbox-unavailable denial, host
///         request Level3, network request Level2 — released with the explicit persistent authorization only
///         when the standing network is enabled and standing trust is Confirmed, shell write-root requests
///         hard-denied on C1 bidirectional overlap first and otherwise Level2 per call, known-sensitive reads
///         and writes, outside-workspace writes, unconfirmed additional-write-root writes, and
///         protected-metadata writes at Level2; every other file read or write at the Level1 baseline),
///         baseline allowance, active grants, approval. Levels are review dimensions, never sources of
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
    private readonly IResourceAccessPolicy                          _resourcePolicy;
    private readonly bool                                           _sandboxExecutionAvailable;
    private readonly bool                                           _standingNetworkEnabled;
    private readonly StandingTrustState                             _standingTrust;
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
    /// <param name="standingTrust">
    ///     <para>
    ///         持久信任三态（组合期由可信设置与显式确认记录评估）。缺省
    ///         <see cref="StandingTrustState.None" />：<see cref="StandingTrustState.None" /> 与
    ///         <see cref="StandingTrustState.Unconfirmed" /> 下联网申请一律逐次审批，附加写根写入
    ///         由资源分类降档逐次审批；仅 <see cref="StandingTrustState.Confirmed" /> 把
    ///         networkPolicy=enabled 与附加写根作为显式持久授权消费。
    ///     </para>
    ///     <para>
    ///         The three-state standing trust (evaluated at composition from the trusted settings and the
    ///         explicit confirmation record). The default <see cref="StandingTrustState.None" />: under both
    ///         <see cref="StandingTrustState.None" /> and <see cref="StandingTrustState.Unconfirmed" /> every
    ///         network request is approved per call and additional write-root writes are demoted to per-call
    ///         approval by the resource classification; only <see cref="StandingTrustState.Confirmed" />
    ///         consumes networkPolicy=enabled and the additional write roots as explicit persistent
    ///         authorization.
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
    /// <param name="standingNetworkEnabled">
    ///     <para>
    ///         组合实际消费的常设网络是否启用（与传给沙箱组合器的 EffectiveSandboxSettings 同源同值：
    ///         其 NetworkPolicy 为 enabled）。false 时网络申请一律逐次审批，写根确认状态不得复用为
    ///         网络免审批；true 时仅 <see cref="StandingTrustState.Confirmed" /> 免审批。
    ///     </para>
    ///     <para>
    ///         Whether the standing network is enabled in the settings the composition actually consumes
    ///         (same source and value as the EffectiveSandboxSettings handed to the sandbox composer: its
    ///         NetworkPolicy is enabled). When false, every network request follows per-call approval and
    ///         the write-root confirmation state is never reused as a network exemption; when true, only
    ///         <see cref="StandingTrustState.Confirmed" /> exempts the approval.
    ///     </para>
    /// </param>
    /// <param name="resourcePolicy">
    ///     <para>
    ///         执行侧注入的资源分类策略（<see cref="IResourceAccessPolicy" />）；缺省 null 时
    ///         所有目标按未分类 fail-closed 处理（一律审批）。
    ///     </para>
    ///     <para>
    ///         The resource-classification policy injected by the execution side
    ///         (<see cref="IResourceAccessPolicy" />); when null every target fails closed to approval
    ///         as unclassified.
    ///     </para>
    /// </param>
    public PermissionEngine(string                                         workspaceRoot,
                            bool                                           denyNonReadOnlyCapabilities = false,
                            Func<bool>?                                    workspaceReadOnly = null,
                            StandingTrustState                             standingTrust = StandingTrustState.None,
                            bool                                           sandboxExecutionAvailable = true,
                            bool                                           standingNetworkEnabled = false,
                            IResourceAccessPolicy?                         resourcePolicy = null,
                            GrantRegistry?                                 grantRegistry = null,
                            TimeProvider?                                  clock = null,
                            Func<ToolPreparation, FrozenCapabilityScope?>? frozenScopeReader = null)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new ArgumentException("A workspace root is required.", nameof(workspaceRoot));

        _workspaceRoot               = Path.GetFullPath(workspaceRoot);
        _denyNonReadOnlyCapabilities = denyNonReadOnlyCapabilities;
        _workspaceReadOnly           = workspaceReadOnly;
        _standingTrust               = standingTrust;
        _sandboxExecutionAvailable   = sandboxExecutionAvailable;
        _standingNetworkEnabled      = standingNetworkEnabled;
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
        // 单次调用显式申请的写根由本次批准（单次批准或绑定 GrantId 的 Turn 授权）背书，
        // 不参与快照存活子集复核；grant 来源的根仍要求双向一致（撤销或收缩即失效）。
        if (currentSnapshot is { } current)
        {
            var frozen               = _frozenScopeReader?.Invoke(preparation);
            var standaloneWriteRoots = preparation.RequestedWriteRoots;
            var currentWrites        = ExceptRoots(current.AdditionalWriteRoots, standaloneWriteRoots);
            var frozenWrites         = ExceptRoots(frozen?.AdditionalWriteRoots ?? [], standaloneWriteRoots);
            if (frozen is null                                                     ||
                !IsSubset(current.AdditionalReadRoots, frozen.AdditionalReadRoots) ||
                !IsSubset(currentWrites, frozenWrites)                             ||
                !IsSubset(current.MetadataWritePaths, frozen.MetadataWritePaths)   ||
                !IsSubset(frozen.AdditionalReadRoots, current.AdditionalReadRoots) ||
                !IsSubset(frozenWrites, currentWrites)                             ||
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
    ///     <para>从根列表剔除单次调用自带的写根（序数比较），供快照存活复核只覆盖 grant 来源的根。</para>
    ///     <para>
    ///         Removes the invocation's own requested write roots from a root list (ordinal), so the snapshot
    ///         liveness check covers only grant-sourced roots.
    ///     </para>
    /// </summary>
    private static IReadOnlyList<string> ExceptRoots(IReadOnlyList<string> roots, IReadOnlyList<string> excluded)
    {
        if (excluded.Count == 0) return roots;
        if (roots.Count    == 0) return roots;

        return roots.Where(root => !excluded.Contains(root, StringComparer.Ordinal)).ToArray();
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
    ///     <para>
    ///         可信授权变更点调用：用户批准同一绑定操作后精确解除其拒绝记忆（指纹与命令身份规则
    ///         各自移除）。任意新消息不得走此通道；宿主在解除发生时写入 authorization-changed 审计。
    ///     </para>
    ///     <para>
    ///         Called at trusted authorization changes: after the user approves the same bound operation, its
    ///         denial memory is released precisely (fingerprint and command-identity rule removed separately).
    ///         Any new message can never take this path; the host writes the authorization-changed audit when a
    ///         release happens.
    ///     </para>
    /// </summary>
    /// <returns>
    ///     <para>是否解除了至少一条拒绝记忆。Whether at least one denial memory entry was released.</para>
    /// </returns>
    public bool ReleaseDenialForApproval(ToolPreparation preparation)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        var fingerprint = Fingerprint(preparation);
        var released    = _deniedFingerprints.Remove(fingerprint);
        // 命令身份规则与指纹一并解除：同一冻结操作的规则匹配等价于指纹命中。
        released |= _deniedRules.RemoveAll(rule => rule.Matches(preparation, true)) > 0;
        return released;
    }

    /// <summary>
    ///     <para>
    ///         可信授权变更点调用（策略身份修订变更、迁移确认等整段授权语义变化）：全量解除拒绝记忆。
    ///         活动授权的撤销语义由 <see cref="RevokeGrants" /> 承担；宿主负责 authorization-changed 审计。
    ///     </para>
    ///     <para>
    ///         Called at trusted authorization changes (a policy-identity revision change or migration
    ///         confirmation — a wholesale change of authorization semantics): releases every denial memory.
    ///         Revoking active grants stays with <see cref="RevokeGrants" />; the host owns the
    ///         authorization-changed audit.
    ///     </para>
    /// </summary>
    /// <returns>
    ///     <para>解除的拒绝记忆条数。The number of released denial-memory entries.</para>
    /// </returns>
    public int ReleaseAllDenials()
    {
        var released = _deniedFingerprints.Count + _deniedRules.Count;
        _deniedFingerprints.Clear();
        _deniedRules.Clear();
        return released;
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

            // 写根申请与网络申请都可能把命令升到 Level2；逐项累积缺失能力与文案后一次返回。
            var processLevel   = PermissionLevel.Level1;
            var processMissing = new List<string>();
            var processReasons = new List<string>(2);

            // shell 写根申请：模型显式申请的工作区外写范围恒按 Level 2 逐次审批（常设信任确认
            // 只覆盖可信设置中的持久根，不覆盖单次调用申请）；C1 权限系统保护路径不可经批准开放。
            var requestedRoots = preparation.RequestedWriteRoots;
            if (requestedRoots.Count > 0)
            {
                var sensitiveRoots = new List<string>();
                foreach (var root in requestedRoots)
                {
                    // C1 双向重叠：申请根落入权限系统保护路径或覆盖任一保护根都硬拒——
                    // 单向分类会让保护对象的父目录申请根进入审批并经批准进入真实写范围。
                    if (_resourcePolicy.OverlapsPermissionSystemRoots(root))
                        return (PermissionEvaluation.Deny(
                                                          "The requested write root '"                            + root +
                                                          "' falls inside or covers permission-system-protected " +
                                                          "resources; it cannot be authorized."),
                                PermissionLevel.None, null, Array.Empty<string>());

                    var rootClass = _resourcePolicy.Classify("filesystem.write", root);
                    if (rootClass is { Class: ResourceClass.PermissionSystemProtected })
                        return (PermissionEvaluation.Deny(
                                                          "The requested write root '"                         + root +
                                                          "' touches a permission-system-protected resource; " +
                                                          "it cannot be authorized."),
                                PermissionLevel.None, null, Array.Empty<string>());
                    if (rootClass?.KnownSensitive == true) sensitiveRoots.Add(root);
                }

                processLevel = PermissionLevel.Level2;
                processMissing.Add(AdditionalPathWrite);
                processReasons.Add("The command requests write access outside the workspace ("                +
                                   string.Join("; ", requestedRoots)                                          +
                                   "); approve this operation to run it"                                      +
                                   (sensitiveRoots.Count > 0 ? " (known sensitive location)" : string.Empty)  +
                                   ". Approval defaults to this single operation; a turn grant stays inside " +
                                   "this run only.");
            }

            // 常设网络免审批与写根确认独立判定：仅"常设网络已启用 + Confirmed"免审批；常设网络
            // 未启用（restricted）时即便写根已确认也逐次审批，文案如实说明 restricted，不复用
            // 未确认措辞。None 与 Unconfirmed 均逐次审批，Unconfirmed 的审批文案如实注明。
            if (preparation.RequiresNetworkAccess &&
                (!_standingNetworkEnabled || _standingTrust is not StandingTrustState.Confirmed))
            {
                processLevel = PermissionLevel.Level2;
                processMissing.Add(NetworkAccessMissing[0]);
                processReasons.Add(BuildNetworkAskReason(commandForm : true));
            }

            if (processLevel != PermissionLevel.Level1)
                return (null, processLevel, string.Join(" ", processReasons),
                        processMissing.Distinct(StringComparer.Ordinal).ToArray());

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
                case ResourceClass.OutsideWorkspace when !readClass :
                    // 普通越界读取属 L1 广泛读基线（不因越界升层）；只有越界写入需要附加写能力审批。
                    // 读与写分开申请与展示，审批文本不得把读显示为写。
                    level     = PermissionLevel.Level2;
                    askReason = "The invocation writes outside the workspace; approval is required.";
                    missing.Add(AdditionalPathWrite);
                    break;
                case ResourceClass.SensitiveLocation :
                    // C2 读保护档：已知敏感位置的读与写在广泛读基线下仍按 Level2 逐次审批；
                    // 能力沿用附加读/写，敏感来源由 KnownSensitive 注记在审批文本如实标注。
                    level = PermissionLevel.Level2;
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
                case ResourceClass.UnconfirmedWriteRoot :
                    // 常设信任未确认：附加写根不在 OS 基线内，写入逐次审批并经 grant 通道临时生效；
                    // 文案注明未确认状态，读方向不受影响（分类层已按读维持基线档）。
                    level = PermissionLevel.Level2;
                    askReason = "Standing trust is unconfirmed; this additional-write-root write requires " +
                                "per-call approval.";
                    missing.Add(AdditionalPathWrite);
                    break;
                case ResourceClass.BaselineWorkspace :
                case ResourceClass.AdditionalReadRoot :
                case ResourceClass.AdditionalWriteRoot :
                case ResourceClass.SandboxRuntimeRoot :
                default :
                    break;
            }

            if (classification.KnownSensitive && askReason is not null)
                askReason += " (known sensitive location)";
        }

        // 常设网络免审批与写根确认独立判定：仅"常设网络已启用 + Confirmed"免审批；常设网络
        // 未启用（restricted）时即便写根已确认也逐次审批，文案如实说明 restricted，不复用
        // 未确认措辞。None 与 Unconfirmed 均逐次审批，Unconfirmed 的审批文案如实注明。
        if (preparation.RequiresNetworkAccess &&
            (!_standingNetworkEnabled || _standingTrust is not StandingTrustState.Confirmed))
        {
            level     = PermissionLevel.Level2;
            missing.Add("network-access");
            askReason = BuildNetworkAskReason(commandForm : false);
        }

        return (null, level, askReason, missing.Distinct(StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    ///     <para>
    ///         构造网络申请的审批文案：常设网络未启用（restricted）时如实说明逐次审批来自受限
    ///         策略本身，不复用"常设信任未确认"措辞；已启用且 Unconfirmed 时注明未确认状态，
    ///         其余为普通逐次审批文案。
    ///     </para>
    ///     <para>
    ///         Builds the approval reason for a network request: with the standing network not enabled
    ///         (restricted) the text states honestly that per-call approval follows the restricted policy
    ///         itself and never reuses the "standing trust is unconfirmed" wording; when enabled and
    ///         Unconfirmed the text notes the unconfirmed state, and everything else keeps the plain
    ///         per-call approval text.
    ///     </para>
    /// </summary>
    private string BuildNetworkAskReason(bool commandForm) =>
        !_standingNetworkEnabled
            ? commandForm
                ? "The command requests sandbox network access, but standing network access is not "   +
                  "enabled (networkPolicy=restricted); this call follows per-call approval."
                : "The invocation requests sandbox network access, but standing network access is not " +
                  "enabled (networkPolicy=restricted); this call follows per-call approval."
            : _standingTrust == StandingTrustState.Unconfirmed
                ? commandForm
                    ? "The command requests sandbox network access (full network through the " +
                      "user's proxy); standing trust is unconfirmed, so this call follows "    +
                      "per-call approval."
                    : "The invocation requests sandbox network access; standing trust is unconfirmed, " +
                      "so this call follows per-call approval."
                : commandForm
                    ? "The command requests sandbox network access (full network through the " +
                      "user's proxy); approve this operation to run it."
                    : "The invocation requests sandbox network access; approval is required.";

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

        // 无保护根信息时不声称重叠：申请根按普通越界档走 Level2 逐次审批（与未分类方向一致）。
        public bool OverlapsPermissionSystemRoots(string path) => false;
    }
}
