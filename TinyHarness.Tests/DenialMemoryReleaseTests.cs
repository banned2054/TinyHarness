using System.Text.Json.Nodes;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Agent;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         拒绝记忆可信解除（B6-3）：ReleaseDenialForApproval 只解除匹配绑定操作（指纹+命令身份规则）、
///         ReleaseAllDenials 全清并返回条数、任意新消息/其他操作不清空既有拒绝（负例）、批准挂点休眠
///         语义（批准一个操作不得误清其他粘滞拒绝——迁移确认的整段解除归宿主流程，已在授权审计接线
///         测试覆盖）。
///     </para>
///     <para>
///         Trusted denial-memory release (B6-3): ReleaseDenialForApproval releases only the matching bound
///         operation (fingerprint plus command-identity rules), ReleaseAllDenials clears everything and
///         returns the count, new messages and other operations never clear existing denials (negative), and
///         the approval-hook dormancy semantics (approving one operation must not clear other sticky denials —
///         the wholesale migration-confirmation release belongs to the host flow, covered by the audit wiring
///         tests).
///     </para>
/// </summary>
public class DenialMemoryReleaseTests
{
    private static ToolPreparation Preparation(string callId, string constraint, string target) => new()
    {
        ToolName          = "apply_patch",
        CallId            = callId,
        Arguments         = new JsonObject { ["patch"] = $"patch-{callId}" },
        Capability        = "filesystem.write",
        Summary           = $"operation {callId}",
        SessionConstraint = constraint,
        TargetPaths       = [target]
    };

