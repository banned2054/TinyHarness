using System.Text.Json.Nodes;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Runtime;

namespace TinyHarness.Tests;

public class PermissionEngineTests
{
    private static ToolPreparation Prep(
        string capability, IReadOnlyList<string> targets, string argumentKey = "x", int argumentValue = 1,
        string? sessionConstraint = null, string? grantConstraint = null, string? executionPolicy = null,
        ProcessExecutionBackendKind? requestedBackend = null, bool requiresNetwork = false) => new()
    {
        ToolName               = "apply_patch",
        CallId                 = "call_1",
        Arguments              = new JsonObject { [argumentKey] = argumentValue },
        Capability             = capability,
        Summary                = "summary",
        SessionConstraint      = sessionConstraint,
        SessionGrantConstraint = grantConstraint,
        ExecutionPolicy        = executionPolicy,
        RequestedBackend       = requestedBackend,
        RequiresNetworkAccess  = requiresNetwork,
        TargetPaths            = targets
    };

    private static ToolPreparation ShellPrep(string root, string executionPolicy = "backend=windows-sandbox:test",
                                             ProcessExecutionBackendKind? requestedBackend = null,
                                             bool requiresNetwork = false) =>
        Prep("process.execute", [root], executionPolicy : executionPolicy, requestedBackend : requestedBackend,
             requiresNetwork : requiresNetwork);

