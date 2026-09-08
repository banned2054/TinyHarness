using TinyHarness.Core.Agent;
using TinyHarness.Core.ChatCompletions;

namespace TinyHarness.Tests;

public class AgentLoopTests
{
    private static AgentOptions Options(int maxSteps = 10) => new()
    {
        Model                     = "test-model",
        MaxAgentSteps             = maxSteps,
        DefaultToolTimeoutSeconds = 30,
    };

    [Fact]
    public async Task PlainText_Completes()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("Hello from ", "the model."));
        var loop = new AgentLoop(client, new ToolRegistry([]), Options());

        var result = await loop.RunAsync("sys", "hi", CancellationToken.None);

        Assert.Equal(AgentStatus.Completed, result.Status);
        Assert.Equal("Hello from the model.", result.FinalMessage);
        Assert.Equal(1, client.Requests);
        Assert.Equal("test-model", client.LastRequestModel);
        Assert.Contains(loop.History, m => m is { Role: ChatRole.Assistant, Content: "Hello from the model." });
    }

    [Fact]
    public async Task SingleToolCall_ExecutesAndReturnsToolResult()
    {
        var client = new FakeChatClient();
        var tool   = new FakeTool("echo_test");
        client.Enqueue(FakeChatClient.ToolCall("echo_test", "{\"value\":\"x\"}"));
        client.Enqueue(FakeChatClient.Text("done"));
        var loop = new AgentLoop(client, new ToolRegistry([tool]), Options());

        var result = await loop.RunAsync("sys", "run it", CancellationToken.None);

        Assert.Equal(AgentStatus.Completed, result.Status);
        Assert.Equal(1, tool.ExecuteCount);
        Assert.Equal(2, client.Requests);
        Assert.Contains(loop.History, m => m is { Role: ChatRole.Tool, Content: "echo_test ok" });
    }

    [Fact]
    public async Task SplitArguments_AreAssembledCorrectly()
    {
        var client = new FakeChatClient();
        var tool   = new FakeTool("echo_test");
        client.Enqueue(FakeChatClient.ToolCall("echo_test", "{\"value\":\"abc\"}", id : "c1"));
        client.Enqueue(FakeChatClient.Text("ok"));
        var loop = new AgentLoop(client, new ToolRegistry([tool]), Options());

        var result = await loop.RunAsync("sys", "run", CancellationToken.None);

        Assert.Equal(AgentStatus.Completed, result.Status);
        Assert.Equal(1, tool.ExecuteCount);

        var toolExecution = loop.History.Single(m => m.Role == ChatRole.Tool);
        Assert.Equal("echo_test ok", toolExecution.Content);
    }

    [Fact]
    public async Task MultipleToolCalls_ExecuteSequentiallyInOneTurn()
    {
        var client = new FakeChatClient();
        var a      = new FakeTool("tool_a");
        var b      = new FakeTool("tool_b");
        var calls = new List<ChatStreamEvent>
        {
            new()
            {
                Kind                 = ChatStreamEventKind.ToolCallDelta, ToolCallIndex = 0, ToolCallId = "a",
                ToolCallFunctionName = "tool_a", ToolCallArgumentsDelta                 = "{}"
            },
            new()
            {
                Kind                 = ChatStreamEventKind.ToolCallDelta, ToolCallIndex = 1, ToolCallId = "b",
                ToolCallFunctionName = "tool_b", ToolCallArgumentsDelta                 = "{}"
            },
            new() { Kind = ChatStreamEventKind.End },
        };
        client.Enqueue(calls);
        client.Enqueue(FakeChatClient.Text("all done"));
        var loop = new AgentLoop(client, new ToolRegistry([a, b]), Options());

        var result = await loop.RunAsync("sys", "go", CancellationToken.None);

        Assert.Equal(AgentStatus.Completed, result.Status);
        Assert.Equal(1, a.ExecuteCount);
        Assert.Equal(1, b.ExecuteCount);
        Assert.Equal(2, result.ToolExecutions);
    }

    [Fact]
    public async Task UnknownTool_ReturnsErrorToModelAndContinues()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("missing_tool", "{}"));
        client.Enqueue(FakeChatClient.Text("ok"));
        var loop = new AgentLoop(client, new ToolRegistry([]), Options());

        var result = await loop.RunAsync("sys", "go", CancellationToken.None);

        Assert.Equal(AgentStatus.Completed, result.Status);
        Assert.Equal(0, result.ToolExecutions);
        Assert.Contains(loop.History, m => m.Role == ChatRole.Tool && m.Content.Contains("Unknown tool"));
    }

    [Fact]
    public async Task StepLimit_IsReached()
    {
        var client = new FakeChatClient();
        var tool   = new FakeTool("loop");
        var loop   = new AgentLoop(client, new ToolRegistry([tool]), Options(maxSteps : 3));

        for (var i = 0; i < 10; i++)
        {
            client.Enqueue(FakeChatClient.ToolCall("loop", "{}"));
        }

        var result = await loop.RunAsync("sys", "go", CancellationToken.None);

        Assert.Equal(AgentStatus.StepLimitReached, result.Status);
        Assert.Equal(3, result.Steps);
    }

    [Fact]
    public async Task InvalidJson_StreamThrows()
    {
        var client = new FakeChatClient();
        client.Enqueue(new List<ChatStreamEvent>
        {
            new()
            {
                Kind = ChatStreamEventKind.ToolCallDelta, ToolCallIndex = 0, ToolCallFunctionName = "t",
                ToolCallArgumentsDelta = "{not json"
            },
            new() { Kind = ChatStreamEventKind.End },
        });
        var loop = new AgentLoop(client, new ToolRegistry([]), Options());

        var result = await loop.RunAsync("sys", "go", CancellationToken.None);

        Assert.Equal(AgentStatus.Failed, result.Status);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Cancellation_ReturnsCancelled()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("never replied")); // cancelled before consumed
        var loop = new AgentLoop(client, new ToolRegistry([]), Options());

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var result = await loop.RunAsync("sys", "go", cts.Token);

        Assert.Equal(AgentStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task CancellationDuringStream_ReturnsCancelled()
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("a", "b", "c"));
        var loop = new AgentLoop(client, new ToolRegistry([]), Options());

        using var cts = new CancellationTokenSource();
        client.CancelMidStream = () => cts.Cancel();

        var result = await loop.RunAsync("sys", "go", cts.Token);

        Assert.Equal(AgentStatus.Cancelled, result.Status);
        Assert.Single(loop.History, m => m.Role == ChatRole.User);
        Assert.DoesNotContain(loop.History, m => m.Role == ChatRole.Assistant);
    }
}
