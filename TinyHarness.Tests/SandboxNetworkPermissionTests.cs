using System.Text.Json.Nodes;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Agent;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         网络权限决策：沙箱工具申请联网时，常设信任未开启则一律逐次审批（硬拒绝、NeedsInspection
///         与 worker 保护仍优先，授权不得绕过该询问）；常设信任开启后回到各模式既有路由；
///         网络状态经授权约束行绑定，不得跨网络状态复用；用户拒绝与冻结执行计划的边界单独固化。
///     </para>
///     <para>
///         Network permission decisions: a sandboxed tool requesting network access
///         is always approved per call while standing trust is off (hard denials,
///         NeedsInspection, and worker protection still take precedence, and grants
///         cannot bypass the ask); with standing trust on, each mode's existing
///         routing applies; network state binds through the grant-constraint line
///         and never crosses between network states; user denial and the frozen
///         plan boundaries are pinned separately.
///     </para>
/// </summary>
public class SandboxNetworkPermissionTests
{
    private const string NetworkConstraintSuffix = "\nnetwork-access: enabled";

    // ---- 常设信任未开启：网络申请逐次审批 ----

    [TestCase(SessionPermissionMode.ConfirmEveryAction, PermissionDecision.Ask)]
    [TestCase(SessionPermissionMode.ReadOnly, PermissionDecision.Ask)]
    [TestCase(SessionPermissionMode.ProjectWrite, PermissionDecision.Ask)]
    [TestCase(SessionPermissionMode.FullAccess, PermissionDecision.Allow)]
    [TestCase(SessionPermissionMode.ReviewEveryAction, PermissionDecision.Ask)]
    [TestCase(SessionPermissionMode.ReadOnlyAutoReview, PermissionDecision.Ask)]
    [TestCase(SessionPermissionMode.ProjectWriteAutoReview, PermissionDecision.Ask)]
    public void NetworkRequiringCall_MatrixFollowsPresetWithStandingTrustOff(
        SessionPermissionMode mode, PermissionDecision expected)
    {
        using var root   = new TestTempDir();
        var       engine = new PermissionEngine(root.Root);
        engine.SetMode(mode);

        // 只读与项目内写入的自动放行不得静默携带联网；FullAccess 走宿主工具，先于本规则放行。
        Assert.That(engine.Decide(Preparation(root, "filesystem.read", requiresNetwork : true)),
                    Is.EqualTo(expected), mode.ToString());
        Assert.That(engine.Decide(Preparation(root, "filesystem.write", requiresNetwork : true)),
                    Is.EqualTo(expected), mode.ToString());
    }

    [Test]
    public void FullAccess_HostToolPathWithoutNetworkRequest_IsUnaffected()
    {
        using var root   = new TestTempDir();
        var       engine = new PermissionEngine(root.Root);
        engine.SetMode(SessionPermissionMode.FullAccess);

        Assert.That(engine.Decide(Preparation(root, "filesystem.write", requiresNetwork : false)),
                    Is.EqualTo(PermissionDecision.Allow));
    }

    // ---- 常设信任开启：回到各模式既有路由 ----

    [TestCase(SessionPermissionMode.ReadOnly, PermissionDecision.Ask)]
    [TestCase(SessionPermissionMode.ProjectWrite, PermissionDecision.Allow)]
    [TestCase(SessionPermissionMode.FullAccess, PermissionDecision.Allow)]
    public void StandingEnabled_NetworkRequestsFollowPresetRouting(SessionPermissionMode mode,
                                                                   PermissionDecision    writeExpected)
    {
        using var root   = new TestTempDir();
        var       engine = new PermissionEngine(root.Root, sandboxNetworkStandingEnabled : true);
        engine.SetMode(mode);

        Assert.That(engine.Decide(Preparation(root, "filesystem.write", requiresNetwork : true)),
                    Is.EqualTo(writeExpected), mode.ToString());
        Assert.That(engine.Decide(Preparation(root, "filesystem.read", requiresNetwork : true)),
                    Is.EqualTo(PermissionDecision.Allow));
    }

    // ---- 硬保护优先于网络询问 ----

