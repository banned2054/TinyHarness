using System.Text.Json.Nodes;
using TinyHarness.Core.Agent;
using TinyHarness.Core.ChatCompletions;
using TinyHarness.Core.Context;
using TinyHarness.Core.Tools;

namespace TinyHarness.Tests;

/// <summary>
/// M6 Context Manager 集成测试：压缩触发、tools 禁用、原子组完整性、失败回滚、跨轮累积、
/// 模型视图裁剪，以及压缩仍无法容纳窗口时主循环的显式失败（PLAN §13/§17）。
///
/// M6 context-manager integration tests: compaction triggering, disabled tools,
/// atomic-group integrity, rollback on failure, cross-round accumulation, model-view
/// trimming, and the explicit failure when compaction still cannot fit the window
/// (PLAN §13/§17).
/// </summary>
public class ContextCompactionTests
{
    // Tight enough that three oversized rounds trigger compaction, yet wide enough
    // that the post-compaction view (head + structured state + newest turns) fits the
    // window; the loop refuses to send a request that still exceeds the window.
    private const int TightWindow = 1000;

    private static AgentOptions TightBudgetOptions(int maxSteps = 10) => new()
    {
        Model                     = "test-model",
        MaxAgentSteps             = maxSteps,
        DefaultToolTimeoutSeconds = 30,
        Context = new ContextOptions
        {
            ContextWindowTokens  = TightWindow,
            ReservedOutputTokens = 30,
        },
    };

    private static AgentOptions NoContextOptions(int maxSteps = 10) => new()
    {
        Model                     = "test-model",
        MaxAgentSteps             = maxSteps,
        DefaultToolTimeoutSeconds = 30,
    };

    private static string StateJson(string goal, string modified, string decision) => new JsonObject
    {
        ["goal"]               = goal,
        ["constraints"]        = new JsonArray(),
        ["decisions"]          = new JsonArray(decision),
        ["filesInspected"]     = new JsonArray("src"),
        ["filesModified"]      = new JsonArray(modified),
        ["commandsAndResults"] = new JsonArray(),
        ["pendingWork"]        = new JsonArray(),
    }.ToJsonString();

    private static string ToolCallId(string prefix, int round) => $"{prefix}_{round}";

    private static void AppendTurn(ConversationContext context,   string toolName, string id,
                                   string              arguments, string result)
    {
        context.Append(ChatMessage.Assistant(string.Empty, [new ChatToolCall(id, toolName, arguments)]));
        context.Append(ChatMessage.Tool(toolName, id, result));
    }

