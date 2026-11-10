namespace TinyHarness.Core.Services.Persistence;

/// <summary>
///     <para>项目删除的解绑结果不完整：仍有日志被占用或处理失败，编排已在任何清理之前中止。</para>
///     <para>
///         A project deletion's unassignment result is incomplete: journals remain held or failed, and the
///         orchestration aborted before any cleanup.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         由 Core 编排在应用解绑结果后、置顶清理与目录册移除之前抛出；目录册保留删除标记，置顶与日志归属
///         不变，宿主可据 <see cref="Result" /> 提示用户稍后重试。
///     </para>
///     <para>
///         Thrown by the Core orchestration after applying the unassignment result and before pin cleanup and
///         catalog removal; the catalog keeps its deletion marker, pins and journal ownership stay untouched, and
///         hosts may prompt a retry based on <see cref="Result" />.
///     </para>
/// </remarks>
public sealed class SessionProjectUnassignmentIncompleteException : Exception
{
    /// <summary>
    ///     <para>用解绑结果构造异常；消息为不含凭据、含占用与失败计数的技术性描述。</para>
    ///     <para>
    ///         Constructs the exception from the unassignment result; the message is a credential-free technical
    ///         description carrying hold and failure counts.
    ///     </para>
    /// </summary>
    public SessionProjectUnassignmentIncompleteException(SessionProjectUnassignmentResult result)
        : base(Describe(result)) =>
        Result = result;

    /// <summary>
    ///     <para>触发本次拒绝的解绑结果；含逐日志的占用与失败原因。</para>
    ///     <para>The unassignment result that triggered the rejection; per-journal holds and failures included.</para>
    /// </summary>
    public SessionProjectUnassignmentResult Result { get; }

    /// <summary>
    ///     <para>返回不完整解绑的技术性摘要：仍被占用与失败的数量。</para>
    ///     <para>Returns a technical summary of the incomplete unassignment: still-held and failed counts.</para>
    /// </summary>
    private static string Describe(SessionProjectUnassignmentResult result)
    {
        var pending = result.Outcomes.Count(outcome => outcome.Status == SessionProjectUnassignmentStatus.Pending);
        var failed  = result.Outcomes.Count(outcome => outcome.Status == SessionProjectUnassignmentStatus.Failed);
        return $"Project deletion stopped before cleanup: the unassignment pass is incomplete " +
               $"({pending} journal(s) held by writers, {failed} failed).";
    }
}