    [Test]
    public void WorkerProtectionAndHardDeny_BeatNetworkRequirement()
    {
        using var root = new TestTempDir();
        var worker = new PermissionEngine(root.Root, denyNonReadOnlyCapabilities : true,
                                          sandboxNetworkStandingEnabled : true);
        Assert.That(worker.Decide(Preparation(root, "filesystem.write", requiresNetwork : true)),
                    Is.EqualTo(PermissionDecision.Deny));

        var engine = new PermissionEngine(root.Root, sandboxNetworkStandingEnabled : true);
        engine.SetNonReadOnlyBlock("unknown effects");
        Assert.That(engine.Decide(Preparation(root, "filesystem.write", requiresNetwork : true)),
                    Is.EqualTo(PermissionDecision.Deny));

        engine.SetNonReadOnlyBlock(null);
        engine.DenySession(Preparation(root, "filesystem.write", requiresNetwork : true));
        Assert.That(engine.Decide(Preparation(root, "filesystem.write", requiresNetwork : true)),
                    Is.EqualTo(PermissionDecision.Deny));
    }

    // ---- AllowOnce 网络批准单次消耗 ----

    [Test]
    public void AllowOnce_NetworkApprovalIsConsumedExactlyOnce()
    {
        using var root   = new TestTempDir();
        var       engine = new PermissionEngine(root.Root);
        var       call   = Preparation(root, "process.execute", requiresNetwork : true,
                                       grantConstraint : PolicyGrantConstraint(requiresNetwork : true));

        engine.GrantOnce(call);
        Assert.That(engine.Decide(call), Is.EqualTo(PermissionDecision.Allow));
        Assert.That(engine.TryConsumeOnce(call), Is.True);
        // 消耗后网络申请回到逐次审批，不因指纹相同而重新放行。
        Assert.That(engine.Decide(call), Is.EqualTo(PermissionDecision.Ask));
        Assert.That(engine.TryConsumeOnce(call), Is.False);
    }

    // ---- 授权约束绑定网络行 ----

    [Test]
    public void SessionGrant_CannotCrossBetweenNetworkStates()
    {
        using var root       = new TestTempDir();
        var networkedRequest = Preparation(root, "process.execute", requiresNetwork : true,
                                           grantConstraint : PolicyGrantConstraint(requiresNetwork : true));
        var plainRequest = Preparation(root, "process.execute", requiresNetwork : false,
                                       grantConstraint : PolicyGrantConstraint(requiresNetwork : false));

        // 授权按约束行精确匹配：网络行的批准不覆盖无网络的同命令。
        var engine = new PermissionEngine(root.Root);
        engine.GrantSession(networkedRequest);
        Assert.That(engine.Decide(networkedRequest), Is.EqualTo(PermissionDecision.Allow));
        Assert.That(engine.Decide(plainRequest), Is.EqualTo(PermissionDecision.Ask));

        // 反向亦然：无网络授权不得授权需要网络的同一命令。
        var fresh = new PermissionEngine(root.Root);
        fresh.GrantSession(plainRequest);
        Assert.That(fresh.Decide(plainRequest), Is.EqualTo(PermissionDecision.Allow));
        Assert.That(fresh.Decide(networkedRequest), Is.EqualTo(PermissionDecision.Ask));
    }

    // ---- 用户拒绝不执行；逐次批准单次消耗 ----

    [Test]
    public async Task UserDenial_KeepsNetworkedToolFromExecuting()
    {
        using var dir     = new TestTempDir();
        var       policy  = SandboxExecutionPolicy();
        var       backend = new FakeProcessExecutionBackend { ExitCode = 0, StdoutChunks = ["ok"] };
        var       client  = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("shell",
                                               new JsonObject
                                               {
                                                   ["mode"]          = "direct",
                                                   ["executable"]    = "dotnet",
                                                   ["arguments"]     = new JsonArray("--version"),
                                                   ["networkAccess"] = "enabled"
                                               }.ToJsonString()));
        client.Enqueue(FakeChatClient.Text("acknowledged"));

        var approver = new ScriptedApprover();
        approver.Enqueue(ApprovalAction.Deny);
        var tools = new ToolRegistry([
            new ShellTool(dir.Workspace, 30, processBackend : backend, executionPolicy : policy)
        ]);
        var loop = new AgentLoop(client, tools, new AgentOptions
                                 {
                                     Model                     = "test-model",
                                     MaxAgentSteps             = 5,
                                     DefaultToolTimeoutSeconds = 30
                                 },
                                 new PermissionEngine(dir.Root), approver);

        var result = await loop.RunAsync("sys", "check version online", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(approver.Prompts, Is.EqualTo(1));
        // 用户拒绝后不得有任何进程启动。
        Assert.That(backend.ReceivedExecution, Is.Null);
    }

