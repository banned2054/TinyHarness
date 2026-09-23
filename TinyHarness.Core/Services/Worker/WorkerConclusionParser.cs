using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using TinyHarness.Core.Models.Worker;

namespace TinyHarness.Core.Services.Worker;

/// <summary>
///     把模型最终答复解析为 <see cref="WorkerConclusionDraft" />。只接受严格的 camelCase JSON：
///     缺字段、未知字段、类型不匹配都算解析失败；额外容忍一个包裹的 markdown 代码围栏，
///     因为真实模型经常添加围栏，围栏内仍是纯 JSON 才会被接受。
///     Parses the model's final answer into a <see cref="WorkerConclusionDraft" />. Only strict camelCase
///     JSON is accepted: missing members, unknown members and type mismatches all fail; a single wrapped
///     markdown code fence is tolerated because real models add them often — what remains inside the
///     fence must still be pure JSON.
/// </summary>
public static class WorkerConclusionParser
{
    public static bool TryParse(string? text, [NotNullWhen(true)] out WorkerConclusionDraft? draft)
    {
        draft = null;

        if (string.IsNullOrWhiteSpace(text)) return false;

        var candidate = ExtractJsonCandidate(text.Trim());
        if (candidate is null) return false;

        try
        {
            draft = JsonSerializer.Deserialize(candidate, WorkerConclusionJsonContext.Default.WorkerConclusionDraft);
        }
        catch (JsonException)
        {
            return false;
        }

        return draft is not null;
    }

    /// <summary>
    ///     剥离单个包裹的代码围栏；没有围栏时原样返回，围栏不完整或为空时返回 null。
    ///     Strips a single wrapping code fence; returns the text as-is without a fence and null for an
    ///     incomplete or empty fence.
    /// </summary>
    private static string? ExtractJsonCandidate(string trimmed)
    {
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return trimmed;

        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline < 0) return null;

        var body         = trimmed[(firstNewline + 1)..];
        var closingFence = body.LastIndexOf("```", StringComparison.Ordinal);
        if (closingFence < 0) return null;

        var candidate = body[..closingFence].Trim();
        return candidate.Length == 0 ? null : candidate;
    }
}