    private static TwoBatchCase FindTwoBatchCase(ToolDefinition longTool, ToolDefinition shortTool)
    {
        const string arguments   = "{\"path\":\"src/file.cs\"}";
        var          summaryJson = StateJson("goal", "a.cs", "decision");

        for (var window = 700; window <= 5000; window += 50)
        {
            for (var longLength = 80; longLength <= 1600; longLength += 20)
            {
                for (var shortLength = 40; shortLength <= 800; shortLength += 20)
                {
                    var options = new ContextOptions
                    {
                        ContextWindowTokens       = window,
                        ReservedOutputTokens      = 30,
                        CompactionThresholdTokens = window,
                        ToolResultViewCharacters  = 12_000,
                    };
                    var context = new ConversationContext(options);
                    context.Append(ChatMessage.System("sys"));
                    context.Append(ChatMessage.User("go"));
                    var a = new string('A', longLength);
                    var b = new string('B', shortLength);
                    var c = new string('C', shortLength);
                    var d = new string('D', longLength);
                    AppendTurn(context, longTool.Name, "a", arguments, a);
                    AppendTurn(context, shortTool.Name, "b", arguments, b);
                    AppendTurn(context, shortTool.Name, "c", arguments, c);
                    var beforeD = context.EstimateViewTokens();
                    AppendTurn(context, longTool.Name, "d", arguments, d);
                    var afterD = context.EstimateViewTokens();
                    var definitions = new[] { longTool, shortTool };
                    var fixedCost = TokenEstimator.EstimateToolDefinitions(definitions) + options.ReservedOutputTokens;
                    if (beforeD + fixedCost > window || afterD + fixedCost <= window)
                    {
                        continue;
                    }

                    var first = context.BuildCompactionMessages(definitions);
                    if (first.Count                                                           == 0 ||
                        TokenEstimator.EstimateMessages(first) + options.ReservedOutputTokens > window)
                    {
                        continue;
                    }

                    var bMessages = new[]
                    {
                        ChatMessage.Assistant(string.Empty,
                                              [new ChatToolCall("b", shortTool.Name, arguments)]),
                        ChatMessage.Tool(shortTool.Name, "b", b),
                    };
                    if (TokenEstimator.EstimateMessages(first.Concat(bMessages)) + options.ReservedOutputTokens <=
                        window)
                    {
                        continue;
                    }

                    if (!context.TryApplyCompaction(summaryJson))
                    {
                        continue;
                    }

                    var afterFirst = context.EstimateViewTokens() + fixedCost;
                    if (afterFirst <= options.CompactionThresholdTokens)
                    {
                        continue;
                    }

                    var second = context.BuildCompactionMessages(definitions);
                    if (second.Count                                                           == 0 ||
                        TokenEstimator.EstimateMessages(second) + options.ReservedOutputTokens > window)
                    {
                        continue;
                    }

                    if (!context.TryApplyCompaction(summaryJson))
                    {
                        continue;
                    }

                    var afterSecond = context.EstimateViewTokens() + fixedCost;
                    if (afterSecond > options.ContextWindowTokens)
                    {
                        continue;
                    }

                    return new TwoBatchCase(options, arguments, a, b, c, d, first, second, beforeD + fixedCost,
                                            afterD + fixedCost, afterFirst, afterSecond);
                }
            }
        }

        throw new Xunit.Sdk.XunitException("No deterministic two-batch compaction fixture was found.");
    }

    private sealed record TwoBatchCase(
        ContextOptions             Options,
        string                     Arguments,
        string                     A,
        string                     B,
        string                     C,
        string                     D,
        IReadOnlyList<ChatMessage> FirstSummary,
        IReadOnlyList<ChatMessage> SecondSummary,
        int                        BeforeD,
        int                        AfterD,
        int                        AfterFirst,
        int                        AfterSecond);

    [Fact]
    public async Task Compaction_DisablesTools_FoldsOldestTurnAndKeepsNewestCompleteTurn()
    {
        var tool   = new FakeTool("t") { ResultContent = new string('x', 1600) };
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("t", "{}", ToolCallId("a", 1)));
        client.Enqueue(FakeChatClient.ToolCall("t", "{}", ToolCallId("b", 2)));
        client.Enqueue(FakeChatClient.ToolCall("t", "{}", ToolCallId("c", 3)));
        client.Enqueue(FakeChatClient.Text(StateJson("goal-one", "a.cs", "decision-x")));
        client.Enqueue(FakeChatClient.Text("done"));

        var changes = new List<ContextChange>();
        var loop    = new AgentLoop(client, new ToolRegistry([tool]), TightBudgetOptions());
        loop.ContextCompacted += changes.Add;

        var result = await loop.RunAsync("sys", "go", CancellationToken.None);

        Assert.True(result.Status == AgentStatus.Completed,
                    $"{result.Error} requests={client.Requests} compactions={result.Compactions} steps={result.Steps}");
        Assert.Equal(3, result.ToolExecutions);
        Assert.Equal(1, result.Compactions);
        Assert.Equal(5, client.Requests);

        // The summarizer request must disable tools (PLAN §13), carry the original
        // task and constraints on the first fold, and fold exactly the oldest
        // complete turn: assistant a_1 plus its capped tool message.
        var summaryRequest = client.RequestLog[3];
        Assert.Null(summaryRequest.Tools);
        Assert.Equal([ChatRole.System, ChatRole.User, ChatRole.User, ChatRole.Assistant, ChatRole.Tool],
                     summaryRequest.Messages.Select(m => m.Role).ToArray());
        Assert.Contains("Original task", summaryRequest.Messages[2].Content);
        Assert.Equal("a_1", summaryRequest.Messages[3].ToolCalls!.Single().Id);
        Assert.Contains("#1", summaryRequest.Messages[4].Content);

