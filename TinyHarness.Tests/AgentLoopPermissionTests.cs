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
    public async Task BaselineWrites_AreAutoAllowed_WithoutPrompting()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("fixme.cs", "line1\nline2\nline3\n");

        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("apply_patch", PatchArgs(ReplacePatch(2)), "p1"));
        client.Enqueue(FakeChatClient.ToolCall("apply_patch", PatchArgs(ReplacePatch(3)), "p2"));
        client.Enqueue(FakeChatClient.Text("both patches applied"));

        var approver = new ScriptedApprover();
        var loop = new AgentLoop(client, new ToolRegistry([PatchTool(dir)]), Options(),
                                 new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root)),
                                 approver);

        var result  = await loop.RunAsync("sys", "patch twice", CancellationToken.None);
        var content = await File.ReadAllTextAsync(Path.Combine(dir.Root, "fixme.cs"));

        Assert.Multiple(() =>
        {
            // 沙箱基线写入属 Level1，自动放行，不再逐次审批。
            Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
            Assert.That(approver.Prompts, Is.EqualTo(0));
            Assert.That(content, Is.EqualTo("line1\nline2-fixed\nline3-fixed\n"));
        });
    }

    [Test]
    public async Task TurnGrant_AuthorizesLevel2Writes_Repeatedly()
    {
        using var dir     = new TestTempDir();
        var       outside = Path.Combine(Path.GetDirectoryName(dir.Root)!, "outside.txt");
        var       client  = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("write_outside",
                                               new JsonObject { ["target"] = outside }.ToJsonString(), "p1"));
        client.Enqueue(FakeChatClient.ToolCall("write_outside",
                                               new JsonObject { ["target"] = outside }.ToJsonString(), "p2"));
        client.Enqueue(FakeChatClient.Text("both done"));

        var tool     = new OutsideWriteTool();
        var approver = new ScriptedApprover();
        approver.Enqueue(ApprovalResolution.Granted(GrantScope.Turn));

        var loop = new AgentLoop(client, new ToolRegistry([tool]), Options(),
                                 new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root)),
                                 approver);

        var result = await loop.RunAsync("sys", "write twice", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
            Assert.That(approver.Prompts, Is.EqualTo(1), "第二次写入由 Turn 授权自动批准。");
            Assert.That(tool.ExecuteCount, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task Deny_ReturnsDenialToModelAndWritesNothing()
    {
        using var dir     = new TestTempDir();
        var       outside = Path.Combine(Path.GetDirectoryName(dir.Root)!, "outside.txt");

        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("write_outside",
                                               new JsonObject { ["target"] = outside }.ToJsonString(), "p1"));
        client.Enqueue(FakeChatClient.Text("understood"));

        var tool     = new OutsideWriteTool();
        var approver = new ScriptedApprover();
        approver.Enqueue(ApprovalResolution.Denied("denied by test"));

        var loop = new AgentLoop(client, new ToolRegistry([tool]), Options(),
                                 new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root)),
                                 approver);

        var result = await loop.RunAsync("sys", "write", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
            Assert.That(approver.Prompts, Is.EqualTo(1));
            Assert.That(result.ToolExecutions, Is.EqualTo(0));
            Assert.That(tool.ExecuteCount, Is.EqualTo(0));
            Assert.That(File.Exists(outside), Is.False);
            TestAssert.ContainsAll(m => m.Role == ChatRole.Tool &&
                                        m.Content.Contains("Permission denied by the approver"), loop.History);
        });
    }

    [Test]
    public async Task DenialMemory_BlocksAMultiTargetCall_DespiteTurnGrant()
    {
        using var dir    = new TestTempDir();
        var       parent = Path.GetDirectoryName(dir.Root)!;
        var       denied = Path.Combine(parent, "denied.txt");
        var       other  = Path.Combine(parent, "other.txt");
        var       tool   = new OutsideWriteTool();
        var       engine = new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root));
        // 拒绝记忆绑定 denied.txt；多目标调用命中其一即整体拒绝，Turn 授权不得绕过。
        engine.RecordDenial(new ToolPreparation
        {
            ToolName  = "write_outside", CallId       = "deny", Capability = "filesystem.write", Summary = "deny",
            Arguments = new JsonObject(), TargetPaths = [denied]
        });
        engine.Grants.Add(GrantScope.Turn, new ToolPreparation
        {
            ToolName  = "write_outside", CallId       = "grant", Capability = "filesystem.write", Summary = "grant",
            Arguments = new JsonObject(), TargetPaths = [denied, other]
        });
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("write_outside",
                                               new JsonObject { ["target"] = denied }.ToJsonString()));
        client.Enqueue(FakeChatClient.Text("understood"));
        var approver = new ScriptedApprover();
        var loop     = new AgentLoop(client, new ToolRegistry([tool]), Options(), engine, approver);

        var result = await loop.RunAsync("sys", "write", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
            Assert.That(approver.Prompts, Is.EqualTo(0));
            Assert.That(result.ToolExecutions, Is.EqualTo(0));
            Assert.That(tool.ExecuteCount, Is.EqualTo(0));
            Assert.That(File.Exists(denied), Is.False);
            TestAssert.ContainsAll(message => message.Role == ChatRole.Tool &&
                                              message.Content.Contains("Previously denied by the approver"),
                                   loop.History);
        });
    }

    [Test]
    public async Task Deny_IsRemembered_IdenticalCallIsNotPromptedAgain()
    {
        using var dir     = new TestTempDir();
        var       outside = Path.Combine(Path.GetDirectoryName(dir.Root)!, "outside.txt");
        var       client  = new FakeChatClient();
        // 后续回合重复同一调用：拒绝记忆命中，不再弹卡、不再询问。
        client.Enqueue(FakeChatClient.ToolCall("write_outside",
                                               new JsonObject { ["target"] = outside }.ToJsonString(), "p1"));
        client.Enqueue(FakeChatClient.ToolCall("write_outside",
                                               new JsonObject { ["target"] = outside }.ToJsonString(), "p2"));
        client.Enqueue(FakeChatClient.Text("done"));

        var tool     = new OutsideWriteTool();
        var approver = new ScriptedApprover();
        approver.Enqueue(ApprovalResolution.Denied("denied by test"));

        var loop = new AgentLoop(client, new ToolRegistry([tool]), Options(),
                                 new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root)),
                                 approver);

        var result = await loop.RunAsync("sys", "write twice", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
            Assert.That(approver.Prompts, Is.EqualTo(1), "同一操作拒绝后不再弹卡。");
            Assert.That(result.ToolExecutions, Is.EqualTo(0));
            Assert.That(tool.ExecuteCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task AllowOnce_RepromptsForADifferentInvocation()
    {
        using var dir      = new TestTempDir();
        var       outsideA = Path.Combine(Path.GetDirectoryName(dir.Root)!, "a.txt");
        var       outsideB = Path.Combine(Path.GetDirectoryName(dir.Root)!, "b.txt");
        var       client   = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("write_outside",
                                               new JsonObject { ["target"] = outsideA }.ToJsonString(), "p1"));
        client.Enqueue(FakeChatClient.ToolCall("write_outside",
                                               new JsonObject { ["target"] = outsideB }.ToJsonString(), "p2"));
        client.Enqueue(FakeChatClient.Text("done"));

        var tool     = new OutsideWriteTool();
        var approver = new ScriptedApprover();
        approver.Enqueue(ApprovalResolution.Granted(GrantScope.SingleOperation));
        approver.Enqueue(ApprovalResolution.Granted(GrantScope.SingleOperation));

        var loop = new AgentLoop(client, new ToolRegistry([tool]), Options(),
                                 new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root)),
                                 approver);

        var result = await loop.RunAsync("sys", "write twice", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
            Assert.That(approver.Prompts, Is.EqualTo(2), "指纹不同则两次都重新询问。");
            Assert.That(tool.ExecuteCount, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task AllowOnce_CoversOneAttempt_IdenticalCallPromptsAgain()
    {
        using var dir     = new TestTempDir();
        var       outside = Path.Combine(Path.GetDirectoryName(dir.Root)!, "same.txt");
        var       client  = new FakeChatClient();
        // 后续回合重复同一调用：指纹（工具、能力、目标、参数）相同而 call id 不同——
        // call id 不参与指纹。首次 "allow once" 不得自动批准第二次尝试。
        client.Enqueue(FakeChatClient.ToolCall("write_outside",
                                               new JsonObject { ["target"] = outside }.ToJsonString(), "p1"));
        client.Enqueue(FakeChatClient.ToolCall("write_outside",
                                               new JsonObject { ["target"] = outside }.ToJsonString(), "p2"));
        client.Enqueue(FakeChatClient.Text("done"));

        var tool     = new OutsideWriteTool();
        var approver = new ScriptedApprover();
        approver.Enqueue(ApprovalResolution.Granted(GrantScope.SingleOperation));
        approver.Enqueue(ApprovalResolution.Granted(GrantScope.SingleOperation));

        var loop = new AgentLoop(client, new ToolRegistry([tool]), Options(),
                                 new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root)),
                                 approver);

        var result = await loop.RunAsync("sys", "write twice", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
            Assert.That(approver.Prompts, Is.EqualTo(2), "完全相同的 Level2 调用再次询问审批。");
            Assert.That(tool.ExecuteCount, Is.EqualTo(2));
        });
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
            new() { Kind = ChatStreamEventKind.End, FinishReason = "tool_calls" }
        };
        client.Enqueue(round);
        client.Enqueue(FakeChatClient.Text("understood"));

        var approver = new ScriptedApprover(); // 未排队任何审批动作：不得发起询问

        var loop = new AgentLoop(client, new ToolRegistry([PatchTool(dir)]), Options(),
                                 new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root)),
                                 approver);

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
            new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason = "tool_calls" }
        ]);
        client.Enqueue(FakeChatClient.Text("done"));

        var approver = new ScriptedApprover { OnPrompt = () => Assert.That(tool.ExecuteCount, Is.EqualTo(0)) };
        approver.Enqueue(ApprovalResolution.Granted(GrantScope.SingleOperation));
        approver.Enqueue(ApprovalResolution.Granted(GrantScope.SingleOperation));

        var loop = new AgentLoop(client, new ToolRegistry([tool]), Options(),
                                 new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root)),
                                 approver);

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
                                 new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root)),
                                 approver);

        var result = await loop.RunAsync("sys", "read", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(approver.Prompts, Is.EqualTo(0));
    }

    [TestCase(GrantScope.SingleOperation)]
    [TestCase(GrantScope.Turn)]
    [TestCase(GrantScope.Session)]
    public async Task RevocationDuringRoundApprovalPreventsEarlierApprovedCallsFromExecuting(GrantScope scope)
    {
        using var dir    = new TestTempDir();
        var       first  = new FakeTool("first");
        var       second = new FakeTool("second");
        var       engine = new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root));
        var       client = new FakeChatClient();
        client.Enqueue([
            new ChatStreamEvent
            {
                Kind                 = ChatStreamEventKind.ToolCallDelta, ToolCallIndex = 0, ToolCallId = "first",
                ToolCallFunctionName = "first", ToolCallArgumentsDelta                  = "{}"
            },
            new ChatStreamEvent
            {
                Kind                 = ChatStreamEventKind.ToolCallDelta, ToolCallIndex = 1, ToolCallId = "second",
                ToolCallFunctionName = "second", ToolCallArgumentsDelta                 = "{}"
            },
            new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason = "tool_calls" }
        ]);
        client.Enqueue(FakeChatClient.Text("done"));
        var prompts = 0;
        var approver = new ScriptedApprover
        {
            OnPrompt = () =>
            {
                if (++prompts == 2) engine.RevokeGrants();
            }
        };
        approver.Enqueue(ApprovalResolution.Granted(scope));
        approver.Enqueue(ApprovalResolution.Granted(GrantScope.SingleOperation));
        var loop = new AgentLoop(client, new ToolRegistry([first, second]), Options(), engine, approver);

        var result = await loop.RunAsync("sys", "run both", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(approver.Prompts, Is.EqualTo(2));
        Assert.That(first.ExecuteCount + second.ExecuteCount, Is.Zero);
        Assert.That(loop.History.Where(message => message.Role == ChatRole.Tool)
                        .Any(message => message.Content.Contains("Authorization invalidated")), Is.True);
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
    ///     <para>
    ///         Level2 写工具：能力为 filesystem.write，目标路径取自参数 target，
    ///         可指向工作区外，用于驱动审批流程。
    ///     </para>
    ///     <para>
    ///         A Level2 write tool: capability filesystem.write with the target taken from the
    ///         target argument, which may sit outside the workspace to drive the approval flow.
    ///     </para>
    /// </summary>
    private sealed class OutsideWriteTool : ITool
    {
        public int ExecuteCount { get; private set; }

        public ToolDefinition Definition { get; } = new()
        {
            Name = "write_outside", Description = "test", Parameters = new JsonObject { ["type"] = "object" }
        };

        public ToolPreparation Prepare(ChatToolCall call)
        {
            var target = JsonNode.Parse(call.ArgumentsJson)!["target"]!.GetValue<string>();
            return new ToolPreparation
            {
                ToolName    = Definition.Name,
                CallId      = call.Id,
                Arguments   = new JsonObject { ["target"] = target },
                Capability  = "filesystem.write",
                Summary     = $"write {target}",
                TargetPaths = [target]
            };
        }

        public Task<ToolResult> ExecuteAsync(ToolPreparation preparation, CancellationToken cancellationToken)
        {
            ExecuteCount++;
            return Task.FromResult(new ToolResult { Succeeded = true, Content = "written" });
        }
    }

    /// <summary>
    ///     <para>按队列顺序返回审批动作，并统计被询问次数。</para>
    ///     <para>Returns actions from a queue and counts how many times it was asked.</para>
    /// </summary>
    private sealed class ScriptedApprover : IApprovalProvider
    {
        private readonly Queue<ApprovalResolution> _actions = new();

        public int Prompts { get; private set; }

        public Action? OnPrompt { get; init; }

        public Task<ApprovalResolution> PromptAsync(ApprovalRequest request, CancellationToken cancellationToken)
        {
            OnPrompt?.Invoke();
            Prompts++;
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