    [Test]
    public void ReleaseDenialForApproval_ReleasesOnlyTheMatchingBoundOperation()
    {
        using var dir     = new TestTempDir();
        var       outside = Path.GetDirectoryName(dir.Root)!;
        var       engine  = new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root));
        var first  = Preparation("call_1", "cmd-identity-1", Path.Combine(outside, "first.txt"));
        var second = Preparation("call_2", "cmd-identity-2", Path.Combine(outside, "second.txt"));
        engine.RecordDenial(first);
        engine.RecordDenial(second);
        Assert.Multiple(() =>
        {
            Assert.That(engine.Evaluate(first).Decision, Is.EqualTo(PermissionDecision.Deny));
            Assert.That(engine.Evaluate(second).Decision, Is.EqualTo(PermissionDecision.Deny));
        });

        // 精确解除：只移除匹配绑定（指纹+命令身份规则）的拒绝；另一条粘滞拒绝必须保留。
        Assert.That(engine.ReleaseDenialForApproval(first), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(engine.Evaluate(first).Decision, Is.EqualTo(PermissionDecision.Ask),
                        "解除后同一操作回到逐次审批。");
            Assert.That(engine.Evaluate(second).Decision, Is.EqualTo(PermissionDecision.Deny),
                        "批准一个操作不得误清其他粘滞拒绝。");
        });

        // 解除不存在的绑定返回 false，不改变任何状态。
        var third = Preparation("call_3", "cmd-identity-3", Path.Combine(outside, "third.txt"));
        Assert.That(engine.ReleaseDenialForApproval(third), Is.False);
        Assert.That(engine.Evaluate(second).Decision, Is.EqualTo(PermissionDecision.Deny));
    }

    [Test]
    public void ReleaseAllDenials_ClearsEverythingAndReturnsTheCount()
    {
        using var dir     = new TestTempDir();
        var       outside = Path.GetDirectoryName(dir.Root)!;
        var       engine  = new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root));
        var first  = Preparation("call_1", "cmd-identity-1", Path.Combine(outside, "first.txt"));
        var second = Preparation("call_2", "cmd-identity-2", Path.Combine(outside, "second.txt"));
        engine.RecordDenial(first);
        engine.RecordDenial(second);

        Assert.Multiple(() =>
        {
            Assert.That(engine.ReleaseAllDenials(), Is.GreaterThanOrEqualTo(2), "返回解除条数供审计。");
            Assert.That(engine.Evaluate(first).Decision, Is.EqualTo(PermissionDecision.Ask));
            Assert.That(engine.Evaluate(second).Decision, Is.EqualTo(PermissionDecision.Ask));
            Assert.That(engine.ReleaseAllDenials(), Is.EqualTo(0), "空态解除返回 0。");
        });
    }

    [Test]
    public void NewMessagesAndUnrelatedOperations_NeverClearExistingDenials()
    {
        using var dir     = new TestTempDir();
        var       outside = Path.GetDirectoryName(dir.Root)!;
        var       engine  = new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root));
        var denied = Preparation("call_1", "cmd-identity-1", Path.Combine(outside, "denied.txt"));
        engine.RecordDenial(denied);

        // 任意新消息与其他调用求值/授权活动都不得清空拒绝记忆（契约负例）。
        for (var turn = 0; turn < 3; turn++)
        {
            var unrelated = Preparation($"other_{turn}", $"other-identity-{turn}",
                                        Path.Combine(outside, $"other-{turn}.txt"));
            Assert.Multiple(() =>
            {
                engine.Evaluate(unrelated);
                engine.Grants.Add(GrantScope.Turn, unrelated);
                engine.RevokeGrants();
                Assert.That(engine.Evaluate(denied).Decision, Is.EqualTo(PermissionDecision.Deny),
                            $"第 {turn + 1} 条新消息后拒绝记忆必须仍然拦截。");
            });
        }
    }

    [Test]
    public async Task ApprovingOneOperation_KeepsOtherStickyDenialsInTheLoopPath()
    {
        using var dir     = new TestTempDir();
        var       outside = Path.GetDirectoryName(dir.Root)!;
        var       engine  = new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root));
        var sticky = Preparation("sticky", "sticky-identity", Path.Combine(outside, "sticky.txt"));
        engine.RecordDenial(sticky);

        // 批准挂点休眠语义：批准另一个操作走完整审批→执行链，粘滞拒绝不得被误清。
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("probe", "{}"));
        client.Enqueue(FakeChatClient.Text("done"));
        var probe = new ApprovableProbeTool(Path.Combine(outside, "probe.txt"));
        var approver = new ScriptedApprover();
        approver.Enqueue(ApprovalResolution.Granted(GrantScope.SingleOperation));
        var loop = new AgentLoop(client, new ToolRegistry([probe]), Options(), engine, approver);

        var result = await loop.RunAsync("sys", "approve the probe", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
            Assert.That(probe.ExecuteCount, Is.EqualTo(1));
            Assert.That(engine.Evaluate(sticky).Decision, Is.EqualTo(PermissionDecision.Deny),
                        "批准挂点只解除被批准操作的绑定，其他粘滞拒绝保留。");
        });
    }

    private static AgentOptions Options() => new()
    {
        Model = "test-model", MaxAgentSteps = 6, DefaultToolTimeoutSeconds = 30
    };

    /// <summary>工作区外写入探针：filesystem.write → 引擎按 L2 逐次审批。</summary>
    private sealed class ApprovableProbeTool(string target) : ITool
    {
        public int ExecuteCount { get; private set; }

        public ToolDefinition Definition { get; } = new()
        {
            Name        = "probe",
            Description = "Outside write probe for the approval hook.",
            Parameters  = new JsonObject { ["type"] = "object" }
        };

        public ToolPreparation Prepare(ChatToolCall call) => new()
        {
            ToolName    = Definition.Name,
            CallId      = call.Id,
            Arguments   = new JsonObject(),
            Capability  = "filesystem.write",
            Summary     = "probe write",
            TargetPaths = [target]
        };

        public Task<ToolResult> ExecuteAsync(ToolPreparation preparation, CancellationToken cancellationToken)
        {
            ExecuteCount++;
            return Task.FromResult(new ToolResult { Succeeded = true, Content = "written" });
        }
    }

    /// <summary>按脚本回复的审批器；脚本耗尽时抛出，避免测试静默放行。</summary>
    private sealed class ScriptedApprover : IApprovalProvider
    {
        private readonly Queue<ApprovalResolution> _actions = new();

        public Task<ApprovalResolution> PromptAsync(ApprovalRequest request, CancellationToken cancellationToken)
        {
            if (_actions.Count == 0) throw new InvalidOperationException("No scripted approval action left.");

            return Task.FromResult(_actions.Dequeue());
        }

        public void Enqueue(ApprovalResolution action) => _actions.Enqueue(action);
    }
}
