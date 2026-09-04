using System.Text.Json.Nodes;
using TinyHarness.Core.ChatCompletions;
using TinyHarness.Core.Tools;

namespace TinyHarness.Tests;

/// <summary>
/// A scripted tool used to observe Prepare/Execute flow in loop tests.
/// </summary>
internal sealed class FakeTool(string name, string description = "Fake tool") : ITool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name        = name,
        Description = description,
        Parameters  = new JsonObject { ["type"] = "object" },
    };

    public int ExecuteCount { get; private set; }

    public bool ThrowOnExecute { get; init; }

    public ToolPreparation Prepare(ChatToolCall call)
    {
        var args = string.IsNullOrEmpty(call.ArgumentsJson)
            ? new JsonObject()
            : JsonNode.Parse(call.ArgumentsJson) as JsonObject
           ?? throw new InvalidDataException("Arguments were not a JSON object.");

        return new ToolPreparation
        {
            ToolName   = Definition.Name,
            CallId     = call.Id,
            Arguments  = args,
            Capability = $"test.{Definition.Name}",
            Summary    = $"{Definition.Name}({args.ToJsonString()})",
        };
    }

    public Task<ToolResult> ExecuteAsync(ToolPreparation preparation, CancellationToken _)
    {
        ExecuteCount++;
        if (ThrowOnExecute)
        {
            return Task.FromResult(new ToolResult { Succeeded = false, Content = $"{Definition.Name} exploded" });
        }

        return Task.FromResult(new ToolResult { Succeeded = true, Content = $"{Definition.Name} ok" });
    }
}