    [Test]
    public async Task AllowOnce_NetworkApprovalIsConsumedPerCall()
    {
        using var dir     = new TestTempDir();
        var       policy  = SandboxExecutionPolicy();
        var       backend = new FakeProcessExecutionBackend { ExitCode = 0, StdoutChunks = ["ok"] };
        var       client  = new FakeChatClient();
        var       call    = ShellToolCallJson(enabled : true);
        // 两个连续的联网工具调用：各自 finish 为 tool_calls，随后文本收尾。
        client.Enqueue(FakeChatClient.ToolCall("shell", call));
        client.Enqueue(FakeChatClient.ToolCall("shell", call));
        client.Enqueue(FakeChatClient.Text("both done"));

        var approver = new ScriptedApprover();
        approver.Enqueue(ApprovalAction.AllowOnce);
        approver.Enqueue(ApprovalAction.AllowOnce);
        var tools = new ToolRegistry([
            new ShellTool(dir.Workspace, 30, processBackend : backend, executionPolicy : policy)
        ]);
        var loop = new AgentLoop(client, tools, new AgentOptions
                                 {
                                     Model                     = "test-model",
                                     MaxAgentSteps             = 6,
                                     DefaultToolTimeoutSeconds = 30
                                 },
                                 new PermissionEngine(dir.Root), approver);

        var result = await loop.RunAsync("sys", "check version twice", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        // 完全相同的联网命令也必须逐次询问：一次批准只覆盖一次尝试。
        Assert.That(approver.Prompts, Is.EqualTo(2));
        Assert.That(backend.ReceivedExecution, Is.Not.Null);
    }

    // ---- shell 工具参数与冻结 ----

    [Test]
    public void ShellTool_NetworkAccessEnabled_BindsFlagSummaryAndGrantConstraint()
    {
        using var dir = new TestTempDir();
        var tool = new ShellTool(dir.Workspace, executionPolicy : SandboxExecutionPolicy());

        var preparation = tool.Prepare(ShellToolCall(enabled : true));

        Assert.That(preparation.RequiresNetworkAccess, Is.True);
        var plan = TestAssert.TypeOf<PreparedProcessExecution>(preparation.ExecutionPlan);
        Assert.That(plan.NetworkAccessEnabled, Is.True);
        // 摘要以联网前缀开头；授权约束携带网络行。
        Assert.That(preparation.Summary, Does.StartWith(ShellTool.NetworkSummaryPrefix));
        TestAssert.NotNull(preparation.SessionGrantConstraint);
        TestAssert.Contains("network-access: enabled", preparation.SessionGrantConstraint!, StringComparison.Ordinal);
    }

    [Test]
    public void ShellTool_DefaultNetworkAccess_KeepsLegacyShape()
    {
        using var dir = new TestTempDir();
        var tool = new ShellTool(dir.Workspace, executionPolicy : SandboxExecutionPolicy());

        var preparation = tool.Prepare(ShellToolCall(enabled : false));

        Assert.That(preparation.RequiresNetworkAccess, Is.False);
        var plan = TestAssert.TypeOf<PreparedProcessExecution>(preparation.ExecutionPlan);
        Assert.That(plan.NetworkAccessEnabled, Is.False);
        TestAssert.DoesNotContain("[联网]", preparation.Summary, StringComparison.Ordinal);
        TestAssert.DoesNotContain("network-access:", preparation.SessionGrantConstraint!,
                                  StringComparison.Ordinal);
    }

    [Test]
    public void ShellTool_RejectsUnknownNetworkAccessValue()
    {
        using var dir = new TestTempDir();
        var tool = new ShellTool(dir.Workspace, executionPolicy : SandboxExecutionPolicy());
        var call = new ChatToolCall("call_1", "shell",
                                    new JsonObject
                                    {
                                        ["mode"]          = "direct",
                                        ["executable"]    = "dotnet",
                                        ["arguments"]     = new JsonArray("--version"),
                                        ["networkAccess"] = "sometimes"
                                    }.ToJsonString());

        Assert.Throws<InvalidDataException>(() => tool.Prepare(call));
    }

    [TestCase("true")]
    [TestCase("1")]
    [TestCase("[\"default\"]")]
    [TestCase("{\"nested\":true}")]
    public void ShellTool_NetworkAccessNonStringValue_IsRejectedWithLegalValues(string json)
    {
        using var dir = new TestTempDir();
        var tool = new ShellTool(dir.Workspace, executionPolicy : SandboxExecutionPolicy());
        var call = new ChatToolCall("call_1", "shell",
                                    new JsonObject
                                    {
                                        ["mode"]          = "direct",
                                        ["executable"]    = "dotnet",
                                        ["arguments"]     = new JsonArray("--version"),
                                        ["networkAccess"] = JsonNode.Parse(json)
                                    }.ToJsonString());

        var error = Assert.Throws<InvalidDataException>(() => tool.Prepare(call));
        // 文案必须列出合法取值，供模型修正调用。
        TestAssert.Contains("'default' or 'enabled'", error!.Message, StringComparison.Ordinal);
    }

    [Test]
    public void ShellTool_ExplicitNullNetworkAccess_IsTreatedAsNotProvided()
    {
        using var dir = new TestTempDir();
        var tool = new ShellTool(dir.Workspace, executionPolicy : SandboxExecutionPolicy());
        var args = new JsonObject
        {
            ["mode"]          = "direct",
            ["executable"]    = "dotnet",
            ["arguments"]     = new JsonArray("--version"),
            ["networkAccess"] = null
        };

        // 显式 null 与缺失同义：视为未提供，保持 default 行为。
        var preparation = tool.Prepare(new ChatToolCall("call_1", "shell", args.ToJsonString()));

        Assert.That(preparation.RequiresNetworkAccess, Is.False);
        Assert.That(((PreparedProcessExecution)preparation.ExecutionPlan!).NetworkAccessEnabled, Is.False);
    }

    [Test]
    public void FrozenPlan_NetworkFlagSurvivesArgumentTampering()
    {
        using var dir = new TestTempDir();
        var tool = new ShellTool(dir.Workspace, executionPolicy : SandboxExecutionPolicy());

        var preparation = tool.Prepare(ShellToolCall(enabled : true));
        Assert.That(((PreparedProcessExecution)preparation.ExecutionPlan!).NetworkAccessEnabled, Is.True);

        // 审批后改写参数副本不得改写冻结计划或准备计划声明的网络需求。
        preparation.Arguments["networkAccess"] = "default";
        Assert.That(preparation.RequiresNetworkAccess, Is.True);
        Assert.That(((PreparedProcessExecution)preparation.ExecutionPlan!).NetworkAccessEnabled, Is.True);
    }

    // ---- 工具 ----

    private static ProcessExecutionPolicy SandboxExecutionPolicy(string? identity = null) => new()
    {
        Backend        = ProcessExecutionBackendKind.WindowsSandbox,
        DisplayName    = "windows-sandbox: workspace-write, network: restricted",
        PolicyIdentity = identity ?? "backend=windows-sandbox\npolicyVersion=1\nkind=workspace-write"
    };

    /// <summary>
    ///     沙箱 shell 工具的授权约束形态：命令身份加执行策略身份，网络行仅在申请联网时追加。
    ///     The sandboxed shell tool's grant-constraint shape: command identity plus
    ///     execution-policy identity, with the network line appended only when requested.
    /// </summary>
    private static string PolicyGrantConstraint(bool requiresNetwork) =>
        "cmd-identity\nexecution-policy: backend=windows-sandbox" +
        (requiresNetwork ? NetworkConstraintSuffix : string.Empty);

    private static ToolPreparation Preparation(TestTempDir root, string capability, bool requiresNetwork,
                                               string? grantConstraint = null) => new()
    {
        ToolName               = "shell",
        CallId                 = "call_1",
        Arguments              = new JsonObject { ["networkAccess"] = requiresNetwork ? "enabled" : "default" },
        Capability             = capability,
        Summary                = "networked command",
        RequiresNetworkAccess  = requiresNetwork,
        SessionConstraint      = "cmd-identity",
        SessionGrantConstraint = grantConstraint,
        TargetPaths            = [root.Root]
    };

    private static ChatToolCall ShellToolCall(bool enabled) =>
        new("call_1", "shell", ShellToolCallJson(enabled));

    private static string ShellToolCallJson(bool enabled) =>
        new JsonObject
        {
            ["mode"]          = "direct",
            ["executable"]    = "dotnet",
            ["arguments"]     = new JsonArray("--version"),
            ["networkAccess"] = enabled ? "enabled" : "default"
        }.ToJsonString();

    /// <summary>
    ///     <para>按脚本回复的审批器；脚本耗尽时抛出，避免测试静默放行。</para>
    ///     <para>
    ///         An approver that replies from a script; exhausting the script throws so
    ///         tests never approve silently.
    ///     </para>
    /// </summary>
    private sealed class ScriptedApprover : IApprovalProvider
    {
        private readonly Queue<ApprovalAction> _actions = new();

        public int Prompts { get; private set; }

        public Task<ApprovalAction> PromptAsync(ToolPreparation preparation, CancellationToken cancellationToken)
        {
            Prompts++;
            if (_actions.Count == 0) throw new InvalidOperationException("No scripted approval action left.");

            return Task.FromResult(_actions.Dequeue());
        }

        public void Enqueue(ApprovalAction action) => _actions.Enqueue(action);
    }
}
