using TinyHarness.Core.ChatCompletions;
using TinyHarness.Core.Tools;

namespace TinyHarness.Core.Agent;

/// <summary>
/// The Agent main loop. Request the model, consume the stream, assemble tool
/// calls, prepare and execute each sequentially, hand results back, and repeat
/// until the model produces a plain-text turn, a limit is reached, or the run is
/// cancelled.
///
/// Authorization is a pass-through in this milestone so the loop can run
/// end-to-end; the Permission Engine (M4) will replace it.
/// </summary>
public sealed class AgentLoop(IChatCompletionClient model, ToolRegistry tools, AgentOptions options)
{
    private readonly List<ChatMessage> _history = [];

    public IReadOnlyList<ChatMessage> History => _history;

    public async Task<AgentResult> RunAsync(string systemPrompt, string userInput, CancellationToken cancellationToken)
    {
        _history.Clear();
        _history.Add(ChatMessage.System(systemPrompt));
        _history.Add(ChatMessage.User(userInput));

        var steps          = 0;
        var toolExecutions = 0;

        try
        {
            while (steps < options.MaxAgentSteps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                steps++;

                var request   = BuildRequest();
                var assistant = await RequestAssistantMessageAsync(request, cancellationToken);

                _history.Add(assistant);

                if (assistant.ToolCalls is null || assistant.ToolCalls.Count == 0)
                {
                    return new AgentResult
                    {
                        Status         = AgentStatus.Completed,
                        FinalMessage   = assistant.Content,
                        Steps          = steps,
                        ToolExecutions = toolExecutions,
                    };
                }

                foreach (var call in assistant.ToolCalls)
                {
                    var result = await ExecuteOneAsync(call, cancellationToken);
                    toolExecutions++;
                    _history.Add(ChatMessage.Tool(call.FunctionName, call.Id, result.Content));
                }
            }

            return new AgentResult
            {
                Status         = AgentStatus.StepLimitReached,
                FinalMessage   = string.Empty,
                Steps          = steps,
                ToolExecutions = toolExecutions,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new AgentResult
            {
                Status         = AgentStatus.Cancelled,
                Steps          = steps,
                ToolExecutions = toolExecutions,
                Error          = "Run cancelled",
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AgentResult
            {
                Status         = AgentStatus.Failed,
                Steps          = steps,
                ToolExecutions = toolExecutions,
                Error          = ex.Message,
            };
        }
    }

    private ChatCompletionRequest BuildRequest()
    {
        var tools1 = tools.Count == 0
            ? null
            : tools.Values.Select(ToDefinition).ToList();

        return new ChatCompletionRequest
        {
            Model    = options.Model,
            Messages = _history,
            Tools    = tools1,
        };
    }

    private static ToolDefinition ToDefinition(ITool tool) => new()
    {
        Name        = tool.Name,
        Description = tool.Description,
        Parameters  = new System.Text.Json.Nodes.JsonObject(),
    };

    private async Task<ChatMessage> RequestAssistantMessageAsync(ChatCompletionRequest request,
                                                                 CancellationToken     cancellationToken)
    {
        var accumulator = new StreamAccumulator();

        await foreach (var @event in model.CompleteAsync(request, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            accumulator.Append(@event);
        }

        accumulator.Finish();
        return ChatMessage.Assistant(accumulator.Content, accumulator.ToolCalls);
    }

    private async Task<ToolResult> ExecuteOneAsync(ChatToolCall call, CancellationToken cancellationToken)
    {
        if (!tools.TryGetValue(call.FunctionName, out var tool))
        {
            return new ToolResult { Succeeded = false, Content = $"Unknown tool: {call.FunctionName}" };
        }

        try
        {
            var preparation = tool.Prepare(call);
            return await tool.ExecuteAsync(preparation, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ToolResult
            {
                Succeeded = false,
                Content   = $"Tool '{call.FunctionName}' failed: {ex.Message}",
            };
        }
    }
}
