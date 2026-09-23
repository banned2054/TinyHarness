using System.Text.Json.Serialization;

namespace TinyHarness.Core.Models.Worker;

/// <summary>
///     模型最终答复必须呈现的严格 JSON 形状（结论草稿）。模型不提供状态、统计或任何运行设置；
///     状态由 runner 依据真实执行情况决定。未知字段会直接导致解析失败。
///     The strict JSON shape the model's final answer must take (a conclusion draft). The model never
///     supplies status, statistics or any run setting; the runner decides the status from what actually
///     happened. Unknown members fail parsing outright.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkerConclusionDraft
{
    public required string Conclusion { get; init; }

    public IReadOnlyList<WorkerEvidenceDraft>? Evidence { get; init; }

    public IReadOnlyList<string>? SuggestedChanges { get; init; }

    public IReadOnlyList<string>? TestSuggestions { get; init; }

    public IReadOnlyList<string>? Uncertainties { get; init; }
}

/// <summary>
///     结论草稿中的单条证据引用；路径必须是工作区相对形状，行号从 1 起。
///     One evidence citation inside a conclusion draft; the path must be workspace-relative and line
///     numbers are 1-based.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkerEvidenceDraft
{
    public required string Path { get; init; }

    public int? LineStart { get; init; }

    public int? LineEnd { get; init; }

    public string? Note { get; init; }
}
