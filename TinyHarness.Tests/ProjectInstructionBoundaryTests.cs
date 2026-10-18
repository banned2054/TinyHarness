using System.Text.Json.Nodes;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.ProjectInstructions;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Agent;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.ProjectInstructions;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

public class ProjectInstructionBoundaryTests
{
    private static readonly SessionModelIdentity Model = new("test-model", "https://example.invalid/v1",
                                                             ChatApiKind.ChatCompletions);

    [Test]
    public async Task SecondInstructionChangeAfterDeferral_FailsWithoutExecutingOldCalls()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("fake", "{}", "old_1"));
        client.Enqueue(FakeChatClient.ToolCall("fake", "{}", "old_2"));

        var tool = new FakeTool("fake");
        var boundary = new SequencedInstructionBoundary(
                                                        [
                                                            new InstructionBoundaryCheck(InstructionBoundaryStatus
                                                               .Updated, null, "revision-2", null),
                                                            new InstructionBoundaryCheck(InstructionBoundaryStatus
                                                               .Unchanged, null, null, null)
                                                        ],
                                                        [
                                                            new InstructionBoundaryCheck(InstructionBoundaryStatus
                                                               .Updated, null, "revision-3", null)
                                                        ]);
        var approver = new ScriptedApprover();
        approver.Enqueue(ApprovalAction.AllowOnce);
        var loop = new AgentLoop(client, new ToolRegistry([tool]), Options(), new PermissionEngine("."),
                                 approver, null, null, null, null, boundary);

        var result = await loop.RunAsync("sys", "inspect nested scope", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Failed));
        Assert.That(result.Error, Does.Contain("changed again"));
        Assert.That(client.Requests, Is.EqualTo(2));
        Assert.That(tool.ExecuteCount, Is.EqualTo(0));
        Assert.That(approver.Prompts, Is.EqualTo(1));
        Assert.That(result.ToolCalls!.Select(record => record.State),
                    Is.EqualTo([
                        ToolCallExecutionState.KnownNotStarted, ToolCallExecutionState.KnownNotStarted
                    ]));
        Assert.That(result.ToolCalls![0].InvocationId, Is.Not.EqualTo(result.ToolCalls[1].InvocationId));
        Assert.That(boundary.ApplyCalls, Is.EqualTo(1));
        Assert.That(loop.History.Count(message => message.Role == ChatRole.Tool), Is.EqualTo(2));
        Assert.That(loop.History.Where(message => message.Role == ChatRole.Tool)
                        .All(message => message.Content.Contains("project instructions changed")), Is.True);
        Assert.DoesNotThrow(() => ChatHistoryInvariants.EnsureCompleteToolGroups(loop.History));
    }

    [Test]
    public async Task InstructionChangeDuringDispatch_KeepsConfirmedResultAndDefersLaterCalls()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCalls("fake", 2, "round"));
        client.Enqueue(FakeChatClient.Text("done"));

        var tool = new FakeTool("fake");
        var boundary =
            new SequencedInstructionBoundary([
                                                 new InstructionBoundaryCheck(InstructionBoundaryStatus.Unchanged, null,
                                                                              null, null)
                                             ],
                                             [
                                                 new InstructionBoundaryCheck(InstructionBoundaryStatus.Unchanged, null,
                                                                              null, null),
                                                 new InstructionBoundaryCheck(InstructionBoundaryStatus.Updated, null,
                                                                              "revision-2", null)
                                             ]);
        var loop = new AgentLoop(client, new ToolRegistry([tool]), Options(), null, null, null, null, null, null,
                                 boundary);

        var result = await loop.RunAsync("sys", "run two calls", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(tool.ExecuteCount, Is.EqualTo(1));
        Assert.That(client.Requests, Is.EqualTo(2));
        Assert.That(boundary.ApplyCalls, Is.EqualTo(1));
        Assert.That(result.ToolCalls![0].State, Is.EqualTo(ToolCallExecutionState.ConfirmedResult));
        Assert.That(result.ToolCalls[1].State, Is.EqualTo(ToolCallExecutionState.KnownNotStarted));
        Assert.That(loop.History.Count(message => message.Role == ChatRole.Tool), Is.EqualTo(2));
    }

    [Test]
    public async Task InstructionLoadFailure_FailsWholeToolRoundWithoutExecution()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCalls("fake", 2, "round"));

        var tool = new FakeTool("fake");
        var boundary = new SequencedInstructionBoundary([
            new InstructionBoundaryCheck(InstructionBoundaryStatus.Failed, "instruction probe failed", null, null)
        ], []);
        var loop = new AgentLoop(client, new ToolRegistry([tool]), Options(), null, null, null, null, null, null,
                                 boundary);

        var result = await loop.RunAsync("sys", "run", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Failed));
        Assert.That(result.Error, Is.EqualTo("instruction probe failed"));
        Assert.That(tool.ExecuteCount, Is.EqualTo(0));
        Assert.That(result.ToolCalls!.Select(record => record.State),
                    Is.EqualTo([
                        ToolCallExecutionState.KnownNotStarted, ToolCallExecutionState.KnownNotStarted
                    ]));
        Assert.That(loop.History.Count(message => message.Role == ChatRole.Tool), Is.EqualTo(2));
    }

    [Test]
    public void ClearTemporaryAllows_PreservesDenialsAndInspectionBlock()
    {
        var preparation = new ToolPreparation
        {
            ToolName   = "apply_patch",
            CallId     = "call_1",
            Arguments  = [],
            Capability = "filesystem.write",
            Summary    = "write"
        };
        var engine = new PermissionEngine(".");
        engine.GrantOnce(preparation);
        Assert.That(engine.Decide(preparation), Is.EqualTo(PermissionDecision.Allow));
        engine.ClearTemporaryAllows();
        Assert.That(engine.Decide(preparation), Is.EqualTo(PermissionDecision.Ask));

        engine.GrantSession(preparation);
        Assert.That(engine.Decide(preparation), Is.EqualTo(PermissionDecision.Allow));
        engine.DenySession(preparation);
        engine.SetNonReadOnlyBlock("inspection pending");

        engine.ClearTemporaryAllows();

        Assert.That(engine.Decide(preparation), Is.EqualTo(PermissionDecision.Deny));
        Assert.That(engine.NonReadOnlyBlockReason, Is.EqualTo("inspection pending"));
    }

    [Test]
    public async Task HostRefreshContinuesFromCurrentSnapshotAcrossTurns()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("AGENTS.md", "ROOT_FIRST_RULE");
        dir.WriteFile("nested/AGENTS.md", "LOCAL_RULE_KEEP");
        var loader  = new ProjectInstructionLoader(dir.Workspace);
        var initial = await loader.LoadAsync(null, [], CancellationToken.None);
        var client  = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("scoped", "{}", "discover_local"));
        client.Enqueue(FakeChatClient.Text("local rules loaded"));
        client.Enqueue(FakeChatClient.Text("next turn"));
        var tool = new ScopedInstructionTool(Path.Combine(dir.Root, "nested"));
        using var host = new ChatSessionHost(dir.Root, Model, Options(), client, new ToolRegistry([tool]),
                                             "sys", instructionLoader : loader, initialInstructions : initial);

        await host.SubmitTurnAsync("inspect local scope", CancellationToken.None);
        dir.WriteFile("AGENTS.md", "ROOT_SECOND_RULE");
        await host.SubmitTurnAsync("continue", CancellationToken.None);

        Assert.That(client.Requests, Is.EqualTo(3));
        Assert.That(client.RequestLog[0].Messages.Any(message => message.Content.Contains("ROOT_FIRST_RULE")),
                    Is.True);
        Assert.That(client.RequestLog[1].Messages.Any(message => message.Content.Contains("LOCAL_RULE_KEEP")),
                    Is.True);
        var nextView = client.RequestLog[2].Messages;
        Assert.That(nextView.Any(message => message.Content.Contains("ROOT_SECOND_RULE")), Is.True);
        Assert.That(nextView.Any(message => message.Content.Contains("LOCAL_RULE_KEEP")), Is.True);
        Assert.That(nextView.Any(message => message.Content.Contains("ROOT_FIRST_RULE")), Is.False);
    }

    [Test]
    public async Task SameRevisionScopeDiscovery_CommitsScopesOnlyWithoutEventOrGrantReset()
    {
        using var workspace = new TestTempDir();
        using var store     = new TestTempDir();
        workspace.WriteFile("AGENTS.md", "ROOT_RULE");
        Directory.CreateDirectory(Path.Combine(workspace.Root, "nested"));
        var loader  = new ProjectInstructionLoader(workspace.Workspace);
        var initial = await loader.LoadAsync(null, [], CancellationToken.None);
        var client  = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("scoped", "{}"));
        client.Enqueue(FakeChatClient.ToolCall("scoped", "{}", "call_2"));
        client.Enqueue(FakeChatClient.Text("done"));
        var tool     = new ScopedInstructionTool(Path.Combine(workspace.Root, "nested"), "filesystem.write");
        var approver = new ScriptedApprover();
        approver.Enqueue(ApprovalAction.AllowSession);
        var events = new TestHostEventSink();
        using var host = new ChatSessionHost(workspace.Root, Model, Options(), client, new ToolRegistry([tool]),
                                             "sys", new PermissionEngine(workspace.Root), approver, events,
                                             sessionWriterFactory : sessionId =>
                                                 new SessionStoreWriter(store.Root, sessionId),
                                             instructionLoader : loader, initialInstructions : initial);

        var run = await host.SubmitTurnAsync("write twice", CancellationToken.None);

        Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(tool.ExecuteCount, Is.EqualTo(2));
        Assert.That(approver.Prompts, Is.EqualTo(1));
        Assert.That(events.Events.Count(@event => @event.Kind == HostEventKind.ProjectInstructionsChanged),
                    Is.EqualTo(1));

        var restored = await SessionStoreReader.ReadAsync(store.Root, host.Session.Id, CancellationToken.None);
        TestAssert.NotNull(restored);
        var records = restored!.Conversation!.ProjectInstructionRecords;
        Assert.That(records.Select(record => record.Action),
                    Is.EqualTo(["rules-applied", "scopes-tracked"]));
        Assert.That(records[1].Revision, Is.EqualTo(records[0].Revision));
    }

    [Test]
    public async Task LoaderFailureCompletesOldToolGroupAsKnownNotStartedAndFailsRun()
    {
        using var workspace = new TestTempDir();
        using var store     = new TestTempDir();
        workspace.WriteFile("AGENTS.md", "ROOT_RULE");
        Directory.CreateDirectory(Path.Combine(workspace.Root, "nested"));
        await File.WriteAllBytesAsync(Path.Combine(workspace.Root, "nested", "AGENTS.md"),
                                      [0xFF, 0xFE, 0x00]);
        var loader  = new ProjectInstructionLoader(workspace.Workspace);
        var initial = await loader.LoadAsync(null, [], CancellationToken.None);
        var client  = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("scoped", "{}", "invalid_local"));
        var    tool = new ScopedInstructionTool(Path.Combine(workspace.Root, "nested"));
        string sessionId;
        using (var host = new ChatSessionHost(workspace.Root, Model, Options(), client, new ToolRegistry([tool]),
                                              "sys",
                                              sessionWriterFactory : sessionId =>
                                                  new SessionStoreWriter(store.Root, sessionId),
                                              instructionLoader : loader, initialInstructions : initial))
        {
            sessionId = host.Session.Id;
            var run = await host.SubmitTurnAsync("inspect invalid local rules", CancellationToken.None);
            Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Failed));
            Assert.That(run.Result.ToolCalls!.Single().State, Is.EqualTo(ToolCallExecutionState.KnownNotStarted));
            Assert.That(run.Result.ToolCalls!.Single().NotStartedReason,
                        Does.Contain("not started").And.Contain("UTF-8"));
            Assert.That(host.InspectionPending, Is.False);
        }

        Assert.That(tool.ExecuteCount, Is.EqualTo(0));
        Assert.That(client.Requests, Is.EqualTo(1));
        var restored = await SessionStoreReader.ReadAsync(store.Root, sessionId, CancellationToken.None);
        TestAssert.NotNull(restored);
        var messages = SessionResume.Restore(restored!, workspace.Root, Model, "sys").Context.Messages;
        var reason   = restored!.Turns.Single().Runs.Single().ToolCalls!.Single().NotStartedReason;
        Assert.That(reason, Does.Contain("not started").And.Contain("UTF-8"));
        var history =
            await new SessionHistoryReader().ReadPageAsync(store.Root, sessionId, 200, null, CancellationToken.None);
        Assert.That(history.Items.Single(item => item.Kind == "tool").NotStartedReason, Is.EqualTo(reason));
        Assert.That(messages.Count(message => message is { Role: ChatRole.Assistant, ToolCalls.Count: 1 }),
                    Is.EqualTo(1));
        Assert.That(messages.Single(message => message.Role == ChatRole.Tool).Content,
                    Does.Contain("project instructions").And.Contains("not started").Or
                        .Contains("could not be refreshed"));
        Assert.DoesNotThrow(() => ChatHistoryInvariants.EnsureCompleteToolGroups(messages));
    }

    private static AgentOptions Options() => new()
    {
        Model                     = "test-model",
        MaxAgentSteps             = 10,
        DefaultToolTimeoutSeconds = 30
    };

    private sealed class ScriptedApprover : IApprovalProvider
    {
        private readonly Queue<ApprovalAction> _actions = new();
        public           int                   Prompts { get; private set; }

        public Task<ApprovalAction> PromptAsync(ToolPreparation preparation, CancellationToken cancellationToken)
        {
            Prompts++;
            return Task.FromResult(_actions.Dequeue());
        }

        public void Enqueue(ApprovalAction action) => _actions.Enqueue(action);
    }

    private sealed class ScopedInstructionTool(string scopeDirectory, string capability = "filesystem.read") : ITool
    {
        public int ExecuteCount { get; private set; }

        public ToolDefinition Definition { get; } = new()
        {
            Name        = "scoped",
            Description = "Scoped test tool",
            Parameters  = new JsonObject { ["type"] = "object" }
        };

        public ToolPreparation Prepare(ChatToolCall call) => new()
        {
            ToolName          = Definition.Name,
            CallId            = call.Id,
            Arguments         = [],
            Capability        = capability,
            Summary           = "scoped tool",
            InstructionScopes = [new InstructionScope(scopeDirectory, InstructionScopeKind.Directory)]
        };

        public Task<ToolResult> ExecuteAsync(ToolPreparation preparation, CancellationToken cancellationToken)
        {
            ExecuteCount++;
            return Task.FromResult(new ToolResult { Succeeded = true, Content = "scoped ok" });
        }
    }

    private sealed class SequencedInstructionBoundary(
        IReadOnlyList<InstructionBoundaryCheck> roundChecks,
        IReadOnlyList<InstructionBoundaryCheck> dispatchChecks) : IAgentInstructionBoundary
    {
        private int _dispatchIndex;
        private int _roundIndex;

        public int ApplyCalls { get; private set; }

        public Task PrepareUserContextAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<ProjectInstructionsChanged?> ConfirmInitialCommitAsync(CancellationToken cancellationToken) =>
            Task.FromResult<ProjectInstructionsChanged?>(null);

        public Task<ProjectInstructionsChanged?> RefreshBeforeModelRequestAsync(CancellationToken cancellationToken) =>
            Task.FromResult<ProjectInstructionsChanged?>(null);

        public Task<InstructionBoundaryCheck> CheckToolRoundAsync(
            IReadOnlyList<ToolPreparation> preparations, CancellationToken cancellationToken) =>
            Task.FromResult(roundChecks[_roundIndex++]);

        public Task<InstructionBoundaryCheck> RecheckBeforeDispatchAsync(
            IReadOnlyList<ToolPreparation> preparations, CancellationToken cancellationToken) =>
            Task.FromResult(dispatchChecks[_dispatchIndex++]);

        public Task<ProjectInstructionsChanged?> ApplyToolRoundUpdateAsync(
            IReadOnlyList<ToolCallRecord> records, CancellationToken cancellationToken)
        {
            ApplyCalls++;
            return Task.FromResult<ProjectInstructionsChanged?>(null);
        }
    }
}