        // The next agent request keeps tools, replaces the folded turn with the state
        // message, and retains the newest complete turn (b_2) plus the in-progress
        // tail (c_3) verbatim and in order.
        var nextRequest = client.RequestLog[4];
        Assert.NotNull(nextRequest.Tools);
        var toolDefinition = Assert.Single(nextRequest.Tools!);
        Assert.Equal([
                         ChatRole.System, ChatRole.User, ChatRole.User, ChatRole.Assistant, ChatRole.Tool,
                         ChatRole.Assistant, ChatRole.Tool
                     ],
                     nextRequest.Messages.Select(m => m.Role).ToArray());

        Assert.True(StructuredState.TryParse(nextRequest.Messages[2].Content, out var state));
        Assert.Equal("goal-one", state.Goal);
        Assert.Equal(["a.cs"], state.FilesModified);
        Assert.Equal(["decision-x"], state.Decisions);

        Assert.Equal("b_2", nextRequest.Messages[3].ToolCalls!.Single().Id);
        Assert.Equal("c_3", nextRequest.Messages[5].ToolCalls!.Single().Id);
        var joined = string.Join('\n', nextRequest.Messages.Select(m => m.Content));
        Assert.Contains("#2", joined);
        Assert.Contains("#3", joined);
        Assert.DoesNotContain("#1", joined);

        // The local full history is untouched: all three rounds, the head, and the
        // final plain-text assistant message remain.
        Assert.Equal(9, loop.History.Count);
        Assert.Contains(loop.History, m => m.Role == ChatRole.Tool && m.Content.Contains("#1"));

