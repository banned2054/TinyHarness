using System.Text.Json;
using System.Text.Json.Serialization;

namespace TinyHarness.Core.Context;

/// <summary>
/// StructuredState 的 source-generated JSON 契约。序列化与反序列化都走静态 metadata，
/// 保持 NativeAOT 裁剪安全（PLAN §6/§21）。
///
/// Source-generated JSON contract for <see cref="StructuredState"/>. Both
/// serialization directions use static metadata so the compaction path stays
/// trimming-safe under NativeAOT (PLAN §6/§21).
/// </summary>
[JsonSerializable(typeof(StructuredState))]
[JsonSourceGenerationOptions(
                                PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
                                GenerationMode = JsonSourceGenerationMode.Metadata |
                                                 JsonSourceGenerationMode.Serialization)]
public sealed partial class StructuredStateJsonContext : JsonSerializerContext;

/// <summary>
/// 旧历史压缩成的结构化状态（PLAN §13）。它只包含模型后续工作需要的稳定事实，不使用
/// 无约束的自由文本摘要；文件与命令列表保留历史记录，约束、决策与待办描述当前工作状态。
///
/// Structured state produced by compacting older history (PLAN §13). It holds only
/// the stable facts the model needs to continue, never an unbounded free-text
/// summary; file and command lists retain historical records while constraints,
/// decisions, and pending work describe the current task state.
/// </summary>
public sealed record StructuredState
{
    public string Goal { get; init; } = string.Empty;

    public IReadOnlyList<string> Constraints { get; init; } = [];

    public IReadOnlyList<string> Decisions { get; init; } = [];

    public IReadOnlyList<string> FilesInspected { get; init; } = [];

    public IReadOnlyList<string> FilesModified { get; init; } = [];

    public IReadOnlyList<string> CommandsAndResults { get; init; } = [];

    public IReadOnlyList<string> PendingWork { get; init; } = [];

    /// <summary>
    /// 全字段为空的初始状态。
    /// The initial state with every field empty.
    /// </summary>
    public static StructuredState Empty { get; } = new();

    /// <summary>
    /// 是否完全没有内容（Goal 为空白且所有列表为空）。提交压缩时，把承载真实工作的回合
    /// 折叠成这种空状态会被拒绝，避免决策、修改文件等事实从模型视图中消失。
    ///
    /// Whether the state carries no content at all (a blank goal and every list empty).
    /// Committing such a state over turns that carried real work is rejected so that
    /// decisions, modified files and similar facts never vanish from the model view.
    /// </summary>
    [JsonIgnore]
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Goal)
     && HasNoSubstantiveEntry(Constraints)
     && HasNoSubstantiveEntry(Decisions)
     && HasNoSubstantiveEntry(FilesInspected)
     && HasNoSubstantiveEntry(FilesModified)
     && HasNoSubstantiveEntry(CommandsAndResults)
     && HasNoSubstantiveEntry(PendingWork);

    /// <summary>
    /// 以紧凑 camelCase JSON 序列化，作为模型视图中的状态消息内容。
    /// Serializes to compact camelCase JSON, used as the state message content in the model view.
    /// </summary>
    public string ToJson() => JsonSerializer.Serialize(this, StructuredStateJsonContext.Default.StructuredState);

    /// <summary>
    /// 解析并校验摘要输出。非对象、字段类型不符或 JSON 非法都视为失败，调用方据此回滚，
    /// 避免格式错误的摘要污染上下文。
    ///
    /// Parses and validates a summarizer output. A non-object payload, a mistyped
    /// field, or invalid JSON is a failure so the caller can roll back instead of
    /// letting a malformed summary corrupt the context.
    /// </summary>
    public static bool TryParse(string json, out StructuredState state)
    {
        state = Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize(json, StructuredStateJsonContext.Default.StructuredState);
            if (parsed is null)
            {
                return false;
            }

            // Normalize: a summarizer may omit empty fields, and a missing collection
            // property can deserialize as null. Every field of the compacted state
            // must be a usable, non-null value before the caller commits it.
            state = new StructuredState
            {
                Goal               = parsed.Goal?.Trim() ?? string.Empty,
                Constraints        = NormalizeEntries(parsed.Constraints),
                Decisions          = NormalizeEntries(parsed.Decisions),
                FilesInspected     = NormalizeEntries(parsed.FilesInspected),
                FilesModified      = NormalizeEntries(parsed.FilesModified),
                CommandsAndResults = NormalizeEntries(parsed.CommandsAndResults),
                PendingWork        = NormalizeEntries(parsed.PendingWork),
            };
            return true;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool HasNoSubstantiveEntry(IReadOnlyList<string> entries)
    {
        foreach (var entry in entries)
        {
            if (!string.IsNullOrWhiteSpace(entry))
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyList<string> NormalizeEntries(IReadOnlyList<string>? entries)
    {
        if (entries is null)
        {
            return [];
        }

        var normalized = new List<string>(entries.Count);
        foreach (var entry in entries)
        {
            if (!string.IsNullOrWhiteSpace(entry))
            {
                normalized.Add(entry.Trim());
            }
        }

        return normalized;
    }
}
