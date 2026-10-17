using System.Text.Json.Nodes;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

/// <summary>
///     <para>用于观察主循环 Prepare/Execute 流程的可配置测试工具。</para>
///     <para>A configurable test tool for observing the loop's Prepare/Execute flow.</para>
/// </summary>
internal sealed class FakeTool(string name, string description = "Fake tool", string? summary = null) : ITool
{
    public int ExecuteCount { get; private set; }

    public bool ThrowOnExecute { get; init; }

    public ToolResult? ResultOverride { get; init; }

    public Func<CancellationToken, Task<ToolResult>>? ExecuteOverride { get; init; }

    /// <summary>
    ///     <para>
    ///         指定结果内容，并追加每次执行的 #n 后缀，
    ///         用于区分重复调用或模拟超大输出。
    ///     </para>
    ///     <para>
    ///         Uses the specified result content with a per-execution #n suffix
    ///         to distinguish calls or simulate oversized output.
    ///     </para>
    /// </summary>
    public string? ResultContent { get; init; }

    /// <summary>
    ///     <para>覆盖准备计划摘要；缺省为工具名加参数 JSON，用于验证展示与脱敏。</para>
    ///     <para>
    ///         Overrides the prepared summary; defaults to the tool name plus argument JSON
    ///         for display and redaction tests.
    ///     </para>
    /// </summary>
    public string? Summary { get; init; } = summary;

    public ToolDefinition Definition { get; } = new()
    {
        Name        = name,
        Description = description,
        Parameters  = new JsonObject { ["type"] = "object" }
    };

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
            Summary    = Summary ?? $"{Definition.Name}({args.ToJsonString()})"
        };
    }

    public Task<ToolResult> ExecuteAsync(ToolPreparation preparation, CancellationToken cancellationToken)
    {
        ExecuteCount++;
        if (ExecuteOverride is not null) return ExecuteOverride(cancellationToken);
        if (ThrowOnExecute)
            return Task.FromResult(new ToolResult { Succeeded = false, Content = $"{Definition.Name} exploded" });

        var content = ResultContent is null ? $"{Definition.Name} ok" : $"{ResultContent}#{ExecuteCount}";
        return Task.FromResult(ResultOverride ?? new ToolResult { Succeeded = true, Content = content });
    }
}
