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
///         验证 PermissionShellTool 的执行范围冻结与授权隔离：scope=host 绑定宿主后端与
///         backend=host 策略身份，授权不能跨范围或跨后端复用，交互批准只覆盖一次尝试。
///     </para>
///     <para>
///         Verifies PermissionShellTool execution-scope freezing and grant isolation: scope=host binds the host
///         backend with the backend=host policy identity, grants never cross scopes or backends, and an
///         interactive approval covers exactly one attempt.
///     </para>
/// </summary>
public class PermissionShellToolTests
{
    private const string RestrictedPolicyIdentity = "backend=windows-sandbox\npolicyVersion=1\nkind=workspace-write";

    private static AgentOptions Options() => new()
    {
        Model                     = "test-model",
        MaxAgentSteps             = 6,
        DefaultToolTimeoutSeconds = 30
    };

    /// <summary>
    ///     <para>
    ///         构造同一 PermissionShellTool 下的受限与宿主两种形态：独立假后端与互异的策略身份，
    ///         使测试能区分被冻结的后端选择。
    ///     </para>
    ///     <para>
    ///         Builds the restricted and host shapes behind one PermissionShellTool with separate fake backends
    ///         and distinct policy identities, so the frozen backend choice is observable.
    ///     </para>
    /// </summary>
    private static (PermissionShellTool Tool, FakeProcessExecutionBackend HostBackend,
        FakeProcessExecutionBackend RestrictedBackend) Tools(TestTempDir dir)
    {
        var hostBackend       = new FakeProcessExecutionBackend { ExitCode = 0, StdoutChunks = ["host-ok"] };
        var restrictedBackend = new FakeProcessExecutionBackend { ExitCode = 0, StdoutChunks = ["restricted-ok"] };
        var restricted = new ShellTool(dir.Workspace, 30,
                                       artifactRoot : Path.Combine(dir.Root, "output", "restricted"),
                                       processBackend : restrictedBackend,
                                       executionPolicy : new ProcessExecutionPolicy
                                       {
                                           Backend        = ProcessExecutionBackendKind.WindowsSandbox,
                                           DisplayName    = "windows-sandbox: workspace-write",
                                           PolicyIdentity = RestrictedPolicyIdentity
                                       });
        var host = new ShellTool(dir.Workspace, 30,
                                 artifactRoot : Path.Combine(dir.Root, "output", "host"),
                                 processBackend : hostBackend,
                                 executionPolicy : ProcessExecutionPolicy.Host());
        return (new PermissionShellTool(restricted, host), hostBackend, restrictedBackend);
    }

    /// <summary>
    ///     <para>
    ///         构造无冻结策略身份的受限 Shell（沙箱未组合形态），验证 workspace 路由明确失败。
    ///     </para>
    ///     <para>
    ///         Builds the restricted shell without a frozen policy identity (the sandbox-not-composed
    ///         shape) to verify the workspace route fails explicitly.
    ///     </para>
    /// </summary>
    private static PermissionShellTool ToolWithoutSandboxIdentity(TestTempDir dir)
    {
        var host = new ShellTool(dir.Workspace, 30,
                                 artifactRoot : Path.Combine(dir.Root, "output", "host"),
                                 executionPolicy : ProcessExecutionPolicy.Host());
        var restricted = new ShellTool(dir.Workspace, 30,
                                       artifactRoot : Path.Combine(dir.Root, "output", "restricted"));
        return new PermissionShellTool(restricted, host);
    }

    private static ChatToolCall ShellCall(string command, string? executionScope, string id) =>
        new(id, "shell", ScopeArgs(command, executionScope).ToJsonString());

    private static string HostShellArgs(string command, string id) =>
        new ChatToolCall(id, "shell", ScopeArgs(command, "host").ToJsonString()).ArgumentsJson;

    private static JsonObject ScopeArgs(string command, string? executionScope)
    {
        var args = new JsonObject
        {
            ["mode"]    = "shell",
            ["shell"]   = "sh",
            ["command"] = command
        };
        if (executionScope is not null) args["executionScope"] = executionScope;
        return args;
    }

