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
        return (new PermissionShellTool(restricted, host, () => SessionPermissionMode.ReadOnly), hostBackend,
                restrictedBackend);
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
        TestAssert.Throws<InvalidDataException>(() => tool.Prepare(ShellCall("echo one", "system", "x1")));
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
        var engine = new PermissionEngine(dir.Root);

        engine.GrantSession(tool.Prepare(ShellCall("dotnet test", "host", "g1")));

        Assert.That(engine.Decide(tool.Prepare(ShellCall("dotnet test", "host", "g2"))),
                    Is.EqualTo(PermissionDecision.Allow));
        Assert.That(engine.Decide(tool.Prepare(ShellCall("dotnet test", null, "g3"))),
                    Is.EqualTo(PermissionDecision.Ask));

        engine.GrantOnce(tool.Prepare(ShellCall("dotnet test", "host", "o1")));

        Assert.That(engine.Decide(tool.Prepare(ShellCall("dotnet test", null, "o2"))),
                    Is.EqualTo(PermissionDecision.Ask));
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
        approver.Enqueue(ApprovalAction.AllowOnce);
        approver.Enqueue(ApprovalAction.AllowOnce);

        var loop = new AgentLoop(client, new ToolRegistry([tool]), Options(),
                                 new PermissionEngine(dir.Root), approver);

        var result = await loop.RunAsync("sys", "run host command twice", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(approver.Prompts, Is.EqualTo(2));
        Assert.That(result.ToolExecutions, Is.EqualTo(2));
        Assert.That(approver.SeenPreparations, Has.Count.EqualTo(2));
        Assert.That(approver.SeenPreparations.All(preparation =>
                                                      preparation.Summary.Contains("host execution (no OS isolation)")),
                    Is.True);
        Assert.That(approver.SeenPreparations.All(preparation => preparation.ExecutionPolicy == "backend=host"),
                    Is.True);

        var execution = TestAssert.NotNull(hostBackend.ReceivedExecution);
        Assert.That(execution.Executable, Is.EqualTo(OperatingSystem.IsWindows() ? "sh" : "/bin/sh"));
        Assert.That(execution.Arguments.ToArray(), Is.EqualTo(new[] { "-c", "echo host-frozen-check" }));
        Assert.That(execution.WorkingDirectory, Is.EqualTo(dir.Root));
        Assert.That(restrictedBackend.ReceivedExecution, Is.Null);
    }

    /// <summary>
    ///     <para>按队列顺序返回审批动作，记录每次看到的准备计划。</para>
    ///     <para>Returns scripted approval actions in order and records each observed preparation.</para>
    /// </summary>
    private sealed class ScriptedApprover : IApprovalProvider
    {
        private readonly Queue<ApprovalAction> _actions = new();

        public int Prompts { get; private set; }

        public List<ToolPreparation> SeenPreparations { get; } = [];

        public Task<ApprovalAction> PromptAsync(ToolPreparation preparation, CancellationToken cancellationToken)
        {
            Prompts++;
            SeenPreparations.Add(preparation);
            return _actions.Count == 0
                ? throw new InvalidOperationException("No scripted approval action left.")
                : Task.FromResult(_actions.Dequeue());
        }

        public void Enqueue(ApprovalAction action)
        {
            _actions.Enqueue(action);
        }
    }
}
