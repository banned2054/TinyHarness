namespace TinyHarness.Core.Models.Worker;

/// <summary>
///     一次性只读 worker run 交还给调用方的有界结果：只包含结论、证据、建议与统计，
///     不包含完整工具历史、凭据或原始审计。未完成、失败和取消都通过 <see cref="Status" /> 表达，
///     不包装成成功结论。GLM 可以提出修改建议，但结果不表示任何变更已被应用。
///     模型输出按非可信资料处理，交回调用方前由 <c>WorkerResultValidator</c> 再校验一次。
///     The bounded result a one-shot read-only worker run returns to the caller: conclusion, evidence,
///     suggestions and statistics only — never the full tool history, credentials or raw audit logs.
///     Incomplete, failed and cancelled runs are expressed through <see cref="Status" /> and are never
///     dressed up as success. Suggested changes are proposals only; nothing has been applied. Model output
///     is treated as untrusted and re-validated by <c>WorkerResultValidator</c> before it reaches the caller.
/// </summary>
public sealed record WorkerResult
{
    public required WorkerResultStatus Status { get; init; }

    /// <summary>
    ///     最终结论；只在 <see cref="WorkerResultStatus.Completed" /> 时给出。
    ///     The final conclusion; only set when the status is Completed.
    /// </summary>
    public string Conclusion { get; init; } = string.Empty;

    /// <summary>
    ///     状态为未完成、失败或取消时的原因说明（达到哪个限制、错误是什么、在哪里被取消）。
    ///     Why the run is incomplete, failed or cancelled: which limit was hit, what failed, where it was cancelled.
    /// </summary>
    public string StatusDetail { get; init; } = string.Empty;

    /// <summary>支持结论或说明不确定性的证据引用。 Evidence citations backing the conclusion or the uncertainty.</summary>
    public IReadOnlyList<WorkerEvidence> Evidence { get; init; } = [];

    /// <summary>建议改法（尚未应用，仅提议）。 Suggested changes; proposals only, nothing has been applied.</summary>
    public IReadOnlyList<string> SuggestedChanges { get; init; } = [];

    /// <summary>验证建议。 Suggested ways to verify the finding.</summary>
    public IReadOnlyList<string> TestSuggestions { get; init; } = [];

    /// <summary>调查中的不确定项与矛盾点。 Open uncertainties and contradictions found during the investigation.</summary>
    public IReadOnlyList<string> Uncertainties { get; init; } = [];

    public WorkerExecutionStats Statistics { get; init; } = new();
}