    [Test]
    public void Prepare_BindsScopeToPolicyIdentityAndRejectsUnknownScope()
    {
        using var dir = new TestTempDir();
        var (tool, _, _) = Tools(dir);

        var host      = tool.Prepare(ShellCall("echo one", "host", "h1"));
        var workspace = tool.Prepare(ShellCall("echo one", null, "w1"));

        Assert.That(host.ExecutionPolicy, Is.EqualTo("backend=host"));
        Assert.That(workspace.ExecutionPolicy, Is.EqualTo(RestrictedPolicyIdentity));
        // 路由只由 executionScope 决定：显式 host 申请标记 RequestedBackend，缺省保持 null。
        Assert.That(host.RequestedBackend, Is.EqualTo(ProcessExecutionBackendKind.Host));
        Assert.That(workspace.RequestedBackend, Is.Null);
        TestAssert.Throws<InvalidDataException>(() => tool.Prepare(ShellCall("echo one", "system", "x1")));
    }

    [Test]
    public void Prepare_WorkspaceRouteWithoutFrozenPolicyIdentity_FailsExplicitlyWithoutHostFallback()
    {
        using var dir  = new TestTempDir();
        var       tool = ToolWithoutSandboxIdentity(dir);

        // 沙箱未组合：workspace 路由无冻结策略身份即失败，绝不静默回退宿主；host 路由不受影响。
        var ex = TestAssert.Throws<InvalidDataException>(() =>
                                                             tool.Prepare(ShellCall("echo one", null, "w1")));
        Assert.That(ex.Message, Does.Contain("no frozen execution policy"));

        var host = tool.Prepare(ShellCall("echo one", "host", "h1"));
        Assert.That(host.ExecutionPolicy, Is.EqualTo("backend=host"));
        Assert.That(host.RequestedBackend, Is.EqualTo(ProcessExecutionBackendKind.Host));
    }

    [Test]
    public async Task Execute_RoutesToTheBackendFrozenAtPrepareTime()
    {
        using var dir = new TestTempDir();
        var (tool, hostBackend, restrictedBackend) = Tools(dir);
        var workspacePreparation = tool.Prepare(ShellCall("echo workspace", null, "w1"));
        var hostPreparation      = tool.Prepare(ShellCall("echo host", "host", "h1"));

        var workspaceResult = await tool.ExecuteAsync(workspacePreparation, CancellationToken.None);

        Assert.That(workspaceResult.Succeeded, Is.True, workspaceResult.Content);
        Assert.That(TestAssert.TypeOf<PreparedProcessExecution>(restrictedBackend.ReceivedExecution).Executable,
                    Is.EqualTo(OperatingSystem.IsWindows() ? "sh" : "/bin/sh"));
        Assert.That(hostBackend.ReceivedExecution, Is.Null);

        var hostResult = await tool.ExecuteAsync(hostPreparation, CancellationToken.None);

        Assert.That(hostResult.Succeeded, Is.True, hostResult.Content);
        Assert.That(hostBackend.ReceivedExecution, Is.Not.Null);
        Assert.That(hostBackend.ReceivedExecution!.Executable,
                    Is.EqualTo(OperatingSystem.IsWindows() ? "sh" : "/bin/sh"));
    }

