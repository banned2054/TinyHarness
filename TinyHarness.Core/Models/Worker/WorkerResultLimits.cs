namespace TinyHarness.Core.Models.Worker;

/// <summary>
///     worker 结果交回调用方前的硬上限。模型输出按非可信资料处理，超长或超量的结果在
///     Core 内被拒绝，防止完整工具历史或大段代码涌入调用方主会话；限制值为保守常量，
///     由 Core 校验逻辑强制执行。
///     Hard limits applied to a worker result before it is handed back to the caller. Model output is
///     treated as untrusted material; over-long or oversized results are rejected inside Core so the full
///     tool history or large code blocks cannot flood the caller's session. The values are conservative
///     constants enforced by Core validation logic.
/// </summary>
public static class WorkerResultLimits
{
    /// <summary>
    ///     <c>conclusion</c> 最大字符数，仅 Completed 状态允许携带。 Maximum length of the conclusion in characters.
    /// </summary>
    public const int MaxConclusionLength = 2_000;

    /// <summary>
    ///     <c>statusDetail</c> 最大字符数，仅未完成/失败/取消状态要求携带。 Maximum length of the status detail in characters.
    /// </summary>
    public const int MaxStatusDetailLength = 500;

    /// <summary>
    ///     <c>evidence</c> 最大条数。 Maximum number of evidence citations.
    /// </summary>
    public const int MaxEvidenceCount = 16;

    /// <summary>
    ///     每条证据路径的最大字符数，与请求 focusPaths 上限一致。 Maximum characters per evidence path, matching the request focusPaths limit.
    /// </summary>
    public const int MaxEvidencePathLength = 256;

    /// <summary>
    ///     每条证据说明的最大字符数。 Maximum characters per evidence note.
    /// </summary>
    public const int MaxEvidenceNoteLength = 200;

    /// <summary>
    ///     suggestedChanges、testSuggestions、uncertainties 各自的最大条数。 Maximum entries per suggestion/uncertainty list.
    /// </summary>
    public const int MaxListCount = 8;

    /// <summary>
    ///     上述列表每条的最大字符数。 Maximum characters per list entry.
    /// </summary>
    public const int MaxListEntryLength = 1_000;

    /// <summary>
    ///     结论、状态说明、全部证据（路径与说明）与全部列表条目的总字符数上限。
    ///     Maximum total characters across conclusion, status detail, all evidence paths and notes, and all list entries.
    /// </summary>
    public const int MaxTotalTextLength = 16_000;
}
