using System.Text.Json;
using System.Text.Json.Nodes;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Agent;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         Shell 写根请求参数（B5/GAP-2）契约：schema 严格校验矩阵（缺省空=历史行为不变；相对路径、
///         盘根、含空格或引号、超量、非字符串一律明确拒绝）、非沙箱栈拒绝、引擎分支（恒 L2 逐次审批、
///         missing 按根聚合、文案含根清单与单次默认语义、C2 命中注记、C1 硬拒）、Grant 语义
///         （AllowedScopes=[SingleOperation,Turn]、身份串随根冻结、参数变化失效旧授权、单次原子消费、
///         Revalidate ExceptRoots 豁免申请根而 grant 来源根仍双向一致）、组合期 fail-closed（凭据/home
///         重叠与盘根）、守卫路径事实字段传播（B5 RequiresNetworkAccess 缺口闭合固化）。
///     </para>
///     <para>
///         Shell write-root request (B5/GAP-2) contracts: the strict schema validation matrix (absent by
///         default keeps historical behavior; relative paths, drive roots, spaces or quotes, over-limit and
///         non-string entries are refused explicitly), the non-sandbox-stack refusal, the engine branch
///         (always Level 2 per call, missing aggregated per root, text with the root list and the
///         single-operation default marker, C2 annotation, C1 hard denial), grant semantics
///         (AllowedScopes=[SingleOperation,Turn], identity frozen with the roots, parameter changes
///         invalidating old grants, single atomic consumption, and Revalidate's ExceptRoots exemption while
///         grant-sourced roots stay bidirectional), compose-time fail-closed checks (credential/home overlap
///         and drive roots), and the guard path's fact-field propagation (pinning the B5
///         RequiresNetworkAccess gap fix).
///     </para>
/// </summary>
public class ShellWriteRootRequestTests
{
    private const string BaselineIdentity =
        "backend=windows-sandbox\npolicyVersion=6\nkind=workspace-write";

    // ---- schema 契约：缺省空 = 历史行为逐字不变 ----

    [Test]
    public void Prepare_WithoutWriteRoots_KeepsTheHistoricalShape()
    {
        using var dir = new TestTempDir();
        var tool = new ShellTool(dir.Workspace, executionPolicy : SandboxPolicy());

        var preparation = tool.Prepare(Call(new { mode = "direct", executable = "cmd.exe" }));

        Assert.Multiple(() =>
        {
            Assert.That(preparation.RequestedWriteRoots, Is.Empty);
            Assert.That(preparation.TargetPaths, Is.EqualTo(new[] { dir.Root }),
                        "无申请时目标仍是工作目录，授权面与历史一致。");
            Assert.That(preparation.Summary, Does.Not.Contain("write roots"));
            Assert.That(preparation.SessionGrantConstraint, Does.Not.Contain("writeRoots"));
        });
        var plan = TestAssert.TypeOf<PreparedProcessExecution>(preparation.ExecutionPlan);
        Assert.That(plan.GrantScope, Is.Null,
                    "未接入快照源的计划不携带冻结范围字段（历史形态）。");
    }

    [Test]
    public void Prepare_EmptyWriteRootsArray_IsTreatedAsAbsent_EvenOutsideTheSandboxStack()
    {
        using var dir = new TestTempDir();
        var tool = new ShellTool(dir.Workspace);

        var preparation = tool.Prepare(Call(new
                                            {
                                                mode = "direct", executable = "cmd.exe", writeRoots = Array.Empty<string>()
                                            }));

        Assert.That(preparation.RequestedWriteRoots, Is.Empty);
    }

    // ---- schema 契约：非沙箱栈携带申请 → 明确拒绝不静默 ----

    [Test]
    public void Prepare_WriteRootsWithoutTheSandboxStack_IsRejectedExplicitly()
    {
        using var dir = new TestTempDir();
        var tool = new ShellTool(dir.Workspace);

        var error = Assert.Throws<InvalidDataException>(() =>
            tool.Prepare(Call(new { mode = "direct", executable = "cmd.exe", writeRoots = new[] { OutsideRoot() } })));

        Assert.That(error!.Message, Does.Contain("does not run inside the Windows sandbox"));
    }

    // ---- schema 校验矩阵 ----

    [TestCase("relative\\dir", "fully qualified absolute path")]
    [TestCase("C:\\", "must not be a file-system root")]
    [TestCase("C:\\dir with space", "must not contain spaces or quotes")]
    [TestCase("C:\\dir\"quoted", "must not contain spaces or quotes")]
    public void Prepare_InvalidWriteRootEntry_IsRejectedExplicitly(string candidate, string expectedDetail)
    {
        using var home  = new SandboxTestHome();
        var       tool  = SandboxStackTool(home, workspaceRoot : home.Components.SandboxHome);

        var error = Assert.Throws<InvalidDataException>(() =>
            tool.Prepare(Call(new { mode = "direct", executable = "cmd.exe", writeRoots = new[] { candidate } })));

        Assert.That(error!.Message, Does.Contain(expectedDetail));
    }

