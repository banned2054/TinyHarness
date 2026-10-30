using System.Text.Json.Nodes;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.ProjectInstructions;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Persistence;
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
    public async Task RecursiveListingDoesNotActivateDescendantRulesButFileReadsStillRequireThem()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("AGENTS.md", "ROOT_RULE");
        for (var index = 0; index < 3; index++)
            dir.WriteFile($"artifacts/copy{index}/AGENTS.md", new string('x', 16_000));
        dir.WriteBytes("artifacts/invalid/AGENTS.md", [0xFF]);
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("list_files", """{"path":"artifacts","recursive":true,"maxDepth":2}"""));
        client.Enqueue(FakeChatClient.Text("listed"));
        client.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":"artifacts/invalid/data.txt"}""", "read"));
        var       loader = new ProjectInstructionLoader(dir.Workspace);
        using var host = Host(dir, client, [new ListFilesTool(dir.Workspace), new ReadFileTool(dir.Workspace)], loader);
        var       listed = await host.SubmitTurnAsync("list names", CancellationToken.None);
        Assert.That(listed.Result!.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(listed.Result.ToolExecutions, Is.EqualTo(1));
        Assert.That(client.RequestLog[1].Messages.Last(message => message.Role == ChatRole.Tool).Content,
                    Does.Contain("artifacts/copy0/AGENTS.md"));
        Assert.That(client.RequestLog[1].Messages[1].Content, Does.Not.Contain("Source: artifacts/"));
        var read = await host.SubmitTurnAsync("read content", CancellationToken.None);
        Assert.That(read.Result!.Status, Is.EqualTo(AgentStatus.Failed));
        Assert.That(read.Result.ToolExecutions, Is.Zero);
        Assert.That(read.Result.Error, Does.Contain("UTF-8"));
    }

    [Test]
    public async Task BroadSearchReturnsAnUnstartedResultAndCanContinueWithANarrowerPath()
    {
        using var dir   = new TestTempDir();
        using var store = new TestTempDir();
        dir.WriteFile("AGENTS.md", "ROOT_RULE");
        for (var index = 0; index < 3; index++)
            dir.WriteFile($"artifacts/copy{index}/AGENTS.md", new string('x', 16_000));
        dir.WriteFile("artifacts/copy0/data.txt", "needle");
        dir.WriteFile("untouched.txt", "old\n");
        var client = new FakeChatClient();
        client.Enqueue([
            new ChatStreamEvent
            {
                Kind                 = ChatStreamEventKind.ToolCallDelta, ToolCallIndex = 0, ToolCallId = "write",
                ToolCallFunctionName = "apply_patch",
                ToolCallArgumentsDelta = new JsonObject
                {
                    ["patch"] = "--- a/untouched.txt\n+++ b/untouched.txt\n@@ -1 +1 @@\n-old\n+changed\n"
                }.ToJsonString()
            },
            new ChatStreamEvent
            {
                Kind                   = ChatStreamEventKind.ToolCallDelta, ToolCallIndex = 1, ToolCallId = "broad",
                ToolCallFunctionName   = "search_text",
                ToolCallArgumentsDelta = """{"path":"artifacts","pattern":"needle"}"""
            },
            new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason = "tool_calls" }
        ]);
        client.Enqueue(FakeChatClient.ToolCall("search_text", """{"path":"artifacts/copy0","pattern":"needle"}""",
                                               "narrow"));
        client.Enqueue(FakeChatClient.ToolCall("search_text", """{"path":"artifacts/copy0","pattern":"needle"}""",
                                               "retry"));
        client.Enqueue(FakeChatClient.Text("found"));
        var loader = new ProjectInstructionLoader(dir.Workspace);
        using var host =
            new ChatSessionHost(dir.Root,
                                new SessionModelIdentity("test-model", "https://example.invalid/v1",
                                                         ChatApiKind.ChatCompletions),
                                new AgentOptions
                                    { Model = "test-model", MaxAgentSteps = 10, DefaultToolTimeoutSeconds = 30 },
                                client,
                                new ToolRegistry([
                                    new ApplyPatchTool(dir.Workspace), new SearchTextTool(dir.Workspace)
                                ]), "system",
                                sessionWriterFactory : id => new SessionStoreWriter(store.Root, id),
                                instructionLoader : loader);
        var run = await host.SubmitTurnAsync("search", CancellationToken.None);
        Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(run.Result.ToolExecutions, Is.EqualTo(1));
        Assert.That(run.Result.ToolCalls!.Take(2).All(call => call.State == ToolCallExecutionState.KnownNotStarted),
                    Is.True);
        Assert.That(await File.ReadAllTextAsync(Path.Combine(dir.Root, "untouched.txt")), Is.EqualTo("old\n"));
        Assert.That(run.Result.ToolCalls!.First().NotStartedReason,
                    Does.Contain("8,000-token").And.Contain("narrower path"));
        Assert.That(client.RequestLog[1].Messages.Last(message => message.Role == ChatRole.Tool).Content,
                    Does.Contain("no tool in this round ran"));
        Assert.That(client.RequestLog[3].Messages.Last(message => message.Role == ChatRole.Tool).Content,
                    Does.Contain("artifacts/copy0/data.txt:1: needle"));
        var restored = await SessionStoreReader.ReadAsync(store.Root, host.Session.Id, CancellationToken.None);
        Assert.That(restored!.Turns.Single().Runs.Single().ToolCalls!.First().NotStartedReason,
                    Is.EqualTo(run.Result.ToolCalls!.First().NotStartedReason));
    }

    [Test]
    public async Task SiblingFileReadsReplaceTheModelRulesAndRestoreOnlyTheLastActiveScope()
    {
        using var dir   = new TestTempDir();
        using var store = new TestTempDir();
        dir.WriteFile("AGENTS.md", "ROOT_RULE");
        dir.WriteFile("a/AGENTS.md", "A_LOCAL_RULE");
        dir.WriteFile("b/AGENTS.md", "B_LOCAL_RULE");
        dir.WriteFile("a/file.txt", "first");
        dir.WriteFile("b/file.txt", "second");
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":"a/file.txt"}""", "a_old"));
        client.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":"a/file.txt"}""", "a_new"));
        client.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":"b/file.txt"}""", "b_old"));
        client.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":"b/file.txt"}""", "b_new"));
        client.Enqueue(FakeChatClient.Text("done"));
        using var host =
            new ChatSessionHost(dir.Root,
                                new SessionModelIdentity("test-model", "https://example.invalid/v1",
                                                         ChatApiKind.ChatCompletions),
                                new AgentOptions
                                    { Model = "test-model", MaxAgentSteps = 10, DefaultToolTimeoutSeconds = 30 },
                                client,
                                new ToolRegistry([new ReadFileTool(dir.Workspace)]), "system",
                                sessionWriterFactory : id => new SessionStoreWriter(store.Root, id),
                                instructionLoader : new ProjectInstructionLoader(dir.Workspace));
        var run = await host.SubmitTurnAsync("read both files", CancellationToken.None);
        Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(run.Result.ToolExecutions, Is.EqualTo(2));
        Assert.That(client.RequestLog[1].Messages[1].Content, Does.Contain("A_LOCAL_RULE"));
        Assert.That(client.RequestLog[3].Messages[1].Content,
                    Does.Contain("B_LOCAL_RULE").And.Not.Contain("A_LOCAL_RULE"));
        var stored   = await SessionStoreReader.ReadAsync(store.Root, host.Session.Id, CancellationToken.None);
        var restored = SessionResume.Restore(stored!, dir.Root, "system");
        Assert.That(restored.Context.ProjectInstructions!.Documents.Select(document => document.ScopeDirectory),
                    Is.EqualTo([".", "b"]));
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
