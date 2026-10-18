namespace TinyHarness.Core.Models.ProjectInstructions;

/// <summary>
///     <para>在统一会话日志中记录项目指令的生效版本或探测范围。</para>
///     <para>Records an applied project-instruction revision or its tracked discovery scopes in the unified session log.</para>
/// </summary>
public sealed record ProjectInstructionRecord
{
    /// <summary>
    ///     <para>结构化项目指令记录的固定 schema 标识。</para>
    ///     <para>The fixed schema identifier for structured project-instruction records.</para>
    /// </summary>
    public string Schema { get; init; } = "tinyharness.project-instructions/1";

    /// <summary>
    ///     <para>记录动作：应用完整规则版本或仅更新跟踪范围。</para>
    ///     <para>The record action: apply a complete rules revision or update tracked scopes only.</para>
    /// </summary>
    public required string Action { get; init; }

    /// <summary>
    ///     <para>本次有效规则集合的确定性 revision。</para>
    ///     <para>The deterministic revision of the effective rules collection.</para>
    /// </summary>
    public required string Revision { get; init; }

    /// <summary>
    ///     <para>前一有效规则 revision；首个应用版本为空。</para>
    ///     <para>The preceding effective rules revision; null for the first applied revision.</para>
    /// </summary>
    public string? PreviousRevision { get; init; }

    /// <summary>
    ///     <para>产生本条记录的真实用户回合标识。</para>
    ///     <para>The real user-turn identity that produced this record.</para>
    /// </summary>
    public required string TurnId { get; init; }

    /// <summary>
    ///     <para>产生本条记录的运行标识。</para>
    ///     <para>The run identity that produced this record.</para>
    /// </summary>
    public required string RunId { get; init; }

    /// <summary>
    ///     <para>描述本次应用或探测更新原因的稳定标签。</para>
    ///     <para>A stable label describing why this application or discovery update occurred.</para>
    /// </summary>
    public required string Reason { get; init; }

    /// <summary>
    ///     <para>完整规则或探测状态快照；探测记录在日志中省略重复正文。</para>
    ///     <para>The complete rules or discovery-state snapshot; journal discovery records omit duplicate document bodies.</para>
    /// </summary>
    public required ProjectInstructionSnapshot Snapshot { get; init; }

    /// <summary>
    ///     <para>应用规则时对应宿主 user 消息的 canonical 零基下标；探测记录为空。</para>
    ///     <para>The canonical zero-based index of the host user message for applied rules; null for discovery records.</para>
    /// </summary>
    public int? CanonicalMessageIndex { get; init; }
}