    [Test]
    public void Prepare_WriteRootsOverTheLimit_IsRejected()
    {
        using var home = new SandboxTestHome();
        var tool = SandboxStackTool(home, workspaceRoot : home.Components.SandboxHome);
        var roots = Enumerable.Range(0, 9).Select(i => Path.Combine(OutsideRoot(), $"r{i}")).ToArray();

        var error = Assert.Throws<InvalidDataException>(() =>
            tool.Prepare(Call(new { mode = "direct", executable = "cmd.exe", writeRoots = roots })));

        Assert.That(error!.Message, Does.Contain("the limit is 8"));
    }

    [Test]
    public void Prepare_NonStringWriteRootEntry_IsRejected()
    {
        using var home = new SandboxTestHome();
        var tool = SandboxStackTool(home, workspaceRoot : home.Components.SandboxHome);

        var error = Assert.Throws<InvalidDataException>(() =>
            tool.Prepare(new ChatToolCall("call_1", "shell",
                                          """{"mode":"direct","executable":"cmd.exe","writeRoots":[7]}""")));

        Assert.That(error!.Message, Does.Contain("must be a string"));
    }

    [Test]
    public void Prepare_WriteRoots_AreNormalizedDeduplicatedAndWrittenBack()
    {
        using var home     = new SandboxTestHome();
        var       root     = Path.Combine(OutsideRoot(), "probe");
        var       tool     = SandboxStackTool(home, workspaceRoot : home.Components.SandboxHome);

        var preparation = tool.Prepare(Call(new
                                            {
                                                mode          = "direct",
                                                executable    = "cmd.exe",
                                                writeRoots    = new[] { root, root.ToLowerInvariant() }
                                            }));

        // 大小写不敏感去重保序（保留首次出现的规范化形态），并写回 args（拒绝记忆指纹与审计读到认可形态）。
        Assert.That(preparation.RequestedWriteRoots, Is.EqualTo(new[] { root }));
        Assert.That(preparation.Arguments["writeRoots"]!.AsArray().Select(node => node!.GetValue<string>()),
                    Is.EqualTo(new[] { root }));
    }

    // ---- 引擎分支：写根申请恒 L2 逐次审批 ----