    [Test]
    public void BaselineReadWriteInsideWorkspace_IsLevel1Allowed()
    {
        using var dir    = new TestTempDir();
        var       engine = new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root));
        var       target = Path.Combine(dir.Root, "a.cs");

        foreach (var capability in new[]
                 {
                     "filesystem.read", "filesystem.list", "filesystem.search",
                     "filesystem.write"
                 })
        {
            var evaluation = engine.Evaluate(Prep(capability, [target]));
            Assert.That(evaluation.Decision, Is.EqualTo(PermissionDecision.Allow), capability);
            Assert.That(evaluation.Level, Is.EqualTo(PermissionLevel.Level1), capability);
        }
    }

    [Test]
    public void ProtectedMetadataRead_StaysBaseline_AndWriteAsksLevel2()
    {
        using var dir    = new TestTempDir();
        var       engine = new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root));
        var       git    = Path.Combine(dir.Root, ".git", "HEAD");

        var read = engine.Evaluate(Prep("filesystem.read", [git]));
        Assert.Multiple(() =>
        {
            Assert.That(read.Decision, Is.EqualTo(PermissionDecision.Allow));
            Assert.That(read.Level, Is.EqualTo(PermissionLevel.Level1));
        });

        var write = engine.Evaluate(Prep("filesystem.write", [git]));
        Assert.Multiple(() =>
        {
            Assert.That(write.Decision, Is.EqualTo(PermissionDecision.Ask));
            Assert.That(write.Level, Is.EqualTo(PermissionLevel.Level2));
        });
    }

    [Test]
    public void OutsideWorkspaceTarget_AsksLevel2_AndMultiTargetTakesStrictest()
    {
        using var dir     = new TestTempDir();
        var       engine  = new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root));
        var       outside = Path.Combine(Path.GetDirectoryName(dir.Root)!, "elsewhere.cs");

        var single = engine.Evaluate(Prep("filesystem.write", [outside]));
        Assert.Multiple(() =>
        {
            Assert.That(single.Decision, Is.EqualTo(PermissionDecision.Ask));
            Assert.That(single.Level, Is.EqualTo(PermissionLevel.Level2));
        });

        var mixed = engine.Evaluate(Prep("filesystem.write",
                                         [Path.Combine(dir.Root, "a.cs"), outside]));
        Assert.That(mixed.Level, Is.EqualTo(PermissionLevel.Level2));
    }

    [Test]
    public void SystemProtectedResource_IsDeniedAheadOfGrants()
    {
        using var dir        = new TestTempDir();
        var       protected_ = Path.Combine(dir.Root, "trusted-config.json");
        var engine = new PermissionEngine(dir.Root,
                                          resourcePolicy : new SystemProtectedPolicy(dir.Root, protected_));
        var target = Prep("filesystem.write", [protected_]);

        engine.Grants.Add(GrantScope.Turn, target);
        Assert.That(engine.Evaluate(target).Decision, Is.EqualTo(PermissionDecision.Deny));
    }

    [Test]
    public void UnknownCapability_AsksFailClosedWithMissingCapability()
    {
        using var dir    = new TestTempDir();
        var       engine = new PermissionEngine(dir.Root);

        var evaluation = engine.Evaluate(Prep("filesystem.unknown", [dir.Root]));
        Assert.Multiple(() =>
        {
            Assert.That(evaluation.Decision, Is.EqualTo(PermissionDecision.Ask));
            Assert.That(evaluation.Level, Is.EqualTo(PermissionLevel.None));
            Assert.That(evaluation.MissingCapabilities, Does.Contain("filesystem.unknown"));
        });
    }

    [Test]
    public void UnclassifiedTarget_FailsClosedToApproval()
    {
        using var dir    = new TestTempDir();
        var       engine = new PermissionEngine(dir.Root);

        var evaluation = engine.Evaluate(Prep("filesystem.write", [Path.Combine(dir.Root, "a.cs")]));
        Assert.Multiple(() =>
        {
            Assert.That(evaluation.Decision, Is.EqualTo(PermissionDecision.Ask));
            Assert.That(evaluation.Level, Is.EqualTo(PermissionLevel.None));
        });
    }

    [Test]
    public void SandboxUnavailable_DeniesEveryProcessExecution_IncludingHostRequests()
    {
        using var dir = new TestTempDir();
        var engine = new PermissionEngine(dir.Root, sandboxExecutionAvailable : false,
                                          resourcePolicy : new TestResourcePolicy(dir.Root));

        var sandboxed = engine.Evaluate(ShellPrep(dir.Root));
        var host      = engine.Evaluate(ShellPrep(dir.Root, requestedBackend : ProcessExecutionBackendKind.Host));

        Assert.Multiple(() =>
        {
            Assert.That(sandboxed.Decision, Is.EqualTo(PermissionDecision.Deny),
                        "沙箱不可用时进程执行 fail-closed。");
            Assert.That(sandboxed.Reason, Does.Contain("Sandbox unavailable"));
            Assert.That(sandboxed.MissingCapabilities, Is.Empty, "不可用是拒绝不是缺失能力。");
            Assert.That(host.Decision, Is.EqualTo(PermissionDecision.Deny));
            Assert.That(host.Level, Is.EqualTo(PermissionLevel.None));
        });
    }

    [Test]
    public void ProcessExecution_IsLevel1_HostRequestIsLevel3Ask_NetworkRequestIsLevel2Ask()
    {
        using var dir    = new TestTempDir();
        var       engine = new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root));

        var sandboxed = engine.Evaluate(ShellPrep(dir.Root));
        Assert.Multiple(() =>
        {
            Assert.That(sandboxed.Decision, Is.EqualTo(PermissionDecision.Allow));
            Assert.That(sandboxed.Level, Is.EqualTo(PermissionLevel.Level1));
        });

        var host = engine.Evaluate(ShellPrep(dir.Root, "backend=host",
                                             ProcessExecutionBackendKind.Host));
        Assert.Multiple(() =>
        {
            Assert.That(host.Decision, Is.EqualTo(PermissionDecision.Ask));
            Assert.That(host.Level, Is.EqualTo(PermissionLevel.Level3));
            Assert.That(host.MissingCapabilities, Does.Contain("host-execution"),
                        "缺口由分类与计划推导。");
        });

        var networked = engine.Evaluate(ShellPrep(dir.Root, requiresNetwork : true));
        Assert.Multiple(() =>
        {
            Assert.That(networked.Decision, Is.EqualTo(PermissionDecision.Ask));
            Assert.That(networked.Level, Is.EqualTo(PermissionLevel.Level2));
            Assert.That(networked.MissingCapabilities, Does.Contain("network-access"));
        });
    }

    [Test]
    public void AdditionalPathWrite_GapIsDerivedFromClassification()
    {
        using var dir    = new TestTempDir();
        var       engine = new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root));

        var outside = engine.Evaluate(Prep("filesystem.write",
                                           [Path.Combine(Path.GetDirectoryName(dir.Root)!, "elsewhere.cs")]));
        Assert.Multiple(() =>
        {
            Assert.That(outside.Decision, Is.EqualTo(PermissionDecision.Ask));
            Assert.That(outside.MissingCapabilities, Does.Contain("additional-path-write"));
        });

        // 三级契约：工作区外普通读取属 L1 广泛读基线，自动放行；写方向仍按 additional-path-write 审批。
        var outsideRead = engine.Evaluate(Prep("filesystem.read",
                                               [Path.Combine(Path.GetDirectoryName(dir.Root)!, "elsewhere.cs")]));
        Assert.Multiple(() =>
        {
            Assert.That(outsideRead.Decision, Is.EqualTo(PermissionDecision.Allow));
            Assert.That(outsideRead.Level, Is.EqualTo(PermissionLevel.Level1));
            Assert.That(outsideRead.MissingCapabilities, Is.Empty);
        });

        var metadata = engine.Evaluate(Prep("filesystem.write",
                                            [Path.Combine(dir.Root, ".git", "config")]));
        Assert.That(metadata.MissingCapabilities, Does.Contain("metadata-write"));
    }

    [Test]
    public void StandingNetworkTrust_LetsNetworkedCommandsFollowTheirFileClassification()
    {
        using var dir = new TestTempDir();
        // 免审批仅对应"常设网络已启用 + Confirmed"；历史 sandboxNetworkStandingEnabled 直通已移除。
        var engine = new PermissionEngine(dir.Root, standingTrust : StandingTrustState.Confirmed,
                                          standingNetworkEnabled : true,
                                          resourcePolicy : new TestResourcePolicy(dir.Root));

        var inside = engine.Evaluate(ShellPrep(dir.Root, requiresNetwork : true));
        Assert.Multiple(() =>
        {
            Assert.That(inside.Decision, Is.EqualTo(PermissionDecision.Allow));
            Assert.That(inside.Level, Is.EqualTo(PermissionLevel.Level1));
        });

        // 已确认的常设信任下网络申请不再单独强制审批；沙箱 Shell 一律按 Level1 路由（冻结决策链）。
        var outside = engine.Evaluate(ShellPrep(Path.Combine(Path.GetDirectoryName(dir.Root)!, "work"),
                                                requiresNetwork : true));
        Assert.Multiple(() =>
        {
            Assert.That(outside.Decision, Is.EqualTo(PermissionDecision.Allow));
            Assert.That(outside.Level, Is.EqualTo(PermissionLevel.Level1));
        });
    }

    [Test]
    public void WorkerAndUntrustedWorkspace_HardDenyNonReadOnly_AheadOfEveryGrant()
    {
        using var dir       = new TestTempDir();
        var       policy    = new TestResourcePolicy(dir.Root);
        var       worker    = new PermissionEngine(dir.Root, true, resourcePolicy : policy);
        var       untrusted = new PermissionEngine(dir.Root, workspaceReadOnly : () => true, resourcePolicy : policy);
        var       write     = Prep("filesystem.write", [Path.Combine(dir.Root, "a.cs")]);

        foreach (var engine in new[] { worker, untrusted })
        {
            engine.Grants.Add(GrantScope.Turn, write);
            Assert.Multiple(() =>
            {
                Assert.That(engine.Evaluate(write).Decision, Is.EqualTo(PermissionDecision.Deny));
                Assert.That(engine.Evaluate(Prep("filesystem.read", [dir.Root])).Decision,
                            Is.EqualTo(PermissionDecision.Allow));
            });
        }
    }

    [Test]
    public void NeedsInspectionBlock_DeniesSideEffects_KeepsReads_AndSurvivesGrants()
    {
        using var dir    = new TestTempDir();
        var       engine = new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root));
        var       write  = Prep("filesystem.write", [Path.Combine(dir.Root, "a.cs")]);

        engine.SetNonReadOnlyBlock("run x left unknown side effects");
        engine.Grants.Add(GrantScope.Turn, write);

        Assert.Multiple(() =>
        {
            Assert.That(engine.Evaluate(write).Decision, Is.EqualTo(PermissionDecision.Deny));
            Assert.That(engine.Evaluate(Prep("filesystem.read", [dir.Root])).Decision,
                        Is.EqualTo(PermissionDecision.Allow));
            Assert.That(engine.NonReadOnlyBlockReason, Is.EqualTo("run x left unknown side effects"));
        });

        engine.SetNonReadOnlyBlock(null);
        Assert.That(engine.Evaluate(write).Decision, Is.EqualTo(PermissionDecision.Allow));
    }

    [Test]
    public void DenialMemory_SuppressesReprompt_ForSameOperation_AndSurvivesBackendSwitch()
    {
        using var dir    = new TestTempDir();
        var       engine = new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root));
        var       target = Path.Combine(dir.Root, "a.cs");
        var denied = Prep("filesystem.write", [target], sessionConstraint : "command-identity",
                          executionPolicy : "backend=windows-sandbox:one");

        engine.RecordDenial(denied);

        // 同一命令换执行后端（策略身份变化、命令约束不变）仍命中拒绝记忆。
        var switched = Prep("filesystem.write", [target], sessionConstraint : "command-identity",
                            executionPolicy : "backend=host");
        Assert.That(engine.Evaluate(switched).Decision, Is.EqualTo(PermissionDecision.Deny));

        // 基线内不同目标不受拒绝记忆影响（Level1 自动放行）；越界目标仍走审批。
        var baseline = engine.Evaluate(Prep("filesystem.write", [Path.Combine(dir.Root, "b.cs")]));
        Assert.That(baseline.Decision, Is.EqualTo(PermissionDecision.Allow));
        var outside =
            engine.Evaluate(Prep("filesystem.write", [Path.Combine(Path.GetDirectoryName(dir.Root)!, "b.cs")]));
        Assert.That(outside.Decision, Is.EqualTo(PermissionDecision.Ask));
    }

    [Test]
    public void DenialMemory_BeatsLevel1Baseline_AndGrants()
    {
        using var dir    = new TestTempDir();
        var       engine = new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root));
        var       write  = Prep("filesystem.write", [Path.Combine(dir.Root, "a.cs")]);

        engine.RecordDenial(write);
        engine.Grants.Add(GrantScope.Session, write);

        Assert.Multiple(() =>
        {
            Assert.That(engine.Evaluate(write).Decision, Is.EqualTo(PermissionDecision.Deny));
            Assert.That(engine.Evaluate(Prep("filesystem.read", [dir.Root])).Decision,
                        Is.EqualTo(PermissionDecision.Allow));
        });
    }

    [Test]
    public void TurnGrant_CoversMatchingScopes_Repeatedly_AndStopsAtExpiryOrRevocation()
    {
        using var dir    = new TestTempDir();
        var       clock  = new FakeClock(new DateTimeOffset(2026, 11, 7, 0, 0, 0, TimeSpan.Zero));
        var       grants = new GrantRegistry(clock);
        var engine = new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root),
                                          grantRegistry : grants, clock : clock);
        // Level2 操作（工作区外）才由授权驱动放行；基线 Level1 无需授权。
        var scope  = Path.Combine(Path.GetDirectoryName(dir.Root)!, "scopes", "dir");
        var inside = Prep("filesystem.write", [Path.Combine(scope, "a.cs")], sessionConstraint : "identity");
        var outsideScope = Prep("filesystem.write", [Path.Combine(scope, "..", "other", "b.cs")],
                                sessionConstraint : "identity");

        var grant = grants.Add(GrantScope.Turn, inside, TimeSpan.FromHours(1));
        Assert.Multiple(() =>
        {
            Assert.That(engine.Evaluate(inside).Decision, Is.EqualTo(PermissionDecision.Allow));
            Assert.That(engine.Evaluate(inside).Decision, Is.EqualTo(PermissionDecision.Allow),
                        "Turn 授权在生命周期内可复用。");
            Assert.That(engine.Evaluate(outsideScope).Decision, Is.EqualTo(PermissionDecision.Ask),
                        "授权范围外的目标不受授权覆盖。");
        });

        clock.Advance(TimeSpan.FromHours(2));
        Assert.That(engine.Evaluate(inside).Decision, Is.EqualTo(PermissionDecision.Ask),
                    "过期的授权不再放行。");
        Assert.That(grants.SnapshotGrants().Single(g => g.GrantId == grant.GrantId).State,
                    Is.EqualTo(GrantState.Expired));
    }

    [Test]
    public void SingleOperationGrant_IsConsumedExactlyOnce_AndThenAsksAgain()
    {
        using var dir    = new TestTempDir();
        var       engine = new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root));
        var       write  = Prep("filesystem.write", [Path.Combine(Path.GetDirectoryName(dir.Root)!, "a.cs")]);

        Assert.That(engine.Evaluate(write).Decision, Is.EqualTo(PermissionDecision.Ask));
        engine.Grants.Add(GrantScope.SingleOperation, write);

        Assert.Multiple(() =>
        {
            Assert.That(engine.Evaluate(write).Decision, Is.EqualTo(PermissionDecision.Allow));
            Assert.That(engine.ConsumeGrant(write), Is.Not.Null);
            Assert.That(engine.ConsumeGrant(write), Is.Null, "同一单次授权不能消费两次。");
            Assert.That(engine.Evaluate(write).Decision, Is.EqualTo(PermissionDecision.Ask));
        });
    }

    [Test]
    public void SingleOperationConsumption_IsAtomic_UnderConcurrentRequests()
    {
        using var dir    = new TestTempDir();
        var       grants = new GrantRegistry();
        var engine = new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root),
                                          grantRegistry : grants);
        var write = Prep("filesystem.write", [Path.Combine(Path.GetDirectoryName(dir.Root)!, "a.cs")]);
        grants.Add(GrantScope.SingleOperation, write);

        using var gate = new ManualResetEventSlim(false);
        var results = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            gate.Wait();
            return engine.ConsumeGrant(write);
        })).ToArray();
        gate.Set();
        Task.WaitAll(results);

        Assert.That(results.Count(result => result.Result is not null), Is.EqualTo(1),
                    "并发请求不得共用一次单次授权。");
        Assert.That(grants.SnapshotGrants().Single().State, Is.EqualTo(GrantState.Consumed));
    }

    [Test]
    public void Grants_DoNotMatchAcrossConstraintsPoliciesOrTargets()
    {
        using var dir    = new TestTempDir();
        var       engine = new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root));
        // Level2 操作（工作区外目录）才由授权驱动放行；基线 Level1 与授权无关。
        var       scope  = Path.Combine(Path.GetDirectoryName(dir.Root)!, "scopes", "dir");
        var granted = Prep("filesystem.write", [scope], sessionConstraint : "command-a",
                           grantConstraint : "command-a\nexecution-policy: p1",
                           executionPolicy : "p1");

        engine.Grants.Add(GrantScope.Turn, granted);

        var samePolicy = Prep("filesystem.write", [Path.Combine(scope, "a.cs")], sessionConstraint : "command-a",
                              grantConstraint : "command-a\nexecution-policy: p1",
                              executionPolicy : "p1");
        Assert.That(engine.Evaluate(samePolicy).Decision, Is.EqualTo(PermissionDecision.Allow));

        var otherCommand = Prep("filesystem.write", [scope], sessionConstraint : "command-b",
                                grantConstraint : "command-b\nexecution-policy: p1",
                                executionPolicy : "p1");
        Assert.That(engine.Evaluate(otherCommand).Decision, Is.EqualTo(PermissionDecision.Ask),
                    "不同命令不能复用授权。");

        var otherPolicy = Prep("filesystem.write", [scope], sessionConstraint : "command-a",
                               grantConstraint : "command-a\nexecution-policy: p2",
                               executionPolicy : "p2");
        Assert.That(engine.Evaluate(otherPolicy).Decision, Is.EqualTo(PermissionDecision.Ask),
                    "不同执行策略身份不能复用授权。");

        var otherTarget = Prep("filesystem.write", [Path.Combine(Path.GetDirectoryName(dir.Root)!, "elsewhere")],
                               sessionConstraint : "command-a",
                               grantConstraint : "command-a\nexecution-policy: p1",
                               executionPolicy : "p1");
        Assert.That(engine.Evaluate(otherTarget).Decision, Is.EqualTo(PermissionDecision.Ask),
                    "授权范围外的目标不能复用授权。");
    }

    [Test]
    public void Revalidate_RerunsSafetyChecks_AndDetectsFrozenScopeDrift()
    {
        using var dir     = new TestTempDir();
        var       outside = Path.Combine(Path.GetDirectoryName(dir.Root)!, "a.cs");
        var       frozen  = new FrozenCapabilityScope([], [outside], []);
        var engine = new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root),
                                          frozenScopeReader : _ => frozen);
        var write = Prep("filesystem.write", [outside]);

        engine.Grants.Add(GrantScope.Turn, write);
        Assert.Multiple(() =>
        {
            Assert.That(engine.Revalidate(write).Decision, Is.EqualTo(PermissionDecision.Allow));
            // current 快照仍在冻结范围内。
            Assert.That(engine.Revalidate(write, new ActiveCapabilitySnapshot([], [outside], [])).Decision,
                        Is.EqualTo(PermissionDecision.Allow));
            Assert.That(engine.Revalidate(write, new ActiveCapabilitySnapshot([], [], [])).Decision,
                        Is.EqualTo(PermissionDecision.Deny), "撤销能力后不得派发仍携带旧能力的冻结计划。");
            // current 快照出现冻结范围之外的路径：授权失效，必须重新发起调用。
            Assert.Multiple(() =>
            {
                Assert.That(engine.Revalidate(write,
                                              new ActiveCapabilitySnapshot([], [
                                                  outside, Path.Combine(Path.GetDirectoryName(dir.Root)!, "b.cs")
                                              ], []))
                                  .Decision, Is.EqualTo(PermissionDecision.Deny));
                Assert.That(engine.Revalidate(write,
                                              new ActiveCapabilitySnapshot([], [
                                                  outside, Path.Combine(Path.GetDirectoryName(dir.Root)!, "b.cs")
                                              ], []))
                                  .Reason, Does.Contain("capability scope changed"));
            });
        });

        // 拒绝记忆在复核中依然生效。
        engine.RecordDenial(write);
        Assert.That(engine.Revalidate(write).Decision, Is.EqualTo(PermissionDecision.Deny));
    }

    [Test]
    public void SetApprovalMode_RevokesGrants_KeepsDenialsAndInspectionBlock()
    {
        using var dir    = new TestTempDir();
        var       engine = new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root));
        var       write  = Prep("filesystem.write", [Path.Combine(Path.GetDirectoryName(dir.Root)!, "a.cs")]);

        engine.Grants.Add(GrantScope.Session, write);
        engine.RecordDenial(Prep("filesystem.write", [Path.Combine(dir.Root, "denied.cs")]));
        engine.SetNonReadOnlyBlock("inspection pending");

        engine.SetApprovalMode(SessionApprovalMode.AIAutoReview);
        Assert.Multiple(() =>
        {
            Assert.That(engine.ApprovalMode, Is.EqualTo(SessionApprovalMode.AIAutoReview));
            Assert.That(engine.NonReadOnlyBlockReason, Is.EqualTo("inspection pending"),
                        "模式切换不撤销中断检查。");
        });

        engine.SetNonReadOnlyBlock(null);
        Assert.Multiple(() =>
        {
            Assert.That(engine.Evaluate(write).Decision, Is.EqualTo(PermissionDecision.Ask),
                        "模式切换撤销活动授权。");
            Assert.That(engine.Evaluate(Prep("filesystem.write", [Path.Combine(dir.Root, "denied.cs")]))
                              .Decision, Is.EqualTo(PermissionDecision.Deny),
                        "模式切换不撤销显式拒绝。");
        });
    }

    [Test]
    public void RevalidateRejectsMissingAndRevokedGrants()
    {
        using var dir    = new TestTempDir();
        var       engine = new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root));
        var       write  = Prep("filesystem.write", [Path.Combine(Path.GetDirectoryName(dir.Root)!, "outside.txt")]);

        Assert.That(engine.Revalidate(write).Decision, Is.EqualTo(PermissionDecision.Deny));
        var grant = engine.Grants.Add(GrantScope.Turn, write);
        Assert.That(engine.Revalidate(write).Decision, Is.EqualTo(PermissionDecision.Allow));
        engine.Grants.Revoke(grant.GrantId);
        Assert.That(engine.Revalidate(write).Decision, Is.EqualTo(PermissionDecision.Deny));
    }

    [Test]
    public void ReapplyingTheSameModePreservesSessionGrants()
    {
        using var dir    = new TestTempDir();
        var       engine = new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root));
        var       write  = Prep("filesystem.write", [Path.Combine(Path.GetDirectoryName(dir.Root)!, "outside.txt")]);
        engine.SetApprovalMode(SessionApprovalMode.HumanApproval);
        engine.Grants.Add(GrantScope.Session, write);

        engine.SetApprovalMode(SessionApprovalMode.HumanApproval);
        Assert.That(engine.Evaluate(write).Decision, Is.EqualTo(PermissionDecision.Allow));
        engine.SetApprovalMode(SessionApprovalMode.AIAutoReview);
        Assert.That(engine.Evaluate(write).Decision, Is.EqualTo(PermissionDecision.Ask));
    }

    [Test]
    public void GrantScopeOptions_Level3IsSingleOperationOnly_AndLevel2AddsTurnAndSession()
    {
        Assert.Multiple(() =>
        {
            Assert.That(GrantScopeOptions.AllowedForLevel(PermissionLevel.Level3),
                        Is.EqualTo([GrantScope.SingleOperation]));
            Assert.That(GrantScopeOptions.AllowedForLevel(PermissionLevel.Level2),
                        Is.EqualTo([GrantScope.SingleOperation, GrantScope.Turn, GrantScope.Session]));
            Assert.That(GrantScopeOptions.AllowedForLevel(PermissionLevel.Level1),
                        Is.EqualTo([GrantScope.SingleOperation]));
            Assert.That(GrantScopeOptions.AllowedForLevel(PermissionLevel.None),
                        Is.EqualTo([GrantScope.SingleOperation, GrantScope.Turn, GrantScope.Session]),
                        "fail-closed 未知能力仍可由用户授予运行期范围。");
        });
    }

    [Test]
    public void LegacySelectionMapper_MapsAiPresetsToAIAutoReview_AndOthersConservatively()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LegacyPermissionSelectionMapper.FromLegacyName("ReviewEveryAction"),
                        Is.EqualTo(SessionApprovalMode.AIAutoReview));
            Assert.That(LegacyPermissionSelectionMapper.FromLegacyName("ReadOnlyAutoReview"),
                        Is.EqualTo(SessionApprovalMode.AIAutoReview));
            Assert.That(LegacyPermissionSelectionMapper.FromLegacyName("ProjectWriteAutoReview"),
                        Is.EqualTo(SessionApprovalMode.AIAutoReview));
            Assert.That(LegacyPermissionSelectionMapper.FromLegacyName("FullAccess"),
                        Is.EqualTo(SessionApprovalMode.HumanApproval));
            Assert.That(LegacyPermissionSelectionMapper.FromLegacyName("ReadOnly"),
                        Is.EqualTo(SessionApprovalMode.HumanApproval));
            Assert.That(LegacyPermissionSelectionMapper.FromLegacyName("ProjectWrite"),
                        Is.EqualTo(SessionApprovalMode.HumanApproval));
            Assert.That(LegacyPermissionSelectionMapper.FromLegacyName("ConfirmEveryAction"),
                        Is.EqualTo(SessionApprovalMode.HumanApproval));
            Assert.That(LegacyPermissionSelectionMapper.FromLegacyName("nonsense"), Is.Null);
            Assert.That(LegacyPermissionSelectionMapper.FromLegacyName(null), Is.Null);
        });
    }

    /// <summary>系统保护路径策略：命中保护名单即硬拒，其余按工作区基线。</summary>
    private sealed class SystemProtectedPolicy(string workspaceRoot, string protectedPath) : IResourceAccessPolicy
    {
        public ResourceClassification? Classify(string capability, string targetPath) =>
            Workspace.IsInside(protectedPath, targetPath)
                ? new ResourceClassification(ResourceClass.PermissionSystemProtected, false)
                : new ResourceClassification(
                                             Workspace.IsInside(workspaceRoot, targetPath)
                                                 ? ResourceClass.BaselineWorkspace
                                                 : ResourceClass.OutsideWorkspace, false);

        // 与分类同语义：目标落入保护路径或覆盖保护路径都算重叠。
        public bool OverlapsPermissionSystemRoots(string path) =>
            Workspace.IsInside(protectedPath, path) || Workspace.IsInside(path, protectedPath);
    }

    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan time) => _now += time;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
