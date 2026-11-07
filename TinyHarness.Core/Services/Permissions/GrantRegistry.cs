using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Runtime;

namespace TinyHarness.Core.Services.Permissions;

/// <summary>
///     <para>
///         每会话一个的显式授权登记表：记录审批建立的 Grant，提供匹配、原子消费与撤销。
///         所有授权只存活于本实例（即本会话、本进程），不持久化为可执行权限，不跨重启恢复。
///     </para>
///     <para>
///         The per-session registry of explicit grants: it records approval-established grants and provides
///         matching, atomic consumption and revocation. All grants live only in this instance (the session,
///         the process), are never persisted as executable authority and never survive a restart.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         匹配要求能力、授权约束、目标覆盖与执行策略身份全部一致，且授权处于活动状态未过期。
///         SingleOperation 授权的匹配加消费在单把锁内完成，并发请求不能共用一次授权。
///     </para>
///     <para>
///         Matching requires the capability, grant constraint, target coverage and execution-policy identity to
///         all agree while the grant is active and unexpired. Matching plus consumption of a SingleOperation
///         grant completes under one lock, so concurrent requests can never share one grant.
///     </para>
/// </remarks>
public sealed class GrantRegistry : IActiveCapabilitySource
{
    private readonly TimeProvider     _clock;
    private readonly List<GrantEvent> _events = [];
    private readonly List<Grant>      _grants = [];
    private readonly object           _sync   = new();