    [Test]
    public void Engine_WriteRootRequest_AsksLevel2_WithRootListAndSingleOperationMarker()
    {
        using var dir  = new TestTempDir();
        var       root = Path.Combine(OutsideRoot(), "artifact");
        var       engine = new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root));
        var preparation = ShellPreparation(root);

        var evaluation = engine.Evaluate(preparation);

        Assert.Multiple(() =>
        {
            Assert.That(evaluation.Decision, Is.EqualTo(PermissionDecision.Ask));
            Assert.That(evaluation.Level, Is.EqualTo(PermissionLevel.Level2));
            Assert.That(evaluation.MissingCapabilities, Is.EqualTo(new[] { "additional-path-write" }));
            Assert.That(evaluation.Reason, Does.Contain(root));
            Assert.That(evaluation.Reason, Does.Contain("approve this operation to run it"));
            Assert.That(evaluation.Reason, Does.Contain("Approval defaults to this single operation; " +
                                                        "a turn grant stays inside this run only."));
        });
    }

    [Test]
    public void Engine_WriteRootRequest_MultipleRoots_AggregateOneAdditionalPathWrite()
    {
        using var dir   = new TestTempDir();
        var       roots = new[]
        {
            Path.Combine(OutsideRoot(), "one"), Path.Combine(OutsideRoot(), "two"),
            Path.Combine(OutsideRoot(), "three")
        };
        var engine = new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root));

        var evaluation = engine.Evaluate(ShellPreparation(roots));

        Assert.Multiple(() =>
        {
            Assert.That(evaluation.Level, Is.EqualTo(PermissionLevel.Level2));
            Assert.That(evaluation.MissingCapabilities, Is.EqualTo(new[] { "additional-path-write" }),
                        "一次调用申请的全部根聚合为一条附加写缺口。");
            foreach (var root in roots) Assert.That(evaluation.Reason, Does.Contain(root));
        });
    }

    [Test]
    public void Engine_WriteRootRequest_SensitiveHit_AnnotatesKnownSensitiveLocation()
    {
        using var dir     = new TestTempDir();
        using var outside = OutsideDirectory();
        var       root    = Path.Combine(outside.Root, ".ssh", "keys");
        var engine = new PermissionEngine(dir.Root, resourcePolicy : new ResourceAccessPolicy(dir.Root));

        var evaluation = engine.Evaluate(ShellPreparation(root));

        Assert.Multiple(() =>
        {
            Assert.That(evaluation.Decision, Is.EqualTo(PermissionDecision.Ask),
                        "C2 命中不阻断：L2 可经批准开放。");
            Assert.That(evaluation.Level, Is.EqualTo(PermissionLevel.Level2));
            Assert.That(evaluation.Reason, Does.Contain("(known sensitive location)"));
        });
    }

    [Test]
    public void Engine_WriteRootRequest_C1PermissionSystemProtected_IsHardDenied()
    {
        using var dir        = new TestTempDir();
        using var protected_ = OutsideDirectory();
        var engine = new PermissionEngine(dir.Root,
                                          resourcePolicy : new ResourceAccessPolicy(dir.Root,
                                              permissionSystemRoots : [protected_.Root]));
        var preparation = ShellPreparation(protected_.Root);

        var evaluation = engine.Evaluate(preparation);

        Assert.Multiple(() =>
        {
            Assert.That(evaluation.Decision, Is.EqualTo(PermissionDecision.Deny));
            Assert.That(evaluation.Level, Is.EqualTo(PermissionLevel.None));
            Assert.That(evaluation.Reason, Does.Contain("cannot be authorized"),
                        "权限系统保护路径不可经批准开放。");
            Assert.That(evaluation.MissingCapabilities, Is.Empty);
        });

        // 硬拒优先于任何授权。
        engine.Grants.Add(GrantScope.SingleOperation, preparation);
        Assert.That(engine.Evaluate(preparation).Decision, Is.EqualTo(PermissionDecision.Deny));
    }

    [Test]
    public void Engine_WriteRootRequest_CoveringAPermissionSystemObject_IsHardDenied()
    {
        using var workspace = new TestTempDir();
        using var userDir   = new TestTempDir();
        using var trustDir  = new TestTempDir();
        using var storeDir  = new TestTempDir();
        // 生产组合形态的 C1 实例：用户配置文件、共享 trust.json 与受保护会话存储目录。
        var userPath  = userDir.WriteFile("user-config.json", "{}");
        var trustPath = trustDir.WriteFile("workspace-trust.json", "{}");
        var engine = new PermissionEngine(workspace.Root,
                                          resourcePolicy : new ResourceAccessPolicy(workspace.Root,
                                              permissionSystemRoots : [userPath, trustPath, storeDir.Root]));

        // 三个保护对象的父目录申请根各一条：父目录自身分类不落保护根内（单向放行），
        // 双向重叠判定必须硬拒（覆盖保护对象不可经批准开放）。
        var coveringUserConfig = engine.Evaluate(ShellPreparation(userDir.Root));
        var coveringTrust      = engine.Evaluate(ShellPreparation(trustDir.Root));
        var coveringStore      = engine.Evaluate(ShellPreparation(storeDir.Root));

        Assert.Multiple(() =>
        {
            foreach (var (evaluation, target) in new[]
                     {
                         (coveringUserConfig, userPath), (coveringTrust, trustPath), (coveringStore, storeDir.Root)
                     })
            {
                Assert.That(evaluation.Decision, Is.EqualTo(PermissionDecision.Deny), target);
                Assert.That(evaluation.Level, Is.EqualTo(PermissionLevel.None), target);
                Assert.That(evaluation.Reason, Does.Contain("permission-system-protected"), target);
                Assert.That(evaluation.Reason, Does.Contain("cannot be authorized"), target);
                Assert.That(evaluation.MissingCapabilities, Is.Empty, target);
            }
        });

        // 会话存储目录内部的申请根（落入方向）同样硬拒。
        var insideStore = engine.Evaluate(ShellPreparation(Path.Combine(storeDir.Root, "sessions")));
        Assert.Multiple(() =>
        {
            Assert.That(insideStore.Decision, Is.EqualTo(PermissionDecision.Deny));
            Assert.That(insideStore.Reason, Does.Contain("cannot be authorized"));
        });

        // 硬拒优先于任何授权。
        var preparation = ShellPreparation(userDir.Root);
        engine.Grants.Add(GrantScope.SingleOperation, preparation);
        Assert.That(engine.Evaluate(preparation).Decision, Is.EqualTo(PermissionDecision.Deny));
    }

    [Test]
    public void Engine_WriteRootRequest_WithNetworkRequest_MergesMissingAndReasons()
    {
        using var dir  = new TestTempDir();
        var       root = Path.Combine(OutsideRoot(), "artifact");
        var engine = new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root));

        var evaluation = engine.Evaluate(ShellPreparation(root, requiresNetwork : true));

        Assert.Multiple(() =>
        {
            Assert.That(evaluation.Decision, Is.EqualTo(PermissionDecision.Ask));
            Assert.That(evaluation.Level, Is.EqualTo(PermissionLevel.Level2));
            Assert.That(evaluation.MissingCapabilities,
                        Is.EqualTo(new[] { "additional-path-write", "network-access" }));
            Assert.That(evaluation.Reason, Does.Contain(root));
            Assert.That(evaluation.Reason, Does.Contain("network access"));
        });
    }

    [Test]
    public void Engine_NetworkOnlyRequest_KeepsTheNetworkOnlyShape()
    {
        using var dir    = new TestTempDir();
        var       engine = new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root));

        var evaluation = engine.Evaluate(ShellPreparation(requiresNetwork : true));

        Assert.Multiple(() =>
        {
            Assert.That(evaluation.Decision, Is.EqualTo(PermissionDecision.Ask));
            Assert.That(evaluation.MissingCapabilities, Is.EqualTo(new[] { "network-access" }),
                        "无写根申请时不引入 additional-path-write。");
            Assert.That(evaluation.Reason, Does.Not.Contain("write access outside"));
        });
    }

    // ---- Grant 语义 ----

    [Test]
    public void AllowedScopes_WriteRootRequests_NeverOfferSession()
    {
        var rootPreparation = ShellPreparation(Path.Combine(OutsideRoot(), "artifact"));
        var plainPreparation = ShellPreparation();

        Assert.Multiple(() =>
        {
            Assert.That(GrantScopeOptions.AllowedForPreparation(PermissionLevel.Level2, rootPreparation),
                        Is.EqualTo(new[] { GrantScope.SingleOperation, GrantScope.Turn }));
            Assert.That(GrantScopeOptions.AllowedForPreparation(PermissionLevel.Level1, rootPreparation),
                        Is.EqualTo(new[] { GrantScope.SingleOperation }));
            Assert.That(GrantScopeOptions.AllowedForPreparation(PermissionLevel.Level3, rootPreparation),
                        Is.EqualTo(new[] { GrantScope.SingleOperation }));
            // 无申请的计划沿用既有矩阵。
            Assert.That(GrantScopeOptions.AllowedForPreparation(PermissionLevel.Level2, plainPreparation),
                        Is.EqualTo(new[]
                        {
                            GrantScope.SingleOperation, GrantScope.Turn, GrantScope.Session
                        }));
        });
    }

    [Test]
    public async Task AgentLoop_SessionScopeOnAWriteRootRequest_IsRejectedAndNeverExecutes()
    {
        using var home    = new SandboxTestHome();
        var       root    = Path.Combine(OutsideRoot(), "session-rejected");
        var       backend = new FakeProcessExecutionBackend { ExitCode = 0, StdoutChunks = ["ok"] };
        var       client  = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("shell",
                                               new JsonObject
                                               {
                                                   ["mode"]       = "direct",
                                                   ["executable"] = "cmd.exe",
                                                   ["arguments"]  = new JsonArray("/c", "echo", "hi"),
                                                   ["writeRoots"] = new JsonArray(root)
                                               }.ToJsonString()));
        client.Enqueue(FakeChatClient.Text("acknowledged"));

        var approver = new ScriptedApprover();
        approver.Enqueue(ApprovalResolution.Granted(GrantScope.Session));
        var tool = SandboxStackTool(home, workspaceRoot : home.Components.SandboxHome, backend : backend);
        var engine = new PermissionEngine(home.Components.SandboxHome,
                                          resourcePolicy : new TestResourcePolicy(home.Components.SandboxHome));
        var loop = new AgentLoop(client, new ToolRegistry([tool]),
                                 new AgentOptions
                                 {
                                     Model = "test-model", MaxAgentSteps = 5, DefaultToolTimeoutSeconds = 30
                                 },
                                 engine, approver);

        var result = await loop.RunAsync("sys", "write outside", CancellationToken.None);

        // 审批卡不提供 Session；越集解析双保险拒绝：不执行、拒绝原因回给模型。
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
            Assert.That(result.ToolExecutions, Is.EqualTo(0));
            Assert.That(approver.Prompts, Is.EqualTo(1));
            Assert.That(backend.ReceivedExecution, Is.Null);
        });
    }

    [Test]
    public void Engine_TurnGrant_OnAWriteRootRequest_RecusesAndDiesWithRevocation()
    {
        using var dir  = new TestTempDir();
        var       root = Path.Combine(OutsideRoot(), "turn-root");
        var       preparation = ShellPreparation(root);
        var engine = new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root));

        Assert.That(engine.Evaluate(preparation).Decision, Is.EqualTo(PermissionDecision.Ask));
        var grant = engine.Grants.Add(GrantScope.Turn, preparation);
        Assert.Multiple(() =>
        {
            Assert.That(engine.Evaluate(preparation).Decision, Is.EqualTo(PermissionDecision.Allow));
            Assert.That(engine.Evaluate(preparation).Decision, Is.EqualTo(PermissionDecision.Allow),
                        "Turn 授权在生命周期内可复用。");
        });
        engine.Grants.Revoke(grant.GrantId);
        Assert.That(engine.Evaluate(preparation).Decision, Is.EqualTo(PermissionDecision.Ask),
                    "撤销后回到逐次审批。");
    }

    [Test]
    public void Engine_SingleOperationGrant_OnAWriteRootRequest_IsConsumedExactlyOnce()
    {
        using var dir  = new TestTempDir();
        var       root = Path.Combine(OutsideRoot(), "single-root");
        var       preparation = ShellPreparation(root);
        var engine = new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root));

        engine.Grants.Add(GrantScope.SingleOperation, preparation);
        Assert.Multiple(() =>
        {
            Assert.That(engine.Evaluate(preparation).Decision, Is.EqualTo(PermissionDecision.Allow));
            Assert.That(engine.ConsumeGrant(preparation), Is.Not.Null);
            Assert.That(engine.ConsumeGrant(preparation), Is.Null, "同一单次授权不能消费两次。");
            Assert.That(engine.Evaluate(preparation).Decision, Is.EqualTo(PermissionDecision.Ask));
        });
    }

    [Test]
    public void Engine_DifferentWriteRoots_DoNotShareGrants()
    {
        using var dir = new TestTempDir();
        var first = ShellPreparation(Path.Combine(OutsideRoot(), "first"),
                                     constraint : "cmd-identity\nwriteRoots=first");
        var second = ShellPreparation(Path.Combine(OutsideRoot(), "second"),
                                      constraint : "cmd-identity\nwriteRoots=second");
        var engine = new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root));

        engine.Grants.Add(GrantScope.Turn, first);
        Assert.Multiple(() =>
        {
            Assert.That(engine.Evaluate(first).Decision, Is.EqualTo(PermissionDecision.Allow));
            Assert.That(engine.Evaluate(second).Decision, Is.EqualTo(PermissionDecision.Ask),
                        "同一命令不同写根互不共享授权（身份串随根冻结）。");
        });
    }

    [Test]
    public void Prepare_SameWriteRoots_FreezeTheSameIdentity_AndDifferentRootsDiverge()
    {
        using var home = new SandboxTestHome();
        var root = Path.Combine(OutsideRoot(), "identity");
        var other = Path.Combine(OutsideRoot(), "other");
        var tool = SandboxStackTool(home, workspaceRoot : home.Components.SandboxHome);

        var first = tool.Prepare(Call(new { mode = "direct", executable = "cmd.exe", writeRoots = new[] { root } }));
        var repeat = tool.Prepare(Call(new { mode = "direct", executable = "cmd.exe", writeRoots = new[] { root } }));
        var changed = tool.Prepare(Call(new { mode = "direct", executable = "cmd.exe", writeRoots = new[] { other } }));

        // 同参数同身份：批准后的 grant 冻结身份与下次 Prepare 一致（Turn 复用的前提）。
        Assert.That(repeat.SessionGrantConstraint, Is.EqualTo(first.SessionGrantConstraint));
        Assert.That(first.SessionGrantConstraint, Does.Contain("grantWriteRoots="));
        Assert.That(first.SessionGrantConstraint, Does.Contain(root.Replace("\\", "\\\\")),
                    "命令身份以 JSON 形态携带申请根。");
        // 参数变化即身份变化：旧授权对新申请不匹配。
        Assert.That(changed.SessionGrantConstraint, Is.Not.EqualTo(first.SessionGrantConstraint));
    }

    // ---- Revalidate：ExceptRoots 豁免申请根，grant 来源根仍双向一致 ----

    [Test]
    public void Revalidate_RequestedRoots_AreExemptFromSnapshotLiveness()
    {
        using var dir  = new TestTempDir();
        var       root = Path.Combine(OutsideRoot(), "requested");
        // 冻结范围=快照(空)∪申请根：单次批准（派发凭据）后首次执行不得被"capability scope changed"误拒。
        var frozen = new FrozenCapabilityScope([], [root], []);
        var engine = new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root),
                                          frozenScopeReader : _ => frozen);
        var preparation = ShellPreparation(root);

        Assert.Multiple(() =>
        {
            var dispatch = engine.CreateDispatchAuthorization(preparation, engine.AuthorizationRevision,
                                                              singleOperationApproved : true);
            Assert.That(engine.RevalidateForDispatch(preparation, null, dispatch).Decision,
                        Is.EqualTo(PermissionDecision.Allow));
            // 派发凭据单次有效：第二次复核使用新凭据（同一批准形态）。
            var secondDispatch = engine.CreateDispatchAuthorization(preparation, engine.AuthorizationRevision,
                                                                    singleOperationApproved : true);
            Assert.That(engine.RevalidateForDispatch(preparation, new ActiveCapabilitySnapshot([], [], []),
                                                     secondDispatch).Decision,
                        Is.EqualTo(PermissionDecision.Allow),
                        "单次调用自带写根由本次批准背书，快照无根不是失效。");
        });
    }

    [Test]
    public void Revalidate_GrantSourcedRootChanges_AreStillDenied()
    {
        using var dir    = new TestTempDir();
        var       root   = Path.Combine(OutsideRoot(), "requested");
        var       granted = Path.Combine(OutsideRoot(), "granted");
        var preparation = ShellPreparation(root);

        // grant 来源根被撤销：冻结含 grant 根+申请根，当前快照为空 → 授权失效。
        var revoked = new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root),
                                           frozenScopeReader : _ => new FrozenCapabilityScope([],
                                               [granted, root], []));
        Assert.Multiple(() =>
        {
            Assert.That(revoked.Revalidate(preparation, new ActiveCapabilitySnapshot([], [], [])).Decision,
                        Is.EqualTo(PermissionDecision.Deny));
            Assert.That(revoked.Revalidate(preparation, new ActiveCapabilitySnapshot([], [], [])).Reason,
                        Does.Contain("capability scope changed"));
        });

        // grant 来源根出现冻结范围之外的新扩大 → 授权失效（豁免只覆盖申请根本身）。
        var expanded = new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root),
                                            frozenScopeReader : _ => new FrozenCapabilityScope([], [root], []));
        Assert.That(expanded.Revalidate(preparation,
                                        new ActiveCapabilitySnapshot([], [root, granted], [])).Decision,
                    Is.EqualTo(PermissionDecision.Deny));
    }

    // ---- C1/C2 组合期校验面（Composer） ----

    [Test]
    public void Prepare_WriteRootCoveringTheSandboxHome_FailsClosedAtPrepare()
    {
        using var home = new SandboxTestHome();
        var tool = SandboxStackTool(home, workspaceRoot : home.Components.SandboxHome);

        // 写根覆盖沙箱 home（含凭据目录）：组合期 ResolveGrantScope fail-closed，Prepare 直接抛出。
        var error = Assert.Throws<InvalidOperationException>(() =>
            tool.Prepare(Call(new
                              {
                                  mode = "direct", executable = "cmd.exe", writeRoots = new[] { home.Components.SandboxHome }
                              })));

        Assert.Multiple(() =>
        {
            Assert.That(error!.Message, Does.Contain("sandbox credentials directory"));
            Assert.That(error.Message, Does.Contain("refusing the grant scope"));
        });
    }

    [Test]
    public void Prepare_WriteRootOverlappingAPermissionSystemPath_FailsClosedAtPrepare()
    {
        using var home     = new SandboxTestHome();
        using var storeDir = new TestTempDir();
        // grant 上下文携带组合点传入的 C1 实例路径：申请根与之重叠在 Prepare 期 fail-closed。
        var tool = SandboxStackTool(home, workspaceRoot : home.Components.SandboxHome,
                                    permissionSystemPaths : [storeDir.Root]);

        var error = Assert.Throws<InvalidOperationException>(() =>
            tool.Prepare(Call(new
                              {
                                  mode = "direct", executable = "cmd.exe",
                                  writeRoots = new[] { Path.Combine(storeDir.Root, "sessions") }
                              })));

        Assert.Multiple(() =>
        {
            Assert.That(error!.Message, Does.Contain("permission-system protected path"));
            Assert.That(error.Message, Does.Contain("refusing the grant scope"));
        });
    }

    [Test]
    public void ResolveGrantScope_DriveRootWriteRoot_IsRefused()
    {
        using var home = new SandboxTestHome();
        var policy = SandboxPolicyResolver.Resolve(SandboxPolicyKind.WorkspaceWrite,
                                                   home.Components.SandboxHome,
                                                   new Dictionary<string, string>
                                                   {
                                                       ["TEMP"] = home.Components.SandboxHome
                                                   });

        var error = Assert.Throws<InvalidOperationException>(() =>
            WindowsSandboxComposer.ResolveGrantScope(new SandboxGrantContext(policy, home.Components).Policy,
                                                     home.Components, [], ["C:\\"], []));

        Assert.That(error!.Message, Does.Contain("must not be a file-system root"));
    }

    // ---- C1 实例路径的组合期保护（Compose 与 ResolveGrantScope 双向重叠） ----

    [Test]
    public void Compose_WriteRootOverlappingAPermissionSystemPath_IsRefused()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Windows sandbox composition requires Windows.");
        using var workspace = new TestTempDir();
        using var home      = new TestTempDir();
        using var storeDir  = new TestTempDir();
        var sessionStore = Path.Combine(storeDir.Root, "store");
        var settings = new WindowsSandboxSettings
        {
            Enabled              = true,
            SetupExecutablePath  = Path.Combine(home.Root, "setup.exe"),
            RunnerExecutablePath = Path.Combine(home.Root, "runner.exe"),
            SandboxHome          = home.Root
        };

        // 覆盖方向：常设写根覆盖会话存储目录 → 组合 fail-closed。
        var covering = settings with { AdditionalWriteRoots = [storeDir.Root] };
        var coveringError = Assert.Throws<InvalidOperationException>(() =>
            WindowsSandboxComposer.Compose(covering, workspace.Root,
                                           permissionSystemPaths : [storeDir.Root]));
        Assert.That(coveringError!.Message, Does.Contain("permission-system protected path"));

        // 落入方向：常设写根落在会话存储目录内部同样拒绝。
        var inside = settings with { AdditionalWriteRoots = [sessionStore] };
        var insideError = Assert.Throws<InvalidOperationException>(() =>
            WindowsSandboxComposer.Compose(inside, workspace.Root,
                                           permissionSystemPaths : [storeDir.Root]));
        Assert.That(insideError!.Message, Does.Contain("permission-system protected path"));
    }

    [Test]
    public void Compose_ConfiguredRuntimeRootOverlappingIntegrityOrPermissionSystemPath_IsRefused()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Windows sandbox composition requires Windows.");
        using var workspace = new TestTempDir();
        using var home      = new TestTempDir();
        using var storeDir  = new TestTempDir();
        var settings = new WindowsSandboxSettings
        {
            Enabled              = true,
            SetupExecutablePath  = home.WriteFile("setup.exe", "stub"),
            RunnerExecutablePath = home.WriteFile("runner.exe", "stub"),
            SandboxHome          = home.Root
        };

        // 用户显式配置的 temp 根落在沙箱 home 敏感子路径上：完整性集合重叠 → 拒绝组合
        // （运行时根位于 home 内是预期布局，完整性键是敏感子路径而非整 home）。
        var tempInHome = settings with { SandboxTempRoot = Path.Combine(home.Root, ".sandbox-bin") };
        var homeError = Assert.Throws<InvalidOperationException>(() =>
            WindowsSandboxComposer.Compose(tempInHome, workspace.Root));
        Assert.That(homeError!.Message, Does.Contain("sandbox integrity path"));

        // 用户显式配置的 temp 根恰好等于 home 根：更早的 resolver 防线（temp 根不得覆盖凭据
        // 目录）即拒绝，无需等组合器完整性环。
        var tempAtHomeRoot = settings with { SandboxTempRoot = home.Root };
        var homeRootError = Assert.Throws<ArgumentException>(() =>
            WindowsSandboxComposer.Compose(tempAtHomeRoot, workspace.Root));
        Assert.That(homeRootError!.Message, Does.Contain("credentials directory"));

        // 对照：显式配置到 home 内非敏感子路径的运行时根（与默认派生根同类布局）可以组合。
        var tempInPlainSubdir = settings with { SandboxTempRoot = Path.Combine(home.Root, "custom-tmp") };
        Assert.DoesNotThrow(() => WindowsSandboxComposer.Compose(tempInPlainSubdir, workspace.Root));

        // 用户显式配置的 temp 根覆盖会话存储目录：C1 实例重叠 → 拒绝组合。
        var tempOverStore = settings with { SandboxTempRoot = storeDir.Root };
        var storeError = Assert.Throws<InvalidOperationException>(() =>
            WindowsSandboxComposer.Compose(tempOverStore, workspace.Root,
                                           permissionSystemPaths : [storeDir.Root]));
        Assert.That(storeError!.Message, Does.Contain("permission-system protected path"));

        // 用户显式配置的 NuGet 缓存根（NUGET_PACKAGES）覆盖会话存储目录：同样拒绝。
        var nugetOverStore = settings with
        {
            ExtraEnvironment = new Dictionary<string, string> { ["NUGET_PACKAGES"] = storeDir.Root }
        };
        var nugetError = Assert.Throws<InvalidOperationException>(() =>
            WindowsSandboxComposer.Compose(nugetOverStore, workspace.Root,
                                           permissionSystemPaths : [storeDir.Root]));
        Assert.That(nugetError!.Message, Does.Contain("permission-system protected path"));

        // 对照：默认派生根（home\tmp、home\nuget-cache）不触发新校验，
        // 相同输入可以走到完整组合（真实文件占位齐备）。
        var execution = WindowsSandboxComposer.Compose(settings, workspace.Root,
                                                       permissionSystemPaths : [storeDir.Root]);
        Assert.That(execution, Is.Not.Null);
    }

    // ---- 守卫路径事实字段传播（B5 RequiresNetworkAccess 缺口闭合固化） ----

    [Test]
    public void GuardedShell_NetworkRequest_ReachesTheEngine_AsLevel2Ask()
    {
        using var dir    = new TestTempDir();
        var       state  = new WorkspaceAccessState(WorkspaceDirectoryIdentity.Capture(dir.Root));
        var tool = state.Guard(() => new ShellTool(dir.Workspace, executionPolicy : SandboxPolicy()));
        var engine = new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root));

        var preparation = tool.Prepare(Call(new
                                            {
                                                mode = "direct", executable = "cmd.exe", networkAccess = "enabled"
                                            }));

        // B5 修复前：守卫丢字段导致引擎按 L1 自动放行（审批旁路）；修复后守卫路径必须逐次审批。
        var evaluation = engine.Evaluate(preparation);
        Assert.Multiple(() =>
        {
            Assert.That(evaluation.Decision, Is.EqualTo(PermissionDecision.Ask));
            Assert.That(evaluation.Level, Is.EqualTo(PermissionLevel.Level2));
            Assert.That(evaluation.MissingCapabilities, Does.Contain("network-access"));
        });
    }

    [Test]
    public void GuardedShell_WriteRootRequest_PropagatesToTheEngine()
    {
        using var home = new SandboxTestHome();
        using var dir  = new TestTempDir();
        var       root = Path.Combine(OutsideRoot(), "guarded");
        var       state = new WorkspaceAccessState(WorkspaceDirectoryIdentity.Capture(dir.Root));
        var tool = state.Guard(() => SandboxStackTool(home, workspaceRoot : dir.Root));
        var engine = new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root));

        var preparation = tool.Prepare(Call(new
                                            {
                                                mode = "direct", executable = "cmd.exe",
                                                writeRoots = new[] { root }
                                            }));

        Assert.Multiple(() =>
        {
            Assert.That(preparation.RequestedWriteRoots, Is.EqualTo(new[] { root }));
            Assert.That(preparation.TargetPaths, Is.EqualTo(new[] { root }));
            Assert.That(preparation.ExecutionPolicy, Does.Contain("workspace-access="),
                        "守卫包装身份保留。");
        });
        var evaluation = engine.Evaluate(preparation);
        Assert.Multiple(() =>
        {
            Assert.That(evaluation.Decision, Is.EqualTo(PermissionDecision.Ask));
            Assert.That(evaluation.Level, Is.EqualTo(PermissionLevel.Level2));
            Assert.That(evaluation.MissingCapabilities, Does.Contain("additional-path-write"));
            Assert.That(evaluation.Reason, Does.Contain(root));
        });
    }

    // ---- 工具与夹具 ----

    private static string OutsideRoot() => Path.Combine(Path.GetTempPath(), "th-write-root-probe");

    private static TestTempDir OutsideDirectory() => new();

    private static ToolPreparation ShellPreparation(string? root = null, bool requiresNetwork = false,
                                                    string? constraint = null) =>
        ShellPreparation(root is null ? [] : new[] { root }, requiresNetwork, constraint);

    private static ToolPreparation ShellPreparation(IReadOnlyList<string> roots, bool requiresNetwork = false,
                                                    string? constraint = null) => new()
    {
        ToolName              = "shell",
        CallId                = "call_1",
        Arguments             = new JsonObject { ["mode"] = "direct" },
        Capability            = "process.execute",
        Summary               = "shell command",
        RequiresNetworkAccess = requiresNetwork,
        SessionConstraint     = constraint ?? "cmd-identity",
        RequestedWriteRoots   = roots,
        TargetPaths           = roots.Count > 0
            ? roots
            : [Path.Combine(Path.GetTempPath(), "th-write-root-cwd")]
    };

    private static ShellTool SandboxStackTool(SandboxTestHome home, string workspaceRoot,
                                              FakeProcessExecutionBackend? backend = null,
                                              IReadOnlyList<string>? permissionSystemPaths = null)
    {
        var policy = SandboxPolicyResolver.Resolve(SandboxPolicyKind.WorkspaceWrite,
                                                   home.Components.SandboxHome,
                                                   new Dictionary<string, string>
                                                   {
                                                       ["TEMP"] = home.Components.SandboxHome
                                                   });
        return new ShellTool(new Workspace(workspaceRoot), processBackend : backend ?? new FakeProcessExecutionBackend(),
                             executionPolicy : SandboxPolicy(), activeCapabilities : new FakeCapabilitySource(
                                 new ActiveCapabilitySnapshot([], [], [])),
                             grantContext : new SandboxGrantContext(policy, home.Components,
                                                                    permissionSystemPaths));
    }

    private static ProcessExecutionPolicy SandboxPolicy() => new()
    {
        Backend        = ProcessExecutionBackendKind.WindowsSandbox,
        DisplayName    = "windows-sandbox: workspace-write",
        PolicyIdentity = BaselineIdentity
    };

    private static ChatToolCall Call(object arguments) =>
        new(Guid.NewGuid().ToString("N"), "shell", JsonSerializer.Serialize(arguments));

    /// <summary>测试用活动快照来源：按构造返回固定快照。 Fixed-snapshot source for tests.</summary>
    private sealed class FakeCapabilitySource(ActiveCapabilitySnapshot snapshot) : IActiveCapabilitySource
    {
        public ActiveCapabilitySnapshot Snapshot() => snapshot;
    }

    /// <summary>
    ///     <para>按脚本回复的审批器；脚本耗尽时抛出，避免测试静默放行。</para>
    ///     <para>
    ///         An approver that replies from a script; exhausting the script throws so
    ///         tests never approve silently.
    ///     </para>
    /// </summary>
    private sealed class ScriptedApprover : IApprovalProvider
    {
        private readonly Queue<ApprovalResolution> _actions = new();

        public int Prompts { get; private set; }

        public Task<ApprovalResolution> PromptAsync(ApprovalRequest request, CancellationToken cancellationToken)
        {
            Prompts++;
            if (_actions.Count == 0) throw new InvalidOperationException("No scripted approval action left.");

            return Task.FromResult(_actions.Dequeue());
        }

        public void Enqueue(ApprovalResolution action) => _actions.Enqueue(action);
    }
}
