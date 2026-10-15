using System.Text.Json.Nodes;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Agent;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

public class AgentLoopPermissionTests
{
    private static AgentOptions Options() => new()
    {
        Model                     = "test-model",
        MaxAgentSteps             = 10,
        DefaultToolTimeoutSeconds = 30
    };

    private static string PatchArgs(string patch) => new JsonObject { ["patch"] = patch }.ToJsonString();

    private static ApplyPatchTool PatchTool(TestTempDir dir) => new(dir.Workspace);

    [Test]
    public async Task SessionGrant_AutoApprovesLaterWritesToSameFile()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("fixme.cs", "line1\nline2\nline3\n");

        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("apply_patch", PatchArgs(ReplacePatch(2)), "p1"));
        client.Enqueue(FakeChatClient.ToolCall("apply_patch", PatchArgs(ReplacePatch(3)), "p2"));
        client.Enqueue(FakeChatClient.Text("both patches applied"));

        var approver = new ScriptedApprover();
        approver.Enqueue(ApprovalAction.AllowSession);

        var loop = new AgentLoop(client, new ToolRegistry([PatchTool(dir)]), Options(),
                                 new PermissionEngine(dir.Root), approver);

        var result = await loop.RunAsync("sys", "patch twice", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(approver.Prompts, Is.EqualTo(1)); // 第二次写入由会话级授权自动批准
        Assert.That(await File.ReadAllTextAsync(Path.Combine(dir.Root, "fixme.cs")),
                    Is.EqualTo("line1\nline2-fixed\nline3-fixed\n"));
    }

    [Test]
    public async Task Deny_ReturnsDenialToModelAndWritesNothing()
    {
        using var dir  = new TestTempDir();
        var       path = dir.WriteFile("fixme.cs", "line1\nline2\nline3\n");

        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("apply_patch", PatchArgs(ReplacePatch(2)), "p1"));
        client.Enqueue(FakeChatClient.Text("understood"));

        var approver = new ScriptedApprover();
        approver.Enqueue(ApprovalAction.Deny);

        var loop = new AgentLoop(client, new ToolRegistry([PatchTool(dir)]), Options(),
                                 new PermissionEngine(dir.Root), approver);

        var result = await loop.RunAsync("sys", "patch", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(approver.Prompts, Is.EqualTo(1));
        Assert.That(result.ToolExecutions, Is.EqualTo(0));
        Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo("line1\nline2\nline3\n")); // 文件未被改动
        TestAssert.ContainsAll(m => m.Role == ChatRole.Tool && m.Content.Contains("Permission denied by user"),
                               loop.History);
    }

    [Test]
    public async Task SessionDeny_BlocksAMultiFilePatchDespiteSessionGrant()
    {
        using var dir           = new TestTempDir();
        var       protectedPath = dir.WriteFile("fixme.cs", "line1\nline2\nline3\n");
        var       otherPath     = dir.WriteFile("other.txt", "old\n");
        var       tool          = PatchTool(dir);
        var       patch         = ReplacePatch(2) + "\n--- a/other.txt\n+++ b/other.txt\n@@ -1 +1 @@\n-old\n+new\n";
        var       engine        = new PermissionEngine(dir.Root);
        engine.GrantSession(tool.Prepare(new ChatToolCall("grant", "apply_patch", PatchArgs(patch))));
        engine.DenySession(tool.Prepare(new ChatToolCall("deny", "apply_patch", PatchArgs(ReplacePatch(2)))));
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("apply_patch", PatchArgs(patch)));
        client.Enqueue(FakeChatClient.Text("understood"));
        var approver = new ScriptedApprover();
        var loop     = new AgentLoop(client, new ToolRegistry([tool]), Options(), engine, approver);

        var result = await loop.RunAsync("sys", "patch both", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(approver.Prompts, Is.EqualTo(0));
        Assert.That(result.ToolExecutions, Is.EqualTo(0));
        Assert.That(await File.ReadAllTextAsync(protectedPath), Is.EqualTo("line1\nline2\nline3\n"));
        Assert.That(await File.ReadAllTextAsync(otherPath), Is.EqualTo("old\n"));
        TestAssert.ContainsAll(message => message.Role == ChatRole.Tool &&
                                          message.Content.Contains("Permission denied"), loop.History);
    }

    [Test]
    public async Task Deny_CoversOneAttempt_IdenticalCallPromptsAgain()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("fixme.cs", "line1\nline2\nline3\n");

        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("apply_patch", PatchArgs(ReplacePatch(2)), "p1"));
        client.Enqueue(FakeChatClient.ToolCall("apply_patch", PatchArgs(ReplacePatch(2)), "p2"));
        client.Enqueue(FakeChatClient.Text("done"));

        var approver = new ScriptedApprover();
        approver.Enqueue(ApprovalAction.Deny);
        approver.Enqueue(ApprovalAction.AllowOnce);

        var loop = new AgentLoop(client, new ToolRegistry([PatchTool(dir)]), Options(),
                                 new PermissionEngine(dir.Root), approver);

        var result = await loop.RunAsync("sys", "patch", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(approver.Prompts, Is.EqualTo(2));
        Assert.That(result.ToolExecutions, Is.EqualTo(1));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(dir.Root, "fixme.cs")),
                    Is.EqualTo("line1\nline2-fixed\nline3\n"));
    }

    [Test]
    public async Task AllowOnce_RepromptsForADifferentInvocation()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("fixme.cs", "line1\nline2\nline3\n");

        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("apply_patch", PatchArgs(ReplacePatch(2)), "p1"));
        client.Enqueue(FakeChatClient.ToolCall("apply_patch", PatchArgs(ReplacePatch(3)), "p2"));
        client.Enqueue(FakeChatClient.Text("done"));

        var approver = new ScriptedApprover();
        approver.Enqueue(ApprovalAction.AllowOnce);
        approver.Enqueue(ApprovalAction.AllowOnce);

        var loop = new AgentLoop(client, new ToolRegistry([PatchTool(dir)]), Options(),
                                 new PermissionEngine(dir.Root), approver);

        var result = await loop.RunAsync("sys", "patch twice", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(approver.Prompts, Is.EqualTo(2)); // 指纹不同则两次都重新询问
    }

    [Test]
    public async Task AllowOnce_CoversOneAttempt_IdenticalCallPromptsAgain()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("fixme.cs", "line1\nline2\nline3\n");

        var client = new FakeChatClient();
        // 后续回合重复同一补丁：指纹（工具、能力、目标、参数）相同而 call id 不同——
        // call id 不参与指纹。首次 "allow once" 不得自动批准第二次尝试。
        client.Enqueue(FakeChatClient.ToolCall("apply_patch", PatchArgs(ReplacePatch(2)), "p1"));
        client.Enqueue(FakeChatClient.ToolCall("apply_patch", PatchArgs(ReplacePatch(2)), "p2"));
        client.Enqueue(FakeChatClient.Text("done"));

        var approver = new ScriptedApprover();
        approver.Enqueue(ApprovalAction.AllowOnce);
        approver.Enqueue(ApprovalAction.AllowOnce);

        var loop = new AgentLoop(client, new ToolRegistry([PatchTool(dir)]), Options(),
                                 new PermissionEngine(dir.Root), approver);

        var result = await loop.RunAsync("sys", "patch", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(approver.Prompts, Is.EqualTo(2)); // 完全相同的调用再次询问审批
    }

    [Test]
    public async Task InvalidSiblingCall_AbortsTheWholeRound_BeforeAnySideEffect()
    {
        using var dir  = new TestTempDir();
        var       path = dir.WriteFile("fixme.cs", "line1\nline2\nline3\n");

        var client = new FakeChatClient();
        // 同一回合两次调用：第一次是合法补丁，第二次逃逸工作区且准备失败。
        // 整个回合先统一校验再执行，合法调用也不得运行。
        var round = new List<ChatStreamEvent>
        {
            new()
            {
                Kind                   = ChatStreamEventKind.ToolCallDelta,
                ToolCallIndex          = 0, ToolCallId = "p1",
                ToolCallFunctionName   = "apply_patch",
                ToolCallArgumentsDelta = PatchArgs(ReplacePatch(2))
            },
            new()
            {
                Kind                   = ChatStreamEventKind.ToolCallDelta,
                ToolCallIndex          = 1, ToolCallId = "p2",
                ToolCallFunctionName   = "apply_patch",
                ToolCallArgumentsDelta = PatchArgs(EscapePatch())
            },
            new() { Kind = ChatStreamEventKind.End }
        };
        client.Enqueue(round);
        client.Enqueue(FakeChatClient.Text("understood"));

        var approver = new ScriptedApprover(); // 未排队任何审批动作：不得发起询问

        var loop = new AgentLoop(client, new ToolRegistry([PatchTool(dir)]), Options(),
                                 new PermissionEngine(dir.Root), approver);

        var result = await loop.RunAsync("sys", "patch", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(approver.Prompts, Is.EqualTo(0)); // 被中止回合的调用都不会获得授权
        Assert.That(result.ToolExecutions, Is.EqualTo(0));
        Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo("line1\nline2\nline3\n")); // 文件未被改动
        TestAssert.ContainsAll(m => m.Role == ChatRole.Tool && m.Content.Contains("failed to prepare"), loop.History);
        TestAssert.ContainsAll(m => m.Role == ChatRole.Tool && m.Content.Contains("Not executed"), loop.History);
    }

    [Test]
    public async Task MultipleCalls_AreAllAuthorizedBeforeTheFirstExecution()
    {
        using var dir    = new TestTempDir();
        var       tool   = new FakeTool("write_test");
        var       client = new FakeChatClient();
        client.Enqueue([
            new ChatStreamEvent
            {
                Kind                   = ChatStreamEventKind.ToolCallDelta,
                ToolCallIndex          = 0,
                ToolCallId             = "w1",
                ToolCallFunctionName   = "write_test",
                ToolCallArgumentsDelta = "{}"
            },
            new ChatStreamEvent
            {
                Kind                   = ChatStreamEventKind.ToolCallDelta,
                ToolCallIndex          = 1,
                ToolCallId             = "w2",
                ToolCallFunctionName   = "write_test",
                ToolCallArgumentsDelta = "{}"
            },
            new ChatStreamEvent { Kind = ChatStreamEventKind.End }
        ]);
        client.Enqueue(FakeChatClient.Text("done"));

        var approver = new ScriptedApprover { OnPrompt = () => Assert.That(tool.ExecuteCount, Is.EqualTo(0)) };
        approver.Enqueue(ApprovalAction.AllowOnce);
        approver.Enqueue(ApprovalAction.AllowOnce);

        var loop = new AgentLoop(client, new ToolRegistry([tool]), Options(),
                                 new PermissionEngine(dir.Root), approver);

        var result = await loop.RunAsync("sys", "run both", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(approver.Prompts, Is.EqualTo(2));
        Assert.That(tool.ExecuteCount, Is.EqualTo(2));
        Assert.That(result.ToolExecutions, Is.EqualTo(2));
    }

    [Test]
    public async Task ReadOnlyTools_DoNotPrompt()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("a.txt", "hello\n");

        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":"a.txt"}"""));
        client.Enqueue(FakeChatClient.Text("read it"));

        var approver = new ScriptedApprover();

        var loop = new AgentLoop(client, new ToolRegistry([new ReadFileTool(dir.Workspace)]), Options(),
                                 new PermissionEngine(dir.Root), approver);

        var result = await loop.RunAsync("sys", "read", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(approver.Prompts, Is.EqualTo(0));
    }

    private static string EscapePatch() =>
        """
        --- a/fixme.cs
        +++ b/../outside.cs
        @@ -1 +1 @@
        -line1
        +line1-x
        """;

    private static string ReplacePatch(int lineNumber)
    {
        var old = $"line{lineNumber}";
        var n   = $"line{lineNumber}-fixed";
        return $"""
            --- a/fixme.cs
            +++ b/fixme.cs
            @@ -{lineNumber} +{lineNumber} @@
            -{old}
            +{n}
            """;
    }

    /// <summary>
    ///     <para>按队列顺序返回审批动作，并统计被询问次数。</para>
    ///     <para>Returns actions from a queue and counts how many times it was asked.</para>
    /// </summary>
    private sealed class ScriptedApprover : IApprovalProvider
    {
        private readonly Queue<ApprovalAction> _actions = new();

        public int Prompts { get; private set; }

        public Action? OnPrompt { get; init; }

        public Task<ApprovalAction> PromptAsync(ToolPreparation preparation, CancellationToken cancellationToken)
        {
            OnPrompt?.Invoke();
            Prompts++;
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
