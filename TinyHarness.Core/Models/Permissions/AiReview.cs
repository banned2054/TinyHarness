namespace TinyHarness.Core.Models.Permissions;

/// <summary>
///     <para>独立 AI 审核员的三值决定；技术失败不在此列，用 <see cref="AiReviewFailure" /> 单独表示。</para>
///     <para>
///         The independent AI reviewer's three-valued decision; technical failures are represented
///         separately by <see cref="AiReviewFailure" />.
///     </para>
/// </summary>
public enum AiReviewDecision
{
    /// <summary>
    ///     <para>批准当前这一个已冻结调用；授权按请求允许的范围建立。</para>
    ///     <para>Approves exactly this frozen invocation; the grant follows the request's allowed scopes.</para>
    /// </summary>
    Allow,

    /// <summary>
    ///     <para>拒绝当前调用并写入拒绝记忆；绝不转交人工，操作保持未执行。</para>
    ///     <para>Denies the invocation and records the denial; never escalates, and the operation stays unexecuted.</para>
    /// </summary>
    Deny,

    /// <summary>
    ///     <para>把同一冻结计划连同风险与理由升级给用户裁决；人批只覆盖原计划。</para>
    ///     <para>
    ///         Escalates the same frozen plan with risk and reason to the user; a human approval covers the original
    ///         plan only.
    ///     </para>
    /// </summary>
    EscalateToUser
}

/// <summary>
///     <para>AI 审核的技术失败种类；失败与拒绝语义不同，失败不写入拒绝记忆也不转人工。</para>
///     <para>
///         Technical failure kinds of AI review; a failure differs from a denial — it neither records
///         denial memory nor escalates to the user.
///     </para>
/// </summary>
public enum AiReviewFailureKind
{
    /// <summary>
    ///     <para>审核员不可用：请求失败或审核模型/供应商缺失。</para>
    ///     <para>The reviewer is unavailable: the request failed or the reviewer model/provider is missing.</para>
    /// </summary>
    ReviewUnavailable,

    /// <summary>
    ///     <para>响应违反输出协议（格式、枚举、长度或结束原因无效）。</para>
    ///     <para>The response violated the output protocol (format, enum, length or finish reason).</para>
    /// </summary>
    InvalidResponse,

    /// <summary>
    ///     <para>审核请求超出时限。</para>
    ///     <para>The review request exceeded its time limit.</para>
    /// </summary>
    Timeout,

    /// <summary>
    ///     <para>审核被取消（随运行取消或审批作废）。</para>
    ///     <para>The review was cancelled (run cancellation or approval obsolescence).</para>
    /// </summary>
    Cancelled
}

/// <summary>
///     <para>AI 审核成功的完整结果：决定加两个仅作展示的审核维度。</para>
///     <para>A successful AI review: the decision plus two display-only review dimensions.</para>
/// </summary>
public sealed record AiReviewResult(
    AiReviewDecision  Decision,
    RiskLevel         RiskLevel,
    UserAuthorization UserAuthorization,
    string            Reason);

/// <summary>
///     <para>AI 审核的技术失败；携带种类与简短细节，操作保持未执行。</para>
///     <para>A technical AI-review failure carrying the kind and a short detail; the operation stays unexecuted.</para>
/// </summary>
public sealed record AiReviewFailure(AiReviewFailureKind Kind, string Detail);

/// <summary>
///     <para>
///         一次 AI 审核的互斥结果：成功携带 <see cref="AiReviewResult" />，技术失败携带
///         <see cref="AiReviewFailure" />。两者至少且只能有一个非空。
///     </para>
///     <para>
///         The exclusive outcome of one AI review: success carries <see cref="AiReviewResult" />, a technical
///         failure carries <see cref="AiReviewFailure" />. Exactly one of the two is non-null.
///     </para>
/// </summary>
public sealed record AiReviewOutcome(AiReviewResult? Result, AiReviewFailure? Failure)
{
    /// <summary>创建成功结果。Wraps a successful result.</summary>
    public static AiReviewOutcome FromResult(AiReviewResult result) =>
        new(ArgumentNull(result), null);

    /// <summary>创建技术失败。Wraps a technical failure.</summary>
    public static AiReviewOutcome FromFailure(AiReviewFailure failure) =>
        new(null, ArgumentNull(failure));

    private static T ArgumentNull<T>(T value) where T : class =>
        value ?? throw new ArgumentNullException(nameof(value));
}