    [Test]
    public void HostScopeGrant_DoesNotAuthorizeWorkspaceScopeForTheSameCommand()
    {
        using var dir = new TestTempDir();
        var (tool, _, _) = Tools(dir);
        var engine = new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root));

        var host      = tool.Prepare(ShellCall("dotnet test", "host", "g1"));
        var workspace = tool.Prepare(ShellCall("dotnet test", null, "g2"));
        engine.Grants.Add(GrantScope.SingleOperation, host);

        Assert.That(engine.Evaluate(host).Decision, Is.EqualTo(PermissionDecision.Allow));
        Assert.That(engine.Grants.Peek(workspace), Is.Null);
        Assert.That(engine.Evaluate(workspace).Level, Is.EqualTo(PermissionLevel.Level1));
        Assert.That(engine.ConsumeGrant(host), Is.Not.Null);
        Assert.That(engine.Evaluate(host).Decision, Is.EqualTo(PermissionDecision.Ask));
    }

    [Test]
    public async Task AllowOnce_HostScopeShell_CoversOneAttemptAndExecutesTheFrozenPlan()
    {
        using var dir = new TestTempDir();
        var (tool, hostBackend, restrictedBackend) = Tools(dir);
        var client = new FakeChatClient();
        // 两轮发起完全相同的 host 调用：call id 不参与指纹，AllowOnce 若入引擎将放行第二次。
        client.Enqueue(FakeChatClient.ToolCall("shell", HostShellArgs("echo host-frozen-check", "p1")));
        client.Enqueue(FakeChatClient.ToolCall("shell", HostShellArgs("echo host-frozen-check", "p2")));
        client.Enqueue(FakeChatClient.Text("done"));

        var approver = new ScriptedApprover();
        approver.Enqueue(ApprovalResolution.Granted(GrantScope.SingleOperation));
        approver.Enqueue(ApprovalResolution.Granted(GrantScope.SingleOperation));

        var loop = new AgentLoop(client, new ToolRegistry([tool]), Options(),
                                 new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root)),
                                 approver);

        var result = await loop.RunAsync("sys", "run host command twice", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(approver.Prompts, Is.EqualTo(2));
        Assert.That(result.ToolExecutions, Is.EqualTo(2));
        Assert.That(approver.SeenRequests, Has.Count.EqualTo(2));
        Assert.That(approver.SeenRequests.All(request =>
                                                  request.Preparation.Summary
                                                         .Contains("host execution (no OS isolation)")),
                    Is.True);
        Assert.That(approver.SeenRequests.All(request => request.Preparation.ExecutionPolicy == "backend=host"),
                    Is.True);
        // 显式 host 申请必须以 RequestedBackend=Host 到达审批人与引擎（Level 3 判定依据）。
        Assert.That(approver.SeenRequests.All(request => request.Preparation.RequestedBackend ==
                                                         ProcessExecutionBackendKind.Host),
                    Is.True);

        var execution = TestAssert.NotNull(hostBackend.ReceivedExecution);
        Assert.That(execution.Executable, Is.EqualTo(OperatingSystem.IsWindows() ? "sh" : "/bin/sh"));
        Assert.That(execution.Arguments.ToArray(), Is.EqualTo(new[] { "-c", "echo host-frozen-check" }));
        Assert.That(execution.WorkingDirectory, Is.EqualTo(dir.Root));
        Assert.That(restrictedBackend.ReceivedExecution, Is.Null);
    }

    [TestCase(GrantScope.Turn)]
    [TestCase(GrantScope.Session)]
    public void HostScopeRejectsReusableGrants(GrantScope scope)
    {
        using var dir = new TestTempDir();
        var (tool, _, _) = Tools(dir);
        var engine = new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root));
        var plan   = tool.Prepare(ShellCall("dotnet test", "host", "host"));
        engine.Grants.Add(scope, plan);

        Assert.That(engine.Evaluate(plan).Decision, Is.EqualTo(PermissionDecision.Ask));
        Assert.That(engine.ConsumeGrant(plan), Is.Null);
    }

    /// <summary>
    ///     <para>按队列顺序返回审批结论，记录每次看到的审批请求。</para>
    ///     <para>Returns scripted approval resolutions in order and records each observed request.</para>
    /// </summary>
    private sealed class ScriptedApprover : IApprovalProvider
    {
        private readonly Queue<ApprovalResolution> _actions = new();

        public int Prompts { get; private set; }

        public List<ApprovalRequest> SeenRequests { get; } = [];

        public Task<ApprovalResolution> PromptAsync(ApprovalRequest request, CancellationToken cancellationToken)
        {
            Prompts++;
            SeenRequests.Add(request);
            return _actions.Count == 0
                ? throw new InvalidOperationException("No scripted approval action left.")
                : Task.FromResult(_actions.Dequeue());
        }

        public void Enqueue(ApprovalResolution action)
        {
            _actions.Enqueue(action);
        }
    }
}
