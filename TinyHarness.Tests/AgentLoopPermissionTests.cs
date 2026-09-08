using System.Text.Json.Nodes;
using TinyHarness.Core.Agent;
using TinyHarness.Core.ChatCompletions;
using TinyHarness.Core.Permissions;
using TinyHarness.Core.Tools;

namespace TinyHarness.Tests;

public class AgentLoopPermissionTests
{
    private static AgentOptions Options() => new()
    {
        Model                     = "test-model",
        MaxAgentSteps             = 10,
        DefaultToolTimeoutSeconds = 30,
    };

    private static string PatchArgs(string patch) => new JsonObject { ["patch"] = patch }.ToJsonString();

    private static ApplyPatchTool PatchTool(TestTempDir dir) => new(dir.Workspace);

    /// <summary>Returns actions from a queue and counts how many times it was asked.</summary>
    private sealed class ScriptedApprover : IApprovalProvider
    {
        private readonly Queue<ApprovalAction> _actions = new();

        public int Prompts { get; private set; }

        public Action? OnPrompt { get; init; }

        public void Enqueue(ApprovalAction action) => _actions.Enqueue(action);

        public Task<ApprovalAction> PromptAsync(ToolPreparation preparation, CancellationToken cancellationToken)
        {
            OnPrompt?.Invoke();
            Prompts++;
            if (_actions.Count == 0)
            {
                throw new InvalidOperationException("No scripted approval action left.");
            }

            return Task.FromResult(_actions.Dequeue());
        }
    }

