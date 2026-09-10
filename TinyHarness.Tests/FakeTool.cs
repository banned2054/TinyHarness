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

    /// <summary>
    /// 设置后作为固定结果内容返回（并追加按执行计数的 “#n” 后缀），用于模拟超出预算的
    /// 大输出并让测试区分同一工具的多次调用结果。
    /// When set, returned verbatim (with a per-execution "#n" suffix) to simulate
    /// oversized output and let tests tell repeated calls to one tool apart.
    /// </summary>
    public string? ResultContent { get; init; }

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

        var content = ResultContent is null ? $"{Definition.Name} ok" : $"{ResultContent}#{ExecuteCount}";
        return Task.FromResult(new ToolResult { Succeeded = true, Content = content });
    }
}
