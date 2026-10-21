using System.Text.Json;
using TinyHarness.Core.Services.Context;

namespace TinyHarness.Core.Models.Context;

/// <summary>
///     <para>已提交的有界交接摘要和真实用户原话来源；不保存授权或完整执行事实。</para>
///     <para>A committed bounded handoff summary and original user sources; stores no grants or complete execution ledger.</para>
/// </summary>
public sealed record CompactionState
{
    public const  string                             CurrentSchema = "tinyharness.compaction/1";
    public        string                             Schema               { get; init; } = CurrentSchema;
    public        string                             Summary              { get; init; } = string.Empty;
    public        IReadOnlyList<RetainedUserMessage> RetainedUserMessages { get; init; } = [];
    public static CompactionState                    Empty                { get; }       = new();

    /// <summary>
    ///     <para>序列化宿主检查点内容，不作为模型摘要输出格式。</para>
    ///     <para>Serializes host checkpoint content; this is not the model's summary output format.</para>
    /// </summary>
    public string ToJson() => JsonSerializer.Serialize(this, CompactionStateJsonContext.Default.CompactionState);
}

/// <summary>
///     <para>折叠区中一条真实用户消息的原文或尾部；下标绑定 canonical 历史。</para>
///     <para>An original user message or suffix from the folded region, indexed into canonical history.</para>
/// </summary>
public sealed record RetainedUserMessage(int MessageIndex, string Content);

/// <summary>
///     <para>不包含模型正文或凭据的压缩拒绝原因。</para>
///     <para>A compaction rejection reason containing neither model text nor credentials.</para>
/// </summary>
public enum CompactionFailureReason
{
    None,
    InvalidPlan,
    EmptySummary,
    SummaryTooLarge,
    NoBudgetBenefit,
    ModelRequestFailed,
    IncompleteResponse,
    ToolCallsNotAllowed
}