        var change = Assert.Single(changes);
        Assert.True(change.BeforeTokens > change.AfterTokens && change.AfterTokens > 0);
    }

    [Fact]
    public async Task CompactionFailure_OverWindowView_FailsRunWithClearError()
    {
        var tool   = new FakeTool("t") { ResultContent = new string('x', 1600) };
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("t", "{}", ToolCallId("a", 1)));
        client.Enqueue(FakeChatClient.ToolCall("t", "{}", ToolCallId("b", 2)));
        client.Enqueue(FakeChatClient.ToolCall("t", "{}", ToolCallId("c", 3)));
        client.Enqueue(FakeChatClient.Text("not json at all")); // summarizer produces garbage
        client.Enqueue(FakeChatClient.Text("done"));

        var loop   = new AgentLoop(client, new ToolRegistry([tool]), TightBudgetOptions());
        var result = await loop.RunAsync("sys", "go", CancellationToken.None);

        // The compaction failed (latched, never retried) and the view still exceeds
        // the window, so the loop refuses to send the over-window request and fails
        // with an explicit error instead of silently continuing.
        Assert.Equal(AgentStatus.Failed, result.Status);
        Assert.Equal(0, result.Compactions);
        Assert.Equal(4, client.Requests); // three agent requests + exactly one failed summary attempt
        Assert.Contains("window", result.Error);
        Assert.Equal(8, loop.History.Count); // head + three rounds, untouched
    }

    [Fact]
    public async Task CompactionFailure_RollsBackAndContinues_WhenViewStillFitsTheWindow()
    {
        var tool   = new FakeTool("t") { ResultContent = new string('x', 1600) };
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("t", "{}", ToolCallId("a", 1)));
        client.Enqueue(FakeChatClient.ToolCall("t", "{}", ToolCallId("b", 2)));
        client.Enqueue(FakeChatClient.ToolCall("t", "{}", ToolCallId("c", 3)));
        client.Enqueue(FakeChatClient.Text("not json at all")); // summarizer produces garbage
        client.Enqueue(FakeChatClient.Text("done"));

        // Compaction triggers below the window (threshold 400) so a failed summary
        // can roll back and let the loop continue: the over-threshold view still
        // fits the declared window and is sent as-is.
        var options = new AgentOptions
        {
            Model                     = "test-model",
            MaxAgentSteps             = 10,
            DefaultToolTimeoutSeconds = 30,
            Context = new ContextOptions
            {
                ContextWindowTokens       = 1500,
                ReservedOutputTokens      = 30,
                CompactionThresholdTokens = 1100,
            },
        };
        var loop   = new AgentLoop(client, new ToolRegistry([tool]), options);
        var result = await loop.RunAsync("sys", "go", CancellationToken.None);

        Assert.True(result.Status == AgentStatus.Completed,
                    $"{result.Error} requests={client.Requests} steps={result.Steps} compactions={result.Compactions}");
        Assert.Equal(0, result.Compactions);
        Assert.Equal(5, client.Requests); // exactly one failed summary attempt, never retried

        // The failed compaction leaves the previous view intact: no state message was
        // inserted and the oldest turn is still sent verbatim.
        var nextRequest = client.RequestLog[4];
        Assert.Equal(1, nextRequest.Messages.Count(m => m.Role == ChatRole.User));
        var joined = string.Join('\n', nextRequest.Messages.Select(m => m.Content));
        Assert.Contains("#1", joined);
        Assert.DoesNotContain("goal-one", joined);
        Assert.Equal(9, loop.History.Count); // three rounds + head + final assistant message
    }

    [Fact]
    public async Task TwoCompactions_FeedPreviousStateForwardAndAccumulate()
    {
        var tool   = new FakeTool("t") { ResultContent = new string('x', 1600) };
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("t", "{}", ToolCallId("a", 1)));
        client.Enqueue(FakeChatClient.ToolCall("t", "{}", ToolCallId("b", 2)));
        client.Enqueue(FakeChatClient.ToolCall("t", "{}", ToolCallId("c", 3)));
        client.Enqueue(FakeChatClient.Text(StateJson("goal-one", "a.cs", "decision-x")));
        client.Enqueue(FakeChatClient.ToolCall("t", "{}", ToolCallId("d", 4)));
        // The second summary must carry the first state's facts forward (the lists
        // accumulate), otherwise the commit would be rejected for dropping them.
        client.Enqueue(FakeChatClient.Text(new JsonObject
        {
            ["goal"]               = "goal-two",
            ["constraints"]        = new JsonArray(),
            ["decisions"]          = new JsonArray("decision-x", "decision-y"),
            ["filesInspected"]     = new JsonArray("src"),
            ["filesModified"]      = new JsonArray("a.cs", "b.cs"),
            ["commandsAndResults"] = new JsonArray(),
            ["pendingWork"]        = new JsonArray(),
        }.ToJsonString()));
        client.Enqueue(FakeChatClient.Text("done"));

        var changes = new List<ContextChange>();
        var loop    = new AgentLoop(client, new ToolRegistry([tool]), TightBudgetOptions(maxSteps : 12));
        loop.ContextCompacted += changes.Add;

        var result = await loop.RunAsync("sys", "go", CancellationToken.None);

        Assert.Equal(AgentStatus.Completed, result.Status);
        Assert.Equal(4, result.ToolExecutions);
        Assert.Equal(2, result.Compactions);
        Assert.Equal(7, client.Requests);

        // The first summarization sees the original task and folds a_1; the second
        // summarization sees the first state as input (accumulation) and folds the
        // next oldest complete turn b_2.
        var firstSummary = client.RequestLog[3];
        Assert.Equal([ChatRole.System, ChatRole.User, ChatRole.User, ChatRole.Assistant, ChatRole.Tool],
                     firstSummary.Messages.Select(m => m.Role).ToArray());

        var secondSummary = client.RequestLog[5];
        Assert.Null(secondSummary.Tools);
        Assert.Contains("goal-one", secondSummary.Messages[1].Content);
        Assert.Equal("b_2", secondSummary.Messages[2].ToolCalls!.Single().Id);
        Assert.Contains("#2", secondSummary.Messages[3].Content);

        // The final agent view carries the accumulated second state: facts from the
        // first summary survive (goal carried over, a.cs and decision-x retained) and
        // raw turns folded into it (a_1, b_2) are gone while the retained turns remain.
        var finalRequest = client.RequestLog[6];
        Assert.Equal([
                         ChatRole.System, ChatRole.User, ChatRole.User, ChatRole.Assistant, ChatRole.Tool,
                         ChatRole.Assistant, ChatRole.Tool
                     ],
                     finalRequest.Messages.Select(m => m.Role).ToArray());
        Assert.True(StructuredState.TryParse(finalRequest.Messages[2].Content, out var state));
        Assert.Equal("goal-two", state.Goal);
        Assert.Contains("a.cs", state.FilesModified);
        Assert.Contains("b.cs", state.FilesModified);
        Assert.Contains("decision-x", state.Decisions);
        Assert.Contains("decision-y", state.Decisions);

        var joined = string.Join('\n', finalRequest.Messages.Select(m => m.Content));
        Assert.Contains("#3", joined);
        Assert.Contains("#4", joined);
        Assert.DoesNotContain("#1", joined);
        Assert.DoesNotContain("#2", joined);

        Assert.Equal(2, changes.Count);
    }

    [Fact]
    public async Task MultipleToolCallRounds_CompactionThenCompleteSuccessfully()
    {
        var tool          = new FakeTool("t") { ResultContent = new string('x', 1400) };
        var client        = new FakeChatClient();
        var argumentsJson = $"{{\"padding\":\"{new string('a', 300)}\"}}";
        for (var round = 1; round <= 3; round++)
        {
            client.Enqueue(FakeChatClient.ToolCalls("t", count : 3, idPrefix : $"round{round}", argumentsJson));
        }

        var summary = new JsonObject
        {
            ["goal"] = "goal",
            ["constraints"] = new JsonArray(),
            ["decisions"] = new JsonArray(Enumerable.Range(1, 18).Select(i => (JsonNode?)$"decision-{i}").ToArray()),
            ["filesInspected"] = new JsonArray(Enumerable.Range(1, 5).Select(i => (JsonNode?)$"file-{i}.cs").ToArray()),
            ["filesModified"] = new JsonArray("file.cs"),
            ["commandsAndResults"] = new JsonArray(),
            ["pendingWork"] = new JsonArray(),
        }.ToJsonString();

        client.Enqueue(FakeChatClient.Text(summary));

        client.Enqueue(FakeChatClient.Text("done"));

        var changes = new List<ContextChange>();
        var loop = new AgentLoop(client, new ToolRegistry([tool]), new AgentOptions
        {
            Model                     = "test-model",
            MaxAgentSteps             = 5,
            DefaultToolTimeoutSeconds = 30,
            Context = new ContextOptions
            {
                ContextWindowTokens       = 2900,
                ReservedOutputTokens      = 30,
                CompactionThresholdTokens = 2800,
            },
        });
        loop.ContextCompacted += changes.Add;

        var result = await loop.RunAsync("sys", "go", CancellationToken.None);

        Assert.True(result.Status == AgentStatus.Completed,
                    $"{result.Error} requests={client.Requests} compactions={result.Compactions} steps={result.Steps}");
        Assert.Equal(9, result.ToolExecutions);
        Assert.Equal(1, result.Compactions);
        Assert.Equal(4, result.Steps);    // 3 tool requests + 1 final text request.
        Assert.Equal(5, client.Requests); // 3 tool + summary + final.
        Assert.Single(changes);

        // The summary request does not advance Agent steps and never receives tools;
        // the request after it is the first normal request again.
        Assert.Null(client.RequestLog[3].Tools);
        Assert.NotNull(client.RequestLog[4].Tools);
        Assert.Equal("done", result.FinalMessage);
        Assert.Equal(15, loop.History.Count); // head + 3 complete rounds + final assistant.
        Assert.Contains(loop.History, m => m.Role             == ChatRole.Assistant &&
                                           m.ToolCalls!.Count == 3                  &&
                                           m.ToolCalls.All(call => call.FunctionName == "t"));
        Assert.Equal(9, loop.History.Count(m => m.Role == ChatRole.Tool));
        Assert.All(loop.History.Where(m => m.Role == ChatRole.Tool), message =>
                       Assert.Contains("#", message.Content));
    }

    [Fact]
    public async Task SameStep_TwoCompactionBatches_CompleteSuccessfully()
    {
        var definitions = (new FakeTool("long"), new FakeTool("short"));
        var fixture     = FindTwoBatchCase(definitions.Item1.Definition, definitions.Item2.Definition);
        var longTool    = new FakeTool("long") { ResultContent  = fixture.A };
        var shortTool   = new FakeTool("short") { ResultContent = fixture.B };
        var client      = new FakeChatClient();

        client.Enqueue(FakeChatClient.ToolCall("long", fixture.Arguments, "a"));
        client.Enqueue(FakeChatClient.ToolCall("short", fixture.Arguments, "b"));
        client.Enqueue(FakeChatClient.ToolCall("short", fixture.Arguments, "c"));
        client.Enqueue(FakeChatClient.ToolCall("long", fixture.Arguments, "d"));
        var summaryJson = StateJson("goal", "a.cs", "decision");
        client.Enqueue(FakeChatClient.Text(summaryJson));
        client.Enqueue(FakeChatClient.Text(summaryJson));
        client.Enqueue(FakeChatClient.Text("done"));

        var loop = new AgentLoop(client, new ToolRegistry([longTool, shortTool]), new AgentOptions
        {
            Model                     = "test-model",
            MaxAgentSteps             = 10,
            DefaultToolTimeoutSeconds = 30,
            Context                   = fixture.Options,
        });

        var result = await loop.RunAsync("sys", "go", CancellationToken.None);

        Assert.True(result.Status == AgentStatus.Completed,
                    $"{result.Error} requests={client.Requests} steps={result.Steps} compactions={result.Compactions}");
        Assert.Equal(5, result.Steps);
        Assert.Equal(2, result.Compactions);
        Assert.Equal(7, client.Requests);
        Assert.Equal("done", result.FinalMessage);

        Assert.Null(client.RequestLog[4].Tools);
        Assert.Null(client.RequestLog[5].Tools);
        Assert.NotNull(client.RequestLog[6].Tools);
        var firstSummaryInput = string.Join('\n', client.RequestLog[4].Messages.Select(m => m.Content));
        Assert.Contains(fixture.A, firstSummaryInput);
        Assert.DoesNotContain(fixture.B, firstSummaryInput);
        var secondSummaryInput = string.Join('\n', client.RequestLog[5].Messages.Select(m => m.Content));
        Assert.Contains(summaryJson, secondSummaryInput);
        Assert.Contains(fixture.B, secondSummaryInput);

        Assert.True(TokenEstimator.EstimateMessages(client.RequestLog[4].Messages) +
                    fixture.Options.ReservedOutputTokens
                 <= fixture.Options.ContextWindowTokens);
        Assert.True(TokenEstimator.EstimateMessages(client.RequestLog[5].Messages) +
                    fixture.Options.ReservedOutputTokens
                 <= fixture.Options.ContextWindowTokens);
        Assert.True(fixture.BeforeD     <= fixture.Options.CompactionThresholdTokens);
        Assert.True(fixture.AfterD      > fixture.Options.CompactionThresholdTokens);
        Assert.True(fixture.AfterFirst  > fixture.Options.CompactionThresholdTokens);
        Assert.True(fixture.AfterSecond <= fixture.Options.CompactionThresholdTokens);

        Assert.Contains(loop.History, m => m.Role                              == ChatRole.Assistant &&
                                           m.ToolCalls!.Single().Id            == "a"                &&
                                           m.ToolCalls!.Single().ArgumentsJson == fixture.Arguments);
        Assert.Contains(loop.History, m => m.Role                              == ChatRole.Assistant &&
                                           m.ToolCalls!.Single().Id            == "b"                &&
                                           m.ToolCalls!.Single().ArgumentsJson == fixture.Arguments);
        Assert.Contains(loop.History, m => m.Role    == ChatRole.Tool && m.ToolCallId == "a" &&
                                           m.Content == fixture.A + "#1");
        Assert.Contains(loop.History, m => m.Role    == ChatRole.Tool && m.ToolCallId == "b" &&
                                           m.Content == fixture.B + "#1");
        Assert.Contains(loop.History, m => m.Role    == ChatRole.Tool && m.ToolCallId == "c" &&
                                           m.Content == fixture.B + "#2");
        Assert.Contains(loop.History, m => m.Role    == ChatRole.Tool && m.ToolCallId == "d" &&
                                           m.Content == fixture.A + "#2");
    }

    [Fact]
    public async Task ViewTruncatesOversizedToolResult_WhileHistoryKeepsTheFullText()
    {
        var tool   = new FakeTool("t") { ResultContent = new string('x', 5000) };
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("t", "{}", ToolCallId("a", 1)));
        client.Enqueue(FakeChatClient.Text("done"));

        var options = new AgentOptions
        {
            Model                     = "test-model",
            MaxAgentSteps             = 10,
            DefaultToolTimeoutSeconds = 30,
            Context = new ContextOptions
            {
                ContextWindowTokens      = 1000,
                ReservedOutputTokens     = 100,
                ToolResultViewCharacters = 96,
            },
        };
        var loop = new AgentLoop(client, new ToolRegistry([tool]), options);

        var result = await loop.RunAsync("sys", "go", CancellationToken.None);

        Assert.Equal(AgentStatus.Completed, result.Status);
        Assert.Equal(0, result.Compactions);

        var modelView = client.RequestLog[1].Messages.Single(m => m.Role == ChatRole.Tool).Content;
        Assert.Contains("[model view truncated to 96 of 5002 chars", modelView);
        Assert.True(modelView.Length < 5002, "the model view must be bounded");

        var retained = loop.History.Single(m => m.Role == ChatRole.Tool).Content;
        Assert.Equal(5002, retained.Length); // 5000 filler + "#1"
        Assert.Contains("#1", retained);
    }

    [Fact]
    public async Task FittingHistory_IsSentWithoutCompaction()
    {
        var tool   = new FakeTool("t");
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("t", "{}", ToolCallId("a", 1)));
        client.Enqueue(FakeChatClient.ToolCall("t", "{}", ToolCallId("b", 2)));
        client.Enqueue(FakeChatClient.ToolCall("t", "{}", ToolCallId("c", 3)));
        client.Enqueue(FakeChatClient.Text("done"));

        var options = new AgentOptions
        {
            Model                     = "test-model",
            MaxAgentSteps             = 10,
            DefaultToolTimeoutSeconds = 30,
            Context                   = new ContextOptions { ContextWindowTokens = 4000, ReservedOutputTokens = 200 },
        };
        var loop = new AgentLoop(client, new ToolRegistry([tool]), options);

        var result = await loop.RunAsync("sys", "go", CancellationToken.None);

        Assert.Equal(AgentStatus.Completed, result.Status);
        Assert.Equal(0, result.Compactions);
        Assert.Equal(4, client.Requests);    // no extra summarizer request
        Assert.Equal(9, loop.History.Count); // three rounds + head + final assistant message
    }

    [Fact]
    public async Task WithoutContextOptions_HistoryIsSentVerbatim()
    {
        var tool   = new FakeTool("t");
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("t", "{}", ToolCallId("a", 1)));
        client.Enqueue(FakeChatClient.ToolCall("t", "{}", ToolCallId("b", 2)));
        client.Enqueue(FakeChatClient.Text("done"));

        var loop = new AgentLoop(client, new ToolRegistry([tool]), NoContextOptions());

        var result = await loop.RunAsync("sys", "go", CancellationToken.None);

        Assert.Equal(AgentStatus.Completed, result.Status);
        Assert.Equal(0, result.Compactions);
        Assert.Equal(3, client.Requests);
        Assert.Equal(7, loop.History.Count); // two rounds + head + final assistant message
    }
}
