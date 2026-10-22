using System.Text.Json.Nodes;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.ProjectInstructions;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.ProjectInstructions;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

public class InstructionScopeTests
{
    [Test]
    public async Task CoverageDerivationPreservesThePrivateShellPlanAndFreezesScopeInput()
    {
        using var dir   = new TestTempDir();
        var       shell = new ShellTool(dir.Workspace);
        var prepared = shell.Prepare(new ChatToolCall("shell1", "shell",
                                                      """{"executable":"tools/runner.exe","arguments":[],"workingDirectory":"."}"""));
        Assert.That(prepared.InstructionScopes.Select(scope => scope.Kind),
                    Is.EqualTo([InstructionScopeKind.Directory, InstructionScopeKind.File]));
        var snapshot =
            await new ProjectInstructionLoader(dir.Workspace).LoadAsync(null, prepared.InstructionScopes,
                                                                        CancellationToken.None);
        var covered = prepared.WithInstructionCoverage(new InstructionCoverage(dir.Root, snapshot));
        Assert.That(covered.ExecutionPlan, Is.SameAs(prepared.ExecutionPlan));
        Assert.That(covered.SessionConstraint, Is.EqualTo(prepared.SessionConstraint));
        Assert.That(covered.SessionGrantConstraint, Is.EqualTo(prepared.SessionGrantConstraint));
        Assert.That(covered.Arguments.ToJsonString(), Is.EqualTo(prepared.Arguments.ToJsonString()));
        Assert.That(prepared.Coverage, Is.Null);
    }

