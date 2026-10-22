using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Agent;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

public class RunBudgetTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void DisabledDeadlineRecordsTimeBeyondOldLimitAndPreservesUserCancellation(bool explicitZero)
    {
        var       clock        = new ElapsedTimeProvider();
        using var cancellation = new CancellationTokenSource();
        using var budget =
            new RunBudget(explicitZero ? new RunBudgetLimits { MaxWallClockSeconds = 0 } : new RunBudgetLimits(),
                          cancellation.Token, clock);
        clock.Advance(TimeSpan.FromSeconds(901));
        budget.Check();
        using (var charge = budget.BeginRequest(10)) charge.Complete(10, 5);

        budget.CheckTool();
        budget.ToolStarted();
        Assert.That(budget.Snapshot.ElapsedMilliseconds, Is.EqualTo(901_000));
        Assert.That(budget.Snapshot.AccountedTokens, Is.EqualTo(15));
        Assert.That(budget.Snapshot.ToolExecutions, Is.EqualTo(1));
        Assert.That(budget.DeadlineReached, Is.False);
        Assert.That(budget.Token.IsCancellationRequested, Is.False);

        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(budget.Check);
        Assert.That(budget.Snapshot.LimitReason, Is.Null);
    }

    private static AgentOptions Options(RunBudgetLimits? limits = null) => new()
    {
        Model                     = "test",
        MaxAgentSteps             = 20,
        DefaultToolTimeoutSeconds = 30,
        Budget                    = limits ?? new RunBudgetLimits()
    };

    [Test]
    public async Task MissingUsage_IsEstimatedWithoutInventingReportedUsage()
    {
        var model = new FakeChatClient();
        model.Enqueue(FakeChatClient.Text("answer"));
        var result = await new AgentLoop(model, new ToolRegistry([]), Options()).RunAsync("sys", "go", default);
        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(result.InputTokens, Is.Null);
        Assert.That(result.OutputTokens, Is.Null);
        Assert.That(result.Budget!.HasEstimatedTokens, Is.True);
        Assert.That(result.Budget.AccountedTokens, Is.GreaterThan(0));
        Assert.That(result.Budget.ModelRequests, Is.EqualTo(1));
    }

    [TestCase(10, 5, false)]
    [TestCase(10, null, true)]
    [TestCase(null, 5, true)]
    public async Task ReportedUsage_ReplacesOnlyTheReportedDirection(int? input, int? output, bool estimated)
    {
        var model = new FakeChatClient();
        model.Enqueue([
            new ChatStreamEvent { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = "answer" },
            new ChatStreamEvent
            {
                Kind = ChatStreamEventKind.End, FinishReason = "stop", InputTokens = input, OutputTokens = output
            }
        ]);
        var result         = await new AgentLoop(model, new ToolRegistry([]), Options()).RunAsync("sys", "go", default);
        var estimatedInput = TokenEstimator.EstimateMessages(model.RequestLog[0].Messages);
        Assert.That(result.Budget!.AccountedTokens, Is.EqualTo((input ?? estimatedInput) + (output ?? 2)));
        Assert.That(result.Budget.HasEstimatedTokens, Is.EqualTo(estimated));
        Assert.That(result.InputTokens, Is.EqualTo(input));
        Assert.That(result.OutputTokens, Is.EqualTo(output));
    }

    [Test]
    public async Task RequestLimit_PreventsAnotherRequest()
    {
        var model = new FakeChatClient();
        var tool  = new ProbeTool();
        model.Enqueue(FakeChatClient.ToolCall("probe", "{}"));
        var result = await new AgentLoop(model, new ToolRegistry([tool]),
                                         Options(new RunBudgetLimits { MaxModelRequests = 1 }))
           .RunAsync("sys", "go", default);
        Assert.That(result.Status, Is.EqualTo(AgentStatus.BudgetLimitReached));
        Assert.That(result.Budget!.LimitReason, Is.EqualTo("model-requests"));
        Assert.That(model.Requests, Is.EqualTo(1));
        Assert.That(tool.Executions, Is.EqualTo(1));
    }

    [Test]
    public async Task ToolLimit_RecordsRemainingCallsAsNotStartedAndKeepsProtocolComplete()
    {
        var model = new FakeChatClient();
        var tool  = new ProbeTool();
        model.Enqueue(FakeChatClient.ToolCalls("probe", 3, "batch"));
        var loop = new AgentLoop(model, new ToolRegistry([tool]),
                                 Options(new RunBudgetLimits { MaxToolExecutions = 1 }));
        var result = await loop.RunAsync("sys", "go", default);
        Assert.That(result.Status, Is.EqualTo(AgentStatus.BudgetLimitReached));
        Assert.That(result.Budget!.LimitReason, Is.EqualTo("tool-executions"));
        Assert.That(result.ToolExecutions, Is.EqualTo(1));
        Assert.That(result.Budget.ToolExecutions, Is.EqualTo(1));
        Assert.That(result.ToolCalls!.Select(call => call.State), Is.EqualTo(new[]
        {
            ToolCallExecutionState.ConfirmedResult, ToolCallExecutionState.KnownNotStarted,
            ToolCallExecutionState.KnownNotStarted
        }));
        Assert.DoesNotThrow(() => ChatHistoryInvariants.EnsureCompleteToolGroups(loop.History));
    }

    [Test]
    public async Task DeniedCalls_DoNotConsumeToolExecutions()
    {
        var model = new FakeChatClient();
        var tool  = new ProbeTool("filesystem.write");
        model.Enqueue(FakeChatClient.ToolCalls("probe", 3, "denied"));
        model.Enqueue(FakeChatClient.Text("denied"));
        var result = await new AgentLoop(model, new ToolRegistry([tool]),
                                         Options(new RunBudgetLimits { MaxToolExecutions = 1 }),
                                         new PermissionEngine(Path.GetTempPath()), new DenyApprover())
           .RunAsync("sys", "go", default);
        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(result.Budget!.ToolExecutions, Is.Zero);
        Assert.That(tool.Executions, Is.Zero);
    }

    [Test]
    public void DisabledTokenLimit_AccountsLargeEstimatesAndStreamingOutput()
    {
        using var budget = new RunBudget(new RunBudgetLimits(), default);
        using (var charge = budget.BeginRequest(int.MaxValue))
        {
            charge.Append(new ChatStreamEvent { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = "answer" });
            charge.Complete(null, null);
        }

        using (var charge = budget.BeginRequest(100)) charge.Complete(100, 20);

        Assert.That(budget.Snapshot.AccountedTokens, Is.EqualTo((long)int.MaxValue + 2 + 100 + 20));
        Assert.That(budget.Snapshot.HasEstimatedTokens, Is.True);
        Assert.That(budget.Snapshot.ModelRequests, Is.EqualTo(2));
        Assert.That(budget.Snapshot.LimitReason, Is.Null);
    }

    [Test]
    public async Task DefaultTokenLimit_AllowsFurtherToolsAndRequestsAboveTwoMillionTokens()
    {
        var model = new FakeChatClient();
        var tool  = new ProbeTool();
        model.Enqueue(FakeChatClient.ToolCall("probe", "{}").Select(value =>
            value.Kind == ChatStreamEventKind.End
                ? value with { InputTokens = 2_100_000, OutputTokens = 10 }
                : value).ToArray());
        model.Enqueue(FakeChatClient.TextWithUsage(100, 20, "done"));

        var result = await new AgentLoop(model, new ToolRegistry([tool]), Options()).RunAsync("sys", "go", default);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed), result.Error);
        Assert.That(tool.Executions, Is.EqualTo(1));
        Assert.That(result.Budget!.ModelRequests, Is.EqualTo(2));
        Assert.That(result.Budget.AccountedTokens, Is.EqualTo(2_100_130));
        Assert.That(result.Budget.HasEstimatedTokens, Is.False);
        Assert.That(result.Budget.LimitReason, Is.Null);
        Assert.That(result.InputTokens, Is.EqualTo(2_100_100));
        Assert.That(result.OutputTokens, Is.EqualTo(30));
    }

    [Test]
    public async Task TokenLimit_StopsDuringOutputWithoutExecutingPartialToolCall()
    {
        var model    = new FakeChatClient();
        var tool     = new ProbeTool();
        var response = FakeChatClient.ToolCall("probe", "{}").ToList();
        response.Insert(0, new ChatStreamEvent
                            { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = new string('文', 1000) });
        model.Enqueue(response);
        var result = await new AgentLoop(model, new ToolRegistry([tool]),
                                         Options(new RunBudgetLimits { MaxTotalTokens = 200 }))
           .RunAsync("sys", "go", default);
        Assert.That(result.Status, Is.EqualTo(AgentStatus.BudgetLimitReached));
        Assert.That(result.Budget!.LimitReason, Is.EqualTo("tokens"));
        Assert.That(result.Budget.HasEstimatedTokens, Is.True);
        Assert.That(tool.Executions, Is.Zero);
        Assert.That(result.ToolCalls, Is.Empty);
    }

    [Test]
    public async Task ReportedTokenLimit_CannotBecomeCompletedOrStartTools()
    {
        var model = new FakeChatClient();
        model.Enqueue(FakeChatClient.TextWithUsage(1000, 1000, "done"));
        var result = await new AgentLoop(model, new ToolRegistry([]),
                                         Options(new RunBudgetLimits { MaxTotalTokens = 500 }))
           .RunAsync("sys", "go", default);
        Assert.That(result.Status, Is.EqualTo(AgentStatus.BudgetLimitReached));
        Assert.That(result.InputTokens, Is.EqualTo(1000));
        Assert.That(result.OutputTokens, Is.EqualTo(1000));
        Assert.That(result.Budget!.AccountedTokens, Is.EqualTo(2000));
        Assert.That(result.Budget.HasEstimatedTokens, Is.False);
    }

    [Test]
    public async Task IncompleteStream_DoesNotExecuteToolCall()
    {
        var model = new FakeChatClient();
        var tool  = new ProbeTool();
        model.Enqueue(FakeChatClient.ToolCall("probe", "{}").Where(value => value.Kind != ChatStreamEventKind.End)
                                    .ToArray());
        var result = await new AgentLoop(model, new ToolRegistry([tool]), Options()).RunAsync("sys", "go", default);
        Assert.That(result.Status, Is.EqualTo(AgentStatus.Failed));
        Assert.That(tool.Executions, Is.Zero);
    }

    [TestCase("model")]
    [TestCase("approval")]
    [TestCase("tool")]
    public async Task Deadline_ReusesCancellationAndPreservesUnknownSideEffects(string stage)
    {
        var scripted = new FakeChatClient();
        scripted.Enqueue(FakeChatClient.ToolCall("probe", "{}"));
        var tool    = new ProbeTool("filesystem.write", stage == "tool");
        var options = Options(new RunBudgetLimits { MaxWallClockSeconds = 1 });
        using var host = new ChatSessionHost(Path.GetTempPath(),
                                             new SessionModelIdentity("test", "https://example.invalid",
                                                                      ChatApiKind.ChatCompletions), options,
                                             stage == "model" ? new WaitingModel() : scripted, new ToolRegistry([tool]),
                                             "sys",
                                             new PermissionEngine(Path.GetTempPath()),
                                             new WaitingApprover(stage == "approval"));
        var result = (await host.SubmitTurnAsync("go", default).WaitAsync(TimeSpan.FromSeconds(10))).Result!;
        Assert.That(result.Status, Is.EqualTo(AgentStatus.BudgetLimitReached));
        Assert.That(result.Budget!.LimitReason, Is.EqualTo("wall-clock"));
        Assert.That(host.InspectionPending, Is.EqualTo(stage == "tool"));
        Assert.That(result.ToolExecutions, Is.EqualTo(stage == "tool" ? 1 : 0));
    }

    [Test]
    public async Task UserCancellation_IsDistinctFromDeadline()
    {
        using var token = new CancellationTokenSource();
        token.Cancel();
        var result =
            await new AgentLoop(new WaitingModel(), new ToolRegistry([]), Options()).RunAsync("sys", "go", token.Token);
        Assert.That(result.Status, Is.EqualTo(AgentStatus.Cancelled));
        Assert.That(result.Budget!.LimitReason, Is.Null);
    }

    [TestCase(4, AgentStatus.BudgetLimitReached)]
    [TestCase(5, AgentStatus.Completed)]
    public async Task CompactionRequests_ConsumeTheSameRequestAndTokenBudget(int requestLimit, AgentStatus status)
    {
        var model = new FakeChatClient();
        var tool  = new FakeTool("t") { ResultContent = new string('x', 1600) };
        model.Enqueue(FakeChatClient.ToolCall("t", "{}", "a"));
        model.Enqueue(FakeChatClient.ToolCall("t", "{}", "b"));
        model.Enqueue(FakeChatClient.ToolCall("t", "{}", "c"));
        model.Enqueue(FakeChatClient.TextWithUsage(100, 20,
                                                   "Goal: inspect. Decision: preserve the API. Continue pending work."));
        model.Enqueue(FakeChatClient.TextWithUsage(200, 30, "done"));
        var options = Options(new RunBudgetLimits { MaxModelRequests = requestLimit }) with
        {
            Context = new ContextOptions { ContextWindowTokens = 1000, ReservedOutputTokens = 30 }
        };
        var result = await new AgentLoop(model, new ToolRegistry([tool]), options).RunAsync("sys", "go", default);
        Assert.That(result.Status, Is.EqualTo(status), result.Error);
        Assert.That(model.RequestLog[3].Tools, Is.Null);
        Assert.That(result.Budget!.ModelRequests, Is.EqualTo(requestLimit));
        Assert.That(result.Compactions, Is.EqualTo(1));
        Assert.That(result.InputTokens, Is.EqualTo(requestLimit  == 4 ? 100 : 300));
        Assert.That(result.OutputTokens, Is.EqualTo(requestLimit == 4 ? 20 : 50));
        Assert.That(result.Budget.AccountedTokens,
                    Is.GreaterThan((result.InputTokens ?? 0) + (result.OutputTokens ?? 0)));
        Assert.That(result.Budget.HasEstimatedTokens, Is.True);
    }

    [TestCase(100)]
    [TestCase(2_100_000)]
    public async Task Journal_RestoresBudgetWithoutCallingModelAgain(int inputTokens)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"m15-budget-{Guid.NewGuid():N}");
        var model     = new FakeChatClient();
        model.Enqueue(FakeChatClient.TextWithUsage(inputTokens, 20, "done"));
        string            id;
        RunBudgetSnapshot expected;
        using (var host = new ChatSessionHost(Path.GetTempPath(),
                                              new SessionModelIdentity("test", "https://example.invalid",
                                                                       ChatApiKind.ChatCompletions), Options(),
                                              model, new ToolRegistry([]), "sys",
                                              sessionWriterFactory : sessionId =>
                                                  new SessionStoreWriter(directory, sessionId)))
        {
            expected = (await host.SubmitTurnAsync("go", default)).Result!.Budget!;
            id       = host.Session.Id;
            Assert.That(host.PersistenceWarning, Is.Null);
        }

        var snapshot = (await SessionStoreReader.ReadAsync(directory, id, default))!;
        Assert.That(snapshot.Turns.Single().Runs.Single().Budget, Is.EqualTo(expected));
        var restored = SessionResume.Restore(snapshot, Path.GetTempPath(), snapshot.Model!, "sys");
        Assert.That(restored.Session.Turns.Single().Runs.Single().Result!.Budget, Is.EqualTo(expected));
        Assert.That(model.Requests, Is.EqualTo(1));
    }

    [TestCase(0L, 0)]
    [TestCase(0L, 10)]
    [TestCase(30_000_000_000L, 0)]
    [TestCase(30_000_000_000L, 10)]
    public async Task TrustedConfiguration_RoundTripsBudget(long tokenLimit, int wallClockSeconds)
    {
        var path = Path.Combine(Path.GetTempPath(), $"m15-config-{Guid.NewGuid():N}.json");
        var limits = new RunBudgetLimits
        {
            MaxModelRequests = 7, MaxToolExecutions = 9, MaxWallClockSeconds = wallClockSeconds,
            MaxTotalTokens   = tokenLimit
        };
        await UserConfigStore.SaveAsync(path,
                                        new UserConfig { Settings = new UserConfigSettings { RunBudget = limits } },
                                        default);
        Assert.That((await UserConfigStore.LoadAsync(path, default)).Settings!.RunBudget, Is.EqualTo(limits));
    }

    [TestCase("{}")]
    [TestCase("{\"maxTotalTokens\":0}")]
    [TestCase("{\"maxWallClockSeconds\":0}")]
    [TestCase("{\"maxWallClockSeconds\":0,\"maxTotalTokens\":0}")]
    public async Task TrustedConfiguration_MissingOrZeroOptionalLimitsKeepsOtherDefaults(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"m15-unlimited-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, "{\"settings\":{\"runBudget\":" + json + "}}");

        var limits = (await UserConfigStore.LoadAsync(path, default)).Settings!.RunBudget!;

        Assert.That(limits.MaxTotalTokens, Is.Zero);
        Assert.That(limits.MaxModelRequests, Is.EqualTo(128));
        Assert.That(limits.MaxToolExecutions, Is.EqualTo(256));
        Assert.That(limits.MaxWallClockSeconds, Is.Zero);
    }

    [TestCase("null")]
    [TestCase("{\"maxModelRequests\":0}")]
    [TestCase("{\"maxWallClockSeconds\":5000000}")]
    [TestCase("{\"maxWallClockSeconds\":-1}")]
    [TestCase("{\"maxTotalTokens\":-1}")]
    [TestCase("{\"typo\":1}")]
    public async Task TrustedConfiguration_RejectsInvalidBudget(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"m15-invalid-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, "{\"settings\":{\"runBudget\":" + json + "}}");
        await TestAssert.ThrowsAsync<InvalidDataException>(async () => await UserConfigStore.LoadAsync(path, default));
    }

    private sealed class ProbeTool(string capability = "filesystem.read", bool wait = false) : ITool
    {
        internal int Executions { get; private set; }

        public ToolDefinition Definition { get; } = new()
            { Name = "probe", Description = "probe", Parameters = new JsonObject { ["type"] = "object" } };

        public ToolPreparation Prepare(ChatToolCall call) => new()
        {
            CallId  = call.Id, ToolName    = Definition.Name, Arguments = new JsonObject(), Capability = capability,
            Summary = "probe", TargetPaths = [Path.GetTempPath()]
        };

        public async Task<ToolResult> ExecuteAsync(ToolPreparation preparation, CancellationToken cancellationToken)
        {
            Executions++;
            if (wait) await Task.Delay(Timeout.Infinite, cancellationToken);
            return new ToolResult { Succeeded = true, Content = "ok" };
        }
    }

    private sealed class DenyApprover : IApprovalProvider
    {
        public Task<ApprovalAction> PromptAsync(ToolPreparation preparation, CancellationToken cancellationToken) =>
            Task.FromResult(ApprovalAction.Deny);
    }

    private sealed class WaitingApprover(bool wait) : IApprovalProvider
    {
        public async Task<ApprovalAction> PromptAsync(ToolPreparation preparation, CancellationToken cancellationToken)
        {
            if (wait) await Task.Delay(Timeout.Infinite, cancellationToken);
            return ApprovalAction.AllowOnce;
        }
    }

    private sealed class WaitingModel : IChatCompletionClient
    {
        public async IAsyncEnumerable<ChatStreamEvent> CompleteAsync(
            ChatCompletionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            yield break;
        }
    }

    private sealed class ElapsedTimeProvider : TimeProvider
    {
        private         long _timestamp;
        public override long TimestampFrequency        => TimeSpan.TicksPerSecond;
        public override long GetTimestamp()            => _timestamp;
        internal        void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            throw new InvalidOperationException("A disabled deadline must not create a timer.");
    }
}