    [Fact]
    public async Task SessionGrant_AutoApprovesLaterWritesToSameFile()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("fixme.cs", "line1\nline2\nline3\n");

        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("apply_patch", PatchArgs(ReplacePatch(2)), id : "p1"));
        client.Enqueue(FakeChatClient.ToolCall("apply_patch", PatchArgs(ReplacePatch(3)), id : "p2"));
        client.Enqueue(FakeChatClient.Text("both patches applied"));

        var approver = new ScriptedApprover();
        approver.Enqueue(ApprovalAction.AllowSession);

        var loop = new AgentLoop(client, new ToolRegistry([PatchTool(dir)]), Options(),
                                 new PermissionEngine(dir.Root), approver);

        var result = await loop.RunAsync("sys", "patch twice", CancellationToken.None);

        Assert.Equal(AgentStatus.Completed, result.Status);
        Assert.Equal(1, approver.Prompts); // the second write is auto-approved by the session grant
        Assert.Equal("line1\nline2-fixed\nline3-fixed\n",
                     File.ReadAllText(Path.Combine(dir.Root, "fixme.cs")));
    }

    [Fact]
    public async Task Deny_ReturnsDenialToModelAndWritesNothing()
    {
        using var dir  = new TestTempDir();
        var       path = dir.WriteFile("fixme.cs", "line1\nline2\nline3\n");

        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("apply_patch", PatchArgs(ReplacePatch(2)), id : "p1"));
        client.Enqueue(FakeChatClient.Text("understood"));

        var approver = new ScriptedApprover();
        approver.Enqueue(ApprovalAction.Deny);

        var loop = new AgentLoop(client, new ToolRegistry([PatchTool(dir)]), Options(),
                                 new PermissionEngine(dir.Root), approver);

        var result = await loop.RunAsync("sys", "patch", CancellationToken.None);

        Assert.Equal(AgentStatus.Completed, result.Status);
        Assert.Equal(1, approver.Prompts);
        Assert.Equal(0, result.ToolExecutions);
        Assert.Equal("line1\nline2\nline3\n", File.ReadAllText(path)); // untouched
        Assert.Contains(loop.History,
                        m => m.Role == ChatRole.Tool && m.Content.Contains("Permission denied by user"));
    }

    [Fact]
    public async Task SessionDeny_BlocksAMultiFilePatchDespiteSessionGrant()
    {
        using var dir = new TestTempDir();
        var protectedPath = dir.WriteFile("fixme.cs", "line1\nline2\nline3\n");
        var otherPath = dir.WriteFile("other.txt", "old\n");
        var tool = PatchTool(dir);
        var patch = ReplacePatch(2) + "\n--- a/other.txt\n+++ b/other.txt\n@@ -1 +1 @@\n-old\n+new\n";
        var engine = new PermissionEngine(dir.Root);
        engine.GrantSession(tool.Prepare(new ChatToolCall("grant", "apply_patch", PatchArgs(patch))));
        engine.DenySession(tool.Prepare(new ChatToolCall("deny", "apply_patch", PatchArgs(ReplacePatch(2)))));
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("apply_patch", PatchArgs(patch)));
        client.Enqueue(FakeChatClient.Text("understood"));
        var approver = new ScriptedApprover();
        var loop = new AgentLoop(client, new ToolRegistry([tool]), Options(), engine, approver);

        var result = await loop.RunAsync("sys", "patch both", CancellationToken.None);

        Assert.Equal(AgentStatus.Completed, result.Status);
        Assert.Equal(0, approver.Prompts);
        Assert.Equal(0, result.ToolExecutions);
        Assert.Equal("line1\nline2\nline3\n", await File.ReadAllTextAsync(protectedPath));
        Assert.Equal("old\n", await File.ReadAllTextAsync(otherPath));
        Assert.Contains(loop.History, message => message.Role == ChatRole.Tool && message.Content.Contains("Permission denied"));
    }

    [Fact]
    public async Task Deny_CoversOneAttempt_IdenticalCallPromptsAgain()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("fixme.cs", "line1\nline2\nline3\n");

        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("apply_patch", PatchArgs(ReplacePatch(2)), id : "p1"));
        client.Enqueue(FakeChatClient.ToolCall("apply_patch", PatchArgs(ReplacePatch(2)), id : "p2"));
        client.Enqueue(FakeChatClient.Text("done"));

        var approver = new ScriptedApprover();
        approver.Enqueue(ApprovalAction.Deny);
        approver.Enqueue(ApprovalAction.AllowOnce);

        var loop = new AgentLoop(client, new ToolRegistry([PatchTool(dir)]), Options(),
                                 new PermissionEngine(dir.Root), approver);

        var result = await loop.RunAsync("sys", "patch", CancellationToken.None);

        Assert.Equal(AgentStatus.Completed, result.Status);
        Assert.Equal(2, approver.Prompts);
        Assert.Equal(1, result.ToolExecutions);
        Assert.Equal("line1\nline2-fixed\nline3\n",
                     File.ReadAllText(Path.Combine(dir.Root, "fixme.cs")));
    }

    [Fact]
    public async Task AllowOnce_RepromptsForADifferentInvocation()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("fixme.cs", "line1\nline2\nline3\n");

        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("apply_patch", PatchArgs(ReplacePatch(2)), id : "p1"));
        client.Enqueue(FakeChatClient.ToolCall("apply_patch", PatchArgs(ReplacePatch(3)), id : "p2"));
        client.Enqueue(FakeChatClient.Text("done"));

        var approver = new ScriptedApprover();
        approver.Enqueue(ApprovalAction.AllowOnce);
        approver.Enqueue(ApprovalAction.AllowOnce);

        var loop = new AgentLoop(client, new ToolRegistry([PatchTool(dir)]), Options(),
                                 new PermissionEngine(dir.Root), approver);

        var result = await loop.RunAsync("sys", "patch twice", CancellationToken.None);

        Assert.Equal(AgentStatus.Completed, result.Status);
        Assert.Equal(2, approver.Prompts); // different fingerprints both re-prompt
    }

    [Fact]
    public async Task AllowOnce_CoversOneAttempt_IdenticalCallPromptsAgain()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("fixme.cs", "line1\nline2\nline3\n");

        var client = new FakeChatClient();
        // The same patch again in a later round: same fingerprint (tool,
        // capability, targets, arguments), different call id — call ids are not
        // part of the fingerprint. The first "allow once" must not auto-approve
        // this second attempt.
        client.Enqueue(FakeChatClient.ToolCall("apply_patch", PatchArgs(ReplacePatch(2)), id : "p1"));
        client.Enqueue(FakeChatClient.ToolCall("apply_patch", PatchArgs(ReplacePatch(2)), id : "p2"));
        client.Enqueue(FakeChatClient.Text("done"));

        var approver = new ScriptedApprover();
        approver.Enqueue(ApprovalAction.AllowOnce);
        approver.Enqueue(ApprovalAction.AllowOnce);

        var loop = new AgentLoop(client, new ToolRegistry([PatchTool(dir)]), Options(),
                                 new PermissionEngine(dir.Root), approver);

        var result = await loop.RunAsync("sys", "patch", CancellationToken.None);

        Assert.Equal(AgentStatus.Completed, result.Status);
        Assert.Equal(2, approver.Prompts); // the identical invocation is approved again
    }

    [Fact]
    public async Task InvalidSiblingCall_AbortsTheWholeRound_BeforeAnySideEffect()
    {
        using var dir  = new TestTempDir();
        var       path = dir.WriteFile("fixme.cs", "line1\nline2\nline3\n");

        var client = new FakeChatClient();
        // One round with two calls: the first is a valid patch, the second
        // escapes the workspace and fails to prepare. The valid call must not
        // run, because the whole round is validated before anything executes.
        var round = new List<ChatStreamEvent>
        {
            new()
            {
                Kind = ChatStreamEventKind.ToolCallDelta, ToolCallIndex = 0, ToolCallId = "p1",
                ToolCallFunctionName = "apply_patch", ToolCallArgumentsDelta = PatchArgs(ReplacePatch(2)),
            },
            new()
            {
                Kind = ChatStreamEventKind.ToolCallDelta, ToolCallIndex = 1, ToolCallId = "p2",
                ToolCallFunctionName = "apply_patch", ToolCallArgumentsDelta = PatchArgs(EscapePatch()),
            },
            new() { Kind = ChatStreamEventKind.End },
        };
        client.Enqueue(round);
        client.Enqueue(FakeChatClient.Text("understood"));

        var approver = new ScriptedApprover(); // no actions queued: nothing may be prompted

        var loop = new AgentLoop(client, new ToolRegistry([PatchTool(dir)]), Options(),
                                 new PermissionEngine(dir.Root), approver);

        var result = await loop.RunAsync("sys", "patch", CancellationToken.None);

        Assert.Equal(AgentStatus.Completed, result.Status);
        Assert.Equal(0, approver.Prompts); // no call of an aborted round is authorized
        Assert.Equal(0, result.ToolExecutions);
        Assert.Equal("line1\nline2\nline3\n", File.ReadAllText(path)); // untouched
        Assert.Contains(loop.History, m => m.Role == ChatRole.Tool && m.Content.Contains("failed to prepare"));
        Assert.Contains(loop.History, m => m.Role == ChatRole.Tool && m.Content.Contains("Not executed"));
    }

    [Fact]
    public async Task MultipleCalls_AreAllAuthorizedBeforeTheFirstExecution()
    {
        using var dir = new TestTempDir();
        var       tool = new FakeTool("write_test");
        var client = new FakeChatClient();
        client.Enqueue([
            new ChatStreamEvent
            {
                Kind = ChatStreamEventKind.ToolCallDelta, ToolCallIndex = 0, ToolCallId = "w1",
                ToolCallFunctionName = "write_test", ToolCallArgumentsDelta = "{}",
            },
            new ChatStreamEvent
            {
                Kind = ChatStreamEventKind.ToolCallDelta, ToolCallIndex = 1, ToolCallId = "w2",
                ToolCallFunctionName = "write_test", ToolCallArgumentsDelta = "{}",
            },
            new ChatStreamEvent { Kind = ChatStreamEventKind.End },
        ]);
        client.Enqueue(FakeChatClient.Text("done"));

        var approver = new ScriptedApprover { OnPrompt = () => Assert.Equal(0, tool.ExecuteCount) };
        approver.Enqueue(ApprovalAction.AllowOnce);
        approver.Enqueue(ApprovalAction.AllowOnce);

        var loop = new AgentLoop(client, new ToolRegistry([tool]), Options(),
                                 new PermissionEngine(dir.Root), approver);

        var result = await loop.RunAsync("sys", "run both", CancellationToken.None);

        Assert.Equal(AgentStatus.Completed, result.Status);
        Assert.Equal(2, approver.Prompts);
        Assert.Equal(2, tool.ExecuteCount);
        Assert.Equal(2, result.ToolExecutions);
    }

    [Fact]
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

        Assert.Equal(AgentStatus.Completed, result.Status);
        Assert.Equal(0, approver.Prompts);
    }

    private static string EscapePatch() => """
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
}