    [Test]
    public async Task RecursiveSearchLoadsDeepRulesBeforeExecutingAnyOldCall()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("AGENTS.md", "ROOT_RULE");
        dir.WriteFile("nested/deep/AGENTS.md", "LOCAL_RULE");
        dir.WriteFile("nested/deep/data.txt", "needle");
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCalls("search_text", 2, "old", """{"pattern":"needle"}"""));
        client.Enqueue(FakeChatClient.ToolCall("search_text", """{"pattern":"needle"}""", "new"));
        client.Enqueue(FakeChatClient.Text("done"));
        var       loader = new ProjectInstructionLoader(dir.Workspace);
        using var host   = Host(dir, client, [new SearchTextTool(dir.Workspace)], loader);
        var       run    = await host.SubmitTurnAsync("search", CancellationToken.None);
        Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(run.Result.ToolExecutions, Is.EqualTo(1));
        Assert.That(run.Result.ToolCalls!.Take(2).All(call => call.State == ToolCallExecutionState.KnownNotStarted),
                    Is.True);
        Assert.That(client.RequestLog[0].Messages.Any(message => message.Content.Contains("LOCAL_RULE")), Is.False);
        Assert.That(client.RequestLog[1].Messages[1].Content, Does.Contain("LOCAL_RULE"));
        Assert.That(client.RequestLog[2].Messages.Last(message => message.Role == ChatRole.Tool).Content,
                    Does.Contain("nested/deep/data.txt:1: needle"));
    }

    [Test]
    public async Task NewDirectoryAfterCoverageIsRejectedBeforeRecursiveContentReads()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("existing.txt", "needle");
        var tool     = new SearchTextTool(dir.Workspace);
        var prepared = tool.Prepare(new ChatToolCall("search", "search_text", """{"pattern":"needle"}"""));
        var snapshot =
            await new ProjectInstructionLoader(dir.Workspace).LoadAsync(null, prepared.InstructionScopes,
                                                                        CancellationToken.None);
        var covered = prepared.WithInstructionCoverage(new InstructionCoverage(dir.Root, snapshot));
        dir.WriteFile("new/deep/secret.txt", "must not read");
        var error =
            await TestAssert
               .ThrowsAsync<InvalidDataException>(() => tool.ExecuteAsync(covered, CancellationToken.None));
        Assert.That(error.Message, Does.Contain("not covered"));
    }

    [Test]
    public async Task UncoveredPatchDirectoryIsRejectedWithoutCreatingIt()
    {
        using var dir   = new TestTempDir();
        var       tool  = new ApplyPatchTool(dir.Workspace);
        var       patch = "--- /dev/null\n+++ b/new/file.txt\n@@ -0,0 +1,1 @@\n+new\n";
        var prepared =
            tool.Prepare(new ChatToolCall("patch", "apply_patch", new JsonObject { ["patch"] = patch }.ToJsonString()));
        var root = await new ProjectInstructionLoader(dir.Workspace).LoadAsync(null, [], CancellationToken.None);
        var result = await tool.ExecuteAsync(prepared.WithInstructionCoverage(new InstructionCoverage(dir.Root, root)),
                                             CancellationToken.None);
        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Content, Does.Contain("not covered"));
        Assert.That(Directory.Exists(Path.Combine(dir.Root, "new")), Is.False);
    }

    [Test]
    public async Task ApprovalRuleChangeStopsFurtherOldApprovalPromptsAndOldPatchExecution()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("AGENTS.md", "INITIAL_RULE");
        dir.WriteFile("file.txt", "old\n");
        var patch = new JsonObject { ["patch"] = "--- a/file.txt\n+++ b/file.txt\n@@ -1 +1 @@\n-old\n+new\n" }
           .ToJsonString();
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCalls("apply_patch", 2, "old", patch));
        client.Enqueue(FakeChatClient.Text("reconsidered"));
        var       approver = new ChangeRuleApprover(() => dir.WriteFile("AGENTS.md", "UPDATED_RULE"));
        var       loader   = new ProjectInstructionLoader(dir.Workspace);
        using var host     = Host(dir, client, [new ApplyPatchTool(dir.Workspace)], loader, approver);
        var       run      = await host.SubmitTurnAsync("change file", CancellationToken.None);
        Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(run.Result.ToolExecutions, Is.Zero);
        Assert.That(approver.Prompts, Is.EqualTo(1));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(dir.Root, "file.txt")), Is.EqualTo("old\n"));
        Assert.That(client.RequestLog[1].Messages[1].Content, Does.Contain("UPDATED_RULE"));
    }

    [Test]
    public async Task PatchChangingAgentsDefersTheRestOfItsRoundAndKeepsTheConfirmedPatch()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("AGENTS.md", "old rule\n");
        dir.WriteFile("file.txt", "old\n");
        var client = new FakeChatClient();
        var arguments = new[]
        {
            new JsonObject { ["patch"] = "--- a/AGENTS.md\n+++ b/AGENTS.md\n@@ -1 +1 @@\n-old rule\n+new rule\n" }
               .ToJsonString(),
            new JsonObject { ["patch"] = "--- a/file.txt\n+++ b/file.txt\n@@ -1 +1 @@\n-old\n+new\n" }.ToJsonString()
        };
        client.Enqueue(ToolCalls("apply_patch", arguments, "self"));
        client.Enqueue(FakeChatClient.Text("done"));
        var       loader = new ProjectInstructionLoader(dir.Workspace);
        using var host   = Host(dir, client, [new ApplyPatchTool(dir.Workspace)], loader, new ChangeRuleApprover(null));
        var       run    = await host.SubmitTurnAsync("change rules then file", CancellationToken.None);
        Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(run.Result.ToolExecutions, Is.EqualTo(1));
        Assert.That(run.Result.ToolCalls!.Select(call => call.State), Is.EqualTo([
            ToolCallExecutionState.ConfirmedResult, ToolCallExecutionState.KnownNotStarted
        ]));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(dir.Root, "file.txt")), Is.EqualTo("old\n"));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(dir.Root, "AGENTS.md")), Is.EqualTo("new rule\n"));
        Assert.That(client.RequestLog[1].Messages[1].Content, Does.Contain("new rule"));
    }

    private static ChatSessionHost Host(TestTempDir              dir, FakeChatClient client, IReadOnlyList<ITool> tools,
                                        ProjectInstructionLoader loader, IApprovalProvider? approver = null) =>
        new(dir.Root, new SessionModelIdentity("test-model", "https://example.invalid/v1", ChatApiKind.ChatCompletions),
            new AgentOptions { Model = "test-model", MaxAgentSteps = 10, DefaultToolTimeoutSeconds = 30 },
            client, new ToolRegistry(tools), "system", new PermissionEngine(dir.Root), approver,
            instructionLoader : loader);

    private static IReadOnlyList<ChatStreamEvent>
        ToolCalls(string name, IReadOnlyList<string> arguments, string prefix) =>
        arguments.Select((argument, index) => new ChatStreamEvent
        {
            Kind                   = ChatStreamEventKind.ToolCallDelta,
            ToolCallIndex          = index,
            ToolCallId             = prefix + index,
            ToolCallFunctionName   = name,
            ToolCallArgumentsDelta = argument
        }).Append(new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason = "tool_calls" }).ToArray();

    private sealed class ChangeRuleApprover(Action? change) : IApprovalProvider
    {
        public int Prompts { get; private set; }

        public Task<ApprovalAction> PromptAsync(ToolPreparation preparation, CancellationToken cancellationToken)
        {
            if (++Prompts == 1) change?.Invoke();
            return Task.FromResult(ApprovalAction.AllowSession);
        }
    }
}