    /// <summary>
    ///     <para>建立授权登记表；时钟仅供测试注入，缺省使用系统时间。</para>
    ///     <para>Creates the registry; the clock exists for tests and defaults to system time.</para>
    /// </summary>
    public GrantRegistry(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         按授权填补的能力缺口聚合：additional-path-read 的授权目标进入附加读根，
    ///         additional-path-write 进入附加写根，metadata-write 进入受保护元数据写路径
    ///         （供执行侧收缩 DenyWritePaths）。host-execution、network-access 与基线能力授权
    ///         不聚合——它们不扩大文件系统范围。廉价纯查询；快照按调用时状态返回，不缓存。
    ///     </para>
    ///     <para>
    ///         Aggregated by the capability gap each grant fills: additional-path-read grant targets become
    ///         additional read roots, additional-path-write becomes additional write roots, and metadata-write
    ///         becomes protected-metadata write paths (for the execution side to narrow DenyWritePaths).
    ///         host-execution, network-access and baseline grants never aggregate — they widen no filesystem
    ///         scope. Cheap and pure; the snapshot reflects the state at call time and is never cached.
    ///     </para>
    /// </remarks>
    public ActiveCapabilitySnapshot Snapshot()
    {
        lock (_sync)
        {
            var now        = _clock.GetUtcNow();
            var readRoots  = new List<string>();
            var writeRoots = new List<string>();
            var metadata   = new List<string>();
            foreach (var grant in _grants)
            {
                if (grant.State != GrantState.Active || IsExpired(grant, now)) continue;
                var targets = grant.MissingCapability switch
                {
                    "additional-path-read"  => readRoots,
                    "additional-path-write" => writeRoots,
                    "metadata-write"        => metadata,
                    _                       => null // host-execution / network-access / 基线：不聚合。
                };
                if (targets is null) continue;
                targets.AddRange(grant.Targets);
            }

            return new ActiveCapabilitySnapshot(
                                                readRoots.Distinct(StringComparer.Ordinal).ToArray(),
                                                writeRoots.Distinct(StringComparer.Ordinal).ToArray(),
                                                metadata.Distinct(StringComparer.Ordinal).ToArray());
        }
    }

    /// <summary>
    ///     <para>登记一条活动授权；同能力、同约束、同策略身份且同范围的旧活动授权被替代。</para>
    ///     <para>
    ///         Records an active grant; an older active grant with the same capability, constraint, policy
    ///         identity and scope is superseded.
    ///     </para>
    /// </summary>
    public Grant Add(Grant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        if (grant.State != GrantState.Active)
            throw new ArgumentException("Only active grants can be registered.", nameof(grant));

        lock (_sync)
        {
            var now = _clock.GetUtcNow();
            for (var i = 0; i < _grants.Count; i++)
            {
                var existing = _grants[i];
                if (existing.State != GrantState.Active || IsExpired(existing, now)) continue;
                if (existing.Scope != grant.Scope                                                   ||
                    !string.Equals(existing.Capability, grant.Capability, StringComparison.Ordinal) ||
                    !string.Equals(existing.Constraint, grant.Constraint, StringComparison.Ordinal) ||
                    !string.Equals(existing.PolicyIdentity, grant.PolicyIdentity, StringComparison.Ordinal))
                    continue;

                TransitionLocked(i, GrantState.Superseded, "superseded", now);
            }

            _grants.Add(grant);
            _events.Add(new GrantEvent(grant, GrantState.Active, "granted", now));
            return grant;
        }
    }

    /// <summary>
    ///     <para>从已冻结计划建立并登记活动授权；约束取授权约束，缺省回落到命令约束。</para>
    ///     <para>
    ///         Creates and records an active grant from a frozen plan; the constraint prefers the grant
    ///         constraint and falls back to the bare command constraint.
    ///     </para>
    /// </summary>
    public Grant Add(GrantScope scope, ToolPreparation preparation, TimeSpan? lifetime = null) =>
        Add(Grant.Active(NewGrantId(), scope, preparation, _clock.GetUtcNow(), lifetime));

    /// <summary>
    ///     <para>
    ///         从已冻结计划建立并登记活动授权，并记录该授权填补的能力缺口（引擎求值推导），
    ///         供活动快照按缺口聚合。
    ///     </para>
    ///     <para>
    ///         Creates and records an active grant from a frozen plan, recording the capability gap the grant
    ///         fills (derived by engine evaluation) for gap-based snapshot aggregation.
    ///     </para>
    /// </summary>
    public Grant Add(GrantScope scope, ToolPreparation preparation, string? missingCapability,
                     TimeSpan?  lifetime = null) =>
        Add(new Grant(NewGrantId(), scope, preparation.Capability,
                      preparation.SessionGrantConstraint ?? preparation.SessionConstraint,
                      preparation.TargetPaths, preparation.ExecutionPolicy,
                      _clock.GetUtcNow(), lifetime is { } span ? _clock.GetUtcNow() + span : null,
                      GrantState.Active, missingCapability));

    /// <summary>
    ///     <para>按序号生成新的授权标识。</para>
    ///     <para>Generates a fresh grant identifier by sequence.</para>
    /// </summary>
    private static string NewGrantId() => $"grant-{Guid.NewGuid():N}";

    /// <summary>
    ///     <para>只读查找第一个与冻结计划匹配的活动授权；不改变任何状态。</para>
    ///     <para>Read-only lookup of the first active grant matching the frozen plan; mutates nothing.</para>
    /// </summary>
    public Grant? Peek(ToolPreparation preparation, DateTimeOffset? now = null)
    {
        lock (_sync)
        {
            var index = FindLocked(preparation, now ?? _clock.GetUtcNow());
            return index < 0 ? null : _grants[index];
        }
    }

    /// <summary>
    ///     <para>
    ///         匹配并按需消费：SingleOperation 授权在同一锁内完成匹配与消费，返回已消费的授权；
    ///         Turn 与 Session 授权原样返回，可继续复用。无匹配时返回 null 且不改变状态。
    ///     </para>
    ///     <para>
    ///         Matches and consumes as needed: a SingleOperation grant is matched and consumed under one lock
    ///         and returned as spent; Turn and Session grants are returned as-is for reuse. Without a match it
    ///         returns null and changes nothing.
    ///     </para>
    /// </summary>
    public Grant? Consume(ToolPreparation preparation, DateTimeOffset? now = null)
    {
        lock (_sync)
        {
            var at    = now ?? _clock.GetUtcNow();
            var index = FindLocked(preparation, at);
            if (index < 0) return null;
            if (_grants[index].Scope == GrantScope.SingleOperation)
                TransitionLocked(index, GrantState.Consumed, "consumed", at);
            return _grants[index];
        }
    }

    /// <summary>
    ///     <para>按标识撤销授权；不存在或已离开活动状态时返回 null。</para>
    ///     <para>Revokes a grant by id; returns null when absent or already inactive.</para>
    /// </summary>
    public Grant? Revoke(string grantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(grantId);
        lock (_sync)
        {
            var now = _clock.GetUtcNow();
            var index = _grants.FindIndex(candidate =>
                                              string.Equals(candidate.GrantId, grantId, StringComparison.Ordinal) &&
                                              candidate.State == GrantState.Active && !IsExpired(candidate, now));
            if (index < 0) return null;
            TransitionLocked(index, GrantState.Revoked, "revoked", now);
            return _grants[index];
        }
    }

    /// <summary>
    ///     <para>撤销全部活动授权（模式切换、信任变化、补充生效与策略变化调用）；拒绝记忆不受影响。</para>
    ///     <para>
    ///         Revokes every active grant (mode switches, trust changes, applied amendments and policy changes);
    ///         denial memory is unaffected.
    ///     </para>
    /// </summary>
    public IReadOnlyList<Grant> RevokeAll()
    {
        lock (_sync)
        {
            var now     = _clock.GetUtcNow();
            var revoked = new List<Grant>();
            for (var i = 0; i < _grants.Count; i++)
            {
                if (_grants[i].State != GrantState.Active || IsExpired(_grants[i], now)) continue;
                TransitionLocked(i, GrantState.Revoked, "revoked", now);
                revoked.Add(_grants[i]);
            }

            return revoked;
        }
    }

    /// <summary>
    ///     <para>把全部活动 Turn 授权标记为到期（运行终态调用）；Session 授权保留。</para>
    ///     <para>Marks every active Turn grant expired (called at run end); Session grants survive.</para>
    /// </summary>
    public IReadOnlyList<Grant> ExpireTurnGrants()
    {
        lock (_sync)
        {
            var now     = _clock.GetUtcNow();
            var expired = new List<Grant>();
            for (var i = 0; i < _grants.Count; i++)
            {
                if (_grants[i].State != GrantState.Active || IsExpired(_grants[i], now)) continue;
                if (_grants[i].Scope != GrantScope.Turn) continue;
                TransitionLocked(i, GrantState.Expired, "expired", now);
                expired.Add(_grants[i]);
            }

            return expired;
        }
    }

    /// <summary>
    ///     <para>返回全部授权（含已结束）的快照；供审计与诊断读取。</para>
    ///     <para>Returns a snapshot of all grants, including finished ones, for audit and diagnostics.</para>
    /// </summary>
    public IReadOnlyList<Grant> SnapshotGrants()
    {
        lock (_sync)
        {
            return _grants.ToArray();
        }
    }

    /// <summary>
    ///     <para>取出并清空授权事件队列；事件按发生顺序排列。</para>
    ///     <para>Drains the grant-event queue; events arrive in occurrence order.</para>
    /// </summary>
    public IReadOnlyList<GrantEvent> DrainEvents()
    {
        lock (_sync)
        {
            var drained = new List<GrantEvent>(_events);
            _events.Clear();
            return drained;
        }
    }

    /// <summary>
    ///     <para>判断授权是否已超过到期时间。</para>
    ///     <para>Whether the grant is past its expiry.</para>
    /// </summary>
    private static bool IsExpired(Grant grant, DateTimeOffset now) =>
        grant.ExpiresUtc is { } expires && now > expires;

    /// <summary>
    ///     <para>在锁内查找与冻结计划匹配的活动授权下标；已过期授权顺带标记为到期。未命中返回 -1。</para>
    ///     <para>
    ///         Finds the index of an active grant matching the frozen plan under lock, lazily marking expired
    ///         grants; -1 when nothing matches.
    ///     </para>
    /// </summary>
    private int FindLocked(ToolPreparation preparation, DateTimeOffset now)
    {
        for (var i = 0; i < _grants.Count; i++)
        {
            if (_grants[i].State != GrantState.Active) continue;
            if (IsExpired(_grants[i], now))
            {
                TransitionLocked(i, GrantState.Expired, "expired", now);
                continue;
            }

            if (Matches(_grants[i], preparation)) return i;
        }

        return -1;
    }

    /// <summary>
    ///     <para>判断授权是否覆盖冻结计划：能力、约束、执行策略身份一致且授权范围覆盖全部目标。</para>
    ///     <para>
    ///         Whether the grant covers the frozen plan: capability, constraint and policy identity agree and the
    ///         grant scope covers every target.
    ///     </para>
    /// </summary>
    private static bool Matches(Grant grant, ToolPreparation preparation)
    {
        if (preparation.RequestedBackend == ProcessExecutionBackendKind.Host &&
            grant.Scope                  != GrantScope.SingleOperation)
            return false;
        if (!string.Equals(grant.Capability, preparation.Capability, StringComparison.Ordinal)) return false;

        var constraint = preparation.SessionGrantConstraint ?? preparation.SessionConstraint;
        if (!string.Equals(grant.Constraint, constraint, StringComparison.Ordinal)) return false;
        if (!string.Equals(grant.PolicyIdentity, preparation.ExecutionPolicy, StringComparison.Ordinal))
            return false;

        if (grant.Targets.Count == 0) return true; // 无范围授权：覆盖该能力的任意资源。Unscoped grant: any resource of the capability.
        if (preparation.TargetPaths.Count == 0) return false;

        // 授权必须覆盖计划声明的每一个目标。The grant must cover every target the plan declares.
        return preparation.TargetPaths.All(target =>
                                               grant.Targets.Any(scope => Workspace.IsInside(scope, target)));
    }

    /// <summary>
    ///     <para>在锁内按下标替换授权状态并记录事件；调用方必须已持有锁。</para>
    ///     <para>Replaces a grant's state by index and records the event under lock; the caller must hold the lock.</para>
    /// </summary>
    private void TransitionLocked(int index, GrantState target, string action, DateTimeOffset at)
    {
        var previous = _grants[index];
        var updated  = previous with { State = target };
        _grants[index] = updated;
        _events.Add(new GrantEvent(updated, previous.State, action, at));
    }

    /// <summary>
    ///     <para>授权事件的只读快照：授予、消费、撤销与到期，供审计写入方按发生顺序消费。</para>
    ///     <para>A read-only grant event: grants, consumptions, revocations and expiries, consumed in order by audit writers.</para>
    /// </summary>
    public sealed record GrantEvent(Grant Grant, GrantState PreviousState, string Action, DateTimeOffset OccurredUtc);
}
