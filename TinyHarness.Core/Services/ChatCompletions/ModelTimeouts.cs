namespace TinyHarness.Core.Services.ChatCompletions;

/// <summary>
///     <para>单次模型请求的超时期限：流空闲保护、可选响应总期限与 AI 审批期限。</para>
///     <para>
///         Timeouts for a single model request: the stream idle guard, an optional end-to-end
///         response deadline, and the AI approval deadline.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         流空闲与响应总期限允许 0 表示关闭；审批期限必须为正数。上限与 RunBudgetLimits 的
///         CancellationTokenSource 计时器上限一致（4_294_966 秒）。
///     </para>
///     <para>
///         The stream idle guard and the response deadline allow zero to disable; the approval
///         deadline must be positive. The upper bound matches the CancellationTokenSource timer
///         limit used by RunBudgetLimits (4,294,966 seconds).
///     </para>
/// </remarks>
public sealed record ModelTimeouts
{
    /// <summary>
    ///     <para>流空闲上限（秒）：超过该时长未收到任何模型 update 即判定流停滞；0 关闭该保护。</para>
    ///     <para>
    ///         Stream idle limit in seconds: the stream counts as stalled once this duration passes
    ///         without any model update; zero disables the guard.
    ///     </para>
    /// </summary>
    public int StreamIdleTimeoutSeconds { get; init; } = 300;

    /// <summary>
    ///     <para>响应总期限（秒）：覆盖从调用开始到流结束的整段时间；0 表示不设总期限。</para>
    ///     <para>
    ///         End-to-end response deadline in seconds covering the whole call from start to stream
    ///         completion; zero disables the deadline.
    ///     </para>
    /// </summary>
    public int ResponseTimeoutSeconds { get; init; } = 0;

    /// <summary>
    ///     <para>AI 审批请求期限（秒），必须为正数。</para>
    ///     <para>Deadline in seconds for AI approval requests; must be positive.</para>
    /// </summary>
    public int ApprovalTimeoutSeconds { get; init; } = 300;

    /// <summary>
    ///     <para>默认配置：空闲 300 秒，无响应总期限，审批 300 秒。</para>
    ///     <para>Defaults: 300-second idle guard, no response deadline, 300-second approval deadline.</para>
    /// </summary>
    public static ModelTimeouts Default { get; } = new();

    /// <summary>
    ///     <para>校验各期限的取值范围；越界时按属性名抛出 ArgumentOutOfRangeException。</para>
    ///     <para>
    ///         Validates each limit range; out-of-range values throw an ArgumentOutOfRangeException
    ///         named after the offending property.
    ///     </para>
    /// </summary>
    public void Validate()
    {
        if (StreamIdleTimeoutSeconds is < 0 or > 4_294_966)
            throw new ArgumentOutOfRangeException(nameof(StreamIdleTimeoutSeconds), StreamIdleTimeoutSeconds,
                                                  "Stream idle timeout must be between 0 and 4294966 seconds.");
        if (ResponseTimeoutSeconds is < 0 or > 4_294_966)
            throw new ArgumentOutOfRangeException(nameof(ResponseTimeoutSeconds), ResponseTimeoutSeconds,
                                                  "Response timeout must be between 0 and 4294966 seconds.");
        if (ApprovalTimeoutSeconds is < 1 or > 4_294_966)
            throw new ArgumentOutOfRangeException(nameof(ApprovalTimeoutSeconds), ApprovalTimeoutSeconds,
                                                  "Approval timeout must be between 1 and 4294966 seconds.");
    }
}
